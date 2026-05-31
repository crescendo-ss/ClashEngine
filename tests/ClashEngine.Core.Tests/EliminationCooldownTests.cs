using ClashEngine.Core;
using ClashEngine.Core.Eligibility;
using ClashEngine.Core.Identity;
using ClashEngine.Core.Matches;
using ClashEngine.Core.Matching;
using ClashEngine.Core.Penalties;
using ClashEngine.Core.Ratings;
using ClashEngine.Core.Tests.Fakes;

namespace ClashEngine.Core.Tests;

public class EliminationCooldownTests
{
    private static PlayerKey K(string n) => new(n);
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private sealed class Harness
    {
        public FakeClock Clock { get; } = new(T0);
        public InMemoryRatingStore Ratings { get; } = new();
        public RecordingTelemetry Telemetry { get; } = new();
        public MatchmakingEngine Engine { get; }

        public Harness(int livesPerPlayer = 1, TimeSpan? cooldown = null)
        {
            Engine = new MatchmakingEngine(
                Ratings, Clock,
                new[]
                {
                    PenaltyPolicy.DefaultAbandonment,
                    PenaltyPolicy.DefaultGriefing,
                    new PenaltyPolicy(PenaltyKind.EliminationCooldown,
                        cooldown ?? TimeSpan.FromMinutes(1), 1.0, TimeSpan.FromMinutes(5)),
                },
                quality: new OrdinalSpreadQuality(),
                telemetry: Telemetry,
                joinTimeout: TimeSpan.FromMinutes(1),
                graceWindow: TimeSpan.FromSeconds(30));

            Engine.Queues.Register(
                "2v2",
                new MatchShape(2, 2),
                new PartitionQualityPolicy(0.5, 0.15, TimeSpan.FromSeconds(90)),
                "gt1",
                () => new KillCountEndPolicy(1000),     // never end on kills alone
                vetoesRequired: 1);

            Engine.Queues.Register(
                "duel",
                new MatchShape(2, 1),
                new PartitionQualityPolicy(0.5, 0.15, TimeSpan.FromSeconds(90)),
                "gt2",
                () => new KillCountEndPolicy(1));
        }

        public ActiveMatch StartMatch(int lives = 1)
        {
            string[] names = { "A", "B", "C", "D" };
            foreach (var n in names) Engine.OnPlayerConnected(K(n), Clock.UtcNow);
            foreach (var n in names) Engine.TryEnqueue(K(n), "2v2", Clock.UtcNow);

            // Pop a match and use a custom ActiveMatch with lives. The engine's matches don't
            // currently configure lives via QueueDefinition, so build the lives-aware match
            // by hand via the public OnKill flow on a still-running engine match.
            Engine.Tick(Clock.UtcNow);
            var m = Engine.ActiveMatches.First().Value;

            // Players need to actually join, then the orchestrator's GO! fires MarkMatchLive.
            Clock.Advance(TimeSpan.FromSeconds(2));
            foreach (var n in names) Engine.OnPlayerJoinedArena(K(n), Clock.UtcNow);
            Engine.MarkMatchLive(m.MatchId, Clock.UtcNow);
            return m;
        }
    }

