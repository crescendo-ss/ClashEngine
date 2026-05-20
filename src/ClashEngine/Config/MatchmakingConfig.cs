using System;
using System.Collections.Generic;
using ClashEngine.Core;
using ClashEngine.Core.GameType;
using ClashEngine.Core.Queue;
using SS.Core;
using SS.Core.ComponentInterfaces;

namespace ClashEngine.Config;

/// <summary>
/// Parses the <c>[ClashEngine]</c> section from a SubspaceServer config document (either an
/// arena.conf or global.conf, plus everything <c>#include</c>'d from it) and produces
/// gametype + queue definitions that <see cref="ClashModule"/> can commit to the engine
/// registries.
/// </summary>
/// <remarks>
/// <para>This file is the orchestrator -- it reads <c>QueueCount</c>, delegates game-type
/// parsing to <see cref="GameTypeParser"/>, and parses each queue via
/// <see cref="QueueParser"/>. The actual parsing logic, the <see cref="GameTypeDef"/>
/// record, and the per-key validation live in the sibling files in this directory.</para>
///
/// <para>The two-phase split (game types first, then queues against an accepted set) exists
/// so <see cref="ClashModule"/> can POST each parsed gametype to the stats server via
/// <see cref="IGameTypeRegistrar"/> before committing anything to the engine. The stats
/// server's accept/reject decision is the gate: only accepted gametypes appear in the local
/// <see cref="GameTypeRegistry"/>, and only queues whose gametype was accepted appear in
/// <see cref="QueueRegistry"/>. Queues that reference a rejected (or unreachable) gametype
/// are dropped with a warn rather than registered against a phantom dependency.</para>
///
/// <para>Validation: every value is sanity-checked. Bad values (negative lives, unknown
/// preset, missing game-type reference, etc.) are logged at Warn and either skipped or
/// replaced with a default. Every parsed game type and queue emits a Debug-level summary
/// showing the final values used (handy for triage when defaults kick in).</para>
///
/// <para>Example <c>[ClashEngine]</c> section (anywhere reachable from arena.conf):</para>
/// <code>
/// [ClashEngine]
/// GameTypeCount = 1
/// GameType1Name           = elimination_3v3
/// GameType1Label          = 3v3 Elimination
/// GameType1Description    = First team to 30 kills or last team standing.
/// GameType1TeamCount      = 2
/// GameType1PlayersPerTeam = 3
/// GameType1KillTarget     = 30
/// GameType1TimeLimit      = 0:20:00
/// GameType1Lives          = 5
/// GameType1WarpOnSpawn    = 1
/// GameType1Team1Spawns    = 480,256;480,257
/// GameType1Team2Spawns    = 544,256;544,257
/// GameType1Team1Ships     = warbird,javelin,spider
/// GameType1Team2Ships     = warbird,javelin,spider
///
/// QueueCount = 2
/// Queue1Name              = 3v3comp
/// Queue1Label             = 3v3 (Competitive)
/// Queue1GameType          = elimination_3v3
/// Queue1MatchArena        = 3v3match
/// Queue1RelaxTime         = 0:01:30
///
/// Queue2Name        = 3v3casual
/// Queue2Label       = 3v3 (Casual)
/// Queue2GameType    = elimination_3v3
/// Queue2Preset      = casual
/// Queue2MatchArena  = 3v3casual
/// </code>
/// </remarks>
public static class MatchmakingConfig
{
    /// <summary>
    /// Sentinel <c>originArena</c> used when posting a zone-wide gametype (one declared in
    /// global.conf rather than per-arena). The stats server still records an origin string;
    /// every zone-wide POST uses this constant so subsequent reloads address the same
    /// gametype-origin tuple.
    /// </summary>
    public const string ZoneOriginArena = "(zone)";

    /// <summary>
    /// Reads the <c>GameType&lt;i&gt;</c> blocks from <paramref name="handle"/> (arena or
    /// zone scope) and returns the parsed defs as a name-keyed dictionary. No engine state
    /// is touched; the caller is responsible for registering each def with the stats server
    /// and then committing the accepted subset.
    /// </summary>
    public static Dictionary<string, GameTypeDef> ParseGameTypes(
        IConfigManager config, ConfigHandle handle, ClashLog? log) =>
        GameTypeParser.ReadAll(config, handle, log);

    /// <summary>
    /// Reads the <c>Queue&lt;i&gt;</c> blocks from an arena's <paramref name="handle"/> and
    /// builds the queue list. Queues whose <c>Queue&lt;i&gt;GameType</c> is not present in
    /// <paramref name="acceptedGameTypes"/> are dropped with a warn -- the gametype either
    /// failed parsing, failed stats-server registration, or was rejected. <paramref name="acceptedGameTypes"/>
    /// is the post-registration filtered dictionary, NOT the raw parse output.
    /// </summary>
    public static IReadOnlyList<QueueDefinition> ParseArenaQueues(
        IConfigManager config,
        ConfigHandle handle,
        string ownerArenaName,
        IReadOnlyDictionary<string, GameTypeDef> acceptedGameTypes,
        ClashLog? log)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(handle);
        ArgumentException.ThrowIfNullOrEmpty(ownerArenaName);
        ArgumentNullException.ThrowIfNull(acceptedGameTypes);

        int queueCount = config.GetInt(handle, ConfigConstants.Section, "QueueCount", 0);
        var queues = new List<QueueDefinition>();
        for (int i = 1; i <= queueCount; i++)
        {
            if (QueueParser.ParseOne(config, handle, i, ownerArenaName, acceptedGameTypes, log) is { } def)
                queues.Add(def);
        }
        return queues;
    }

    /// <summary>
    /// Returns the configured <c>DefaultQueue</c> for the given arena, or <see langword="null"/>
    /// if the arena does not declare one. The value is the BASE queue name; the caller is
    /// responsible for qualifying it with the arena before lookup.
    /// </summary>
    public static string? DefaultQueueForArena(IConfigManager config, ConfigHandle arenaConf)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(arenaConf);
        return config.GetStr(arenaConf, ConfigConstants.Section, "DefaultQueue");
    }
}
