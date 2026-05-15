using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.Loader;
using System.Threading;
using System.Threading.Tasks;
using ClashEngine.Adapter;
using ClashEngine.Commands;
using ClashEngine.Config;
using ClashEngine.Core;
using ClashEngine.Core.GameType;
using ClashEngine.Core.Identity;
using ClashEngine.Core.Penalties;
using ClashEngine.Core.Queue;
using ClashEngine.Core.Ratings;
using ClashEngine.Core.Stats;
using ClashEngine.Events;
using ClashEngine.Orchestration;
using ClashEngine.Lvz;
using ClashEngine.Persistence;
using ClashEngine.Recording;
using ClashEngine.Replay;
using ClashEngine.Stats;
using SS.Core;
using SS.Core.ComponentInterfaces;

namespace ClashEngine;

/// <summary>
/// SubspaceServer plug-in module that hosts the ClashEngine matchmaking engine. Wires the pure
/// <see cref="MatchmakingEngine"/> to the server's player/arena/chat/persist subsystems.
/// </summary>
[ModuleInfo("""
    Skill-based matchmaking engine.
    Configuration: [ClashEngine] in global.conf and per-arena overrides.
    """)]
public sealed class ClashModule : IAsyncModule, IAsyncModuleLoaderAware, IAsyncArenaAttachableModule
{
    private const string LogCategory = nameof(ClashModule);

    private readonly IComponentBroker _broker;
    private readonly IMainloop _mainloop;
    private readonly IMainloopTimer _mainloopTimer;
    private readonly ILogManager _log;
    private readonly IConfigManager _config;
    private readonly IPlayerData _playerData;
    private readonly IArenaManager _arenaManager;
    private readonly IGame _game;
    private readonly IChat _chat;
    private readonly ICommandManager _commands;
    private readonly IObjectPoolManager _objectPool;

    private IPersist? _persist;
    private IMapData? _mapData;
    private INetwork? _network;
    private ISecuritySeedSync? _securitySeedSync;
    private IWatchDamage? _watchDamage;
    private MatchRecorder? _matchRecorder;

    private MatchmakingEngine? _engine;
    private SystemClock? _clock;
    private PlayerKeyResolver? _resolver;
    private PlayerStateObserver? _observer;
    private MatchKillRouter? _killRouter;
    private EngineEventListener? _listener;
    private ClashLog? _clashLog;
    private MatchOrchestratorRegistry? _orchestrators;
    private InMemoryRatingStore? _ratingsCache;
    private PersistRatingStore? _persistRatings;
    private PersistPenaltyStore? _persistPenalties;
    private DelegatePersistentData<Player>? _ratingsRegistration;
    private DelegatePersistentData<Player>? _penaltiesRegistration;
    private MatchmakingCommands? _commandHandlers;
    private MatchStatsRegistry? _matchStats;
    private ClashStatsTelemetry? _matchStatsTelemetry;
    private StatsListener? _statsListener;
    private ChartCommand? _chartCommand;
    private ItemsCommand? _itemsCommand;
    private DistanceSampler? _distanceSampler;
    private MatchLvzAdapter? _lvzAdapter;
    private MatchFreqAdvisor? _freqAdvisor;
    private IMatchUploader? _matchUploader;
    private ClashReplayRecorder? _replayRecorder;

    // Penalty-tracker memory grows unbounded without periodic pruning. Pruning on every 500 ms
    // tick is overkill -- penalty memory windows run for hours -- so we throttle to one sweep
    // every PenaltyPruneInterval and stash the next-due time across ticks.
    private static readonly TimeSpan PenaltyPruneInterval = TimeSpan.FromMinutes(5);
    private DateTimeOffset _nextPenaltyPrune;

    // LIFO list of teardown actions. Each callsite that calls foo.Register() also adds
    // foo.Unregister to this list, so PreUnloadAsync can tear everything down in reverse-
    // construction order without a parallel pile of `_foo?.Unregister()` lines that have
    // to stay in sync with whatever was registered above.
    private readonly List<Action> _unregisterActions = new();

    // Per-arena teardown actions, populated by AttachModuleAsync and drained by
    // DetachModuleAsync. SS guarantees DetachModule fires for every attached arena before
    // PreUnload, but PreUnloadAsync drains any leftovers as belt-and-braces.
    private readonly Dictionary<Arena, List<Action>> _arenaUnregisterActions = new();

    // Zone-wide global.conf handle. We read the [ClashEngine] section from it for zone-wide
    // game type definitions; the section can live directly in global.conf or in any file
    // #include'd from it. Queues without an owner arena make no sense under per-arena
    // namespacing, so only game types are picked up at zone scope.
    private ConfigHandle? _zoneClashHandle;

