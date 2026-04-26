using ClashEngine.Core.Identity;
using ClashEngine.Core.Queue;
using ClashEngine.Core.Ratings;

namespace ClashEngine.Core.Tests.Queue;

public class PlayerQueueTests
{
    private static QueueEntry Entry(string name, double mu = 25, DateTimeOffset? at = null) =>
        new(
            new PlayerKey(name),
            new Rating(mu, 8.33, 0, default),
            at ?? new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));

    [Fact]
    public void New_queue_is_empty()
    {
        var q = new PlayerQueue("4v4");
        Assert.Equal(0, q.Count);
        Assert.False(q.TryPeek(out _));
        Assert.Empty(q.Snapshot());
    }

    [Fact]
    public void Add_returns_true_for_new_player()
    {
        var q = new PlayerQueue("4v4");
        Assert.True(q.Add(Entry("Alice")));
        Assert.Equal(1, q.Count);
        Assert.True(q.Contains(new PlayerKey("alice")));
    }

    [Fact]
    public void Add_returns_false_for_duplicate_by_name()
    {
        var q = new PlayerQueue("4v4");
        q.Add(Entry("Alice"));
        Assert.False(q.Add(Entry("alice")));
        Assert.Equal(1, q.Count);
    }

    [Fact]
    public void Add_preserves_FIFO()
    {
        var q = new PlayerQueue("4v4");
        var t0 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        q.Add(Entry("Alice", at: t0));
        q.Add(Entry("Bob", at: t0.AddSeconds(1)));
        q.Add(Entry("Carol", at: t0.AddSeconds(2)));

        var snap = q.Snapshot();
        Assert.Equal(3, snap.Count);
        Assert.Equal("Alice", snap[0].Player.Name);
        Assert.Equal("Bob", snap[1].Player.Name);
        Assert.Equal("Carol", snap[2].Player.Name);
    }

    [Fact]
    public void TryPeek_returns_head_without_removing()
    {
        var q = new PlayerQueue("4v4");
        q.Add(Entry("Alice"));
        q.Add(Entry("Bob"));

        Assert.True(q.TryPeek(out var entry));
        Assert.Equal("Alice", entry.Player.Name);
        Assert.Equal(2, q.Count);
    }

    [Fact]
    public void Remove_existing_player_returns_true_and_decrements_count()
    {
        var q = new PlayerQueue("4v4");
        q.Add(Entry("Alice"));
        q.Add(Entry("Bob"));

        Assert.True(q.Remove(new PlayerKey("ALICE")));
        Assert.Equal(1, q.Count);
        Assert.False(q.Contains(new PlayerKey("alice")));
    }

    [Fact]
    public void Remove_missing_player_returns_false()
    {
        var q = new PlayerQueue("4v4");
        q.Add(Entry("Alice"));
        Assert.False(q.Remove(new PlayerKey("Carol")));
        Assert.Equal(1, q.Count);
    }

    [Fact]
    public void Remove_preserves_FIFO_for_remaining_entries()
    {
        var q = new PlayerQueue("4v4");
        q.Add(Entry("Alice"));
        q.Add(Entry("Bob"));
        q.Add(Entry("Carol"));

        q.Remove(new PlayerKey("Bob"));

        var snap = q.Snapshot();
        Assert.Equal(new[] { "Alice", "Carol" }, snap.Select(e => e.Player.Name));
    }

    [Fact]
    public void Re_add_after_remove_succeeds()
    {
        var q = new PlayerQueue("4v4");
        q.Add(Entry("Alice"));
        q.Remove(new PlayerKey("Alice"));

        Assert.True(q.Add(Entry("Alice")));
        Assert.Equal(1, q.Count);
    }

    [Fact]
    public void Clear_empties_queue()
    {
        var q = new PlayerQueue("4v4");
        q.Add(Entry("Alice"));
        q.Add(Entry("Bob"));

        q.Clear();

        Assert.Equal(0, q.Count);
        Assert.False(q.TryPeek(out _));
        Assert.True(q.Add(Entry("Alice")));
    }

    [Fact]
    public void Snapshot_is_independent_of_internal_state()
    {
        var q = new PlayerQueue("4v4");
        q.Add(Entry("Alice"));
        var snap = q.Snapshot();

        q.Add(Entry("Bob"));

        Assert.Single(snap);
    }

    [Fact]
    public void Add_default_player_key_throws()
    {
        var q = new PlayerQueue("4v4");
        Assert.Throws<ArgumentException>(() => q.Add(default));
    }

    [Fact]
    public void Empty_queue_name_throws()
    {
        Assert.Throws<ArgumentException>(() => new PlayerQueue(""));
        Assert.Throws<ArgumentNullException>(() => new PlayerQueue(null!));
    }

    [Fact]
    public void Name_is_preserved()
    {
        Assert.Equal("4v4", new PlayerQueue("4v4").Name);
    }
}
