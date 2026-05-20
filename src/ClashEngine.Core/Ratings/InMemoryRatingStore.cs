using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using ClashEngine.Core.Identity;

namespace ClashEngine.Core.Ratings;

/// <summary>
/// Thread-safe, in-memory <see cref="IRatingStore"/>. Used both as the test double and
/// as the live cache that the persistence layer wraps in iteration #2.
/// </summary>
/// <remarks>
/// Game-type keys are stored case-insensitively to match how <see cref="GameTypeRegistry"/>
/// indexes them -- otherwise an arena that declares <c>Elim_3v3</c> in conf and the upload
/// wire that carries <c>elim_3v3</c> on the match envelope would be addressed as two
/// independent rating rows.
/// </remarks>
public sealed class InMemoryRatingStore : IRatingStore
{
    private readonly ConcurrentDictionary<(PlayerKey Player, string GameType), Rating> _data =
        new(GameTypeKeyComparer.Instance);

    public Rating Get(PlayerKey player, string gameType) =>
        _data.TryGetValue((player, gameType), out var rating) ? rating : Rating.Default;

    public bool TryGet(PlayerKey player, string gameType, out Rating rating) =>
        _data.TryGetValue((player, gameType), out rating);

    public void Set(PlayerKey player, string gameType, Rating rating) =>
        _data[(player, gameType)] = rating;

    public bool Remove(PlayerKey player, string gameType) =>
        _data.TryRemove((player, gameType), out _);

    public IReadOnlyList<RatingEntry> Snapshot()
    {
        var list = new List<RatingEntry>(_data.Count);
        foreach (var kvp in _data)
            list.Add(new RatingEntry(kvp.Key.Player, kvp.Key.GameType, kvp.Value));
        return list;
    }

    /// <summary>
    /// Equality comparer for <c>(PlayerKey, string)</c> tuple keys that hashes the game-type
    /// segment case-insensitively. <see cref="PlayerKey"/> is already case-insensitive on its
    /// own; this wraps both halves into a single comparer the <see cref="ConcurrentDictionary"/>
    /// can consume.
    /// </summary>
    private sealed class GameTypeKeyComparer : IEqualityComparer<(PlayerKey Player, string GameType)>
    {
        public static readonly GameTypeKeyComparer Instance = new();

        public bool Equals((PlayerKey Player, string GameType) x, (PlayerKey Player, string GameType) y) =>
            x.Player.Equals(y.Player)
            && string.Equals(x.GameType, y.GameType, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((PlayerKey Player, string GameType) obj) =>
            HashCode.Combine(
                obj.Player.GetHashCode(),
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.GameType ?? string.Empty));
    }
}
