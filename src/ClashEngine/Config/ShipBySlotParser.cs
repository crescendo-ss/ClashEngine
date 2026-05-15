using System;
using System.Collections.Generic;
using System.Globalization;
using SS.Core;
using SS.Core.ComponentInterfaces;

namespace ClashEngine.Config;

/// <summary>Parses the per-team per-slot ship-override lists nested under each game-type
/// block: <c>{prefix}Team{T}Ships = warbird,javelin,...</c>. Names are case-insensitive;
/// numeric indices 0..7 are also accepted. Eight ship names supported (Continuum's stock
/// set): warbird, javelin, spider, leviathan/lev, terrier/terr, weasel, lancaster/lanc, shark.</summary>
internal static class ShipBySlotParser
{
    /// <summary>
    /// Reads <c>{prefix}Team{T}Ships</c> for every team. Returns null if no team has any ships
    /// configured at all (no override; orchestrator falls back to Warbird). When *any* team is
    /// configured, every team must list exactly <paramref name="perTeam"/> entries -- a missing
    /// or wrong-sized team logs a warn and skips the override entirely.
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<int>>? Read(
        IConfigManager config, ConfigHandle handle, string prefix, int teamCount, int perTeam, ClashLog? log)
    {
        var rows = new List<List<int>>(teamCount);
        bool anyConfigured = false;
        for (int t = 0; t < teamCount; t++)
        {
            string key = $"{prefix}Team{t + 1}Ships";
            var raw = config.GetStr(handle, ConfigConstants.Section, key);
            var slots = ParseList(raw, log, prefix, key);
            if (slots.Count > 0) anyConfigured = true;
            rows.Add(slots);
        }
        if (!anyConfigured) return null;

        for (int t = 0; t < rows.Count; t++)
        {
            if (rows[t].Count == 0)
            {
                log?.Warn(ConfigConstants.LogCategory,
                    $"{prefix}: Team{t + 1}Ships missing while another team is configured; skipping ship overrides.");
                return null;
            }
            if (rows[t].Count != perTeam)
            {
                log?.Warn(ConfigConstants.LogCategory,
                    $"{prefix}: Team{t + 1}Ships count={rows[t].Count} != PlayersPerTeam={perTeam}; skipping ship overrides.");
                return null;
            }
        }

        var result = new IReadOnlyList<int>[rows.Count];
        for (int t = 0; t < rows.Count; t++) result[t] = rows[t];
        return result;
    }

    /// <summary>One-line summary per team for the debug log.</summary>
    public static IEnumerable<string> Describe(IReadOnlyList<IReadOnlyList<int>> shipBySlot)
    {
        for (int t = 0; t < shipBySlot.Count; t++)
        {
            var team = shipBySlot[t];
            var names = new string[team.Count];
            for (int i = 0; i < team.Count; i++) names[i] = NameOf(team[i]);
            yield return $"team{t + 1}=[{string.Join("|", names)}]";
        }
    }

    private static List<int> ParseList(string? raw, ClashLog? log, string prefix, string key)
    {
        var list = new List<int>();
        if (string.IsNullOrWhiteSpace(raw)) return list;

        foreach (var token in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (TryParseShip(token, out var ship)) list.Add(ship);
            else log?.Warn(ConfigConstants.LogCategory,
                $"{prefix}: could not parse '{token}' in {key} as a ship name or 0..7; skipping.");
        }
        return list;
    }

    private static bool TryParseShip(string token, out int ship)
    {
        if (int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out ship))
            return ship is >= 0 and <= 7;

        ship = token.ToLowerInvariant() switch
        {
            "warbird" => 0,
            "javelin" => 1,
            "spider" => 2,
            "leviathan" or "lev" => 3,
            "terrier" or "terr" => 4,
            "weasel" => 5,
            "lancaster" or "lanc" => 6,
            "shark" => 7,
            _ => -1,
        };
        return ship >= 0;
    }

    private static string NameOf(int ship) => ship switch
    {
        0 => "warbird",
        1 => "javelin",
        2 => "spider",
        3 => "leviathan",
        4 => "terrier",
        5 => "weasel",
        6 => "lancaster",
        7 => "shark",
        _ => "?",
    };
}
