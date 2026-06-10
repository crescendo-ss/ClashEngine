using ClashEngine.Core;
using ClashEngine.Core.Identity;
using ClashEngine.Core.Matches;
using ClashEngine.Core.Matching;
using ClashEngine.Core.Penalties;
using ClashEngine.Core.Queue;
using ClashEngine.Core.Ratings;
using ClashEngine.Core.Tests.Fakes;

namespace ClashEngine.Core.Tests.Matches;

/// <summary>
/// The presence-zone ("stay in the zone or lose") end condition: a Live match's team that has no
/// Active member sampled inside the game type's zone box for PresenceZoneTimeout forfeits.
/// </summary>
public class ZonePresenceTests
{
    private static PlayerKey K(string n) => new(n);
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>Zone box: center (500,500), radius 10 tiles -> contains 490..510 on both axes.</summary>
    private static readonly SpawnArea Zone = new(new StartPoint(500, 500), 10);

    private static ActiveMatch BuildAndStart(
        SpawnArea? zone = null,
        TimeSpan? zoneTimeout = null,
        int? livesPerPlayer = null,
        TimeSpan? teamCollapseGrace = null)
    {
        var m = new ActiveMatch(
            Guid.NewGuid(), "gt1",
            new IReadOnlyList<PlayerKey>[]
            {
                new[] { K("A"), K("B") },
                new[] { K("C"), K("D") },
            },
            new KillCountEndPolicy(1000),
            joinTimeout: TimeSpan.FromMinutes(1),
            graceWindow: TimeSpan.FromMinutes(1),
            proposedAt: T0,
            livesPerPlayer: livesPerPlayer,
            teamCollapseGrace: teamCollapseGrace ?? TimeSpan.FromSeconds(10),
            presenceZone: zone ?? Zone,
            presenceZoneTimeout: zoneTimeout ?? TimeSpan.FromSeconds(30));

        foreach (var team in m.Teams)
            foreach (var p in team)
                m.OnPlayerJoined(p, T0.AddSeconds(1));
        m.MarkLive(T0.AddSeconds(1));   // presence clocks seeded here
        return m;
    }

    /// <summary>Both members of team 0 report from inside the zone at <paramref name="at"/>.</summary>
    private static void Team0Inside(ActiveMatch m, DateTimeOffset at)
    {
        m.OnPositionSample(K("A"), 500, 500, at);
        m.OnPositionSample(K("B"), 505, 495, at);
    }

    [Fact]
    public void Team_that_never_enters_the_zone_forfeits_at_timeout_and_is_ranked_last()
    {
        var m = BuildAndStart(zoneTimeout: TimeSpan.FromSeconds(30));

        // Team 0 holds the zone; team 1 reports only from outside it.
        for (int s = 5; s <= 30; s += 5)
        {
            Team0Inside(m, T0.AddSeconds(s));
            m.OnPositionSample(K("C"), 100, 100, T0.AddSeconds(s));
            m.Tick(T0.AddSeconds(s));
        }
        Assert.Equal(MatchState.Live, m.State);

        // 31s+ after GO (T0+1s) with no inside sample from team 1: forfeit.
        m.Tick(T0.AddSeconds(33));
        Assert.Equal(MatchState.Completed, m.State);
        Assert.Equal(MatchOutcomeReason.ZoneForfeit, m.Outcome!.EndReason);
        Assert.Contains(K("A"), m.Outcome.RankedTeams[0].Players);
        Assert.Contains(K("C"), m.Outcome.RankedTeams[^1].Players);
    }

    [Fact]
    public void One_member_inside_is_enough_to_hold_the_zone()
    {
        var m = BuildAndStart(zoneTimeout: TimeSpan.FromSeconds(30));

        // Only C (one of team 1's two players) ever sits in the zone; D roams outside.
        for (int s = 10; s <= 120; s += 10)
        {
            Team0Inside(m, T0.AddSeconds(s));
            m.OnPositionSample(K("C"), 510, 510, T0.AddSeconds(s));   // boundary tile counts
            m.OnPositionSample(K("D"), 50, 900, T0.AddSeconds(s));
            m.Tick(T0.AddSeconds(s));
        }

        Assert.Equal(MatchState.Live, m.State);
    }

