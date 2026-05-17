using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using ClashEngine.Adapter;
using ClashEngine.Core;
using ClashEngine.Core.Adapter;
using ClashEngine.Core.Identity;
using ClashEngine.Core.Matches;
using ClashEngine.Core.Matching;
using ClashEngine.Core.Penalties;
using ClashEngine.Core.Queue;
using SS.Core;
using SS.Core.ComponentInterfaces;
using SS.Utilities;
using ClashEngine.Recording;

namespace ClashEngine.Replay;

/// <summary>
/// <see cref="IMatchmakingTelemetry"/> consumer that drives <see cref="MatchRecorder"/> to
/// record every started match. On <see cref="OnMatchStarted"/> it opens a recording session
/// scoped to the match's participants and stashes the file path under the match id. On
/// <see cref="OnMatchEnded"/> it stops the session. The recorder does NOT upload or delete
/// files itself -- it exposes <see cref="GetRecordingPath"/> so
/// <see cref="ClashEngine.Stats.ClashStatsTelemetry"/> can attach the path to the match
/// envelope, and <see cref="IsRecordingComplete"/> so the uploader can wait until the worker
/// thread has flushed the file before posting.
/// </summary>
/// <remarks>
/// <para>Unlike the per-arena <c>IReplayController</c>, <see cref="MatchRecorder"/> is
/// per-session -- multiple matches can record concurrently in the same arena, each scoped to
/// its own player set. We add every team's players to the session up front; reconnects add
/// nothing new (they re-enter via the recorder's own callbacks).</para>
///
/// <para>Filename: <c>{recordingDir}/{matchId:N}.replay</c>. The session writes asynchronously
/// on the recorder's worker thread, so the file isn't fully on disk until the session goes
/// inactive -- that's what <see cref="IsRecordingComplete"/> waits on.</para>
/// </remarks>
public sealed class ClashReplayRecorder : IMatchmakingTelemetry, IMatchChatRecorder
{
    private const string LogCategory = nameof(ClashReplayRecorder);

    /// <summary>How long to keep the session capturing after <see cref="OnMatchEnded"/> fires
    /// before calling <c>StopRecording</c>. Smooths the abrupt cut at the eliminating kill --
    /// the playback shows a few seconds of post-kill aftermath instead of ending mid-explosion.
    /// Stays under the uploader's 5-minute readiness timeout by a wide margin.</summary>
    private const int PostMatchTailMs = 5000;

    private readonly MatchmakingEngine _engine;
    private readonly MatchRecorder _recorder;
    private readonly IArenaManager _arenaManager;
    private readonly PlayerKeyResolver _resolver;
    private readonly IMainloopTimer _timer;
    private readonly ILogManager _log;
    private readonly string _recordingDir;
    private readonly TimerDelegate<Guid> _onTailExpired;

    // Captured at OnMatchProposed (same pattern ClashStatsTelemetry uses) so OnMatchStarted has
    // the queue's MatchArenaName for the recording. Cleared at OnMatchEnded.
    private readonly Dictionary<Guid, QueueDefinition> _queueByMatch = new();

    // matchId -> handle. Populated at OnMatchStarted; entries linger until DeleteRecording is
    // called (after upload) or pruned via PruneStale.
    private readonly ConcurrentDictionary<Guid, RecordingHandle> _byMatch = new();

    public ClashReplayRecorder(
        MatchmakingEngine engine,
        MatchRecorder recorder,
        IArenaManager arenaManager,
        PlayerKeyResolver resolver,
        IMainloopTimer timer,
        ILogManager log,
        string recordingDir)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _recorder = recorder ?? throw new ArgumentNullException(nameof(recorder));
        _arenaManager = arenaManager ?? throw new ArgumentNullException(nameof(arenaManager));
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        _timer = timer ?? throw new ArgumentNullException(nameof(timer));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        if (string.IsNullOrWhiteSpace(recordingDir))
            throw new ArgumentException("Must be non-empty.", nameof(recordingDir));
        _recordingDir = recordingDir;
        _onTailExpired = OnTailExpired;

