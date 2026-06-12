using ClashEngine.Core.Matching;
using ClashEngine.Core.Queue;

namespace ClashEngine.Core.Tests.Queue;

public class QueueRegistryTests
{
    private static PartitionQualityPolicy Policy() =>
        new(0.5, 0.15, TimeSpan.FromSeconds(90));

    [Fact]
    public void Register_creates_definition()
    {
        var r = new QueueRegistry();
        var policy = Policy();
        var def = r.Register("4v4", new MatchShape(2, 4), policy);

        Assert.Equal("4v4", def.UniqueId);
        Assert.Equal(8, def.Shape.TotalPlayers);
        Assert.Same(policy, def.QualityPolicy);
        Assert.NotNull(def.Queue);
    }

    [Fact]
    public void Register_duplicate_throws()
    {
        var r = new QueueRegistry();
        r.Register("4v4", new MatchShape(2, 4), Policy());
        Assert.Throws<ArgumentException>(() => r.Register("4V4", new MatchShape(2, 4), Policy()));
    }

    [Fact]
    public void TryGet_is_case_insensitive()
    {
        var r = new QueueRegistry();
        r.Register("4v4", new MatchShape(2, 4), Policy());
        Assert.True(r.TryGet("4V4", out var def));
        Assert.Equal("4v4", def.UniqueId);
    }

    [Fact]
    public void Definitions_iterates_in_registration_order()
    {
        var r = new QueueRegistry();
        r.Register("a", new MatchShape(2, 2), Policy());
        r.Register("b", new MatchShape(2, 2), Policy());
        r.Register("c", new MatchShape(2, 2), Policy());

        var names = r.Definitions.Select(d => d.UniqueId).ToArray();
        Assert.Equal(new[] { "a", "b", "c" }, names);
    }

    [Fact]
    public void Count_reflects_registrations()
    {
        var r = new QueueRegistry();
        Assert.Equal(0, r.Count);
        r.Register("a", new MatchShape(2, 2), Policy());
        Assert.Equal(1, r.Count);
    }

    [Fact]
    public void QualifyName_produces_arena_slash_base()
    {
        Assert.Equal("lobby/3v3comp", QueueRegistry.QualifyName("lobby", "3v3comp"));
    }

    [Theory]
    [InlineData("4v4", "4v4")]
    [InlineData("casual 4v4", "casual_4v4")]
    [InlineData("casual_4v4", "casual_4v4")]
    [InlineData("  CASUAL    4v4  ", "CASUAL_4v4")]
    [InlineData("a b c d", "a_b_c_d")]
    [InlineData("", "")]
    [InlineData("   ", "")]
    [InlineData("\t single \t", "single")]
    public void JoinTokensToQueueName_collapses_whitespace_and_joins_with_underscore(string input, string expected)
    {
        Assert.Equal(expected, QueueRegistry.JoinTokensToQueueName(input.AsSpan()));
    }

    [Fact]
    public void Same_base_name_in_two_arenas_does_not_collide()
    {
        var r = new QueueRegistry();
        // Two arenas each define a queue called "3v3comp" -- they get distinct registry keys
        // via QualifyName, and BaseName strips the prefix for the lookup-stable identifier.
        var lobbyDef = new QueueDefinition(
            QueueRegistry.QualifyName("lobby", "3v3comp"),
            new MatchShape(2, 3), Policy(),
            ownerArenaName: "lobby");
        var matchDef = new QueueDefinition(
            QueueRegistry.QualifyName("matcharena", "3v3comp"),
            new MatchShape(2, 3), Policy(),
            ownerArenaName: "matcharena");

        r.Add(lobbyDef);
        r.Add(matchDef);

        Assert.Equal(2, r.Count);
        Assert.True(r.TryGet("lobby/3v3comp", out var got1));
        Assert.Equal("3v3comp", got1.BaseName);
        Assert.True(r.TryGet("matcharena/3v3comp", out var got2));
        Assert.Equal("3v3comp", got2.BaseName);
    }

    [Fact]
    public void RemoveByOwner_drops_only_that_arenas_queues()
    {
        var r = new QueueRegistry();
        r.Add(new QueueDefinition(QueueRegistry.QualifyName("lobby", "a"),
            new MatchShape(2, 2), Policy(), ownerArenaName: "lobby"));
        r.Add(new QueueDefinition(QueueRegistry.QualifyName("matcharena", "b"),
            new MatchShape(2, 2), Policy(), ownerArenaName: "matcharena"));

        var removed = r.RemoveByOwner("lobby");
        Assert.Single(removed);
        Assert.Equal("lobby/a", removed[0].UniqueId);
        Assert.Equal(1, r.Count);
        Assert.True(r.TryGet("matcharena/b", out _));
    }

