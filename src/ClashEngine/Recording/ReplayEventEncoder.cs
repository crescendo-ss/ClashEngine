using System;
using System.Buffers;
using System.Runtime.InteropServices;
using SS.Core;
using SS.Core.ComponentCallbacks;
using SS.Core.ComponentInterfaces;
using SS.Packets.Game;
using SS.Replay.FileFormat;
using SS.Replay.FileFormat.Events;
using SS.Utilities;

namespace ClashEngine.Recording;

/// <summary>
/// Per-event byte serializer for the Afluxion .replay file format. Each method rents a buffer
/// from <see cref="Pool"/>, writes the event header + payload via <c>MemoryMarshal.AsRef</c>,
/// and enqueues a <see cref="MatchRecorder.RecordBuffer"/> on the session's worker queue.
/// </summary>
/// <remarks>
/// <para>The pool is shared across all sessions and reused by <see cref="MatchRecorder"/>'s
/// worker thread, which returns each buffer to the pool after writing it to disk.</para>
///
/// <para>Every method assumes the caller has already filtered for "session is active and the
/// player(s) belong to this session"; the encoder doesn't peek inside the session beyond
/// the queue itself. The <c>IsAddingCompleted</c> check is the caller's responsibility.</para>
/// </remarks>
internal static class ReplayEventEncoder
{
    internal static readonly ArrayPool<byte> Pool = ArrayPool<byte>.Create();

    public static void EncodeFreqChange(MatchRecorder.Session session, Player player, short newFreq)
    {
        byte[] buffer = Pool.Rent(FreqChange.Length);
        ref FreqChange freqChange = ref MemoryMarshal.AsRef<FreqChange>(buffer.AsSpan(0, FreqChange.Length));
        freqChange = new(ServerTick.Now, (short)player.Id, newFreq);
        session.RecorderQueue.Add(new MatchRecorder.RecordBuffer(buffer, FreqChange.Length));
    }

    public static void EncodeShipChange(MatchRecorder.Session session, Player player, ShipType newShip, short newFreq)
    {
        byte[] buffer = Pool.Rent(ShipChange.Length);
        ref ShipChange shipChange = ref MemoryMarshal.AsRef<ShipChange>(buffer.AsSpan(0, ShipChange.Length));
        shipChange = new(ServerTick.Now, (short)player.Id, newShip, newFreq);
        session.RecorderQueue.Add(new MatchRecorder.RecordBuffer(buffer, ShipChange.Length));
    }

    public static void EncodeKill(MatchRecorder.Session session, Player killer, Player killed, short points, short flagCount)
    {
        byte[] buffer = Pool.Rent(Kill.Length);
        ref Kill kill = ref MemoryMarshal.AsRef<Kill>(buffer.AsSpan(0, Kill.Length));
        kill = new(ServerTick.Now, (short)killer.Id, (short)killed.Id, points, flagCount);
        session.RecorderQueue.Add(new MatchRecorder.RecordBuffer(buffer, Kill.Length));
    }

    public static void EncodeChat(
        MatchRecorder.Session session,
        Player player,
        ChatMessageType type,
        ChatSound sound,
        ReadOnlySpan<char> message)
    {
        int messageByteCount = StringUtils.DefaultEncoding.GetByteCount(message) + 1;
        int totalLength = Chat.Length + messageByteCount;

        byte[] buffer = Pool.Rent(totalLength);
        ref Chat chat = ref MemoryMarshal.AsRef<Chat>(buffer.AsSpan(0, Chat.Length));
        chat = new(ServerTick.Now, (short)player.Id, type, sound, (ushort)messageByteCount);
        Span<byte> messageBytes = buffer.AsSpan(Chat.Length, messageByteCount);
        messageBytes.WriteNullTerminatedString(message);
        session.RecorderQueue.Add(new MatchRecorder.RecordBuffer(buffer, totalLength));
    }

    /// <summary>
    /// Encode a server-originated arena chat line (no source player) -- the countdown ticks,
    /// "GO!", scoreboard, etc. that ClashEngine broadcasts to a match's audience. Uses a -1
    /// player id (the format's "no player" sentinel, matching <see cref="EncodeAttach"/>); the
    /// replay module renders <see cref="ChatMessageType.Arena"/> lines without a sender anyway.
    /// </summary>
    public static void EncodeArenaChat(
        MatchRecorder.Session session,
        ChatSound sound,
        ReadOnlySpan<char> message)
    {
        int messageByteCount = StringUtils.DefaultEncoding.GetByteCount(message) + 1;
        int totalLength = Chat.Length + messageByteCount;

        byte[] buffer = Pool.Rent(totalLength);
        ref Chat chat = ref MemoryMarshal.AsRef<Chat>(buffer.AsSpan(0, Chat.Length));
        chat = new(ServerTick.Now, (short)-1, ChatMessageType.Arena, sound, (ushort)messageByteCount);
        Span<byte> messageBytes = buffer.AsSpan(Chat.Length, messageByteCount);
        messageBytes.WriteNullTerminatedString(message);
        session.RecorderQueue.Add(new MatchRecorder.RecordBuffer(buffer, totalLength));
    }