    // Per-arena arena.conf handles, keyed by arena. We read the [ClashEngine] section from
    // each one (which may sit directly in arena.conf or in any #include'd file). Each handle
    // has a change callback registered so an operator edit -- in arena.conf itself or any
    // included file -- triggers a re-parse + atomic registry swap (with waiter drops on
    // queues removed or changed by the new content).
    private readonly Dictionary<Arena, ConfigHandle> _arenaClashHandles = new();

    public ClashModule(
        IComponentBroker broker,
        IMainloop mainloop,
        IMainloopTimer mainloopTimer,
        ILogManager log,
        IConfigManager config,
        IPlayerData playerData,
        IArenaManager arenaManager,
        IGame game,
        IChat chat,
        ICommandManager commands,
        IObjectPoolManager objectPool)
    {
        _broker = broker ?? throw new ArgumentNullException(nameof(broker));
        _mainloop = mainloop ?? throw new ArgumentNullException(nameof(mainloop));
        _mainloopTimer = mainloopTimer ?? throw new ArgumentNullException(nameof(mainloopTimer));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _playerData = playerData ?? throw new ArgumentNullException(nameof(playerData));
        _arenaManager = arenaManager ?? throw new ArgumentNullException(nameof(arenaManager));
        _game = game ?? throw new ArgumentNullException(nameof(game));
        _chat = chat ?? throw new ArgumentNullException(nameof(chat));
        _commands = commands ?? throw new ArgumentNullException(nameof(commands));
        _objectPool = objectPool ?? throw new ArgumentNullException(nameof(objectPool));

        EnsureSharedAssemblyResolver();
    }

    /// <summary>0 = not yet installed, 1 = installed. Set via Interlocked so duplicate
    /// ctors (or a reload) don't double-register the handler on the same ALC.</summary>
    private static int s_resolverInstalled;

    /// <summary>
    /// Installs an <see cref="AssemblyLoadContext.Resolving"/> hook on the ClashEngine
    /// plug-in's own ALC so that lookups for shared dependencies (currently only
    /// <c>OpenSkillSharp</c>) are redirected to the copy already loaded by another plug-in's
    /// ALC -- typically <c>SS.Matchmaking</c>'s -- rather than loading our own isolated copy.
    /// </summary>
    /// <remarks>
    /// <para>This is the workaround for the cross-ALC type-identity mismatch that surfaces as
    /// <c>TypeLoadException: Method 'get_OpenSkillModel' ... does not have an implementation</c>
    /// when <see cref="ClashEngine.Lvz.ClashMatchConfiguration"/>'s explicit-interface impl
    /// of <c>SS.Matchmaking.TeamVersus.IMatchConfiguration.OpenSkillModel</c> is verified.</para>
    ///
    /// <para>By the time <see cref="ClashMatchConfiguration"/> is first instantiated (on
    /// match start, well after module load), <c>SS.Matchmaking</c>'s ALC has already loaded
    /// its own <c>OpenSkillSharp</c> from <c>bin/modules/Matchmaking/</c>. Returning that
    /// already-loaded <see cref="Assembly"/> reference makes both plug-ins share a single
    /// <c>OpenSkillSharp</c> assembly identity, which is what the CLR vtable check needs.</para>
    ///
    /// <para>ClashEngine's own <c>bin/modules/ClashEngine/</c> deliberately does NOT include
    /// <c>OpenSkillSharp.dll</c> (the project marks the package as
    /// <c>&lt;ExcludeAssets&gt;runtime&lt;/ExcludeAssets&gt;</c>), so the plug-in's
    /// <c>AssemblyDependencyResolver</c> returns null and the runtime falls through to this
    /// hook. If for some reason no plug-in has <c>OpenSkillSharp</c> loaded yet, the hook
    /// returns null and the CLR continues with normal default-ALC probing.</para>
    /// </remarks>
    private static void EnsureSharedAssemblyResolver()
    {
        if (Interlocked.CompareExchange(ref s_resolverInstalled, 1, 0) != 0) return;
        var ourAlc = AssemblyLoadContext.GetLoadContext(typeof(ClashModule).Assembly);
        if (ourAlc is null) return;
        ourAlc.Resolving += OnAlcResolving;
    }

    private static Assembly? OnAlcResolving(AssemblyLoadContext askingAlc, AssemblyName name)
    {
        // Currently only OpenSkillSharp is shared this way. Add more entries here if
        // additional dependencies start hitting the same cross-plug-in ALC mismatch.
        if (!string.Equals(name.Name, "OpenSkillSharp", StringComparison.Ordinal))
            return null;

        foreach (var ctx in AssemblyLoadContext.All)
        {
            if (ReferenceEquals(ctx, askingAlc)) continue;
            foreach (var asm in ctx.Assemblies)
            {
                if (string.Equals(asm.GetName().Name, name.Name, StringComparison.Ordinal))
                    return asm;
            }
        }
        return null;
    }

