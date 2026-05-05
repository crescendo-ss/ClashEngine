using System;
using System.Collections.Generic;
using ClashEngine.Core;
using ClashEngine.Core.Identity;
using ClashEngine.Core.Matches;
using ClashEngine.Core.Matching;
using ClashEngine.Core.Penalties;
using ClashEngine.Core.Queue;
using SS.Core.ComponentInterfaces;

namespace ClashEngine.Config;

/// <summary>
/// Parses one <c>Queue&lt;i&gt;</c> block and registers the resulting <see cref="QueueDefinition"/>
/// with the engine. Each queue is a matchmaking pool over a referenced game type plus per-queue
/// matchmaking-policy knobs (strictness, look-ahead, hold window, KOTH, vetoes, etc.).
/// </summary>
internal static class QueueParser
{
    /// <summary>Reads <c>Queue&lt;index&gt;</c> and registers it with <paramref name="engine"/>.
    /// Returns true on success, false if the queue was skipped for any reason (missing name,
    /// unknown game type, duplicate, validation failure).</summary>
    public static bool ReadAndRegister(
        MatchmakingEngine engine,
        IConfigManager config,
        int index,
        IReadOnlyDictionary<string, GameTypeDef> gameTypes,
        ClashLog? log)
    {
        string p = $"Queue{index}";
        var name = config.GetStr(config.Global, ConfigConstants.Section, p + "Name");
        if (string.IsNullOrWhiteSpace(name))
        {
            log?.Warn(ConfigConstants.LogCategory, $"{p}Name missing -- skipping this queue slot.");
            return false;
        }
        if (engine.Queues.Contains(name))
        {
            log?.Warn(ConfigConstants.LogCategory,
                $"{p}: queue name '{name}' already registered; skipping.");
            return false;
        }

        var gtName = config.GetStr(config.Global, ConfigConstants.Section, p + "GameType");
        if (string.IsNullOrWhiteSpace(gtName))
        {
            log?.Warn(ConfigConstants.LogCategory,
                $"{p}GameType missing for queue '{name}' -- skipping.");
            return false;
        }
        if (!gameTypes.TryGetValue(gtName, out var gt))
        {
            log?.Warn(ConfigConstants.LogCategory,
                $"{p}GameType='{gtName}' for queue '{name}' references an unknown game type -- skipping.");
            return false;
        }

        var (casual, matchmakingDefaulted) = ReadMatchmakingTier(config, p, log);
        var arenaName = config.GetStr(config.Global, ConfigConstants.Section, p + "MatchArena");

        var lookAhead = ReadLookAhead(config, p, gt.TeamCount * gt.PlayersPerTeam, log);
        var (effectiveRelax, relaxDefaulted) = ReadRelaxTime(config, p, casual, log);
        var (effectiveHold, holdDefaulted) = ReadHoldWindow(config, p, log);
        var (effectiveQc, qcDefaulted) = ReadQualityCeiling(config, p, log);
        var (effectiveVetoes, vetoesDefaulted) = ReadVetoesRequired(config, p, log);
        var (vetoWindow, vetoWindowDefaulted) = ReadVetoWindow(config, p, log);
        var (promoteWinners, effectiveMaxDef, maxDefDefaulted) = ReadKoth(config, p, log);

        var quality = new PartitionQualityPolicy(
            qStart: casual ? 0.4 : 0.6,
            qFloor: casual ? 0.10 : 0.30,
            relaxTime: effectiveRelax);
        var shape = new MatchShape(
            teamCount: gt.TeamCount,
            playersPerTeam: gt.PlayersPerTeam,
            maxLiabilityGap: casual ? null : 8.0);

        var (endPolicy, endPolicyDesc) = BuildEndPolicy(gt);
        Func<IGriefingHeuristic> griefing = gt.Lives > 0
            ? () => new EarlyExitHeuristic(minimumDuration: TimeSpan.FromMinutes(2))
            : () => new TeamkillThresholdHeuristic(threshold: casual ? 3 : 2);

        try
        {
            engine.Queues.Register(
                name: name,
                shape: shape,
                qualityPolicy: quality,
                gameType: new GameTypeId(gt.Id),
                endPolicyFactory: endPolicy,
                griefingHeuristicFactory: griefing,
                vetoesRequired: effectiveVetoes,
                vetoWindow: vetoWindow,
                ratingWeight: casual ? 0.5 : 1.0,
                matchArenaName: string.IsNullOrWhiteSpace(arenaName) ? null : arenaName,
                shipBySlot: gt.ShipBySlot,
                spawnSetByTeam: gt.SpawnSetByTeam,
                maxSpawnDriftTiles: gt.MaxSpawnDriftTiles,
                warpOnSpawn: gt.WarpOnSpawn,
                stagingDuration: gt.StagingDuration,
                countdownDuration: gt.CountdownDuration,
                lookAheadWindow: lookAhead.EffectiveTotal,
                promoteWinnersToFront: promoteWinners,
                maxConsecutiveDefenses: effectiveMaxDef,
                holdWindow: effectiveHold,
                qualityCeiling: effectiveQc,
                knockoutSpecDelay: gt.KnockoutSpecDelay,
                livesPerPlayer: gt.Lives > 0 ? gt.Lives : null,
                teamCollapseGrace: gt.TeamCollapseGrace,
                tier: casual ? MatchmakingTier.Casual : MatchmakingTier.Competitive,
                shipChangeGracePeriod: gt.ShipChangeGracePeriod,
                timeLimit: gt.TimeLimit,
                returnItemsAction: gt.ReturnItemsAction);
        }
        catch (Exception ex)
        {
            log?.Warn(ConfigConstants.LogCategory,
                $"{p}: Register failed for queue '{name}' -- {ex.Message}");
            return false;
        }

        if (log is { IsDebug: true })
        {
            string Note(bool defaulted) => defaulted ? " (default)" : "";
            log.Debug(ConfigConstants.LogCategory,
                $"Queue '{name}' parsed: GameType='{gt.Name}' (id={gt.Id}, shape={gt.TeamCount}x{gt.PlayersPerTeam}), " +
                $"Matchmaking={(casual ? "casual" : "competitive")}{Note(matchmakingDefaulted)}, " +
                $"MatchArena={(string.IsNullOrWhiteSpace(arenaName) ? "(none)" : arenaName)}, " +
                $"LookAhead=+{lookAhead.Extra}{Note(lookAhead.Defaulted)} (pool={lookAhead.EffectiveTotal}), " +
                $"RelaxTime={effectiveRelax}{Note(relaxDefaulted)}, " +
                $"HoldWindow={effectiveHold}{Note(holdDefaulted)}, " +
                $"QualityCeiling={effectiveQc:F2}{Note(qcDefaulted)}, " +
                $"VetoesRequired={effectiveVetoes}{Note(vetoesDefaulted)}, " +
                $"VetoWindow={(vetoWindow is { } vwd ? vwd.ToString() : "(default 60s)")}{Note(vetoWindowDefaulted)}, " +
                $"PromoteWinners={(promoteWinners ? "yes" : "no")}, " +
                $"MaxConsecutiveDefenses={effectiveMaxDef}{Note(maxDefDefaulted)}, " +
                $"EndPolicy=[{endPolicyDesc}].");
        }
        return true;
    }

