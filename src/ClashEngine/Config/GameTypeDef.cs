using System;
using System.Collections.Generic;
using ClashEngine.Core.Queue;

namespace ClashEngine.Config;

/// <summary>
/// Pure-data view of one configured game type, produced by <see cref="GameTypeParser"/> and
/// consumed by <see cref="QueueParser"/> when a queue references this game type by name. Once
/// a queue is registered the engine's queue catalog indexes it by (name, GameTypeId); this
/// record is a transient parse intermediate, not stored long-term.
/// </summary>
internal sealed record GameTypeDef(
    string Name,
    byte Id,
    int TeamCount,
    int PlayersPerTeam,
    int KillTarget,
    int Lives,
    TimeSpan? TimeLimit,
    IReadOnlyList<IReadOnlyList<SpawnPoint>>? SpawnSetByTeam,
    int? MaxSpawnDriftTiles,
    bool WarpOnSpawn,
    TimeSpan? StagingDuration,
    TimeSpan? CountdownDuration,
    TimeSpan? KnockoutSpecDelay,
    TimeSpan? TeamCollapseGrace,
    IReadOnlyList<IReadOnlyList<int>>? ShipBySlot,
    TimeSpan? ShipChangeGracePeriod);
