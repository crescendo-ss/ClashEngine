using System.Linq;
using ClashEngine.Core;
using ClashEngine.Core.Adapter;
using ClashEngine.Core.Groups;
using ClashEngine.Core.Identity;
using ClashEngine.Core.Matches;
using ClashEngine.Core.Matching;
using ClashEngine.Core.Penalties;
using ClashEngine.Core.Ratings;
using ClashEngine.Core.Tests.Fakes;

namespace ClashEngine.Core.Tests.Events;

public class QueueRemovalReasonTests
{
    private static PlayerKey K(string n) => new(n);
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private sealed class Harness
    {
        public FakeClock Clock { get; } = new(T0);
        public InMemoryRatingStore Ratings { get; } = new();
        public RecordingTelemetry Telemetry { get; } = new();
        public MatchmakingEngine Engine { get; }

        public Harness()
        {
            Engine = new MatchmakingEngine(
                Ratings, Clock,
                new[] { PenaltyPolicy.DefaultAbandonment, PenaltyPolicy.DefaultGriefing },
                telemetry: Telemetry);
            Engine.Queues.Register(
                "2v2",
                new MatchShape(2, 2),
                new PartitionQualityPolicy(0.5, 0.15, TimeSpan.FromSeconds(90)),
                "gt1",
                () => new KillCountEndPolicy(5));
        }

        public void Connect(params string[] names)
        {
            foreach (var n in names) Engine.OnPlayerConnected(K(n), Clock.UtcNow);
        }

        public void Enqueue(params string[] names)
        {
            foreach (var n in names) Engine.TryEnqueue(K(n), "2v2", Clock.UtcNow);
        }
    }

    [Fact]
    public void Cancel_via_dequeue_reports_reason_cancel()
    {
        var h = new Harness();
        h.Connect("A");
        h.Enqueue("A");
        h.Engine.Dequeue(K("A"), "2v2", h.Clock.UtcNow);

        Assert.Equal(QueueRemovalReason.Cancel, Assert.Single(h.Telemetry.QueueRemovals).Reason);
    }

    [Fact]
    public void Cancel_via_dequeue_everywhere_reports_reason_cancel()
    {
        var h = new Harness();
        h.Connect("A");
        h.Enqueue("A");
        h.Engine.DequeueEverywhere(K("A"), h.Clock.UtcNow);

        Assert.Equal(QueueRemovalReason.Cancel, Assert.Single(h.Telemetry.QueueRemovals).Reason);
    }

    [Fact]
    public void Disconnect_while_queued_reports_reason_disconnect()
    {
        var h = new Harness();
        h.Connect("A");
        h.Enqueue("A");
        h.Engine.OnPlayerDisconnected(K("A"), h.Clock.UtcNow);

        Assert.Equal(QueueRemovalReason.Disconnect, Assert.Single(h.Telemetry.QueueRemovals).Reason);
    }

    [Fact]
    public void Reset_reports_reason_reset()
    {
        var h = new Harness();
        h.Connect("A");
        h.Enqueue("A");
        h.Engine.ResetPlayer(K("A"), h.Clock.UtcNow, keepRating: true);

        Assert.Contains(h.Telemetry.QueueRemovals, r => r.Player.Equals(K("A")) && r.Reason == QueueRemovalReason.Reset);
        Assert.All(h.Telemetry.QueueRemovals, r => Assert.Equal(QueueRemovalReason.Reset, r.Reason));
    }

    [Fact]
    public void Match_formation_reports_reason_matched()
    {
        var h = new Harness();
        h.Connect("A", "B", "C", "D");
        h.Enqueue("A", "B", "C", "D");
        h.Engine.Tick(T0);

        Assert.Equal(4, h.Telemetry.QueueRemovals.Count);
        Assert.All(h.Telemetry.QueueRemovals, r => Assert.Equal(QueueRemovalReason.Matched, r.Reason));
    }

    [Fact]
    public void Match_formation_reports_matched_for_other_queues_the_player_was_searching()
    {
        var h = new Harness();
        // A second queue (a 1v1 duel) that player A is searching alongside the 2v2 from the harness.
        h.Engine.Queues.Register(
            "duel",
            new MatchShape(2, 1),
            new PartitionQualityPolicy(0.5, 0.15, TimeSpan.FromSeconds(90)),
            "gt2",
            () => new KillCountEndPolicy(5));

        h.Connect("A", "B", "C", "D");
        h.Enqueue("A", "B", "C", "D");                       // all four searching 2v2
        h.Engine.TryEnqueue(K("A"), "duel", h.Clock.UtcNow); // A is also searching the duel queue

        h.Engine.Tick(T0); // 2v2 fills and forms; the duel can't (only A is in it)

        // Every removal is reason=Matched (nobody cancelled or disconnected)...
        Assert.All(h.Telemetry.QueueRemovals, r => Assert.Equal(QueueRemovalReason.Matched, r.Reason));
        // ...four for the 2v2 the match actually formed from...
        Assert.Equal(4, h.Telemetry.QueueRemovals.Count(r => r.Queue == "2v2"));
        // ...plus one for A's *other* queue, which previously went unreported to the event stream.
        Assert.Contains(h.Telemetry.QueueRemovals, r => r.Player.Equals(K("A")) && r.Queue == "duel");
        Assert.Equal(5, h.Telemetry.QueueRemovals.Count);
    }

    [Fact]
    public void Group_accept_reports_reason_group_change()
    {
        var h = new Harness();
        h.Connect("A", "B");
        h.Enqueue("A", "B");
        Assert.Equal(InviteResult.Sent, h.Engine.InviteToGroup(K("A"), K("B"), h.Clock.UtcNow));
        Assert.Equal(AcceptResult.Joined, h.Engine.AcceptInvite(K("B"), K("A"), h.Clock.UtcNow, out _));

        Assert.NotEmpty(h.Telemetry.QueueRemovals);
        Assert.All(h.Telemetry.QueueRemovals, r => Assert.Equal(QueueRemovalReason.GroupChange, r.Reason));
    }
}
