using System;
using System.Collections.Generic;
using System.Text;
using ClashEngine.Adapter;
using ClashEngine.Core;
using ClashEngine.Core.Adapter;
using ClashEngine.Core.Identity;
using ClashEngine.Core.Matches;
using ClashEngine.Core.Matching;
using ClashEngine.Core.Penalties;
using ClashEngine.Core.Queue;
using SS.Core;
using SS.Core.ComponentAdvisors;
using SS.Core.ComponentCallbacks;
using SS.Core.ComponentInterfaces;
using SS.Packets.Game;

namespace ClashEngine.Adapter;

/// <summary>
/// Enforces the per-life ship lock and the per-match freq lock for active match participants.
/// Implements <see cref="IFreqManagerEnforcerAdvisor"/> and registers on every configured match
/// arena (the same arena name(s) declared on each <see cref="QueueDefinition.MatchArenaName"/>).
/// </summary>
/// <remarks>
/// <para><b>Per-life ship lock.</b> Mid-life ship changes are forbidden because each ship
/// transition refreshes Continuum item counts (a free reload). After a non-knockout death the
/// player gets a brief grace window (queue-configured <see cref="QueueDefinition.ShipChangeGracePeriod"/>)
/// to swap ships before being re-locked to whatever ship they're currently in. Knockouts (last
/// life) don't open a window -- the orchestrator's deferred spec handles that path.</para>
///
/// <para><b>Pre-match window.</b> From <see cref="OnMatchProposed"/> through staging and most of
/// the countdown, ship changes are unrestricted -- the advisor opens the same grace window it
/// uses post-death so participants can pick their loadout. The lock kicks in
/// <see cref="ShipLockBeforeStart"/> before GO so the final seconds are committed; the
/// orchestrator broadcasts a "Locked you to your current ship" notice at that moment. Free
/// reloads aren't a concern pre-GO because no fighting has started yet.</para>
///
/// <para><b>Freq lock.</b> Match participants can only change to their assigned freq
/// (effectively a no-op). Going to spec is always permitted by SS Core's FreqManager (it short
/// circuits before consulting the advisor) so abandonment via Esc-S still works; the engine's
/// abandonment policy takes over from there.</para>
///
/// <para><b>Direct API placement.</b> ClashEngine's own placements via <c>IGame.SetShipAndFreq</c>
/// bypass the advisor (FreqManager only consults advisors when a player initiates the change),
/// so the orchestrator's setup and knockout-spec calls are unaffected.</para>
/// </remarks>
public sealed class MatchFreqAdvisor : IFreqManagerEnforcerAdvisor, IMatchmakingTelemetry
{
    private const string LogCategory = nameof(MatchFreqAdvisor);

    /// <summary>How long before GO the per-life ship lock takes effect. Players can change
    /// ships during staging and the early portion of the countdown; once countdown drops to
    /// this many seconds the advisor re-locks them. Shared with
    /// <see cref="ClashEngine.Orchestration.MatchOrchestrator"/> so the orchestrator's
    /// per-second tick can fire the matching player notice at the same instant.</summary>
    public static readonly TimeSpan ShipLockBeforeStart = TimeSpan.FromSeconds(5);

    private readonly IComponentBroker _broker;
    private readonly MatchmakingEngine _engine;
    private readonly IArenaManager _arenaManager;
    private readonly PlayerKeyResolver _resolver;
    private readonly IClock _clock;
    private readonly ILogManager _log;
    private readonly IChat _chat;
    private readonly MatchFreqAllocator? _freqAllocator;

    /// <summary>
    /// Per-match-participant lock state. Populated at <see cref="OnMatchStarted"/>, mutated by
    /// kill events to open the grace window, drained at <see cref="OnMatchEnded"/>.
    /// </summary>
    private readonly Dictionary<PlayerKey, LockState> _byPlayer = new();

    /// <summary>
    /// Captured at <see cref="OnMatchProposed"/> by walking active matches for the proposal's
    /// Teams reference. Read at <see cref="OnMatchStarted"/> to look up the queue's grace period
    /// (mirrors the pattern used by <see cref="ClashStatsTelemetry"/>).
    /// </summary>
    private readonly Dictionary<Guid, QueueDefinition> _queueByMatch = new();

    /// <summary>Advisor registration token per match arena, keyed by arena name (case-insensitive).</summary>
    private readonly Dictionary<string, AdvisorRegistration> _byArena =
        new(StringComparer.OrdinalIgnoreCase);

    private bool _registered;

