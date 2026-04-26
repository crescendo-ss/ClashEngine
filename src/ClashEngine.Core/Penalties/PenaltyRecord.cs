using System;
using ClashEngine.Core.Identity;

namespace ClashEngine.Core.Penalties;

/// <summary>One row of penalty history. Persistence stores these directly; the tracker rebuilds
/// its state from a list of them on load.</summary>
/// <param name="Player">The player penalized.</param>
/// <param name="Kind">Category of penalty (abandonment, griefing).</param>
/// <param name="At">When the penalty was incurred.</param>
/// <param name="Severity">Multiplier applied to the offense's timeout. 1.0 = standard.</param>
public readonly record struct PenaltyRecord(PlayerKey Player, PenaltyKind Kind, DateTimeOffset At, double Severity = 1.0);
