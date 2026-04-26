using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using SS.Core;
using SS.Core.ComponentCallbacks;
using SS.Core.ComponentInterfaces;
using SS.Packets.Game;
using SS.Replay.FileFormat;
using SS.Replay.FileFormat.Events;
using SS.Utilities;

namespace ClashEngine.Recording;

/// <summary>
/// Records replays scoped to arbitrary partitions of players within an arena. Lives inside the
/// ClashEngine plug-in so the server stays free of matchmaking-specific recording logic.
/// </summary>
/// <remarks>
/// <para>This is the per-match counterpart to SubspaceServer's <c>ReplayModule</c>. The replay
/// module manages a single recording per arena, which is incorrect when an arena hosts multiple
/// simultaneous matches (e.g. via the server's <c>IMatchFocus</c>). This recorder supports any
/// number of concurrent sessions per arena and only writes events for players that were
/// explicitly added to a session.</para>
///
/// <para>Output is the Afluxion .replay file format (the same format the replay module
/// produces). Playback is intentionally not implemented here -- the regular replay module reads
/// these files just fine.</para>
///
/// <para>Unlike a regular SS module, this is a plain class instantiated and lifecycle-managed
/// by <see cref="ClashEngine.ClashModule"/>. It registers / unregisters its own server
/// callbacks; it does not register on the broker as a public interface, since only ClashEngine
/// internals consume it.</para>
/// </remarks>
public sealed class MatchRecorder
{
    private const string LogCategory = nameof(MatchRecorder);
    private const uint MapChecksumKey = 0x46692018;

    private readonly IComponentBroker _broker;
    private readonly IArenaManager _arenaManager;
    private readonly ILogManager _logManager;
    private readonly IMainloop _mainloop;
    private readonly IMapData _mapData;
    private readonly INetwork _network;
    private readonly ISecuritySeedSync _securitySeedSync;

    // All access from the mainloop thread.
    private readonly Dictionary<Arena, List<Session>> _arenaSessions = new(Constants.TargetArenaCount);

    private bool _registered;

    public MatchRecorder(
        IComponentBroker broker,
        IArenaManager arenaManager,
        ILogManager logManager,
        IMainloop mainloop,
        IMapData mapData,
        INetwork network,
        ISecuritySeedSync securitySeedSync)
    {
        _broker = broker ?? throw new ArgumentNullException(nameof(broker));
        _arenaManager = arenaManager ?? throw new ArgumentNullException(nameof(arenaManager));
        _logManager = logManager ?? throw new ArgumentNullException(nameof(logManager));
        _mainloop = mainloop ?? throw new ArgumentNullException(nameof(mainloop));
        _mapData = mapData ?? throw new ArgumentNullException(nameof(mapData));
        _network = network ?? throw new ArgumentNullException(nameof(network));
        _securitySeedSync = securitySeedSync ?? throw new ArgumentNullException(nameof(securitySeedSync));
    }

    public void Register()
    {
        if (_registered) return;

        _network.AddPacket(C2SPacketType.Position, Packet_Position);

        ArenaActionCallback.Register(_broker, Callback_ArenaAction);
        PlayerActionCallback.Register(_broker, Callback_PlayerAction);
        ShipFreqChangeCallback.Register(_broker, Callback_ShipFreqChange);
        KillCallback.Register(_broker, Callback_Kill);
        ChatMessageCallback.Register(_broker, Callback_ChatMessage);
        CrownToggledCallback.Register(_broker, Callback_CrownToggled);
        AttachCallback.Register(_broker, Callback_Attach);
        TurretKickoffCallback.Register(_broker, Callback_TurretKickoff);
        SecuritySeedChangedCallback.Register(_broker, Callback_SecuritySeedChanged);

        _registered = true;
    }

    public void Unregister()
    {
        if (!_registered) return;

        ArenaActionCallback.Unregister(_broker, Callback_ArenaAction);
        PlayerActionCallback.Unregister(_broker, Callback_PlayerAction);
        ShipFreqChangeCallback.Unregister(_broker, Callback_ShipFreqChange);
        KillCallback.Unregister(_broker, Callback_Kill);
        ChatMessageCallback.Unregister(_broker, Callback_ChatMessage);
        CrownToggledCallback.Unregister(_broker, Callback_CrownToggled);
        AttachCallback.Unregister(_broker, Callback_Attach);
        TurretKickoffCallback.Unregister(_broker, Callback_TurretKickoff);
        SecuritySeedChangedCallback.Unregister(_broker, Callback_SecuritySeedChanged);

        _network.RemovePacket(C2SPacketType.Position, Packet_Position);

        _registered = false;
    }

