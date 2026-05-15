using System;
using System.Collections.Generic;
using ClashEngine.Config;
using SS.Core;
using SS.Core.ComponentInterfaces;

namespace ClashEngine.Commands;

/// <summary>
/// Helper for the <c>?clashmigrate</c> operator command. Reads the legacy
/// <c>global.conf [ClashEngine]</c> queue + game-type entries and emits paste-ready
/// <c>[ClashEngine]</c> blocks scoped per target file:
/// <list type="bullet">
///   <item><c>conf/clash.conf</c> gets every game type (zone-wide).</item>
///   <item>Each <c>Queue&lt;N&gt;MatchArena</c> becomes a separate
///         <c>arenas/{MatchArena}/clash.conf</c> block.</item>
/// </list>
/// Queue <c>Id</c> bytes are preserved exactly so the on-disk ratings store (keyed by
/// <c>GameTypeId</c>) is not invalidated by the move.
/// </summary>
/// <remarks>
/// Deliberately print-only: the operator pastes the output into the target files themselves.
/// Avoids the failure modes of programmatic conf file edits (encoding, comments, line endings,
/// include directives) and keeps the migration trivially reversible -- if the operator doesn't
/// paste anything, nothing has changed.
/// </remarks>
internal static class ClashMigrate
{
    /// <summary>Subset of <c>GameType&lt;i&gt;</c> keys we re-emit. Order here is the order
    /// they appear in the output block, so it matches the documented schema.</summary>
    private static readonly string[] GameTypeKeys = new[]
    {
        "Name", "Id", "TeamCount", "PlayersPerTeam", "KillTarget", "TimeLimit", "Lives",
        "WarpOnSpawn",
        "Team1Spawns", "Team2Spawns", "Team3Spawns", "Team4Spawns",
        "Team1Ships", "Team2Ships", "Team3Ships", "Team4Ships",
        "MaxSpawnDrift", "StagingDuration", "CountdownDuration",
        "KnockoutSpecDelay", "TeamCollapseGrace", "ShipChangeGracePeriod",
        "ReturnItemsAction",
    };

    /// <summary>Subset of <c>Queue&lt;i&gt;</c> keys we re-emit.</summary>
    private static readonly string[] QueueKeys = new[]
    {
        "Name", "GameType", "Matchmaking", "MatchArena", "LookAhead", "RelaxTime",
        "HoldWindow", "QualityCeiling", "VetoesRequired", "VetoWindow",
        "PromoteWinners", "MaxConsecutiveDefenses",
    };

    public static IReadOnlyList<string> BuildReport(IConfigManager config)
    {
        ArgumentNullException.ThrowIfNull(config);
        var lines = new List<string>();
        var gh = config.Global;

        int gtCount = config.GetInt(gh, ConfigConstants.Section, "GameTypeCount", 0);
        int qCount = config.GetInt(gh, ConfigConstants.Section, "QueueCount", 0);

        if (gtCount <= 0 && qCount <= 0)
        {
            lines.Add("No legacy [ClashEngine] queues or game types found in global.conf -- nothing to migrate.");
            return lines;
        }

        lines.Add("?clashmigrate -- preview of per-file [ClashEngine] blocks.");
        lines.Add("Paste each block into the indicated file (create the file if needed),");
        lines.Add("then remove the legacy GameType*/Queue* entries from global.conf.");
        lines.Add("");

        // ---- conf/clash.conf (zone-wide game types) -----------------------------------------
        if (gtCount > 0)
        {
            lines.Add("===== conf/clash.conf =====");
            lines.Add("[ClashEngine]");
            lines.Add($"GameTypeCount = {gtCount}");
            for (int i = 1; i <= gtCount; i++)
                EmitKeys(config, gh, "GameType", i, GameTypeKeys, lines);
            lines.Add("");
        }

        // ---- arenas/<MatchArena>/clash.conf (one section per target arena) -------------------
        // Group queues by MatchArena. Queues without a MatchArena go into an "(unassigned)" bucket
        // so the operator notices and supplies one.
        var byArena = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
        var unassigned = new List<int>();
        for (int i = 1; i <= qCount; i++)
        {
            var arena = config.GetStr(gh, ConfigConstants.Section, $"Queue{i}MatchArena");
            if (string.IsNullOrWhiteSpace(arena))
            {
                unassigned.Add(i);
                continue;
            }
            if (!byArena.TryGetValue(arena, out var list))
            {
                list = new List<int>();
                byArena[arena] = list;
            }
            list.Add(i);
        }

        foreach (var (arena, indices) in byArena)
        {
            lines.Add($"===== arenas/{arena}/clash.conf =====");
            lines.Add("[ClashEngine]");
            lines.Add($"QueueCount = {indices.Count}");
            for (int j = 0; j < indices.Count; j++)
            {
                // Re-number queues from 1 in the target file so the index ordering is local.
                int srcIdx = indices[j];
                int dstIdx = j + 1;
                EmitKeysRenumbered(config, gh, "Queue", srcIdx, dstIdx, QueueKeys, lines);
            }
            lines.Add("");
        }

        if (unassigned.Count > 0)
        {
            lines.Add("===== UNASSIGNED (no Queue<i>MatchArena set) =====");
            lines.Add("Add a MatchArena to each of these queues and re-run, or paste them into");
            lines.Add("the arena where they should live:");
            for (int j = 0; j < unassigned.Count; j++)
                EmitKeys(config, gh, "Queue", unassigned[j], QueueKeys, lines);
            lines.Add("");
        }

        return lines;
    }

    private static void EmitKeys(
        IConfigManager config, ConfigHandle handle,
        string prefix, int index,
        IReadOnlyList<string> keys, List<string> lines)
    {
        for (int k = 0; k < keys.Count; k++)
        {
            var key = $"{prefix}{index}{keys[k]}";
            var val = config.GetStr(handle, ConfigConstants.Section, key);
            if (val is null) continue;
            lines.Add($"{key} = {val}");
        }
    }

    private static void EmitKeysRenumbered(
        IConfigManager config, ConfigHandle handle,
        string prefix, int srcIndex, int dstIndex,
        IReadOnlyList<string> keys, List<string> lines)
    {
        for (int k = 0; k < keys.Count; k++)
        {
            var srcKey = $"{prefix}{srcIndex}{keys[k]}";
            var val = config.GetStr(handle, ConfigConstants.Section, srcKey);
            if (val is null) continue;
            lines.Add($"{prefix}{dstIndex}{keys[k]} = {val}");
        }
    }
}
