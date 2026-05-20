using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ClashEngine.Core.GameType;
using ClashEngine.Core.Identity;
using ClashEngine.Core.Ratings;
using SS.Core.ComponentInterfaces;

namespace ClashEngine.Stats;

/// <summary>
/// Seeds a player's local <see cref="IRatingStore"/> from the stats server <i>only the
/// first time they connect to a zone that has no persisted data for them</i>. After that
/// the local store + the match-upload envelope channel keep ratings consistent without
/// further pulls or pushes.
/// </summary>
/// <remarks>
/// <para>The gate is "does the local cache hold ANY row for this player?". If yes (any
/// gametype), we trust the local store -- it was either loaded from
/// <see cref="Persistence.PersistRatingStore"/>'s persisted blob during the player's
/// session-load, or written during this session by <see cref="Core.Ratings.RatingUpdater"/>.
/// If no, we fire off a per-gametype pull. A null pull (server has no row either) leaves the
/// cache empty -- <see cref="IRatingStore.Get"/> returns <see cref="Rating.Default"/> on a
/// miss, so "start from scratch" is the natural behavior.</para>
///
/// <para>There is intentionally no push. Match envelopes carry post-match ratings back to
/// the server as a side effect of every finalized match; a separate disconnect or periodic
/// push would just be a parallel channel for the same data without fixing the actual failure
/// modes (envelope queue overflow / zone crash mid-session) -- those want a durable upload
/// queue, not a second channel.</para>
///
/// <para>HTTP work runs on <see cref="Task.Run(Func{Task})"/>; the connect handler returns
/// immediately so <see cref="Events.PlayerStateObserver"/> can finish its mainloop callback.
/// Failures are logged and swallowed. <see cref="Dispose"/> cancels every in-flight pull;
/// the coordinator does not block on completion -- in-flight tasks observe the cancellation
/// token and unwind on their own.</para>
/// </remarks>
public sealed class RatingSyncCoordinator : IDisposable
{
    private const string LogCategory = nameof(RatingSyncCoordinator);

    private readonly IRatingSync _sync;
    private readonly IRatingStore _ratings;
    private readonly GameTypeRegistry _gameTypes;
    private readonly ILogManager _log;
    private readonly CancellationTokenSource _cts = new();

    public RatingSyncCoordinator(
        IRatingSync sync,
        IRatingStore ratings,
        GameTypeRegistry gameTypes,
        ILogManager log)
    {
        _sync = sync ?? throw new ArgumentNullException(nameof(sync));
        _ratings = ratings ?? throw new ArgumentNullException(nameof(ratings));
        _gameTypes = gameTypes ?? throw new ArgumentNullException(nameof(gameTypes));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    /// <summary>
    /// PlayerStateObserver.PlayerConnected handler. If the local cache already has any row
    /// for this player (across any gametype), the pull is skipped -- the local store is
    /// already the source of truth. Otherwise per-gametype pulls fire off-thread and any
    /// row the server returns is written into the cache.
    /// </summary>
    public void OnPlayerConnected(PlayerKey key, DateTimeOffset at)
    {
        if (HasAnyLocalRating(key)) return;

        // Snapshot the gametype names up-front. The registry can be hot-reloaded; pulling
        // against whatever is registered at connect-time is the right semantics (a gametype
        // that later disappears just stops being pulled on subsequent connects).
        var names = new List<string>();
        foreach (var def in _gameTypes.Definitions) names.Add(def.Name);
        if (names.Count == 0) return;

        var token = _cts.Token;
        _ = Task.Run(async () =>
        {
            try { await PullAllAsync(key, names, token).ConfigureAwait(false); }
            catch (OperationCanceledException) { /* shutdown */ }
            catch (Exception ex)
            {
                _log.LogM(LogLevel.Warn, LogCategory,
                    $"Pull-on-connect failed for '{key.Name}': {ex}");
            }
        }, token);
    }

    /// <summary>
    /// Walks the rating-store snapshot looking for any entry matching <paramref name="key"/>.
    /// The snapshot is a point-in-time copy, safe to enumerate on the mainloop thread.
    /// Returns true on the first hit; we don't care which gametype it is.
    /// </summary>
    private bool HasAnyLocalRating(PlayerKey key)
    {
        foreach (var entry in _ratings.Snapshot())
        {
            if (entry.Player.Equals(key)) return true;
        }
        return false;
    }

    private async Task PullAllAsync(PlayerKey key, IReadOnlyList<string> gameTypeNames, CancellationToken ct)
    {
        int seeded = 0;
        int serverEmpty = 0;
        for (int i = 0; i < gameTypeNames.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            string gt = gameTypeNames[i];
            Rating? remote = await _sync.TryPullAsync(key.Name, gt, ct).ConfigureAwait(false);
            if (remote is null)
            {
                serverEmpty++;
                continue;
            }
            _ratings.Set(key, gt, remote.Value);
            seeded++;
        }

        if (seeded > 0 || serverEmpty > 0)
        {
            _log.LogM(LogLevel.Drivel, LogCategory,
                $"Seeded ratings for '{key.Name}': {seeded} from server, {serverEmpty} server-empty (left at default).");
        }
    }

    public void Dispose()
    {
        try { _cts.Cancel(); } catch { /* swallow */ }
        _cts.Dispose();
    }
}
