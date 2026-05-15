using System;
using System.Collections.Generic;
using System.IO;
using ClashEngine.Core.Identity;
using ClashEngine.Core.Ratings;
using SS.Core;
using SS.Core.ComponentInterfaces;

namespace ClashEngine.Persistence;

/// <summary>
/// <see cref="IRatingStore"/> backed by an in-memory cache that round-trips through
/// SubspaceServer's <see cref="IPersist"/>. On player login, <c>SetData</c> populates the cache
/// for that player from a versioned binary blob. On logoff, <c>GetData</c> writes their current
/// ratings back to the database.
/// </summary>
/// <remarks>
/// Persist callbacks run on a worker thread. All cache mutations go through a single
/// <see cref="_gate"/> so the matchmaking engine (mainloop) and the persist worker do not race.
/// </remarks>
public sealed class PersistRatingStore : IRatingStore
{
    public const int PersistKey = 100;

    // Wire format version. v1 wrote gameType as a byte; v2 widens it to uint32 so the on-disk
    // format is no longer capped at 256 game types. v1 blobs are still readable for migration;
    // we always write v2 going forward.
    private const ushort BlobVersion = 2;

    private readonly object _gate = new();
    private readonly Dictionary<(PlayerKey, GameTypeId), Rating> _cache = new();

    public Rating Get(PlayerKey player, GameTypeId gameType)
    {
        lock (_gate)
            return _cache.TryGetValue((player, gameType), out var r) ? r : Rating.Default;
    }

    public bool TryGet(PlayerKey player, GameTypeId gameType, out Rating rating)
    {
        lock (_gate)
            return _cache.TryGetValue((player, gameType), out rating);
    }

    public void Set(PlayerKey player, GameTypeId gameType, Rating rating)
    {
        lock (_gate)
            _cache[(player, gameType)] = rating;
    }

    public bool Remove(PlayerKey player, GameTypeId gameType)
    {
        lock (_gate)
            return _cache.Remove((player, gameType));
    }

    public IReadOnlyList<RatingEntry> Snapshot()
    {
        lock (_gate)
        {
            var list = new List<RatingEntry>(_cache.Count);
            foreach (var kvp in _cache)
                list.Add(new RatingEntry(kvp.Key.Item1, kvp.Key.Item2, kvp.Value));
            return list;
        }
    }

    /// <summary>Persistence callback: write this player's ratings to <paramref name="outStream"/>.</summary>
    public void GetData(Player? player, Stream outStream)
    {
        if (player?.Name is not { Length: > 0 } name) return;
        var key = new PlayerKey(name);

        var rows = new List<(uint GameType, Rating R)>();
        lock (_gate)
        {
            foreach (var kvp in _cache)
                if (kvp.Key.Item1 == key)
                    rows.Add((kvp.Key.Item2.Value, kvp.Value));
        }
        if (rows.Count == 0) return;   // skip writing for never-played players

        using var writer = new BinaryWriter(outStream, System.Text.Encoding.UTF8, leaveOpen: true);
        writer.Write(BlobVersion);
        writer.Write((ushort)rows.Count);
        foreach (var row in rows)
        {
            writer.Write(row.GameType);          // v2: uint32 (v1 was byte)
            writer.Write(row.R.Mu);
            writer.Write(row.R.Sigma);
            writer.Write(row.R.GamesPlayed);
            writer.Write(row.R.LastSeen.UtcTicks);
        }
    }

    /// <summary>Persistence callback: read ratings for this player from <paramref name="inStream"/>.</summary>
    /// <remarks>
    /// Runs on the persist worker thread. Wrapped in a top-level catch so a corrupt or
    /// truncated blob can't escape into <c>IPersist</c>'s machinery and take down the persist
    /// pipeline. On any read failure the player loads with default ratings.
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
            if (version != 1 && version != BlobVersion) return;   // unknown version -> ignore

            ushort count = reader.ReadUInt16();
            lock (_gate)
            {
                for (int i = 0; i < count; i++)
                {
                    // v1 wrote gameType as a byte; v2 widens it to uint32.
                    uint gameType = version == 1 ? reader.ReadByte() : reader.ReadUInt32();
                    double mu = reader.ReadDouble();
                    double sigma = reader.ReadDouble();
                    uint games = reader.ReadUInt32();
                    long lastSeenTicks = reader.ReadInt64();
                    _cache[(key, new GameTypeId(gameType))] =
                        new Rating(mu, sigma, games, new DateTimeOffset(lastSeenTicks, TimeSpan.Zero));
                }
            }
        }
        catch (Exception)
        {
            // Corrupt or truncated blob. Swallow -- the player simply gets default ratings.
            // Logging is intentionally skipped here: this runs on the persist worker, and
            // we don't have an injected ILogManager. SS will still surface the malformed
            // blob via its own integrity checks if any are configured.
        }
    }

    /// <summary>Persistence callback: clear this player's data on database reset.</summary>
    public void ClearData(Player? player)
    {
        if (player?.Name is not { Length: > 0 } name) return;
        var key = new PlayerKey(name);
        lock (_gate)
        {
            var toRemove = new List<(PlayerKey, GameTypeId)>();
            foreach (var k in _cache.Keys)
                if (k.Item1 == key) toRemove.Add(k);
            foreach (var k in toRemove) _cache.Remove(k);
        }
    }
}
