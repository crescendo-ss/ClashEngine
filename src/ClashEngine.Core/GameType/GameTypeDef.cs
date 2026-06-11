using System;
using System.Collections.Generic;
using ClashEngine.Core.Queue;

namespace ClashEngine.Core.GameType;

/// <summary>
/// Pure-data view of one configured game type. Produced by the host's <c>[ClashEngine]</c>
/// parser and stored in <see cref="GameTypeRegistry"/>; queues resolve their <c>GameType</c>
/// reference through the registry by name.
/// </summary>
/// <remarks>
/// <para>The <see cref="Name"/> is the registry identifier and matches the
/// <c>match.gameType</c> string on the v5 match-upload schema and the <c>name</c> field on
/// the gametype-registration POST. <see cref="Label"/>, <see cref="Description"/>, and
/// <see cref="Metadata"/> are version-frozen on the stats server (per the gametype schema)
/// -- a subsequent re-POST with the same name but different fields creates a new version
/// on the server side; ClashEngine itself stores only the current parsed view here.</para>
/// <para>The remaining knobs (team shape, lives, spawn layout, durations) drive
/// match formation locally. They are summarized into <see cref="Metadata"/> for the upload
/// payload but are otherwise ClashEngine-internal.</para>
/// <para>The griefing detectors (<see cref="EarlyExitPenalty"/>, <see cref="TeamkillPenalty"/>)
/// are opt-in and default off; <see cref="EarlyExitMinimumDuration"/> and
/// <see cref="TeamkillThreshold"/> are <see langword="null"/> for "engine default", resolved by
/// <see cref="Penalties.GriefingHeuristicSelector"/>.</para>
/// </remarks>
public sealed record GameTypeDef(
    string Name,
    string Label,
    string? Description,
    GameTypeMetadata Metadata,
    int TeamCount,
    int PlayersPerTeam,
    int KillTarget,
    int Lives,
    TimeSpan? TimeLimit,
    IReadOnlyList<IReadOnlyList<StartPoint>>? StartSetByTeam,
    int? MaxStartDriftTiles,
    bool UseStartLocation,
    IReadOnlyList<SpawnArea?>? SpawnByTeam,
    TimeSpan? StagingDuration,
    TimeSpan? CountdownDuration,
    TimeSpan? KnockoutSpecDelay,
    TimeSpan? TeamCollapseGrace,
    TimeSpan? ShipChangeGracePeriod,
    ItemsAction ReturnItemsAction,
    TimeSpan? EliminationCooldown = null,
    bool DisallowItems = false,
    SpawnArea? PresenceZone = null,
    TimeSpan? PresenceZoneTimeout = null,
    bool EarlyExitPenalty = false,
    TimeSpan? EarlyExitMinimumDuration = null,
    bool TeamkillPenalty = false,
    int? TeamkillThreshold = null);

/// <summary>
/// Auto-derived <c>metadata</c> blob attached to the gametype-registration POST. Follows the
/// conventional shape called out in <c>schema/gametype.schema.json</c>:
/// <c>{ teamCount, teamSizes, livesPerPlayer }</c>. The stats server stores the blob verbatim
/// and does not enforce its inner structure -- we still emit it shape-consistent so other
/// consumers (scoreboards, dashboards) can read it without per-arena reshaping.
/// </summary>
/// <remarks>
/// <see cref="TeamSizes"/> mirrors <see cref="GameTypeDef.PlayersPerTeam"/> across every team
/// (uniform shape, since ClashEngine doesn't support asymmetric teams yet).
/// <see cref="LivesPerPlayer"/> is null for "unlimited"; otherwise an N×PerTeam matrix of the
/// uniform per-player lives count (room to go asymmetric later without breaking the wire shape).
/// </remarks>
public sealed record GameTypeMetadata(
    int TeamCount,
    IReadOnlyList<int> TeamSizes,
    IReadOnlyList<IReadOnlyList<int>>? LivesPerPlayer)
{
    /// <summary>
    /// Builds the conventional metadata blob for a uniform-shape gametype: every team has
    /// <paramref name="playersPerTeam"/> slots, and each slot has <paramref name="lives"/>
    /// lives (or unlimited when <paramref name="lives"/> &lt;= 0).
    /// </summary>
    public static GameTypeMetadata Uniform(int teamCount, int playersPerTeam, int lives)
    {
        if (teamCount < 1) throw new ArgumentOutOfRangeException(nameof(teamCount));
        if (playersPerTeam < 1) throw new ArgumentOutOfRangeException(nameof(playersPerTeam));

        var sizes = new int[teamCount];
        for (int i = 0; i < teamCount; i++) sizes[i] = playersPerTeam;

        IReadOnlyList<IReadOnlyList<int>>? livesMatrix = null;
        if (lives > 0)
        {
            var matrix = new int[teamCount][];
            for (int t = 0; t < teamCount; t++)
            {
                matrix[t] = new int[playersPerTeam];
                for (int j = 0; j < playersPerTeam; j++) matrix[t][j] = lives;
            }
            livesMatrix = matrix;
        }

        return new GameTypeMetadata(teamCount, sizes, livesMatrix);
    }
}
