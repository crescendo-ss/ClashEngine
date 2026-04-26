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

        Assert.Equal("4v4", def.Name);
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
        Assert.Equal("4v4", def.Name);
    }

    [Fact]
    public void Definitions_iterates_in_registration_order()
    {
        var r = new QueueRegistry();
        r.Register("a", new MatchShape(2, 2), Policy());
        r.Register("b", new MatchShape(2, 2), Policy());
        r.Register("c", new MatchShape(2, 2), Policy());

        var names = r.Definitions.Select(d => d.Name).ToArray();
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
}
