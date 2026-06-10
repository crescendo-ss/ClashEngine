using System;
using ClashEngine.Core.Identity;

namespace ClashEngine.Core.Penalties;

/// <summary>
/// Point-in-time view of one player's standing on one penalty kind's escalation ladder, as
/// returned by <see cref="PenaltyTracker.ActiveStatuses"/>. Unlike <see cref="PenaltyRecord"/>
/// (one raw event), this aggregates the player's whole ladder for the kind.
/// </summary>
/// <param name="Player">The penalized player.</param>
/// <param name="Kind">Which ladder this row describes (the reason for the timeout).</param>
/// <param name="OffenseCount">Position on the ladder (1-indexed): consecutive offenses within
/// the policy's memory window.</param>
/// <param name="Severity">Severity of the most recent event (1.0 = standard).</param>
/// <param name="Multiplier">Nominal scale applied to the kind's base timeout:
/// <c>EscalationFactor^(OffenseCount-1) * Severity</c>. The assessed timeout is still clamped
/// to the policy's <see cref="PenaltyPolicy.MaxTimeout"/>, so a large multiplier may not
/// lengthen the actual timeout further.</param>
/// <param name="LastAt">When the most recent offense was recorded.</param>
/// <param name="TimeoutEnd">When the queue timeout ends (may be in the past if the player has
/// served it but the ladder is still armed).</param>
/// <param name="MemoryEnd">When the ladder resets (<c>LastAt + MemoryWindow</c>): a new offense
/// after this moment starts back at offense 1 / multiplier 1.</param>
public readonly record struct PenaltyStatus(
    PlayerKey Player,
    PenaltyKind Kind,
    int OffenseCount,
    double Severity,
    double Multiplier,
    DateTimeOffset LastAt,
    DateTimeOffset TimeoutEnd,
    DateTimeOffset MemoryEnd);
