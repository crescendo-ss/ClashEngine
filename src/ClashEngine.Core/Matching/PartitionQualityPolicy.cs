using System;

namespace ClashEngine.Core.Matching;

/// <summary>
/// Determines the minimum partition quality required to start a match given how long
/// players have been waiting. Linear decay with a floor: q(t) = max(qFloor, qStart - (t / tRelax) * (qStart - qFloor)).
/// </summary>
public sealed class PartitionQualityPolicy
{
    public PartitionQualityPolicy(double qStart, double qFloor, TimeSpan relaxTime)
    {
        if (qStart < 0 || qStart > 1) throw new ArgumentOutOfRangeException(nameof(qStart), "Must be in [0, 1].");
        if (qFloor < 0 || qFloor > 1) throw new ArgumentOutOfRangeException(nameof(qFloor), "Must be in [0, 1].");
        if (qFloor > qStart) throw new ArgumentException("qFloor must not exceed qStart.", nameof(qFloor));
        if (relaxTime <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(relaxTime), "Must be positive.");

        QStart = qStart;
        QFloor = qFloor;
        RelaxTime = relaxTime;
    }

    public double QStart { get; }
    public double QFloor { get; }
    public TimeSpan RelaxTime { get; }

    /// <summary>Default for small-population zones: starts at 0.5, decays to 0.15 over 90s.</summary>
    public static PartitionQualityPolicy Default { get; } =
        new(qStart: 0.5, qFloor: 0.15, relaxTime: TimeSpan.FromSeconds(90));

    /// <summary>
    /// Required minimum quality given the longest-waiting player has been queued for <paramref name="waitTime"/>.
    /// </summary>
    public double MinQuality(TimeSpan waitTime)
    {
        if (waitTime <= TimeSpan.Zero) return QStart;
        if (waitTime >= RelaxTime) return QFloor;

        double progress = waitTime.TotalSeconds / RelaxTime.TotalSeconds;
        return QStart - progress * (QStart - QFloor);
    }
}
