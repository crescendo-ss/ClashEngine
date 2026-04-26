using System;

namespace ClashEngine.Core.Identity;

/// <summary>
/// Identifies a game type (e.g., 4v4-team, 1v1-duel) within the rating store.
/// One byte is enough -- we don't expect more than ~16 game types ever.
/// </summary>
public readonly record struct GameTypeId(byte Value)
{
    public static GameTypeId From(int value)
    {
        if (value < 0 || value > byte.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(value), value, "GameTypeId must fit in a byte.");
        return new GameTypeId((byte)value);
    }

    public override string ToString() => Value.ToString();
}