    #region Public API

    /// <summary>
    /// Starts a new recording session. The session starts empty; use
    /// <see cref="Session.AddPlayer"/> to add players. The file is opened on a background
    /// thread, so this call returns immediately and does not block on disk I/O. Returns
    /// <see langword="null"/> if <paramref name="arena"/> is null or <paramref name="filePath"/>
    /// is empty.
    /// </summary>
    public Session? StartRecording(Arena arena, string filePath, string? comments, Player? recorder)
    {
        Debug.Assert(_mainloop.IsMainloop);

        if (arena is null || string.IsNullOrWhiteSpace(filePath))
            return null;

        Session session = new(arena, filePath, recorder?.Name);

        if (!_arenaSessions.TryGetValue(arena, out List<Session>? list))
        {
            list = [];
            _arenaSessions.Add(arena, list);
        }
        list.Add(session);

        // Seed the recording with security info so the worker has a baseline as soon as it starts.
        _securitySeedSync.GetCurrentSeedInfo(out uint greenSeed, out uint doorSeed, out uint timestamp);
        ServerTick now = ServerTick.Now;
        ReplayEventEncoder.EncodeSecuritySeedChange(
            session, greenSeed, doorSeed, now, (uint)(now - (ServerTick)timestamp));

        session.RecorderTask = Task.Factory.StartNew(
            () =>
            {
                DoRecording(session, comments);
                _mainloop.QueueMainWorkItem(MainloopWorkItem_EndRecording, session);
            },
            TaskCreationOptions.LongRunning | TaskCreationOptions.DenyChildAttach);

        return session;
    }

    /// <summary>
    /// Stops a session that was previously started by <see cref="StartRecording"/>. Tells the
    /// worker to drain its queue and finalize the file. Returns immediately; the file may still
    /// be flushing when this returns.
    /// </summary>
    public bool StopRecording(Session session)
    {
        Debug.Assert(_mainloop.IsMainloop);
        return session is not null && StopSession(session);
    }

    #endregion

    #region Callbacks

    private void Callback_ArenaAction(Arena arena, ArenaAction action)
    {
        if (action != ArenaAction.Destroy)
            return;

        if (!_arenaSessions.TryGetValue(arena, out List<Session>? list))
            return;

        // Stop any sessions still running in this arena.
        for (int i = 0; i < list.Count; i++)
        {
            StopSession(list[i]);
        }
    }

    private void Callback_PlayerAction(Player player, PlayerAction action, Arena? arena)
    {
        Debug.Assert(_mainloop.IsMainloop);

        if (arena is null) return;
        if (action != PlayerAction.LeaveArena) return;
        if (!_arenaSessions.TryGetValue(arena, out List<Session>? list)) return;

        // Auto-remove the player from any sessions tracking them so subsequent events
        // (which won't have the player) don't leak in, and so a Leave event is recorded.
        for (int i = 0; i < list.Count; i++)
        {
            Session session = list[i];
            if (!session.RemovePlayerInternal(player)) continue;
            // Skip the Leave write if the session has already been told to stop -- the
            // session can stay in _arenaSessions until MainloopWorkItem_EndRecording drains,
            // but the underlying BlockingCollection will throw on Add once CompleteAdding has run.
            if (session.RecorderQueue.IsAddingCompleted) continue;
            ReplayEventEncoder.EncodeLeave(session, player);
        }
    }

    private void Callback_ShipFreqChange(Player player, ShipType newShip, ShipType oldShip, short newFreq, short oldFreq)
    {
        Debug.Assert(_mainloop.IsMainloop);
        Arena? arena = player.Arena;
        if (arena is null || !_arenaSessions.TryGetValue(arena, out List<Session>? list)) return;

        for (int i = 0; i < list.Count; i++)
        {
            Session session = list[i];
            if (!session.ContainsPlayerInternal(player)) continue;
            if (session.RecorderQueue.IsAddingCompleted) continue;

            if (newShip == oldShip)
                ReplayEventEncoder.EncodeFreqChange(session, player, newFreq);
            else
                ReplayEventEncoder.EncodeShipChange(session, player, newShip, newFreq);
        }
    }

    private void Callback_Kill(Arena arena, Player killer, Player killed, short bounty, short flagCount, short points, Prize green)
    {
        Debug.Assert(_mainloop.IsMainloop);
        if (arena is null || !_arenaSessions.TryGetValue(arena, out List<Session>? list)) return;

        for (int i = 0; i < list.Count; i++)
        {
            Session session = list[i];
            // Both players need to be in the session for the kill to belong to it.
            // (If only one is, the other is from a different match and including the kill
            // would expose cross-match info.)
            if (!session.ContainsPlayerInternal(killer) || !session.ContainsPlayerInternal(killed)) continue;
            if (session.RecorderQueue.IsAddingCompleted) continue;
            ReplayEventEncoder.EncodeKill(session, killer, killed, points, flagCount);
        }
    }

