using System;
using System.Collections.Generic;
using ClashEngine.Core;
using ClashEngine.Core.GameType;
using ClashEngine.Core.Matches;
using ClashEngine.Core.Matching;
using ClashEngine.Core.Penalties;
using ClashEngine.Core.Queue;
using SS.Core;
using SS.Core.ComponentInterfaces;

namespace ClashEngine.Config;

/// <summary>
/// Parses one <c>Queue&lt;i&gt;</c> block from the <c>[ClashEngine]</c> section of a given
/// <see cref="ConfigHandle"/> (the arena's arena.conf document, including anything
/// <c>#include</c>'d from it). Returns a <see cref="QueueDefinition"/> (or
/// <see langword="null"/> if the block is missing / invalid). The caller is responsible for
/// registering the resulting definition with the engine's <see cref="QueueRegistry"/>.
/// </summary>
internal static class QueueParser
{
    /// <summary>
    /// Reads <c>Queue&lt;index&gt;</c> from <paramref name="handle"/> and returns the parsed
    /// definition. The returned queue's <see cref="QueueDefinition.UniqueId"/> is qualified with
    /// <paramref name="ownerArenaName"/> (e.g. "<c>lobby/3v3comp</c>") so two arenas can both
    /// declare a queue named "<c>3v3comp</c>" without collision in the global registry.
    /// </summary>
    public static QueueDefinition? ParseOne(
        IConfigManager config,
        ConfigHandle handle,
        int index,
        string ownerArenaName,
        IReadOnlyDictionary<string, GameTypeDef> gameTypes,
        ClashLog? log)
    {
        ArgumentException.ThrowIfNullOrEmpty(ownerArenaName);

        string p = $"Queue{index}";
        var baseName = config.GetStr(handle, ConfigConstants.Section, p + "Name");
        if (string.IsNullOrWhiteSpace(baseName))
        {
            log?.Warn(ConfigConstants.LogCategory, $"{p}Name missing -- skipping this queue slot.");
            return null;
        }
        baseName = baseName.Trim();

        var gtName = config.GetStr(handle, ConfigConstants.Section, p + "GameType");
        if (string.IsNullOrWhiteSpace(gtName))
        {
            log?.Warn(ConfigConstants.LogCategory,
                $"{p}GameType missing for queue '{baseName}' -- skipping.");
            return null;
        }
        if (!gameTypes.TryGetValue(gtName, out var gt))
        {
            // Game types are globally referenceable and arenas attach in arbitrary order, so a
            // reference to a not-yet-registered game type is normal during startup -- log at Info,
            // not Warn. The queue is left unloaded and resolves on a later reconcile once that game
            // type is declared by some arena. (A genuine typo also lands here; it just never
            // resolves -- the Info line names the game type so it's still diagnosable.)
            log?.Info(ConfigConstants.LogCategory,
                $"{p}GameType='{gtName}' for queue '{baseName}' is not registered (yet) -- queue not loaded; " +
                "will resolve if/when that game type is declared in any arena.");
            return null;
        }

        bool casual = ReadPreset(config, handle, p, log);
        var arenaName = config.GetStr(handle, ConfigConstants.Section, p + "MatchArena");
        var label = config.GetStr(handle, ConfigConstants.Section, p + "Label");

        var lookAhead = ReadLookAhead(config, handle, p, gt.TeamCount * gt.PlayersPerTeam, log);
        var (effectiveRelax, relaxDefaulted) = ReadRelaxTime(config, handle, p, casual, log);
        var (effectiveHold, holdDefaulted) = ReadHoldWindow(config, handle, p, log);
        var (effectiveQc, qcDefaulted) = ReadQualityCeiling(config, handle, p, log);
        var (effectiveVetoes, vetoesDefaulted) = ReadVetoesRequired(config, handle, p, log);
        var (vetoWindow, vetoWindowDefaulted) = ReadVetoWindow(config, handle, p, log);
        var (promoteWinners, effectiveMaxDef, maxDefDefaulted) = ReadKoth(config, handle, p, log);
        var (afkWarn, afkCull) = ReadAfkDwell(config, handle, p, log);
        // Default true: pin the longest waiter into every candidate set (strict FIFO fairness).
        // Off lets the matcher pass the head over for a better-balanced subset from the pool.
        bool alwaysChooseLongestWaiter = config.GetBool(
            handle, ConfigConstants.Section, p + "AlwaysChooseLongestWaiter", defaultValue: true);

        // Preset defaults (only used when the corresponding explicit key wasn't set). Each row
        // is overridable -- explicit Queue<i><Key> always wins.
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

        QueueDefinition def;
        try
        {
            def = new QueueDefinition(
                uniqueId: QueueRegistry.QualifyName(ownerArenaName, baseName),
                shape: shape,
                qualityPolicy: quality,
                gameType: gt.Name,
                endPolicyFactory: endPolicy,
                griefingHeuristicFactory: griefing,
                vetoesRequired: effectiveVetoes,
                vetoWindow: vetoWindow,
                ratingWeight: casual ? 0.5 : 1.0,
                matchArenaName: string.IsNullOrWhiteSpace(arenaName) ? null : arenaName,
                startSetByTeam: gt.StartSetByTeam,
                maxStartDriftTiles: gt.MaxStartDriftTiles,
                useStartLocation: gt.UseStartLocation,
                spawnByTeam: gt.SpawnByTeam,
                stagingDuration: gt.StagingDuration,
                countdownDuration: gt.CountdownDuration,
                lookAheadWindow: lookAhead.EffectiveTotal,
                alwaysChooseLongestWaiter: alwaysChooseLongestWaiter,
                promoteWinnersToFront: promoteWinners,
                maxConsecutiveDefenses: effectiveMaxDef,
                holdWindow: effectiveHold,
                qualityCeiling: effectiveQc,
                knockoutSpecDelay: gt.KnockoutSpecDelay,
                livesPerPlayer: gt.Lives > 0 ? gt.Lives : null,
                teamCollapseGrace: gt.TeamCollapseGrace,
                shipChangeGracePeriod: gt.ShipChangeGracePeriod,
                timeLimit: gt.TimeLimit,
                returnItemsAction: gt.ReturnItemsAction,
                disallowItems: gt.DisallowItems,
                ownerArenaName: ownerArenaName,
                label: label,
                afkDwellWarning: afkWarn,
                afkDwellCull: afkCull,
                eliminationCooldown: gt.EliminationCooldown);
        }
        catch (Exception ex)
        {
            log?.Warn(ConfigConstants.LogCategory,
                $"{p}: construct failed for queue '{baseName}' -- {ex.Message}");
            return null;
        }

        if (log is { IsDebug: true })
        {
            string Note(bool defaulted) => defaulted ? " (default)" : "";
            log.Debug(ConfigConstants.LogCategory,
                $"Queue '{def.UniqueId}' parsed: GameType='{gt.Name}' (shape={gt.TeamCount}x{gt.PlayersPerTeam}), " +
                $"Preset={(casual ? "casual" : "(none)")}, " +
                $"Label='{def.Label}', " +
                $"MatchArena={(string.IsNullOrWhiteSpace(arenaName) ? "(none)" : arenaName)}, " +
                $"LookAhead=+{lookAhead.Extra}{Note(lookAhead.Defaulted)} (pool={lookAhead.EffectiveTotal}), " +
                $"AlwaysChooseLongestWaiter={alwaysChooseLongestWaiter}, " +
                $"RelaxTime={effectiveRelax}{Note(relaxDefaulted)}, " +
                $"HoldWindow={effectiveHold}{Note(holdDefaulted)}, " +
                $"QualityCeiling={effectiveQc:F2}{Note(qcDefaulted)}, " +
                $"VetoesRequired={effectiveVetoes}{Note(vetoesDefaulted)}, " +
                $"VetoWindow={(vetoWindow is { } vwd ? vwd.ToString() : "(default 60s)")}{Note(vetoWindowDefaulted)}, " +
                $"PromoteWinners={(promoteWinners ? "yes" : "no")}, " +
                $"MaxConsecutiveDefenses={effectiveMaxDef}{Note(maxDefDefaulted)}, " +
                $"AfkWarn={DescribeAfk(afkWarn)}, AfkCull={DescribeAfk(afkCull)}, " +
                $"EndPolicy=[{endPolicyDesc}].");
        }
        return def;
    }

