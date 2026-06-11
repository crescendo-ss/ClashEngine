using System;
using System.Collections.Generic;
using ClashEngine.Core.Identity;

namespace ClashEngine.Core.Queue;

/// <summary>
/// Decision kernel for queue-liveness refresh: given raw activity signals for players, decides
/// when a queued player has shown <em>deliberate</em> in-game activity and the engine's AFK
/// dwell clock should be refreshed (<c>MatchmakingEngine.OnPlayerActivity</c>). The host adapter
/// feeds it queue membership (from telemetry) and per-player signals; this class owns the
/// rotation anchor and the forward cooldown so the engine isn't touched 10-20x/sec per player.
/// </summary>
/// <remarks>
/// <para>
/// Activity rules: a weapon fire is always activity; a rotation value is activity only when it
/// <em>differs from the previous one seen</em> — the first position signal merely seeds the
/// anchor. Mere packet receipt must never count: a motionless client keep-aliving at a constant
/// rotation is exactly the AFK case the dwell cull exists for. Position (x/y) is deliberately
/// not a signal — ships drift.
/// </para>
/// <para>
/// State exists only for tracked (queued) players, so the per-packet cost for everyone else is
/// one dictionary miss. Leaving the last queue clears the player's state entirely; a later
/// re-queue re-seeds the rotation anchor.
/// </para>
/// <para>Not thread-safe; the host drives it from the mainloop thread only.</para>
/// </remarks>
public sealed class QueueActivityTracker
{
    /// <summary>
    /// Minimum spacing between forwarded refreshes per player. AFK thresholds are minutes-scale
    /// (15/20 min defaults), so up to this much staleness in the dwell clock is invisible, while
    /// the engine write rate is capped at one per cooldown instead of per position packet.
    /// </summary>
    public static readonly TimeSpan DefaultForwardCooldown = TimeSpan.FromSeconds(10);

    private readonly TimeSpan _forwardCooldown;
    private readonly Dictionary<PlayerKey, State> _players = new();

    private sealed class State
    {
        public readonly HashSet<string> Queues = new(StringComparer.OrdinalIgnoreCase);
        public sbyte? LastRotation;
        public DateTimeOffset? LastForwardedAt;
    }

    public QueueActivityTracker(TimeSpan? forwardCooldown = null)
    {
        if (forwardCooldown is { } fc && fc < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(forwardCooldown), "Must be non-negative.");
        _forwardCooldown = forwardCooldown ?? DefaultForwardCooldown;
    }

    /// <summary>Starts (or keeps) tracking <paramref name="player"/>; refcounted per queue.</summary>
    public void OnQueueAdded(PlayerKey player, string queueName)
    {
        if (player.IsDefault || string.IsNullOrEmpty(queueName)) return;
        if (!_players.TryGetValue(player, out var state))
        {
            state = new State();
            _players[player] = state;
        }
        state.Queues.Add(queueName);
    }

    /// <summary>
    /// Drops the (player, queue) tracking; on the last queue the player's whole state (rotation
    /// anchor, cooldown) is discarded so a later re-queue starts fresh.
    /// </summary>
    public void OnQueueRemoved(PlayerKey player, string queueName)
    {
        if (!_players.TryGetValue(player, out var state)) return;
        state.Queues.Remove(queueName);
        if (state.Queues.Count == 0)
            _players.Remove(player);
    }

    public bool IsTracking(PlayerKey player) => _players.ContainsKey(player);

    /// <summary>
    /// A position packet's liveness-relevant fields for <paramref name="player"/>. Returns
    /// <see langword="true"/> exactly when the caller should forward the activity to the engine
    /// (tracked + deliberate activity + cooldown elapsed).
    /// </summary>
    public bool OnPositionSignal(PlayerKey player, sbyte rotation, bool weaponFired, DateTimeOffset at)
    {
        if (!_players.TryGetValue(player, out var state)) return false;

        // Rotation is activity only against an established anchor; fire is always activity.
        bool rotated = state.LastRotation is sbyte last && last != rotation;
        state.LastRotation = rotation;

        if (!weaponFired && !rotated) return false;
        return TryForward(state, at);
    }

    /// <summary>
    /// An inherently deliberate signal (chat). Same cooldown as position-derived activity.
    /// </summary>
    public bool OnDeliberateSignal(PlayerKey player, DateTimeOffset at)
    {
        if (!_players.TryGetValue(player, out var state)) return false;
        return TryForward(state, at);
    }

    private bool TryForward(State state, DateTimeOffset at)
    {
        if (state.LastForwardedAt is { } lastAt && at - lastAt < _forwardCooldown)
            return false;
        state.LastForwardedAt = at;
        return true;
    }
}
