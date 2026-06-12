using System.Collections.Generic;
using SS.Core;
using SS.Core.ComponentInterfaces;

namespace ClashEngine.Adapter;

/// <summary>
/// Senderless RemotePrivate chat delivery, mirroring SS.Matchmaking.TeamVersusMatch's match-found
/// notice (<c>TeamVersusMatch.cs:4842</c>): <see cref="IChat.SendAnyMessage"/> with
/// <see cref="ChatMessageType.RemotePrivate"/>, no <c>from</c>, and <see cref="ChatSound.None"/>.
/// Continuum plays its standard incoming-private sound (the "beep") on receipt and flashes the
/// taskbar for a minimized Windows client; with no sender it lands as a bare line rather than a
/// "(theirname)>message" self-echo, which clients mute as an outbound echo. Use this -- not
/// <see cref="IChat.SendMessage"/>, which is silent -- for notices that must reach a player who
/// may be tabbed out (match found, inactivity warnings).
/// </summary>
public static class ChatDm
{
    public static void Send(IChat chat, Player recipient, string message)
    {
        var set = new HashSet<Player> { recipient };
        chat.SendAnyMessage(set, ChatMessageType.RemotePrivate, ChatSound.None, null, message);
    }
}
