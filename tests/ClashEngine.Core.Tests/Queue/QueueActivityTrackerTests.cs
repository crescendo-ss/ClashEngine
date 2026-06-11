using ClashEngine.Core.Identity;
using ClashEngine.Core.Queue;

namespace ClashEngine.Core.Tests.Queue;

/// <summary>
/// The queue-liveness decision kernel. The central contract: mere packet receipt is never
/// activity -- only a rotation <em>change</em> against the anchor, a weapon fire, or a
/// deliberate signal (chat) is, and forwards are spaced by the cooldown. A motionless client
/// keep-aliving at a constant rotation must never refresh (the AFK-cull case, and the invariant
/// the ClashRig e2e dwell scenario depends on).
/// </summary>
public class QueueActivityTrackerTests
{
    private static PlayerKey K(string n) => new(n);
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Cooldown = TimeSpan.FromSeconds(10);

    private static QueueActivityTracker Tracked(params string[] players)
    {
        var t = new QueueActivityTracker(Cooldown);
        foreach (var p in players) t.OnQueueAdded(K(p), "q1");
        return t;
    }

    [Fact]
    public void First_position_signal_seeds_the_rotation_anchor_without_signaling()
    {
        var t = Tracked("A");
        Assert.False(t.OnPositionSignal(K("A"), rotation: 7, weaponFired: false, T0));
    }

    [Fact]
    public void Unchanged_rotation_packets_never_signal()
    {
        var t = Tracked("A");
        // A motionless client keep-alives at a constant rotation for a long time, with every
        // packet outside the cooldown window -- still no liveness.
        for (int i = 0; i < 20; i++)
            Assert.False(t.OnPositionSignal(K("A"), rotation: 7, weaponFired: false,
                T0 + TimeSpan.FromSeconds(15 * i)));
    }

    [Fact]
    public void Rotation_change_signals_then_cooldown_suppresses_repeats()
    {
        var t = Tracked("A");
        t.OnPositionSignal(K("A"), 7, false, T0);                                 // seeds
        Assert.True(t.OnPositionSignal(K("A"), 8, false, T0.AddSeconds(1)));      // turned
        Assert.False(t.OnPositionSignal(K("A"), 9, false, T0.AddSeconds(2)));     // cooldown
        Assert.False(t.OnPositionSignal(K("A"), 10, false, T0.AddSeconds(9)));    // cooldown
    }

    [Fact]
    public void Cooldown_elapsed_allows_the_next_forward()
    {
        var t = Tracked("A");
        t.OnPositionSignal(K("A"), 7, false, T0);
        Assert.True(t.OnPositionSignal(K("A"), 8, false, T0.AddSeconds(1)));
        Assert.True(t.OnPositionSignal(K("A"), 9, false, T0.AddSeconds(11)));   // 10s since forward
    }

    [Fact]
    public void Rotation_during_cooldown_still_moves_the_anchor()
    {
        var t = Tracked("A");
        t.OnPositionSignal(K("A"), 7, false, T0);
        Assert.True(t.OnPositionSignal(K("A"), 8, false, T0.AddSeconds(1)));
        Assert.False(t.OnPositionSignal(K("A"), 20, false, T0.AddSeconds(2)));  // cooldown eats it
        // After the cooldown, a packet at the SAME rotation 20 is not a change -- the anchor
        // followed the cooldown-period packet, so stillness stays stillness.
        Assert.False(t.OnPositionSignal(K("A"), 20, false, T0.AddSeconds(15)));
    }

    [Fact]
    public void Weapon_fire_signals_even_on_the_first_packet()
    {
        var t = Tracked("A");
        Assert.True(t.OnPositionSignal(K("A"), rotation: 7, weaponFired: true, T0));
    }

    [Fact]
    public void Chat_signals_and_shares_the_cooldown_with_position_activity()
    {
        var t = Tracked("A");
        Assert.True(t.OnDeliberateSignal(K("A"), T0));
        Assert.False(t.OnDeliberateSignal(K("A"), T0.AddSeconds(5)));                  // cooldown
        t.OnPositionSignal(K("A"), 7, false, T0.AddSeconds(6));                        // seeds
        Assert.False(t.OnPositionSignal(K("A"), 8, true, T0.AddSeconds(7)));           // same cooldown
        Assert.True(t.OnDeliberateSignal(K("A"), T0.AddSeconds(11)));
    }

    [Fact]
    public void Signals_for_untracked_players_are_ignored()
    {
        var t = Tracked("A");
        Assert.False(t.OnPositionSignal(K("B"), 7, weaponFired: true, T0));
        Assert.False(t.OnDeliberateSignal(K("B"), T0));
        Assert.False(t.IsTracking(K("B")));
    }

    [Fact]
    public void Leaving_the_last_queue_clears_state_so_a_requeue_reseeds_the_anchor()
    {
        var t = Tracked("A");
        t.OnPositionSignal(K("A"), 7, false, T0);
        t.OnQueueRemoved(K("A"), "q1");
        Assert.False(t.IsTracking(K("A")));

        t.OnQueueAdded(K("A"), "q1");
        // Were the old anchor (7) retained, this rotation-25 packet would signal; a fresh
        // tracking epoch must re-seed instead.
        Assert.False(t.OnPositionSignal(K("A"), 25, false, T0.AddMinutes(5)));
    }

    [Fact]
    public void Player_in_two_queues_stays_tracked_until_the_last_removal()
    {
        var t = Tracked("A");
        t.OnQueueAdded(K("A"), "q2");

        t.OnQueueRemoved(K("A"), "q1");
        Assert.True(t.IsTracking(K("A")));

        t.OnQueueRemoved(K("A"), "q2");
        Assert.False(t.IsTracking(K("A")));
    }
}
