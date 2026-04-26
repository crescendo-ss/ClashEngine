using ClashEngine.Core.Identity;

namespace ClashEngine.Core.Penalties;

/// <summary>
/// One griefer nomination from an <see cref="IGriefingHeuristic"/>.
/// </summary>
/// <param name="Player">The player being flagged.</param>
/// <param name="Reason">Human-readable explanation, used in announcement messages.</param>
/// <param name="Severity">
/// Multiplier on the base timeout (>= 1.0). 1.0 = barely over the heuristic threshold, higher
/// values = much worse. Heuristics typically cap this to keep individual penalties bounded.
/// </param>
public readonly record struct GriefingFlag(PlayerKey Player, string Reason, double Severity = 1.0);