    private static (bool Casual, bool Defaulted) ReadMatchmakingTier(IConfigManager config, string p, ClashLog? log)
    {
        var raw = config.GetStr(config.Global, ConfigConstants.Section, p + "Matchmaking");
        if (string.IsNullOrWhiteSpace(raw)) return (false, true);
        if (string.Equals(raw, "casual", StringComparison.OrdinalIgnoreCase)) return (true, false);
        if (string.Equals(raw, "competitive", StringComparison.OrdinalIgnoreCase)) return (false, false);
        log?.Warn(ConfigConstants.LogCategory,
            $"{p}Matchmaking='{raw}' is not 'competitive' or 'casual'; defaulting to 'competitive'.");
        return (false, true);
    }

    /// <summary>
    /// LookAhead in conf = EXTRA candidates above the minimum required (i.e. how much further
    /// than strict FIFO the matcher is allowed to peek for better balance). LookAhead=0 means
    /// "exactly the longest-waiting TotalPlayers candidates" (strict FIFO). The engine's
    /// LookAheadWindow is the total pool size, so we add TotalPlayers here.
    /// </summary>
    private static (int Extra, int EffectiveTotal, bool Defaulted) ReadLookAhead(
        IConfigManager config, string p, int totalPlayers, ClashLog? log)
    {
        int? raw = ConfigReadHelpers.TryReadInt(config, p + "LookAhead");
        if (raw is null) return (0, totalPlayers, Defaulted: true);
        if (raw.Value < 0)
        {
            log?.Warn(ConfigConstants.LogCategory,
                $"{p}LookAhead={raw.Value} must be >=0 (extra candidates above the {totalPlayers} required); using 0.");
            return (0, totalPlayers, Defaulted: true);
        }
        return (raw.Value, totalPlayers + raw.Value, Defaulted: false);
    }

    private static (TimeSpan Effective, bool Defaulted) ReadRelaxTime(IConfigManager config, string p, bool casual, ClashLog? log)
    {
        var raw = ConfigReadHelpers.TryReadTimeSpan(config, p + "RelaxTime", log, p);
        if (raw is { } rr && rr <= TimeSpan.Zero)
        {
            log?.Warn(ConfigConstants.LogCategory, $"{p}RelaxTime={rr} must be > 0; using default.");
            raw = null;
        }
        return (raw ?? TimeSpan.FromSeconds(casual ? 45 : 120), Defaulted: raw is null);
    }

