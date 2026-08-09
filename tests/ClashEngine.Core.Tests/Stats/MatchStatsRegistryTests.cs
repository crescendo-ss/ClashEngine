using ClashEngine.Core.Identity;
using ClashEngine.Core.Stats;

namespace ClashEngine.Core.Tests.Stats;

public class MatchStatsRegistryTests
{
    private static PlayerKey K(string n) => new(n);

    private static WeaponEnergyConfig Energy() => new(
        new Dictionary<WeaponKind, (int, int)> { [WeaponKind.Bullet] = (100, 0) },
        multifireBulletEnergy: 0);

    [Fact]
    public void Begin_match_creates_recorder()
    {
        var reg = new MatchStatsRegistry(new DamageDecay());
        var id = Guid.NewGuid();
        var r = reg.BeginMatch(id);
        Assert.NotNull(r);
        Assert.Same(r, reg.ActiveRecorders[id]);
    }

    [Fact]
    public void Begin_match_twice_throws()
    {
        var reg = new MatchStatsRegistry(new DamageDecay());
        var id = Guid.NewGuid();
        reg.BeginMatch(id);
        Assert.Throws<InvalidOperationException>(() => reg.BeginMatch(id));
    }

    [Fact]
    public void Add_player_indexes_player_to_match()
    {
        var reg = new MatchStatsRegistry(new DamageDecay());
        var id = Guid.NewGuid();
        reg.BeginMatch(id);
        reg.AddPlayer(id, K("A"), teamIndex: 0, maxEnergy: 1000, rechargeRate: 1.0, Energy(), atTick: 0);
        Assert.Equal(id, reg.MatchIdOf(K("A")));
        Assert.NotNull(reg.RecorderFor(K("A")));
    }

    [Fact]
    public void Add_player_to_unknown_match_throws()
    {
        var reg = new MatchStatsRegistry(new DamageDecay());
        Assert.Throws<InvalidOperationException>(() =>
            reg.AddPlayer(Guid.NewGuid(), K("A"), 0, 1000, 1.0, Energy(), 0));
    }

    [Fact]
    public void Player_in_two_matches_throws()
    {
        var reg = new MatchStatsRegistry(new DamageDecay());
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        reg.BeginMatch(a);
        reg.BeginMatch(b);
        reg.AddPlayer(a, K("X"), 0, 1000, 1.0, Energy(), 0);
        Assert.Throws<InvalidOperationException>(() =>
            reg.AddPlayer(b, K("X"), 0, 1000, 1.0, Energy(), 0));
    }

    [Fact]
    public void End_match_closes_recorder_and_removes_index()
    {
        var reg = new MatchStatsRegistry(new DamageDecay());
        var id = Guid.NewGuid();
        reg.BeginMatch(id);
        reg.AddPlayer(id, K("A"), 0, 1000, 1.0, Energy(), 0);
        reg.RecorderFor(K("A"))!.OnSpawn(K("A"), atTick: 100);

        var final = reg.EndMatch(id, atTick: 1000);

        Assert.NotNull(final);
        Assert.Empty(reg.ActiveRecorders);
        Assert.Null(reg.RecorderFor(K("A")));
        Assert.Equal(LifeEndReason.MatchEnded, final!.Stats[K("A")].Lives.Single().EndReason);
    }

    [Fact]
    public void End_unknown_match_returns_null()
    {
        var reg = new MatchStatsRegistry(new DamageDecay());
        Assert.Null(reg.EndMatch(Guid.NewGuid(), 0));
    }

    [Fact]
    public void Sub_closes_outgoing_life_and_registers_incoming()
    {
        var reg = new MatchStatsRegistry(new DamageDecay());
        var id = Guid.NewGuid();
        reg.BeginMatch(id);
        reg.AddPlayer(id, K("A"), 0, 1000, 1.0, Energy(), atTick: 0);
        reg.RecorderFor(K("A"))!.OnSpawn(K("A"), atTick: 0);

        reg.OnPlayerSubbed(id, outgoing: K("A"), incoming: K("B"),
            teamIndex: 0, maxEnergy: 1000, rechargeRate: 1.0, Energy(), atTick: 500);

        var recorder = reg.ActiveRecorders[id];
        Assert.Equal(LifeEndReason.LeftMatch, recorder.Stats[K("A")].Lives.Single().EndReason);
        Assert.Null(reg.MatchIdOf(K("A")));
        Assert.Equal(id, reg.MatchIdOf(K("B")));
    }

    [Fact]
    public void Recorder_for_unknown_player_returns_null()
    {
        var reg = new MatchStatsRegistry(new DamageDecay());
        Assert.Null(reg.RecorderFor(K("ZZZ")));
    }

    [Fact]
    public void Released_player_can_be_added_to_a_new_match_while_old_match_lives()
    {
        var reg = new MatchStatsRegistry(new DamageDecay());
        var y = Guid.NewGuid();
        var x = Guid.NewGuid();
        reg.BeginMatch(y);
        reg.AddPlayer(y, K("happy_004"), 0, 1000, 1.0, Energy(), atTick: 0);
        reg.RecorderFor(K("happy_004"))!.OnSpawn(K("happy_004"), atTick: 0);

        reg.OnPlayerReleased(y, K("happy_004"), atTick: 500);

        Assert.Null(reg.MatchIdOf(K("happy_004")));
        // The release closed the open life on Y but kept the player's stats in Y's recorder.
        Assert.Equal(LifeEndReason.LeftMatch, reg.ActiveRecorders[y].Stats[K("happy_004")].Lives.Single().EndReason);

        // Same player can now be registered in a different live match -- this is the elim+requeue path.
        reg.BeginMatch(x);
        reg.AddPlayer(x, K("happy_004"), 0, 1000, 1.0, Energy(), atTick: 600);
        Assert.Equal(x, reg.MatchIdOf(K("happy_004")));
    }