    Task<bool> IAsyncModule.LoadAsync(IComponentBroker broker, CancellationToken cancellationToken)
    {
        _log.LogM(LogLevel.Info, LogCategory, "ClashEngine loaded.");
        return Task.FromResult(true);
    }

    async Task IAsyncModuleLoaderAware.PostLoadAsync(IComponentBroker broker, CancellationToken cancellationToken)
    {
        _persist = broker.GetInterface<IPersist>();
        _watchDamage = broker.GetInterface<IWatchDamage>();

        var initialVerbosity = ClashLog.ParseVerbosity(
            _config.GetStr(_config.Global, "ClashEngine", "LogVerbosity"),
            fallback: ClashVerbosity.Normal);
        _clashLog = new ClashLog(_log, initialVerbosity);
        _clashLog.Info(LogCategory, $"ClashLog verbosity = {initialVerbosity}.");

        _clock = new SystemClock();
        _resolver = new PlayerKeyResolver();
        _persistRatings = new PersistRatingStore();
        _ratingsCache = new InMemoryRatingStore();   // legacy fallback if IPersist unavailable
        IRatingStore ratingStore = _persist is not null ? _persistRatings : _ratingsCache;

        // Per-policy hard ceiling on the assessed timeout. Defaults to 6h; configurable to give
        // operators an escape hatch if a future bug ever puts a player on a runaway escalation
        // ladder again. Values <= 0 fall back to the default rather than disabling the cap.
        int maxPenaltyHours = _config.GetInt(_config.Global, "ClashEngine", "MaxPenaltyHours", 6);
        var maxPenalty = maxPenaltyHours > 0
            ? TimeSpan.FromHours(maxPenaltyHours)
            : PenaltyPolicy.DefaultMaxTimeout;

        // Engine is constructed with a no-op telemetry sink so that listeners (which need the
        // engine reference) can be wired up below; once they exist we swap in the real
        // composite via SetTelemetry. Events fired before that swap (none, in practice -- the
        // engine doesn't emit on construction) would have hit the no-op sink anyway.
        _engine = new MatchmakingEngine(
            ratings: ratingStore,
            clock: _clock,
            penaltyPolicies: new[]
            {
                PenaltyPolicy.DefaultAbandonment.WithMaxTimeout(maxPenalty),
                PenaltyPolicy.DefaultGriefing.WithMaxTimeout(maxPenalty),
                PenaltyPolicy.DefaultStagingAfk.WithMaxTimeout(maxPenalty),
            },
            invitationTtl: TimeSpan.FromSeconds(15));

        _listener = new EngineEventListener(_chat, _log, _resolver, _clashLog, _engine.Queues, _engine.Groups, _clock);

        _persistPenalties = new PersistPenaltyStore(_engine.Penalties);

        // Recipient-resolution helper that adds spectators-in-focus to participant broadcasts so
        // staging / countdown / cleanup / collapse messages reach watchers as well.
        var matchAudience = new MatchAudience(broker, _playerData, _arenaManager, _chat);

        // Rotates per-match team freqs (100..2000) so concurrent matches in the same arena don't
        // all park on freq 100/200. Shared by the orchestrator, the LVZ team adapter, and the
        // freq-lock advisor so they all agree on what freq this match's team-t is on.
        var freqAllocator = new MatchFreqAllocator();

        // Per-match damage-aware stat tracking. The telemetry consumer drives
        // BeginMatch/EndMatch; the listener subscribes to wire events and dispatches.
        // Constructed before the orchestrator registry so we can hand it the recorder reference
        // for the ?return loadout-restore path.
        _matchStats = new MatchStatsRegistry(new DamageDecay());

        _orchestrators = new MatchOrchestratorRegistry(
            broker, _engine, _game, _chat, _mainloopTimer, _arenaManager, _clock, _log, _resolver, _clashLog,
            matchAudience, freqAllocator, matchStats: _matchStats);
        _orchestrators.Register();
        _unregisterActions.Add(_orchestrators.Unregister);

        // Replay recorder (optional): the in-plug-in MatchRecorder captures a per-match,
        // per-session .replay file. Per-session scoping (not per-arena like the server's
        // IReplayController) lets concurrent matches in the same arena each get their own
        // clean recording. The resulting file is attached to the upload and deleted on
        // success.
        bool recordReplays = _config.GetInt(_config.Global, "ClashEngine", "RecordReplays", 1) != 0;
        if (recordReplays)
        {
            _mapData = broker.GetInterface<IMapData>();
            _network = broker.GetInterface<INetwork>();
            _securitySeedSync = broker.GetInterface<ISecuritySeedSync>();

            if (_mapData is null || _network is null || _securitySeedSync is null)
            {
                _log.LogM(LogLevel.Warn, LogCategory,
                    "RecordReplays=1 but a required server interface (IMapData/INetwork/ISecuritySeedSync) was unavailable; replays will not be recorded.");
                if (_mapData is not null) broker.ReleaseInterface(ref _mapData);
                if (_network is not null) broker.ReleaseInterface(ref _network);
                if (_securitySeedSync is not null) broker.ReleaseInterface(ref _securitySeedSync);
            }
            else
            {
                _matchRecorder = new MatchRecorder(
                    broker, _arenaManager, _log, _mainloop, _mapData, _network, _securitySeedSync);
                _matchRecorder.Register();
                _unregisterActions.Add(_matchRecorder.Unregister);

                string recordingDir = _config.GetStr(_config.Global, "ClashEngine", "ReplayRecordingDir")
                    ?? System.IO.Path.Combine(AppContext.BaseDirectory, "clash-replays");
                _replayRecorder = new ClashReplayRecorder(
                    _engine, _matchRecorder, _arenaManager, _resolver, _mainloopTimer, _log, recordingDir);
                _unregisterActions.Add(_replayRecorder.Shutdown);
            }
        }

        _matchUploader = BuildMatchUploader(_replayRecorder);

        Func<Guid, string?>? recordingPathLookup = _replayRecorder is not null
            ? (Func<Guid, string?>)(id => _replayRecorder.GetRecordingPath(id))
            : null;
        _matchStatsTelemetry = new ClashStatsTelemetry(
            _engine, _matchStats, _config, _arenaManager, _clock, _matchUploader, _log,
            _chat, _resolver, _watchDamage, recordingPathLookup, matchAudience);
        if (_watchDamage is null)
            _log.LogM(LogLevel.Warn, LogCategory,
                "IWatchDamage not available; damage stats (DDealt/DTaken/HitCount) will be zero.");

        var empLookup = new EmpShutdownLookup(_config);
        var killFeedReporter = new KillFeedReporter(_chat, _clock, _resolver, matchAudience);
        _statsListener = new StatsListener(
            broker, _engine, _matchStats, _playerData, _resolver, empLookup, killFeedReporter, _mainloopTimer, _log);
        // Wire the drain hook so the telemetry's OnMatchEnded flushes any pending deferred
        // kill-attribution work into the recorder before the upload payload is built.
        _matchStatsTelemetry.DrainPendingKills = _statsListener.DrainPendingForMatch;
        _statsListener.Register();
        _unregisterActions.Add(_statsListener.Unregister);

        var matchLookup = new ActiveMatchLookup(broker, _engine, _matchStats, _resolver);

        // ChartCommand and ItemsCommand are arena-scoped (see AttachModuleAsync); they're
        // constructed here so DI/state is available, but the per-arena AddCommand calls
        // happen at attach time.
        _chartCommand = new ChartCommand(_engine, matchLookup, _commands, _chat);
        _itemsCommand = new ItemsCommand(matchLookup, _commands, _chat);

        // Periodic distance-to-nearest-enemy sampling. Default 5 Hz; 0 disables.
        int distanceHz = _config.GetInt(_config.Global, "ClashEngine", "DistanceSampleHz", 5);
        if (distanceHz > 0)
        {
            int clamped = Math.Clamp(distanceHz, 1, 50);
            _distanceSampler = new DistanceSampler(
                _engine, _matchStats, _resolver, _mainloopTimer, _log, clamped);
            _distanceSampler.Register();
            _unregisterActions.Add(_distanceSampler.Unregister);
            _clashLog.Info(LogCategory, $"DistanceSampler enabled at {clamped} Hz (period {_distanceSampler.PeriodMs} ms).");
        }
        else
        {
            _clashLog.Info(LogCategory, "DistanceSampler disabled (DistanceSampleHz=0).");
        }

        // MatchLvz integration: bridges our match lifecycle into SS's MatchLvz module so the
        // statbox / scoreboard render automatically. Registered after stats so the recorder
        // is available for IMemberStats reads. Skipped silently if SS.Matchmaking module
        // graph isn't loaded -- the callbacks fire harmlessly with no subscribers.
        _lvzAdapter = new MatchLvzAdapter(broker, _engine, _matchStats, _resolver, _arenaManager, _game, _log, freqAllocator);

        // Per-life ship lock + per-match freq lock. Implements IFreqManagerEnforcerAdvisor and
        // registers on every configured match arena; SS Core's FreqManager consults it on each
        // ship/freq change request from a player. Direct API placements via IGame.SetShipAndFreq
        // (orchestrator setup, knockout-spec) bypass the advisor and remain unaffected.
        _freqAdvisor = new MatchFreqAdvisor(broker, _engine, _arenaManager, _resolver, _clock, _log, _chat, freqAllocator);

        // _listener is registered AFTER _matchStatsTelemetry so its OnMatchEnded handler runs
        // post-statbox: EngineEventListener buffers post-match DMs (abandonment / griefing /
        // promotion) during FinalizeMatch and drains them in OnMatchEnded, which must come after
        // ClashStatsTelemetry has broadcast the scoreboard. The other events EngineEventListener
        // handles are independent of the listeners that come before it here.
        var listeners = new List<Core.Adapter.IMatchmakingTelemetry> { _orchestrators };
        if (_replayRecorder is not null) listeners.Add(_replayRecorder);
        listeners.Add(_matchStatsTelemetry);
        listeners.Add(_listener);
        listeners.Add(_lvzAdapter);
        listeners.Add(_freqAdvisor);
        _engine.SetTelemetry(new CompositeTelemetry(listeners.ToArray()));

        _observer = new PlayerStateObserver(broker, _engine, _resolver, _clock, _clashLog);
        _observer.Register();
        _unregisterActions.Add(_observer.Unregister);

        _lvzAdapter.Register();
        _unregisterActions.Add(_lvzAdapter.Unregister);

        _freqAdvisor.Register();
        _unregisterActions.Add(_freqAdvisor.Unregister);

        // Single owner of the broker's KillCallback. The router calls engine.OnKill itself,
        // then fans out to the per-event readers in registration order -- so the previously
        // implicit "PlayerStateObserver registers first, others read after" dependency is now
        // structural rather than positional. Recorder uses its own KillCallback subscription
        // because replay encoding is independent of engine state.
        _killRouter = new MatchKillRouter(broker, _engine, _resolver, _clock, _log, _clashLog);
        // StatsListener is a pre-engine reader: the final kill of a match must be recorded
        // before engine.OnKill triggers OnMatchEnded, which tears down the recorder before
        // post-engine readers run.
        _killRouter.AddPreEngineReader(nameof(StatsListener), _statsListener.OnKill);
        _killRouter.AddReader(nameof(MatchOrchestratorRegistry), _orchestrators.OnKill);
        _killRouter.AddReader(nameof(MatchLvzAdapter), _lvzAdapter.OnKill);
        _killRouter.AddReader(nameof(MatchFreqAdvisor), _freqAdvisor.OnKill);
        _killRouter.Register();
        _unregisterActions.Add(_killRouter.Unregister);

        if (_persist is not null)
        {
            _ratingsRegistration = new DelegatePersistentData<Player>(
                PersistRatingStore.PersistKey, PersistInterval.Forever, PersistScope.Global,
                getDataCallback: _persistRatings.GetData,
                setDataCallback: _persistRatings.SetData,
                clearDataCallback: _persistRatings.ClearData);
            await _persist.RegisterPersistentDataAsync(_ratingsRegistration);

            _penaltiesRegistration = new DelegatePersistentData<Player>(
                PersistPenaltyStore.PersistKey, PersistInterval.Forever, PersistScope.Global,
                getDataCallback: _persistPenalties.GetData,
                setDataCallback: _persistPenalties.SetData,
                clearDataCallback: _persistPenalties.ClearData);
            await _persist.RegisterPersistentDataAsync(_penaltiesRegistration);
        }

        // Only ?play and ?queue are zone-wide so a player can opt into matchmaking from any
        // arena (lobby, public, etc.). The remaining 11 matchmaking commands register
        // per-arena in AttachModuleAsync.
        _commandHandlers = new MatchmakingCommands(_engine, _commands, _chat, _clock, _resolver, _config, _clashLog, _orchestrators);
        _commandHandlers.RegisterGlobal();
        _unregisterActions.Add(_commandHandlers.UnregisterGlobal);

        // Zone-wide global.conf. Provides shared game type definitions any arena can reference
        // via its [ClashEngine] section. Queues are not loaded at zone scope -- per the
        // per-arena model, every queue must have an owning arena.
        _zoneClashHandle = await _config.OpenConfigFileAsync(null, null, OnZoneClashChanged);
        if (_zoneClashHandle is not null)
        {
            LoadZoneClashContribution();
            _unregisterActions.Add(() =>
            {
                _engine?.GameTypes.Remove(sourceArena: null);
                if (_zoneClashHandle is { } h)
                {
                    _config.CloseConfigFile(h);
                    _zoneClashHandle = null;
                }
            });
        }
        else
        {
            _clashLog.Info(LogCategory, "No global.conf found; zone-wide game types are empty.");
        }

        _mainloopTimer.SetTimer(OnTick, initialDelay: 500, interval: 500, key: this);

        _log.LogM(LogLevel.Info, LogCategory,
            $"ClashEngine ready ({_engine.GameTypes.Count} zone-wide game type(s), {_engine.Queues.Count} queue(s) registered so far).");
    }

