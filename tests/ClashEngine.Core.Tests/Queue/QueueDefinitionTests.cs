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
    public void Default_return_items_action_is_full()
    {
        // The default keeps existing behavior (?return gives a fresh ship). Queues opt in to
        // Restore / Burn explicitly.
        var def = new QueueDefinition("q", new MatchShape(2, 2), Policy());
        Assert.Equal(ItemsAction.Full, def.ReturnItemsAction);
    }

    [Theory]
    [InlineData(ItemsAction.Full)]
    [InlineData(ItemsAction.Restore)]
    [InlineData(ItemsAction.Burn)]
    public void Return_items_action_round_trips(ItemsAction action)
    {
        var def = new QueueDefinition("q", new MatchShape(2, 2), Policy(), returnItemsAction: action);
        Assert.Equal(action, def.ReturnItemsAction);
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
    public void Label_defaults_to_BaseName_when_no_label_supplied()
    {
        // No owner arena: BaseName == Name == "q"; Label falls back to BaseName.
        var def = new QueueDefinition("q", new MatchShape(2, 2), Policy());
        Assert.Equal("q", def.BaseName);
        Assert.Equal("q", def.Label);
    }

    [Fact]
    public void Label_defaults_to_BaseName_when_empty_label_supplied()
    {
        var def = new QueueDefinition("q", new MatchShape(2, 2), Policy(), label: "");
        Assert.Equal("q", def.Label);
    }

    [Fact]
    public void Operator_supplied_Label_overrides_BaseName()
    {
        var def = new QueueDefinition(
            "lobby/casual_4v4", new MatchShape(2, 4), Policy(),
            ownerArenaName: "lobby", label: "4v4 (Casual)");
        Assert.Equal("casual_4v4", def.BaseName);
        Assert.Equal("4v4 (Casual)", def.Label);
    }

    [Fact]
    public void BaseName_strips_owner_arena_prefix()
    {
        var def = new QueueDefinition(
            "lobby/3v3comp", new MatchShape(2, 3), Policy(), ownerArenaName: "lobby");
        Assert.Equal("3v3comp", def.BaseName);
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
    public void StartSetByTeam_accepts_per_team_lists()
    {
        var spawns = new IReadOnlyList<StartPoint>[]
        {
            new[] { new StartPoint(100, 200), new StartPoint(150, 200) },
            new[] { new StartPoint(800, 200) },
        };
        var def = new QueueDefinition("q", new MatchShape(2, 2), Policy(), startSetByTeam: spawns);
        Assert.NotNull(def.StartSetByTeam);
        Assert.Equal(2, def.StartSetByTeam!.Count);
        Assert.Equal(2, def.StartSetByTeam[0].Count);
    }

    [Fact]
    public void StartSetByTeam_rejects_wrong_team_count()
    {
        var spawns = new IReadOnlyList<StartPoint>[]
        {
            new[] { new StartPoint(100, 200) },
        };
        Assert.Throws<ArgumentException>(() =>
            new QueueDefinition("q", new MatchShape(2, 2), Policy(), startSetByTeam: spawns));
    }

    [Fact]
    public void StartSetByTeam_rejects_empty_inner_list()
    {
        var spawns = new IReadOnlyList<StartPoint>[]
        {
            new[] { new StartPoint(100, 200) },
            Array.Empty<StartPoint>(),
        };
        Assert.Throws<ArgumentException>(() =>
            new QueueDefinition("q", new MatchShape(2, 2), Policy(), startSetByTeam: spawns));
    }

    [Fact]
    public void MaxStartDriftTiles_rejects_negative()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new QueueDefinition("q", new MatchShape(2, 2), Policy(), maxStartDriftTiles: -1));
    }

    [Fact]
    public void UseStartLocation_defaults_off()
    {
        var def = new QueueDefinition("q", new MatchShape(2, 2), Policy());
        Assert.False(def.UseStartLocation);
    }

    [Fact]
    public void UseStartLocation_can_be_enabled()
    {
        var spawns = new IReadOnlyList<StartPoint>[]
        {
            new[] { new StartPoint(100, 200) },
            new[] { new StartPoint(800, 200) },
        };
        var def = new QueueDefinition("q", new MatchShape(2, 2), Policy(),
            startSetByTeam: spawns, useStartLocation: true);
        Assert.True(def.UseStartLocation);
    }

    [Fact]
    public void SpawnByTeam_accepts_per_team_boxes_with_null_entries()
    {
        var boxes = new SpawnArea?[]
        {
            new SpawnArea(new StartPoint(480, 256), 8),
            null,
        };
        var def = new QueueDefinition("q", new MatchShape(2, 2), Policy(), spawnByTeam: boxes);
        Assert.NotNull(def.SpawnByTeam);
        Assert.Equal(2, def.SpawnByTeam!.Count);
        Assert.Equal(8, def.SpawnByTeam[0]!.Value.RadiusTiles);
        Assert.Null(def.SpawnByTeam[1]);
    }

    [Fact]
    public void SpawnByTeam_rejects_wrong_team_count()
    {
        var boxes = new SpawnArea?[] { new SpawnArea(new StartPoint(480, 256), 8) };
        Assert.Throws<ArgumentException>(() =>
            new QueueDefinition("q", new MatchShape(2, 2), Policy(), spawnByTeam: boxes));
    }

    [Fact]
    public void SpawnByTeam_rejects_radius_above_native_max()
    {
        var boxes = new SpawnArea?[]
        {
            new SpawnArea(new StartPoint(480, 256), 512),
            null,
        };
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new QueueDefinition("q", new MatchShape(2, 2), Policy(), spawnByTeam: boxes));
    }

    [Fact]
    public void StagingDuration_and_CountdownDuration_default_to_10s_and_10s()
    {
        var def = new QueueDefinition("q", new MatchShape(2, 2), Policy());
        Assert.Equal(TimeSpan.FromSeconds(10), def.StagingDuration);
        // Countdown defaults to 10s -- 5s ship-pick window + 5s locked before GO.
        Assert.Equal(TimeSpan.FromSeconds(10), def.CountdownDuration);
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
