using System;
using System.Collections.Generic;
using ClashEngine.Core.Identity;

namespace ClashEngine.Core.Queue;

/// <summary>
/// A single named FIFO queue of players waiting for a match. One entry per <see cref="PlayerKey"/>;
/// adding a player who is already enqueued is a no-op (returns false).
/// </summary>
public sealed class PlayerQueue
{
    private readonly LinkedList<QueueEntry> _entries = new();
    private readonly Dictionary<PlayerKey, LinkedListNode<QueueEntry>> _index = new();

    public PlayerQueue(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        Name = name;
    }

    public string Name { get; }

    public int Count => _entries.Count;

    public bool Add(QueueEntry entry)
    {
        if (entry.Player.IsDefault)
            throw new ArgumentException("Entry must have a non-default PlayerKey.", nameof(entry));

        if (_index.ContainsKey(entry.Player))
            return false;

        var node = _entries.AddLast(entry);
        _index[entry.Player] = node;
        return true;
    }

    /// <summary>
    /// Inserts <paramref name="entry"/> at the head of the queue (priority insertion). Used by
    /// king-of-the-hill queues to re-enqueue match winners ahead of regular waiters.
    /// </summary>
    public bool AddPriority(QueueEntry entry)
    {
        if (entry.Player.IsDefault)
            throw new ArgumentException("Entry must have a non-default PlayerKey.", nameof(entry));

        if (_index.ContainsKey(entry.Player))
            return false;

        var node = _entries.AddFirst(entry);
        _index[entry.Player] = node;
        return true;
    }

    public bool Remove(PlayerKey player)
    {
        if (!_index.TryGetValue(player, out var node))
            return false;

        _entries.Remove(node);
        _index.Remove(player);
        return true;
    }

    public bool Contains(PlayerKey player) => _index.ContainsKey(player);

    public bool TryPeek(out QueueEntry entry)
    {
        if (_entries.First is null)
        {
            entry = default;
            return false;
        }

        entry = _entries.First.Value;
        return true;
    }

    public IReadOnlyList<QueueEntry> Snapshot()
    {
        var snapshot = new QueueEntry[_entries.Count];
        int i = 0;
        foreach (var entry in _entries)
            snapshot[i++] = entry;
        return snapshot;
    }

    public void Clear()
    {
        _entries.Clear();
        _index.Clear();
    }
}