    async Task IAsyncModuleLoaderAware.PreUnloadAsync(IComponentBroker broker, CancellationToken cancellationToken)
    {
        _mainloopTimer.ClearTimer(OnTick, key: this);

        // Belt-and-braces: SS guarantees DetachModule fires for every attached arena before
        // PreUnload, so this dictionary should be empty here. Drain anything that slipped
        // through so we don't leak per-arena command bindings on reload.
        foreach (var (arena, actions) in _arenaUnregisterActions)
        {
            for (int i = actions.Count - 1; i >= 0; i--)
            {
                try { actions[i](); }
                catch (Exception ex) { _log.LogM(LogLevel.Error, LogCategory, $"Arena {arena.Name} detach-on-unload failed: {ex}"); }
            }
        }
        _arenaUnregisterActions.Clear();

        // Tear down in reverse-construction order. Each action is wrapped so a single
        // misbehaving Unregister can't strand the rest -- the broker would still hold the
        // unraveled subscriptions otherwise, and reload would double-register.
        for (int i = _unregisterActions.Count - 1; i >= 0; i--)
        {
            try { _unregisterActions[i](); }
            catch (Exception ex) { _log.LogM(LogLevel.Error, LogCategory, $"Unregister failed: {ex}"); }
        }
        _unregisterActions.Clear();

        if (_persist is not null)
        {
            if (_ratingsRegistration is not null)
                await _persist.UnregisterPersistentDataAsync(_ratingsRegistration);
            if (_penaltiesRegistration is not null)
                await _persist.UnregisterPersistentDataAsync(_penaltiesRegistration);
        }
        _ratingsRegistration = null;
        _penaltiesRegistration = null;
    }

