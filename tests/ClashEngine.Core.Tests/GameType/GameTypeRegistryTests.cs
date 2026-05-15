using ClashEngine.Core.GameType;

namespace ClashEngine.Core.Tests.GameType;

public class GameTypeRegistryTests
{
    private static GameTypeDef Def(string name, byte id, int teamCount = 2, int perTeam = 3) =>
        new GameTypeDef(
            Name: name,
            Id: id,
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
            new[] { Def("elim_3v3", 1), Def("elim_4v4", 2) }, out var errors);

        Assert.True(ok);
        Assert.Empty(errors);
        Assert.Equal(2, r.Count);
        Assert.True(r.TryGet("elim_3v3", out var got));
        Assert.Equal((byte)1, got.Id);
    }

    [Fact]
    public void ReplaceArenaContribution_rejects_id_collision_across_sources()
    {
        var r = new GameTypeRegistry();
        Assert.True(r.ReplaceArenaContribution("lobby", new[] { Def("elim_3v3", 1) }, out _));

        // Arena 'other' tries to define a different name with the same Id -- should fail.
        var ok = r.ReplaceArenaContribution("other", new[] { Def("ctf_3v3", 1) }, out var errors);
        Assert.False(ok);
        Assert.NotEmpty(errors);
        Assert.Equal(1, r.Count);   // first contribution preserved
    }

    [Fact]
    public void ReplaceArenaContribution_rejects_name_collision_across_sources()
    {
        var r = new GameTypeRegistry();
        Assert.True(r.ReplaceArenaContribution("lobby", new[] { Def("elim_3v3", 1) }, out _));

        var ok = r.ReplaceArenaContribution("other", new[] { Def("elim_3v3", 2) }, out var errors);
        Assert.False(ok);
        Assert.NotEmpty(errors);
        Assert.True(r.TryGet("elim_3v3", out var got));
        Assert.Equal((byte)1, got.Id);   // original Id preserved
    }

    [Fact]
    public void ReplaceArenaContribution_rejects_id_change_for_existing_name()
    {
        var r = new GameTypeRegistry();
        Assert.True(r.ReplaceArenaContribution("lobby", new[] { Def("elim_3v3", 1) }, out _));

        // Hot-reload that renumbers a game type's Id is rejected because PersistRatingStore
        // keys by Id; silent renumbering would orphan rating rows.
        var ok = r.ReplaceArenaContribution("lobby", new[] { Def("elim_3v3", 5) }, out var errors);
        Assert.False(ok);
        Assert.Contains(errors, e => e.Contains("Id change rejected"));
        Assert.True(r.TryGet("elim_3v3", out var got));
        Assert.Equal((byte)1, got.Id);
    }

    [Fact]
    public void ReplaceArenaContribution_allows_same_source_to_replace_own_entries()
    {
        var r = new GameTypeRegistry();
        Assert.True(r.ReplaceArenaContribution("lobby",
            new[] { Def("elim_3v3", 1), Def("elim_4v4", 2) }, out _));

        // Same source replaces its set: drops elim_4v4, keeps elim_3v3, adds ctf.
        var ok = r.ReplaceArenaContribution("lobby",
            new[] { Def("elim_3v3", 1), Def("ctf_3v3", 3) }, out _);
        Assert.True(ok);
        Assert.Equal(2, r.Count);
        Assert.True(r.TryGet("elim_3v3", out _));
        Assert.True(r.TryGet("ctf_3v3", out _));
        Assert.False(r.TryGet("elim_4v4", out _));
    }

    [Fact]
    public void ReplaceArenaContribution_rejects_internal_duplicate_id()
    {
        var r = new GameTypeRegistry();
        var ok = r.ReplaceArenaContribution("lobby",
            new[] { Def("a", 1), Def("b", 1) }, out var errors);
        Assert.False(ok);
        Assert.NotEmpty(errors);
        Assert.Equal(0, r.Count);
    }

    [Fact]
    public void Remove_drops_only_targeted_source()
    {
        var r = new GameTypeRegistry();
        Assert.True(r.ReplaceArenaContribution("lobby", new[] { Def("a", 1) }, out _));
        Assert.True(r.ReplaceArenaContribution("matcharena", new[] { Def("b", 2) }, out _));

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
        Assert.True(r.ReplaceArenaContribution(sourceArena: null, new[] { Def("shared", 10) }, out _));
        Assert.True(r.TryGetSource("shared", out var src));
        Assert.Null(src);
    }
}
