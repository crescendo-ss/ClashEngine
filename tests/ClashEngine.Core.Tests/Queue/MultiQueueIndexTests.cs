using ClashEngine.Core.Identity;
using ClashEngine.Core.Queue;

namespace ClashEngine.Core.Tests.Queue;

public class MultiQueueIndexTests
{
    private static PlayerKey K(string name) => new(name);

    [Fact]
    public void New_index_is_empty()
    {
        var idx = new MultiQueueIndex();
        Assert.Equal(0, idx.PlayerCount);
        Assert.False(idx.IsTrackingAnywhere(K("Alice")));
        Assert.Empty(idx.QueuesFor(K("Alice")));
    }

    [Fact]
    public void Add_returns_true_for_new_pair()
    {
        var idx = new MultiQueueIndex();
        Assert.True(idx.Add(K("Alice"), "4v4"));
        Assert.True(idx.IsTrackingAnywhere(K("Alice")));
        Assert.True(idx.Contains(K("Alice"), "4v4"));
    }

    [Fact]
    public void Add_returns_false_for_duplicate_pair()
    {
        var idx = new MultiQueueIndex();
        idx.Add(K("Alice"), "4v4");
        Assert.False(idx.Add(K("Alice"), "4v4"));
        Assert.False(idx.Add(K("alice"), "4V4"));
    }

    [Fact]
    public void Add_same_player_to_multiple_queues_tracks_all()
    {
        var idx = new MultiQueueIndex();
        idx.Add(K("Alice"), "4v4");
        idx.Add(K("Alice"), "duel");
        idx.Add(K("Alice"), "2v2");

        var queues = idx.QueuesFor(K("Alice"));
        Assert.Equal(3, queues.Count);
        Assert.Contains("4v4", queues);
        Assert.Contains("duel", queues);
        Assert.Contains("2v2", queues);
    }

    [Fact]
    public void Queue_name_lookup_is_case_insensitive()
    {
        var idx = new MultiQueueIndex();
        idx.Add(K("Alice"), "4v4");
        Assert.True(idx.Contains(K("Alice"), "4V4"));
    }

    [Fact]
    public void Remove_existing_pair_returns_true()
    {
        var idx = new MultiQueueIndex();
        idx.Add(K("Alice"), "4v4");
        idx.Add(K("Alice"), "duel");

        Assert.True(idx.Remove(K("Alice"), "4v4"));
        Assert.False(idx.Contains(K("Alice"), "4v4"));
        Assert.True(idx.IsTrackingAnywhere(K("Alice")));
    }

    [Fact]
    public void Remove_missing_pair_returns_false()
    {
        var idx = new MultiQueueIndex();
        idx.Add(K("Alice"), "4v4");
        Assert.False(idx.Remove(K("Alice"), "duel"));
        Assert.False(idx.Remove(K("Bob"), "4v4"));
    }

    [Fact]
    public void Remove_last_queue_drops_player_from_index()
    {
        var idx = new MultiQueueIndex();
        idx.Add(K("Alice"), "4v4");
        idx.Remove(K("Alice"), "4v4");

        Assert.False(idx.IsTrackingAnywhere(K("Alice")));
        Assert.Equal(0, idx.PlayerCount);
    }

    [Fact]
    public void RemoveAll_returns_every_queue_player_was_in()
    {
        var idx = new MultiQueueIndex();
        idx.Add(K("Alice"), "4v4");
        idx.Add(K("Alice"), "duel");
        idx.Add(K("Alice"), "2v2");
        idx.Add(K("Bob"), "4v4");

        var removed = idx.RemoveAll(K("Alice"));

        Assert.Equal(3, removed.Count);
        Assert.Contains("4v4", removed);
        Assert.Contains("duel", removed);
        Assert.Contains("2v2", removed);

        Assert.False(idx.IsTrackingAnywhere(K("Alice")));
        Assert.True(idx.IsTrackingAnywhere(K("Bob")));
    }

    [Fact]
    public void RemoveAll_for_unknown_player_returns_empty()
    {
        var idx = new MultiQueueIndex();
        Assert.Empty(idx.RemoveAll(K("Ghost")));
    }

    [Fact]
    public void Different_players_are_isolated()
    {
        var idx = new MultiQueueIndex();
        idx.Add(K("Alice"), "4v4");
        idx.Add(K("Bob"), "4v4");

        Assert.True(idx.Contains(K("Alice"), "4v4"));
        Assert.True(idx.Contains(K("Bob"), "4v4"));
        Assert.Equal(2, idx.PlayerCount);
    }

    [Fact]
    public void Add_default_player_throws()
    {
        var idx = new MultiQueueIndex();
        Assert.Throws<ArgumentException>(() => idx.Add(default, "4v4"));
    }

    [Fact]
    public void Add_with_empty_or_null_queue_name_throws()
    {
        var idx = new MultiQueueIndex();
        Assert.Throws<ArgumentException>(() => idx.Add(K("Alice"), ""));
        Assert.Throws<ArgumentNullException>(() => idx.Add(K("Alice"), null!));
    }
}
