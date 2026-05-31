using System;
using System.Collections.Generic;
using ClashEngine.Core.GameType;
using ClashEngine.Core.Queue;
using SS.Core;
using SS.Core.ComponentInterfaces;

namespace ClashEngine.Config;

/// <summary>
/// Parses the <c>GameType&lt;i&gt;</c> blocks under <c>[ClashEngine]</c> in an arena's
/// <see cref="ConfigHandle"/> (arena.conf, plus anything <c>#include</c>'d from it). Game types
/// are arena-scoped only -- they are not read from global.conf. Each game type describes one
/// match's rules (team shape, win condition, lives, spawn / ship layout); a queue references one
/// game type by name, and many queues can share a game type.
/// </summary>
internal static class GameTypeParser
{
    /// <summary>
    /// Reads <c>GameTypeCount</c> and the <c>GameType1..N</c> blocks from <paramref name="handle"/>,
    /// returning a name-keyed dictionary the queue parser can resolve queue-side
    /// <c>Queue&lt;i&gt;GameType</c> references against. Bad or duplicate-named game types are
    /// logged at Warn and skipped.
    /// </summary>
    public static Dictionary<string, GameTypeDef> ReadAll(
        IConfigManager config, ConfigHandle handle, ClashLog? log)
    {
        var result = new Dictionary<string, GameTypeDef>(StringComparer.OrdinalIgnoreCase);
        int gtCount = config.GetInt(handle, ConfigConstants.Section, "GameTypeCount", 0);
        if (gtCount <= 0) return result;

        for (int i = 1; i <= gtCount; i++)
        {
            if (TryReadOne(config, handle, i, log) is { } def)
            {
                if (result.ContainsKey(def.Name))
                {
                    log?.Warn(ConfigConstants.LogCategory,
                        $"GameType{i}: duplicate game-type name '{def.Name}' (case-insensitive); skipping.");
                    continue;
                }
                result[def.Name] = def;
                LogParsed(def, log);
            }
        }
        return result;
    }

