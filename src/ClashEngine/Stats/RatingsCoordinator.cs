using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ClashEngine.Core.GameType;
using ClashEngine.Core.Identity;
using ClashEngine.Core.Ratings;
using SS.Core.ComponentInterfaces;

namespace ClashEngine.Stats;

/// <summary>
/// Seeds and synchronizes a player's local <see cref="IRatingStore"/> with the stats
/// server. Refreshes on connect (one pull per registered gametype); the <c>?play</c> command
/// uses <see cref="EnsurePulledAsync"/> to close the race when a player queues before the
/// connect-pull has resolved.
/// </summary>
/// <remarks>
/// <para><b>Connect</b>: every registered gametype gets a per-(player, gametype) pull task
/// started on a thread-pool thread. The task is captured in <see cref="_inFlight"/> while it
/// runs. On result:</para>
/// <list type="bullet">
///   <item>Server returns a rating -> write into the store using newer-<see cref="Rating.LastSeen"/>-
///         wins. The guard prevents a stale server row from clobbering a freshly-finalized
///         local rating whose match envelope hasn't reached the server yet.</item>
///   <item>Server returns null -> write <c>Rating.Default with { IsNew = true }</c>. The
///         marker disambiguates "asked, server was empty" from "we never asked" for any
///         downstream code that wants to render a "new player" affordance.</item>
/// </list>
///
/// <para><b><c>?play</c></b>: the handler calls <see cref="EnsurePulledAsync"/> with the
/// resolved gametype before dispatching the enqueue back onto the mainloop. Three cases:</para>
/// <list type="bullet">
///   <item>A pull task is in <see cref="_inFlight"/> for this pair -> return that task; the
///         caller awaits the existing pull rather than starting a second one.</item>
///   <item>No pull in flight, but the local store has a row for this pair -> return
///         <see cref="Task.CompletedTask"/>. Match envelopes keep the row in sync; no need
///         to re-pull on every queue command.</item>
///   <item>No pull in flight and no local row -> start a fresh pull and return its task.
///         This covers the rare case where the connect-pull never ran (no connect event
///         delivered for this player on this process -- e.g. a hot-reload mid-session).</item>
/// </list>
///
/// <para><b>Threading</b>: connect handlers run on the mainloop; pull tasks run on the
/// thread pool; store writes go through the existing <see cref="InMemoryRatingStore"/> /
/// <see cref="PersistRatingStore"/> locking. <see cref="_inFlight"/> is a
/// <see cref="ConcurrentDictionary{TKey, TValue}"/> so the mainloop and the pull continuations
/// can both touch it without explicit locks.</para>
/// </remarks>
public sealed class RatingsCoordinator : IDisposable
{
    private const string LogCategory = nameof(RatingsCoordinator);

    private readonly IRatingsProvider _provider;
    private readonly IRatingStore _ratings;
    private readonly GameTypeRegistry _gameTypes;
    private readonly ILogManager _log;
    private readonly CancellationTokenSource _cts = new();

    // Pull tasks currently in flight. Keyed by (player, gametype) using a case-insensitive
    // equality comparer so a connect-pull and a same-session ?play lookup address the same
    // task even if conf casing drifts.
    private readonly ConcurrentDictionary<(PlayerKey, string), Task<Rating?>> _inFlight =
        new(InFlightKeyComparer.Instance);

    public RatingsCoordinator(
        IRatingsProvider provider,
        IRatingStore ratings,
        GameTypeRegistry gameTypes,
        ILogManager log)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _ratings = ratings ?? throw new ArgumentNullException(nameof(ratings));
        _gameTypes = gameTypes ?? throw new ArgumentNullException(nameof(gameTypes));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    /// <summary>
    /// PlayerStateObserver.PlayerConnected handler. Fires off one pull per currently-registered
    /// gametype; each task is captured in <see cref="_inFlight"/> so <see cref="EnsurePulledAsync"/>
    /// can await it from a concurrent <c>?play</c> command. The handler returns immediately;
    /// HTTP work happens on the thread pool. <paramref name="at"/> is unused -- merge ordering
    /// uses the server's <c>updatedAt</c> against the local <see cref="Rating.LastSeen"/>,
    /// never the connect-time clock.
    /// </summary>
    public void OnPlayerConnected(PlayerKey key, DateTimeOffset at)
    {
        // Snapshot the gametype names at connect time. The registry can be hot-reloaded; a
        // gametype that disappears mid-pull keeps its task running (harmless -- the result
        // either writes a row that never gets read, or marks new and gets ignored).
        var names = new List<string>();
        foreach (var def in _gameTypes.Definitions) names.Add(def.Name);
        if (names.Count == 0) return;

        for (int i = 0; i < names.Count; i++)
            StartPull(key, names[i]);
    }

