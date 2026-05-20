using ClashEngine.Core.Identity;
using ClashEngine.Core.Ratings;

namespace ClashEngine.Core.Tests.Ratings;

public class InMemoryRatingStoreTests
{
    private static PlayerKey K(string name) => new(name);
    private const string G1 = "gt1";
    private const string G2 = "gt2";

    [Fact]
    public void Get_returns_Default_for_unknown_player()
    {
        var store = new InMemoryRatingStore();
        Assert.Equal(Rating.Default, store.Get(K("Alice"), G1));
    }

    [Fact]
    public void TryGet_returns_false_for_unknown_player()
    {
        var store = new InMemoryRatingStore();
        Assert.False(store.TryGet(K("Alice"), G1, out _));
    }

    [Fact]
    public void Set_then_Get_round_trips()
    {
        var store = new InMemoryRatingStore();
        var when = new DateTimeOffset(2026, 4, 1, 0, 0, 0, TimeSpan.Zero);
        var rating = new Rating(28.0, 6.5, 12, when);

        store.Set(K("Alice"), G1, rating);

        Assert.Equal(rating, store.Get(K("Alice"), G1));
        Assert.True(store.TryGet(K("Alice"), G1, out var got));
        Assert.Equal(rating, got);
    }

    [Fact]
    public void Set_overwrites_existing_rating()
    {
        var store = new InMemoryRatingStore();
        store.Set(K("Alice"), G1, new Rating(20, 8, 1, default));
        store.Set(K("Alice"), G1, new Rating(30, 4, 5, default));

        var got = store.Get(K("Alice"), G1);
        Assert.Equal(30.0, got.Mu);
        Assert.Equal(5u, got.GamesPlayed);
    }

    [Fact]
    public void Different_game_types_for_same_player_are_isolated()
    {
        var store = new InMemoryRatingStore();
        store.Set(K("Alice"), G1, new Rating(30, 4, 0, default));
        store.Set(K("Alice"), G2, new Rating(20, 8, 0, default));

        Assert.Equal(30.0, store.Get(K("Alice"), G1).Mu);
        Assert.Equal(20.0, store.Get(K("Alice"), G2).Mu);
    }

    [Fact]
    public void Different_players_are_isolated()
    {
        var store = new InMemoryRatingStore();
        store.Set(K("Alice"), G1, new Rating(30, 4, 0, default));
        store.Set(K("Bob"), G1, new Rating(20, 8, 0, default));

        Assert.Equal(30.0, store.Get(K("Alice"), G1).Mu);
        Assert.Equal(20.0, store.Get(K("Bob"), G1).Mu);
    }

    [Fact]
    public void Player_key_lookup_is_case_insensitive()
    {
        var store = new InMemoryRatingStore();
        store.Set(K("Alice"), G1, new Rating(30, 4, 0, default));
        Assert.Equal(30.0, store.Get(K("ALICE"), G1).Mu);
        Assert.True(store.TryGet(K("alice"), G1, out _));
    }

    [Fact]
    public void Remove_existing_rating_returns_true_and_clears_row()
    {
        var store = new InMemoryRatingStore();
        store.Set(K("Alice"), G1, new Rating(30, 4, 0, default));

        Assert.True(store.Remove(K("Alice"), G1));
        Assert.False(store.TryGet(K("Alice"), G1, out _));
        Assert.Equal(Rating.Default, store.Get(K("Alice"), G1));
    }

    [Fact]
    public void Remove_unknown_returns_false()
    {
        var store = new InMemoryRatingStore();
        Assert.False(store.Remove(K("Ghost"), G1));
    }

    [Fact]
    public void Remove_only_affects_specified_game_type()
    {
        var store = new InMemoryRatingStore();
        store.Set(K("Alice"), G1, new Rating(30, 4, 0, default));
        store.Set(K("Alice"), G2, new Rating(20, 8, 0, default));

        store.Remove(K("Alice"), G1);

        Assert.False(store.TryGet(K("Alice"), G1, out _));
        Assert.True(store.TryGet(K("Alice"), G2, out _));
    }

    [Fact]
    public void Snapshot_reflects_current_state()
    {
        var store = new InMemoryRatingStore();
        store.Set(K("Alice"), G1, new Rating(30, 4, 0, default));
        store.Set(K("Bob"), G1, new Rating(20, 8, 0, default));
        store.Set(K("Alice"), G2, new Rating(15, 7, 0, default));

        var snap = store.Snapshot();
        Assert.Equal(3, snap.Count);
        Assert.Contains(snap, e => e.Player == K("Alice") && e.GameType == G1 && e.Value.Mu == 30.0);
        Assert.Contains(snap, e => e.Player == K("Bob") && e.GameType == G1 && e.Value.Mu == 20.0);
        Assert.Contains(snap, e => e.Player == K("Alice") && e.GameType == G2 && e.Value.Mu == 15.0);
    }

    [Fact]
    public void Snapshot_is_independent_of_later_writes()
    {
        var store = new InMemoryRatingStore();
        store.Set(K("Alice"), G1, new Rating(30, 4, 0, default));
        var snap = store.Snapshot();

        store.Set(K("Bob"), G1, new Rating(20, 8, 0, default));

        Assert.Single(snap);
    }

    [Fact]
    public void Empty_snapshot_is_empty_list()
    {
        var store = new InMemoryRatingStore();
        Assert.Empty(store.Snapshot());
    }
}
