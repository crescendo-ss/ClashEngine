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

    /// <summary>
    /// Typo-tolerant lookup for command arguments that take a player name. Builds the
    /// candidate roster from connected players in <paramref name="scope"/> (pass null for
    /// zone-wide) and delegates to <see cref="PlayerNameMatcher.Resolve"/>, which layers
    /// exact / unique prefix / unique Damerau-Levenshtein and rejects ties.
    ///
    /// Arena-scoping the candidate set is what makes the ambiguity-rejection rule useful --
    /// a zone-wide pool would have too many collisions on short prefixes, so almost every
    /// typo would be reported as ambiguous. Commands that legitimately want zone-wide
    /// resolution (e.g. operator tools targeting offline players) should keep using
    /// <see cref="Resolve(PlayerKey)"/> or build their own candidate set.
    ///
    /// <see cref="FuzzyResolve.Player"/> is non-null exactly when the match is
    /// Exact/Prefix/Fuzzy; for None/Ambiguous it's null and the caller should surface
    /// <see cref="NameMatch.Candidates"/> in the error message.
    /// </summary>
    public FuzzyResolve ResolveFuzzy(string typed, Arena? scope)
    {
        var names = new List<string>(_byName.Count);
        foreach (var p in _byName.Values)
        {
            if (string.IsNullOrEmpty(p.Name)) continue;
            if (scope is not null && !ReferenceEquals(p.Arena, scope)) continue;
            names.Add(p.Name);
        }

        var match = PlayerNameMatcher.Resolve(typed, names);
        Player? player = match.Name is { } n && _byName.TryGetValue(n, out var hit) ? hit : null;
        return new FuzzyResolve(match, player);
    }

    public void Clear() => _byName.Clear();

    public int Count => _byName.Count;
}

/// <summary>Result of <see cref="PlayerKeyResolver.ResolveFuzzy"/>. <see cref="Player"/> is
/// non-null iff <see cref="Match"/>.Kind is Exact, Prefix, or Fuzzy.</summary>
public readonly record struct FuzzyResolve(NameMatch Match, Player? Player);