    [Fact]
    public void Ending_old_match_keeps_the_index_of_a_player_already_in_a_newer_match()
    {
        // Continuation of the elim+requeue path above: the OLD match ends while the released
        // player is mid-fight in the new one. EndMatch used to clear the index for everyone in
        // its recorder, which severed dispatch for the match the player was actually playing --
        // no recorder meant no new lives opened, so their recorded play time stopped dead at the
        // instant the old match ended, and every later kill/damage event was dropped.
        var reg = new MatchStatsRegistry(new DamageDecay());
        var old = Guid.NewGuid();
        var current = Guid.NewGuid();
        reg.BeginMatch(old);
        reg.AddPlayer(old, K("happy_004"), 0, 1000, 1.0, Energy(), atTick: 0);
        reg.AddPlayer(old, K("teammate"), 0, 1000, 1.0, Energy(), atTick: 0);
        reg.OnPlayerReleased(old, K("happy_004"), atTick: 500);

        reg.BeginMatch(current);
        reg.AddPlayer(current, K("happy_004"), 0, 1000, 1.0, Energy(), atTick: 600);

        var final = reg.EndMatch(old, atTick: 1000);

        // The re-queued player keeps dispatching into the live match...
        Assert.Equal(current, reg.MatchIdOf(K("happy_004")));
        Assert.Same(reg.ActiveRecorders[current], reg.RecorderFor(K("happy_004")));
        // ...while their stats from the old match still ship with its upload.
        Assert.Contains(K("happy_004"), final!.Stats.Keys);
        // A player the old match really did own is still released.
        Assert.Null(reg.MatchIdOf(K("teammate")));
    }

    [Fact]
    public void Player_in_a_newer_match_keeps_recording_lives_after_the_old_match_ends()
    {
        var reg = new MatchStatsRegistry(new DamageDecay());
        var old = Guid.NewGuid();
        var current = Guid.NewGuid();
        reg.BeginMatch(old);
        reg.AddPlayer(old, K("happy_004"), 0, 1000, 1.0, Energy(), atTick: 0);
        reg.OnPlayerReleased(old, K("happy_004"), atTick: 500);
        reg.BeginMatch(current);
        reg.AddPlayer(current, K("happy_004"), 0, 1000, 1.0, Energy(), atTick: 600);
        reg.AddPlayer(current, K("opponent"), 1, 1000, 1.0, Energy(), atTick: 600);
        reg.RecorderFor(K("happy_004"))!.OnSpawn(K("happy_004"), atTick: 600);

        reg.EndMatch(old, atTick: 1000);

        // Everything past this point routes the way StatsListener does -- via RecorderFor, whose
        // null result is a silent drop. Pre-fix the index was gone, so the death and the respawn
        // never reached the live recorder and the player's last life was whatever was open when
        // the old match ended.
        reg.RecorderFor(K("happy_004"))?.OnKill(K("happy_004"), K("opponent"), atTick: 1200);
        reg.RecorderFor(K("happy_004"))?.OnSpawn(K("happy_004"), atTick: 1300);
        var live = reg.EndMatch(current, atTick: 2000);

        var lives = live!.Stats[K("happy_004")].Lives;
        Assert.Equal(2, lives.Count);
        Assert.Equal(1200u, lives[0].EndTick);
        Assert.Equal(LifeEndReason.MatchEnded, lives[^1].EndReason);
        Assert.Equal(2000u, lives[^1].EndTick);
    }

    [Fact]
    public void Releasing_player_not_in_match_is_a_noop()
    {
        var reg = new MatchStatsRegistry(new DamageDecay());
        var y = Guid.NewGuid();
        reg.BeginMatch(y);
        reg.AddPlayer(y, K("A"), 0, 1000, 1.0, Energy(), atTick: 0);

        // Different match id -- no-op, doesn't clobber the existing index entry.
        reg.OnPlayerReleased(Guid.NewGuid(), K("A"), atTick: 100);
        Assert.Equal(y, reg.MatchIdOf(K("A")));

        // Unknown player -- no-op.
        reg.OnPlayerReleased(y, K("never-added"), atTick: 100);
    }

    [Fact]
    public void End_match_after_release_still_uploads_player_stats()
    {
        var reg = new MatchStatsRegistry(new DamageDecay());
        var y = Guid.NewGuid();
        reg.BeginMatch(y);
        reg.AddPlayer(y, K("happy_004"), 0, 1000, 1.0, Energy(), atTick: 0);
        reg.RecorderFor(K("happy_004"))!.OnSpawn(K("happy_004"), atTick: 0);
        reg.OnPlayerReleased(y, K("happy_004"), atTick: 500);

        var final = reg.EndMatch(y, atTick: 1000);

        Assert.NotNull(final);
        Assert.Contains(K("happy_004"), final!.Stats.Keys);
        // Released player's life closed at the release tick, not at match-end -- second EndMatch
        // pass for an already-closed life is a no-op, so the original LeftMatch reason stands.
        Assert.Equal(LifeEndReason.LeftMatch, final.Stats[K("happy_004")].Lives.Single().EndReason);
    }
}
