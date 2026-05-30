using ClashEngine.Core;
using ClashEngine.Core.Eligibility;
using ClashEngine.Core.Groups;
using ClashEngine.Core.Identity;
using ClashEngine.Core.Matches;
using ClashEngine.Core.Matching;
using ClashEngine.Core.Penalties;
using ClashEngine.Core.Ratings;
using ClashEngine.Core.Tests.Fakes;

namespace ClashEngine.Core.Tests;

public class GroupMatchmakingTests
{
    private static PlayerKey K(string n) => new(n);
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private sealed class Harness
    {
        public FakeClock Clock { get; } = new(T0);
        public InMemoryRatingStore Ratings { get; } = new();
        public RecordingTelemetry Telemetry { get; } = new();
        public MatchmakingEngine Engine { get; }

        public Harness(double qStart = 0.5, double qFloor = 0.15)
        {
            Engine = new MatchmakingEngine(
                Ratings, Clock,
                new[] { PenaltyPolicy.DefaultAbandonment, PenaltyPolicy.DefaultGriefing },
                quality: new OrdinalSpreadQuality(),
                telemetry: Telemetry,
                joinTimeout: TimeSpan.FromMinutes(1),
                graceWindow: TimeSpan.FromSeconds(30));

            Engine.Queues.Register(
                "2v2",
                new MatchShape(2, 2),
                new PartitionQualityPolicy(qStart, qFloor, TimeSpan.FromSeconds(90)),
                "gt1");
        }

        public void Connect(params string[] names)
        {
            foreach (var n in names) Engine.OnPlayerConnected(K(n), Clock.UtcNow);
        }

        public void SetRating(string name, double mu)
        {
            Ratings.Set(K(name), "gt1", new Rating(mu, 0, 0, default));
        }
    }

    [Fact]
    public void TryEnqueueGroup_succeeds_and_returns_a_group_id()
    {
        var h = new Harness();
        h.Connect("A", "B");

        var status = h.Engine.TryEnqueueGroup(new[] { K("A"), K("B") }, "2v2", T0, out var groupId);

        Assert.Equal(EnqueueResult.Ok, status);
        Assert.False(groupId.IsDefault);
        Assert.Equal(2, h.Telemetry.QueueAdds.Count);
    }

    [Fact]
    public void TryEnqueueGroup_with_existing_group_id_reuses_it()
    {
        var h = new Harness();
        h.Connect("A", "B");
        var preset = GroupId.New();

        h.Engine.TryEnqueueGroup(new[] { K("A"), K("B") }, "2v2", T0, out var got, existingGroup: preset);

        Assert.Equal(preset, got);
    }

    [Fact]
    public void TryEnqueueGroup_fails_atomically_when_one_member_ineligible()
    {
        var h = new Harness();
        h.Connect("A");  // B is not connected.

        var status = h.Engine.TryEnqueueGroup(new[] { K("A"), K("B") }, "2v2", T0, out _);

        Assert.Equal(EnqueueResult.NotConnected, status);
        // Neither player should be enqueued; no QueueAdded telemetry.
        Assert.Empty(h.Telemetry.QueueAdds);
    }

    [Fact]
    public void TryEnqueueGroup_rejects_duplicate_member_keys()
    {
        var h = new Harness();
        h.Connect("A");
        var status = h.Engine.TryEnqueueGroup(new[] { K("A"), K("a") }, "2v2", T0, out _);
        Assert.Equal(EnqueueResult.AlreadyQueued, status);
    }

    [Fact]
    public void Group_of_two_in_2v2_keeps_them_together()
    {
        var h = new Harness();
        h.Connect("A", "B", "C", "D");
        h.Engine.TryEnqueueGroup(new[] { K("A"), K("B") }, "2v2", T0, out _);
        h.Engine.TryEnqueue(K("C"), "2v2", T0);
        h.Engine.TryEnqueue(K("D"), "2v2", T0);

        h.Engine.Tick(T0);

        Assert.Single(h.Telemetry.Proposed);
        var teams = h.Telemetry.Proposed[0].Teams;
        var teamWithA = teams.First(t => t.Contains(K("A")));
        Assert.Contains(K("B"), teamWithA);
    }

