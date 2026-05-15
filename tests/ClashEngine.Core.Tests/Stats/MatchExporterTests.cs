using ClashEngine.Core.Identity;
using ClashEngine.Core.Matches;
using ClashEngine.Core.Stats;

namespace ClashEngine.Core.Tests.Stats;

public class MatchExporterTests
{
    private static PlayerKey K(string n) => new(n);

    private static WeaponEnergyConfig Energy() => new(
        new Dictionary<WeaponKind, (int, int)> { [WeaponKind.Bullet] = (100, 0) },
        multifireBulletEnergy: 0);

    private static StatsRecorder Make4Player()
    {
        var r = new StatsRecorder(new DamageDecay(halfLifeTicks: 200));
        r.RegisterPlayer(K("A"), 0, 1000, 0.0, Energy(), 0);
        r.RegisterPlayer(K("B"), 0, 1000, 0.0, Energy(), 0);
        r.RegisterPlayer(K("C"), 1, 1000, 0.0, Energy(), 0);
        r.RegisterPlayer(K("D"), 1, 1000, 0.0, Energy(), 0);
        return r;
    }

    private static IReadOnlyList<IReadOnlyList<PlayerKey>> Teams() => new[]
    {
        new[] { K("A"), K("B") },
        new[] { K("C"), K("D") },
    };

    private static MatchOutcome Outcome(Guid matchId) =>
        new(matchId,
            GameType: new GameTypeId(1),
            RankedTeams: new[]
            {
                new RankedTeam(1, new[] { K("A"), K("B") }, 5),
                new RankedTeam(2, new[] { K("C"), K("D") }, 2),
            },
            AbandonedBy: Array.Empty<PlayerKey>(),
            FinalState: MatchState.Completed,
            EndedAt: new DateTimeOffset(2026, 4, 25, 12, 0, 0, TimeSpan.Zero));

    [Fact]
    public void Build_includes_all_registered_players()
    {
        var r = Make4Player();
        var matchId = Guid.NewGuid();
        var payload = MatchExporter.Build(
            matchId, queueName: "4v4", queueLabel: null, gameType: 1, arena: "tdm", teams: Teams(), startedAt: null,
            recorder: r, outcome: Outcome(matchId));

        Assert.Equal(4, payload.Participants.Count);
        Assert.Contains(payload.Participants, p => p.Name == "A");
        Assert.Contains(payload.Participants, p => p.Name == "C");
    }

    [Fact]
    public void Build_assigns_team_index_from_input_teams_not_outcome_rank()
    {
        var r = Make4Player();
        var matchId = Guid.NewGuid();
        var payload = MatchExporter.Build(
            matchId, "4v4", null, 1, "tdm", Teams(), null, r, Outcome(matchId));

        var a = payload.Participants.Single(p => p.Name == "A");
        var c = payload.Participants.Single(p => p.Name == "C");
        Assert.Equal(0, a.TeamIndex);
        Assert.Equal(1, c.TeamIndex);
    }

    [Fact]
    public void Build_serializes_per_weapon_and_items_and_lives()
    {
        var r = Make4Player();
        r.OnSpawn(K("A"), 0);
        r.OnWeaponFired(K("A"), WeaponKind.Bullet, 1, false, 100);
        r.OnDamage(K("C"), K("A"), 200, WeaponKind.Bullet, 0, 100);
        r.OnItemUsed(K("A"), ItemKind.Repel, 110);
        r.OnKill(K("C"), K("A"), 120);

        var matchId = Guid.NewGuid();
        var payload = MatchExporter.Build(
            matchId, "4v4", null, 1, "tdm", Teams(), null, r, Outcome(matchId));

        var a = payload.Participants.Single(p => p.Name == "A");
        Assert.Equal(1, a.PerWeapon["Bullet"].FireCount);
        Assert.Equal(1, a.PerWeapon["Bullet"].HitCount);
        Assert.Equal(1, a.ItemUses["Repel"]);
        Assert.NotEmpty(a.Lives);
    }

    [Fact]
    public void Build_includes_outcome_ranked_teams()
    {
        var r = Make4Player();
        var matchId = Guid.NewGuid();
        var payload = MatchExporter.Build(
            matchId, "4v4", null, 1, "tdm", Teams(), null, r, Outcome(matchId));

        Assert.Equal(2, payload.Teams.Count);
        Assert.Equal(1, payload.Teams[0].Rank);
        Assert.Equal(5, payload.Teams[0].Score);
        Assert.Contains("A", payload.Teams[0].Players);
    }