    async Task<bool> IAsyncArenaAttachableModule.AttachModuleAsync(Arena arena, CancellationToken cancellationToken)
    {
        // Per-arena command registration. Construction of the command handlers happened in
        // PostLoadAsync; here we just bind their AddCommand calls to this specific arena so
        // they appear in ?man's Arena: section and only resolve for players in `arena`.
        var actions = new List<Action>();
        try
        {
            _chartCommand!.RegisterArena(arena);
            actions.Add(() => _chartCommand!.UnregisterArena(arena));

            _itemsCommand!.RegisterArena(arena);
            actions.Add(() => _itemsCommand!.UnregisterArena(arena));

            _commandHandlers!.RegisterArena(arena);
            actions.Add(() => _commandHandlers!.UnregisterArena(arena));

            // Open the arena's main conf (arena.conf). Whichever file actually carries the
            // [ClashEngine] section -- arena.conf itself, or any file #include'd from it --
            // contributes its game types + queues to the engine registries until the arena
            // detaches. The host watches the entire document (base + all transitive includes)
            // for changes and invokes OnArenaClashChanged on the mainloop thread.
            var clashHandle = await _config.OpenConfigFileAsync(
                arena.BaseName, null, OnArenaClashChanged, arena);
            if (clashHandle is not null)
            {
                _arenaClashHandles[arena] = clashHandle;
                LoadArenaClashContribution(arena);
                actions.Add(() => UnloadArenaClashContribution(arena));
            }
        }
        catch (Exception ex)
        {
            _log.LogM(LogLevel.Error, LogCategory, $"AttachModule({arena.Name}) failed: {ex}");
            // Roll back any partial registrations so a failed attach doesn't leak a half-
            // wired arena into the command manager.
            for (int i = actions.Count - 1; i >= 0; i--)
            {
                try { actions[i](); } catch { /* swallow -- already in failure path */ }
            }
            return false;
        }

        _arenaUnregisterActions[arena] = actions;
        return true;
    }

