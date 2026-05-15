using System.Collections.Generic;
using ClashEngine.Core.Stats;

namespace ClashEngine.Core.Tests.Stats;

public class ScoreboardFormatterTests
{
    private static MatchPayload SamplePayload()
    {
        var beacon = new PlayerStatsPayload(
            Name: "Beacon", TeamIndex: 0,
            RatingAtStart: new RatingPayload(Mu: 25.0, Sigma: 8.333, GamesPlayed: 10),
            RatingChange: null,
            Kills: 8, Deaths: 3, Knockouts: 2, Assists: 3, Teamkills: 0,
            DamageDealt: 6357, DamageTaken: 17836, SelfDamage: 0,
            TeamDamageDealt: 162, TeamDamageTaken: 0,
            KillDamage: 3041, KillCredit: 1.5, ForcedRepelDamage: 612, ForcedRepelCredit: 2.0,
            ActiveTicks: 18000,
            PerWeapon: new Dictionary<string, WeaponStatsPayload>
            {
                ["Bullet"] = new(FireCount: 320, HitCount: 100, EnergySpent: 9600),
                ["Bomb"] = new(FireCount: 40, HitCount: 13, EnergySpent: 12000),
                ["Mine"] = new(FireCount: 1, HitCount: 0, EnergySpent: 800),
            },
            ItemUses: new Dictionary<string, int> { ["Repel"] = 4 },
            WastedItems: new Dictionary<string, int> { ["Repel"] = 1 },
            DistanceSamples: new[] { new DistanceSamplePayload(0, 944), new DistanceSamplePayload(100, 944) },
            Lives: System.Array.Empty<LifeStatsPayload>());

        var jolt = new PlayerStatsPayload(
            Name: "Jolt", TeamIndex: 0,
            RatingAtStart: new RatingPayload(Mu: 22.0, Sigma: 7.0, GamesPlayed: 5),
            RatingChange: null,
            Kills: 12, Deaths: 4, Knockouts: 1, Assists: 0, Teamkills: 1,
            DamageDealt: 7655, DamageTaken: 15643,
            SelfDamage: 0, TeamDamageDealt: 453, TeamDamageTaken: 0,
            KillDamage: 3388, KillCredit: 2.0, ForcedRepelDamage: 200, ForcedRepelCredit: 1.5,
            ActiveTicks: 18000,
            PerWeapon: new Dictionary<string, WeaponStatsPayload>
            {
                ["Bullet"] = new(0, 0, 0),
                ["Bomb"] = new(60, 16, 18000),
            },
            ItemUses: new Dictionary<string, int>(),
            WastedItems: new Dictionary<string, int> { ["Repel"] = 3 },
            DistanceSamples: System.Array.Empty<DistanceSamplePayload>(),
            Lives: System.Array.Empty<LifeStatsPayload>());

        var teams = new[]
        {
            new RankedTeamPayload(Rank: 1, Score: 20, Players: new[] { "Beacon", "Jolt" }),
        };

        return new MatchPayload(
            SchemaVersion: MatchExporter.CurrentSchemaVersion, MatchId: System.Guid.NewGuid(), QueueName: "test", QueueLabel: null, GameType: 1,
            Arena: "test", StartedAt: System.DateTimeOffset.UtcNow,
            EndedAt: System.DateTimeOffset.UtcNow,
            FinalState: "Completed", Teams: teams,
            Participants: new[] { beacon, jolt },
            AbandonedBy: System.Array.Empty<string>(),
            RecordingPath: null);
    }

    [Fact]
    public void Format_emits_team_block_with_dividers_header_rows_and_totals()
    {
        var payload = SamplePayload();
        var post = new Dictionary<string, double>(System.StringComparer.OrdinalIgnoreCase)
        {
            ["Beacon"] = 25.5 - 3.0 * 8.0,  // small rating gain
            ["Jolt"] = 23.0 - 3.0 * 6.5,    // bigger gain (lower sigma)
        };

        var lines = ScoreboardFormatter.Format(payload, post);

        // Single team: top + header + sep + row + row + sep + totals + closing = 8 lines.
        Assert.Equal(8, lines.Count);
        Assert.StartsWith("+", lines[0]);

        // Header row carries the rank label as the first-column header (no separate title row).
        // Single-team payload falls into the "Rank #N" branch (Victors/Defeated only when teamCount == 2).
        Assert.Contains("Rank #1", lines[1]);
        Assert.Contains("(20 pts)", lines[1]);
        Assert.DoesNotContain("Player", lines[1]);
        Assert.Contains("Ki/De", lines[1]);
        Assert.Contains("AcB", lines[1]);
        Assert.Contains("+/-", lines[1]);

        // Player rows render names and KO/Death counts.
        Assert.Contains("Beacon", lines[3]);
        Assert.Contains(" 8/ 3", lines[3]);
        Assert.Contains("Jolt", lines[4]);
        Assert.Contains("12/ 4", lines[4]);

        // Totals row sums kills/deaths and shows K/D as "20/ 7".
        Assert.Contains("Totals", lines[6]);
        Assert.Contains("20/ 7", lines[6]);

        // Closing divider after the totals row.
        Assert.StartsWith("+", lines[7]);
    }

