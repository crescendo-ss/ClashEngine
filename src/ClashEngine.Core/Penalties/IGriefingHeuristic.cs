using System.Collections.Generic;
using ClashEngine.Core.Matches;

namespace ClashEngine.Core.Penalties;

/// <summary>
/// Game-type-specific automated griefing detector. Run once on match completion. Players it
/// flags receive a queue-timeout that match participants may collectively veto.
/// </summary>
public interface IGriefingHeuristic
{
    IReadOnlyList<GriefingFlag> Evaluate(ActiveMatch match);
}

/// <summary>Default heuristic that flags nobody. Used when a queue does not configure one.</summary>
public sealed class NoGriefingHeuristic : IGriefingHeuristic
{
    public static NoGriefingHeuristic Instance { get; } = new();
    public IReadOnlyList<GriefingFlag> Evaluate(ActiveMatch match) => System.Array.Empty<GriefingFlag>();
}