    Task<bool> IAsyncArenaAttachableModule.DetachModuleAsync(Arena arena, CancellationToken cancellationToken)
    {
        if (!_arenaUnregisterActions.Remove(arena, out var actions))
            return Task.FromResult(true);

        // LIFO drain matching PreUnloadAsync's pattern: a single failing Unregister can't
        // strand the rest, since the broker still holds the unraveled bindings.
        for (int i = actions.Count - 1; i >= 0; i--)
        {
            try { actions[i](); }
            catch (Exception ex) { _log.LogM(LogLevel.Error, LogCategory, $"DetachModule({arena.Name}) action failed: {ex}"); }
        }
        return Task.FromResult(true);
    }

    Task<bool> IAsyncModule.UnloadAsync(IComponentBroker broker, CancellationToken cancellationToken)
    {
        // Dispose the uploader so the background drain loop exits and any in-flight HTTP work
        // is cancelled before the broker tears down dependent interfaces.
        if (_matchUploader is IDisposable disposableUploader)
            disposableUploader.Dispose();

        // _matchRecorder.Unregister was already invoked via the _unregisterActions list during
        // PreUnloadAsync; here we only release the server interfaces it depended on.
        if (_securitySeedSync is not null) broker.ReleaseInterface(ref _securitySeedSync);
        if (_network is not null) broker.ReleaseInterface(ref _network);
        if (_mapData is not null) broker.ReleaseInterface(ref _mapData);
        if (_watchDamage is not null) broker.ReleaseInterface(ref _watchDamage);

        if (_persist is not null)
            broker.ReleaseInterface(ref _persist);

        _engine = null;
        _clock = null;
        _resolver = null;
        _listener = null;
        _observer = null;
        _killRouter = null;
        _orchestrators = null;
        _ratingsCache = null;
        _persistRatings = null;
        _persistPenalties = null;
        _commandHandlers = null;
        _matchStats = null;
        _matchStatsTelemetry = null;
        _statsListener = null;
        _chartCommand = null;
        _itemsCommand = null;
        _distanceSampler = null;
        _lvzAdapter = null;
        _matchUploader = null;
        _replayRecorder = null;

        _log.LogM(LogLevel.Info, LogCategory, "ClashEngine unloaded.");
        return Task.FromResult(true);
    }