    [Fact]
    public void Format_uses_winners_and_losers_labels_for_two_team_match()
    {
        var team1 = new RankedTeamPayload(Rank: 1, Score: 12, Players: new[] { "Alpha" });
        var team2 = new RankedTeamPayload(Rank: 2, Score: 7, Players: new[] { "Bravo" });

        var alpha = MakeStub("Alpha");
        var bravo = MakeStub("Bravo");
        var payload = new MatchPayload(
            SchemaVersion: MatchExporter.CurrentSchemaVersion, MatchId: System.Guid.NewGuid(), QueueName: "test", QueueLabel: null, GameType: 1,
            Arena: "test", StartedAt: System.DateTimeOffset.UtcNow,
            EndedAt: System.DateTimeOffset.UtcNow,
            FinalState: "Completed", Teams: new[] { team1, team2 },
            Participants: new[] { alpha, bravo },
            AbandonedBy: System.Array.Empty<string>(),
            RecordingPath: null);

        var lines = ScoreboardFormatter.Format(payload);

        // Two teams: 1 top + (header + sep + row + sep + totals = 5) + 1 thick separator
        //   + (header + sep + row + sep + totals = 5) + 1 closing = 13 lines.
        Assert.Equal(13, lines.Count);
        Assert.Contains("Winners", lines[1]);
        Assert.Contains("(12 pts)", lines[1]);
        Assert.DoesNotContain("Rank #1", lines[1]);

        // Thick (===) separator between team blocks.
        Assert.Contains("==", lines[6]);

        Assert.Contains("Losers", lines[7]);
        Assert.Contains("(7 pts)", lines[7]);
        Assert.DoesNotContain("Rank #2", lines[7]);
    }

    [Fact]
    public void Format_uses_rank_labels_for_three_or_more_teams()
    {
        var team1 = new RankedTeamPayload(Rank: 1, Score: 5, Players: new[] { "Alpha" });
        var team2 = new RankedTeamPayload(Rank: 2, Score: 4, Players: new[] { "Bravo" });
        var team3 = new RankedTeamPayload(Rank: 3, Score: 2, Players: new[] { "Cobra" });

        var payload = new MatchPayload(
            SchemaVersion: MatchExporter.CurrentSchemaVersion, MatchId: System.Guid.NewGuid(), QueueName: "test", QueueLabel: null, GameType: 1,
            Arena: "test", StartedAt: System.DateTimeOffset.UtcNow,
            EndedAt: System.DateTimeOffset.UtcNow,
            FinalState: "Completed", Teams: new[] { team1, team2, team3 },
            Participants: new[] { MakeStub("Alpha"), MakeStub("Bravo"), MakeStub("Cobra") },
            AbandonedBy: System.Array.Empty<string>(),
            RecordingPath: null);

        var lines = ScoreboardFormatter.Format(payload);

        Assert.Contains(lines, l => l.Contains("Rank #1") && l.Contains("(5 pts)"));
        Assert.Contains(lines, l => l.Contains("Rank #2") && l.Contains("(4 pts)"));
        Assert.Contains(lines, l => l.Contains("Rank #3") && l.Contains("(2 pts)"));
        Assert.DoesNotContain(lines, l => l.Contains("Winners") || l.Contains("Losers"));
    }

    private static PlayerStatsPayload MakeStub(string name) => new(
        Name: name, TeamIndex: 0,
        RatingAtStart: null, RatingChange: null,
        Kills: 0, Deaths: 0, Knockouts: 0, Assists: 0, Teamkills: 0,
        DamageDealt: 0, DamageTaken: 0, SelfDamage: 0,
        TeamDamageDealt: 0, TeamDamageTaken: 0,
        KillDamage: 0, KillCredit: 0, ForcedRepelDamage: 0, ForcedRepelCredit: 0,
        ActiveTicks: 0,
        PerWeapon: new Dictionary<string, WeaponStatsPayload>(),
        ItemUses: new Dictionary<string, int>(),
        WastedItems: new Dictionary<string, int>(),
        DistanceSamples: System.Array.Empty<DistanceSamplePayload>(),
        Lives: System.Array.Empty<LifeStatsPayload>());

    [Fact]
    public void Format_renders_dash_for_missing_rating_and_zero_division_cells()
    {
        var payload = SamplePayload();
        // No post-match ordinals provided -> +/- and Rat should be dashes.
        var lines = ScoreboardFormatter.Format(payload);

        // Jolt row has no bullets fired, so AcB should be dashed; with no postOrdinal,
        // +/- and Rat are also dashed. Jolt is the second player row (index 4 in the new layout).
        string joltRow = lines[4];
        Assert.Contains("Jolt", joltRow);
        // " - " (space-hyphen-space) only appears inside dash-placeholder cells -- divider
        // rows look like "+---...---+" with no surrounding spaces around the hyphens.
        Assert.Contains(" - ", joltRow);
    }

    [Fact]
    public void Format_keeps_consistent_row_width_across_all_lines()
    {
        var payload = SamplePayload();
        var lines = ScoreboardFormatter.Format(payload);
        int width = lines[0].Length; // divider line sets the canonical width
        for (int i = 0; i < lines.Count; i++)
        {
            if (string.IsNullOrEmpty(lines[i])) continue;
            Assert.Equal(width, lines[i].Length);
        }
    }
}