    private void Callback_ChatMessage(Arena? arena, Player? player, ChatMessageType type, ChatSound sound, Player? toPlayer, short freq, ReadOnlySpan<char> message)
    {
        Debug.Assert(_mainloop.IsMainloop);
        if (arena is null || player is null) return;
        if (!_arenaSessions.TryGetValue(arena, out List<Session>? list)) return;

        // Only record chat from players belonging to a session, and only the message types
        // that make sense for a per-match recording.
        if (type != ChatMessageType.Pub && type != ChatMessageType.PubMacro && type != ChatMessageType.Freq)
            return;

        for (int i = 0; i < list.Count; i++)
        {
            Session session = list[i];
            if (!session.ContainsPlayerInternal(player)) continue;
            if (session.RecorderQueue.IsAddingCompleted) continue;
            ReplayEventEncoder.EncodeChat(session, player, type, sound, message);
        }
    }

    private void Callback_CrownToggled(Player player, bool on)
    {
        Debug.Assert(_mainloop.IsMainloop);
        Arena? arena = player.Arena;
        if (arena is null || !_arenaSessions.TryGetValue(arena, out List<Session>? list)) return;

        for (int i = 0; i < list.Count; i++)
        {
            Session session = list[i];
            if (!session.ContainsPlayerInternal(player)) continue;
            if (session.RecorderQueue.IsAddingCompleted) continue;
            ReplayEventEncoder.EncodeCrownToggle(session, player, on);
        }
    }

    private void Callback_Attach(Player player, Player? to)
    {
        Debug.Assert(_mainloop.IsMainloop);
        Arena? arena = player.Arena;
        if (arena is null || !_arenaSessions.TryGetValue(arena, out List<Session>? list)) return;

        // Only record attaches that stay within the session: the attacher must be tracked,
        // and if attaching to someone, the target must be tracked too.
        if (to is not null && to.Arena != arena) return;

        for (int i = 0; i < list.Count; i++)
        {
            Session session = list[i];
            if (!session.ContainsPlayerInternal(player)) continue;
            if (to is not null && !session.ContainsPlayerInternal(to)) continue;
            if (session.RecorderQueue.IsAddingCompleted) continue;
            ReplayEventEncoder.EncodeAttach(session, player, to);
        }
    }

    private void Callback_TurretKickoff(Player player)
    {
        Debug.Assert(_mainloop.IsMainloop);
        Arena? arena = player.Arena;
        if (arena is null || !_arenaSessions.TryGetValue(arena, out List<Session>? list)) return;

        for (int i = 0; i < list.Count; i++)
        {
            Session session = list[i];
            if (!session.ContainsPlayerInternal(player)) continue;
            if (session.RecorderQueue.IsAddingCompleted) continue;
            ReplayEventEncoder.EncodeTurretKickoff(session, player);
        }
    }

    private void Callback_SecuritySeedChanged(uint greenSeed, uint doorSeed, ServerTick timestamp)
    {
        Debug.Assert(_mainloop.IsMainloop);

        // Security seed changes are arena-independent; fan out to all active sessions.
        foreach (List<Session> list in _arenaSessions.Values)
        {
            for (int i = 0; i < list.Count; i++)
            {
                Session session = list[i];
                if (session.RecorderQueue.IsAddingCompleted) continue;
                ReplayEventEncoder.EncodeSecuritySeedChange(session, greenSeed, doorSeed, timestamp, deltaTicks: 0);
            }
        }
    }

    #endregion

    private void Packet_Position(Player player, ReadOnlySpan<byte> data, NetReceiveFlags flags)
    {
        Debug.Assert(_mainloop.IsMainloop);

        int length = data.Length;
        if (length != C2S_PositionPacket.Length && length != C2S_PositionPacket.LengthWithExtra)
            return;

        Arena? arena = player.Arena;
        if (arena is null || !_arenaSessions.TryGetValue(arena, out List<Session>? list)) return;

        ref readonly C2S_PositionPacket c2sPosition = ref MemoryMarshal.AsRef<C2S_PositionPacket>(data);

        for (int i = 0; i < list.Count; i++)
        {
            Session session = list[i];
            if (!session.ContainsPlayerInternal(player)) continue;
            if (session.RecorderQueue.IsAddingCompleted) continue;

            if (length == C2S_PositionPacket.Length)
            {
                ReplayEventEncoder.EncodePosition(session, player, in c2sPosition);
            }
            else
            {
                ref readonly ExtraPositionData extra = ref MemoryMarshal.AsRef<ExtraPositionData>(data.Slice(C2S_PositionPacket.Length, ExtraPositionData.Length));
                ReplayEventEncoder.EncodePositionWithExtra(session, player, in c2sPosition, in extra);
            }
        }
    }

