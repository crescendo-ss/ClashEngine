using ClashEngine.Core.Identity;
using ClashEngine.Core.Matching;
using ClashEngine.Core.Queue;

namespace ClashEngine.Core.Tests.Queue;

public class QueueDefinitionTests
{
    private static PartitionQualityPolicy Policy() =>
        new(0.5, 0.15, TimeSpan.FromSeconds(90));

    [Fact]
    public void Default_rating_weight_is_one()
    {
        var def = new QueueDefinition("q", new MatchShape(2, 2), Policy());
        Assert.Equal(1.0, def.RatingWeight);
    }

    [Fact]
    public void Casual_rating_weight_can_be_below_one()
    {
        var def = new QueueDefinition("q", new MatchShape(2, 2), Policy(), ratingWeight: 0.5);
        Assert.Equal(0.5, def.RatingWeight);
    }

    [Fact]
    public void Negative_or_above_one_rating_weight_throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new QueueDefinition("q", new MatchShape(2, 2), Policy(), ratingWeight: -0.1));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new QueueDefinition("q", new MatchShape(2, 2), Policy(), ratingWeight: 1.1));
    }

    [Fact]
    public void Match_arena_name_round_trips()
    {
        var def = new QueueDefinition("q", new MatchShape(2, 2), Policy(), matchArenaName: "4v4comp");
        Assert.Equal("4v4comp", def.MatchArenaName);
    }

    [Fact]
    public void Default_match_arena_is_null()
    {
        Assert.Null(new QueueDefinition("q", new MatchShape(2, 2), Policy()).MatchArenaName);
    }

    [Fact]
    public void Ship_by_slot_round_trips()
    {
        var ships = new IReadOnlyList<int>[]
        {
            new[] { 1, 2 },   // team 0: Warbird, Javelin
            new[] { 3, 4 },   // team 1: Spider, Leviathan
        };
        var def = new QueueDefinition("q", new MatchShape(2, 2), Policy(), shipBySlot: ships);
        Assert.NotNull(def.ShipBySlot);
        Assert.Equal(2, def.ShipBySlot![0][1]);
        Assert.Equal(4, def.ShipBySlot[1][1]);
    }

    [Fact]
    public void Ship_by_slot_dimension_mismatch_throws()
    {
        var wrongTeamCount = new IReadOnlyList<int>[] { new[] { 1, 2 } };  // only 1 team
        Assert.Throws<ArgumentException>(() =>
            new QueueDefinition("q", new MatchShape(2, 2), Policy(), shipBySlot: wrongTeamCount));

        var wrongSlotCount = new IReadOnlyList<int>[] { new[] { 1, 2, 3 }, new[] { 4, 5, 6 } };  // 3 per team
        Assert.Throws<ArgumentException>(() =>
            new QueueDefinition("q", new MatchShape(2, 2), Policy(), shipBySlot: wrongSlotCount));
    }

    [Fact]
    public void FreqOf_follows_hundred_step_convention()
    {
        Assert.Equal((short)100, QueueDefinition.FreqOf(0));
        Assert.Equal((short)200, QueueDefinition.FreqOf(1));
        Assert.Equal((short)300, QueueDefinition.FreqOf(2));
        Assert.Equal((short)400, QueueDefinition.FreqOf(3));
    }

    [Fact]
    public void SpawnSetByTeam_accepts_per_team_lists()
    {
        var spawns = new IReadOnlyList<SpawnPoint>[]
        {
            new[] { new SpawnPoint(100, 200), new SpawnPoint(150, 200) },
            new[] { new SpawnPoint(800, 200) },
        };
        var def = new QueueDefinition("q", new MatchShape(2, 2), Policy(), spawnSetByTeam: spawns);
        Assert.NotNull(def.SpawnSetByTeam);
        Assert.Equal(2, def.SpawnSetByTeam!.Count);
        Assert.Equal(2, def.SpawnSetByTeam[0].Count);
    }

    [Fact]
    public void SpawnSetByTeam_rejects_wrong_team_count()
    {
        var spawns = new IReadOnlyList<SpawnPoint>[]
        {
            new[] { new SpawnPoint(100, 200) },
        };
        Assert.Throws<ArgumentException>(() =>
            new QueueDefinition("q", new MatchShape(2, 2), Policy(), spawnSetByTeam: spawns));
    }

    [Fact]
    public void SpawnSetByTeam_rejects_empty_inner_list()
    {
        var spawns = new IReadOnlyList<SpawnPoint>[]
        {
            new[] { new SpawnPoint(100, 200) },
            Array.Empty<SpawnPoint>(),
        };
        Assert.Throws<ArgumentException>(() =>
            new QueueDefinition("q", new MatchShape(2, 2), Policy(), spawnSetByTeam: spawns));
    }

    [Fact]
    public void MaxSpawnDriftTiles_rejects_negative()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new QueueDefinition("q", new MatchShape(2, 2), Policy(), maxSpawnDriftTiles: -1));
    }

    [Fact]
    public void WarpOnSpawn_defaults_off()
    {
        var def = new QueueDefinition("q", new MatchShape(2, 2), Policy());
        Assert.False(def.WarpOnSpawn);
    }

    [Fact]
    public void WarpOnSpawn_can_be_enabled()
    {
        var spawns = new IReadOnlyList<SpawnPoint>[]
        {
            new[] { new SpawnPoint(100, 200) },
            new[] { new SpawnPoint(800, 200) },
        };
        var def = new QueueDefinition("q", new MatchShape(2, 2), Policy(),
            spawnSetByTeam: spawns, warpOnSpawn: true);
        Assert.True(def.WarpOnSpawn);
    }

    [Fact]
    public void StagingDuration_and_CountdownDuration_default_to_10s_and_5s()
    {
        var def = new QueueDefinition("q", new MatchShape(2, 2), Policy());
        Assert.Equal(TimeSpan.FromSeconds(10), def.StagingDuration);
        Assert.Equal(TimeSpan.FromSeconds(5), def.CountdownDuration);
    }

    [Fact]
    public void StagingDuration_and_CountdownDuration_accept_overrides()
    {
        var def = new QueueDefinition("q", new MatchShape(2, 2), Policy(),
            stagingDuration: TimeSpan.FromSeconds(5),
            countdownDuration: TimeSpan.FromSeconds(7));
        Assert.Equal(TimeSpan.FromSeconds(5), def.StagingDuration);
        Assert.Equal(TimeSpan.FromSeconds(7), def.CountdownDuration);
    }

    [Fact]
    public void StagingDuration_rejects_non_positive()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new QueueDefinition("q", new MatchShape(2, 2), Policy(),
                stagingDuration: TimeSpan.Zero));
    }

    [Fact]
    public void CountdownDuration_rejects_below_minimum()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new QueueDefinition("q", new MatchShape(2, 2), Policy(),
                countdownDuration: TimeSpan.FromSeconds(-1)));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new QueueDefinition("q", new MatchShape(2, 2), Policy(),
                countdownDuration: TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new QueueDefinition("q", new MatchShape(2, 2), Policy(),
                countdownDuration: TimeSpan.FromSeconds(4)));

        // 5s exactly is allowed (the minimum).
        var def = new QueueDefinition("q", new MatchShape(2, 2), Policy(),
            countdownDuration: TimeSpan.FromSeconds(5));
        Assert.Equal(TimeSpan.FromSeconds(5), def.CountdownDuration);
    }
}
