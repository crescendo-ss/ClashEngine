using System;
using System.Collections.Generic;
using System.Globalization;
using ClashEngine.Core.Queue;
using SS.Core;
using SS.Core.ComponentInterfaces;

namespace ClashEngine.Config;

/// <summary>
/// Parses the per-team in-match <b>respawn</b> boxes nested under each game-type block:
/// <c>{prefix}Team{T}SpawnCenter = x,y</c> (pixels) and <c>{prefix}Team{T}SpawnRadius = r</c>
/// (tiles). These drive the client-settings <c>[Spawn]</c> override (a port of SS's
/// <c>SendSpawnOverrides</c>) so the client respawns players inside the box after a death.
/// Distinct from the one-time start warp parsed by <see cref="StartSetParser"/>.
/// </summary>
internal static class SpawnAreaParser
{
    /// <summary>Native Continuum <c>[Spawn]</c> radius is a 9-bit field, so it caps at 511 tiles.</summary>
    private const int MaxRadiusTiles = 511;

    /// <summary>
    /// Reads <c>{prefix}Team{T}SpawnCenter</c> (+ optional <c>{prefix}Team{T}SpawnRadius</c>) for
    /// every team. Returns a per-team array of <see cref="SpawnArea"/> (null entry = that team has
    /// no respawn override). Returns null when no team configured a center at all. A radius with
    /// no center is meaningless and warned/ignored; a center with no radius defaults the radius to
    /// 0 (respawn exactly at the center).
    /// </summary>
    public static IReadOnlyList<SpawnArea?>? Read(
        IConfigManager config, ConfigHandle handle, string prefix, int teamCount, ClashLog? log)
    {
        var byTeam = new SpawnArea?[teamCount];
        bool anyConfigured = false;

        for (int t = 0; t < teamCount; t++)
        {
            string centerKey = $"{prefix}Team{t + 1}SpawnCenter";
            string radiusKey = $"{prefix}Team{t + 1}SpawnRadius";

            var rawCenter = config.GetStr(handle, ConfigConstants.Section, centerKey);
            int? rawRadius = ConfigReadHelpers.TryReadInt(config, handle, radiusKey);

            if (string.IsNullOrWhiteSpace(rawCenter))
            {
                if (rawRadius is not null)
                    log?.Warn(ConfigConstants.LogCategory,
                        $"{radiusKey} is set but {centerKey} is not; respawn override for team {t + 1} ignored.");
                continue;
            }

            if (!TryParsePoint(rawCenter, out var center))
            {
                log?.Warn(ConfigConstants.LogCategory,
                    $"{prefix}: could not parse '{rawCenter}' in {centerKey} as 'x,y'; skipping.");
                continue;
            }

            int radius = rawRadius ?? 0;
            if (radius < 0)
            {
                log?.Warn(ConfigConstants.LogCategory, $"{radiusKey}={radius} must be >=0; using 0.");
                radius = 0;
            }
            else if (radius > MaxRadiusTiles)
            {
                log?.Warn(ConfigConstants.LogCategory,
                    $"{radiusKey}={radius} exceeds the native client max ({MaxRadiusTiles} tiles); clamping.");
                radius = MaxRadiusTiles;
            }

            byTeam[t] = new SpawnArea(center, radius);
            anyConfigured = true;
        }

        return anyConfigured ? byTeam : null;
    }

    /// <summary>One-line summary per team for the debug log.</summary>
    public static IEnumerable<string> Describe(IReadOnlyList<SpawnArea?> spawnByTeam)
    {
        for (int t = 0; t < spawnByTeam.Count; t++)
        {
            yield return spawnByTeam[t] is { } a
                ? $"team{t + 1}=({a.Center.X},{a.Center.Y}) r{a.RadiusTiles}t"
                : $"team{t + 1}=(none)";
        }
    }

    private static bool TryParsePoint(string raw, out StartPoint point)
    {
        point = default;
        var parts = raw.Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length != 2
            || !short.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var x)
            || !short.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var y))
            return false;
        point = new StartPoint(x, y);
        return true;
    }
}
