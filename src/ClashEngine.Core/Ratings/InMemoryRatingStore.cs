using System.Collections.Concurrent;
using System.Collections.Generic;
using ClashEngine.Core.Identity;

namespace ClashEngine.Core.Ratings;

/// <summary>
/// Thread-safe, in-memory <see cref="IRatingStore"/>. Used both as the test double and
/// as the live cache that the persistence layer wraps in iteration #2.
/// </summary>
public sealed class InMemoryRatingStore : IRatingStore
{
    private readonly ConcurrentDictionary<(PlayerKey Player, GameTypeId GameType), Rating> _data = new();

    public Rating Get(PlayerKey player, GameTypeId gameType) =>
        _data.TryGetValue((player, gameType), out var rating) ? rating : Rating.Default;

    public bool TryGet(PlayerKey player, GameTypeId gameType, out Rating rating) =>
        _data.TryGetValue((player, gameType), out rating);

    public void Set(PlayerKey player, GameTypeId gameType, Rating rating) =>
        _data[(player, gameType)] = rating;

    public bool Remove(PlayerKey player, GameTypeId gameType) =>
        _data.TryRemove((player, gameType), out _);

    public IReadOnlyList<RatingEntry> Snapshot()
    {
        var list = new List<RatingEntry>(_data.Count);
        foreach (var kvp in _data)
            list.Add(new RatingEntry(kvp.Key.Player, kvp.Key.GameType, kvp.Value));
        return list;
    }
}