    private static GameTypeDef? TryReadOne(IConfigManager config, ConfigHandle handle, int index, ClashLog? log)
    {
        string p = $"GameType{index}";
        var name = config.GetStr(handle, ConfigConstants.Section, p + "Name");
        if (string.IsNullOrWhiteSpace(name))
        {
            log?.Warn(ConfigConstants.LogCategory, $"{p}Name missing -- skipping this game type slot.");
            return null;
        }

        // Label/Description ride along on the stats-server registration POST and are stored
        // version-frozen by the consumer. We default Label to Name so an operator who never
        // sets one still gets a sane display string; Description is nullable per the schema.
        var label = config.GetStr(handle, ConfigConstants.Section, p + "Label");
        if (string.IsNullOrWhiteSpace(label)) label = name;
        var description = config.GetStr(handle, ConfigConstants.Section, p + "Description");
        if (string.IsNullOrWhiteSpace(description)) description = null;

        int teamCount = config.GetInt(handle, ConfigConstants.Section, p + "TeamCount", 2);
        if (teamCount < 2)
        {
            log?.Warn(ConfigConstants.LogCategory,
                $"{p}TeamCount={teamCount} (must be >=2); skipping game type '{name}'.");
            return null;
        }
        int perTeam = config.GetInt(handle, ConfigConstants.Section, p + "PlayersPerTeam", 4);
        if (perTeam < 1)
        {
            log?.Warn(ConfigConstants.LogCategory,
                $"{p}PlayersPerTeam={perTeam} (must be >=1); skipping game type '{name}'.");
            return null;
        }

        int killTarget = config.GetInt(handle, ConfigConstants.Section, p + "KillTarget", 0);
        if (killTarget < 0)
        {
            log?.Warn(ConfigConstants.LogCategory, $"{p}KillTarget={killTarget} (must be >=0); using 0.");
            killTarget = 0;
        }
        int lives = config.GetInt(handle, ConfigConstants.Section, p + "Lives", 0);   // 0 = unlimited
        if (lives < 0)
        {
            log?.Warn(ConfigConstants.LogCategory, $"{p}Lives={lives} (must be >=0); using 0 (unlimited).");
            lives = 0;
        }
        TimeSpan? timeLimit = ConfigReadHelpers.TryReadTimeSpan(config, handle, p + "TimeLimit", log, p);
        if (timeLimit is { } tl && tl <= TimeSpan.Zero)
        {
            log?.Warn(ConfigConstants.LogCategory, $"{p}TimeLimit={tl} must be >0; ignored.");
            timeLimit = null;
        }

        var startSetByTeam = StartSetParser.Read(config, handle, p, teamCount, log);
        int? maxDrift = ConfigReadHelpers.TryReadInt(config, handle, p + "MaxStartDrift");
        if (maxDrift is { } mdr && mdr < 0)
        {
            log?.Warn(ConfigConstants.LogCategory, $"{p}MaxStartDrift={mdr} must be >=0; ignored.");
            maxDrift = null;
        }
        bool useStartLocation = config.GetInt(handle, ConfigConstants.Section, p + "UseStartLocation", 0) != 0;

        // In-match respawn boxes (client-settings [Spawn] override). Self-gating and independent
        // of UseStartLocation: a team gets an override iff it configured a SpawnCenter.
        var spawnByTeam = SpawnAreaParser.Read(config, handle, p, teamCount, log);

        TimeSpan? stagingDuration = ReadOptionalPositiveTimeSpan(config, handle, p + "StagingDuration", p, log);
        TimeSpan? countdownDuration = ReadOptionalPositiveTimeSpan(config, handle, p + "CountdownDuration", p, log);
        TimeSpan? knockoutSpecDelay = ReadOptionalNonNegativeTimeSpan(config, handle, p + "KnockoutSpecDelay", p, log);
        TimeSpan? teamCollapseGrace = ReadOptionalNonNegativeTimeSpan(config, handle, p + "TeamCollapseGrace", p, log);
        TimeSpan? shipChangeGracePeriod = ReadOptionalNonNegativeTimeSpan(config, handle, p + "ShipChangeGracePeriod", p, log);
        ItemsAction returnItemsAction = ReadReturnItemsAction(config, handle, p, log);
        // Absent -> null (engine's built-in default cooldown); 0 -> disabled (eliminated players in
        // this game type requeue immediately); >0 -> that duration. Negative is warned and dropped.
        TimeSpan? eliminationCooldown = ReadOptionalNonNegativeTimeSpan(config, handle, p + "EliminationCooldown", p, log);

        // Auto-derive the metadata blob from the resolved shape. The stats server stores this
        // verbatim with the gametype version; downstream consumers (scoreboards, dashboards)
        // read teamCount / teamSizes / livesPerPlayer from here. Operators don't see a
        // GameType<i>Metadata conf key -- if asymmetric teams ever land, this is where they
        // would lift the uniform assumption.
        var metadata = GameTypeMetadata.Uniform(teamCount, perTeam, lives);

        return new GameTypeDef(
            name, label!, description, metadata,
            teamCount, perTeam, killTarget, lives, timeLimit,
            startSetByTeam, maxDrift, useStartLocation, spawnByTeam,
            stagingDuration, countdownDuration, knockoutSpecDelay,
            teamCollapseGrace, shipChangeGracePeriod,
            returnItemsAction, eliminationCooldown);
    }

    /// <summary>Reads <c>GameType&lt;i&gt;ReturnItemsAction</c>. Accepts <c>full</c>, <c>restore</c>,
    /// or <c>burn</c> (case-insensitive). Missing or invalid values fall back to
    /// <see cref="ItemsAction.Full"/> (the legacy behavior).</summary>
    private static ItemsAction ReadReturnItemsAction(IConfigManager config, ConfigHandle handle, string prefix, ClashLog? log)
    {
        var raw = config.GetStr(handle, ConfigConstants.Section, prefix + "ReturnItemsAction");
        if (string.IsNullOrWhiteSpace(raw)) return ItemsAction.Full;
        if (Enum.TryParse<ItemsAction>(raw, ignoreCase: true, out var parsed)) return parsed;
        log?.Warn(ConfigConstants.LogCategory,
            $"{prefix}ReturnItemsAction='{raw}' not recognized (expected full/restore/burn); using Full.");
        return ItemsAction.Full;
    }

