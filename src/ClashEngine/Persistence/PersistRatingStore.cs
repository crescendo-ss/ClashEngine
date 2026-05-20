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
/// <para>Persist callbacks run on a worker thread. All cache mutations go through a single
/// <see cref="_gate"/> so the matchmaking engine (mainloop) and the persist worker do not race.</para>
///
/// <para>Blob versions:</para>
/// <list type="bullet">
///   <item><b>v1</b> wrote gameType as a single byte.</item>
///   <item><b>v2</b> widened gameType to <c>uint32</c>.</item>
///   <item><b>v3</b> (current) re-keys ratings by the gametype <b>name</b> string -- the same
///         registry name used in <c>match.gameType</c> on the upload wire and <c>name</c> on
///         the gametype-registration POST. There is no longer a numeric id at the engine layer.</item>
/// </list>
///
/// <para>v1 / v2 blobs are <i>not</i> migrated forward: once <c>GameTypeId</c> went away there
/// is no in-engine way to map the numeric id back to a name (the conf file that knew the
/// mapping at write time may have been edited since). On encountering a v1/v2 blob the
/// callback skips it and the player loads with default ratings -- the same behavior as a
/// corrupt blob -- which lets them rebuild on the new key going forward without spurious
/// failures. A separate one-time migration utility can read v1/v2 blobs alongside an
/// arena's conf file if backfill ever becomes necessary.</para>
/// </remarks>
public sealed class PersistRatingStore : IRatingStore
{
    public const int PersistKey = 100;

    // Wire format version. v3 stores gameType as a length-prefixed UTF-8 string.
    private const ushort BlobVersion = 3;

    private readonly object _gate = new();
    private readonly Dictionary<(PlayerKey, string), Rating> _cache =
        new(GameTypeKeyComparer.Instance);

    public Rating Get(PlayerKey player, string gameType)
    {
        lock (_gate)
            return _cache.TryGetValue((player, gameType), out var r) ? r : Rating.Default;
    }

    public bool TryGet(PlayerKey player, string gameType, out Rating rating)
    {
        lock (_gate)
            return _cache.TryGetValue((player, gameType), out rating);
    }

    public void Set(PlayerKey player, string gameType, Rating rating)
    {
        lock (_gate)
            _cache[(player, gameType)] = rating;
    }

    public bool Remove(PlayerKey player, string gameType)
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

        var rows = new List<(string GameType, Rating R)>();
        lock (_gate)
        {
            foreach (var kvp in _cache)
                if (kvp.Key.Item1 == key)
                    rows.Add((kvp.Key.Item2, kvp.Value));
        }
        if (rows.Count == 0) return;   // skip writing for never-played players

        using var writer = new BinaryWriter(outStream, System.Text.Encoding.UTF8, leaveOpen: true);
        writer.Write(BlobVersion);
        writer.Write((ushort)rows.Count);
        foreach (var row in rows)
        {
            writer.Write(row.GameType);   // BinaryWriter emits a length-prefixed UTF-8 string
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
    /// pipeline. v1/v2 blobs are skipped on purpose -- see the type-level remarks.
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
            if (version != BlobVersion) return;   // legacy v1/v2 unmappable -> ignore

            ushort count = reader.ReadUInt16();
            lock (_gate)
            {
                for (int i = 0; i < count; i++)
                {
                    string gameType = reader.ReadString();
                    double mu = reader.ReadDouble();
                    double sigma = reader.ReadDouble();
                    uint games = reader.ReadUInt32();
                    long lastSeenTicks = reader.ReadInt64();
                    _cache[(key, gameType)] =
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
            var toRemove = new List<(PlayerKey, string)>();
            foreach (var k in _cache.Keys)
                if (k.Item1 == key) toRemove.Add(k);
            foreach (var k in toRemove) _cache.Remove(k);
        }
    }

    /// <summary>
    /// Equality comparer for <c>(PlayerKey, string)</c> keys that treats the game-type
    /// segment case-insensitively. Mirrors <see cref="InMemoryRatingStore"/>'s comparer so
    /// the persist cache addresses the same row identity the in-process store does.
    /// </summary>
    private sealed class GameTypeKeyComparer : IEqualityComparer<(PlayerKey, string)>
    {
        public static readonly GameTypeKeyComparer Instance = new();

        public bool Equals((PlayerKey, string) x, (PlayerKey, string) y) =>
            x.Item1.Equals(y.Item1)
            && string.Equals(x.Item2, y.Item2, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((PlayerKey, string) obj) =>
            HashCode.Combine(
                obj.Item1.GetHashCode(),
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Item2 ?? string.Empty));
    }
}
