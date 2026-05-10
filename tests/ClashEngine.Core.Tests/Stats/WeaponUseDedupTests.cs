using ClashEngine.Core.Identity;
using ClashEngine.Core.Stats;

namespace ClashEngine.Core.Tests.Stats;

public class WeaponUseDedupTests
{
    private static PlayerKey K(string n) => new(n);

    private const byte Repel = 5; // arbitrary stand-in; the helper is weapon-code-agnostic.
    private const byte Bullet = 1;

    [Fact]
    public void First_observation_is_accepted()
    {
        var d = new WeaponUseDedup();
        Assert.True(d.Observe(K("A"), Repel, 100));
    }

    [Fact]
    public void Exact_duplicate_within_window_is_rejected()
    {
        var d = new WeaponUseDedup();
        Assert.True(d.Observe(K("A"), Repel, 100));
        Assert.False(d.Observe(K("A"), Repel, 100));
    }

    [Fact]
    public void Same_weapon_at_different_tick_is_accepted()
    {
        var d = new WeaponUseDedup();
        Assert.True(d.Observe(K("A"), Repel, 100));
        Assert.True(d.Observe(K("A"), Repel, 101));
    }

    [Fact]
    public void Different_weapon_at_same_tick_is_accepted()
    {
        // Two presses landing in the same tick is implausible for one player on a single
        // wire, but the dedup key is (weapon, tick) -- different weapon must not collide.
        var d = new WeaponUseDedup();
        Assert.True(d.Observe(K("A"), Repel, 100));
        Assert.True(d.Observe(K("A"), Bullet, 100));
    }

    [Fact]
    public void Entry_outside_window_is_evicted_and_pair_is_re_accepted()
    {
        var d = new WeaponUseDedup();
        Assert.True(d.Observe(K("A"), Repel, 100));
        // First observation past the trim cutoff (atTick - WindowTicks > 100) trims the
        // earlier entry, so the same (weapon, tick) is fresh again. We deliberately step
        // past the window to verify trimming, not to model real wire behavior.
        uint past = 100 + WeaponUseDedup.WindowTicks + 1;
        Assert.True(d.Observe(K("A"), Repel, past));
        Assert.True(d.Observe(K("A"), Repel, 100));
    }

    [Fact]
    public void Entry_at_window_boundary_is_retained()
    {
        var d = new WeaponUseDedup();
        Assert.True(d.Observe(K("A"), Repel, 100));
        // Boundary tick: cutoff = atTick - WindowTicks; entries with tick < cutoff are
        // evicted, so an entry exactly at the cutoff stays.
        uint boundary = 100 + WeaponUseDedup.WindowTicks;
        Assert.True(d.Observe(K("A"), Bullet, boundary));
        Assert.False(d.Observe(K("A"), Repel, 100));
    }

    [Fact]
    public void Per_player_logs_are_isolated()
    {
        var d = new WeaponUseDedup();
        Assert.True(d.Observe(K("A"), Repel, 100));
        Assert.True(d.Observe(K("B"), Repel, 100));
        Assert.False(d.Observe(K("A"), Repel, 100));
        Assert.False(d.Observe(K("B"), Repel, 100));
    }

    [Fact]
    public void Forget_clears_a_single_player()
    {
        var d = new WeaponUseDedup();
        Assert.True(d.Observe(K("A"), Repel, 100));
        Assert.True(d.Observe(K("B"), Repel, 100));
        d.Forget(K("A"));
        Assert.True(d.Observe(K("A"), Repel, 100));
        Assert.False(d.Observe(K("B"), Repel, 100));
    }

    [Fact]
    public void Clear_drops_every_player_log()
    {
        var d = new WeaponUseDedup();
        d.Observe(K("A"), Repel, 100);
        d.Observe(K("B"), Bullet, 100);
        d.Clear();
        Assert.True(d.Observe(K("A"), Repel, 100));
        Assert.True(d.Observe(K("B"), Bullet, 100));
    }
}