    /// <summary>
    /// Reads the optional <c>Queue&lt;i&gt;Preset</c> shortcut. <c>casual</c> is the only legal
    /// value today and expands into the lenient bundle of defaults (q-bands, MaxLiabilityGap,
    /// RelaxTime, RatingWeight, griefing threshold). Each individual knob can still be
    /// overridden by an explicit per-key setting. Unknown preset values log a Warn and apply
    /// nothing.
    /// </summary>
    private static bool ReadPreset(IConfigManager config, ConfigHandle handle, string p, ClashLog? log)
    {
        var raw = config.GetStr(handle, ConfigConstants.Section, p + "Preset");
        if (string.IsNullOrWhiteSpace(raw)) return false;
        if (string.Equals(raw, "casual", StringComparison.OrdinalIgnoreCase)) return true;
        log?.Warn(ConfigConstants.LogCategory,
            $"{p}Preset='{raw}' is not a recognized preset (expected 'casual'); ignoring (defaults applied).");
        return false;
    }

    /// <summary>
    /// LookAhead in conf = EXTRA candidates above the minimum required (i.e. how much further
    /// than strict FIFO the matcher is allowed to peek for better balance). LookAhead=0 means
    /// "exactly the longest-waiting TotalPlayers candidates" (strict FIFO). The engine's
    /// LookAheadWindow is the total pool size, so we add TotalPlayers here.
    /// </summary>
    private static (int Extra, int EffectiveTotal, bool Defaulted) ReadLookAhead(
        IConfigManager config, ConfigHandle handle, string p, int totalPlayers, ClashLog? log)
    {
        int? raw = ConfigReadHelpers.TryReadInt(config, handle, p + "LookAhead");
        if (raw is null) return (0, totalPlayers, Defaulted: true);
        if (raw.Value < 0)
        {
            log?.Warn(ConfigConstants.LogCategory,
                $"{p}LookAhead={raw.Value} must be >=0 (extra candidates above the {totalPlayers} required); using 0.");
            return (0, totalPlayers, Defaulted: true);
        }
        return (raw.Value, totalPlayers + raw.Value, Defaulted: false);
    }

