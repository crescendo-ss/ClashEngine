using System;
using System.Collections.Generic;
using ClashEngine.Core.Queue;

namespace ClashEngine.Core.GameType;

/// <summary>
/// Pure-data view of one configured game type. Produced by the host's [ClashEngine] parser and
/// stored in <see cref="GameTypeRegistry"/>; queues resolve their <c>GameType</c> reference
/// through the registry by name. Two contributions sharing a name (and the same
/// <see cref="Id"/>) are treated as the same logical game type for collision purposes;
/// mismatched IDs are rejected.
/// </summary>
public sealed record GameTypeDef(
    string Name,
    uint Id,
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
    TimeSpan? ShipChangeGracePeriod,
    ItemsAction ReturnItemsAction);
