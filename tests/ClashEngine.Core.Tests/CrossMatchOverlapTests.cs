using ClashEngine.Core;
using ClashEngine.Core.Identity;
using ClashEngine.Core.Matches;
using ClashEngine.Core.Matching;
using ClashEngine.Core.Penalties;
using ClashEngine.Core.Ratings;
using ClashEngine.Core.Tests.Fakes;

namespace ClashEngine.Core.Tests;

/// <summary>
/// A player eliminated from a lives-mode match is released from the engine's player-&gt;match
/// index (see <see cref="MatchmakingEngine.OnKill"/>) so they can re-queue while the match they
/// were rostered into is still running. They stay on that match's Teams list for the end-of-match
/// rating update, which means the old match's teardown walks a roster containing a player who is,
/// by then, playing somewhere else. These tests pin that teardown to the players it still owns.
/// </summary>
public class CrossMatchOverlapTests
{
    private static PlayerKey K(string n) => new(n);
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private sealed class Harness
    {
        public FakeClock Clock { get; } = new(T0);
        public RecordingTelemetry Telemetry { get; } = new();
        public MatchmakingEngine Engine { get; }

        public Harness()
        {
            Engine = new MatchmakingEngine(
                new InMemoryRatingStore(), Clock,
                new[]
                {
                    PenaltyPolicy.DefaultAbandonment,
                    PenaltyPolicy.DefaultGriefing,
                    PenaltyPolicy.DefaultEliminationCooldown,
                },
                quality: new OrdinalSpreadQuality(),
                telemetry: Telemetry,
                joinTimeout: TimeSpan.FromMinutes(1),
                graceWindow: TimeSpan.FromSeconds(30));

            // Lives = 1 so a single cross-team kill eliminates (and releases) the victim, and
            // EliminationCooldown = 0 so they can re-queue immediately -- the cooldown is an
            // orthogonal knob and a live one would just make the test sleep.
            Engine.Queues.Register(
                "2v2-lives",
                new MatchShape(2, 2),
                new PartitionQualityPolicy(0.5, 0.15, TimeSpan.FromSeconds(90)),
                "gt-overlap",
                () => new KillCountEndPolicy(1000),     // never end on kills alone
                vetoesRequired: 1,
                holdWindow: TimeSpan.Zero,
                livesPerPlayer: 1,
                eliminationCooldown: TimeSpan.Zero);
        }

        /// <summary>Queues the four names, pops a match, and drives it to Live.</summary>
        public ActiveMatch StartMatch(params string[] names)
        {
            foreach (var n in names) Engine.OnPlayerConnected(K(n), Clock.UtcNow);
            foreach (var n in names) Engine.TryEnqueue(K(n), "2v2-lives", Clock.UtcNow);

            var before = new HashSet<Guid>(Engine.ActiveMatches.Keys);
            Engine.Tick(Clock.UtcNow);
            var match = Engine.ActiveMatches.Single(kv => !before.Contains(kv.Key)).Value;

            Clock.Advance(TimeSpan.FromSeconds(2));
            foreach (var n in names) Engine.OnPlayerJoinedArena(K(n), Clock.UtcNow);
            Assert.True(Engine.MarkMatchLive(match.MatchId, Clock.UtcNow));
            return match;
        }

        /// <summary>An opponent of <paramref name="player"/> in <paramref name="match"/>.</summary>
        public static PlayerKey EnemyOf(ActiveMatch match, PlayerKey player)
        {
            int team = match.TeamIndexOf(player)!.Value;
            return match.Teams[team == 0 ? 1 : 0][0];
        }
    }

    /// <summary>
    /// Sets up the overlap: match A goes Live, <c>victim</c> is eliminated out of it and
    /// immediately re-queues into match B, which also goes Live while A is still running.
    /// </summary>
    private static (Harness H, ActiveMatch A, ActiveMatch B, PlayerKey Victim) TwoOverlappingMatches()
    {
        var h = new Harness();
        var a = h.StartMatch("A1", "A2", "A3", "A4");

        // Eliminate one player out of match A. Their teammate survives, so A stays Live.
        var victim = a.Teams[0][0];
        var killer = Harness.EnemyOf(a, victim);
        h.Clock.Advance(TimeSpan.FromSeconds(1));
        h.Engine.OnKill(killer, victim, h.Clock.UtcNow);
        Assert.Equal(MatchState.Live, a.State);
        Assert.True(a.IsKnockedOut(victim));
        Assert.False(h.Engine.IsInActiveMatch(victim));      // released -- free to re-queue

        // ?play again: the victim forms match B with three fresh players while A runs on.
        var b = h.StartMatch(victim.Name, "B2", "B3", "B4");
        Assert.NotEqual(a.MatchId, b.MatchId);
        Assert.Equal(b.MatchId, h.Engine.MatchIdOf(victim));
        return (h, a, b, victim);
    }