    private static (TimeSpan Effective, bool Defaulted) ReadHoldWindow(IConfigManager config, string p, ClashLog? log)
    {
        var raw = ConfigReadHelpers.TryReadTimeSpan(config, p + "HoldWindow", log, p);
        if (raw is { } hr && hr < TimeSpan.Zero)
        {
            log?.Warn(ConfigConstants.LogCategory, $"{p}HoldWindow={hr} must be >=0; using default.");
            raw = null;
        }
        return (raw ?? TimeSpan.FromSeconds(10), Defaulted: raw is null);
    }

    private static (double Effective, bool Defaulted) ReadQualityCeiling(IConfigManager config, string p, ClashLog? log)
    {
        var raw = ConfigReadHelpers.TryReadDouble(config, p + "QualityCeiling");
        if (raw is { } qc && (qc < 0 || qc > 1))
        {
            log?.Warn(ConfigConstants.LogCategory, $"{p}QualityCeiling={qc} must be in [0,1]; using default.");
            raw = null;
        }
        return (raw ?? 0.9, Defaulted: raw is null);
    }

    private static (int Effective, bool Defaulted) ReadVetoesRequired(IConfigManager config, string p, ClashLog? log)
    {
        var raw = ConfigReadHelpers.TryReadInt(config, p + "VetoesRequired");
        if (raw is { } vr && vr < 1)
        {
            log?.Warn(ConfigConstants.LogCategory, $"{p}VetoesRequired={vr} must be >=1; using default.");
            raw = null;
        }
        return (raw ?? 2, Defaulted: raw is null);
    }

    private static (TimeSpan? Value, bool Defaulted) ReadVetoWindow(IConfigManager config, string p, ClashLog? log)
    {
        var raw = ConfigReadHelpers.TryReadTimeSpan(config, p + "VetoWindow", log, p);
        if (raw is { } vw && vw <= TimeSpan.Zero)
        {
            log?.Warn(ConfigConstants.LogCategory, $"{p}VetoWindow={vw} must be > 0; using default.");
            raw = null;
        }
        return (raw, Defaulted: raw is null);
    }

    /// <summary>Reads the KOTH ("king of the hill") knobs. Off by default; when on, winners
    /// auto-re-enqueue at the head of the queue and step aside after MaxConsecutiveDefenses
    /// straight wins.</summary>
    private static (bool PromoteWinners, int MaxDef, bool MaxDefDefaulted) ReadKoth(
        IConfigManager config, string p, ClashLog? log)
    {
        bool promote = config.GetInt(config.Global, ConfigConstants.Section, p + "PromoteWinners", 0) != 0;
        var raw = ConfigReadHelpers.TryReadInt(config, p + "MaxConsecutiveDefenses");
        if (raw is { } md && md < 1)
        {
            log?.Warn(ConfigConstants.LogCategory,
                $"{p}MaxConsecutiveDefenses={md} must be >=1; using default.");
            raw = null;
        }
        return (promote, raw ?? 3, MaxDefDefaulted: raw is null);
    }

    /// <summary>
    /// End policy: <c>KillTarget</c> and <c>TimeLimit</c> can both be set, in which case
    /// whichever fires first wins via <see cref="CompositeEndPolicy"/>. If neither is set we
    /// fall back to a default <c>KillTarget</c> of 30 so an under-specified game type still
    /// terminates.
    /// </summary>
    private static (Func<IMatchEndPolicy> Factory, string Description) BuildEndPolicy(GameTypeDef gt)
    {
        bool hasKill = gt.KillTarget > 0;
        bool hasTime = gt.TimeLimit is { } limCheck && limCheck > TimeSpan.Zero;

        if (hasKill && hasTime)
        {
            int kt = gt.KillTarget;
            var lim = gt.TimeLimit!.Value;
            return (
                () => new CompositeEndPolicy(new KillCountEndPolicy(kt), new TimeLimitEndPolicy(lim)),
                $"first to {kt} kills OR leader at {lim} (sudden-death overtime)");
        }
        if (hasTime)
        {
            var lim = gt.TimeLimit!.Value;
            return (
                () => new TimeLimitEndPolicy(lim),
                $"leader at {lim} (sudden-death overtime)");
        }
        int kills = hasKill ? gt.KillTarget : 30;
        return (
            () => new KillCountEndPolicy(kills),
            $"first to {kills} kills" + (hasKill ? "" : " (default; KillTarget unset)"));
    }
}