    [Fact]
    public void Group_larger_than_team_size_is_rejected_by_the_queue()
    {
        var h = new Harness();
        h.Connect("A", "B", "C");

        var status = h.Engine.TryEnqueueGroup(new[] { K("A"), K("B"), K("C") }, "2v2", T0, out _);

        Assert.Equal(EnqueueResult.GroupTooLarge, status);
        Assert.Empty(h.Telemetry.QueueAdds);
    }

    [Fact]
    public void Group_size_equal_to_team_size_is_allowed()
    {
        var h = new Harness();
        h.Connect("A", "B");

        var status = h.Engine.TryEnqueueGroup(new[] { K("A"), K("B") }, "2v2", T0, out _);

        Assert.Equal(EnqueueResult.Ok, status);
    }

    [Fact]
    public void Same_group_can_queue_a_smaller_subset_for_a_smaller_team_size()
    {
        // Members A and B can queue together in 2v2; in a hypothetical "duel" (1v1) queue,
        // they'd need to queue separately because group size 2 exceeds team size 1.
        var h = new Harness();
        h.Engine.Queues.Register(
            "duel",
            new MatchShape(2, 1),
            new PartitionQualityPolicy(0.5, 0.15, TimeSpan.FromSeconds(90)),
            "gt2");

        h.Connect("A", "B");
        Assert.Equal(EnqueueResult.Ok,
            h.Engine.TryEnqueueGroup(new[] { K("A"), K("B") }, "2v2", T0, out _));
        Assert.Equal(EnqueueResult.GroupTooLarge,
            h.Engine.TryEnqueueGroup(new[] { K("A"), K("B") }, "duel", T0, out _));
    }

    [Fact]
    public void Group_preference_yields_to_quality_when_grouped_partition_is_unfair()
    {
        // Setup: a duo group of 2 high-rated players and 2 low-rated solos. Keeping the group
        // together produces an unbalanced match {High,High} vs {Low,Low} — quality 0. Splitting
        // produces {High,Low} vs {High,Low} — quality 1. Threshold 0.5 → only split passes.
        var h = new Harness(qStart: 0.5, qFloor: 0.5);  // tight floor
        h.Connect("A", "B", "C", "D");
        h.SetRating("A", 50);
        h.SetRating("B", 50);
        h.SetRating("C", 0);
        h.SetRating("D", 0);

        h.Engine.TryEnqueueGroup(new[] { K("A"), K("B") }, "2v2", T0, out _);
        h.Engine.TryEnqueue(K("C"), "2v2", T0);
        h.Engine.TryEnqueue(K("D"), "2v2", T0);

        h.Engine.Tick(T0);

        Assert.Single(h.Telemetry.Proposed);
        var teams = h.Telemetry.Proposed[0].Teams;
        var teamWithA = teams.First(t => t.Contains(K("A")));
        // Group should be split for fairness.
        Assert.DoesNotContain(K("B"), teamWithA);
    }

    [Fact]
    public void DequeueEverywhere_for_one_group_member_does_not_remove_others()
    {
        var h = new Harness();
        h.Connect("A", "B");
        h.Engine.TryEnqueueGroup(new[] { K("A"), K("B") }, "2v2", T0, out _);

        h.Engine.DequeueEverywhere(K("A"), T0);

        h.Engine.Queues.TryGet("2v2", out var def);
        Assert.False(def!.Queue.Contains(K("A")));
        Assert.True(def.Queue.Contains(K("B")));
    }

    [Fact]
    public void Group_id_can_span_multiple_queues()
    {
        var h = new Harness();
        h.Engine.Queues.Register(
            "4v4",
            new MatchShape(2, 4),
            new PartitionQualityPolicy(0.5, 0.15, TimeSpan.FromSeconds(90)),
            "gt2");

        h.Connect("A", "B");
        h.Engine.TryEnqueueGroup(new[] { K("A"), K("B") }, "2v2", T0, out var groupId);
        var status = h.Engine.TryEnqueueGroup(new[] { K("A"), K("B") }, "4v4", T0, out var sameGroup,
            existingGroup: groupId);

        Assert.Equal(EnqueueResult.Ok, status);
        Assert.Equal(groupId, sameGroup);
    }

