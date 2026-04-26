using System;
using System.Collections.Generic;
using ClashEngine.Core.Identity;

namespace ClashEngine.Core.Queue;

/// <summary>
/// Reverse map of <see cref="PlayerKey"/> -> set of queue names the player is currently enqueued in.
/// Lets the matcher atomically remove a player from every queue when they accept a match
/// in any one of them. Pure data structure -- does not own the queues themselves.
/// </summary>
public sealed class MultiQueueIndex
{
    private readonly Dictionary<PlayerKey, HashSet<string>> _byPlayer = new();
    private static readonly IReadOnlySet<string> EmptySet = new HashSet<string>();

    /// <summary>
    /// Marks <paramref name="player"/> as enqueued in <paramref name="queueName"/>.
    /// Returns <see langword="true"/> if newly added, <see langword="false"/> if already present.
    /// </summary>
    public bool Add(PlayerKey player, string queueName)
    {
        ArgumentException.ThrowIfNullOrEmpty(queueName);
        if (player.IsDefault) throw new ArgumentException("Player must not be default.", nameof(player));

        if (!_byPlayer.TryGetValue(player, out var set))
        {
            set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            _byPlayer[player] = set;
        }
        return set.Add(queueName);
    }

    /// <summary>
    /// Removes the (player, queueName) tracking. Returns <see langword="true"/> if it was tracked.
    /// </summary>
    public bool Remove(PlayerKey player, string queueName)
    {
        if (!_byPlayer.TryGetValue(player, out var set))
            return false;

        bool removed = set.Remove(queueName);
        if (set.Count == 0)
            _byPlayer.Remove(player);
        return removed;
    }

    /// <summary>
    /// Untracks <paramref name="player"/> from every queue they are in and returns those queue names.
    /// Returns an empty list if the player wasn't tracked.
    /// </summary>
    public IReadOnlyList<string> RemoveAll(PlayerKey player)
    {
        if (!_byPlayer.Remove(player, out var set) || set.Count == 0)
            return Array.Empty<string>();

        return new List<string>(set);
    }

    public IReadOnlySet<string> QueuesFor(PlayerKey player) =>
        _byPlayer.TryGetValue(player, out var set) ? set : EmptySet;

    public bool Contains(PlayerKey player, string queueName) =>
        _byPlayer.TryGetValue(player, out var set) && set.Contains(queueName);

    public bool IsTrackingAnywhere(PlayerKey player) =>
        _byPlayer.TryGetValue(player, out var set) && set.Count > 0;

    public int PlayerCount => _byPlayer.Count;
}
