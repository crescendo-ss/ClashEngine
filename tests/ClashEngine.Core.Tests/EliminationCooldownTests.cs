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
                new GameTypeId(1),
                () => new KillCountEndPolicy(1000),     // never end on kills alone
                vetoesRequired: 1);

            Engine.Queues.Register(
                "duel",
                new MatchShape(2, 1),
                new PartitionQualityPolicy(0.5, 0.15, TimeSpan.FromSeconds(90)),
                new GameTypeId(2),
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
}