    [Fact]
    public void Returning_to_the_zone_before_the_timeout_resets_the_clock()
    {
        var m = BuildAndStart(zoneTimeout: TimeSpan.FromSeconds(30));

        Team0Inside(m, T0.AddSeconds(5));
        m.OnPositionSample(K("C"), 500, 500, T0.AddSeconds(5));

        // Team 1 absent for 25s (within the 30s timeout), then re-enters.
        Team0Inside(m, T0.AddSeconds(28));
        m.Tick(T0.AddSeconds(28));
        Assert.Equal(MatchState.Live, m.State);
        Assert.True(m.ZoneVacantSince.ContainsKey(1));   // vacancy flagged for the warning

        m.OnPositionSample(K("D"), 495, 505, T0.AddSeconds(29));
        Assert.False(m.ZoneVacantSince.ContainsKey(1));  // reclaim cleared it

        // Old deadline (5s presence + 30s = 35s) passes harmlessly; clock now runs from 29s.
        Team0Inside(m, T0.AddSeconds(40));
        m.Tick(T0.AddSeconds(40));
        Assert.Equal(MatchState.Live, m.State);

        // But the new deadline (29s + 30s) still fires.
        Team0Inside(m, T0.AddSeconds(58));
        m.Tick(T0.AddSeconds(60));
        Assert.Equal(MatchState.Completed, m.State);
        Assert.Equal(MatchOutcomeReason.ZoneForfeit, m.Outcome!.EndReason);
    }

    [Fact]
    public void Samples_outside_the_box_do_not_refresh_presence()
    {
        var m = BuildAndStart(zoneTimeout: TimeSpan.FromSeconds(30));

        // One tile past the radius on one axis -> outside.
        for (int s = 5; s <= 35; s += 5)
        {
            Team0Inside(m, T0.AddSeconds(s));
            m.OnPositionSample(K("C"), 511, 500, T0.AddSeconds(s));
            m.Tick(T0.AddSeconds(s));
        }

        Assert.Equal(MatchState.Completed, m.State);
        Assert.Equal(MatchOutcomeReason.ZoneForfeit, m.Outcome!.EndReason);
        Assert.Contains(K("C"), m.Outcome.RankedTeams[^1].Players);
    }

    [Fact]
    public void Samples_from_in_grace_and_knocked_out_players_do_not_count()
    {
        var m = BuildAndStart(zoneTimeout: TimeSpan.FromSeconds(30), livesPerPlayer: 1);

        // C self-specs (InGrace); D dies on his only life (knocked out, exits the roster).
        m.OnPlayerLeft(K("C"), T0.AddSeconds(2));
        m.OnKill(K("A"), K("D"), T0.AddSeconds(3));

        // Both keep "reporting" from inside the zone (e.g. spectator cam parked on it) -- ignored.
        for (int s = 5; s <= 35; s += 5)
        {
            Team0Inside(m, T0.AddSeconds(s));
            m.OnPositionSample(K("C"), 500, 500, T0.AddSeconds(s));
            m.OnPositionSample(K("D"), 500, 500, T0.AddSeconds(s));
        }

        // Their team never forfeits the ZONE, though -- with no live member it belongs to the
        // collapse machinery (D is out of lives; C's grace is still running at the 10s collapse
        // mark, so collapse-grace forfeit fires instead).
        m.Tick(T0.AddSeconds(14));
        Assert.Equal(MatchState.Completed, m.State);
        Assert.Equal(MatchOutcomeReason.Standard, m.Outcome!.EndReason);
    }

    [Fact]
    public void Collapsed_team_is_not_zone_forfeited_while_collapse_grace_runs()
    {
        // Zone timeout (5s) much shorter than collapse grace (60s): a fully-disconnected team must
        // still get the full collapse grace, not a quick zone forfeit on stale presence data.
        var m = BuildAndStart(
            zoneTimeout: TimeSpan.FromSeconds(5),
            teamCollapseGrace: TimeSpan.FromSeconds(60));

        m.OnPlayerLeft(K("C"), T0.AddSeconds(2));
        m.OnPlayerLeft(K("D"), T0.AddSeconds(2));

        for (int s = 4; s <= 40; s += 4)
        {
            Team0Inside(m, T0.AddSeconds(s));
            m.Tick(T0.AddSeconds(s));
        }
        Assert.Equal(MatchState.Live, m.State);   // no zone forfeit at 5s; collapse grace still running

        // C returns -- and must get a fresh zone clock, not be forfeited on the spot.
        m.OnPlayerReturned(K("C"), T0.AddSeconds(42));
        Team0Inside(m, T0.AddSeconds(44));
        m.Tick(T0.AddSeconds(44));
        Assert.Equal(MatchState.Live, m.State);

        // But the fresh clock still runs: 5s+ with C outside the zone forfeits.
        Team0Inside(m, T0.AddSeconds(48));
        m.Tick(T0.AddSeconds(50));
        Assert.Equal(MatchState.Completed, m.State);
        Assert.Equal(MatchOutcomeReason.ZoneForfeit, m.Outcome!.EndReason);
    }

