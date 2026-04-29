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
    /// they show up in the focused-spectator set too.
    /// </summary>
    public void Broadcast(
        Guid matchId,
        string? arenaName,
        IReadOnlyCollection<Player> participants,
        string message)
    {
        ArgumentNullException.ThrowIfNull(participants);
        ArgumentNullException.ThrowIfNull(message);

        var sent = new HashSet<Player>(participants.Count);
        foreach (var p in participants)
        {
            if (p is null) continue;
            if (sent.Add(p)) _chat.SendMessage(p, message);
        }

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
                        _chat.SendMessage(p, message);
                    }
                }
            }
            finally { _playerData.Unlock(); }
        }
        finally { _broker.ReleaseInterface(ref focus); }
    }
}
