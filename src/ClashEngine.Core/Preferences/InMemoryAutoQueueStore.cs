using System.Collections.Concurrent;
using ClashEngine.Core.Identity;

namespace ClashEngine.Core.Preferences;

/// <summary>
/// Thread-safe, in-memory <see cref="IAutoQueueStore"/>. Used both as the test double and as the
/// engine's default when no persistent store is supplied. Presence in the map (value
/// <see langword="true"/>) means auto-queue is on; the off state is simply the absence of an entry,
/// so the off-by-default majority costs nothing.
/// </summary>
public sealed class InMemoryAutoQueueStore : IAutoQueueStore
{
    private readonly ConcurrentDictionary<PlayerKey, bool> _enabled = new();

    public bool IsEnabled(PlayerKey player) => _enabled.TryGetValue(player, out var on) && on;

    public void Set(PlayerKey player, bool enabled)
    {
        if (enabled) _enabled[player] = true;
        else _enabled.TryRemove(player, out _);
    }
}