    [Fact]
    public void Invite_then_Accept_creates_group_and_TryEnqueueGroup_uses_it()
    {
        var h = new Harness();
        h.Connect("A", "B", "C", "D");

        Assert.Equal(InviteResult.Sent, h.Engine.InviteToGroup(K("A"), K("B"), T0));
        Assert.Equal(AcceptResult.Joined, h.Engine.AcceptInvite(K("B"), K("A"), T0.AddSeconds(2), out var registryGroup));

        // TryEnqueueGroup without an explicit existingGroup should pick up the registry group.
        var status = h.Engine.TryEnqueueGroup(new[] { K("A"), K("B") }, "2v2", T0.AddSeconds(3), out var queueGroup);
        Assert.Equal(EnqueueResult.Ok, status);
        Assert.Equal(registryGroup, queueGroup);
    }

    [Fact]
    public void LeaveGroup_dequeues_from_all_queues()
    {
        var h = new Harness();
        h.Engine.Queues.Register(
            "4v4",
            new MatchShape(2, 4),
            new PartitionQualityPolicy(0.5, 0.15, TimeSpan.FromSeconds(90)),
            "gt2");

        h.Connect("A", "B");
        h.Engine.InviteToGroup(K("A"), K("B"), T0);
        h.Engine.AcceptInvite(K("B"), K("A"), T0.AddSeconds(1), out _);

        h.Engine.TryEnqueueGroup(new[] { K("A"), K("B") }, "2v2", T0.AddSeconds(2), out _);
        h.Engine.TryEnqueueGroup(new[] { K("A"), K("B") }, "4v4", T0.AddSeconds(3), out _);

        h.Engine.LeaveGroup(K("A"), T0.AddSeconds(4));

        h.Engine.Queues.TryGet("2v2", out var def2);
        h.Engine.Queues.TryGet("4v4", out var def4);
        Assert.False(def2!.Queue.Contains(K("A")));
        Assert.False(def4!.Queue.Contains(K("A")));
        Assert.Null(h.Engine.Groups.GroupOf(K("A")));
    }

    [Fact]
    public void LeaveGroup_dequeues_every_remaining_member_too()
    {
        // Membership change -> sweep all members, not just the leaver.
        var h = new Harness();
        h.Engine.Queues.Register(
            "4v4",
            new MatchShape(2, 4),
            new PartitionQualityPolicy(0.5, 0.15, TimeSpan.FromSeconds(90)),
            "gt2");

        h.Connect("A", "B", "C");
        h.Engine.InviteToGroup(K("A"), K("B"), T0);
        h.Engine.AcceptInvite(K("B"), K("A"), T0.AddSeconds(1), out _);
        h.Engine.InviteToGroup(K("A"), K("C"), T0.AddSeconds(2));
        h.Engine.AcceptInvite(K("C"), K("A"), T0.AddSeconds(3), out _);

        h.Engine.TryEnqueueGroup(new[] { K("A"), K("B"), K("C") }, "4v4", T0.AddSeconds(4), out _);

        // C leaves. The party survives (open mode, count drops to 2). A and B should still get
        // swept out of the 4v4 queue because the group composition just changed.
        h.Engine.LeaveGroup(K("C"), T0.AddSeconds(5));

        h.Engine.Queues.TryGet("4v4", out var def4);
        Assert.False(def4!.Queue.Contains(K("A")));
        Assert.False(def4.Queue.Contains(K("B")));
        Assert.False(def4.Queue.Contains(K("C")));
    }

