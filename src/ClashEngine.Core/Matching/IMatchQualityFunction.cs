using System.Collections.Generic;
using ClashEngine.Core.Ratings;

namespace ClashEngine.Core.Matching;

/// <summary>
/// Computes a unitless quality score in <c>[0, 1]</c> for a candidate team partition,
/// where 1.0 is "perfectly balanced" and 0.0 is "totally lopsided."
/// </summary>
public interface IMatchQualityFunction
{
    double Compute(IReadOnlyList<IReadOnlyList<Rating>> teams);
}