    /// <summary>
    /// Encode a single placed brick. The Afluxion format's <see cref="Brick"/> event carries one
    /// <see cref="BrickData"/>; callers fanning out a multi-brick placement call this once per
    /// brick. On playback the replay module re-drops the brick via its <c>IBrickManager</c>.
    /// </summary>
    public static void EncodeBrick(MatchRecorder.Session session, ref readonly BrickData brickData)
    {
        byte[] buffer = Pool.Rent(Brick.Length);
        ref Brick brick = ref MemoryMarshal.AsRef<Brick>(buffer.AsSpan(0, Brick.Length));
        brick = new(ServerTick.Now, in brickData);
        session.RecorderQueue.Add(new MatchRecorder.RecordBuffer(buffer, Brick.Length));
    }

    public static void EncodeCrownToggle(MatchRecorder.Session session, Player player, bool on)
    {
        byte[] buffer = Pool.Rent(CrownToggle.Length);
        ref CrownToggle crownToggle = ref MemoryMarshal.AsRef<CrownToggle>(buffer.AsSpan(0, CrownToggle.Length));
        crownToggle = new(ServerTick.Now, on, (short)player.Id);
        session.RecorderQueue.Add(new MatchRecorder.RecordBuffer(buffer, CrownToggle.Length));
    }

    public static void EncodeAttach(MatchRecorder.Session session, Player player, Player? to)
    {
        byte[] buffer = Pool.Rent(AttachChange.Length);
        ref AttachChange attachChange = ref MemoryMarshal.AsRef<AttachChange>(buffer.AsSpan(0, AttachChange.Length));
        attachChange = new(ServerTick.Now, (short)player.Id, to is null ? (short)-1 : (short)to.Id);
        session.RecorderQueue.Add(new MatchRecorder.RecordBuffer(buffer, AttachChange.Length));
    }

    public static void EncodeTurretKickoff(MatchRecorder.Session session, Player player)
    {
        byte[] buffer = Pool.Rent(TurretKickoff.Length);
        ref TurretKickoff turretKickoff = ref MemoryMarshal.AsRef<TurretKickoff>(buffer.AsSpan(0, TurretKickoff.Length));
        turretKickoff = new(ServerTick.Now, (short)player.Id);
        session.RecorderQueue.Add(new MatchRecorder.RecordBuffer(buffer, TurretKickoff.Length));
    }

    public static void EncodeSecuritySeedChange(
        MatchRecorder.Session session, uint greenSeed, uint doorSeed, ServerTick timestamp, uint deltaTicks)
    {
        byte[] buffer = Pool.Rent(SecuritySeedChange.Length);
        ref SecuritySeedChange seedChange = ref MemoryMarshal.AsRef<SecuritySeedChange>(buffer.AsSpan(0, SecuritySeedChange.Length));
        seedChange = new(timestamp, greenSeed, doorSeed, deltaTicks);
        session.RecorderQueue.Add(new MatchRecorder.RecordBuffer(buffer, SecuritySeedChange.Length));
    }

    public static void EncodePosition(MatchRecorder.Session session, Player player, ref readonly C2S_PositionPacket c2s)
    {
        byte[] buffer = Pool.Rent(PositionWrapper.Length);
        ref PositionWrapper position = ref MemoryMarshal.AsRef<PositionWrapper>(buffer.AsSpan(0, PositionWrapper.Length));
        position = new(ServerTick.Now, (short)player.Id, in c2s);
        session.RecorderQueue.Add(new MatchRecorder.RecordBuffer(buffer, PositionWrapper.Length));
    }

    public static void EncodePositionWithExtra(
        MatchRecorder.Session session,
        Player player,
        ref readonly C2S_PositionPacket c2s,
        ref readonly ExtraPositionData extra)
    {
        byte[] buffer = Pool.Rent(PositionWithExtraWrapper.Length);
        ref PositionWithExtraWrapper position = ref MemoryMarshal.AsRef<PositionWithExtraWrapper>(buffer.AsSpan(0, PositionWithExtraWrapper.Length));
        position = new(ServerTick.Now, (short)player.Id, in c2s, in extra);
        session.RecorderQueue.Add(new MatchRecorder.RecordBuffer(buffer, PositionWithExtraWrapper.Length));
    }

    public static void EncodeEnter(MatchRecorder.Session session, Player player)
    {
        byte[] buffer = Pool.Rent(Enter.Length);
        ref Enter enter = ref MemoryMarshal.AsRef<Enter>(buffer.AsSpan(0, Enter.Length));
        enter = new(ServerTick.Now, (short)player.Id, player.Name, player.Squad, player.Ship, player.Freq);
        session.RecorderQueue.Add(new MatchRecorder.RecordBuffer(buffer, Enter.Length));
    }

    public static void EncodeLeave(MatchRecorder.Session session, Player player)
    {
        byte[] buffer = Pool.Rent(Leave.Length);
        ref Leave leave = ref MemoryMarshal.AsRef<Leave>(buffer.AsSpan(0, Leave.Length));
        leave = new(ServerTick.Now, (short)player.Id);
        session.RecorderQueue.Add(new MatchRecorder.RecordBuffer(buffer, Leave.Length));
    }
}
