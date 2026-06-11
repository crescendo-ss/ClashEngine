using System;
using System.Collections.Concurrent;
using System.IO;
using ClashEngine.Core.Identity;
using SS.Core;

namespace ClashEngine.Persistence;

/// <summary>
/// IPersist round-trip for the per-player "last ship" memory: the ship a player was last on
/// before going to spectator mode (or leaving an arena / disconnecting while still in a ship).
/// Doubles as the live cache <see cref="Adapter.LastShipTracker"/> writes and
/// <see cref="Orchestration.MatchOrchestrator"/> reads on the mainloop thread; the persist
/// callbacks here run on a worker thread, so the cache is a <see cref="ConcurrentDictionary{TKey,TValue}"/>.
/// </summary>
/// <remarks>
/// Players with no memory yet are simply absent -- an absent or empty blob loads as "no memory",
/// and match placement falls back to Warbird. The blob is a version stamp plus the ship as a
/// single byte.
/// </remarks>
public sealed class PersistLastShipStore
{
    public const int PersistKey = 103;
    private const ushort BlobVersion = 1;

    private readonly ConcurrentDictionary<PlayerKey, ShipType> _lastShip = new();

    /// <summary>The remembered ship for <paramref name="player"/>, or <see langword="null"/> if
    /// they have never been observed leaving a ship.</summary>
    public ShipType? Get(PlayerKey player) =>
        _lastShip.TryGetValue(player, out var ship) ? ship : null;

    /// <summary>Remember <paramref name="ship"/> as the last ship <paramref name="player"/> was
    /// on. Non-playable values (<see cref="ShipType.Spec"/> or out of range) are ignored rather
    /// than clearing the memory -- a bogus event shouldn't erase a good one.</summary>
    public void Set(PlayerKey player, ShipType ship)
    {
        if (ship is < ShipType.Warbird or > ShipType.Shark) return;
        _lastShip[player] = ship;
    }

    /// <summary>Persistence callback: write this player's last ship (only when one is remembered).</summary>
    public void GetData(Player? player, Stream outStream)
    {
        if (player?.Name is not { Length: > 0 } name) return;
        if (Get(new PlayerKey(name)) is not { } ship) return;   // no memory => write nothing

        using var writer = new BinaryWriter(outStream, System.Text.Encoding.UTF8, leaveOpen: true);
        writer.Write(BlobVersion);
        writer.Write((byte)ship);
    }

    /// <summary>Persistence callback: read this player's last ship.</summary>
    /// <remarks>
    /// Runs on the persist worker thread. Wrapped in a top-level catch so a corrupt, truncated, or
    /// absent blob can't escape into <c>IPersist</c>'s machinery; on any read failure the player
    /// loads with no memory (and any stale cache entry from a prior connect is dropped).
    /// </remarks>
    public void SetData(Player? player, Stream inStream)
    {
        if (player?.Name is not { Length: > 0 } name) return;
        var key = new PlayerKey(name);

        ShipType? ship = null;
        try
        {
            using var reader = new BinaryReader(inStream, System.Text.Encoding.UTF8, leaveOpen: true);
            ushort version = reader.ReadUInt16();
            if (version == BlobVersion)
                ship = (ShipType)reader.ReadByte();
        }
        catch (Exception)
        {
            ship = null;
        }

        if (ship is { } s && s is >= ShipType.Warbird and <= ShipType.Shark)
            _lastShip[key] = s;
        else
            _lastShip.TryRemove(key, out _);
    }

    /// <summary>Persistence callback: nothing to clear at database-reset granularity. The
    /// <c>IDelegatePersistentData</c> contract requires a body; ours is intentionally empty.</summary>
    public void ClearData(Player? player) { }
}