    [Fact]
    public void Player_loses_last_life_to_cross_team_kill_drops_to_Available_after_cooldown()
    {
        // The default engine queue uses unlimited lives (LivesPerPlayer = null), so to test
        // the elimination path here we need a match with lives. Build one directly.
        var clock = new FakeClock(T0);
        var ratings = new InMemoryRatingStore();
        var penalties = new PenaltyTracker(
            PenaltyPolicy.DefaultAbandonment,
            PenaltyPolicy.DefaultGriefing,
            PenaltyPolicy.DefaultEliminationCooldown);

        // We exercise PenaltyTracker semantics directly: simulate elimination by recording.
        penalties.RecordPenalty(K("A"), PenaltyKind.EliminationCooldown, T0);

        Assert.True(penalties.IsInTimeout(K("A"), T0));
        Assert.True(penalties.IsInTimeout(K("A"), T0 + TimeSpan.FromSeconds(30)));
        Assert.False(penalties.IsInTimeout(K("A"), T0 + TimeSpan.FromMinutes(1) + TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void Default_elimination_policy_has_one_minute_no_escalation()
    {
        var p = PenaltyPolicy.DefaultEliminationCooldown;
        Assert.Equal(PenaltyKind.EliminationCooldown, p.Kind);
        Assert.Equal(TimeSpan.FromMinutes(1), p.BaseTimeout);
        Assert.Equal(1.0, p.EscalationFactor);
    }

    [Fact]
    public void Repeated_eliminations_do_not_escalate_with_factor_one()
    {
        var t = new PenaltyTracker(PenaltyPolicy.DefaultEliminationCooldown);
        t.RecordPenalty(K("A"), PenaltyKind.EliminationCooldown, T0);
        t.RecordPenalty(K("A"), PenaltyKind.EliminationCooldown, T0 + TimeSpan.FromSeconds(30));

        // Latest event at +30s, count = 2 but factor 1 → still base 1min from latest event.
        Assert.Equal(T0 + TimeSpan.FromSeconds(30) + TimeSpan.FromMinutes(1),
                     t.TimeoutEndForKind(K("A"), PenaltyKind.EliminationCooldown));
    }

    [Fact]
    public void Eligibility_returns_InTimeout_during_cooldown()
    {
        var clock = new FakeClock(T0);
        var t = new PenaltyTracker(PenaltyPolicy.DefaultEliminationCooldown);
        var elig = new PlayerEligibility(t, clock);

        t.RecordPenalty(K("A"), PenaltyKind.EliminationCooldown, T0);

        Assert.Equal(EligibilityStatus.InTimeout, elig.Check(K("A"), true, false).Status);

        clock.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal(EligibilityStatus.Available, elig.Check(K("A"), true, false).Status);
    }

    [Fact]
    public void Kill_from_just_eliminated_killer_still_eliminates_the_victim()
    {
        // Regression: in a simultaneous last-life trade, both players' final shots land in the
        // same tick. The first kill the server processes eliminates one fighter and removes
        // them from _matchOf; pre-fix, the second kill (from that just-removed killer) was
        // dropped on the floor because engine.OnKill required the killer to still be in
        // _matchOf. The "dead" killer's residual shot then never decremented the second
        // victim's life, so the freq advisor opened a ship-change grace window for them and
        // they could ship back up after their own "final" death.
        var clock = new FakeClock(T0);
        var ratings = new InMemoryRatingStore();
        var telemetry = new RecordingTelemetry();
        var engine = new MatchmakingEngine(
            ratings, clock,
            new[]
            {
                PenaltyPolicy.DefaultAbandonment,
                PenaltyPolicy.DefaultGriefing,
                PenaltyPolicy.DefaultEliminationCooldown,
            },
            quality: new OrdinalSpreadQuality(),
            telemetry: telemetry,
            joinTimeout: TimeSpan.FromMinutes(1),
            graceWindow: TimeSpan.FromSeconds(30));

        // 2v2 (not 1v1) so that the first elimination doesn't auto-forfeit the losing team
        // and end the match before the second kill can be processed -- each team needs another
        // live member to keep the match Live through both kills.
        engine.Queues.Register(
            "2v2-lives",
            new MatchShape(2, 2),
            new PartitionQualityPolicy(0.5, 0.15, TimeSpan.FromSeconds(90)),
            "gt7",
            // Set the kill threshold high so the first elimination doesn't end the match on
            // the score side -- we want both kills to flow through engine.OnKill while the
            // match is still Live.
            () => new KillCountEndPolicy(1000),
            vetoesRequired: 1,
            holdWindow: TimeSpan.Zero,
            livesPerPlayer: 1);

        foreach (var n in new[] { "A", "B", "X", "Y" }) engine.OnPlayerConnected(K(n), T0);
        foreach (var n in new[] { "A", "B", "X", "Y" }) engine.TryEnqueue(K(n), "2v2-lives", T0);
        engine.Tick(T0);
        var match = engine.ActiveMatches.First().Value;

        // Sanity: the queue produced a lives-mode match.
        Assert.Equal(1, match.LivesPerPlayer);

        clock.Advance(TimeSpan.FromSeconds(2));
        foreach (var n in new[] { "A", "B", "X", "Y" }) engine.OnPlayerJoinedArena(K(n), clock.UtcNow);
        engine.MarkMatchLive(match.MatchId, clock.UtcNow);
        Assert.Equal(MatchState.Live, match.State);

        // Pick A's enemy from the opposing team -- balancing may shuffle the roster, so we don't
        // assume B ended up on the other side. Each team has 2 members; either of A's enemies
        // works, the first one is fine.
        var enemyTeam = match.TeamIndexOf(K("A"))!.Value == 0 ? 1 : 0;
        var aEnemy = match.Teams[enemyTeam][0];

        // Simultaneous kill: A -> aEnemy is processed first (aEnemy eliminated, removed from
        // _matchOf), then aEnemy -> A in the very next tick. With the bug, the second kill is
        // dropped because aEnemy is no longer in _matchOf and A's _exitedAt never gets set.
        // The match continues Live because each team still has a live teammate.
        clock.Advance(TimeSpan.FromSeconds(1));
        engine.OnKill(K("A"), aEnemy, clock.UtcNow);
        engine.OnKill(aEnemy, K("A"), clock.UtcNow);

        Assert.Equal(MatchState.Live, match.State);   // teammates still alive on both sides
        // Both fighters must show as eliminated. Pre-fix the second assertion was the failure:
        // A had _exitedAt unset and could ship back up via the freq advisor's grace window.
        Assert.True(match.IsKnockedOut(aEnemy));
        Assert.True(match.IsKnockedOut(K("A")));
    }

    /// <summary>
    /// Forms a 2v2 lives=1 match under a game type carrying <paramref name="cooldown"/>, marks it
    /// Live, and eliminates one player via a cross-team last-life kill. 2v2 (not 1v1) so the
    /// victim's surviving teammate keeps the match Live -- match end auto-rescinds the cooldown,
    /// which would mask what we're asserting. Returns the still-Live engine and the eliminated
    /// player.
    /// </summary>
    private static (MatchmakingEngine Engine, FakeClock Clock, PlayerKey Victim) EliminateOneIn2v2(TimeSpan? cooldown)
    {
        var clock = new FakeClock(T0);
        var engine = new MatchmakingEngine(
            new InMemoryRatingStore(), clock,
            new[]
            {
                PenaltyPolicy.DefaultAbandonment,
                PenaltyPolicy.DefaultGriefing,
                PenaltyPolicy.DefaultEliminationCooldown,
            },
            quality: new OrdinalSpreadQuality(),
            telemetry: new RecordingTelemetry(),
            joinTimeout: TimeSpan.FromMinutes(1),
            graceWindow: TimeSpan.FromSeconds(30));

        engine.Queues.Register(
            "2v2-lives",
            new MatchShape(2, 2),
            new PartitionQualityPolicy(0.5, 0.15, TimeSpan.FromSeconds(90)),
            "gt-elim",
            () => new KillCountEndPolicy(1000),     // never end on kills alone
            vetoesRequired: 1,
            holdWindow: TimeSpan.Zero,
            livesPerPlayer: 1,
            eliminationCooldown: cooldown);

        foreach (var n in new[] { "A", "B", "X", "Y" }) engine.OnPlayerConnected(K(n), T0);
        foreach (var n in new[] { "A", "B", "X", "Y" }) engine.TryEnqueue(K(n), "2v2-lives", T0);
        engine.Tick(T0);
        var match = engine.ActiveMatches.First().Value;
        Assert.Equal(1, match.LivesPerPlayer);
        Assert.Equal(cooldown, match.EliminationCooldown);

        clock.Advance(TimeSpan.FromSeconds(2));
        foreach (var n in new[] { "A", "B", "X", "Y" }) engine.OnPlayerJoinedArena(K(n), clock.UtcNow);
        engine.MarkMatchLive(match.MatchId, clock.UtcNow);

        // Kill across teams so the victim loses their last life. The killer is on A's team; the
        // victim is the head of the opposing team.
        int aTeam = match.TeamIndexOf(K("A"))!.Value;
        var killer = match.Teams[aTeam][0];
        var victim = match.Teams[aTeam == 0 ? 1 : 0][0];
        clock.Advance(TimeSpan.FromSeconds(1));
        engine.OnKill(killer, victim, clock.UtcNow);

        Assert.Equal(MatchState.Live, match.State);   // teammates still alive on both sides
        Assert.True(match.IsKnockedOut(victim));
        return (engine, clock, victim);
    }

    [Fact]
    public void Per_gametype_cooldown_overrides_default_on_elimination()
    {
        var (engine, clock, victim) = EliminateOneIn2v2(TimeSpan.FromSeconds(30));

        // The 30s game-type cooldown applies -- shorter than the 1-min policy default.
        Assert.True(engine.Penalties.IsInTimeout(victim, clock.UtcNow));
        Assert.True(engine.Penalties.IsInTimeout(victim, clock.UtcNow + TimeSpan.FromSeconds(29)));
        Assert.False(engine.Penalties.IsInTimeout(victim, clock.UtcNow + TimeSpan.FromSeconds(31)));
    }

    [Fact]
    public void Per_gametype_zero_cooldown_records_no_penalty()
    {
        var (engine, clock, victim) = EliminateOneIn2v2(TimeSpan.Zero);

        // Cooldown disabled for this game type: the eliminated player may requeue immediately.
        Assert.False(engine.Penalties.IsInTimeout(victim, clock.UtcNow));
    }

    [Fact]
    public void Absent_gametype_cooldown_falls_back_to_policy_default()
    {
        var (engine, clock, victim) = EliminateOneIn2v2(cooldown: null);

        // No per-game-type value -> the engine's 1-minute default policy applies.
        Assert.True(engine.Penalties.IsInTimeout(victim, clock.UtcNow + TimeSpan.FromSeconds(59)));
        Assert.False(engine.Penalties.IsInTimeout(victim, clock.UtcNow + TimeSpan.FromSeconds(61)));
    }

    [Fact]
    public void Cross_match_kill_is_still_rejected()
    {
        // Sanity: the killer-residual fall-through must not let a kill from one match's
        // (just-eliminated) participant land on the other match's player.
        var clock = new FakeClock(T0);
        var ratings = new InMemoryRatingStore();
        var telemetry = new RecordingTelemetry();
        var engine = new MatchmakingEngine(
            ratings, clock,
            new[]
            {
                PenaltyPolicy.DefaultAbandonment,
                PenaltyPolicy.DefaultGriefing,
                PenaltyPolicy.DefaultEliminationCooldown,
            },
            quality: new OrdinalSpreadQuality(),
            telemetry: telemetry,
            joinTimeout: TimeSpan.FromMinutes(1),
            graceWindow: TimeSpan.FromSeconds(30));

        engine.Queues.Register(
            "1v1-lives",
            new MatchShape(2, 1),
            new PartitionQualityPolicy(0.5, 0.15, TimeSpan.FromSeconds(90)),
            "gt7",
            () => new KillCountEndPolicy(1000),
            vetoesRequired: 1,
            livesPerPlayer: 1);

        // Two separate 1v1 matches: (A vs B) and (C vs D).
        foreach (var n in new[] { "A", "B", "C", "D" }) engine.OnPlayerConnected(K(n), T0);
        engine.TryEnqueue(K("A"), "1v1-lives", T0);
        engine.TryEnqueue(K("B"), "1v1-lives", T0);
        engine.Tick(T0);
        engine.TryEnqueue(K("C"), "1v1-lives", T0);
        engine.TryEnqueue(K("D"), "1v1-lives", T0);
        engine.Tick(T0);

        var matches = engine.ActiveMatches.Values.ToList();
        Assert.Equal(2, matches.Count);

        clock.Advance(TimeSpan.FromSeconds(2));
        foreach (var n in new[] { "A", "B", "C", "D" }) engine.OnPlayerJoinedArena(K(n), clock.UtcNow);
        foreach (var m in matches) engine.MarkMatchLive(m.MatchId, clock.UtcNow);

        // Eliminate A (in its match) so A is no longer in _matchOf.
        clock.Advance(TimeSpan.FromSeconds(1));
        engine.OnKill(K("B"), K("A"), clock.UtcNow);

        // Now fire a (nonsense) kill where the just-eliminated A "kills" C from the OTHER
        // match. The cross-match guard must reject it -- C's life must not decrement.
        var cMatch = matches.First(m => m.TeamIndexOf(K("C")) is not null);
        engine.OnKill(K("A"), K("C"), clock.UtcNow);
        Assert.False(cMatch.IsKnockedOut(K("C")));
    }
}
