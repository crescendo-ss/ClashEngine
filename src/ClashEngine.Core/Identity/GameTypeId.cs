using System;

namespace ClashEngine.Core.Identity;

/// <summary>
/// Identifies a game type (e.g., 4v4-team, 1v1-duel) within the rating store. Backed by an
/// unsigned int, so non-negativity is a type-level guarantee -- no runtime range check.
/// </summary>
public readonly record struct GameTypeId(uint Value)
{
    /// <summary>
    /// Constructs a <see cref="GameTypeId"/> from an externally-supplied <see cref="int"/>
    /// (typically a conf value parsed via <c>IConfigManager.GetInt</c>, which is signed).
    /// Throws <see cref="ArgumentOutOfRangeException"/> if <paramref name="value"/> is negative.
    /// </summary>
    public static GameTypeId From(int value)
    {
        if (value < 0)
            throw new ArgumentOutOfRangeException(nameof(value), value, "GameTypeId must be non-negative.");
        return new GameTypeId((uint)value);
    }

    public override string ToString() => Value.ToString();
}
