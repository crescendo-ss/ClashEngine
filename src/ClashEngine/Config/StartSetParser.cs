using System;
using System.Collections.Generic;
using System.Globalization;
using ClashEngine.Core.Queue;
using SS.Core;
using SS.Core.ComponentInterfaces;

namespace ClashEngine.Config;

/// <summary>Parses the per-team match <b>starting</b>-location lists nested under each game-type
/// block: <c>{prefix}Team{T}Starts = x1,y1; x2,y2; ...</c> (pixels). These are the one-time
/// setup-warp destinations; in-match respawn boxes are parsed separately by
/// <see cref="SpawnAreaParser"/>.</summary>
internal static class StartSetParser
{
    /// <summary>
    /// Reads <c>{prefix}Team{T}Starts</c> for every team. Returns a per-team list of start
    /// points, substituting empty teams with a synthetic single-zero entry so
    /// <see cref="QueueDefinition"/>'s "no empty inner list" validation passes. Returns null
    /// when no team had any start location at all (i.e. no start override).
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<StartPoint>>? Read(
        IConfigManager config, ConfigHandle handle, string prefix, int teamCount, ClashLog? log)
    {
        var byTeam = new List<List<StartPoint>>(teamCount);
        bool anyConfigured = false;
        for (int t = 0; t < teamCount; t++)
        {
            string key = $"{prefix}Team{t + 1}Starts";
            var raw = config.GetStr(handle, ConfigConstants.Section, key);
            var points = ParseList(raw, log, prefix, key);
            if (points.Count > 0) anyConfigured = true;
            byTeam.Add(points);
        }
        if (!anyConfigured) return null;

        // Each team must have at least one point for QueueDefinition validation. Substitute the
        // arena's default (0,0) so the orchestrator's "no start override" path handles it.
        for (int t = 0; t < byTeam.Count; t++)
            if (byTeam[t].Count == 0) byTeam[t].Add(default);

        var result = new IReadOnlyList<StartPoint>[byTeam.Count];
        for (int t = 0; t < byTeam.Count; t++) result[t] = byTeam[t];
        return result;
    }

    /// <summary>One-line summary per team for the debug log.</summary>
    public static IEnumerable<string> Describe(IReadOnlyList<IReadOnlyList<StartPoint>> startSetByTeam)
    {
        for (int t = 0; t < startSetByTeam.Count; t++)
        {
            var team = startSetByTeam[t];
            yield return team.Count == 0
                ? $"team{t + 1}=(none)"
                : $"team{t + 1}=[{string.Join("|", FormatPoints(team))}]";
        }
    }

    private static List<StartPoint> ParseList(string? raw, ClashLog? log, string prefix, string key)
    {
        var list = new List<StartPoint>();
        if (string.IsNullOrWhiteSpace(raw)) return list;

        foreach (var pair in raw.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = pair.Split(',', StringSplitOptions.TrimEntries);
            if (parts.Length != 2
                || !short.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var x)
                || !short.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var y))
            {
                log?.Warn(ConfigConstants.LogCategory,
                    $"{prefix}: could not parse '{pair}' in {key} as 'x,y'; skipping.");
                continue;
            }
            list.Add(new StartPoint(x, y));
        }
        return list;
    }

    private static IEnumerable<string> FormatPoints(IReadOnlyList<StartPoint> points)
    {
        for (int i = 0; i < points.Count; i++) yield return $"{points[i].X},{points[i].Y}";
    }
}
