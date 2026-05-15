using System;
using System.Collections.Generic;

namespace ClashEngine.Core.Stats;

/// <summary>
/// Serializable end-of-match envelope. Combines match metadata (queue, arena, teams, final
/// ranking), every participant's full <see cref="PlayerStats"/>, and an optional pointer to
/// the replay recording. Produced by <see cref="MatchExporter"/> and consumed by an
/// <see cref="IMatchUploader"/>.
/// </summary>
/// <remarks>
/// <see cref="SchemaVersion"/> identifies the wire format (currently 1). Increment on any
/// breaking change to the schema document under <c>schema/match.schema.json</c>.
/// </remarks>
public sealed record MatchPayload(
    int SchemaVersion,
    Guid MatchId,
    string? QueueName,
    string? QueueLabel,
    uint GameType,
    string? Arena,
    DateTimeOffset? StartedAt,
    DateTimeOffset EndedAt,
    string FinalState,
    IReadOnlyList<RankedTeamPayload> Teams,
    IReadOnlyList<PlayerStatsPayload> Participants,
    IReadOnlyList<string> AbandonedBy,
    string? RecordingPath);

public sealed record RankedTeamPayload(int Rank, int Score, IReadOnlyList<string> Players);

public sealed record PlayerStatsPayload(
    string Name,
    int TeamIndex,
    RatingPayload? RatingAtStart,
    double? RatingChange,
    int Kills,
    int Deaths,
    int Knockouts,
    int Assists,
    int Teamkills,
    long DamageDealt,
    long DamageTaken,
    long SelfDamage,
    long TeamDamageDealt,
    long TeamDamageTaken,
    double KillDamage,
    double KillCredit,
    double ForcedRepelDamage,
    double ForcedRepelCredit,
    uint ActiveTicks,
    IReadOnlyDictionary<string, WeaponStatsPayload> PerWeapon,
    IReadOnlyDictionary<string, int> ItemUses,
    IReadOnlyDictionary<string, int> WastedItems,
    IReadOnlyList<DistanceSamplePayload> DistanceSamples,
    IReadOnlyList<LifeStatsPayload> Lives);

/// <summary>Frozen snapshot of a player's rating at the moment the match started.</summary>
public sealed record RatingPayload(double Mu, double Sigma, uint GamesPlayed);

public sealed record WeaponStatsPayload(int FireCount, int HitCount, long EnergySpent);

/// <summary>
/// A single distance-to-nearest-enemy sample. <c>Tick</c> is server tick (10 ms granularity);
/// <c>Distance</c> is in pixels.
/// </summary>
public sealed record DistanceSamplePayload(uint Tick, float Distance);

public sealed record LifeStatsPayload(
    uint StartTick,
    uint? EndTick,
    string? EndReason,
    string? KnockoutBy,
    int Kills,
    int DamageDealt,
    int DamageTaken);
