using System;
using System.Collections.Generic;
using ClashEngine.Lvz;
using SS.Core;
using SS.Core.ComponentInterfaces;
using SS.Matchmaking.Interfaces;

namespace ClashEngine.Adapter;

/// <summary>
/// Resolves the recipient set for a match-related chat broadcast: participants resolvable to live
/// <see cref="Player"/>s plus any spectators in the match arena who have the match in focus
/// according to <see cref="IMatchFocus"/>. Used by <c>MatchOrchestrator</c> and the orchestrator
/// registry so the staging/countdown/cleanup/team-collapse lines reach watchers as well as
/// players.
/// </summary>
/// <remarks>
/// The <see cref="IMatchFocus"/> module is an optional dependency. When it isn't loaded the
/// helper degrades to participants-only -- spectators won't be included, but the in-match players
/// still receive every message. Participants are de-duplicated against the focused-spectator set
/// so a player who is somehow both rostered and watching doesn't get a message twice.
/// </remarks>
public sealed class MatchAudience
{
    private readonly IComponentBroker _broker;
    private readonly IPlayerData _playerData;
    private readonly IArenaManager _arenaManager;
    private readonly IChat _chat;

    // Late-bound: the replay recorder is constructed after this helper (and only when replay
    // recording is enabled), so it's wired via SetChatRecorder rather than the ctor. Null when
    // recording is disabled or in test paths -- recording then silently no-ops.
    private IMatchChatRecorder? _chatRecorder;

    public MatchAudience(IComponentBroker broker, IPlayerData playerData, IArenaManager arenaManager, IChat chat)
    {
        _broker = broker ?? throw new ArgumentNullException(nameof(broker));
        _playerData = playerData ?? throw new ArgumentNullException(nameof(playerData));
        _arenaManager = arenaManager ?? throw new ArgumentNullException(nameof(arenaManager));
        _chat = chat ?? throw new ArgumentNullException(nameof(chat));
    }

    /// <summary>
    /// Send <paramref name="message"/> to every participant in <paramref name="participants"/>
    /// and to every spectator in <paramref name="arenaName"/> currently focused on the match
    /// identified by <paramref name="matchId"/>. Participants are messaged exactly once even if
    /// they show up in the focused-spectator set too. Pass <paramref name="sound"/> to attach
    /// a chat sound (e.g. <see cref="ChatSound.Ding"/> for the GO! announcement).
    /// </summary>
    public void Broadcast(
        Guid matchId,
        string? arenaName,
        IReadOnlyCollection<Player> participants,
        string message,
        ChatSound sound = ChatSound.None)
    {
        ArgumentNullException.ThrowIfNull(participants);
        ArgumentNullException.ThrowIfNull(message);

        var sent = new HashSet<Player>(participants.Count);
        foreach (var p in participants)
        {
            if (p is null) continue;
            if (sent.Add(p)) Send(p, message, sound);
        }

        // Record this as a single arena chat event in the match's replay. Driven here (not in
        // the recorder's chat callback) so it's exactly one event per broadcast and scoped to
        // matchId -- server-sent arena messages have no source player to scope by otherwise.
        _chatRecorder?.RecordArenaMessage(matchId, message, sound);

        if (string.IsNullOrEmpty(arenaName)) return;
        var arena = _arenaManager.FindArena(arenaName);
        if (arena is null) return;

        var focus = _broker.GetInterface<IMatchFocus>();
        if (focus is null) return;
        try
        {
            _playerData.Lock();
            try
            {
                foreach (Player p in _playerData.Players)
                {
                    if (p.Arena != arena) continue;
                    if (sent.Contains(p)) continue;
                    var focused = focus.GetFocusedMatch(p);
                    if (focused is ClashMatchData cmd && cmd.Match.MatchId == matchId
                        && sent.Add(p))
                    {
                        Send(p, message, sound);
                    }
                }
            }
            finally { _playerData.Unlock(); }
        }
        finally { _broker.ReleaseInterface(ref focus); }
    }

    /// <summary>Wire the replay-recorder sink. Called once by <c>ClashModule</c> after the
    /// recorder is constructed (recording enabled). Passing the same instance again is harmless.</summary>
    public void SetChatRecorder(IMatchChatRecorder? recorder) => _chatRecorder = recorder;

    /// <summary>
    /// Record an arena chat line that ClashEngine delivered to a match's participants outside
    /// the <see cref="Broadcast"/> path (the per-player ship-lock notice). Recording-only -- it
    /// does not send anything; the caller has already delivered the message.
    /// </summary>
    public void RecordArenaLine(Guid matchId, ReadOnlySpan<char> message, ChatSound sound = ChatSound.None)
        => _chatRecorder?.RecordArenaMessage(matchId, message, sound);

    private void Send(Player p, string message, ChatSound sound)
    {
        if (sound == ChatSound.None) _chat.SendMessage(p, message);
        else _chat.SendMessage(p, sound, message);
    }
}