    // ---- [ClashEngine] load / reload helpers ------------------------------------------------

    /// <summary>
    /// Parses the zone-wide <c>[ClashEngine]</c> section (from global.conf or anything
    /// <c>#include</c>'d from it) and commits its game types to the engine. Game-type IDs are
    /// bytes persisted by <c>PersistRatingStore</c>; collisions and ID changes-by-rename are
    /// rejected and logged. On rejection, the previous zone contribution is preserved.
    /// </summary>
    private void LoadZoneClashContribution()
    {
        if (_engine is null || _zoneClashHandle is null || _clashLog is null) return;

        var parsed = MatchmakingConfig.ParseZoneGameTypes(_config, _zoneClashHandle, _clashLog);
        if (!_engine.GameTypes.ReplaceArenaContribution(sourceArena: null, parsed, out var errors))
        {
            foreach (var e in errors) _clashLog.Warn(LogCategory, $"global.conf [ClashEngine]: {e}");
            _clashLog.Warn(LogCategory, "global.conf [ClashEngine] rejected; prior zone-wide game types retained.");
            return;
        }
        _clashLog.Info(LogCategory, $"global.conf [ClashEngine] loaded ({parsed.Count} zone-wide game type(s)).");
    }

    private void OnZoneClashChanged()
    {
        // Host fires this on the mainloop thread. Re-parse and re-commit; failures leave the
        // prior state intact (and log the reason).
        try { LoadZoneClashContribution(); }
        catch (Exception ex) { _log.LogM(LogLevel.Error, LogCategory, $"OnZoneClashChanged failed: {ex}"); }
    }

    /// <summary>
    /// Parses <paramref name="arena"/>'s <c>[ClashEngine]</c> contribution (from its arena.conf
    /// document, including anything <c>#include</c>'d) and commits it. On a hot reload, queues
    /// that are removed or significantly altered are drained first and each affected player is
    /// sent a chat notice.
    /// </summary>
    private void LoadArenaClashContribution(Arena arena)
    {
        if (_engine is null || _clashLog is null) return;
        if (!_arenaClashHandles.TryGetValue(arena, out var handle)) return;

        var contribution = MatchmakingConfig.ParseArenaContribution(_config, handle, arena.BaseName, _clashLog);
        string src = $"arena '{arena.BaseName}' [ClashEngine]";

        // 1. Commit game types FIRST so the queue parser (already done above) and any later
        //    lookups can resolve cross-references through GameTypes. A failure here aborts the
        //    whole reload for this arena -- queues would reference unknown IDs otherwise.
        var gameTypeList = new List<GameTypeDef>();
        foreach (var g in contribution.GameTypes) gameTypeList.Add(g);
        if (!_engine.GameTypes.ReplaceArenaContribution(arena.BaseName, gameTypeList, out var gtErrors))
        {
            foreach (var e in gtErrors) _clashLog.Warn(LogCategory, $"{src}: {e}");
            _clashLog.Warn(LogCategory, $"{src} rejected; prior contribution retained.");
            return;
        }

        // 2. Preview the queue diff so we can drain waiters BEFORE the registry swap. The
        //    matcher's per-queue dequeue path requires the queue to still be registered, so we
        //    have to act on the old registry state.
        if (!_engine.Queues.TryComputeArenaContributionDiff(
                arena.BaseName, contribution.Queues, out var wouldRemove, out var wouldAdd, out var qErrors))
        {
            foreach (var e in qErrors) _clashLog.Warn(LogCategory, $"{src}: {e}");
            _clashLog.Warn(LogCategory, $"{src} queues rejected; prior queues retained.");
            return;
        }

        // 3. Drain waiters from queues that will be removed or have their shape changed by the
        //    swap. Notify each surviving player via chat.
        var now = _clock!.UtcNow;
        DrainQueuesWithNotice(wouldRemove, now);

        // 4. Apply the swap.
        _engine.Queues.ApplyArenaContribution(arena.BaseName, contribution.Queues);

        _clashLog.Info(LogCategory,
            $"{src} loaded ({gameTypeList.Count} game type(s), " +
            $"{contribution.Queues.Count} queue(s); +{wouldAdd.Count} -{wouldRemove.Count} since last load).");
    }

