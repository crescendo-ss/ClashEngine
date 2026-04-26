using System;
using System.Collections.Generic;
using SS.Core.ComponentInterfaces;

namespace ClashEngine.Config;

/// <summary>
/// Parses the <c>GameType&lt;i&gt;</c> blocks under <c>[ClashEngine]</c>. Each game type
/// describes one match's rules (team shape, win condition, lives, spawn / ship layout); a
/// queue references one game type by name, and many queues can share a game type.
/// </summary>
internal static class GameTypeParser
{
    /// <summary>
    /// Reads <c>GameTypeCount</c> and the <c>GameType1..N</c> blocks, returning a name-keyed
    /// dictionary the queue parser can resolve queue-side <c>Queue&lt;i&gt;GameType</c>
    /// references against. Bad or duplicate-named game types are logged at Warn and skipped.
    /// </summary>
    public static Dictionary<string, GameTypeDef> ReadAll(IConfigManager config, ClashLog? log)
    {
        var result = new Dictionary<string, GameTypeDef>(StringComparer.OrdinalIgnoreCase);
        int gtCount = config.GetInt(config.Global, ConfigConstants.Section, "GameTypeCount", 0);
        if (gtCount <= 0)
        {
            log?.Warn(ConfigConstants.LogCategory,
                "GameTypeCount = 0 (or missing); queues that reference any game type will be skipped.");
            return result;
        }

        for (int i = 1; i <= gtCount; i++)
        {
            if (TryReadOne(config, i, log) is { } def)
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

    private static GameTypeDef? TryReadOne(IConfigManager config, int index, ClashLog? log)
    {
        string p = $"GameType{index}";
        var name = config.GetStr(config.Global, ConfigConstants.Section, p + "Name");
        if (string.IsNullOrWhiteSpace(name))
        {
            log?.Warn(ConfigConstants.LogCategory, $"{p}Name missing -- skipping this game type slot.");
            return null;
        }

        int rawId = config.GetInt(config.Global, ConfigConstants.Section, p + "Id", index);
        if (rawId < 0 || rawId > 255)
            log?.Warn(ConfigConstants.LogCategory, $"{p}Id={rawId} outside [0,255]; clamped.");
        byte id = (byte)Math.Clamp(rawId, 0, 255);

        int teamCount = config.GetInt(config.Global, ConfigConstants.Section, p + "TeamCount", 2);
        if (teamCount < 2)
        {
            log?.Warn(ConfigConstants.LogCategory,
                $"{p}TeamCount={teamCount} (must be >=2); skipping game type '{name}'.");
            return null;
        }
        int perTeam = config.GetInt(config.Global, ConfigConstants.Section, p + "PlayersPerTeam", 4);
        if (perTeam < 1)
        {
            log?.Warn(ConfigConstants.LogCategory,
                $"{p}PlayersPerTeam={perTeam} (must be >=1); skipping game type '{name}'.");
            return null;
        }

        int killTarget = config.GetInt(config.Global, ConfigConstants.Section, p + "KillTarget", 0);
        if (killTarget < 0)
        {
            log?.Warn(ConfigConstants.LogCategory, $"{p}KillTarget={killTarget} (must be >=0); using 0.");
            killTarget = 0;
        }
        int lives = config.GetInt(config.Global, ConfigConstants.Section, p + "Lives", 0);   // 0 = unlimited
        if (lives < 0)
        {
            log?.Warn(ConfigConstants.LogCategory, $"{p}Lives={lives} (must be >=0); using 0 (unlimited).");
            lives = 0;
        }
        TimeSpan? timeLimit = ConfigReadHelpers.TryReadTimeSpan(config, p + "TimeLimit", log, p);
        if (timeLimit is { } tl && tl <= TimeSpan.Zero)
        {
            log?.Warn(ConfigConstants.LogCategory, $"{p}TimeLimit={tl} must be >0; ignored.");
            timeLimit = null;
        }

        var spawnSetByTeam = SpawnSetParser.Read(config, p, teamCount, log);
        int? maxDrift = ConfigReadHelpers.TryReadInt(config, p + "MaxSpawnDrift");
        if (maxDrift is { } mdr && mdr < 0)
        {
            log?.Warn(ConfigConstants.LogCategory, $"{p}MaxSpawnDrift={mdr} must be >=0; ignored.");
            maxDrift = null;
        }
        bool warpOnSpawn = config.GetInt(config.Global, ConfigConstants.Section, p + "WarpOnSpawn", 0) != 0;

        TimeSpan? stagingDuration = ReadOptionalPositiveTimeSpan(config, p + "StagingDuration", p, log);
        TimeSpan? countdownDuration = ReadOptionalPositiveTimeSpan(config, p + "CountdownDuration", p, log);
        TimeSpan? knockoutSpecDelay = ReadOptionalNonNegativeTimeSpan(config, p + "KnockoutSpecDelay", p, log);
        TimeSpan? teamCollapseGrace = ReadOptionalNonNegativeTimeSpan(config, p + "TeamCollapseGrace", p, log);
        TimeSpan? shipChangeGracePeriod = ReadOptionalNonNegativeTimeSpan(config, p + "ShipChangeGracePeriod", p, log);

        var shipBySlot = ShipBySlotParser.Read(config, p, teamCount, perTeam, log);

        return new GameTypeDef(
            name, id, teamCount, perTeam, killTarget, lives, timeLimit,
            spawnSetByTeam, maxDrift, warpOnSpawn,
            stagingDuration, countdownDuration, knockoutSpecDelay,
            teamCollapseGrace, shipBySlot, shipChangeGracePeriod);
    }

    /// <summary>Reads an optional <c>TimeSpan</c> that must be strictly positive when set.
    /// Bad values are warned and dropped to <see langword="null"/> (let the engine layer default).</summary>
    private static TimeSpan? ReadOptionalPositiveTimeSpan(IConfigManager config, string key, string prefix, ClashLog? log)
    {
        var ts = ConfigReadHelpers.TryReadTimeSpan(config, key, log, prefix);
        if (ts is { } v && v <= TimeSpan.Zero)
        {
            log?.Warn(ConfigConstants.LogCategory, $"{key}={v} must be > 0; using default.");
            return null;
        }
        return ts;
    }

    /// <summary>Reads an optional <c>TimeSpan</c> that must be non-negative. Bad values are
    /// warned and dropped to <see langword="null"/>.</summary>
    private static TimeSpan? ReadOptionalNonNegativeTimeSpan(IConfigManager config, string key, string prefix, ClashLog? log)
    {
        var ts = ConfigReadHelpers.TryReadTimeSpan(config, key, log, prefix);
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

        string spawnDesc = def.SpawnSetByTeam is null
            ? "(none)"
            : string.Join(", ", SpawnSetParser.Describe(def.SpawnSetByTeam));
        string shipDesc = def.ShipBySlot is null
            ? "(default Warbird)"
            : string.Join(", ", ShipBySlotParser.Describe(def.ShipBySlot));

        log.Debug(ConfigConstants.LogCategory,
            $"GameType '{def.Name}' parsed: Id={def.Id}, TeamCount={def.TeamCount}, PlayersPerTeam={def.PlayersPerTeam}, " +
            $"KillTarget={(def.KillTarget > 0 ? def.KillTarget.ToString() : "0 (unset)")}, " +
            $"TimeLimit={(def.TimeLimit is { } tl ? tl.ToString() : "(unset)")}, " +
            $"Lives={(def.Lives > 0 ? def.Lives.ToString() : "0 (unlimited)")}, " +
            $"WarpOnSpawn={def.WarpOnSpawn}, Spawns={spawnDesc}, Ships={shipDesc}, " +
            $"MaxSpawnDrift={(def.MaxSpawnDriftTiles is { } md ? md + "t" : "(unset)")}, " +
            $"StagingDuration={(def.StagingDuration is { } sd ? sd.ToString() : "(default 10s)")}, " +
            $"CountdownDuration={(def.CountdownDuration is { } cd ? cd.ToString() : "(default 3s)")}, " +
            $"KnockoutSpecDelay={(def.KnockoutSpecDelay is { } ks ? ks.ToString() : "(default 0)")}, " +
            $"TeamCollapseGrace={(def.TeamCollapseGrace is { } tg ? tg.ToString() : "(default 10s)")}, " +
            $"ShipChangeGracePeriod={(def.ShipChangeGracePeriod is { } sg ? sg.ToString() : "(default 10s)")}.");
    }
}
