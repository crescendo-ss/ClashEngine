using System;
using ClashEngine.Core;
using SS.Core;
using SS.Core.ComponentInterfaces;

namespace ClashEngine.Config;

/// <summary>
/// Public entry point for parsing the <c>[ClashEngine]</c> section of <c>global.conf</c> and
/// registering the resulting queue catalog with <see cref="MatchmakingEngine"/>. The format
/// separates rules ("game type") from matchmaking policy ("queue") so multiple queues can
/// share one game type without duplicating its rules.
/// </summary>
/// <remarks>
/// <para>This file is the orchestrator -- it reads <c>QueueCount</c>, delegates game-type
/// parsing to <see cref="GameTypeParser"/>, and registers each queue via
/// <see cref="QueueParser"/>. The actual parsing logic, the <see cref="GameTypeDef"/> record,
/// and the per-key validation live in the sibling files in this directory.</para>
///
/// <para>Validation: every value is sanity-checked. Bad values (negative lives, unknown
/// matchmaking strictness, missing game-type reference, etc.) are logged at Warn and either
/// skipped or replaced with a default. Every parsed game type and queue emits a Debug-level
/// summary showing the final values used (handy for triage when defaults kick in).</para>
///
/// <para>Example:</para>
/// <code>
/// [ClashEngine]
/// GameTypeCount = 1
/// GameType1Name           = elimination_3v3
/// GameType1Id             = 1
/// GameType1TeamCount      = 2
/// GameType1PlayersPerTeam = 3
/// GameType1KillTarget     = 30                  ; either or both of KillTarget +
/// GameType1TimeLimit      = 0:20:00              ; TimeLimit may be set; whichever
/// GameType1Lives          = 5                    ; fires first ends the match.
/// GameType1WarpOnSpawn      = 1                  ; gate: 0/1; off by default
/// GameType1Team1Spawns      = 480,256;480,257    ; semicolon-separated x,y in tiles
/// GameType1Team2Spawns      = 544,256;544,257    ; no spaces between semi-colons
/// GameType1Team1Ships       = warbird,javelin,spider ; per-slot ship; names or 0..7
/// GameType1Team2Ships       = warbird,javelin,spider
/// GameType1MaxSpawnDrift    = 8                  ; tiles (16 px); pre-GO drift warp-back
/// GameType1StagingDuration  = 10                 ; seconds or HH:MM:SS; warmup (default 10s)
/// GameType1CountdownDuration = 5                 ; seconds or HH:MM:SS; countdown (min/default 5s)
/// GameType1KnockoutSpecDelay = 2                 ; seconds; grace before specing on last-life
///                                                ; death so residual mines/bombs can land.
///                                                ; Default 0 (immediate).
/// GameType1TeamCollapseGrace = 10                ; seconds; how long an entire team can be without
///                                                ; live members before forfeiting. Default 10s.
/// GameType1ShipChangeGracePeriod = 10            ; seconds; window after a non-fatal death during
///                                                ; which a player may swap ships (mid-life
///                                                ; changes are otherwise blocked). Default 10s;
///                                                ; set to 0 to forbid all in-match changes.
///
/// QueueCount = 2
/// Queue1Name              = 3v3comp
/// Queue1GameType          = elimination_3v3
/// Queue1Matchmaking       = competitive
/// Queue1MatchArena        = 3v3match
/// Queue1LookAhead         = 4    ; consider 4 extra candidates beyond the 6 required
/// Queue1RelaxTime         = 0:01:30
/// Queue1VetoesRequired    = 2    ; griefing-veto threshold (default 2)
/// Queue1VetoWindow        = 0:01:00 ; veto open period (default 60s)
/// Queue1PromoteWinners    = 0    ; KOTH: 1 = re-enqueue winners at the head (default 0)
/// Queue1MaxConsecutiveDefenses = 3 ; KOTH: champions step aside after this many wins
///
/// Queue2Name        = 3v3casual
/// Queue2GameType    = elimination_3v3
/// Queue2Matchmaking = casual
/// Queue2MatchArena  = 3v3casual
/// Queue2RelaxTime   = 0:00:45
/// </code>
/// </remarks>
public static class MatchmakingConfig
{
    /// <summary>
    /// Apply the configured queue catalog to <paramref name="engine"/>. Pass a <see cref="ClashLog"/>
    /// to surface parse Debug + Warn messages; pass <see langword="null"/> to silence them. If the
    /// config section is absent or specifies no queues, no queues are registered -- the operator
    /// must explicitly opt in via <c>QueueCount</c> &gt; 0 and matching <c>Queue&lt;i&gt;</c> blocks.
    /// </summary>
    public static void ApplyTo(MatchmakingEngine engine, IConfigManager config, ClashLog? log = null)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(config);

        int queueCount = config.GetInt(config.Global, ConfigConstants.Section, "QueueCount", 0);
        if (queueCount <= 0)
        {
            log?.Info(ConfigConstants.LogCategory, "[ClashEngine] QueueCount unset/0 -- no queues registered.");
            return;
        }

        // Game types load first so queues can resolve their references by name.
        var gameTypes = GameTypeParser.ReadAll(config, log);
        log?.Info(ConfigConstants.LogCategory,
            $"Loaded {gameTypes.Count} game type(s); reading {queueCount} queue(s).");

        int registered = 0;
        for (int i = 1; i <= queueCount; i++)
            if (QueueParser.ReadAndRegister(engine, config, i, gameTypes, log)) registered++;
        log?.Info(ConfigConstants.LogCategory, $"Registered {registered} of {queueCount} queue(s).");
    }

    /// <summary>
    /// Returns the configured <c>DefaultQueue</c> for the given arena, or <see langword="null"/>
    /// if the arena does not declare one.
    /// </summary>
    public static string? DefaultQueueForArena(IConfigManager config, ConfigHandle arenaConf)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(arenaConf);
        return config.GetStr(arenaConf, ConfigConstants.Section, "DefaultQueue");
    }
}
