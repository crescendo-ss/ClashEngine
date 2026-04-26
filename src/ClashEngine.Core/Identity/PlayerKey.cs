using System;

namespace ClashEngine.Core.Identity;

/// <summary>
/// Stable, case-insensitive handle for a player. Equality is based on <see cref="Name"/>
/// because the underlying SubspaceServer <c>Player.Id</c> is per-connection and resets across reconnects.
/// </summary>
public readonly struct PlayerKey : IEquatable<PlayerKey>
{
    public string Name { get; }

    public PlayerKey(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        Name = name;
    }

    public bool IsDefault => Name is null;

    public bool Equals(PlayerKey other) =>
        string.Equals(Name, other.Name, StringComparison.OrdinalIgnoreCase);

    public override bool Equals(object? obj) => obj is PlayerKey other && Equals(other);

    public override int GetHashCode() =>
        Name is null ? 0 : StringComparer.OrdinalIgnoreCase.GetHashCode(Name);

    public override string ToString() => Name ?? "(default)";

    public static bool operator ==(PlayerKey left, PlayerKey right) => left.Equals(right);
    public static bool operator !=(PlayerKey left, PlayerKey right) => !left.Equals(right);
}
