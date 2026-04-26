using ClashEngine.Core.Identity;
using ClashEngine.Core.Matches;
using ClashEngine.Core.Ratings;

namespace ClashEngine.Core.Tests.Ratings;

public class RatingUpdaterTests
{
    private static PlayerKey K(string n) => new(n);
    private static readonly GameTypeId G = new(1);
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Equal_teams_winner_mu_rises_loser_falls()
    {
        var store = new InMemoryRatingStore();
        store.Set(K("A"), G, Rating.Default);
        store.Set(K("B"), G, Rating.Default);
        store.Set(K("C"), G, Rating.Default);
        store.Set(K("D"), G, Rating.Default);

        var outcome = new MatchOutcome(
            Guid.NewGuid(), G,
            new[]
            {
                new RankedTeam(1, new[] { K("A"), K("B") }, Score: 5),
                new RankedTeam(2, new[] { K("C"), K("D") }, Score: 3),
            },
            Array.Empty<PlayerKey>(),
            MatchState.Completed,
            T0);

        new RatingUpdater().ApplyOutcome(store, outcome, T0);

        var winnerA = store.Get(K("A"), G);
        var loserC = store.Get(K("C"), G);

        Assert.True(winnerA.Mu > Rating.Default.Mu, "winner mu should rise");
        Assert.True(loserC.Mu < Rating.Default.Mu, "loser mu should fall");
        Assert.Equal(1u, winnerA.GamesPlayed);
        Assert.Equal(1u, loserC.GamesPlayed);
        Assert.Equal(T0, winnerA.LastSeen);
    }

    [Fact]
    public void Sigma_decreases_after_a_match()
    {
        var store = new InMemoryRatingStore();
        foreach (var name in new[] { "A", "B", "C", "D" })
            store.Set(K(name), G, Rating.Default);

        var outcome = new MatchOutcome(
            Guid.NewGuid(), G,
            new[]
            {
                new RankedTeam(1, new[] { K("A"), K("B") }, Score: 5),
                new RankedTeam(2, new[] { K("C"), K("D") }, Score: 3),
            },
            Array.Empty<PlayerKey>(),
            MatchState.Completed,
            T0);

        new RatingUpdater().ApplyOutcome(store, outcome, T0);

        Assert.True(store.Get(K("A"), G).Sigma < Rating.Default.Sigma);
        Assert.True(store.Get(K("C"), G).Sigma < Rating.Default.Sigma);
    }

    [Fact]
    public void New_players_get_default_baseline_before_update()
    {
        // No prior Set() calls.
        var store = new InMemoryRatingStore();

        var outcome = new MatchOutcome(
            Guid.NewGuid(), G,
            new[]
            {
                new RankedTeam(1, new[] { K("X") }, 1),
                new RankedTeam(2, new[] { K("Y") }, 0),
            },
            Array.Empty<PlayerKey>(),
            MatchState.Completed,
            T0);

        new RatingUpdater().ApplyOutcome(store, outcome, T0);

        // Both players now have ratings recorded (one rose, one fell).
        Assert.True(store.TryGet(K("X"), G, out _));
        Assert.True(store.TryGet(K("Y"), G, out _));
        Assert.True(store.Get(K("X"), G).Mu > Rating.Default.Mu);
        Assert.True(store.Get(K("Y"), G).Mu < Rating.Default.Mu);
    }

    [Fact]
    public void Cancelled_outcome_does_not_change_ratings()
    {
        var store = new InMemoryRatingStore();
        store.Set(K("A"), G, new Rating(25, 8.33, 7, T0));
        var snapshotBefore = store.Get(K("A"), G);

        var outcome = new MatchOutcome(
            Guid.NewGuid(), G,
            Array.Empty<RankedTeam>(),
            new[] { K("A") },
            MatchState.Cancelled,
            T0);

        new RatingUpdater().ApplyOutcome(store, outcome, T0);

        Assert.Equal(snapshotBefore, store.Get(K("A"), G));
    }

    [Fact]
    public void Abandoned_outcome_still_updates_ratings()
    {
        var store = new InMemoryRatingStore();
        foreach (var name in new[] { "A", "B", "C", "D" })
            store.Set(K(name), G, Rating.Default);

        var outcome = new MatchOutcome(
            Guid.NewGuid(), G,
            new[]
            {
                new RankedTeam(1, new[] { K("A"), K("B") }, 5),
                new RankedTeam(2, new[] { K("C"), K("D") }, 0),
            },
            new[] { K("C"), K("D") },
            MatchState.Abandoned,
            T0);

        new RatingUpdater().ApplyOutcome(store, outcome, T0);

        Assert.True(store.Get(K("A"), G).Mu > Rating.Default.Mu);
        Assert.True(store.Get(K("C"), G).Mu < Rating.Default.Mu);
    }