    private void OnArenaClashChanged(Arena arena)
    {
        try { LoadArenaClashContribution(arena); }
        catch (Exception ex) { _log.LogM(LogLevel.Error, LogCategory, $"OnArenaClashChanged({arena.Name}) failed: {ex}"); }
    }

    /// <summary>
    /// Removes <paramref name="arena"/>'s entire <c>[ClashEngine]</c> contribution from the
    /// engine registries and closes the arena.conf handle. Invoked from the per-arena
    /// unregister-action list when the arena is detached.
    /// </summary>
    private void UnloadArenaClashContribution(Arena arena)
    {
        if (!_arenaClashHandles.Remove(arena, out var handle)) return;

        if (_engine is not null)
        {
            // Drain waiters from queues we're about to remove, then drop the queues. Mirrors the
            // hot-reload "removed queue" path so detach uses the same UX.
            var owned = new List<QueueDefinition>();
            foreach (var def in _engine.Queues.Definitions)
                if (string.Equals(def.OwnerArenaName, arena.BaseName, StringComparison.OrdinalIgnoreCase))
                    owned.Add(def);
            DrainQueuesWithNotice(owned, _clock?.UtcNow ?? DateTimeOffset.UtcNow);
            _engine.Queues.RemoveByOwner(arena.BaseName);
            _engine.GameTypes.Remove(arena.BaseName);
        }

        try { _config.CloseConfigFile(handle); }
        catch (Exception ex) { _log.LogM(LogLevel.Error, LogCategory, $"CloseConfigFile({arena.Name}) failed: {ex}"); }
    }

    /// <summary>
    /// Drains every waiter from each queue in <paramref name="queues"/> via the engine's normal
    /// dequeue path (so telemetry / _multiQueue stay consistent), and sends each removed
    /// connected player a chat notice. Must be called BEFORE the queue is removed from the
    /// registry -- the matcher's dequeue rejects queues that aren't registered.
    /// </summary>
    private void DrainQueuesWithNotice(IReadOnlyList<QueueDefinition> queues, DateTimeOffset now)
    {
        if (_engine is null) return;
        foreach (var def in queues)
        {
            var snap = def.Queue.Snapshot();
            for (int i = 0; i < snap.Count; i++)
            {
                var player = snap[i].Player;
                _engine.Dequeue(player, def.UniqueId, now);
                if (_resolver?.Resolve(player) is { } p)
                    _chat.SendMessage(p, $"Queue '{def.Label}' was reconfigured; you've been removed.");
            }
        }
    }

    private bool OnTick()
    {
        try
        {
            var now = _clock!.UtcNow;
            _engine?.Tick(now);
            // Drop entries for matches whose upload never completed; an hour is comfortably
            // beyond the HTTP uploader's 5-minute readiness timeout + retry budget.
            _replayRecorder?.PruneStale(now, TimeSpan.FromHours(1));

            // Drop expired penalty events so the tracker's per-player history doesn't grow
            // forever. Throttled because the prune is O(events) and there's no urgency --
            // a stale event past its memory window has no effect on lookups; we just want
            // to bound memory. _nextPenaltyPrune defaults to MinValue so the first tick fires.
            if (_engine is not null && now >= _nextPenaltyPrune)
            {
                _engine.Penalties.Prune(now);
                _nextPenaltyPrune = now + PenaltyPruneInterval;
            }
        }
        catch (Exception ex)
        {
            _log.LogM(LogLevel.Error, LogCategory, $"Tick failed: {ex}");
        }
        return true;
    }

    /// <summary>
    /// Picks the upload sink based on config: <c>UploadUrl</c> + <c>UploadApiKey</c> set ->
    /// <see cref="HttpMatchUploader"/>; otherwise the JSON-file fallback so the operator can
    /// still inspect or batch-upload locally.
    /// </summary>
    private IMatchUploader BuildMatchUploader(ClashReplayRecorder? recorder)
    {
        var url = _config.GetStr(_config.Global, "ClashEngine", "UploadUrl");
        var apiKey = _config.GetStr(_config.Global, "ClashEngine", "UploadApiKey");
        if (!string.IsNullOrWhiteSpace(url) && !string.IsNullOrWhiteSpace(apiKey))
        {
            _log.LogM(LogLevel.Info, LogCategory, $"Match uploads enabled -> POST {url}");
            return new HttpMatchUploader(
                url: url,
                apiKey: apiKey,
                log: _log,
                isRecordingReady: recorder is null ? null : recorder.IsRecordingComplete,
                onUploadSuccess: recorder is null ? null : recorder.DeleteRecording);
        }

        string matchesDir = System.IO.Path.Combine(AppContext.BaseDirectory, "matches");
        _log.LogM(LogLevel.Info, LogCategory,
            $"Match uploads not configured (UploadUrl/UploadApiKey unset); falling back to JSON files in {matchesDir}.");
        return new JsonFileMatchUploader(matchesDir, _log);
    }
}
