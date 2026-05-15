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
/// arena.conf or global.conf, plus everything <c>#include</c>'d from it) and produces a
/// <see cref="ClashContribution"/> that <see cref="ClashModule"/> can commit to the engine
/// registries. The actual file the section sits in is irrelevant -- the host resolves keys
/// across the whole document, so operators can split <c>[ClashEngine]</c> across includes.
/// </summary>
/// <remarks>
/// <para>This file is the orchestrator -- it reads <c>QueueCount</c>, delegates game-type
/// parsing to <see cref="GameTypeParser"/>, and parses each queue via
/// <see cref="QueueParser"/>. The actual parsing logic, the <see cref="GameTypeDef"/> record,
/// and the per-key validation live in the sibling files in this directory.</para>
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
/// GameType1Id             = 1
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
    /// Parses an arena's <c>[ClashEngine]</c> contribution from its arena.conf document handle
    /// (which transparently resolves keys across any <c>#include</c>'d files). Both the game
    /// types and the queues are owned by <paramref name="ownerArenaName"/>; queues' canonical
    /// <see cref="QueueDefinition.UniqueId"/> is qualified with the owner so two arenas can
    /// each declare a queue named e.g. "3v3comp" without collision. Returns an empty
    /// contribution if the section declares no game types or no queues.
    /// </summary>
    public static ClashContribution ParseArenaContribution(
        IConfigManager config,
        ConfigHandle handle,
        string ownerArenaName,
        ClashLog? log)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(handle);
        ArgumentException.ThrowIfNullOrEmpty(ownerArenaName);

        var gameTypes = GameTypeParser.ReadAll(config, handle, log);

        int queueCount = config.GetInt(handle, ConfigConstants.Section, "QueueCount", 0);
        var queues = new List<QueueDefinition>();
        for (int i = 1; i <= queueCount; i++)
        {
            if (QueueParser.ParseOne(config, handle, i, ownerArenaName, gameTypes, log) is { } def)
                queues.Add(def);
        }

        return new ClashContribution(ownerArenaName, gameTypes.Values, queues);
    }

    /// <summary>
    /// Parses the zone-wide <c>[ClashEngine]</c> section from global.conf (or anything
    /// <c>#include</c>'d from it). Only game types are read at zone scope -- queues without an
    /// owning arena have no addressable home under the per-arena model.
    /// </summary>
    public static IReadOnlyList<GameTypeDef> ParseZoneGameTypes(
        IConfigManager config,
        ConfigHandle handle,
        ClashLog? log)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(handle);

        var dict = GameTypeParser.ReadAll(config, handle, log);
        var list = new List<GameTypeDef>(dict.Count);
        foreach (var v in dict.Values) list.Add(v);
        return list;
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

/// <summary>
/// Bundle of definitions parsed from one arena's <c>[ClashEngine]</c> section. Passed to
/// <see cref="ClashModule"/> for atomic commit into the engine registries.
/// </summary>
public sealed record ClashContribution(
    string OwnerArenaName,
    IEnumerable<GameTypeDef> GameTypes,
    IReadOnlyList<QueueDefinition> Queues);