        // Create the recording directory once at construction time so OnMatchStarted (which
        // runs on the mainloop) doesn't pay a Directory.CreateDirectory syscall every time a
        // match begins. If creation fails here, OnMatchStarted will retry and log on the
        // mainloop -- not ideal, but at least we tried the off-mainloop path first.
        try { Directory.CreateDirectory(_recordingDir); }
        catch (Exception ex)
        {
            _log.LogM(LogLevel.Warn, LogCategory,
                $"Could not pre-create recording directory '{_recordingDir}': {ex.Message}");
        }
    }

    /// <summary>Returns the absolute path of the recording file for <paramref name="matchId"/>,
    /// or <see langword="null"/> if no recording was started for this match (e.g. recorder
    /// refused, or the match's queue had no <c>MatchArena</c> configured).</summary>
    public string? GetRecordingPath(Guid matchId) =>
        _byMatch.TryGetValue(matchId, out var h) ? h.FilePath : null;

    /// <summary>True once the recorder's worker thread has finished writing the file. Returns
    /// true also when no recording exists for this id (so the uploader doesn't block forever
    /// on a missing match).</summary>
    public bool IsRecordingComplete(Guid matchId)
    {
        if (!_byMatch.TryGetValue(matchId, out var h)) return true;
        if (!h.StopRequested) return false;

        // Once we've called StopRecording, the session reports IsActive=false after the worker
        // drains and finalizes the file. Combine with File.Exists so an empty/in-flight write
        // doesn't surface.
        return !h.Session.IsActive && File.Exists(h.FilePath);
    }

    /// <summary>Removes the entry and deletes the recording file from disk. Called by the
    /// uploader after a successful POST.</summary>
    public void DeleteRecording(Guid matchId)
    {
        if (!_byMatch.TryRemove(matchId, out var h)) return;
        try
        {
            if (File.Exists(h.FilePath))
            {
                File.Delete(h.FilePath);
                _log.LogM(LogLevel.Info, LogCategory, $"Deleted uploaded recording {h.FilePath}.");
            }
        }
        catch (Exception ex)
        {
            _log.LogM(LogLevel.Warn, LogCategory,
                $"Failed to delete recording {h.FilePath} for match {matchId:N}: {ex.Message}");
        }
    }

    /// <summary>Drops entries older than <paramref name="maxAge"/>. The recorder otherwise holds
    /// onto state forever in the rare case an upload never completes; the host should call this
    /// from a periodic timer.</summary>
    public void PruneStale(DateTimeOffset now, TimeSpan maxAge)
    {
        List<Guid>? expired = null;
        foreach (var kvp in _byMatch)
        {
            if (now - kvp.Value.StartedAt > maxAge)
                (expired ??= new List<Guid>()).Add(kvp.Key);
        }
        if (expired is null) return;
        foreach (var id in expired)
        {
            if (_byMatch.TryRemove(id, out var h))
                _log.LogM(LogLevel.Warn, LogCategory,
                    $"Pruned stale recording entry for match {id:N} ({h.FilePath}); upload likely failed.");
        }
    }

    /// <summary>
    /// <see cref="IMatchChatRecorder"/>: append a server-originated arena chat line to the
    /// match's session. No-op if the match has no active session (recording not started,
    /// already stopped, or in the post-match tail after the queue was completed). Runs on the
    /// mainloop, same as the recorder's other event callbacks.
    /// </summary>
    public void RecordArenaMessage(Guid matchId, ReadOnlySpan<char> message, ChatSound sound)
    {
        if (!_byMatch.TryGetValue(matchId, out var h)) return;
        if (h.StopRequested) return;
        var session = h.Session;
        if (!session.IsActive || session.RecorderQueue.IsAddingCompleted) return;
        ReplayEventEncoder.EncodeArenaChat(session, sound, message);
    }

    public void OnMatchProposed(MatchProposal proposal)
    {
        if (!_engine.Queues.TryGet(proposal.QueueName, out var queueDef)) return;
        // Match is created by the engine before this fires -- find its id by Teams-reference equality.
        foreach (var (matchId, match) in _engine.ActiveMatches)
        {
            if (ReferenceEquals(match.Teams, proposal.Teams))
            {
                _queueByMatch[matchId] = queueDef;
                return;
            }
        }
    }

    public void OnMatchStarted(ActiveMatch match)
    {
        if (_byMatch.ContainsKey(match.MatchId)) return;
        if (!_queueByMatch.TryGetValue(match.MatchId, out var queue))
        {
            _log.LogM(LogLevel.Warn, LogCategory,
                $"Match {match.MatchId:N} started but no queue captured; skipping recording.");
            return;
        }

        string? arenaName = queue.MatchArenaName;
        if (string.IsNullOrWhiteSpace(arenaName))
        {
            _log.LogM(LogLevel.Info, LogCategory,
                $"Queue '{queue.UniqueId}' has no MatchArena configured; skipping recording for {match.MatchId:N}.");
            return;
        }

        var arena = _arenaManager.FindArena(arenaName);
        if (arena is null)
        {
            _log.LogM(LogLevel.Warn, LogCategory,
                $"Arena '{arenaName}' not found at match start; skipping recording for {match.MatchId:N}.");
            return;
        }

        // Directory was pre-created at construction time. If something deleted it since
        // (administrative action, fs reset), the worker thread inside MatchRecorder will retry
        // creation off the mainloop and log on failure -- no syscall here.

        string filePath = Path.Combine(_recordingDir, $"{match.MatchId:N}.replay");
        string comments = $"ClashEngine match {match.MatchId:N}; queue={queue.UniqueId}; gameType={match.GameType.Value}; arena={arenaName}";

        var session = _recorder.StartRecording(arena, filePath, comments, recorder: null);
        if (session is null)
        {
            _log.LogM(LogLevel.Warn, LogCategory,
                $"MatchRecorder.StartRecording returned null for match {match.MatchId:N} in arena '{arenaName}'.");
            return;
        }

        // Add every match participant up front. Players not currently connected (rare at
        // OnMatchStarted, but possible if a name resolves later) won't be added -- the recorder
        // will not capture them. AddPlayer is idempotent so reconnect-driven re-adds are safe.
        int added = 0;
        for (int t = 0; t < match.Teams.Count; t++)
        {
            for (int j = 0; j < match.Teams[t].Count; j++)
            {
                var key = match.Teams[t][j];
                var player = _resolver.Resolve(key);
                if (player is not null && session.AddPlayer(player)) added++;
            }
        }

        var startedAt = match.StartedAt ?? DateTimeOffset.UtcNow;
        _byMatch[match.MatchId] = new RecordingHandle(session, arenaName, filePath, startedAt);
        _log.LogM(LogLevel.Info, LogCategory,
            $"Started recording for match {match.MatchId:N} -> {filePath} ({added} players).");
    }

    public void OnMatchEnded(MatchOutcome outcome)
    {
        // Always release the queue mapping -- symmetric with the OnMatchProposed capture.
        _queueByMatch.Remove(outcome.MatchId);

        if (!_byMatch.TryGetValue(outcome.MatchId, out _)) return;

        // Defer StopRecording by PostMatchTailMs so the recording captures a few seconds of
        // post-match action (death animations, victory laps) instead of cutting off mid-frame
        // at the eliminating kill. The session keeps accepting events from the arena until
        // StopRecording lands. IsRecordingComplete continues to return false during the tail
        // because StopRequested is still false, which keeps the uploader's worker thread
        // polling rather than uploading metadata-only.
        _timer.SetTimer(_onTailExpired, PostMatchTailMs, Timeout.Infinite, outcome.MatchId, this);
    }

    private bool OnTailExpired(Guid matchId)
    {
        StopAndMark(matchId);
        return false;
    }

    private void StopAndMark(Guid matchId)
    {
        if (!_byMatch.TryGetValue(matchId, out var handle)) return;
        if (handle.StopRequested) return;

        bool stopped = _recorder.StopRecording(handle.Session);
        _byMatch[matchId] = handle with { StopRequested = true };
        if (!stopped)
        {
            _log.LogM(LogLevel.Warn, LogCategory,
                $"MatchRecorder.StopRecording returned false for match {matchId:N} " +
                "(session may have already stopped).");
        }
    }

    /// <summary>
    /// Cancel any pending post-match tails and stop their sessions immediately. Called from
    /// <c>ClashModule.PreUnloadAsync</c> so we don't leak active recordings or fire timers
    /// after the module has been torn down.
    /// </summary>
    public void Shutdown()
    {
        _timer.ClearTimer(_onTailExpired, this);
        foreach (var kvp in _byMatch)
            StopAndMark(kvp.Key);
    }

    private sealed record RecordingHandle(
        MatchRecorder.Session Session,
        string ArenaName,
        string FilePath,
        DateTimeOffset StartedAt,
        bool StopRequested = false);
}
