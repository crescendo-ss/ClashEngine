using System;
using System.Collections.Generic;
using ClashEngine.Adapter;
using ClashEngine.Core;
using ClashEngine.Core.Adapter;
using ClashEngine.Core.Identity;
using SS.Core;
using SS.Core.ComponentCallbacks;
using SS.Core.ComponentInterfaces;
using SS.Packets.Game;

namespace ClashEngine.Events;

/// <summary>
/// Single owner of the <see cref="KillCallback"/> subscription. On each kill the router (1)
/// calls <c>engine.OnKill</c> so the per-match state (kill counts, lives remaining) is updated,
/// then (2) fans out to <see cref="ReaderDelegate"/>s registered by downstream consumers
/// (orchestrator registry, LVZ adapter, stats listener).
/// </summary>
/// <remarks>
/// <para>This replaces the previous setup where four separate components each subscribed to
/// <c>KillCallback</c> directly. Three of them needed engine state to already reflect the kill
/// before they read <c>LivesRemaining</c>, which used to be enforced by registering them later
/// in <c>ClashModule.PostLoadAsync</c> than <c>PlayerStateObserver</c>. That ordering was
/// implicit and a reordering of construction would silently break knockout detection. With the
/// router, engine state is mutated by the router itself before any reader runs, so the
/// dependency is structural rather than positional.</para>
///
/// <para>Reader exceptions are caught and logged so a single bad reader can't kill the
/// remaining handlers. The state-update phase is also wrapped: if <c>engine.OnKill</c> throws,
/// the readers are skipped (they would be reading a partial state anyway).</para>
///
/// <para><see cref="ClashEngine.Recording.MatchRecorder"/> deliberately keeps its own
/// <c>KillCallback</c> subscription -- replay encoding is independent of engine state and has
/// no ordering relationship with anyone else.</para>
/// </remarks>
public sealed class MatchKillRouter
{
    private const string LogCategory = nameof(MatchKillRouter);

    /// <summary>Post-state-update kill handler. Same signature as <c>KillDelegate</c> minus the
    /// callback wiring -- just the kill data.</summary>
    public delegate void ReaderDelegate(Arena arena, Player killer, Player killed, short bounty, short flagCount, short points, Prize green);

    private readonly IComponentBroker _broker;
    private readonly MatchmakingEngine _engine;
    private readonly PlayerKeyResolver _resolver;
    private readonly IClock _clock;
    private readonly ILogManager _log;
    private readonly ClashLog _verbose;
    private readonly List<(string Name, ReaderDelegate Handler)> _preEngineReaders = new();
    private readonly List<(string Name, ReaderDelegate Handler)> _readers = new();
    private bool _registered;

    public MatchKillRouter(
        IComponentBroker broker,
        MatchmakingEngine engine,
        PlayerKeyResolver resolver,
        IClock clock,
        ILogManager log,
        ClashLog verbose)
    {
        _broker = broker ?? throw new ArgumentNullException(nameof(broker));
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _verbose = verbose ?? throw new ArgumentNullException(nameof(verbose));
    }

    /// <summary>Add a post-state-update kill reader. <paramref name="readerName"/> is used in
    /// log lines if the reader throws. Readers fire in registration order.</summary>
    public void AddReader(string readerName, ReaderDelegate handler)
    {
        ArgumentException.ThrowIfNullOrEmpty(readerName);
        ArgumentNullException.ThrowIfNull(handler);
        _readers.Add((readerName, handler));
    }

    /// <summary>
    /// Add a pre-state-update kill reader -- runs <em>before</em> <c>engine.OnKill</c>. Use this
    /// for consumers (notably stats recording) that must observe the kill even when it's the
    /// match-ending one: <c>engine.OnKill</c> can synchronously trigger <c>FinalizeMatch</c>,
    /// which fires <c>OnMatchEnded</c> and tears down per-match state. A post-engine reader
    /// would see that teardown and silently drop the final kill.
    /// </summary>
    public void AddPreEngineReader(string readerName, ReaderDelegate handler)
    {
        ArgumentException.ThrowIfNullOrEmpty(readerName);
        ArgumentNullException.ThrowIfNull(handler);
        _preEngineReaders.Add((readerName, handler));
    }

    public void Register()
    {
        if (_registered) return;
        KillCallback.Register(_broker, OnKill);
        _registered = true;
    }

    public void Unregister()
    {
        if (!_registered) return;
        KillCallback.Unregister(_broker, OnKill);
        _registered = false;
    }

    private void OnKill(Arena arena, Player killer, Player killed, short bounty, short flagCount, short points, Prize green)
    {
        // Phase 1: state update. Resolve keys and forward to the engine. Only proceed to
        // readers if both players resolve and the engine update doesn't throw -- otherwise
        // readers would be inspecting partial state.
        if (_resolver.KeyOf(killer) is not PlayerKey killerKey)
        {
            if (_verbose.IsTrace)
                _verbose.Trace(LogCategory, $"Kill (unresolved killer) killer={killer.Name ?? "(no-name)"} killed={killed.Name ?? "(no-name)"}");
            return;
        }
        if (_resolver.KeyOf(killed) is not PlayerKey killedKey)
        {
            if (_verbose.IsTrace)
                _verbose.Trace(LogCategory, $"Kill (unresolved victim) killer={killerKey.Name} killed={killed.Name ?? "(no-name)"}");
            return;
        }

        if (_verbose.IsTrace)
            _verbose.Trace(LogCategory, $"Kill {killerKey.Name} -> {killedKey.Name} (bounty={bounty}, flags={flagCount}, points={points})");

        // Phase 0: pre-engine readers. Stats recording lives here so the final kill of a match
        // is captured before engine.OnKill synchronously tears the match down via FinalizeMatch.
        for (int i = 0; i < _preEngineReaders.Count; i++)
        {
            var (name, handler) = _preEngineReaders[i];
            try { handler(arena, killer, killed, bounty, flagCount, points, green); }
            catch (Exception ex)
            {
                _log.LogM(LogLevel.Error, LogCategory,
                    $"Pre-engine kill reader '{name}' threw for {killerKey.Name} -> {killedKey.Name}: {ex}");
            }
        }

        try
        {
            _engine.OnKill(killerKey, killedKey, _clock.UtcNow);
        }
        catch (Exception ex)
        {
            _log.LogM(LogLevel.Error, LogCategory,
                $"engine.OnKill failed for {killerKey.Name} -> {killedKey.Name}; skipping readers: {ex}");
            return;
        }

        // Phase 2: fan-out. Each reader gets the unmodified callback args; if a reader throws,
        // log and continue to the next so a single bad consumer can't take out the rest.
        for (int i = 0; i < _readers.Count; i++)
        {
            var (name, handler) = _readers[i];
            try { handler(arena, killer, killed, bounty, flagCount, points, green); }
            catch (Exception ex)
            {
                _log.LogM(LogLevel.Error, LogCategory,
                    $"Kill reader '{name}' threw for {killerKey.Name} -> {killedKey.Name}: {ex}");
            }
        }
    }
}