    /// <summary>Reads an optional <c>TimeSpan</c> that must be strictly positive when set.
    /// Bad values are warned and dropped to <see langword="null"/> (let the engine layer default).</summary>
    private static TimeSpan? ReadOptionalPositiveTimeSpan(IConfigManager config, ConfigHandle handle, string key, string prefix, ClashLog? log)
    {
        var ts = ConfigReadHelpers.TryReadTimeSpan(config, handle, key, log, prefix);
        if (ts is { } v && v <= TimeSpan.Zero)
        {
            log?.Warn(ConfigConstants.LogCategory, $"{key}={v} must be > 0; using default.");
            return null;
        }
        return ts;
    }

    /// <summary>Reads an optional <c>TimeSpan</c> that must be non-negative. Bad values are
    /// warned and dropped to <see langword="null"/>.</summary>
    private static TimeSpan? ReadOptionalNonNegativeTimeSpan(IConfigManager config, ConfigHandle handle, string key, string prefix, ClashLog? log)
    {
        var ts = ConfigReadHelpers.TryReadTimeSpan(config, handle, key, log, prefix);
        if (ts is { } v && v < TimeSpan.Zero)
        {
            log?.Warn(ConfigConstants.LogCategory, $"{key}={v} must be >=0; using default.");
            return null;
        }
        return ts;
    }

    private static void LogParsed(GameTypeDef def, ClashLog? log)
    {
        if (log is not { IsDebug: true }) return;

        string startDesc = def.StartSetByTeam is null
            ? "(none)"
            : string.Join(", ", StartSetParser.Describe(def.StartSetByTeam));
        string respawnDesc = def.SpawnByTeam is null
            ? "(none)"
            : string.Join(", ", SpawnAreaParser.Describe(def.SpawnByTeam));

        log.Debug(ConfigConstants.LogCategory,
            $"GameType '{def.Name}' parsed: Label='{def.Label}', TeamCount={def.TeamCount}, PlayersPerTeam={def.PlayersPerTeam}, " +
            $"KillTarget={(def.KillTarget > 0 ? def.KillTarget.ToString() : "0 (unset)")}, " +
            $"TimeLimit={(def.TimeLimit is { } tl ? tl.ToString() : "(unset)")}, " +
            $"Lives={(def.Lives > 0 ? def.Lives.ToString() : "0 (unlimited)")}, " +
            $"UseStartLocation={def.UseStartLocation}, Starts={startDesc}, " +
            $"MaxStartDrift={(def.MaxStartDriftTiles is { } md ? md + "t" : "(unset)")}, " +
            $"Respawn={respawnDesc}, " +
            $"StagingDuration={(def.StagingDuration is { } sd ? sd.ToString() : "(default 10s)")}, " +
            $"CountdownDuration={(def.CountdownDuration is { } cd ? cd.ToString() : "(default 5s)")}, " +
            $"KnockoutSpecDelay={(def.KnockoutSpecDelay is { } ks ? ks.ToString() : "(default 0)")}, " +
            $"TeamCollapseGrace={(def.TeamCollapseGrace is { } tg ? tg.ToString() : "(default 10s)")}, " +
            $"ShipChangeGracePeriod={(def.ShipChangeGracePeriod is { } sg ? sg.ToString() : "(default 10s)")}, " +
            $"EliminationCooldown={(def.EliminationCooldown is { } ec ? (ec == TimeSpan.Zero ? "0 (disabled)" : ec.ToString()) : "(default 1m)")}, " +
            $"ReturnItemsAction={def.ReturnItemsAction}.");
    }
}