    #region Session lifecycle helpers

    private bool StopSession(Session session)
    {
        Debug.Assert(_mainloop.IsMainloop);

        lock (session.Lock)
        {
            if (!session.IsActive || session.RecorderQueue.IsAddingCompleted)
                return false;

            session.IsActive = false;
            session.RecorderQueue.CompleteAdding();
            return true;
        }
    }

    private void MainloopWorkItem_EndRecording(Session session)
    {
        Debug.Assert(_mainloop.IsMainloop);

        // Make sure the session is marked stopped (in case the worker exited due to an error
        // before StopRecording was called).
        StopSession(session);

        // Drain any leftover buffers (in case the worker bailed early).
        while (session.RecorderQueue.TryTake(out RecordBuffer leftover))
        {
            ReplayEventEncoder.Pool.Return(leftover.Buffer, true);
        }
        session.RecorderQueue.Dispose();

        // Remove the session from the arena's list.
        if (_arenaSessions.TryGetValue(session.Arena, out List<Session>? list))
        {
            list.Remove(session);
            if (list.Count == 0)
            {
                _arenaSessions.Remove(session.Arena);
            }
        }
    }

    #endregion

    #region Worker

    private void DoRecording(Session session, string? comments)
    {
        string path = session.FilePath;

        try
        {
            string? directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }
        }
        catch (Exception ex)
        {
            _logManager.LogA(LogLevel.Error, LogCategory, session.Arena, $"Unable to create directory for replay file '{path}'. {ex.Message}");
            return;
        }

        FileStream fileStream;
        try
        {
            fileStream = new(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
        }
        catch (Exception ex)
        {
            _logManager.LogA(LogLevel.Error, LogCategory, session.Arena, $"Unable to create replay file '{path}'. {ex.Message}");
            return;
        }

        try
        {
            int commentsLength = comments is not null ? StringUtils.DefaultEncoding.GetByteCount(comments) + 1 : 0;

            FileHeader fileHeader = new()
            {
                Header = "ass$game", // The $ is temporary; we update it at the end.
                Version = FileVersion.Current,
                Offset = (uint)FileHeader.Length + (uint)commentsLength,
                Events = 0,
                EndTime = 0,
                MaxPlayerId = 0,
                SpecFreq = (uint)session.Arena.SpecFreq,
                Recorded = DateTimeOffset.UtcNow,
                MapChecksum = _mapData.GetChecksum(session.Arena, MapChecksumKey),
                Recorder = session.StartedBy,
                ArenaName = session.Arena.Name,
            };

            Span<byte> fileHeaderBytes = MemoryMarshal.AsBytes(MemoryMarshal.CreateSpan(ref fileHeader, 1));
            fileStream.Write(fileHeaderBytes);

            if (commentsLength > 0)
            {
                byte[] commentsBuffer = ArrayPool<byte>.Shared.Rent(commentsLength);
                try
                {
                    Span<byte> commentsSpan = commentsBuffer.AsSpan(0, commentsLength);
                    StringUtils.WriteNullTerminatedString(commentsSpan, comments);
                    fileStream.Write(commentsSpan);
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(commentsBuffer, true);
                }
            }

            using GZipStream gzStream = new(fileStream, CompressionLevel.Optimal);

            ServerTick started = session.Started;
            uint eventCount = 0;
            int maxPlayerId = 0;

            while (!session.RecorderQueue.IsCompleted)
            {
                if (!session.RecorderQueue.TryTake(out RecordBuffer recordBuffer, -1))
                    continue;

                try
                {
                    Span<byte> eventBytes = recordBuffer.Buffer.AsSpan(0, recordBuffer.Length);
                    ref EventHeader eventHeader = ref MemoryMarshal.AsRef<EventHeader>(eventBytes);

                    // Normalize timestamps to start from 0.
                    eventHeader.Ticks = (ServerTick)(eventHeader.Ticks - started);

                    if (eventHeader.Type == EventType.PositionWrapper)
                    {
                        ref PositionWrapper positionEvent = ref MemoryMarshal.AsRef<PositionWrapper>(eventBytes);
                        positionEvent.PositionPacket.Time = (ServerTick)(positionEvent.PositionPacket.Time - started);
                    }
                    else if (eventHeader.Type == EventType.PositionWithExtraWrapper)
                    {
                        ref PositionWithExtraWrapper positionEvent = ref MemoryMarshal.AsRef<PositionWithExtraWrapper>(eventBytes);
                        positionEvent.PositionPacket.Time = (ServerTick)(positionEvent.PositionPacket.Time - started);
                    }
                    else if (eventHeader.Type == EventType.Enter)
                    {
                        ref Enter enter = ref MemoryMarshal.AsRef<Enter>(eventBytes);
                        if (enter.PlayerId > maxPlayerId)
                            maxPlayerId = enter.PlayerId;
                    }

                    gzStream.Write(eventBytes);
                    eventCount++;
                }
                finally
                {
                    ReplayEventEncoder.Pool.Return(recordBuffer.Buffer, true);
                }
            }

            // Patch the file header now that we know the totals.
            fileHeader.Header = "asssgame";
            fileHeader.Events = eventCount;
            fileHeader.EndTime = (uint)(ServerTick.Now - started);
            fileHeader.MaxPlayerId = (uint)maxPlayerId;

            gzStream.Flush();

            long originalPosition = fileStream.Position;
            fileStream.Position = 0;
            fileStream.Write(fileHeaderBytes);
            fileStream.Position = originalPosition;
        }
        catch (Exception ex)
        {
            _logManager.LogA(LogLevel.Error, LogCategory, session.Arena, $"Error recording to '{path}'. {ex.Message}");
        }
        finally
        {
            fileStream.Dispose();
        }
    }

