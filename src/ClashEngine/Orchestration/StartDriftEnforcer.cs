using System.Collections.Generic;
using ClashEngine.Core.Identity;
using ClashEngine.Core.Matching;
using ClashEngine.Core.Queue;

namespace ClashEngine.Orchestration;

/// <summary>
/// Per-match match-<b>start</b> pick + pre-GO drift-back enforcer. At setup time
/// <see cref="ChooseStartForEachTeam"/> picks one entry from each team's configured start-location
/// set. While the match is in Staging or Countdown, <see cref="ShouldWarpBack"/> answers whether a
/// player has wandered too far from their team's chosen start. (In-match respawn placement is a
/// separate concern, handled by client-settings overrides -- see <see cref="ClashEngine.Adapter.SpawnSettingsApplier"/>.)
/// </summary>
/// <remarks>
/// All distance math is done in <b>pixel</b> units (squared, no <c>sqrt</c>): the position-packet
/// coordinates are pixels and the <see cref="StartPoint"/> is tiles (1 tile = 16 px), so the start
/// is compared via its <see cref="StartPoint.PixelX"/>/<see cref="StartPoint.PixelY"/> form.
/// <see cref="QueueDefinition.MaxStartDriftTiles"/> is a tile budget, so
/// <see cref="ShouldWarpBack"/> scales it up by 16 before the comparison. Returning
/// <see langword="false"/> for "no start override configured" rather than a magic value keeps the
/// call site readable.
/// </remarks>
internal sealed class StartDriftEnforcer
{
    private readonly QueueDefinition _queue;
    private readonly Dictionary<PlayerKey, int> _teamIndexByPlayer = new();
    private readonly Dictionary<int, StartPoint> _chosenStartByTeam = new();

    public StartDriftEnforcer(QueueDefinition queue, MatchProposal proposal)
    {
        _queue = queue;
        for (int t = 0; t < proposal.Teams.Count; t++)
            for (int j = 0; j < proposal.Teams[t].Count; j++)
                _teamIndexByPlayer[proposal.Teams[t][j]] = t;
    }

    /// <summary>Picks one start location for each team from the queue's configured set, using
    /// <paramref name="rng"/> for the per-team selection. <see cref="ChosenStart"/> returns
    /// the picks afterwards.</summary>
    public void ChooseStartForEachTeam(MatchProposal proposal, IRandomSource rng)
    {
        for (int t = 0; t < proposal.Teams.Count; t++)
            _chosenStartByTeam[t] = PickStartFor(t, rng);
    }

    /// <summary>Returns the start location picked for <paramref name="teamIdx"/>, or
    /// <see langword="default"/> if none was set (no start override configured).</summary>
    public StartPoint ChosenStart(int teamIdx) =>
        _chosenStartByTeam.TryGetValue(teamIdx, out var sp) ? sp : default;

    /// <summary>
    /// True iff drift enforcement is configured and <paramref name="player"/> has wandered
    /// more than <see cref="QueueDefinition.MaxStartDriftTiles"/> tiles from their team's
    /// chosen start location. <paramref name="xPixels"/> and <paramref name="yPixels"/> are the
    /// player's position-packet coordinates (pixels); they compare against the tile-valued
    /// <see cref="StartPoint"/> via its pixel form, with the tile drift budget scaled up by 16.
    /// Outputs the start point (tiles) to warp the player back to.
    /// </summary>
    public bool ShouldWarpBack(PlayerKey player, short xPixels, short yPixels, out StartPoint backTo)
    {
        backTo = default;
        if (!_queue.UseStartLocation) return false;
        if (_queue.MaxStartDriftTiles is not { } maxTiles || maxTiles <= 0) return false;
        if (!_teamIndexByPlayer.TryGetValue(player, out var teamIdx)) return false;
        if (!_chosenStartByTeam.TryGetValue(teamIdx, out var start)) return false;
        if (start.X == 0 && start.Y == 0) return false;

        int dxPixels = xPixels - start.PixelX;
        int dyPixels = yPixels - start.PixelY;
        long distSq = (long)dxPixels * dxPixels + (long)dyPixels * dyPixels;
        long maxPixels = (long)maxTiles * 16;
        long maxSq = maxPixels * maxPixels;
        if (distSq <= maxSq) return false;

        backTo = start;
        return true;
    }

    private StartPoint PickStartFor(int teamIdx, IRandomSource rng)
    {
        if (!_queue.UseStartLocation) return default;
        if (_queue.StartSetByTeam is null) return default;
        if (teamIdx >= _queue.StartSetByTeam.Count) return default;
        var set = _queue.StartSetByTeam[teamIdx];
        if (set.Count == 0) return default;
        if (set.Count == 1) return set[0];
        return set[rng.Next(set.Count)];
    }
}