    private static (TimeSpan Effective, bool Defaulted) ReadRelaxTime(IConfigManager config, ConfigHandle handle, string p, bool casual, ClashLog? log)
    {
        var raw = ConfigReadHelpers.TryReadTimeSpan(config, handle, p + "RelaxTime", log, p);
        if (raw is { } rr && rr <= TimeSpan.Zero)
        {
            log?.Warn(ConfigConstants.LogCategory, $"{p}RelaxTime={rr} must be > 0; using default.");
            raw = null;
        }
        return (raw ?? TimeSpan.FromSeconds(casual ? 45 : 120), Defaulted: raw is null);
    }

    private static (TimeSpan Effective, bool Defaulted) ReadHoldWindow(IConfigManager config, ConfigHandle handle, string p, ClashLog? log)
    {
        var raw = ConfigReadHelpers.TryReadTimeSpan(config, handle, p + "HoldWindow", log, p);
        if (raw is { } hr && hr < TimeSpan.Zero)
        {
            log?.Warn(ConfigConstants.LogCategory, $"{p}HoldWindow={hr} must be >=0; using default.");
            raw = null;
        }
        return (raw ?? TimeSpan.FromSeconds(10), Defaulted: raw is null);
    }

    private static (double Effective, bool Defaulted) ReadQualityCeiling(IConfigManager config, ConfigHandle handle, string p, ClashLog? log)
    {
        var raw = ConfigReadHelpers.TryReadDouble(config, handle, p + "QualityCeiling");
        if (raw is { } qc && (qc < 0 || qc > 1))
        {
            log?.Warn(ConfigConstants.LogCategory, $"{p}QualityCeiling={qc} must be in [0,1]; using default.");
            raw = null;
        }
        return (raw ?? 0.9, Defaulted: raw is null);
    }