    [Fact]
    public void Both_teams_abandoning_the_zone_ends_the_match_in_a_draw()
    {
        var m = BuildAndStart(zoneTimeout: TimeSpan.FromSeconds(30));

        // Nobody ever enters the zone: no team beat the other, so it's a draw -- Completed
        // with every team sharing rank 1 (a tie for rating purposes), not Abandoned.
        m.Tick(T0.AddSeconds(35));

        Assert.Equal(MatchState.Completed, m.State);
        Assert.Equal(MatchOutcomeReason.ZoneForfeit, m.Outcome!.EndReason);
        Assert.All(m.Outcome.RankedTeams, rt => Assert.Equal(1, rt.Rank));
    }

    [Fact]
    public void No_zone_configured_means_no_tracking_and_no_forfeit()
    {
        var m = new ActiveMatch(
            Guid.NewGuid(), "gt1",
            new IReadOnlyList<PlayerKey>[] { new[] { K("A") }, new[] { K("C") } },
            new KillCountEndPolicy(1000),
            joinTimeout: TimeSpan.FromMinutes(1),
            graceWindow: TimeSpan.FromMinutes(1),
            proposedAt: T0);
        foreach (var team in m.Teams)
            foreach (var p in team)
                m.OnPlayerJoined(p, T0.AddSeconds(1));
        m.MarkLive(T0.AddSeconds(1));

        Assert.Null(m.OnPositionSample(K("A"), 500, 500, T0.AddSeconds(5)));
        m.Tick(T0.AddMinutes(30));

        Assert.Equal(MatchState.Live, m.State);
        Assert.Empty(m.ZoneVacantSince);
    }

    [Fact]
    public void Samples_before_live_are_ignored()
    {
        var m = new ActiveMatch(
            Guid.NewGuid(), "gt1",
            new IReadOnlyList<PlayerKey>[] { new[] { K("A") }, new[] { K("C") } },
            new KillCountEndPolicy(1000),
            joinTimeout: TimeSpan.FromMinutes(5),
            graceWindow: TimeSpan.FromMinutes(1),
            proposedAt: T0,
            presenceZone: Zone,
            presenceZoneTimeout: TimeSpan.FromSeconds(30));
        m.OnPlayerJoined(K("A"), T0.AddSeconds(1));
        m.OnPlayerJoined(K("C"), T0.AddSeconds(1));

        Assert.Null(m.OnPositionSample(K("A"), 500, 500, T0.AddSeconds(2)));   // still Forming

        // GO! at +60s; the presence clocks must start there, not at the Forming-phase sample.
        m.MarkLive(T0.AddSeconds(60));
        m.OnPositionSample(K("A"), 500, 500, T0.AddSeconds(62));
        m.Tick(T0.AddSeconds(85));   // 25s after GO: C hasn't entered yet but is within timeout
        Assert.Equal(MatchState.Live, m.State);

        m.OnPositionSample(K("A"), 500, 500, T0.AddSeconds(88));
        m.Tick(T0.AddSeconds(95));   // 35s after GO: C's team forfeits
        Assert.Equal(MatchState.Completed, m.State);
        Assert.Equal(MatchOutcomeReason.ZoneForfeit, m.Outcome!.EndReason);
    }

    // ----- Engine-level: telemetry emission + the position intake path -----

    private sealed class Harness
    {
        public FakeClock Clock { get; } = new(T0);
        public RecordingTelemetry Telemetry { get; } = new();
        public MatchmakingEngine Engine { get; }