    public MatchFreqAdvisor(
        IComponentBroker broker,
        MatchmakingEngine engine,
        IArenaManager arenaManager,
        PlayerKeyResolver resolver,
        IClock clock,
        ILogManager log,
        IChat chat,
        MatchFreqAllocator? freqAllocator = null)
    {
        _broker = broker ?? throw new ArgumentNullException(nameof(broker));
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _arenaManager = arenaManager ?? throw new ArgumentNullException(nameof(arenaManager));
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _chat = chat ?? throw new ArgumentNullException(nameof(chat));
        _freqAllocator = freqAllocator;
    }

    /// <summary>Subscribe to arena lifecycle so we can register the advisor on each match
    /// arena. Kill events are routed via <see cref="ClashEngine.Events.MatchKillRouter"/>.</summary>
    public void Register()
    {
        if (_registered) return;
        ArenaActionCallback.Register(_broker, OnArenaAction);

        // Register on any match arenas already loaded (PermanentArenas may have created them
        // before our module finished loading).
        foreach (var queue in _engine.Queues.Definitions)
        {
            if (string.IsNullOrEmpty(queue.MatchArenaName)) continue;
            var arena = _arenaManager.FindArena(queue.MatchArenaName);
            if (arena is not null) RegisterArena(arena);
        }
        _registered = true;
    }

    public void Unregister()
    {
        if (!_registered) return;
        ArenaActionCallback.Unregister(_broker, OnArenaAction);

        foreach (var (_, reg) in _byArena)
        {
            reg.Arena.UnregisterAdvisor(ref reg.Token);
        }
        _byArena.Clear();
        _byPlayer.Clear();
        _registered = false;
    }

    // --- IMatchmakingTelemetry: capture queue at proposal, register/drain at start/end ---

    public void OnMatchProposed(MatchProposal proposal)
    {
        if (!_engine.Queues.TryGet(proposal.QueueName, out var queueDef)) return;
        Guid matchId = Guid.Empty;
        foreach (var (id, match) in _engine.ActiveMatches)
        {
            if (ReferenceEquals(match.Teams, proposal.Teams))
            {
                matchId = id;
                _queueByMatch[id] = queueDef;
                break;
            }
        }
        if (matchId == Guid.Empty) return;

        // Populate locks at proposal time (not at OnMatchStarted) so freq enforcement is in
        // effect during Setup/Staging/Countdown, with ShipChangeAllowedUntil extended through
        // staging and most of the countdown -- the lock only kicks in ShipLockBeforeStart before
        // GO. Listener order in CompositeTelemetry guarantees MatchOrchestratorRegistry has
        // already run BeginSetup (allocating the freq base) by the time this listener fires.
        var lockOffset = queueDef.StagingDuration + queueDef.CountdownDuration - ShipLockBeforeStart;
        // QueueDefinition validates CountdownDuration >= 5s so this is non-negative in practice;
        // clamp defensively for any future relaxation of that bound.
        if (lockOffset < queueDef.StagingDuration) lockOffset = queueDef.StagingDuration;
        var shipChangeUntil = _clock.UtcNow + lockOffset;
        for (int t = 0; t < proposal.Teams.Count; t++)
        {
            short freq = _freqAllocator?.FreqOf(matchId, t) ?? QueueDefinition.FreqOf(t);
            for (int j = 0; j < proposal.Teams[t].Count; j++)
            {
                var key = proposal.Teams[t][j];
                _byPlayer[key] = new LockState(freq, queueDef.ShipChangeGracePeriod, shipChangeUntil);
            }
        }
    }

    public void OnMatchStarted(ActiveMatch match)
    {
        // Locks were populated at OnMatchProposed; by GO the pre-match
        // ShipChangeAllowedUntil has already passed (it expires ShipLockBeforeStart before GO),
        // so the per-life lock is in effect. Nothing to refresh here.
    }

    public void OnMatchEnded(MatchOutcome outcome)
    {
        // Drop every participant of the ending match. The engine clears ActiveMatches before
        // firing OnMatchEnded, so we walk what remains and remove the difference.
        var stillActive = new HashSet<PlayerKey>();
        foreach (var (_, m) in _engine.ActiveMatches)
        {
            for (int t = 0; t < m.Teams.Count; t++)
                for (int j = 0; j < m.Teams[t].Count; j++)
                    stillActive.Add(m.Teams[t][j]);
        }

        var toDrop = new List<PlayerKey>();
        foreach (var k in _byPlayer.Keys)
            if (!stillActive.Contains(k)) toDrop.Add(k);
        foreach (var k in toDrop) _byPlayer.Remove(k);

        _queueByMatch.Remove(outcome.MatchId);
    }

    // --- IFreqManagerEnforcerAdvisor ---

