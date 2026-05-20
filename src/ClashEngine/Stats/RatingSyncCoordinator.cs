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
/// Bridges player connect / disconnect lifecycle into <see cref="IRatingSync"/>:
/// <list type="bullet">
///   <item><b>Connect</b>: fire-and-forget pull of every registered gametype's rating for
///         the connecting player. The pull merges back into <see cref="IRatingStore"/> with
///         a newer-<see cref="Rating.LastSeen"/>-wins guard so a freshly-finalized match
///         (whose envelope hasn't reached the stats server yet) isn't clobbered by an older
///         server row.</item>
///   <item><b>Disconnect</b>: fire-and-forget bulk push of every row the player owns in the
///         local store. The server's <c>updatedAt</c> guard makes the push idempotent for
///         rows the server already has at the same or newer timestamp, so we don't need to
///         track which rows changed locally during the session.</item>
/// </list>
/// </summary>
/// <remarks>
/// <para>All HTTP work happens off the mainloop via <see cref="Task.Run(Func{Task})"/>; the
/// connect/disconnect handlers return immediately so <see cref="Events.PlayerStateObserver"/>
/// can finish its callback. Errors are logged and swallowed -- a sync failure must not
/// destabilize the player session.</para>
///
/// <para>Cancellation: <see cref="Dispose"/> cancels every in-flight pull/push so the engine
/// can unload cleanly without waiting on outstanding HTTP. The coordinator does NOT block on
/// completion -- in-flight tasks observe the cancellation token and unwind on their own.</para>
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
    /// PlayerStateObserver.PlayerConnected handler. Fire-and-forget per-gametype pull; the
    /// handler itself returns synchronously so the observer's mainloop callback finishes
    /// promptly. <paramref name="at"/> is unused -- the engine's local clock has no bearing
    /// on the server's <c>updatedAt</c> compare; we use whatever the server returns.
    /// </summary>
    public void OnPlayerConnected(PlayerKey key, DateTimeOffset at)
    {
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
    /// PlayerStateObserver.PlayerDisconnected handler. Snapshots the player's full row set
    /// synchronously (so we capture state BEFORE the engine releases anything), then fires
    /// the push off-thread.
    /// </summary>
    public void OnPlayerDisconnected(PlayerKey key, DateTimeOffset at)
    {
        // Snapshot synchronously while we still have a coherent view. The Snapshot() copy is
        // safe to read off-thread.
        var entries = new List<RatingEntry>();
        foreach (var entry in _ratings.Snapshot())
        {
            if (entry.Player.Equals(key)) entries.Add(entry);
        }
        if (entries.Count == 0) return;

        var token = _cts.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                var result = await _sync.TryPushBatchAsync(entries, token).ConfigureAwait(false);
                // The sync impl already logs success / failure detail; here we only need to
                // surface the disconnect-scoped context the impl doesn't have.
                if (result.Status == RatingPushStatus.Ok && _log is { } log)
                {
                    log.LogM(LogLevel.Drivel, LogCategory,
                        $"Pushed disconnect snapshot for '{key.Name}' ({entries.Count} row(s), " +
                        $"{result.Accepted} stored, {result.Skipped} skipped).");
                }
            }
            catch (OperationCanceledException) { /* shutdown */ }
            catch (Exception ex)
            {
                _log.LogM(LogLevel.Warn, LogCategory,
                    $"Push-on-disconnect failed for '{key.Name}': {ex}");
            }
        }, token);
    }

    private async Task PullAllAsync(PlayerKey key, IReadOnlyList<string> gameTypeNames, CancellationToken ct)
    {
        int merged = 0;
        int kept = 0;
        for (int i = 0; i < gameTypeNames.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            string gt = gameTypeNames[i];
            Rating? remote = await _sync.TryPullAsync(key.Name, gt, ct).ConfigureAwait(false);
            if (remote is null) continue;

            // Newer-updatedAt-wins. If the local cache has a row whose LastSeen is at or
            // after the server's, we don't overwrite -- avoids stomping a just-finalized
            // match whose envelope is still queued in the HttpMatchUploader.
            if (_ratings.TryGet(key, gt, out var local) && local.LastSeen >= remote.Value.LastSeen)
            {
                kept++;
                continue;
            }

            _ratings.Set(key, gt, remote.Value);
            merged++;
        }

        if (merged > 0 || kept > 0)
        {
            _log.LogM(LogLevel.Drivel, LogCategory,
                $"Pulled ratings for '{key.Name}': {merged} merged, {kept} kept-newer-local, " +
                $"{gameTypeNames.Count - merged - kept} server-empty.");
        }
    }

    public void Dispose()
    {
        try { _cts.Cancel(); } catch { /* swallow */ }
        _cts.Dispose();
    }
}
