using System.Collections.Generic;
using ClashEngine.Core.Identity;
using ClashEngine.Core.Matching;
using ClashEngine.Core.Queue;

namespace ClashEngine.Orchestration;

/// <summary>
/// Per-match spawn-pick + pre-GO drift-back enforcer. At setup time
/// <see cref="ChooseSpawnForEachTeam"/> picks one entry from each team's configured spawn
/// set. While the match is in Staging or Countdown, <see cref="ShouldWarpBack"/> answers
/// whether a player has wandered too far from their team's chosen spawn.
/// </summary>
/// <remarks>
/// All distance math uses squared pixel distance (no <c>sqrt</c>) and a per-tile = 16 pixel
/// conversion. Returning <see langword="false"/> for "no spawn override configured" rather
/// than a magic value keeps the call site readable.
/// </remarks>
internal sealed class SpawnDriftEnforcer
{
    private const int PixelsPerTile = 16;

    private readonly QueueDefinition _queue;
    private readonly Dictionary<PlayerKey, int> _teamIndexByPlayer = new();
    private readonly Dictionary<int, SpawnPoint> _chosenSpawnByTeam = new();

    public SpawnDriftEnforcer(QueueDefinition queue, MatchProposal proposal)
    {
        _queue = queue;
        for (int t = 0; t < proposal.Teams.Count; t++)
            for (int j = 0; j < proposal.Teams[t].Count; j++)
                _teamIndexByPlayer[proposal.Teams[t][j]] = t;
    }

    /// <summary>Picks one spawn for each team from the queue's configured set, using
    /// <paramref name="rng"/> for the per-team selection. <see cref="ChosenSpawn"/> returns
    /// the picks afterwards.</summary>
    public void ChooseSpawnForEachTeam(MatchProposal proposal, IRandomSource rng)
    {
        for (int t = 0; t < proposal.Teams.Count; t++)
            _chosenSpawnByTeam[t] = PickSpawnFor(t, rng);
    }

    /// <summary>Returns the spawn picked for <paramref name="teamIdx"/>, or
    /// <see langword="default"/> if none was set (no spawn override configured).</summary>
    public SpawnPoint ChosenSpawn(int teamIdx) =>
        _chosenSpawnByTeam.TryGetValue(teamIdx, out var sp) ? sp : default;

    /// <summary>
    /// True iff drift enforcement is configured and <paramref name="player"/> has wandered
    /// more than <see cref="QueueDefinition.MaxSpawnDriftTiles"/> tiles from their team's
    /// chosen spawn. Outputs the spawn coords to warp the player back to.
    /// </summary>
    public bool ShouldWarpBack(PlayerKey player, short x, short y, out SpawnPoint backTo)
    {
        backTo = default;
        if (!_queue.WarpOnSpawn) return false;
        if (_queue.MaxSpawnDriftTiles is not { } maxTiles || maxTiles <= 0) return false;
        if (!_teamIndexByPlayer.TryGetValue(player, out var teamIdx)) return false;
        if (!_chosenSpawnByTeam.TryGetValue(teamIdx, out var spawn)) return false;
        if (spawn.X == 0 && spawn.Y == 0) return false;

        int dx = x - spawn.X;
        int dy = y - spawn.Y;
        long distSq = (long)dx * dx + (long)dy * dy;
        long maxPixels = (long)maxTiles * PixelsPerTile;
        long maxSq = maxPixels * maxPixels;
        if (distSq <= maxSq) return false;

        backTo = spawn;
        return true;
    }

    private SpawnPoint PickSpawnFor(int teamIdx, IRandomSource rng)
    {
        if (!_queue.WarpOnSpawn) return default;
        if (_queue.SpawnSetByTeam is null) return default;
        if (teamIdx >= _queue.SpawnSetByTeam.Count) return default;
        var set = _queue.SpawnSetByTeam[teamIdx];
        if (set.Count == 0) return default;
        if (set.Count == 1) return set[0];
        return set[rng.Next(set.Count)];
    }
}
