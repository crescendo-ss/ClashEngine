using System;
using System.Text;
using ClashEngine.Adapter;
using ClashEngine.Core.Matches;
using ClashEngine.Core.Stats;
using SS.Core;
using SS.Core.ComponentInterfaces;

namespace ClashEngine.Commands;

public sealed class ItemsCommand
{
    private readonly ActiveMatchLookup _lookup;
    private readonly ICommandManager _commands;
    private readonly IChat _chat;

    public ItemsCommand(ActiveMatchLookup lookup, ICommandManager commands, IChat chat)
    {
        _lookup = lookup ?? throw new ArgumentNullException(nameof(lookup));
        _commands = commands ?? throw new ArgumentNullException(nameof(commands));
        _chat = chat ?? throw new ArgumentNullException(nameof(chat));
    }

    public void Register()
    {
        _commands.AddCommand("items", Run, helpText:
            "?items -- Show each still-alive player's Repels/Rockets, grouped by team.");
    }

    public void Unregister()
    {
        _commands.RemoveCommand("items", Run);
    }

    private void Run(ReadOnlySpan<char> name, ReadOnlySpan<char> parameters, Player player, ITarget target)
    {
        if (_lookup.TryFind(player, out var err) is not { } found)
        {
            _chat.SendMessage(player, err);
            return;
        }

        for (int t = 0; t < found.Match.Teams.Count; t++)
        {
            var line = BuildTeamLine(found.Match, found.Recorder, t);
            if (line is not null) _chat.SendMessage(player, line);
        }
    }

    private static string? BuildTeamLine(ActiveMatch match, StatsRecorder recorder, int teamIdx)
    {
        var roster = match.Teams[teamIdx];
        var sb = new StringBuilder();
        sb.Append("Team ").Append(teamIdx + 1).Append(':');
        bool any = false;

        for (int j = 0; j < roster.Count; j++)
        {
            var key = roster[j];
            if (match.IsKnockedOut(key)) continue;
            if (!recorder.Stats.TryGetValue(key, out var ps)) continue;
            ps.Inventory.TryGetValue(ItemKind.Repel, out var reps);
            ps.Inventory.TryGetValue(ItemKind.Rocket, out var rockets);
            sb.Append(any ? ", " : " ").Append(key.Name).Append(' ').Append(reps).Append('/').Append(rockets);
            any = true;
        }
        return any ? sb.ToString() : null;
    }
}
