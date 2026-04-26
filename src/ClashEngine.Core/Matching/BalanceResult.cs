using System.Collections.Generic;
using ClashEngine.Core.Identity;

namespace ClashEngine.Core.Matching;

/// <summary>
/// Output of <see cref="TeamBalancer.FindBest"/>: the chosen partition, its quality (higher is better, in [0,1]),
/// and the absolute spread between team-mean ordinals (lower is better).
/// </summary>
public sealed record BalanceResult(
    IReadOnlyList<IReadOnlyList<PlayerKey>> Teams,
    double Quality,
    double Imbalance);