        public Harness(TimeSpan zoneTimeout)
        {
            Engine = new MatchmakingEngine(
                new InMemoryRatingStore(), Clock,
                new[] { PenaltyPolicy.DefaultAbandonment, PenaltyPolicy.DefaultGriefing },
                quality: new OrdinalSpreadQuality(),
                telemetry: Telemetry,
                joinTimeout: TimeSpan.FromMinutes(1),
                graceWindow: TimeSpan.FromSeconds(30));

            Engine.Queues.Register(
                "zone1v1",
                new MatchShape(2, 1),
                new PartitionQualityPolicy(0.5, 0.15, TimeSpan.FromSeconds(90)),
                "gt1",
                () => new KillCountEndPolicy(1000),
                presenceZone: Zone,
                presenceZoneTimeout: zoneTimeout);
        }

        public ActiveMatch StartMatch(params string[] names)
        {
            foreach (var n in names) Engine.OnPlayerConnected(K(n), Clock.UtcNow);
            foreach (var n in names) Engine.TryEnqueue(K(n), "zone1v1", Clock.UtcNow);
            Engine.Tick(Clock.UtcNow);
            var match = Engine.ActiveMatches.Values.Single();
            foreach (var team in match.Teams)
                foreach (var p in team)
                    Engine.OnPlayerJoinedArena(p, Clock.UtcNow);
            Engine.MarkMatchLive(match.MatchId, Clock.UtcNow);
            return match;
        }
    }

    [Fact]
    public void Engine_emits_vacated_once_then_reclaimed_on_inside_sample()
    {
        var h = new Harness(zoneTimeout: TimeSpan.FromSeconds(30));
        var m = h.StartMatch("A", "C");
        var inZone = m.Teams[0][0];
        var away = m.Teams[1][0];

        // Both inside shortly after GO.
        h.Clock.Advance(TimeSpan.FromSeconds(2));
        h.Engine.OnPlayerPosition(inZone, 500, 500, h.Clock.UtcNow);
        h.Engine.OnPlayerPosition(away, 500, 500, h.Clock.UtcNow);
        var lastPresent = h.Clock.UtcNow;

        // `away` wanders off; past the detection threshold the warning fires exactly once, with
        // the forfeit deadline anchored to the last confirmed presence.
        h.Clock.Advance(TimeSpan.FromSeconds(5));
        h.Engine.OnPlayerPosition(inZone, 500, 500, h.Clock.UtcNow);
        h.Engine.Tick(h.Clock.UtcNow);
        h.Clock.Advance(TimeSpan.FromSeconds(5));
        h.Engine.OnPlayerPosition(inZone, 500, 500, h.Clock.UtcNow);
        h.Engine.Tick(h.Clock.UtcNow);

        var vacated = Assert.Single(h.Telemetry.ZonesVacated);
        Assert.Equal(m.MatchId, vacated.MatchId);
        Assert.Equal(1, vacated.TeamIdx);
        Assert.Equal(lastPresent, vacated.Since);
        Assert.Equal(lastPresent + TimeSpan.FromSeconds(30), vacated.ForfeitAt);

        // Re-entering the zone emits the reclaim immediately (sample-driven, not tick-driven).
        h.Engine.OnPlayerPosition(away, 495, 505, h.Clock.UtcNow);
        var reclaimed = Assert.Single(h.Telemetry.ZonesReclaimed);
        Assert.Equal(1, reclaimed.TeamIdx);
        Assert.Empty(h.Telemetry.Ended);
    }

    [Fact]
    public void Engine_finalizes_zone_forfeit_through_the_normal_outcome_flow()
    {
        var h = new Harness(zoneTimeout: TimeSpan.FromSeconds(30));
        var m = h.StartMatch("A", "C");
        var inZone = m.Teams[0][0];

        for (int i = 0; i < 7; i++)
        {
            h.Clock.Advance(TimeSpan.FromSeconds(5));
            h.Engine.OnPlayerPosition(inZone, 500, 500, h.Clock.UtcNow);
            h.Engine.Tick(h.Clock.UtcNow);
        }

        var ended = Assert.Single(h.Telemetry.Ended);
        Assert.Equal(MatchOutcomeReason.ZoneForfeit, ended.EndReason);
        Assert.Equal(MatchState.Completed, ended.FinalState);
        Assert.Contains(inZone, ended.RankedTeams[0].Players);
    }
}
