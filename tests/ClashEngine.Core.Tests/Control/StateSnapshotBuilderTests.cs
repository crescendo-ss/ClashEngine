using System.Linq;
using ClashEngine.Core;
using ClashEngine.Core.Control;
using ClashEngine.Core.Identity;
using ClashEngine.Core.Matching;
using ClashEngine.Core.Penalties;
using ClashEngine.Core.Ratings;
using ClashEngine.Core.Tests.Fakes;

namespace ClashEngine.Core.Tests.Control;

public class StateSnapshotBuilderTests
{
    private static PlayerKey K(string n) => new(n);
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private sealed class Harness
    {
        public FakeClock Clock { get; } = new(T0);
        public MatchmakingEngine Engine { get; }

        public Harness()
        {
            Engine = new MatchmakingEngine(
                new InMemoryRatingStore(), Clock,
                new[] { PenaltyPolicy.DefaultAbandonment, PenaltyPolicy.DefaultGriefing },
                quality: new OrdinalSpreadQuality());

            Engine.Queues.Register(
                "2v2",
                new MatchShape(2, 2),
                new PartitionQualityPolicy(0.5, 0.15, TimeSpan.FromSeconds(90)),
                "gt1");
        }

        public void Connect(params string[] names)
        {
            foreach (var n in names) Engine.OnPlayerConnected(K(n), Clock.UtcNow);
        }
    }

    private static IReadOnlyList<IReadOnlyList<PlayerKey>> Teams2v2(string a, string b, string c, string d) =>
        new[] { new[] { K(a), K(b) }, new[] { K(c), K(d) } };

    [Fact]
    public void Snapshot_captures_queues_matches_and_penalties()
    {
        var h = new Harness();
        h.Connect("A", "B", "C", "D", "E", "F");
        Assert.Equal(EnqueueResult.Ok,
            h.Engine.TryEnqueueGroup(new[] { K("A"), K("B") }, "2v2", T0, out _));
        var formed = h.Engine.TryFormMatch("2v2", Teams2v2("C", "D", "E", "F"), T0);
        Assert.Equal(FormMatchResult.Ok, formed.Result);
        h.Engine.Penalties.RecordPenalty(K("Z"), PenaltyKind.Abandonment, T0);

        var snap = StateSnapshotBuilder.Build(h.Engine, h.Clock.UtcNow);

        Assert.Equal(ControlSchema.Version, snap.SchemaVersion);
        Assert.Equal(h.Clock.UtcNow, snap.CapturedAt);

        var q = Assert.Single(snap.Queues);
        Assert.Equal("2v2", q.QueueName);
        Assert.Equal("gt1", q.GameType);
        Assert.Equal(4, q.Capacity);
        Assert.Equal(new[] { "A", "B" }, q.Members.Select(m => m.Player).ToArray());
        Assert.Equal(T0, q.Members[0].EnqueuedAt);
        Assert.NotNull(q.Members[0].Group);
        Assert.Equal(q.Members[0].Group, q.Members[1].Group);   // party members share the tag

        var m = Assert.Single(snap.Matches);
        Assert.Equal(formed.MatchId, m.MatchId);
        Assert.Equal("gt1", m.GameType);
        Assert.Equal("2v2", m.QueueName);
        Assert.Equal("Forming", m.State);
        Assert.Null(m.StartedAt);
        Assert.Equal(new[] { "C", "D" }, m.Teams[0]);
        Assert.Equal(new[] { "E", "F" }, m.Teams[1]);

        var p = Assert.Single(snap.Penalties);
        Assert.Equal("Z", p.Player);
        Assert.Equal("abandonment", p.Kind);
        Assert.Equal(1, p.OffenseCount);
        Assert.True(p.TimeoutUntil > h.Clock.UtcNow);
    }

    [Fact]
    public void Live_match_reports_state_and_start_time()
    {
        var h = new Harness();
        h.Connect("A", "B", "C", "D");
        var formed = h.Engine.TryFormMatch("2v2", Teams2v2("A", "B", "C", "D"), T0);
        foreach (var n in new[] { "A", "B", "C", "D" })
            h.Engine.OnPlayerJoinedArena(K(n), h.Clock.UtcNow);
        h.Clock.Advance(TimeSpan.FromSeconds(20));
        Assert.True(h.Engine.MarkMatchLive(formed.MatchId, h.Clock.UtcNow));

        var snap = StateSnapshotBuilder.Build(h.Engine, h.Clock.UtcNow);

        var m = Assert.Single(snap.Matches);
        Assert.Equal("Live", m.State);
        Assert.Equal(h.Clock.UtcNow, m.StartedAt);
    }

    [Fact]
    public void Served_timeouts_are_excluded()
    {
        var h = new Harness();
        h.Engine.Penalties.RecordPenalty(K("Z"), PenaltyKind.Abandonment, T0);
        var until = h.Engine.Penalties.TimeoutUntil(K("Z"));
        Assert.NotNull(until);

        h.Clock.Set(until!.Value + TimeSpan.FromSeconds(1));
        var snap = StateSnapshotBuilder.Build(h.Engine, h.Clock.UtcNow);

        Assert.Empty(snap.Penalties);
    }

    [Fact]
    public void Empty_engine_yields_empty_but_well_formed_snapshot()
    {
        var h = new Harness();
        var snap = StateSnapshotBuilder.Build(h.Engine, h.Clock.UtcNow);
        var q = Assert.Single(snap.Queues);   // registered queues appear even when empty
        Assert.Empty(q.Members);
        Assert.Empty(snap.Matches);
        Assert.Empty(snap.Penalties);
    }
}
