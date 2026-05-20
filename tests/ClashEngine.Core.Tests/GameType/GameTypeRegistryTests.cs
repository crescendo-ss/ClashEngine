using ClashEngine.Core.GameType;

namespace ClashEngine.Core.Tests.GameType;

public class GameTypeRegistryTests
{
    private static GameTypeDef Def(string name, int teamCount = 2, int perTeam = 3) =>
        new GameTypeDef(
            Name: name,
            Label: name,
            Description: null,
            Metadata: GameTypeMetadata.Uniform(teamCount, perTeam, lives: 0),
            TeamCount: teamCount,
            PlayersPerTeam: perTeam,
            KillTarget: 30,
            Lives: 0,
            TimeLimit: null,
            SpawnSetByTeam: null,
            MaxSpawnDriftTiles: null,
            WarpOnSpawn: false,
            StagingDuration: null,
            CountdownDuration: null,
            KnockoutSpecDelay: null,
            TeamCollapseGrace: null,
            ShipBySlot: null,
            ShipChangeGracePeriod: null,
            ReturnItemsAction: Core.Queue.ItemsAction.Full);

    [Fact]
    public void ReplaceArenaContribution_accepts_first_load()
    {
        var r = new GameTypeRegistry();
        var ok = r.ReplaceArenaContribution("lobby",
            new[] { Def("elim_3v3"), Def("elim_4v4") }, out var errors);

        Assert.True(ok);
        Assert.Empty(errors);
        Assert.Equal(2, r.Count);
        Assert.True(r.TryGet("elim_3v3", out var got));
        Assert.Equal("elim_3v3", got.Name);
    }

    [Fact]
    public void ReplaceArenaContribution_rejects_name_collision_across_sources()
    {
        var r = new GameTypeRegistry();
        Assert.True(r.ReplaceArenaContribution("lobby", new[] { Def("elim_3v3") }, out _));

        // Arena 'other' tries to define a gametype with the same name -- another source can't
        // grab a name that's already taken.
        var ok = r.ReplaceArenaContribution("other", new[] { Def("elim_3v3", perTeam: 4) }, out var errors);
        Assert.False(ok);
        Assert.NotEmpty(errors);
        Assert.Equal(1, r.Count);   // first contribution preserved
        Assert.True(r.TryGet("elim_3v3", out var got));
        Assert.Equal(3, got.PlayersPerTeam);   // original preserved
    }

    [Fact]
    public void ReplaceArenaContribution_allows_same_source_to_replace_own_entries()
    {
        var r = new GameTypeRegistry();
        Assert.True(r.ReplaceArenaContribution("lobby",
            new[] { Def("elim_3v3"), Def("elim_4v4") }, out _));

        // Same source replaces its set: drops elim_4v4, keeps elim_3v3, adds ctf.
        var ok = r.ReplaceArenaContribution("lobby",
            new[] { Def("elim_3v3"), Def("ctf_3v3") }, out _);
        Assert.True(ok);
        Assert.Equal(2, r.Count);
        Assert.True(r.TryGet("elim_3v3", out _));
        Assert.True(r.TryGet("ctf_3v3", out _));
        Assert.False(r.TryGet("elim_4v4", out _));
    }

    [Fact]
    public void ReplaceArenaContribution_rejects_internal_duplicate_name()
    {
        var r = new GameTypeRegistry();
        var ok = r.ReplaceArenaContribution("lobby",
            new[] { Def("a"), Def("a", perTeam: 4) }, out var errors);
        Assert.False(ok);
        Assert.NotEmpty(errors);
        Assert.Equal(0, r.Count);
    }

    [Fact]
    public void Remove_drops_only_targeted_source()
    {
        var r = new GameTypeRegistry();
        Assert.True(r.ReplaceArenaContribution("lobby", new[] { Def("a") }, out _));
        Assert.True(r.ReplaceArenaContribution("matcharena", new[] { Def("b") }, out _));

        var removed = r.Remove("lobby");
        Assert.Contains("a", removed);
        Assert.Equal(1, r.Count);
        Assert.True(r.TryGet("b", out _));
        Assert.False(r.TryGet("a", out _));
    }

    [Fact]
    public void ZoneWide_contribution_uses_null_source_arena()
    {
        var r = new GameTypeRegistry();
        Assert.True(r.ReplaceArenaContribution(sourceArena: null, new[] { Def("shared") }, out _));
        Assert.True(r.TryGetSource("shared", out var src));
        Assert.Null(src);
    }
}
