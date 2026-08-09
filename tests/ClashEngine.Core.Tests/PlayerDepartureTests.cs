using ClashEngine.Core;
using ClashEngine.Core.Identity;
using ClashEngine.Core.Matches;
using ClashEngine.Core.Matching;
using ClashEngine.Core.Penalties;
using ClashEngine.Core.Ratings;
using ClashEngine.Core.Tests.Fakes;

namespace ClashEngine.Core.Tests;

/// <summary>
/// The departure announcement feed. A mid-match departure (spec, arena exit, drop) used to be
/// invisible to the other players until the grace window expired -- and then only to the
/// departing player's teammates -- while the matching <em>return</em> was broadcast to the whole
/// match. These pin the event the adapter announces from.
/// </summary>
public class PlayerDepartureTests
{
    private static PlayerKey K(string n) => new(n);
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private sealed class Harness
    {
        public FakeClock Clock { get; } = new(T0);
        public RecordingTelemetry Telemetry { get; } = new();
        public MatchmakingEngine Engine { get; }
        public TimeSpan Grace { get; }

        public Harness(TimeSpan? graceWindow = null)
        {
            Grace = graceWindow ?? TimeSpan.FromSeconds(30);
            Engine = new MatchmakingEngine(
                new InMemoryRatingStore(), Clock,
                new[] { PenaltyPolicy.DefaultAbandonment, PenaltyPolicy.DefaultGriefing },
                quality: new OrdinalSpreadQuality(),
                telemetry: Telemetry,
                joinTimeout: TimeSpan.FromMinutes(1),
                graceWindow: Grace);

            Engine.Queues.Register(
                "2v2",
                new MatchShape(2, 2),
                new PartitionQualityPolicy(0.5, 0.15, TimeSpan.FromSeconds(90)),
                "gt-depart",
                () => new KillCountEndPolicy(5));
        }

        public ActiveMatch StartMatch()
        {
            string[] names = { "A", "B", "C", "D" };
            foreach (var n in names) Engine.OnPlayerConnected(K(n), Clock.UtcNow);
            foreach (var n in names) Engine.TryEnqueue(K(n), "2v2", Clock.UtcNow);
            Engine.Tick(Clock.UtcNow);

            var match = Engine.ActiveMatches.Values.Single();
            Clock.Advance(TimeSpan.FromSeconds(2));
            foreach (var n in names) Engine.OnPlayerJoinedArena(K(n), Clock.UtcNow);
            Engine.MarkMatchLive(match.MatchId, Clock.UtcNow);
            return match;
        }
    }

    [Fact]
    public void Speccing_out_of_a_live_match_announces_the_departure()
    {
        var h = new Harness();
        var match = h.StartMatch();
        h.Clock.Advance(TimeSpan.FromSeconds(5));

        h.Engine.OnPlayerSpecced(K("A"), h.Clock.UtcNow);

        var d = Assert.Single(h.Telemetry.PlayerDepartures);
        Assert.Equal(K("A"), d.Player);
        Assert.Equal(match.MatchId, d.MatchId);
        Assert.Equal(h.Clock.UtcNow, d.At);
        Assert.Equal(h.Clock.UtcNow + h.Grace, d.ReturnBy);      // deadline for a penalty-free return
    }

    [Fact]
    public void Leaving_the_arena_announces_the_departure()
    {
        var h = new Harness();
        h.StartMatch();
        h.Clock.Advance(TimeSpan.FromSeconds(5));

        h.Engine.OnPlayerLeftArena(K("A"), h.Clock.UtcNow);

        Assert.Single(h.Telemetry.PlayerDepartures);
    }

    [Fact]
    public void Disconnecting_announces_the_departure()
    {
        var h = new Harness();
        h.StartMatch();
        h.Clock.Advance(TimeSpan.FromSeconds(5));

        h.Engine.OnPlayerDisconnected(K("A"), h.Clock.UtcNow);

        Assert.Single(h.Telemetry.PlayerDepartures);
    }

    [Fact]
    public void One_departure_reported_three_ways_announces_once()
    {
        // A real drop arrives as a spec, then a leave-arena, then a disconnect. Only the first
        // is a status transition, so the match hears about it once.
        var h = new Harness();
        h.StartMatch();
        h.Clock.Advance(TimeSpan.FromSeconds(5));

        h.Engine.OnPlayerSpecced(K("A"), h.Clock.UtcNow);
        h.Engine.OnPlayerLeftArena(K("A"), h.Clock.UtcNow);
        h.Engine.OnPlayerDisconnected(K("A"), h.Clock.UtcNow);

        Assert.Single(h.Telemetry.PlayerDepartures);
    }

    [Fact]
    public void Returning_then_leaving_again_announces_each_departure()
    {
        var h = new Harness();
        h.StartMatch();

        h.Clock.Advance(TimeSpan.FromSeconds(5));
        h.Engine.OnPlayerSpecced(K("A"), h.Clock.UtcNow);
        h.Clock.Advance(TimeSpan.FromSeconds(5));
        h.Engine.OnPlayerReturned(K("A"), h.Clock.UtcNow);       // within the 30s grace
        h.Clock.Advance(TimeSpan.FromSeconds(5));
        h.Engine.OnPlayerSpecced(K("A"), h.Clock.UtcNow);

        Assert.Equal(2, h.Telemetry.PlayerDepartures.Count);
        Assert.Single(h.Telemetry.PlayerReturns);
    }

    [Fact]
    public void Departure_of_a_player_in_no_match_announces_nothing()
    {
        var h = new Harness();
        h.StartMatch();
        h.Engine.OnPlayerConnected(K("Bystander"), h.Clock.UtcNow);

        h.Engine.OnPlayerSpecced(K("Bystander"), h.Clock.UtcNow);
        h.Engine.OnPlayerDisconnected(K("Bystander"), h.Clock.UtcNow);

        Assert.Empty(h.Telemetry.PlayerDepartures);
    }

    [Fact]
    public void Departure_still_drives_abandonment_and_team_collapse()
    {
        // The announcement is additive -- it must not disturb the bookkeeping OnPlayerLeft does.
        var h = new Harness();
        var match = h.StartMatch();
        var team = match.TeamIndexOf(K("A"))!.Value;

        h.Clock.Advance(TimeSpan.FromSeconds(5));
        foreach (var p in match.Teams[team]) h.Engine.OnPlayerDisconnected(p, h.Clock.UtcNow);

        Assert.Equal(match.Teams[team].Count, h.Telemetry.PlayerDepartures.Count);
        Assert.Contains(h.Telemetry.TeamsCollapsing, c => c.MatchId == match.MatchId && c.TeamIdx == team);
    }
}
