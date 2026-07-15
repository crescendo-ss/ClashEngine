using ClashEngine.Core.Identity;
using ClashEngine.Core.Matching;
using ClashEngine.Core.Queue;
using ClashEngine.Core.Ratings;

namespace ClashEngine.Core.Tests.Matching;

public class TeamBalancerTests
{
    private static QueueEntry E(string name, double mu, DateTimeOffset? at = null) =>
        new(new PlayerKey(name), new Rating(mu, 0.0, 0, default),
            at ?? new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));

    private static readonly IMatchQualityFunction Quality = new OrdinalSpreadQuality(normalizer: 50.0);

    [Fact]
    public void Returns_null_when_too_few_candidates()
    {
        var balancer = new TeamBalancer();
        var result = balancer.FindBest(new[] { E("A", 25), E("B", 25) }, new MatchShape(2, 2), Quality);
        Assert.Null(result);
    }

    [Fact]
    public void Returns_a_partition_of_correct_shape()
    {
        var balancer = new TeamBalancer();
        var candidates = new[] { E("A", 25), E("B", 25), E("C", 25), E("D", 25) };

        var result = balancer.FindBest(candidates, new MatchShape(2, 2), Quality);

        Assert.NotNull(result);
        Assert.Equal(2, result!.Teams.Count);
        Assert.All(result.Teams, t => Assert.Equal(2, t.Count));
    }

    [Fact]
    public void Includes_every_candidate_exactly_once()
    {
        var balancer = new TeamBalancer();
        var candidates = new[] { E("A", 25), E("B", 25), E("C", 25), E("D", 25) };

        var result = balancer.FindBest(candidates, new MatchShape(2, 2), Quality);

        var allPlayers = result!.Teams.SelectMany(t => t).ToList();
        Assert.Equal(4, allPlayers.Count);
        Assert.Equal(4, allPlayers.Distinct().Count());
        foreach (var c in candidates)
            Assert.Contains(c.Player, allPlayers);
    }

    [Fact]
    public void Identical_ratings_yield_quality_one()
    {
        var balancer = new TeamBalancer();
        var candidates = new[] { E("A", 25), E("B", 25), E("C", 25), E("D", 25) };

        var result = balancer.FindBest(candidates, new MatchShape(2, 2), Quality);

        Assert.Equal(1.0, result!.Quality, precision: 6);
        Assert.Equal(0.0, result.Imbalance, precision: 6);
    }

    [Fact]
    public void Picks_balanced_partition_for_skewed_candidates()
    {
        // Ordinals (sigma=0): A=40, B=30, C=20, D=10
        // Best partition for 2v2: {A,D} (mean=25) vs {B,C} (mean=25), imbalance=0
        // Worst would be {A,B} (mean=35) vs {C,D} (mean=15), imbalance=20
        var balancer = new TeamBalancer();
        var candidates = new[] { E("A", 40), E("B", 30), E("C", 20), E("D", 10) };

        var result = balancer.FindBest(candidates, new MatchShape(2, 2), Quality);

        Assert.Equal(0.0, result!.Imbalance, precision: 6);

        var teamA = result.Teams.First(t => t.Any(p => p == new PlayerKey("A")));
        Assert.Contains(new PlayerKey("D"), teamA);
    }

    [Fact]
    public void Uses_only_first_N_candidates_when_more_than_needed()
    {
        // 5 candidates for a 2v2: only the first 4 are used; C is ignored.
        var balancer = new TeamBalancer();
        var t0 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var candidates = new[]
        {
            E("A", 25, t0),
            E("B", 25, t0.AddSeconds(1)),
            E("C", 25, t0.AddSeconds(2)),
            E("D", 25, t0.AddSeconds(3)),
            E("E", 25, t0.AddSeconds(4)),
        };

        var result = balancer.FindBest(candidates, new MatchShape(2, 2), Quality);

        var allPlayers = result!.Teams.SelectMany(t => t).Select(p => p.Name).ToHashSet();
        Assert.Contains("A", allPlayers);
        Assert.Contains("B", allPlayers);
        Assert.Contains("C", allPlayers);
        Assert.Contains("D", allPlayers);
        Assert.DoesNotContain("E", allPlayers);
    }

    [Fact]
    public void Returns_null_when_max_ordinal_spread_violated()
    {
        var balancer = new TeamBalancer();
        var candidates = new[] { E("A", 40), E("B", 30), E("C", 20), E("D", 10) };  // spread = 30

        var resultSnug = balancer.FindBest(candidates, new MatchShape(2, 2, maxOrdinalSpread: 20), Quality);
        Assert.Null(resultSnug);

        var resultLoose = balancer.FindBest(candidates, new MatchShape(2, 2, maxOrdinalSpread: 50), Quality);
        Assert.NotNull(resultLoose);
    }

    [Fact]
    public void MaxLiabilityGap_rejects_partitions_with_lone_low_player()
    {
        // 4 players: 40, 40, 40, 5. Best partition for 2v2 keeps the 5 with a 40 — that team's
        // gap (between 5 and 40) is 35, which exceeds a 20-point cap.
        var balancer = new TeamBalancer();
        var candidates = new[] { E("A", 40), E("B", 40), E("C", 40), E("D", 5) };

        var snug = balancer.FindBest(candidates, new MatchShape(2, 2, maxLiabilityGap: 20), Quality);
        Assert.Null(snug);

        var loose = balancer.FindBest(candidates, new MatchShape(2, 2, maxLiabilityGap: 50), Quality);
        Assert.NotNull(loose);
    }

    [Fact]
    public void MaxLiabilityGap_allows_partition_when_low_player_paired_with_low_player()
    {
        // 4 players: 40, 40, 5, 5. Partitions like {40,5} vs {40,5} have gap 35 → rejected.
        // Partition {40,40} vs {5,5} has gap 0 on each team → accepted.
        var balancer = new TeamBalancer();
        var candidates = new[] { E("A", 40), E("B", 40), E("C", 5), E("D", 5) };

        var result = balancer.FindBest(candidates, new MatchShape(2, 2, maxLiabilityGap: 10), Quality);

        Assert.NotNull(result);
        // The accepted partition pairs the high players together and the low players together.
        var team0 = result!.Teams[0].Select(p => p.Name).ToHashSet();
        Assert.True(
            (team0.SetEquals(new[] { "A", "B" }) || team0.SetEquals(new[] { "C", "D" })),
            $"Expected high+high vs low+low partition; got {string.Join(",", team0)}");
    }

    [Fact]
    public void MaxLiabilityGap_does_not_apply_to_single_player_teams()
    {
        // 1v1: only one player per team, so the rule has nothing to reject against.
        var balancer = new TeamBalancer();
        var candidates = new[] { E("A", 40), E("B", 5) };

        var result = balancer.FindBest(candidates, new MatchShape(2, 1, maxLiabilityGap: 1), Quality);
        Assert.NotNull(result);
    }

    [Fact]
    public void MaxMuSpread_excludes_a_player_too_far_below_the_best()
    {
        // Best mu in any 4-player roster is 40; a 15-point cap sets the floor at 25, so the mu=10
        // player can never be in the match. With only these 4 candidates that leaves too few, so
        // no match forms. Widen the cap past 30 and the same roster becomes eligible.
        var balancer = new TeamBalancer();
        var candidates = new[] { E("A", 40), E("B", 38), E("C", 36), E("D", 10) };

        var blocked = balancer.FindBest(candidates, new MatchShape(2, 2, maxMuSpread: 15), Quality);
        Assert.Null(blocked);

        var allowed = balancer.FindBest(candidates, new MatchShape(2, 2, maxMuSpread: 40), Quality);
        Assert.NotNull(allowed);
    }

    [Fact]
    public void MaxMuSpread_forms_a_match_from_the_within_threshold_players_when_pool_is_deep_enough()
    {
        // Six waiting: four bunched near the top (within 8 mu of the best=42) plus two far below.
        // A 10-point cap floors eligibility at 32, so the mu=12 and mu=8 players are excluded and
        // the match is formed from the four top players -- the low pair keeps waiting.
        var balancer = new TeamBalancer();
        var t0 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var candidates = new[]
        {
            E("A", 42, t0), E("B", 40, t0.AddSeconds(1)), E("C", 38, t0.AddSeconds(2)),
            E("D", 34, t0.AddSeconds(3)), E("Low1", 12, t0.AddSeconds(4)), E("Low2", 8, t0.AddSeconds(5)),
        };

        var result = balancer.FindBest(
            candidates, new MatchShape(2, 2, maxMuSpread: 10), Quality, requireLongestWaiter: false);

        Assert.NotNull(result);
        var chosen = result!.Teams.SelectMany(t => t).Select(p => p.Name).ToHashSet();
        Assert.True(chosen.SetEquals(new[] { "A", "B", "C", "D" }),
            $"Expected the four top players; got {string.Join(",", chosen)}");
    }

    [Fact]
    public void MaxMuSpread_gates_on_mu_not_ordinal()
    {
        // Two players share the top mu (40) but one is far more uncertain (sigma 10 -> ordinal 10)
        // than the other (sigma 1 -> ordinal 37). An ordinal-spread cap would treat the uncertain
        // ace as "weak" and reject pairing them with the low player; the mu-spread cap does not,
        // because on raw skill they set the same bar. mu spread here is 40-30 = 10.
        var balancer = new TeamBalancer();
        var candidates = new[]
        {
            new QueueEntry(new PlayerKey("AceCertain"),   new Rating(40, 1.0, 0, default), default),
            new QueueEntry(new PlayerKey("AceUncertain"), new Rating(40, 10.0, 0, default), default),
            new QueueEntry(new PlayerKey("MidA"),         new Rating(30, 1.0, 0, default), default),
            new QueueEntry(new PlayerKey("MidB"),         new Rating(30, 1.0, 0, default), default),
        };

        // mu spread = 10 -> a 12-point mu cap admits the roster...
        Assert.NotNull(balancer.FindBest(candidates, new MatchShape(2, 2, maxMuSpread: 12), Quality));
        // ...even though the ordinal spread (37 - 10 = 27) would have failed a comparable ordinal cap.
        Assert.Null(balancer.FindBest(candidates, new MatchShape(2, 2, maxOrdinalSpread: 12), Quality));
    }

    [Fact]
    public void MaxMuSpread_is_skipped_when_enforceMuSpread_is_false()
    {
        // 40,38,36,10: spread 30 fails a 15-point cap, so the enforced search forms nothing...
        var balancer = new TeamBalancer();
        var candidates = new[] { E("A", 40), E("B", 38), E("C", 36), E("D", 10) };
        var shape = new MatchShape(2, 2, maxMuSpread: 15);

        Assert.Null(balancer.FindBest(candidates, shape, Quality, enforceMuSpread: true));
        // ...but the relaxation escape hatch forgoes the cap and forms the best available split.
        Assert.NotNull(balancer.FindBest(candidates, shape, Quality, enforceMuSpread: false));
    }

    [Fact]
    public void Three_team_partition_works()
    {
        var balancer = new TeamBalancer();
        var candidates = new[]
        {
            E("A", 30), E("B", 30),
            E("C", 25), E("D", 25),
            E("E", 20), E("F", 20),
        };

        var result = balancer.FindBest(candidates, new MatchShape(3, 2), Quality);

        Assert.NotNull(result);
        Assert.Equal(3, result!.Teams.Count);
        Assert.All(result.Teams, t => Assert.Equal(2, t.Count));

        // Best partition: pair high+low to balance: {A,F}(25) {B,E}(25) {C,D}(25)
        Assert.Equal(0.0, result.Imbalance, precision: 6);
    }

    [Fact]
    public void Quality_is_in_zero_to_one_for_any_partition()
    {
        var balancer = new TeamBalancer();
        var rng = new Random(7);
        var candidates = Enumerable.Range(0, 8)
            .Select(i => E($"P{i}", 10 + rng.NextDouble() * 30))
            .ToArray();

        var result = balancer.FindBest(candidates, new MatchShape(2, 4), Quality);

        Assert.NotNull(result);
        Assert.InRange(result!.Quality, 0.0, 1.0);
        Assert.True(result.Imbalance >= 0);
    }

    [Fact]
    public void Enumerate_partitions_count_matches_combinatorics()
    {
        // 6 players in 3 teams of 2: 6! / (2!^3 * 3!) = 720 / (8 * 6) = 15
        var partitions = TeamBalancer.EnumeratePartitions(6, 3, 2).Count();
        Assert.Equal(15, partitions);

        // 8 players in 2 teams of 4: 8! / (4!^2 * 2!) = 40320 / (576 * 2) = 35
        partitions = TeamBalancer.EnumeratePartitions(8, 2, 4).Count();
        Assert.Equal(35, partitions);

        // 4 players in 2 teams of 2: 4! / (2!^2 * 2!) = 24 / 8 = 3
        partitions = TeamBalancer.EnumeratePartitions(4, 2, 2).Count();
        Assert.Equal(3, partitions);
    }

    [Fact]
    public void Enumerated_partitions_are_unique_and_well_formed()
    {
        var partitions = TeamBalancer.EnumeratePartitions(6, 3, 2).ToList();

        // Canonical ordering: each partition's teams should be flattened-sorted to detect duplicates.
        var canonical = new HashSet<string>();
        foreach (var p in partitions)
        {
            // Each team has size 2
            Assert.All(p, t => Assert.Equal(2, t.Length));
            // All indices 0..5 used exactly once
            var flat = p.SelectMany(t => t).OrderBy(x => x).ToArray();
            Assert.Equal(new[] { 0, 1, 2, 3, 4, 5 }, flat);

            // Build a canonical signature: sort each team, then sort the team list.
            var sig = string.Join("|", p.Select(t => string.Join(",", t.OrderBy(x => x))).OrderBy(s => s));
            Assert.True(canonical.Add(sig), $"duplicate partition: {sig}");
        }
    }

    [Fact]
    public void Null_arguments_throw()
    {
        var balancer = new TeamBalancer();
        Assert.Throws<ArgumentNullException>(() => balancer.FindBest(null!, new MatchShape(2, 2), Quality));
        Assert.Throws<ArgumentNullException>(() => balancer.FindBest(Array.Empty<QueueEntry>(), null!, Quality));
        Assert.Throws<ArgumentNullException>(() => balancer.FindBest(Array.Empty<QueueEntry>(), new MatchShape(2, 2), null!));
    }
}