    [Fact]
    public void Build_carries_match_metadata_through()
    {
        var r = Make4Player();
        var matchId = Guid.NewGuid();
        var startedAt = new DateTimeOffset(2026, 4, 25, 11, 30, 0, TimeSpan.Zero);
        var payload = MatchExporter.Build(
            matchId, "lobby/casual_4v4", queueLabel: "4v4 (Casual)", gameType: 7, arena: "tdm_pro",
            Teams(), startedAt, r, Outcome(matchId));

        Assert.Equal(MatchExporter.CurrentSchemaVersion, payload.SchemaVersion);
        Assert.Equal(matchId, payload.MatchId);
        Assert.Equal("lobby/casual_4v4", payload.QueueName);
        Assert.Equal("4v4 (Casual)", payload.QueueLabel);
        Assert.Equal(7u, payload.GameType);
        Assert.Equal("tdm_pro", payload.Arena);
        Assert.Equal(startedAt, payload.StartedAt);
        Assert.Equal("Completed", payload.FinalState);
    }

    [Fact]
    public void Build_attaches_rating_at_start_when_provided()
    {
        var r = Make4Player();
        var ratings = new Dictionary<PlayerKey, RatingPayload>
        {
            [K("A")] = new RatingPayload(Mu: 28.5, Sigma: 6.2, GamesPlayed: 42),
            [K("C")] = new RatingPayload(Mu: 22.0, Sigma: 7.0, GamesPlayed: 10),
        };

        var matchId = Guid.NewGuid();
        var payload = MatchExporter.Build(
            matchId, "4v4", null, 1, "tdm", Teams(), null, r, Outcome(matchId),
            ratingsAtStart: ratings);

        var a = payload.Participants.Single(p => p.Name == "A");
        Assert.NotNull(a.RatingAtStart);
        Assert.Equal(28.5, a.RatingAtStart!.Mu);
        Assert.Equal(42u, a.RatingAtStart.GamesPlayed);

        var b = payload.Participants.Single(p => p.Name == "B");
        Assert.Null(b.RatingAtStart); // not in the dict
    }

    [Fact]
    public void Build_emits_abandoned_by_with_players_from_either_team()
    {
        var r = Make4Player();
        var matchId = Guid.NewGuid();
        // A player on each team abandoned -- the schema's abandonedBy is a flat list, not
        // partitioned by team, so both should land in the same array.
        var abandonedOutcome = new MatchOutcome(
            matchId,
            GameType: new GameTypeId(1),
            RankedTeams: new[]
            {
                new RankedTeam(1, new[] { K("A"), K("B") }, 5),
                new RankedTeam(2, new[] { K("C"), K("D") }, 2),
            },
            AbandonedBy: new[] { K("B"), K("D") },
            FinalState: MatchState.Abandoned,
            EndedAt: new DateTimeOffset(2026, 4, 25, 12, 0, 0, TimeSpan.Zero));

        var payload = MatchExporter.Build(
            matchId, "4v4", null, 1, "tdm", Teams(), null, r, abandonedOutcome);

        Assert.Equal("Abandoned", payload.FinalState);
        Assert.Equal(2, payload.AbandonedBy.Count);
        Assert.Contains("B", payload.AbandonedBy);
        Assert.Contains("D", payload.AbandonedBy);
    }

    [Fact]
    public void Build_emits_empty_abandoned_by_for_non_abandoned_match()
    {
        var r = Make4Player();
        var matchId = Guid.NewGuid();
        var payload = MatchExporter.Build(
            matchId, "4v4", null, 1, "tdm", Teams(), null, r, Outcome(matchId));
        Assert.Empty(payload.AbandonedBy);
    }

    [Fact]
    public void Build_carries_recording_path()
    {
        var r = Make4Player();
        var matchId = Guid.NewGuid();
        var payload = MatchExporter.Build(
            matchId, "4v4", null, 1, "tdm", Teams(), null, r, Outcome(matchId),
            recordingPath: "replays/2026/04/25/match_abc.ssrec");
        Assert.Equal("replays/2026/04/25/match_abc.ssrec", payload.RecordingPath);
    }

