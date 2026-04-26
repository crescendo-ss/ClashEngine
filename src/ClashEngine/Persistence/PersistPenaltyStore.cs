using System;
using System.Collections.Generic;
using System.IO;
using ClashEngine.Core.Identity;
using ClashEngine.Core.Penalties;
using SS.Core;

namespace ClashEngine.Persistence;

/// <summary>
/// IPersist round-trip for <see cref="PenaltyTracker"/>. Stores per-player penalty event lists
/// in a versioned binary blob. The matchmaking engine reads/writes the tracker on the mainloop
/// thread and the persist callbacks here run on a worker thread, but every <c>PenaltyTracker</c>
/// method already locks internally, so the store doesn't add a second redundant lock.
/// </summary>
public sealed class PersistPenaltyStore
{
    public const int PersistKey = 101;
    private const ushort BlobVersion = 1;

    private readonly PenaltyTracker _tracker;

    public PersistPenaltyStore(PenaltyTracker tracker)
    {
        _tracker = tracker ?? throw new ArgumentNullException(nameof(tracker));
    }

    /// <summary>Persistence callback: write this player's penalty events.</summary>
    public void GetData(Player? player, Stream outStream)
    {
        if (player?.Name is not { Length: > 0 } name) return;
        var key = new PlayerKey(name);

        var rows = new List<PenaltyRecord>();
        foreach (var record in _tracker.Snapshot())
            if (record.Player == key) rows.Add(record);
        if (rows.Count == 0) return;

        using var writer = new BinaryWriter(outStream, System.Text.Encoding.UTF8, leaveOpen: true);
        writer.Write(BlobVersion);
        writer.Write((ushort)rows.Count);
        foreach (var r in rows)
        {
            writer.Write((byte)r.Kind);
            writer.Write(r.At.UtcTicks);
            writer.Write(r.Severity);
        }
    }

    /// <summary>Persistence callback: read this player's penalty events.</summary>
    /// <remarks>
    /// Runs on the persist worker thread. Wrapped in a top-level catch so a corrupt or
    /// truncated blob can't escape into <c>IPersist</c>'s machinery and take down the persist
    /// pipeline. On any read failure the player loads with no penalties recorded.
    /// </remarks>
    public void SetData(Player? player, Stream inStream)
    {
        if (player?.Name is not { Length: > 0 } name) return;
        var key = new PlayerKey(name);

        try
        {
            using var reader = new BinaryReader(inStream, System.Text.Encoding.UTF8, leaveOpen: true);
            ushort version;
            try { version = reader.ReadUInt16(); }
            catch (EndOfStreamException) { return; }
            if (version != BlobVersion) return;

            ushort count = reader.ReadUInt16();
            for (int i = 0; i < count; i++)
            {
                var kind = (PenaltyKind)reader.ReadByte();
                long ticks = reader.ReadInt64();
                double severity = reader.ReadDouble();
                if (severity < 1.0) severity = 1.0;
                if (!_tracker.HasPolicy(kind)) continue;
                _tracker.RecordPenalty(key, kind, new DateTimeOffset(ticks, TimeSpan.Zero), severity);
            }
        }
        catch (Exception)
        {
            // Corrupt or truncated blob. Swallow -- this player simply loads with no
            // recorded penalties.
        }
    }

    /// <summary>Persistence callback: nothing to clear at database-reset granularity. The
    /// <c>IDelegatePersistentData</c> contract requires a body; ours is intentionally empty.</summary>
    public void ClearData(Player? player) { }
}