    ShipMask IFreqManagerEnforcerAdvisor.GetAllowableShips(Player player, ShipType ship, short freq, StringBuilder? errorMessage)
    {
        if (_resolver.KeyOf(player) is not PlayerKey key) return ShipMask.All;
        if (!_byPlayer.TryGetValue(key, out var st)) return ShipMask.All;

        if (_clock.UtcNow < st.ShipChangeAllowedUntil)
            return ShipMask.All;

        // Outside the post-death grace: re-lock to the player's current ship. Spec'ing isn't
        // gated by this advisor (ShipChange short-circuits to spec before consulting us).
        var currentMask = player.Ship.GetShipMask();
        if (currentMask == ShipMask.None)
        {
            // Player is in spec; this advisor isn't in a position to grant a ship. Returning All
            // would let the FreqManager pick any ship for an un-spec'ing match participant; we
            // explicitly forbid mid-match un-spec by returning None.
            errorMessage?.Append("Match participants cannot ship up while in spec.");
            return ShipMask.None;
        }
        if (ship != player.Ship)
            errorMessage?.Append($"You're locked to your current ship for the rest of the life.");
        return currentMask;
    }

    bool IFreqManagerEnforcerAdvisor.CanChangeToFreq(Player player, short newFreq, StringBuilder? errorMessage)
    {
        if (_resolver.KeyOf(player) is not PlayerKey key) return true;
        if (!_byPlayer.TryGetValue(key, out var st)) return true;
        if (newFreq == st.Freq) return true;
        errorMessage?.Append("You can't switch teams while in a match.");
        return false;
    }

    // --- private ---

    private void OnArenaAction(Arena arena, ArenaAction action)
    {
        if (action == ArenaAction.Create)
        {
            // Only register on arenas that are configured as a match arena for some queue.
            if (IsMatchArena(arena.Name))
                RegisterArena(arena);
        }
        else if (action == ArenaAction.Destroy)
        {
            if (_byArena.Remove(arena.Name, out var reg))
            {
                arena.UnregisterAdvisor(ref reg.Token);
            }
        }
    }

    private bool IsMatchArena(string arenaName)
    {
        foreach (var queue in _engine.Queues.Definitions)
        {
            if (string.IsNullOrEmpty(queue.MatchArenaName)) continue;
            if (string.Equals(queue.MatchArenaName, arenaName, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private void RegisterArena(Arena arena)
    {
        if (_byArena.ContainsKey(arena.Name)) return;
        var token = arena.RegisterAdvisor<IFreqManagerEnforcerAdvisor>(this);
        _byArena[arena.Name] = new AdvisorRegistration(arena, token);
    }

    /// <summary>
    /// Called by <see cref="ClashEngine.Events.MatchKillRouter"/> after the engine has decremented
    /// <c>LivesRemaining</c> for the victim. Opens the per-life ship-change grace window when the
    /// kill was non-fatal (the player is going to respawn in their existing ship) so they have a
    /// brief moment to swap ships before being re-locked.
    /// </summary>
    public void OnKill(Arena arena, Player killer, Player killed, short bounty, short flagCount, short points, Prize green)
    {
        if (_resolver.KeyOf(killed) is not PlayerKey victim) return;
        if (!_byPlayer.TryGetValue(victim, out var st)) return;

        // Don't open the grace window for a knockout (last life) -- the orchestrator's deferred
        // spec handles that path; opening the window here would let the player ship-swap after
        // their final death (during the residual-weapons window) which we don't want.
        if (IsKnockout(victim)) return;

        if (st.GracePeriod <= TimeSpan.Zero) return;
        var until = _clock.UtcNow + st.GracePeriod;
        _byPlayer[victim] = st with { ShipChangeAllowedUntil = until };

        // Round up so a 9.x-second grace renders as "10s" rather than "9s" (the player's
        // wall-clock window is up to GracePeriod long).
        int seconds = (int)Math.Ceiling(st.GracePeriod.TotalSeconds);
        _chat.SendMessage(killed,
            $"You have {seconds}s to change ships before being locked back to your current ship.");
    }

    private bool IsKnockout(PlayerKey victim)
    {
        foreach (var (_, match) in _engine.ActiveMatches)
        {
            if (match.LivesPerPlayer is null) continue;
            if (match.LivesRemaining.ContainsKey(victim))
                return match.ExitedAt.ContainsKey(victim);
        }
        return false;
    }

    private readonly record struct LockState(
        short Freq,
        TimeSpan GracePeriod,
        DateTimeOffset ShipChangeAllowedUntil);

    private sealed class AdvisorRegistration
    {
        public AdvisorRegistration(Arena arena, AdvisorRegistrationToken<IFreqManagerEnforcerAdvisor>? token)
        {
            Arena = arena;
            Token = token;
        }
        public Arena Arena { get; }
        public AdvisorRegistrationToken<IFreqManagerEnforcerAdvisor>? Token;
    }
}