    [Fact]
    public void AcceptInvite_dequeues_existing_group_members_and_joiner()
    {
        // Joining an existing group changes its composition -> sweep all members from queues.
        var h = new Harness();
        h.Connect("A", "B", "C");
        h.Engine.InviteToGroup(K("A"), K("B"), T0);
        h.Engine.AcceptInvite(K("B"), K("A"), T0.AddSeconds(1), out _);

        // {A, B} queue together. Then C joins.
        h.Engine.TryEnqueueGroup(new[] { K("A"), K("B") }, "2v2", T0.AddSeconds(2), out _);
        // C is solo-queued separately to verify their entry is also wiped on accept.
        h.Engine.TryEnqueue(K("C"), "2v2", T0.AddSeconds(3));

        h.Engine.InviteToGroup(K("A"), K("C"), T0.AddSeconds(4));
        h.Engine.AcceptInvite(K("C"), K("A"), T0.AddSeconds(5), out _);

        h.Engine.Queues.TryGet("2v2", out var def);
        Assert.False(def!.Queue.Contains(K("A")));
        Assert.False(def.Queue.Contains(K("B")));
        Assert.False(def.Queue.Contains(K("C")));
    }

    [Fact]
    public void LeaveGroup_in_closed_party_with_leader_disbands_everyone()
    {
        // Closed-party leader leave -> everyone is removed from groups and queues.
        var h = new Harness();
        h.Connect("A", "B");
        h.Engine.InviteToGroup(K("A"), K("B"), T0);
        h.Engine.AcceptInvite(K("B"), K("A"), T0.AddSeconds(1), out _);
        h.Engine.SetGroupMode(K("A"), GroupMode.Closed, T0.AddSeconds(2));   // A is leader

        h.Engine.TryEnqueueGroup(new[] { K("A"), K("B") }, "2v2", T0.AddSeconds(3), out _);

        h.Engine.LeaveGroup(K("A"), T0.AddSeconds(4));

        Assert.Null(h.Engine.Groups.GroupOf(K("A")));
        Assert.Null(h.Engine.Groups.GroupOf(K("B")));
        h.Engine.Queues.TryGet("2v2", out var def);
        Assert.False(def!.Queue.Contains(K("A")));
        Assert.False(def.Queue.Contains(K("B")));
    }

    [Fact]
    public void TryEnqueueGroup_in_open_party_attributes_initiator_to_other_members_only()
    {
        // The initiator's own row stays unattributed (so they see the standard "Queued for ..."
        // reply); the rest of the party gets the initiator on their row so the listener can
        // render "X queued you for ...".
        var h = new Harness();
        h.Engine.Queues.Register(
            "4v4",
            new MatchShape(2, 4),
            new PartitionQualityPolicy(0.5, 0.15, TimeSpan.FromSeconds(90)),
            "gt2");

        h.Connect("A", "B", "C");
        h.Engine.InviteToGroup(K("A"), K("B"), T0);
        h.Engine.AcceptInvite(K("B"), K("A"), T0.AddSeconds(1), out _);
        h.Engine.InviteToGroup(K("A"), K("C"), T0.AddSeconds(2));
        h.Engine.AcceptInvite(K("C"), K("A"), T0.AddSeconds(3), out _);

        h.Telemetry.QueueAdds.Clear();
        var status = h.Engine.TryEnqueueGroup(
            new[] { K("A"), K("B"), K("C") }, "4v4", T0.AddSeconds(4), out _, initiator: K("A"));

        Assert.Equal(EnqueueResult.Ok, status);
        Assert.Equal(3, h.Telemetry.QueueAdds.Count);
        var addsByPlayer = h.Telemetry.QueueAdds.ToDictionary(e => e.Player);
        Assert.Null(addsByPlayer[K("A")].Initiator);
        Assert.Equal(K("A"), addsByPlayer[K("B")].Initiator);
        Assert.Equal(K("A"), addsByPlayer[K("C")].Initiator);
    }

    [Fact]
    public void TryEnqueueGroup_in_closed_party_does_not_attribute_initiator()
    {
        // Closed-party convention: the leader is the implicit queuer, so the bare "Queued for ..."
        // reply is correct -- no attribution needed.
        var h = new Harness();
        h.Connect("A", "B");
        h.Engine.InviteToGroup(K("A"), K("B"), T0);
        h.Engine.AcceptInvite(K("B"), K("A"), T0.AddSeconds(1), out _);
        h.Engine.SetGroupMode(K("A"), GroupMode.Closed, T0.AddSeconds(2));

        h.Telemetry.QueueAdds.Clear();
        var status = h.Engine.TryEnqueueGroup(
            new[] { K("A"), K("B") }, "2v2", T0.AddSeconds(3), out _, initiator: K("A"));

        Assert.Equal(EnqueueResult.Ok, status);
        Assert.All(h.Telemetry.QueueAdds, e => Assert.Null(e.Initiator));
    }

