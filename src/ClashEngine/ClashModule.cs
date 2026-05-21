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
    private IGameTypeRegistrar? _gameTypeRegistrar;
    private IRatingSync? _ratingSync;
    private RatingSyncCoordinator? _ratingSyncCoordinator;
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

                // Route match-audience arena lines (countdown/GO/scoreboard/...) into the
                // per-match replay. Only wired when recording is enabled; otherwise the
                // audience's recorder stays null and recording silently no-ops.
                matchAudience.SetChatRecorder(_replayRecorder);
            }
        }

        _matchUploader = BuildMatchUploader(_replayRecorder);
        _gameTypeRegistrar = BuildGameTypeRegistrar();
        _ratingSync = BuildRatingSync();

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
        // Rating sync coordinator: pull-on-first-connect. Match envelopes carry post-match
        // ratings BACK to the server, so there's no push path here -- the coordinator only
        // seeds the local cache when a player connects to a zone that has no persisted data
        // for them. Subscribed to PlayerStateObserver BEFORE Register() so the very first
        // connect event reaches the coordinator; uses the same in-memory rating store the
        // engine reads so a pull populates the same rows ?play and the matcher consult.
        _ratingSyncCoordinator = new RatingSyncCoordinator(_ratingSync!, ratingStore, _engine.GameTypes, _log);
        _observer.PlayerConnected += _ratingSyncCoordinator.OnPlayerConnected;
        _observer.Register();
        _unregisterActions.Add(() =>
        {
            _observer!.PlayerConnected -= _ratingSyncCoordinator.OnPlayerConnected;
            _ratingSyncCoordinator.Dispose();
            _ratingSyncCoordinator = null;
        });
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
        _commandHandlers = new MatchmakingCommands(
            _engine, _commands, _chat, _clock, _resolver, _config, _clashLog, _orchestrators,
            _ratingSyncCoordinator, _mainloop);
        _commandHandlers.RegisterGlobal();
        _unregisterActions.Add(_commandHandlers.UnregisterGlobal);

        // Zone-wide global.conf. Provides shared game type definitions any arena can reference
        // via its [ClashEngine] section. Queues are not loaded at zone scope -- per the
        // per-arena model, every queue must have an owning arena.
        _zoneClashHandle = await _config.OpenConfigFileAsync(null, null, OnZoneClashChanged);
        if (_zoneClashHandle is not null)
        {
            await LoadZoneClashContributionAsync(cancellationToken).ConfigureAwait(false);
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
                await LoadArenaClashContributionAsync(arena, cancellationToken).ConfigureAwait(false);
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
        if (_gameTypeRegistrar is IDisposable disposableRegistrar)
            disposableRegistrar.Dispose();
        if (_ratingSync is IDisposable disposableSync)
            disposableSync.Dispose();

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
        _gameTypeRegistrar = null;
        _ratingSync = null;
        _ratingSyncCoordinator = null;
        _replayRecorder = null;

        _log.LogM(LogLevel.Info, LogCategory, "ClashEngine unloaded.");
        return Task.FromResult(true);
    }

    // ---- [ClashEngine] load / reload helpers ------------------------------------------------

    /// <summary>
    /// Parses the zone-wide <c>[ClashEngine]</c> section (from global.conf or anything
    /// <c>#include</c>'d from it), POSTs each parsed gametype to the stats server via
    /// <see cref="IGameTypeRegistrar"/>, and commits the accepted subset to the engine.
    /// Rejected and unreachable gametypes are dropped with a warn so the registry contains
    /// only stats-server-validated entries (fail-closed). On any rejection during the commit
    /// step, the previous zone contribution is preserved.
    /// </summary>
    private async Task LoadZoneClashContributionAsync(CancellationToken ct)
    {
        if (_engine is null || _zoneClashHandle is null || _clashLog is null) return;

        var parsed = MatchmakingConfig.ParseGameTypes(_config, _zoneClashHandle, _clashLog);
        var accepted = await RegisterGameTypesAsync(
            parsed, MatchmakingConfig.ZoneOriginArena, "global.conf [ClashEngine]", ct).ConfigureAwait(false);

        var acceptedList = new List<GameTypeDef>(accepted.Values);
        if (!_engine.GameTypes.ReplaceArenaContribution(sourceArena: null, acceptedList, out var errors))
        {
            foreach (var e in errors) _clashLog.Warn(LogCategory, $"global.conf [ClashEngine]: {e}");
            _clashLog.Warn(LogCategory, "global.conf [ClashEngine] rejected; prior zone-wide game types retained.");
            return;
        }
        _clashLog.Info(LogCategory,
            $"global.conf [ClashEngine] loaded ({acceptedList.Count} zone-wide game type(s) accepted of {parsed.Count} parsed).");
    }

    private void OnZoneClashChanged()
    {
        // Host fires this on the mainloop thread. The reload is async (HTTP POSTs to the
        // stats server) so we fire-and-forget on the mainloop; a top-level catch ensures a
        // failure here can't surface back into the config-change pipeline. Subsequent reloads
        // serialize by virtue of the host raising change events sequentially.
        _ = Task.Run(async () =>
        {
            try { await LoadZoneClashContributionAsync(CancellationToken.None).ConfigureAwait(false); }
            catch (Exception ex) { _log.LogM(LogLevel.Error, LogCategory, $"OnZoneClashChanged failed: {ex}"); }
        });
    }

    /// <summary>
    /// Parses <paramref name="arena"/>'s <c>[ClashEngine]</c> contribution (from its arena.conf
    /// document, including anything <c>#include</c>'d), validates every parsed gametype with
    /// the stats server, and commits the accepted gametypes + their queues. On a hot reload,
    /// queues that are removed or significantly altered are drained first and each affected
    /// player is sent a chat notice. Queues whose gametype was rejected/unreachable are
    /// dropped at parse time -- they never reach the registry.
    /// </summary>
    private async Task LoadArenaClashContributionAsync(Arena arena, CancellationToken ct)
    {
        if (_engine is null || _clashLog is null) return;
        if (!_arenaClashHandles.TryGetValue(arena, out var handle)) return;

        string src = $"arena '{arena.BaseName}' [ClashEngine]";

        // 1. Parse + register gametypes. Only the accepted subset reaches the queue parser.
        var parsedGameTypes = MatchmakingConfig.ParseGameTypes(_config, handle, _clashLog);
        var accepted = await RegisterGameTypesAsync(
            parsedGameTypes, arena.BaseName, src, ct).ConfigureAwait(false);

        var acceptedList = new List<GameTypeDef>(accepted.Values);
        if (!_engine.GameTypes.ReplaceArenaContribution(arena.BaseName, acceptedList, out var gtErrors))
        {
            foreach (var e in gtErrors) _clashLog.Warn(LogCategory, $"{src}: {e}");
            _clashLog.Warn(LogCategory, $"{src} rejected; prior contribution retained.");
            return;
        }

        // 2. Parse queues against the accepted gametype dictionary. Queues that reference an
        //    unaccepted gametype get a warn and are dropped here (rather than registered against
        //    a phantom dependency).
        var queues = MatchmakingConfig.ParseArenaQueues(_config, handle, arena.BaseName, accepted, _clashLog);

        // 3. Preview the queue diff so we can drain waiters BEFORE the registry swap. The
        //    matcher's per-queue dequeue path requires the queue to still be registered, so we
        //    have to act on the old registry state.
        if (!_engine.Queues.TryComputeArenaContributionDiff(
                arena.BaseName, queues, out var wouldRemove, out var wouldAdd, out var qErrors))
        {
            foreach (var e in qErrors) _clashLog.Warn(LogCategory, $"{src}: {e}");
            _clashLog.Warn(LogCategory, $"{src} queues rejected; prior queues retained.");
            return;
        }

        // 4. Drain waiters from queues that will be removed or have their shape changed by the
        //    swap. Notify each surviving player via chat.
        var now = _clock!.UtcNow;
        DrainQueuesWithNotice(wouldRemove, now);

        // 5. Apply the swap.
        _engine.Queues.ApplyArenaContribution(arena.BaseName, queues);

        _clashLog.Info(LogCategory,
            $"{src} loaded ({acceptedList.Count} of {parsedGameTypes.Count} game type(s) accepted, " +
            $"{queues.Count} queue(s); +{wouldAdd.Count} -{wouldRemove.Count} since last load).");
    }

    private void OnArenaClashChanged(Arena arena)
    {
        _ = Task.Run(async () =>
        {
            try { await LoadArenaClashContributionAsync(arena, CancellationToken.None).ConfigureAwait(false); }
            catch (Exception ex) { _log.LogM(LogLevel.Error, LogCategory, $"OnArenaClashChanged({arena.Name}) failed: {ex}"); }
        });
    }

    /// <summary>
    /// POSTs every parsed gametype to <see cref="IGameTypeRegistrar"/> serially, returning a
    /// name-keyed dictionary of the accepted subset. Rejected and unreachable gametypes are
    /// logged at Warn and dropped. Serial (not fan-out) because operators rarely declare more
    /// than a handful per arena and serial keeps log ordering deterministic for triage.
    /// </summary>
    private async Task<Dictionary<string, GameTypeDef>> RegisterGameTypesAsync(
        IReadOnlyDictionary<string, GameTypeDef> parsed,
        string originArena,
        string sourceLabel,
        CancellationToken ct)
    {
        var accepted = new Dictionary<string, GameTypeDef>(StringComparer.OrdinalIgnoreCase);
        if (parsed.Count == 0 || _gameTypeRegistrar is null || _clashLog is null) return accepted;

        foreach (var (name, def) in parsed)
        {
            var registration = new GameTypeRegistration(
                Name: def.Name,
                Label: def.Label,
                Description: def.Description,
                Metadata: def.Metadata,
                OriginArena: originArena);

            RegistrationResult result;
            try { result = await _gameTypeRegistrar.TryRegisterAsync(registration, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _clashLog.Warn(LogCategory,
                    $"{sourceLabel}: gametype '{name}' registration threw: {ex.Message}; dropped.");
                continue;
            }

            switch (result.Status)
            {
                case RegistrationStatus.Accepted:
                    accepted[name] = def;
                    break;
                case RegistrationStatus.Rejected:
                    _clashLog.Warn(LogCategory,
                        $"{sourceLabel}: gametype '{name}' rejected by stats server ({result.Detail}); dropped.");
                    break;
                case RegistrationStatus.Unreachable:
                    _clashLog.Warn(LogCategory,
                        $"{sourceLabel}: gametype '{name}' unreachable ({result.Detail}); dropped this reload, will retry on next load.");
                    break;
            }
        }

        return accepted;
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

    /// <summary>
    /// Picks the gametype-registration sink based on config. With both <c>UploadUrl</c> and
    /// <c>UploadApiKey</c> set, derives the gametype URL from the match-upload URL (replacing
    /// <c>/matches</c> with <c>/gametypes</c>) and returns an HTTP registrar against it.
    /// Without an <c>UploadUrl</c> the policy is fail-closed: every gametype is reported
    /// <see cref="RegistrationStatus.Unreachable"/> and dropped, since there's no stats server
    /// to validate against. (The match-upload path keeps its own JSON-file fallback for
    /// operators who still want local match envelopes.)
    /// </summary>
    private IGameTypeRegistrar BuildGameTypeRegistrar()
    {
        var url = _config.GetStr(_config.Global, "ClashEngine", "UploadUrl");
        var apiKey = _config.GetStr(_config.Global, "ClashEngine", "UploadApiKey");
        var registrationUrl = HttpGameTypeRegistrar.DeriveRegistrationUrl(url);
        if (!string.IsNullOrWhiteSpace(registrationUrl) && !string.IsNullOrWhiteSpace(apiKey))
        {
            _log.LogM(LogLevel.Info, LogCategory,
                $"Gametype registration enabled -> POST {registrationUrl}");
            return new HttpGameTypeRegistrar(registrationUrl, apiKey, _log);
        }
        return new NoStatsServerGameTypeRegistrar(_log);
    }

    /// <summary>
    /// Picks the rating-sync sink based on config. With <c>UploadUrl</c> + <c>UploadApiKey</c>
    /// set, derives the stats-API base from the upload URL (strips <c>/matches</c> or
    /// <c>/gametypes</c> if present) and returns an HTTP-backed sync. Without an
    /// <c>UploadUrl</c>, falls back to the no-op stub so the engine keeps running and the
    /// local <see cref="PersistRatingStore"/> remains the source of truth.
    /// </summary>
    private IRatingSync BuildRatingSync()
    {
        var url = _config.GetStr(_config.Global, "ClashEngine", "UploadUrl");
        var apiKey = _config.GetStr(_config.Global, "ClashEngine", "UploadApiKey");
        var apiBase = HttpRatingSync.DeriveStatsApiBase(url);
        if (!string.IsNullOrWhiteSpace(apiBase) && !string.IsNullOrWhiteSpace(apiKey))
        {
            _log.LogM(LogLevel.Info, LogCategory,
                $"Rating sync enabled -> GET/PUT {apiBase}/players/.../rating, POST {apiBase}/ratings");
            return new HttpRatingSync(apiBase, apiKey, _log);
        }
        return new NoStatsServerRatingSync(_log);
    }
}
