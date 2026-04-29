using System;

namespace ClashEngine.Adapter;

/// <summary>
/// Renders a <see cref="TimeSpan"/> as a single-unit, player-facing phrase. Picks the largest
/// unit that yields a small integer (seconds up to 60, then minutes up to 59, then hours).
/// Always rounds away from zero so a near-expired timeout never reads as "0 seconds".
/// </summary>
public static class HumanDuration
{
    public static string Humanize(TimeSpan ts)
    {
        if (ts <= TimeSpan.FromSeconds(5)) return "a few seconds";

        int seconds = (int)Math.Ceiling(ts.TotalSeconds);
        if (seconds <= 60) return $"{seconds} seconds";

        int minutes = (int)Math.Ceiling(ts.TotalMinutes);
        if (minutes < 60) return minutes == 1 ? "1 minute" : $"{minutes} minutes";

        int hours = (int)Math.Round(ts.TotalHours, MidpointRounding.AwayFromZero);
        if (hours < 1) hours = 1;
        return hours == 1 ? "1 hour" : $"{hours} hours";
    }
}
