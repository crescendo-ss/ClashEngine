using System;
using System.Collections.Generic;
using ClashEngine.Core.Identity;
using SS.Core;

namespace ClashEngine.Adapter;

/// <summary>
/// Translates between SubspaceServer's <see cref="Player"/> objects and the pure-layer
/// <see cref="PlayerKey"/>. The cache is keyed by player name (case-insensitive) to survive
/// reconnects, since <c>Player.Id</c> is per-connection.
/// </summary>
public sealed class PlayerKeyResolver
{
    private readonly Dictionary<string, Player> _byName = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Updates the cache when a player connects.</summary>
    public void OnConnect(Player player)
    {
        ArgumentNullException.ThrowIfNull(player);
        if (string.IsNullOrEmpty(player.Name)) return;
        _byName[player.Name] = player;
    }

    /// <summary>Removes a disconnecting player from the cache.</summary>
    public void OnDisconnect(Player player)
    {
        if (player.Name is { Length: > 0 } name && _byName.TryGetValue(name, out var existing) && existing == player)
            _byName.Remove(name);
    }

    /// <summary>
    /// Returns the <see cref="PlayerKey"/> for a connected player, or <see langword="null"/> if
    /// the player has no name yet.
    /// </summary>
    public PlayerKey? KeyOf(Player player)
    {
        if (player is null || string.IsNullOrEmpty(player.Name)) return null;
        return new PlayerKey(player.Name);
    }

    /// <summary>Looks up the SubspaceServer <see cref="Player"/> for a given key, or null if not connected.</summary>
    public Player? Resolve(PlayerKey key) =>
        !key.IsDefault && _byName.TryGetValue(key.Name, out var p) ? p : null;

    public void Clear() => _byName.Clear();

    public int Count => _byName.Count;
}