    [Fact]
    public void Build_emits_rating_change_when_pre_and_post_provided()
    {
        var r = Make4Player();
        var ratings = new Dictionary<PlayerKey, RatingPayload>
        {
            [K("A")] = new RatingPayload(Mu: 25.0, Sigma: 8.333, GamesPlayed: 0),
        };
        // post Ordinal = 26.0 - 3*7.0 = 5.0; pre Ordinal = 25.0 - 3*8.333 = 0.001;
        // delta = (5.0 - 0.001) * 10.0 ≈ 49.99
        var post = new Dictionary<PlayerKey, double>
        {
            [K("A")] = 5.0,
        };

        var matchId = Guid.NewGuid();
        var payload = MatchExporter.Build(
            matchId, "4v4", null, 1, "tdm", Teams(), null, r, Outcome(matchId),
            ratingsAtStart: ratings,
            postOrdinalByPlayer: post);

        var a = payload.Participants.Single(p => p.Name == "A");
        Assert.NotNull(a.RatingChange);
        Assert.Equal(49.99, a.RatingChange!.Value, precision: 2);

        // No pre-rating -> null delta even when post is known.
        var b = payload.Participants.Single(p => p.Name == "B");
        Assert.Null(b.RatingChange);

        // No post-rating -> null delta even when pre is known.
        var ratingsOnlyA = new Dictionary<PlayerKey, RatingPayload> { [K("A")] = ratings[K("A")] };
        var noPost = MatchExporter.Build(
            matchId, "4v4", null, 1, "tdm", Teams(), null, r, Outcome(matchId),
            ratingsAtStart: ratingsOnlyA);
        Assert.Null(noPost.Participants.Single(p => p.Name == "A").RatingChange);
    }

    [Fact]
    public void Build_folds_bouncing_bullet_into_bullet_and_drops_burst_and_shrapnel()
    {
        // Need an energy config that knows about both Bullet and BouncingBullet so each
        // shot costs energy; the shared Make4Player helper only configures Bullet.
        var energy = new WeaponEnergyConfig(
            new Dictionary<WeaponKind, (int, int)>
            {
                [WeaponKind.Bullet] = (100, 0),
                [WeaponKind.BouncingBullet] = (100, 0),
            },
            multifireBulletEnergy: 0);
        var r = new StatsRecorder(new DamageDecay(halfLifeTicks: 200));
        r.RegisterPlayer(K("A"), 0, 1000, 0.0, energy, 0);
        r.RegisterPlayer(K("B"), 0, 1000, 0.0, energy, 0);
        r.RegisterPlayer(K("C"), 1, 1000, 0.0, energy, 0);
        r.RegisterPlayer(K("D"), 1, 1000, 0.0, energy, 0);

        // A fires a regular bullet and a bouncing bullet (each 100 energy).
        r.OnSpawn(K("A"), 0);
        r.OnWeaponFired(K("A"), WeaponKind.Bullet, 1, false, 100);
        r.OnWeaponFired(K("A"), WeaponKind.BouncingBullet, 1, false, 100);
        // Bouncing-bullet hit on C, plus a shrapnel hit and a burst hit (both should drop).
        r.OnDamage(K("C"), K("A"), 100, WeaponKind.BouncingBullet, 0, 110);
        r.OnDamage(K("C"), K("A"), 50, WeaponKind.Shrapnel, 0, 110);
        r.OnDamage(K("C"), K("A"), 50, WeaponKind.Burst, 0, 110);

        var matchId = Guid.NewGuid();
        var payload = MatchExporter.Build(
            matchId, "4v4", null, 1, "tdm", Teams(), null, r, Outcome(matchId));

        var a = payload.Participants.Single(p => p.Name == "A");
        Assert.True(a.PerWeapon.ContainsKey("Bullet"));
        Assert.False(a.PerWeapon.ContainsKey("BouncingBullet"));
        Assert.False(a.PerWeapon.ContainsKey("Burst"));
        Assert.False(a.PerWeapon.ContainsKey("Shrapnel"));
        Assert.Equal(2, a.PerWeapon["Bullet"].FireCount);
        Assert.Equal(1, a.PerWeapon["Bullet"].HitCount);
        Assert.Equal(200, a.PerWeapon["Bullet"].EnergySpent);
    }

    [Fact]
    public void Build_emits_distance_samples_and_wasted_items_per_participant()
    {
        var r = Make4Player();
        // A: two distance samples + one repel left in inventory
        r.OnDistanceSample(K("A"), 100, 256.0f);
        r.OnDistanceSample(K("A"), 120, 100.5f);
        r.SetWastedItem(K("A"), ItemKind.Repel, 1);
        // C: no samples, no wasted items -> empty arrays / dicts in the payload

        var matchId = Guid.NewGuid();
        var payload = MatchExporter.Build(
            matchId, "4v4", null, 1, "tdm", Teams(), null, r, Outcome(matchId));

        var a = payload.Participants.Single(p => p.Name == "A");
        Assert.Equal(2, a.DistanceSamples.Count);
        Assert.Equal(100u, a.DistanceSamples[0].Tick);
        Assert.Equal(256.0f, a.DistanceSamples[0].Distance);
        Assert.Equal(100.5f, a.DistanceSamples[1].Distance);
        Assert.Equal(1, a.WastedItems["Repel"]);

        var c = payload.Participants.Single(p => p.Name == "C");
        Assert.Empty(c.DistanceSamples);
        Assert.Empty(c.WastedItems);
    }
}
