using System;
using System.Collections.Generic;
using ClashEngine.Core.Identity;

namespace ClashEngine.Core.Stats;

/// <summary>
/// Owns one <see cref="StatsRecorder"/> per active match and the player->match index used by
/// the adapter to dispatch wire events to the right recorder. Lifecycle hooks
/// (<see cref="BeginMatch"/>, <see cref="EndMatch"/>) bracket a match's accounting; players
/// are added inside that bracket via <see cref="AddPlayer"/>.
/// </summary>
public sealed class MatchStatsRegistry
{
    private readonly Dictionary<Guid, StatsRecorder> _recorders = new();
    private readonly Dictionary<PlayerKey, Guid> _matchOf = new();
    private readonly DamageDecay _decay;
    private readonly double _assistThresholdFraction;

    public MatchStatsRegistry(
        DamageDecay decay,
        double assistThresholdFraction = StatsRecorder.DefaultAssistThresholdFraction)
    {
        ArgumentNullException.ThrowIfNull(decay);
        if (assistThresholdFraction < 0 || assistThresholdFraction > 1)
            throw new ArgumentOutOfRangeException(nameof(assistThresholdFraction), "Must be in [0, 1].");
        _decay = decay;
        _assistThresholdFraction = assistThresholdFraction;
    }

    public IReadOnlyDictionary<Guid, StatsRecorder> ActiveRecorders => _recorders;

    /// <summary>Open a new accounting bracket for <paramref name="matchId"/>.</summary>
    public StatsRecorder BeginMatch(Guid matchId)
    {
        if (_recorders.ContainsKey(matchId))
            throw new InvalidOperationException($"Match {matchId:N} already has a recorder.");
        var recorder = new StatsRecorder(_decay, _assistThresholdFraction);
        _recorders[matchId] = recorder;
        return recorder;
    }

    /// <summary>
    /// Register <paramref name="player"/> as a participant in <paramref name="matchId"/> with
    /// their starting ship parameters. Forwards to the underlying recorder.
    /// </summary>
    public void AddPlayer(
        Guid matchId,
        PlayerKey player,
        int teamIndex,
        int maxEnergy,
        double rechargeRate,
        WeaponEnergyConfig energyConfig,
        uint atTick,
        IReadOnlyDictionary<ItemKind, int>? initialInventory = null,
        IReadOnlyDictionary<ItemKind, int>? maxInventory = null)
    {
        if (!_recorders.TryGetValue(matchId, out var recorder))
            throw new InvalidOperationException($"Match {matchId:N} has no recorder; call BeginMatch first.");
        if (_matchOf.TryGetValue(player, out var existing) && existing != matchId)
            throw new InvalidOperationException($"Player {player} is already in match {existing:N}.");

        recorder.RegisterPlayer(
            player, teamIndex, maxEnergy, rechargeRate, energyConfig, atTick, initialInventory, maxInventory);
        _matchOf[player] = matchId;
    }

    /// <summary>
    /// Slot replacement: <paramref name="incoming"/> takes <paramref name="outgoing"/>'s seat
    /// in the match. The outgoing player's life is closed with <see cref="LifeEndReason.LeftMatch"/>;
    /// the incoming player is registered fresh on the same team.
    /// </summary>
    /// <remarks>
    /// The slot itself is a match-level concept tracked elsewhere (on the <c>ActiveMatch</c>);
    /// this method only updates the stats side. Both players' stats remain accessible from
    /// <see cref="StatsRecorder.Stats"/> after the swap.
    /// </remarks>
    public void OnPlayerSubbed(
        Guid matchId,
        PlayerKey outgoing,
        PlayerKey incoming,
        int teamIndex,
        int maxEnergy,
        double rechargeRate,
        WeaponEnergyConfig energyConfig,
        uint atTick)
    {
        if (!_recorders.TryGetValue(matchId, out var recorder))
            throw new InvalidOperationException($"Match {matchId:N} has no recorder.");

        recorder.OnLeaveMatch(outgoing, atTick);
        _matchOf.Remove(outgoing);

        recorder.RegisterPlayer(incoming, teamIndex, maxEnergy, rechargeRate, energyConfig, atTick);
        _matchOf[incoming] = matchId;
    }

    /// <summary>Close the match's accounting: any open lives are closed with
    /// <see cref="LifeEndReason.MatchEnded"/>, then the recorder is removed.</summary>
    public StatsRecorder? EndMatch(Guid matchId, uint atTick)
    {
        if (!_recorders.TryGetValue(matchId, out var recorder)) return null;
        recorder.OnMatchEnded(atTick);
        _recorders.Remove(matchId);
        foreach (var p in recorder.Stats.Keys)
            _matchOf.Remove(p);
        return recorder;
    }

    /// <summary>Look up the recorder a player's events should be dispatched to.</summary>
    public StatsRecorder? RecorderFor(PlayerKey player) =>
        _matchOf.TryGetValue(player, out var matchId) && _recorders.TryGetValue(matchId, out var r) ? r : null;

    /// <summary>Append a distance sample for a player. No-op if the player isn't currently in a match.</summary>
    public void OnDistanceSample(PlayerKey player, uint atTick, float distancePixels)
    {
        if (!_matchOf.TryGetValue(player, out var matchId)) return;
        if (!_recorders.TryGetValue(matchId, out var recorder)) return;
        recorder.OnDistanceSample(player, atTick, distancePixels);
    }

    /// <summary>The match a player is currently participating in, if any.</summary>
    public Guid? MatchIdOf(PlayerKey player) =>
        _matchOf.TryGetValue(player, out var matchId) ? matchId : null;
}