    [Fact]
    public void GamesPlayed_increments_by_one_per_match()
    {
        var store = new InMemoryRatingStore();
        store.Set(K("A"), G, new Rating(25, 8.33, 5, T0));
        store.Set(K("B"), G, Rating.Default);

        var outcome = new MatchOutcome(
            Guid.NewGuid(), G,
            new[]
            {
                new RankedTeam(1, new[] { K("A") }, 1),
                new RankedTeam(2, new[] { K("B") }, 0),
            },
            Array.Empty<PlayerKey>(),
            MatchState.Completed,
            T0);

        new RatingUpdater().ApplyOutcome(store, outcome, T0);

        Assert.Equal(6u, store.Get(K("A"), G).GamesPlayed);
        Assert.Equal(1u, store.Get(K("B"), G).GamesPlayed);
    }

    [Fact]
    public void Different_game_types_are_isolated()
    {
        var store = new InMemoryRatingStore();
        var g2 = new GameTypeId(2);
        store.Set(K("A"), G, new Rating(25, 8.33, 0, default));
        store.Set(K("A"), g2, new Rating(40, 1.0, 100, default));  // unrelated
        store.Set(K("B"), G, Rating.Default);

        var outcome = new MatchOutcome(
            Guid.NewGuid(), G,
            new[]
            {
                new RankedTeam(1, new[] { K("A") }, 1),
                new RankedTeam(2, new[] { K("B") }, 0),
            },
            Array.Empty<PlayerKey>(),
            MatchState.Completed,
            T0);

        new RatingUpdater().ApplyOutcome(store, outcome, T0);

        Assert.Equal(40.0, store.Get(K("A"), g2).Mu);  // unchanged
        Assert.Equal(100u, store.Get(K("A"), g2).GamesPlayed);
    }

    [Fact]
    public void Null_arguments_throw()
    {
        var u = new RatingUpdater();
        var outcome = new MatchOutcome(
            Guid.NewGuid(), G, Array.Empty<RankedTeam>(),
            Array.Empty<PlayerKey>(), MatchState.Cancelled, T0);
        Assert.Throws<ArgumentNullException>(() => u.ApplyOutcome(null!, outcome, T0));
        Assert.Throws<ArgumentNullException>(() => u.ApplyOutcome(new InMemoryRatingStore(), null!, T0));
    }

    private static MatchOutcome SimpleOutcome() =>
        new(Guid.NewGuid(), G,
            new[]
            {
                new RankedTeam(1, new[] { K("A") }, 1),
                new RankedTeam(2, new[] { K("B") }, 0),
            },
            Array.Empty<PlayerKey>(),
            MatchState.Completed,
            T0);

    [Fact]
    public void Weight_zero_records_game_but_does_not_change_mu_or_sigma()
    {
        var store = new InMemoryRatingStore();
        var initial = new Rating(25, 8.33, 4, default);
        store.Set(K("A"), G, initial);
        store.Set(K("B"), G, initial);

        new RatingUpdater().ApplyOutcome(store, SimpleOutcome(), T0, weight: 0);

        Assert.Equal(initial.Mu, store.Get(K("A"), G).Mu, precision: 9);
        Assert.Equal(initial.Sigma, store.Get(K("A"), G).Sigma, precision: 9);
        Assert.Equal(5u, store.Get(K("A"), G).GamesPlayed);  // games still increment
    }

    [Fact]
    public void Weight_half_produces_half_the_mu_change_of_full_weight()
    {
        var fullStore = new InMemoryRatingStore();
        var halfStore = new InMemoryRatingStore();
        foreach (var s in new[] { fullStore, halfStore })
        {
            s.Set(K("A"), G, Rating.Default);
            s.Set(K("B"), G, Rating.Default);
        }

        new RatingUpdater().ApplyOutcome(fullStore, SimpleOutcome(), T0, weight: 1.0);
        new RatingUpdater().ApplyOutcome(halfStore, SimpleOutcome(), T0, weight: 0.5);

        double fullDelta = fullStore.Get(K("A"), G).Mu - Rating.Default.Mu;
        double halfDelta = halfStore.Get(K("A"), G).Mu - Rating.Default.Mu;

        Assert.Equal(fullDelta * 0.5, halfDelta, precision: 6);
    }

    [Fact]
    public void Weight_outside_zero_one_throws()
    {
        var u = new RatingUpdater();
        var store = new InMemoryRatingStore();
        var outcome = SimpleOutcome();
        Assert.Throws<ArgumentOutOfRangeException>(() => u.ApplyOutcome(store, outcome, T0, weight: -0.1));
        Assert.Throws<ArgumentOutOfRangeException>(() => u.ApplyOutcome(store, outcome, T0, weight: 1.1));
    }
}
