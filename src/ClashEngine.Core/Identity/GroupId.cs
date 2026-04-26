using System;

namespace ClashEngine.Core.Identity;

/// <summary>
/// Stable identifier for a player group. Groups are sets of players that prefer to play together;
/// the matcher will keep grouped players on the same team unless splitting is required for fairness.
/// </summary>
public readonly record struct GroupId(Guid Value)
{
    public static GroupId New() => new(Guid.NewGuid());
    public bool IsDefault => Value == Guid.Empty;
    public override string ToString() => Value.ToString("N");
}