    [Fact]
    public void TryEnqueue_solo_records_no_initiator()
    {
        var h = new Harness();
        h.Connect("A");

        var status = h.Engine.TryEnqueue(K("A"), "2v2", T0);

        Assert.Equal(EnqueueResult.Ok, status);
        Assert.Single(h.Telemetry.QueueAdds);
        Assert.Null(h.Telemetry.QueueAdds[0].Initiator);
    }

    [Fact]
    public void Tick_prunes_expired_invitations()
    {
        var h = new Harness();
        h.Connect("A", "B");
        h.Engine.InviteToGroup(K("A"), K("B"), T0);

        h.Clock.Advance(TimeSpan.FromSeconds(20));   // past 15s default TTL
        h.Engine.Tick(h.Clock.UtcNow);

        Assert.Empty(h.Engine.Groups.PendingFor(K("B"), h.Clock.UtcNow));
    }

    [Fact]
    public void Party_with_a_member_beyond_the_lookahead_is_not_partially_selected()
    {
        // 2v2 with the default lookahead (= 4). Queue three solos, then a 2-party at the back, so
        // only ONE party member (the 4th entry) lands inside the lookahead pool and the other sits
        // just beyond it. The party is all-or-nothing: the in-pool member must NOT be pulled in
        // without its partner -- and since the partner is outside the pool, no match can form.
        var h = new Harness();
        h.Connect("C", "D", "E", "A", "B");

        h.Engine.TryEnqueue(K("C"), "2v2", T0);
        h.Engine.TryEnqueue(K("D"), "2v2", T0.AddSeconds(1));
        h.Engine.TryEnqueue(K("E"), "2v2", T0.AddSeconds(2));
        h.Engine.TryEnqueueGroup(new[] { K("A"), K("B") }, "2v2", T0.AddSeconds(3), out _);

        // Tick across a long wait window: even as the quality threshold relaxes, the party can
        // never be partially selected, so no proposal is ever produced.
        for (int s = 3; s <= 120; s += 10)
            h.Engine.Tick(T0.AddSeconds(s));

        Assert.Empty(h.Telemetry.Proposed);
    }

    [Fact]
    public void A_party_is_pulled_in_whole_rather_than_one_member_dropped_for_quality()
    {
        // All five candidates fit in the lookahead pool. Dropping the party's off-rated member (B)
        // would let the matcher form a flawless {C,D,E,A} 2v2 -- but that partially selects the
        // party. With integrity enforced the matcher must take the whole party in (or none), so the
        // proposal contains BOTH A and B.
        var h = new Harness();
        h.Engine.Queues.Register(
            "wide2v2",
            new MatchShape(2, 2),
            new PartitionQualityPolicy(0.5, 0.15, TimeSpan.FromSeconds(90)),
            "gtw",
            lookAheadWindow: 8);

        h.Connect("C", "D", "E", "A", "B");
        h.SetRating("C", 25); h.SetRating("D", 25); h.SetRating("E", 25);
        h.SetRating("A", 25); h.SetRating("B", 30);   // {C,D,E,A} would be the "best" partial pick

        h.Engine.TryEnqueue(K("C"), "wide2v2", T0);
        h.Engine.TryEnqueue(K("D"), "wide2v2", T0.AddSeconds(1));
        h.Engine.TryEnqueue(K("E"), "wide2v2", T0.AddSeconds(2));
        h.Engine.TryEnqueueGroup(new[] { K("A"), K("B") }, "wide2v2", T0.AddSeconds(3), out _);

        h.Engine.Tick(T0.AddSeconds(3));

        Assert.Single(h.Telemetry.Proposed);
        var players = h.Telemetry.Proposed[0].Teams.SelectMany(t => t).ToList();
        Assert.Contains(K("A"), players);
        Assert.Contains(K("B"), players);
    }
}
