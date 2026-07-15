using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.Loader;
using System.Threading;
using System.Threading.Tasks;
using ClashEngine.Adapter;
using ClashEngine.Commands;
using ClashEngine.Config;
using ClashEngine.Control;
using ClashEngine.Core;
using ClashEngine.Core.Control;
using ClashEngine.Core.Events;
using ClashEngine.Core.GameType;
using ClashEngine.Core.Matching;
using ClashEngine.Core.Identity;
using ClashEngine.Core.Penalties;
using ClashEngine.Core.Preferences;
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
    private IClientSettings? _clientSettings;
    private MatchRecorder? _matchRecorder;

    private MatchmakingEngine? _engine;
    private SystemClock? _clock;
    private PlayerKeyResolver? _resolver;
    private PlayerStateObserver? _observer;
    private QueueLivenessWatcher? _queueLiveness;
    private MatchKillRouter? _killRouter;
    private EngineEventListener? _listener;
    private ClashLog? _clashLog;
    private MatchOrchestratorRegistry? _orchestrators;
    private InMemoryRatingStore? _ratingsCache;
    private PersistRatingStore? _persistRatings;
    private PersistPenaltyStore? _persistPenalties;
    private InMemoryAutoQueueStore? _autoQueueCache;
    private PersistAutoQueueStore? _persistAutoQueue;
    private PersistLastShipStore? _persistLastShip;
    private LastShipTracker? _lastShipTracker;
    private DelegatePersistentData<Player>? _ratingsRegistration;
    private DelegatePersistentData<Player>? _penaltiesRegistration;
    private DelegatePersistentData<Player>? _autoQueueRegistration;
    private DelegatePersistentData<Player>? _lastShipRegistration;
    private MatchmakingCommands? _commandHandlers;
    private ArenaQueueGreeter? _queueGreeter;
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
    private IRatingsProvider? _ratingsProvider;
    private IEventSink? _eventSink;
    private RatingsCoordinator? _ratingsCoordinator;
    private ClashReplayRecorder? _replayRecorder;
    private ControlHttpListener? _controlListener;

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

    // Per-arena arena.conf handles, keyed by arena. We read the [ClashEngine] section from
    // each one (which may sit directly in arena.conf or in any #include'd file). Each handle
    // has a change callback registered so an operator edit -- in arena.conf itself or any
    // included file -- triggers a re-parse + atomic registry swap (with waiter drops on
    // queues removed or changed by the new content).
    private readonly Dictionary<Arena, ConfigHandle> _arenaClashHandles = new();

    // Serializes every mutation of the engine registries (game-type commits, queue reconcile,
    // per-arena queue removal on unload). Reconcile now writes EVERY arena's queue entries from a
    // background Task.Run thread (the reload path), while attach/detach mutate the same
    // dictionaries from the mainloop thread -- without this gate those would race on plain
    // Dictionaries. Held only across the synchronous commit/reconcile block; the awaited HTTP
    // gametype registration deliberately runs OUTSIDE the gate so the synchronous unload path can
    // acquire it (via Wait) without ever blocking on a slow stats server.
    private readonly SemaphoreSlim _clashRegistryGate = new(1, 1);

    // Set during PreUnload/Unload so in-flight reload callbacks skip reconcile rather than touch
    // a registry that's being torn down.
    private volatile bool _shuttingDown;

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

        // Per-player ?autoqueue preference. Persist-backed when IPersist is available, otherwise an
        // in-memory cache that lives only for the process (matching the ratings fallback pattern).
        _persistAutoQueue = new PersistAutoQueueStore();
        _autoQueueCache = new InMemoryAutoQueueStore();
        IAutoQueueStore autoQueueStore = _persist is not null ? _persistAutoQueue : _autoQueueCache;

        // Per-player "last ship" memory (QoL: match placement puts each player in the ship they
        // last flew). The store doubles as its own in-memory cache, so without IPersist it still
        // works for the life of the process -- it just isn't persisted across sessions.
        _persistLastShip = new PersistLastShipStore();

        // Per-policy hard ceiling on the assessed timeout. Defaults to 6h; configurable to give
        // operators an escape hatch if a future bug ever puts a player on a runaway escalation
        // ladder again. Values <= 0 fall back to the default rather than disabling the cap.
        int maxPenaltyHours = _config.GetInt(_config.Global, "ClashEngine", "MaxPenaltyHours", 6);
        var maxPenalty = maxPenaltyHours > 0
            ? TimeSpan.FromHours(maxPenaltyHours)
            : PenaltyPolicy.DefaultMaxTimeout;

        // Elimination cooldown: when a player loses their last life in an elimination match
        // (GameType<i>Lives > 0) they are released from the match roster and must wait before
        // ?play re-queues them into a new match. The cooldown length is per game type
        // (GameType<i>EliminationCooldown), applied as a per-event base override when the match
        // forms. The policy registered here supplies the built-in 1-minute default a game type
        // falls back to when it doesn't set its own cooldown, plus the MemoryWindow / MaxTimeout
        // that bound every elimination event. It is always registered: a game type that sets
        // EliminationCooldown = 0 disables the cooldown for its own matches by recording no
        // penalty (engine-side), not by leaving the policy out.
        var penaltyPolicies = new List<PenaltyPolicy>
        {
            PenaltyPolicy.DefaultAbandonment.WithMaxTimeout(maxPenalty),
            PenaltyPolicy.DefaultGriefing.WithMaxTimeout(maxPenalty),
            PenaltyPolicy.DefaultStagingAfk.WithMaxTimeout(maxPenalty),
            PenaltyPolicy.DefaultEliminationCooldown.WithMaxTimeout(maxPenalty),
        };

        // Engine-wide match-quality function. Zone-scope because the engine holds a single instance
        // shared by every queue. OrdinalSpread (default) scores on mu-3sigma team-mean spread;
        // PredictDraw uses OpenSkill's win-probability balance (see PredictDrawQuality). Note the two
        // score on different scales, so the qStart/qFloor bands mean different things under each.
        var quality = BuildQualityFunction();
        _clashLog.Info(LogCategory, $"Match-quality function = {quality.GetType().Name}.");

        // Engine is constructed with a no-op telemetry sink so that listeners (which need the
        // engine reference) can be wired up below; once they exist we swap in the real
        // composite via SetTelemetry. Events fired before that swap (none, in practice -- the
        // engine doesn't emit on construction) would have hit the no-op sink anyway.
        _engine = new MatchmakingEngine(
            ratings: ratingStore,
            clock: _clock,
            penaltyPolicies: penaltyPolicies,
            quality: quality,
            invitationTtl: TimeSpan.FromSeconds(15),
            autoQueue: autoQueueStore);

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
        // Assist eligibility gets twice the credit half-life (4 s vs 2 s): the 2 s decay made
        // assists vanish for damage dealt even a few seconds before the kill. KillCredit /
        // ForcedRepelCredit normalization stays on the default decay.
        _matchStats = new MatchStatsRegistry(
            new DamageDecay(),
            assistDecay: new DamageDecay(2 * DamageDecay.DefaultHalfLifeTicks));

        // Client-settings edge for the in-match respawn-box override (a port of SS's
        // SendSpawnOverrides). Optional: if the interface (or the [Spawn] identifiers) can't be
        // resolved, the applier reports not-ready and respawn overrides degrade to the arena
        // default with a one-time warning.
        _clientSettings = broker.GetInterface<IClientSettings>();
        var spawnApplier = _clientSettings is not null
            ? new SpawnSettingsApplier(_clientSettings, _log)
            : null;

        // Same client-settings edge, different overrides: zeroed per-ship item maxes for game
        // types with DisallowItems=1 (no-items mode) and zeroed AntiWarpStatus for game types
        // with DisallowAntiwarp=1. Optional with the same degrade story.
        var noItemsApplier = _clientSettings is not null
            ? ShipSettingsZeroApplier.NoItems(_clientSettings, _log)
            : null;
        var noAntiwarpApplier = _clientSettings is not null
            ? ShipSettingsZeroApplier.NoAntiwarp(_clientSettings, _log)
            : null;

        // Keeps the last-ship store current from zone-wide ship->spec (and in-ship arena-exit)
        // transitions, in and out of matches alike.
        _lastShipTracker = new LastShipTracker(broker, _persistLastShip, _resolver);
        _lastShipTracker.Register();
        _unregisterActions.Add(_lastShipTracker.Unregister);

        // Per-life ship lock + per-match freq lock. Implements IFreqManagerEnforcerAdvisor and
        // registers on every configured match arena; SS Core's FreqManager consults it on each
        // ship/freq change request from a player. Direct API placements via IGame.SetShipAndFreq
        // (orchestrator setup, knockout-spec) bypass the advisor and remain unaffected.
        // Constructed before the orchestrator registry: each orchestrator re-anchors the
        // advisor's ship-lock deadline (OnCountdownStarted) when its countdown actually begins,
        // since staging can short-circuit early. Registration happens further down with the
        // other advisors/listeners.
        _freqAdvisor = new MatchFreqAdvisor(broker, _engine, _arenaManager, _resolver, _clock, _log, _chat, freqAllocator);

        _orchestrators = new MatchOrchestratorRegistry(
            broker, _engine, _game, _chat, _mainloopTimer, _arenaManager, _clock, _log, _resolver, _clashLog,
            matchAudience, freqAllocator, matchStats: _matchStats, spawnApplier: spawnApplier,
            noItemsApplier: noItemsApplier, noAntiwarpApplier: noAntiwarpApplier,
            lastShips: _persistLastShip, freqAdvisor: _freqAdvisor);
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
        _ratingsProvider = BuildRatingsProvider();

        // Outbound event-stream edge: a pure Core mapper translates telemetry into normalized
        // JSON envelopes handed to the sink (HTTP when configured, no-op otherwise). The sink owns
        // a background worker, so register its disposal LIFO like every other Register() callsite.
        _eventSink = BuildEventSink();
        if (_eventSink is IDisposable disposableEventSink)
            _unregisterActions.Add(disposableEventSink.Dispose);
        var eventStreamTelemetry = new EventStreamTelemetry(_eventSink, _engine.Queues, _engine.GameTypes, _clock);

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

        // Queue liveness: in-game activity (ship rotation change, weapon fire, chat) refreshes
        // queued players' AFK dwell clocks so present players never need to re-?play. Zone-scope
        // kill switch: when 0 the watcher isn't constructed, so the zone-wide position/chat
        // callbacks aren't subscribed at all and refresh reverts to ?play-only.
        bool queueActivityRefresh = _config.GetInt(_config.Global, "ClashEngine", "QueueActivityRefresh", 1) != 0;
        if (queueActivityRefresh)
        {
            _queueLiveness = new QueueLivenessWatcher(broker, _engine, _resolver, _clock, _clashLog);
            _queueLiveness.Register();
            _unregisterActions.Add(_queueLiveness.Unregister);
        }

        // _listener is registered AFTER _matchStatsTelemetry so its OnMatchEnded handler runs
        // post-statbox: EngineEventListener buffers post-match DMs (abandonment / griefing /
        // promotion) during FinalizeMatch and drains them in OnMatchEnded, which must come after
        // ClashStatsTelemetry has broadcast the scoreboard. The other events EngineEventListener
        // handles are independent of the listeners that come before it here.
        // EventStreamTelemetry leads: it's a pure observer (emits to the sink, never broadcasts
        // chat or mutates engine state), so it has no ordering dependency and reads the same state
        // the other listeners see. It must NOT go after _listener, which stays last for its
        // post-match DM drain.
        var listeners = new List<Core.Adapter.IMatchmakingTelemetry> { eventStreamTelemetry, _orchestrators };
        // The liveness watcher is a pure observer (tracks queue membership only, never chats or
        // mutates engine state), so like eventStreamTelemetry it has no ordering dependency.
        if (_queueLiveness is not null) listeners.Add(_queueLiveness);
        if (_replayRecorder is not null) listeners.Add(_replayRecorder);
        listeners.Add(_matchStatsTelemetry);
        listeners.Add(_listener);
        listeners.Add(_lvzAdapter);
        listeners.Add(_freqAdvisor);
        _engine.SetTelemetry(new CompositeTelemetry(listeners.ToArray()));

        _observer = new PlayerStateObserver(broker, _engine, _resolver, _clock, _clashLog);
        // Ratings coordinator: pull-on-first-connect. Match envelopes carry post-match
        // ratings BACK to the server, so there's no push path here -- the coordinator only
        // seeds the local cache when a player connects to a zone that has no persisted data
        // for them. Subscribed to PlayerStateObserver BEFORE Register() so the very first
        // connect event reaches the coordinator; uses the same in-memory rating store the
        // engine reads so a pull populates the same rows ?play and the matcher consult.
        _ratingsCoordinator = new RatingsCoordinator(_ratingsProvider!, ratingStore, _engine.GameTypes, _log);
        _observer.PlayerConnected += _ratingsCoordinator.OnPlayerConnected;
        _observer.Register();
        _unregisterActions.Add(() =>
        {
            _observer!.PlayerConnected -= _ratingsCoordinator.OnPlayerConnected;
            _ratingsCoordinator.Dispose();
            _ratingsCoordinator = null;
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

            _autoQueueRegistration = new DelegatePersistentData<Player>(
                PersistAutoQueueStore.PersistKey, PersistInterval.Forever, PersistScope.Global,
                getDataCallback: _persistAutoQueue.GetData,
                setDataCallback: _persistAutoQueue.SetData,
                clearDataCallback: _persistAutoQueue.ClearData);
            await _persist.RegisterPersistentDataAsync(_autoQueueRegistration);

            _lastShipRegistration = new DelegatePersistentData<Player>(
                PersistLastShipStore.PersistKey, PersistInterval.Forever, PersistScope.Global,
                getDataCallback: _persistLastShip.GetData,
                setDataCallback: _persistLastShip.SetData,
                clearDataCallback: _persistLastShip.ClearData);
            await _persist.RegisterPersistentDataAsync(_lastShipRegistration);
        }

        // Only ?play and ?queue are zone-wide so a player can opt into matchmaking from any
        // arena (lobby, public, etc.). The remaining 11 matchmaking commands register
        // per-arena in AttachModuleAsync.
        _commandHandlers = new MatchmakingCommands(
            _engine, _commands, _chat, _clock, _resolver, _config, _clashLog, _orchestrators,
            _ratingsCoordinator, _mainloop);
        _commandHandlers.RegisterGlobal();
        _unregisterActions.Add(_commandHandlers.UnregisterGlobal);

        // Inbound control surface (optional): an HTTP listener external services use to
        // manipulate queue state and form private matches (schema/command.schema.json) and to
        // pull a full state snapshot (schema/snapshot.schema.json) -- the write half of the
        // integration surface, mirroring the outbound event stream. Requests are marshaled onto
        // the mainloop; the listener owns background accept/worker tasks, so its disposal
        // registers LIFO like every other Register() callsite.
        _controlListener = BuildControlListener();
        if (_controlListener is not null)
            _unregisterActions.Add(_controlListener.Dispose);

        // Auto-show the arena's queue table (the naked-?queue view) to each player on EnterGame,
        // so the matchmaking surface is discoverable without knowing the command. Zone-scope kill
        // switch: when 0 the greeter isn't constructed, so the EnterGame callback isn't subscribed
        // and players see the queue only when they issue ?queue themselves.
        bool showQueueOnEnter = _config.GetInt(_config.Global, "ClashEngine", "ShowQueueOnEnter", 1) != 0;
        if (showQueueOnEnter)
        {
            _queueGreeter = new ArenaQueueGreeter(broker, _engine, _commandHandlers, _resolver);
            _queueGreeter.Register();
            _unregisterActions.Add(_queueGreeter.Unregister);
        }

        // Game types and queues are arena-scoped only: each must be declared in the arena.conf
        // [ClashEngine] section of the arena that uses it. We deliberately do NOT parse game
        // types or queues from global.conf. Warn if an operator left a block there so the
        // now-ignored config surfaces as a migration notice rather than a silent no-op.
        WarnOnStrandedZoneClashBlocks();

        _mainloopTimer.SetTimer(OnTick, initialDelay: 500, interval: 500, key: this);

        _log.LogM(LogLevel.Info, LogCategory,
            "ClashEngine ready (game types and queues load per-arena on attach; " +
            $"{_engine.GameTypes.Count} game type(s), {_engine.Queues.Count} queue(s) registered so far).");
    }

    async Task IAsyncModuleLoaderAware.PreUnloadAsync(IComponentBroker broker, CancellationToken cancellationToken)
    {
        // Signal teardown so any in-flight reload callback (OnArenaClashChanged -> Task.Run) skips
        // its commit/reconcile instead of mutating registries we're tearing down.
        _shuttingDown = true;
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
            if (_autoQueueRegistration is not null)
                await _persist.UnregisterPersistentDataAsync(_autoQueueRegistration);
            if (_lastShipRegistration is not null)
                await _persist.UnregisterPersistentDataAsync(_lastShipRegistration);
        }
        _ratingsRegistration = null;
        _penaltiesRegistration = null;
        _autoQueueRegistration = null;
        _lastShipRegistration = null;
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
                // Register the unload action BEFORE awaiting the load: if the load throws partway,
                // the rollback below closes the handle and drops any partial contribution, rather
                // than leaking the handle in _arenaClashHandles.
                actions.Add(() => UnloadArenaClashContribution(arena));
                await LoadArenaClashContributionAsync(arena, cancellationToken).ConfigureAwait(false);
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
        if (_ratingsProvider is IDisposable disposableProvider)
            disposableProvider.Dispose();

        // _matchRecorder.Unregister was already invoked via the _unregisterActions list during
        // PreUnloadAsync; here we only release the server interfaces it depended on.
        if (_securitySeedSync is not null) broker.ReleaseInterface(ref _securitySeedSync);
        if (_network is not null) broker.ReleaseInterface(ref _network);
        if (_mapData is not null) broker.ReleaseInterface(ref _mapData);
        if (_watchDamage is not null) broker.ReleaseInterface(ref _watchDamage);
        if (_clientSettings is not null) broker.ReleaseInterface(ref _clientSettings);

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
        _autoQueueCache = null;
        _persistAutoQueue = null;
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
        _ratingsProvider = null;
        _eventSink = null;
        _ratingsCoordinator = null;
        _replayRecorder = null;
        _controlListener = null;

        _log.LogM(LogLevel.Info, LogCategory, "ClashEngine unloaded.");
        return Task.FromResult(true);
    }

    // ---- [ClashEngine] load / reload helpers ------------------------------------------------

    /// <summary>
    /// Game types and queues live in arena.conf only. If an operator still has a
    /// <c>GameTypeCount</c> or <c>QueueCount</c> under <c>[ClashEngine]</c> in global.conf
    /// (the pre-arena-scoped layout), those keys are now ignored -- warn once at load so the
    /// migration is visible rather than silently dropping their matchmaking config. Read
    /// directly off <see cref="IConfigManager.Global"/>; no watched handle is opened, so edits
    /// to these stranded keys do not trigger any reload.
    /// </summary>
    private void WarnOnStrandedZoneClashBlocks()
    {
        if (_clashLog is null) return;
        int gtCount = _config.GetInt(_config.Global, "ClashEngine", "GameTypeCount", 0);
        int qCount = _config.GetInt(_config.Global, "ClashEngine", "QueueCount", 0);
        if (gtCount <= 0 && qCount <= 0) return;

        _clashLog.Warn(LogCategory,
            $"global.conf [ClashEngine] declares GameTypeCount={gtCount}, QueueCount={qCount}, but game types " +
            "and queues are parsed from arena.conf only and are ignored at zone scope. Move these blocks into " +
            "the arena.conf of each arena that uses them.");
    }

    /// <summary>
    /// Parses <paramref name="arena"/>'s <c>[ClashEngine]</c> contribution (from its arena.conf
    /// document, including anything <c>#include</c>'d), validates every parsed gametype with the
    /// stats server, commits the accepted gametypes to the (sticky) global registry, and then
    /// reconciles every attached arena's queues against the updated registry. Because game types
    /// are globally referenceable, a queue here can name a gametype declared in another arena, and
    /// a queue in another arena can name one declared here -- the reconcile picks those up
    /// regardless of attach order. On a hot reload, queues removed or altered by the new content
    /// are drained first and each affected player is sent a chat notice.
    /// </summary>
    private async Task LoadArenaClashContributionAsync(Arena arena, CancellationToken ct)
    {
        var engine = _engine;
        if (engine is null || _clashLog is null || _shuttingDown) return;
        if (!_arenaClashHandles.TryGetValue(arena, out var handle)) return;

        string src = $"arena '{arena.BaseName}' [ClashEngine]";

        // 1. Parse + register gametypes with the stats server. The awaited HTTP runs OUTSIDE the
        //    registry gate so it can never make the synchronous unload path block on a slow server.
        var parsedGameTypes = MatchmakingConfig.ParseGameTypes(_config, handle, _clashLog);
        var accepted = await RegisterGameTypesAsync(
            parsedGameTypes, arena.BaseName, src, ct).ConfigureAwait(false);

        // A gametype this arena previously registered but that came back Unreachable this pass
        // (network/5xx, NOT a 4xx rejection) is carried forward from the registry so a transient
        // stats-server blip can't drop a sticky type other arenas' queues may depend on.
        CarryForwardUnreachableGameTypes(engine, parsedGameTypes, accepted, arena.BaseName, src);

        // 2. Commit this arena's gametypes, then reconcile all arenas' queues. Gated because the
        //    reconcile writes every arena's queue entries and must not race attach/detach/other
        //    reloads on the plain-Dictionary registries. The body is synchronous and sub-ms.
        await _clashRegistryGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_shuttingDown) return;

            var acceptedList = new List<GameTypeDef>(accepted.Values);
            if (!engine.GameTypes.ReplaceArenaContribution(arena.BaseName, acceptedList, out var gtErrors))
            {
                foreach (var e in gtErrors) _clashLog.Warn(LogCategory, $"{src}: {e}");
                _clashLog.Warn(LogCategory, $"{src} rejected; prior contribution retained.");
                return;
            }
            _clashLog.Info(LogCategory,
                $"{src}: {acceptedList.Count} of {parsedGameTypes.Count} game type(s) accepted.");

            ReconcileAllArenaQueues(engine);
        }
        finally
        {
            _clashRegistryGate.Release();
        }
    }

    /// <summary>
    /// Re-resolves and re-applies EVERY attached arena's queues against the current global gametype
    /// registry, draining waiters from queues that the new resolution removes or changes. This is
    /// what makes gametypes globally referenceable independent of attach order: a queue that names a
    /// gametype declared in another arena resolves as soon as that arena's gametype is registered.
    /// <para>Queue-only by contract: it never registers gametypes (no HTTP, no config-watch
    /// re-trigger), so it cannot feed back into itself. Idempotent -- an unchanged arena yields an
    /// empty diff via <c>QueueShapeMatches</c> and drops no waiters. MUST be called while holding
    /// <see cref="_clashRegistryGate"/>.</para>
    /// </summary>
    private void ReconcileAllArenaQueues(MatchmakingEngine engine)
    {
        if (_clashLog is null) return;

        // Global gametype snapshot. OrdinalIgnoreCase to match GameTypeRegistry's own comparer, so
        // a queue's GameType reference resolves case-insensitively the same way it does at runtime.
        var gameTypes = new Dictionary<string, GameTypeDef>(StringComparer.OrdinalIgnoreCase);
        foreach (var def in engine.GameTypes.Definitions) gameTypes[def.Name] = def;

        var now = _clock?.UtcNow ?? DateTimeOffset.UtcNow;

        // Snapshot the handle set: we're under the gate, but copying is cheap insurance against any
        // future caller mutating _arenaClashHandles mid-iteration.
        var arenas = new List<KeyValuePair<Arena, ConfigHandle>>(_arenaClashHandles);
        foreach (var (arena, handle) in arenas)
        {
            string src = $"arena '{arena.BaseName}' [ClashEngine]";
            var queues = MatchmakingConfig.ParseArenaQueues(_config, handle, arena.BaseName, gameTypes, _clashLog);

            if (!engine.Queues.TryComputeArenaContributionDiff(
                    arena.BaseName, queues, out var wouldRemove, out var wouldAdd, out var qErrors))
            {
                foreach (var e in qErrors) _clashLog.Warn(LogCategory, $"{src}: {e}");
                _clashLog.Warn(LogCategory, $"{src} queues rejected; prior queues retained.");
                continue;
            }

            if (wouldRemove.Count > 0) DrainQueuesWithNotice(wouldRemove, now);
            engine.Queues.ApplyArenaContribution(arena.BaseName, queues);

            if (wouldAdd.Count > 0 || wouldRemove.Count > 0)
                _clashLog.Info(LogCategory,
                    $"{src} queues: +{wouldAdd.Count} -{wouldRemove.Count} ({queues.Count} total).");
        }
    }

    /// <summary>
    /// Adds back into <paramref name="accepted"/> any gametype this arena parsed that is missing
    /// from the accepted set purely because its registration came back Unreachable this pass, AND
    /// that is already present in the registry under this arena's ownership. This keeps a sticky
    /// gametype alive across a transient stats-server outage on reload, so its registry entry (and
    /// any cross-arena queue depending on it) survives. Rejected (4xx) gametypes are NOT carried
    /// forward -- the operator must fix those.
    /// </summary>
    private void CarryForwardUnreachableGameTypes(
        MatchmakingEngine engine,
        IReadOnlyDictionary<string, GameTypeDef> parsed,
        Dictionary<string, GameTypeDef> accepted,
        string ownerArena,
        string src)
    {
        foreach (var (name, _) in parsed)
        {
            if (accepted.ContainsKey(name)) continue;
            if (!engine.GameTypes.TryGet(name, out var existing)) continue;
            if (!engine.GameTypes.TryGetSource(name, out var source)) continue;
            if (!string.Equals(source, ownerArena, StringComparison.OrdinalIgnoreCase)) continue;

            accepted[name] = existing;
            _clashLog?.Info(LogCategory,
                $"{src}: gametype '{name}' unreachable this pass; retaining the previously-registered definition.");
        }
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
    /// Removes <paramref name="arena"/>'s queues from the engine and closes its arena.conf handle.
    /// Invoked from the per-arena unregister-action list when the arena is detached. Its game types
    /// are deliberately NOT removed -- see the body.
    /// </summary>
    private void UnloadArenaClashContribution(Arena arena)
    {
        ConfigHandle? handleToClose = null;
        var engine = _engine;

        // Gate the registry mutation against a concurrent reload's reconcile (which writes queue
        // entries from a background thread). HTTP never runs under this gate, so the wait is sub-ms
        // and can't stall the mainloop on a slow stats server.
        _clashRegistryGate.Wait();
        try
        {
            if (!_arenaClashHandles.Remove(arena, out var handle)) return;
            handleToClose = handle;

            if (engine is not null)
            {
                // Drain waiters from this arena's queues, then drop the queues. Mirrors the
                // hot-reload "removed queue" path so detach uses the same UX.
                var owned = new List<QueueDefinition>();
                foreach (var def in engine.Queues.Definitions)
                    if (string.Equals(def.OwnerArenaName, arena.BaseName, StringComparison.OrdinalIgnoreCase))
                        owned.Add(def);
                DrainQueuesWithNotice(owned, _clock?.UtcNow ?? DateTimeOffset.UtcNow);
                engine.Queues.RemoveByOwner(arena.BaseName);

                // Game types are intentionally STICKY: we do not remove them on detach. They persist
                // for the process lifetime so (a) queues in other arenas that reference this arena's
                // game types keep resolving after it detaches, and (b) the name stays owned by this
                // arena, so a different arena can't redefine it once this one has claimed it. A
                // re-attach of this arena re-commits its types (same-source replace) and updates them.
            }
        }
        finally
        {
            _clashRegistryGate.Release();
        }

        if (handleToClose is { } h)
        {
            try { _config.CloseConfigFile(h); }
            catch (Exception ex) { _log.LogM(LogLevel.Error, LogCategory, $"CloseConfigFile({arena.Name}) failed: {ex}"); }
        }
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
    /// Picks the engine-wide match-quality function from <c>[ClashEngine] QualityFunction</c>:
    /// <c>PredictDraw</c> -> <see cref="PredictDrawQuality"/>; anything else (incl. unset) ->
    /// <see cref="OrdinalSpreadQuality"/>. An unrecognized non-empty value warns and defaults.
    /// </summary>
    private IMatchQualityFunction BuildQualityFunction()
    {
        var kind = _config.GetStr(_config.Global, "ClashEngine", "QualityFunction");
        if (string.IsNullOrWhiteSpace(kind) ||
            string.Equals(kind, "OrdinalSpread", StringComparison.OrdinalIgnoreCase))
            return new OrdinalSpreadQuality();
        if (string.Equals(kind, "PredictDraw", StringComparison.OrdinalIgnoreCase))
            return new PredictDrawQuality();

        _clashLog?.Warn(LogCategory,
            $"QualityFunction='{kind}' not recognized (expected OrdinalSpread|PredictDraw); using OrdinalSpread.");
        return new OrdinalSpreadQuality();
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
    /// Picks the <see cref="IRatingsProvider"/> based on config. With <c>UploadUrl</c> +
    /// <c>UploadApiKey</c> set, derives the stats-API base from the upload URL (strips
    /// <c>/matches</c> or <c>/gametypes</c> if present) and returns the HTTP-backed
    /// provider. Without an <c>UploadUrl</c>, falls back to the no-op stub so the engine
    /// keeps running and the local <see cref="PersistRatingStore"/> remains the source of
    /// truth.
    /// </summary>
    private IRatingsProvider BuildRatingsProvider()
    {
        var url = _config.GetStr(_config.Global, "ClashEngine", "UploadUrl");
        var apiKey = _config.GetStr(_config.Global, "ClashEngine", "UploadApiKey");
        var apiBase = HttpRatingsProvider.DeriveStatsApiBase(url);
        if (!string.IsNullOrWhiteSpace(apiBase) && !string.IsNullOrWhiteSpace(apiKey))
        {
            _log.LogM(LogLevel.Info, LogCategory,
                $"Ratings provider enabled -> GET {apiBase}/players/.../rating");
            return new HttpRatingsProvider(apiBase, apiKey, _log);
        }
        return new NoStatsServerRatingsProvider(_log);
    }

    /// <summary>
    /// Picks the outbound event-stream sink based on config. With <c>EventStreamUrl</c> set (plus
    /// an API key -- a dedicated <c>EventStreamApiKey</c>, falling back to <c>UploadApiKey</c> so a
    /// single gateway needs no extra config), returns an <see cref="HttpEventSink"/> that POSTs
    /// normalized queue/match/player events as JSON. Otherwise the no-op sink, so event emission
    /// is simply suppressed. The URL is always dedicated -- never derived from <c>UploadUrl</c>,
    /// since the event consumer (e.g. a Discord bot) is a distinct service.
    /// </summary>
    private IEventSink BuildEventSink()
    {
        var url = _config.GetStr(_config.Global, "ClashEngine", "EventStreamUrl");
        var apiKey = _config.GetStr(_config.Global, "ClashEngine", "EventStreamApiKey");
        if (string.IsNullOrWhiteSpace(apiKey))
            apiKey = _config.GetStr(_config.Global, "ClashEngine", "UploadApiKey");

        if (!string.IsNullOrWhiteSpace(url) && !string.IsNullOrWhiteSpace(apiKey))
        {
            _log.LogM(LogLevel.Info, LogCategory, $"Event stream enabled -> POST {url}");
            return new HttpEventSink(url, apiKey, _log);
        }

        _log.LogM(LogLevel.Info, LogCategory,
            "Event stream not configured (EventStreamUrl unset); queue/match event emission disabled.");
        return NoOpEventSink.Instance;
    }

    /// <summary>
    /// Starts the inbound control listener when configured. Requires <c>ControlListenUrl</c>
    /// (e.g. <c>http://127.0.0.1:9550/clash/</c>; loopback strongly recommended -- the paired
    /// service is expected to run on the same box) plus an api key -- a dedicated
    /// <c>ControlApiKey</c>, falling back to <c>UploadApiKey</c> so a single-gateway setup needs
    /// no extra config. Unset URL or key = the control surface is simply off. A bind failure
    /// (port in use) is logged and disables the surface rather than failing the module load.
    /// </summary>
    private ControlHttpListener? BuildControlListener()
    {
        var url = _config.GetStr(_config.Global, "ClashEngine", "ControlListenUrl");
        var apiKey = _config.GetStr(_config.Global, "ClashEngine", "ControlApiKey");
        if (string.IsNullOrWhiteSpace(apiKey))
            apiKey = _config.GetStr(_config.Global, "ClashEngine", "UploadApiKey");

        if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(apiKey))
        {
            _log.LogM(LogLevel.Info, LogCategory,
                "Control surface not configured (ControlListenUrl/api key unset); inbound commands disabled.");
            return null;
        }

        var listener = new ControlHttpListener(
            url, apiKey, _engine!, new ControlCommandDispatcher(_engine!),
            _ratingsCoordinator!, new MainloopDispatcher(_mainloop), _clock!, _log);
        try
        {
            listener.Start();
        }
        catch (Exception ex)
        {
            _log.LogM(LogLevel.Error, LogCategory,
                $"Control surface failed to bind '{url}': {ex.Message}; inbound commands disabled.");
            listener.Dispose();
            return null;
        }

        _log.LogM(LogLevel.Info, LogCategory,
            $"Control surface enabled -> POST {url.TrimEnd('/')}/commands, GET {url.TrimEnd('/')}/state");
        return listener;
    }
}
