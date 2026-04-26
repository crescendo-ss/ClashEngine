using System;
using System.Collections.Generic;
using ClashEngine.Core.Matches;

namespace ClashEngine.Core.Penalties;

/// <summary>
/// Flags any player who burned through all their lives unusually fast -- leaving teammates "out
/// to dry." Useful as a griefing detector for limited-lives game modes (e.g., team-versus where
/// each player has 5 lives and the match runs 20 minutes).
/// </summary>
/// <remarks>
/// <para>
/// At least one of <see cref="MinimumDuration"/> or <see cref="MinimumFractionOfMatch"/> must be
/// supplied. A player is flagged if either threshold is violated.
/// </para>
/// <para>
/// Severity scales with how badly the threshold was missed: a player who exited at 5s with a
/// 60s minimum gets severity 12.0 (capped at <see cref="SeverityCap"/>), while exiting at 50s
/// gets severity ~= 1.2. When both thresholds apply, the higher-severity violation wins.
/// </para>
/// <para>
/// Has no effect on matches without configured lives (<see cref="ActiveMatch.LivesPerPlayer"/>
/// is <see langword="null"/>).
/// </para>
/// </remarks>
public sealed class EarlyExitHeuristic : IGriefingHeuristic
{
    public EarlyExitHeuristic(
        TimeSpan? minimumDuration = null,
        double? minimumFractionOfMatch = null,
        double severityCap = 5.0)
    {
        if (minimumDuration is null && minimumFractionOfMatch is null)
            throw new ArgumentException("At least one threshold must be supplied.");
        if (minimumDuration is { } d && d < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(minimumDuration), "Must be non-negative.");
        if (minimumFractionOfMatch is { } f && (f < 0 || f > 1))
            throw new ArgumentOutOfRangeException(nameof(minimumFractionOfMatch), "Must be in [0, 1].");
        if (severityCap < 1.0)
            throw new ArgumentOutOfRangeException(nameof(severityCap), "Must be >= 1.");

        MinimumDuration = minimumDuration;
        MinimumFractionOfMatch = minimumFractionOfMatch;
        SeverityCap = severityCap;
    }

    public TimeSpan? MinimumDuration { get; }
    public double? MinimumFractionOfMatch { get; }
    public double SeverityCap { get; }

    public IReadOnlyList<GriefingFlag> Evaluate(ActiveMatch match)
    {
        ArgumentNullException.ThrowIfNull(match);
        if (match.LivesPerPlayer is null || match.StartedAt is null || match.ExitedAt.Count == 0)
            return Array.Empty<GriefingFlag>();

        TimeSpan matchDuration = (match.EndedAt ?? match.StartedAt.Value) - match.StartedAt.Value;
        if (matchDuration < TimeSpan.Zero) matchDuration = TimeSpan.Zero;

        List<GriefingFlag>? flags = null;
        foreach (var kvp in match.ExitedAt)
        {
            var participation = kvp.Value - match.StartedAt.Value;
            if (participation < TimeSpan.Zero) participation = TimeSpan.Zero;

            double severity = 0;
            string? reason = null;

            if (MinimumDuration is { } minDur && participation < minDur)
            {
                double s = Math.Min(SeverityCap, minDur.TotalSeconds / Math.Max(1.0, participation.TotalSeconds));
                if (s > severity)
                {
                    severity = s;
                    reason = $"used all {match.LivesPerPlayer} lives in {participation:mm\\:ss} (minimum {minDur:mm\\:ss})";
                }
            }

            if (MinimumFractionOfMatch is { } minFrac && matchDuration > TimeSpan.Zero)
            {
                double fraction = participation.TotalSeconds / matchDuration.TotalSeconds;
                if (fraction < minFrac)
                {
                    double s = Math.Min(SeverityCap, minFrac / Math.Max(0.001, fraction));
                    if (s > severity)
                    {
                        severity = s;
                        reason = $"participated for {fraction:P0} of the match (minimum {minFrac:P0})";
                    }
                }
            }

            if (reason is not null)
                (flags ??= new List<GriefingFlag>()).Add(
                    new GriefingFlag(kvp.Key, reason, Math.Max(1.0, severity)));
        }
        return flags ?? (IReadOnlyList<GriefingFlag>)Array.Empty<GriefingFlag>();
    }
}
