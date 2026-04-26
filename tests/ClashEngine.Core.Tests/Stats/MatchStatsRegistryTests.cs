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
}