    /// <summary>Ends match A by eliminating the rest of the team the victim was on.</summary>
    private static void FinishMatchA(Harness h, ActiveMatch a, PlayerKey victim)
    {
        var survivor = a.Teams[a.TeamIndexOf(victim)!.Value][1];
        var killer = Harness.EnemyOf(a, survivor);
        h.Clock.Advance(TimeSpan.FromSeconds(1));
        h.Engine.OnKill(killer, survivor, h.Clock.UtcNow);
        Assert.Equal(MatchState.Completed, a.Outcome!.FinalState);
        Assert.DoesNotContain(a.MatchId, h.Engine.ActiveMatches.Keys);
    }

    [Fact]
    public void Finalizing_old_match_keeps_the_index_of_a_player_now_in_a_newer_match()
    {
        var (h, a, b, victim) = TwoOverlappingMatches();

        FinishMatchA(h, a, victim);

        // The victim is on A's roster, but their index entry points at the live match B.
        // FinalizeMatch used to remove it unconditionally.
        Assert.Equal(b.MatchId, h.Engine.MatchIdOf(victim));
        Assert.True(h.Engine.IsInActiveMatch(victim));
    }

    [Fact]
    public void Player_in_a_newer_match_can_still_be_killed_after_the_old_match_ends()
    {
        // The "infinite lives" report: with the index entry gone, engine.OnKill anchored on
        // _matchOf and early-returned for every death, so the victim's lives never decremented,
        // ExitedAt never got set, the orchestrator's knockout-spec never fired, and a match that
        // ends on eliminations could never end.
        var (h, a, b, victim) = TwoOverlappingMatches();

        FinishMatchA(h, a, victim);

        var enemyInB = Harness.EnemyOf(b, victim);
        h.Clock.Advance(TimeSpan.FromSeconds(1));
        h.Engine.OnKill(enemyInB, victim, h.Clock.UtcNow);

        Assert.True(b.IsKnockedOut(victim));
        Assert.Contains(victim, b.ExitedAt.Keys);
    }

    [Fact]
    public void Finalizing_old_match_does_not_auto_requeue_a_player_who_is_in_a_live_match()
    {
        var (h, a, b, victim) = TwoOverlappingMatches();
        h.Engine.AutoQueue.Set(victim, true);

        FinishMatchA(h, a, victim);

        // ApplyAutoQueueReenqueue skips players already pulled into another match. That guard
        // reads IsInActiveMatch, which the unconditional index removal above it defeated --
        // putting a player who was mid-fight in B back in line for a third match.
        Assert.Empty(h.Engine.QueuesFor(victim));
    }

    [Fact]
    public void Finalizing_old_match_still_clears_the_index_of_its_own_players()
    {
        // The guard must not leak entries for the players the match really did own.
        var (h, a, b, victim) = TwoOverlappingMatches();
        var stillInA = a.Teams[a.TeamIndexOf(victim)!.Value][1];

        FinishMatchA(h, a, victim);

        Assert.False(h.Engine.IsInActiveMatch(stillInA));
        Assert.Null(h.Engine.MatchIdOf(stillInA));
        foreach (var p in a.Teams[Harness.EnemyOf(a, victim).Equals(a.Teams[0][0]) ? 0 : 1])
            Assert.False(h.Engine.IsInActiveMatch(p));
    }

    [Fact]
    public void Reset_of_an_in_match_player_announces_the_release()
    {
        // ResetPlayer drops the index entry so the target can re-queue immediately. Without the
        // matching telemetry the stats registry keeps holding them against the old match, and
        // their next match start throws "already in match".
        var h = new Harness();
        var match = h.StartMatch("A1", "A2", "A3", "A4");
        var target = match.Teams[0][0];

        h.Clock.Advance(TimeSpan.FromSeconds(1));
        h.Engine.ResetPlayer(target, h.Clock.UtcNow, keepRating: false);

        Assert.False(h.Engine.IsInActiveMatch(target));
        Assert.Contains(h.Telemetry.PlayerReleases, r => r.Player.Equals(target) && r.MatchId == match.MatchId);
    }

    [Fact]
    public void Reset_of_a_player_outside_a_match_announces_nothing()
    {
        var h = new Harness();
        h.Engine.OnPlayerConnected(K("Solo"), h.Clock.UtcNow);

        h.Engine.ResetPlayer(K("Solo"), h.Clock.UtcNow, keepRating: false);

        Assert.Empty(h.Telemetry.PlayerReleases);
    }
}