    /// <summary>
    /// Returns a task that completes once the local store has the freshest pull-derived state
    /// for <c>(<paramref name="key"/>, <paramref name="gameType"/>)</c> the engine can offer.
    /// See the type-level remarks for the three resolution cases.
    /// </summary>
    /// <remarks>
    /// The returned task never throws -- pull failures are absorbed inside the pull task and
    /// surface as <see langword="null"/> remote values (which the coordinator handles by
    /// either marking new or leaving the cache as-is). Callers can <c>await</c> without
    /// try/catch.
    /// </remarks>
    public Task EnsurePulledAsync(PlayerKey key, string gameType)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameType);

        var pairKey = (key, gameType);
        if (_inFlight.TryGetValue(pairKey, out var pending))
            return pending;

        if (_ratings.TryGet(key, gameType, out _))
            return Task.CompletedTask;

        // Connect-pull never ran (or ran before this gametype was registered). Fire a fresh
        // pull and return its task; the caller awaits this exactly like a connect-time pull.
        return StartPull(key, gameType);
    }

    /// <summary>
    /// Kicks off the pull-and-merge work for one <c>(player, gameType)</c> pair and registers
    /// the task in <see cref="_inFlight"/>. Returns the task so callers that need to await it
    /// (the fresh-pull branch of <see cref="EnsurePulledAsync"/>) can. The task removes
    /// itself from <see cref="_inFlight"/> when it finishes regardless of outcome.
    /// </summary>
    private Task<Rating?> StartPull(PlayerKey key, string gameType)
    {
        var pairKey = (key, gameType);
        var token = _cts.Token;

        // GetOrAdd guards against the rare double-call (connect fires + ?play arrives before
        // OnPlayerConnected has finished spinning up all tasks). The factory only runs when
        // the key wasn't already there.
        return _inFlight.GetOrAdd(pairKey, _ => Task.Run(async () =>
        {
            try
            {
                Rating? remote = await _provider.TryPullAsync(key.Name, gameType, token).ConfigureAwait(false);
                ApplyPullResult(key, gameType, remote);
                return remote;
            }
            catch (OperationCanceledException) { return null; }
            catch (Exception ex)
            {
                // Defensive: IRatingsProvider impls aren't supposed to throw, but if one does
                // we surface it as null (the "transport failure" semantic) rather than letting
                // it tear down the connect handler's fire-and-forget Task.
                _log.LogM(LogLevel.Warn, LogCategory,
                    $"Pull threw for '{key.Name}' gameType='{gameType}': {ex.Message}");
                return null;
            }
            finally
            {
                _inFlight.TryRemove(pairKey, out Task<Rating?>? _);
            }
        }, token));
    }

    /// <summary>
    /// Merges a pull result into the local store. Server-returned ratings are written under
    /// the newer-<see cref="Rating.LastSeen"/>-wins rule; nulls install the "new player"
    /// marker unless we already have a non-marker row (a real local rating shouldn't be
    /// overwritten by a marker).
    /// </summary>
    private void ApplyPullResult(PlayerKey key, string gameType, Rating? remote)
    {
        if (remote is { } r)
        {
            // Don't clobber a freshly-finalized local rating whose envelope is still queued.
            if (!_ratings.TryGet(key, gameType, out var local) || local.LastSeen < r.LastSeen)
            {
                _ratings.Set(key, gameType, r);
            }
            return;
        }

        // Server has no row. Only install the marker if we have no real local row -- if a
        // local row exists with non-default values, the player isn't actually new (probably a
        // race with a match update that beat the pull response back), and overwriting with
        // the marker would lose that data.
        if (_ratings.TryGet(key, gameType, out var existing) && !IsMarkerOrDefault(existing))
            return;

        _ratings.Set(key, gameType, Rating.Default with { IsNew = true });
    }

    /// <summary>
    /// True when <paramref name="r"/> looks like either an existing new-player marker or a
    /// no-data default row. We treat both the same way for marker placement: it's safe to
    /// overwrite either with a fresh marker (or to leave them alone). A row with real game
    /// history (<see cref="Rating.GamesPlayed"/> &gt; 0) or non-default mu/sigma never gets
    /// overwritten by a marker.
    /// </summary>
    private static bool IsMarkerOrDefault(Rating r) =>
        r.IsNew
        || (r.GamesPlayed == 0
            && r.Mu == Rating.Default.Mu
            && r.Sigma == Rating.Default.Sigma);

    public void Dispose()
    {
        try { _cts.Cancel(); } catch { /* swallow */ }
        _cts.Dispose();
    }

    /// <summary>
    /// Case-insensitive equality on the gametype name segment of the <see cref="_inFlight"/>
    /// dictionary key. Mirrors the same comparer the rating stores use, so the in-flight
    /// lookup and the store-row lookup address the same logical entry.
    /// </summary>
    private sealed class InFlightKeyComparer : IEqualityComparer<(PlayerKey, string)>
    {
        public static readonly InFlightKeyComparer Instance = new();
        public bool Equals((PlayerKey, string) x, (PlayerKey, string) y) =>
            x.Item1.Equals(y.Item1)
            && string.Equals(x.Item2, y.Item2, StringComparison.OrdinalIgnoreCase);
        public int GetHashCode((PlayerKey, string) obj) =>
            HashCode.Combine(
                obj.Item1.GetHashCode(),
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Item2 ?? string.Empty));
    }
}