    #endregion

    #region Helper types

    internal readonly struct RecordBuffer
    {
        public readonly byte[] Buffer;
        public readonly int Length;

        public RecordBuffer(byte[] buffer, int length)
        {
            Buffer = buffer;
            Length = length;
        }
    }

    /// <summary>
    /// Handle to a single recording session controlled by a <see cref="MatchRecorder"/>. A
    /// session represents one .replay file on disk; the recorder writes only events from
    /// players added to the session, so multiple sessions can run in the same arena
    /// simultaneously (e.g. when an arena hosts multiple matches via <c>IMatchFocus</c>) without
    /// their recordings bleeding into one another.
    /// </summary>
    public sealed class Session
    {
        /// <summary>The arena the session is recording in.</summary>
        public Arena Arena { get; }

        /// <summary>The path the recording is being written to.</summary>
        public string FilePath { get; }

        internal string? StartedBy { get; }
        internal ServerTick Started { get; }
        internal BlockingCollection<RecordBuffer> RecorderQueue { get; } = [];
        internal Task? RecorderTask;
        internal readonly Lock Lock = new();

        /// <summary>Whether the session is still accepting events. Becomes <see langword="false"/>
        /// after <see cref="MatchRecorder.StopRecording"/> is called or the session is torn down
        /// internally (e.g. arena destroy).</summary>
        public bool IsActive { get; set; } = true;

        // Mainloop-thread-only.
        private readonly HashSet<Player> _players = new(Constants.TargetPlayerCount);

        public Session(Arena arena, string filePath, string? startedBy)
        {
            Arena = arena;
            FilePath = filePath;
            StartedBy = startedBy;
            Started = ServerTick.Now;
        }

        /// <summary>Number of players currently being tracked by the session.</summary>
        public int PlayerCount => _players.Count;

        internal bool ContainsPlayerInternal(Player player) => _players.Contains(player);

        internal bool RemovePlayerInternal(Player player) => _players.Remove(player);

        /// <summary>Adds a player so subsequent events get recorded; writes an Enter event at
        /// the current tick. No-op if the session is closed or the player is already tracked.</summary>
        public bool AddPlayer(Player player)
        {
            if (player is null)
                return false;

            if (!IsActive || RecorderQueue.IsAddingCompleted)
                return false;

            if (!_players.Add(player))
                return false;

            ReplayEventEncoder.EncodeEnter(this, player);
            return true;
        }

        /// <summary>Removes a player so their subsequent events are no longer recorded; writes
        /// a Leave event at the current tick.</summary>
        public bool RemovePlayer(Player player)
        {
            if (player is null)
                return false;

            if (!_players.Remove(player))
                return false;

            if (IsActive && !RecorderQueue.IsAddingCompleted)
            {
                ReplayEventEncoder.EncodeLeave(this, player);
            }

            return true;
        }

        public bool ContainsPlayer(Player player) => player is not null && _players.Contains(player);
    }

    #endregion
}
