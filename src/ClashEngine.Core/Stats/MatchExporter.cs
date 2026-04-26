using System;
using System.Collections.Generic;
using ClashEngine.Core.Identity;
using ClashEngine.Core.Matches;

namespace ClashEngine.Core.Stats;

/// <summary>
/// Builds a <see cref="MatchPayload"/> envelope from a finalized match. Pure -- no I/O.
/// </summary>
public static class MatchExporter
{
    public const int CurrentSchemaVersion = 2;

    public static MatchPayload Build(
        Guid matchId,
        string? queueName,
        int gameType,
        string? arena,
        IReadOnlyList<IReadOnlyList<PlayerKey>> teams,
        DateTimeOffset? startedAt,
        StatsRecorder recorder,
        MatchOutcome outcome,
        IReadOnlyDictionary<PlayerKey, RatingPayload>? ratingsAtStart = null,
        IReadOnlyDictionary<PlayerKey, double>? postOrdinalByPlayer = null,
        string? recordingPath = null)
    {
        ArgumentNullException.ThrowIfNull(teams);
        ArgumentNullException.ThrowIfNull(recorder);
        ArgumentNullException.ThrowIfNull(outcome);

        // Team index per player (for the player payload). Built from the original Teams
        // structure so it's stable regardless of how the outcome ranks them.
        var teamIndex = new Dictionary<PlayerKey, int>();
        for (int t = 0; t < teams.Count; t++)
            for (int j = 0; j < teams[t].Count; j++)
                teamIndex[teams[t][j]] = t;

        var rankedTeams = new List<RankedTeamPayload>(outcome.RankedTeams.Count);
        for (int i = 0; i < outcome.RankedTeams.Count; i++)
        {
            var rt = outcome.RankedTeams[i];
            var names = new List<string>(rt.Players.Count);
            for (int j = 0; j < rt.Players.Count; j++) names.Add(rt.Players[j].Name);
            rankedTeams.Add(new RankedTeamPayload(rt.Rank, rt.Score, names));
        }

        var participants = new List<PlayerStatsPayload>(recorder.Stats.Count);
        foreach (var (key, ps) in recorder.Stats)
        {
            int tIndex = teamIndex.TryGetValue(key, out var ti) ? ti : -1;

            // Fold bouncing variants into their canonical kind, drop Burst/Shrapnel (bookkeeping
            // kinds that the schema's perWeapon enum doesn't admit). Keys: Bullet/Bomb/Mine/Thor.
            var perWeapon = new Dictionary<string, WeaponStatsPayload>(ps.PerWeapon.Count);
            foreach (var (kind, w) in ps.PerWeapon)
            {
                var folded = FoldWeaponKind(kind);
                if (folded is null) continue;
                if (perWeapon.TryGetValue(folded, out var existing))
                {
                    perWeapon[folded] = new WeaponStatsPayload(
                        existing.FireCount + w.FireCount,
                        existing.HitCount + w.HitCount,
                        existing.EnergySpent + w.EnergySpent);
                }
                else
                {
                    perWeapon[folded] = new WeaponStatsPayload(w.FireCount, w.HitCount, w.EnergySpent);
                }
            }

            var itemUses = new Dictionary<string, int>(ps.ItemUses.Count);
            foreach (var (item, count) in ps.ItemUses)
                itemUses[item.ToString()] = count;

            var wastedItems = new Dictionary<string, int>(ps.WastedItems.Count);
            foreach (var (item, count) in ps.WastedItems)
                wastedItems[item.ToString()] = count;

            var distanceSamples = new List<DistanceSamplePayload>(ps.DistanceSamples.Count);
            for (int i = 0; i < ps.DistanceSamples.Count; i++)
            {
                var s = ps.DistanceSamples[i];
                distanceSamples.Add(new DistanceSamplePayload(s.Tick, s.Distance));
            }

            var lives = new List<LifeStatsPayload>(ps.Lives.Count);
            foreach (var l in ps.Lives)
            {
                lives.Add(new LifeStatsPayload(
                    l.StartTick,
                    l.EndTick,
                    l.EndReason?.ToString(),
                    l.KnockoutBy?.Name,
                    l.Kills,
                    l.DamageDealt,
                    l.DamageTaken));
            }

            RatingPayload? rating = null;
            if (ratingsAtStart is not null && ratingsAtStart.TryGetValue(key, out var snap))
                rating = snap;

            // Rating delta in displayed-ordinal units: (post − pre) × 10. Same scale the
            // scoreboard uses; only emitted when both halves are known.
            double? ratingChange = null;
            if (rating is not null
                && postOrdinalByPlayer is not null
                && postOrdinalByPlayer.TryGetValue(key, out var post))
            {
                double pre = rating.Mu - 3.0 * rating.Sigma;
                ratingChange = (post - pre) * 10.0;
            }

            participants.Add(new PlayerStatsPayload(
                Name: key.Name,
                TeamIndex: tIndex,
                RatingAtStart: rating,
                RatingChange: ratingChange,
                Kills: ps.Kills,
                Deaths: ps.Deaths,
                Knockouts: ps.Knockouts,
                Assists: ps.Assists,
                Teamkills: ps.Teamkills,
                DamageDealt: ps.DamageDealt,
                DamageTaken: ps.DamageTaken,
                SelfDamage: ps.SelfDamage,
                TeamDamageDealt: ps.TeamDamageDealt,
                TeamDamageTaken: ps.TeamDamageTaken,
                KillDamage: ps.KillDamage,
                KillCredit: ps.KillCredit,
                ForcedRepelDamage: ps.ForcedRepelDamage,
                ForcedRepelCredit: ps.ForcedRepelCredit,
                ActiveTicks: ps.ActiveTicks,
                PerWeapon: perWeapon,
                ItemUses: itemUses,
                WastedItems: wastedItems,
                DistanceSamples: distanceSamples,
                Lives: lives));
        }

        // Players who caused the abandonment, by name. May span multiple teams -- multiple
        // players (from either team) can drop in the same match -- so this is a flat list,
        // not partitioned by team.
        var abandonedBy = new List<string>(outcome.AbandonedBy.Count);
        for (int i = 0; i < outcome.AbandonedBy.Count; i++)
            abandonedBy.Add(outcome.AbandonedBy[i].Name);

        return new MatchPayload(
            SchemaVersion: CurrentSchemaVersion,
            MatchId: matchId,
            QueueName: queueName,
            GameType: gameType,
            Arena: arena,
            StartedAt: startedAt,
            EndedAt: outcome.EndedAt,
            FinalState: outcome.FinalState.ToString(),
            Teams: rankedTeams,
            Participants: participants,
            AbandonedBy: abandonedBy,
            RecordingPath: recordingPath);
    }

    private static string? FoldWeaponKind(WeaponKind kind) => kind switch
    {
        WeaponKind.Bullet or WeaponKind.BouncingBullet => "Bullet",
        WeaponKind.Bomb or WeaponKind.BouncingBomb => "Bomb",
        WeaponKind.Mine or WeaponKind.BouncingMine => "Mine",
        WeaponKind.Thor => "Thor",
        _ => null, // Burst, Shrapnel — bookkeeping kinds not in the schema enum.
    };
}
