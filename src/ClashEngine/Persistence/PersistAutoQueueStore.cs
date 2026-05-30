using System;
using System.Collections.Concurrent;
using System.IO;
using ClashEngine.Core.Identity;
using ClashEngine.Core.Preferences;
using SS.Core;

namespace ClashEngine.Persistence;

/// <summary>
/// IPersist round-trip for the per-player <c>?autoqueue</c> preference. Doubles as the live
/// <see cref="IAutoQueueStore"/> the engine reads/writes on the mainloop thread; the persist
/// callbacks here run on a worker thread, so the cache is a <see cref="ConcurrentDictionary{TKey,TValue}"/>.
/// </summary>
/// <remarks>
/// Only enabled players are written -- an absent or empty blob loads as off, which matches the
/// default -- so the off-by-default majority costs no storage. The blob is a version stamp plus a
/// single bool; the bool is carried (rather than relying on presence alone) to leave room for
/// future per-player preference fields without a key bump.
/// </remarks>
public sealed class PersistAutoQueueStore : IAutoQueueStore
{
    public const int PersistKey = 102;
    private const ushort BlobVersion = 1;

    private readonly ConcurrentDictionary<PlayerKey, bool> _enabled = new();

    public bool IsEnabled(PlayerKey player) => _enabled.TryGetValue(player, out var on) && on;

    public void Set(PlayerKey player, bool enabled)
    {
        if (enabled) _enabled[player] = true;
        else _enabled.TryRemove(player, out _);
    }

    /// <summary>Persistence callback: write this player's auto-queue flag (only when enabled).</summary>
    public void GetData(Player? player, Stream outStream)
    {
        if (player?.Name is not { Length: > 0 } name) return;
        if (!IsEnabled(new PlayerKey(name))) return;   // off => write nothing => loads as off

        using var writer = new BinaryWriter(outStream, System.Text.Encoding.UTF8, leaveOpen: true);
        writer.Write(BlobVersion);
        writer.Write(true);
    }

    /// <summary>Persistence callback: read this player's auto-queue flag.</summary>
    /// <remarks>
    /// Runs on the persist worker thread. Wrapped in a top-level catch so a corrupt, truncated, or
    /// absent blob can't escape into <c>IPersist</c>'s machinery; on any read failure the player
    /// loads with auto-queue off (the default).
    /// </remarks>
    public void SetData(Player? player, Stream inStream)
    {
        if (player?.Name is not { Length: > 0 } name) return;
        var key = new PlayerKey(name);

        bool enabled = false;
        try
        {
            using var reader = new BinaryReader(inStream, System.Text.Encoding.UTF8, leaveOpen: true);
            ushort version = reader.ReadUInt16();
            if (version == BlobVersion)
                enabled = reader.ReadBoolean();
        }
        catch (Exception)
        {
            enabled = false;
        }

        Set(key, enabled);
    }

    /// <summary>Persistence callback: nothing to clear at database-reset granularity. The
    /// <c>IDelegatePersistentData</c> contract requires a body; ours is intentionally empty.</summary>
    public void ClearData(Player? player) { }
}