    private static (int Effective, bool Defaulted) ReadVetoesRequired(IConfigManager config, ConfigHandle handle, string p, ClashLog? log)
    {
        var raw = ConfigReadHelpers.TryReadInt(config, handle, p + "VetoesRequired");
        if (raw is { } vr && vr < 1)
        {
            log?.Warn(ConfigConstants.LogCategory, $"{p}VetoesRequired={vr} must be >=1; using default.");
            raw = null;
        }
        return (raw ?? 2, Defaulted: raw is null);
    }

    private static (TimeSpan? Value, bool Defaulted) ReadVetoWindow(IConfigManager config, ConfigHandle handle, string p, ClashLog? log)
    {
        var raw = ConfigReadHelpers.TryReadTimeSpan(config, handle, p + "VetoWindow", log, p);
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
        IConfigManager config, ConfigHandle handle, string p, ClashLog? log)
    {
        bool promote = config.GetInt(handle, ConfigConstants.Section, p + "PromoteWinners", 0) != 0;
        var raw = ConfigReadHelpers.TryReadInt(config, handle, p + "MaxConsecutiveDefenses");
        if (raw is { } md && md < 1)
        {
            log?.Warn(ConfigConstants.LogCategory,
                $"{p}MaxConsecutiveDefenses={md} must be >=1; using default.");
            raw = null;
        }
        return (promote, raw ?? 3, MaxDefDefaulted: raw is null);
    }

    /// <summary>
    /// Reads the in-queue AFK dwell thresholds. <c>Queue&lt;i&gt;AfkWarn</c> fires a one-time
    /// "still there?" warning after a player has waited that long; <c>Queue&lt;i&gt;AfkCull</c>
    /// auto-dequeues them after that long. Both accept seconds or <c>HH:MM:SS</c>. When a key is
    /// unset it defaults to 15 min (warn) / 20 min (cull); an explicit <c>0</c> disables that
    /// stage (<c>AfkWarn=0</c> disables warning AND culling for the queue; <c>AfkCull=0</c> keeps
    /// the warning but never culls). A cull below the warn is raised to the warn (with a notice)
    /// rather than dropping the queue.
    /// </summary>
    private static (TimeSpan Warn, TimeSpan Cull) ReadAfkDwell(
        IConfigManager config, ConfigHandle handle, string p, ClashLog? log)
    {
        var warn = ConfigReadHelpers.TryReadTimeSpan(config, handle, p + "AfkWarn", log, p)
            ?? TimeSpan.FromMinutes(15);
        var cull = ConfigReadHelpers.TryReadTimeSpan(config, handle, p + "AfkCull", log, p)
            ?? TimeSpan.FromMinutes(20);

        if (warn < TimeSpan.Zero) warn = TimeSpan.Zero;
        if (cull < TimeSpan.Zero) cull = TimeSpan.Zero;

        if (warn > TimeSpan.Zero && cull > TimeSpan.Zero && cull < warn)
        {
            log?.Warn(ConfigConstants.LogCategory,
                $"{p}AfkCull ({cull}) is below {p}AfkWarn ({warn}); raising cull to the warn threshold.");
            cull = warn;
        }
        return (warn, cull);
    }

    private static string DescribeAfk(TimeSpan ts) => ts <= TimeSpan.Zero ? "off" : ts.ToString();

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
