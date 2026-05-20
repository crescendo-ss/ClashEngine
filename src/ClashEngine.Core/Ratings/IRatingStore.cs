using System.Collections.Generic;
using ClashEngine.Core.Identity;

namespace ClashEngine.Core.Ratings;

/// <summary>One row in an <see cref="IRatingStore"/> snapshot.</summary>
/// <remarks>
/// <paramref name="GameType"/> is the registry name of the game type (the same string used
/// in <c>match.gameType</c> on the upload wire). v3 of the persisted blob keys ratings by
/// this string; the legacy v1/v2 uint id is no longer used anywhere in the engine.
/// </remarks>
public readonly record struct RatingEntry(PlayerKey Player, string GameType, Rating Value);

/// <summary>
/// Stores per-player, per-game-type ratings. Implementations may be in-memory or
/// backed by persistent storage. Thread-safety is implementation-defined; the in-memory
/// implementation is safe for concurrent reads and writes.
/// </summary>
public interface IRatingStore
{
    /// <summary>Returns the stored rating, or <see cref="Rating.Default"/> if none exists.</summary>
    Rating Get(PlayerKey player, string gameType);

    /// <summary>Returns <see langword="true"/> with the stored rating; <see langword="false"/> if none exists.</summary>
    bool TryGet(PlayerKey player, string gameType, out Rating rating);

    /// <summary>Inserts or overwrites the rating.</summary>
    void Set(PlayerKey player, string gameType, Rating rating);

    /// <summary>Removes the rating. Returns <see langword="true"/> if a row was removed.</summary>
    bool Remove(PlayerKey player, string gameType);

    /// <summary>
    /// Point-in-time snapshot of all rows. The returned list does not reflect later changes.
    /// </summary>
    IReadOnlyList<RatingEntry> Snapshot();
}