    [Fact]
    public void TryComputeArenaContributionDiff_finds_added_and_removed()
    {
        var r = new QueueRegistry();
        r.Add(new QueueDefinition(QueueRegistry.QualifyName("lobby", "old1"),
            new MatchShape(2, 2), Policy(), ownerArenaName: "lobby"));
        r.Add(new QueueDefinition(QueueRegistry.QualifyName("lobby", "kept"),
            new MatchShape(2, 2), Policy(), ownerArenaName: "lobby"));

        var newDefs = new[]
        {
            new QueueDefinition(QueueRegistry.QualifyName("lobby", "kept"),
                new MatchShape(2, 2), Policy(), ownerArenaName: "lobby"),
            new QueueDefinition(QueueRegistry.QualifyName("lobby", "new1"),
                new MatchShape(2, 2), Policy(), ownerArenaName: "lobby"),
        };

        Assert.True(r.TryComputeArenaContributionDiff(
            "lobby", newDefs, out var wouldRemove, out var wouldAdd, out var errors));
        Assert.Empty(errors);
        Assert.Single(wouldRemove);
        Assert.Equal("lobby/old1", wouldRemove[0].UniqueId);
        Assert.Single(wouldAdd);
        Assert.Equal("lobby/new1", wouldAdd[0].UniqueId);

        // Registry not yet mutated -- preview must be side-effect-free.
        Assert.Equal(2, r.Count);
    }

    [Fact]
    public void ApplyArenaContribution_swaps_atomically()
    {
        var r = new QueueRegistry();
        r.Add(new QueueDefinition(QueueRegistry.QualifyName("lobby", "old1"),
            new MatchShape(2, 2), Policy(), ownerArenaName: "lobby"));
        r.Add(new QueueDefinition(QueueRegistry.QualifyName("other", "stays"),
            new MatchShape(2, 2), Policy(), ownerArenaName: "other"));

        var newDefs = new[]
        {
            new QueueDefinition(QueueRegistry.QualifyName("lobby", "new1"),
                new MatchShape(2, 2), Policy(), ownerArenaName: "lobby"),
        };
        r.ApplyArenaContribution("lobby", newDefs);

        Assert.Equal(2, r.Count);
        Assert.True(r.TryGet("lobby/new1", out _));
        Assert.True(r.TryGet("other/stays", out _));
        Assert.False(r.TryGet("lobby/old1", out _));
    }

    [Fact]
    public void ApplyArenaContribution_keeps_live_instance_and_waiters_of_unchanged_queues()
    {
        var r = new QueueRegistry();
        var old = new QueueDefinition(QueueRegistry.QualifyName("lobby", "4v4"),
            new MatchShape(2, 4), Policy(), ownerArenaName: "lobby");
        r.Add(old);
        Assert.True(old.Queue.Add(new QueueEntry(
            new("P1"), new(25, 0.0, 0, default), new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero))));

        // A reconcile re-parses config into fresh (empty) definition objects. Shape-matched
        // queues must keep the existing instance so waiters survive.
        var reparsed = new QueueDefinition(QueueRegistry.QualifyName("lobby", "4v4"),
            new MatchShape(2, 4), Policy(), ownerArenaName: "lobby");
        r.ApplyArenaContribution("lobby", new[] { reparsed });

        Assert.True(r.TryGet("lobby/4v4", out var got));
        Assert.Same(old, got);
        Assert.Equal(1, got.Queue.Count);
    }

    [Fact]
    public void ApplyArenaContribution_swaps_in_the_new_instance_when_the_queue_changed()
    {
        var r = new QueueRegistry();
        var old = new QueueDefinition(QueueRegistry.QualifyName("lobby", "4v4"),
            new MatchShape(2, 4), Policy(), ownerArenaName: "lobby", label: "4v4");
        r.Add(old);

        var changed = new QueueDefinition(QueueRegistry.QualifyName("lobby", "4v4"),
            new MatchShape(2, 4), Policy(), ownerArenaName: "lobby", label: "4v4 (Competitive)");
        r.ApplyArenaContribution("lobby", new[] { changed });

        Assert.True(r.TryGet("lobby/4v4", out var got));
        Assert.Same(changed, got);
    }

    [Fact]
    public void Diff_treats_label_change_as_a_swap_so_hot_reload_drops_waiters()
    {
        var r = new QueueRegistry();
        r.Add(new QueueDefinition(QueueRegistry.QualifyName("lobby", "4v4"),
            new MatchShape(2, 4), Policy(), ownerArenaName: "lobby", label: "4v4"));

        var newDefs = new[]
        {
            new QueueDefinition(QueueRegistry.QualifyName("lobby", "4v4"),
                new MatchShape(2, 4), Policy(), ownerArenaName: "lobby", label: "4v4 (Competitive)"),
        };
        Assert.True(r.TryComputeArenaContributionDiff(
            "lobby", newDefs, out var wouldRemove, out var wouldAdd, out _));
        Assert.Single(wouldRemove);
        Assert.Single(wouldAdd);
    }

    [Fact]
    public void Diff_flags_owner_mismatch_as_error()
    {
        var r = new QueueRegistry();
        var bad = new[]
        {
            new QueueDefinition(QueueRegistry.QualifyName("other", "x"),
                new MatchShape(2, 2), Policy(), ownerArenaName: "other"),
        };
        Assert.False(r.TryComputeArenaContributionDiff(
            "lobby", bad, out _, out _, out var errors));
        Assert.Contains(errors, e => e.Contains("OwnerArenaName"));
    }
}
