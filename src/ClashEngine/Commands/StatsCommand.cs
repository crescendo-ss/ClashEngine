using System;
using System.Collections.Generic;
using ClashEngine.Adapter;
using ClashEngine.Core;
using ClashEngine.Core.Identity;
using ClashEngine.Core.Matches;
using ClashEngine.Core.Stats;
using ClashEngine.Lvz;
using SS.Core;
using SS.Core.ComponentInterfaces;
using SS.Matchmaking.Interfaces;
using SS.Packets.Game;

namespace ClashEngine.Commands;

/// <summary>
/// Implements the <c>?stats</c> command: prints the same scoreboard that's broadcast at match
/// end, but for the in-progress match the requester is associated with. Resolution order:
/// (1) requester is participating -> their match; (2) requester is spectating in the match
/// arena -> that match; (3) no active match -> short "no match" message.
/// </summary>
public sealed class StatsCommand
{
    private readonly IComponentBroker _broker;
    private readonly MatchmakingEngine _engine;
    private readonly MatchStatsRegistry _registry;
    private readonly ICommandManager _commands;
    private readonly IChat _chat;
    private readonly PlayerKeyResolver _resolver;

    public StatsCommand(
        IComponentBroker broker,
        MatchmakingEngine engine,
        MatchStatsRegistry registry,
        ICommandManager commands,
        IChat chat,
        PlayerKeyResolver resolver)
    {
        _broker = broker ?? throw new ArgumentNullException(nameof(broker));
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _commands = commands ?? throw new ArgumentNullException(nameof(commands));
        _chat = chat ?? throw new ArgumentNullException(nameof(chat));
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
    }

    public void Register()
    {
        _commands.AddCommand("stats", Run, helpText:
            "?stats -- Show the live scoreboard for your match (or the match you're spectating in this arena).");
    }

    public void Unregister()
    {
        _commands.RemoveCommand("stats", Run);
    }

    private void Run(ReadOnlySpan<char> name, ReadOnlySpan<char> parameters, Player player, ITarget target)
    {
        if (_resolver.KeyOf(player) is not PlayerKey selfKey)
        {
            _chat.SendMessage(player, "Could not resolve your player key.");
            return;
        }

        // (1) Participating in a match.
        Guid? matchId = _registry.MatchIdOf(selfKey);

        // (2) Spectator: ask SS.Matchmaking.IMatchFocus which match this player is focusing on
        //     (covers spec'ing a participant or watching the arena). Falls back to an arena-scan
        //     if the IMatchFocus module isn't loaded.
        if (matchId is null && player.Ship == ShipType.Spec)
        {
            matchId = ResolveSpectatedMatchId(player) ?? FindMatchInArena(player.Arena);
        }

        if (matchId is null
            || !_engine.ActiveMatches.TryGetValue(matchId.Value, out var match)
            || !_registry.ActiveRecorders.TryGetValue(matchId.Value, out var recorder))
        {
            _chat.SendMessage(player, "No active match to show.");
            return;
        }

        var ratingsAtStart = new Dictionary<PlayerKey, RatingPayload>();
        var liveOrdinalByName = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        for (int t = 0; t < match.Teams.Count; t++)
        {
            for (int j = 0; j < match.Teams[t].Count; j++)
            {
                var key = match.Teams[t][j];
                if (_engine.Ratings.TryGet(key, match.GameType, out var r))
                {
                    ratingsAtStart[key] = new RatingPayload(r.Mu, r.Sigma, r.GamesPlayed);
                    liveOrdinalByName[key.Name] = r.Ordinal;
                }
            }
        }

        var queueName = QueueNameFor(match);
        var arena = player.Arena?.Name;

        var payload = LiveScoreboardBuilder.Build(
            matchId: match.MatchId,
            queueName: queueName,
            gameType: match.GameType.Value,
            arena: arena,
            teams: match.Teams,
            startedAt: match.StartedAt,
            recorder: recorder,
            match: match,
            now: DateTimeOffset.UtcNow,
            ratingsAtStart: ratingsAtStart);

        var lines = ScoreboardFormatter.Format(payload, liveOrdinalByName);
        for (int i = 0; i < lines.Count; i++)
            _chat.SendMessage(player, lines[i]);
    }

    /// <summary>
    /// Asks <see cref="IMatchFocus"/> which match the player is focusing on. Returns the matchId
    /// if it's a ClashEngine match (i.e. the IMatch is one of our <see cref="ClashMatchData"/>
    /// adapters). Returns null if IMatchFocus isn't loaded, the player isn't focused on
    /// anything, or they're focused on a non-ClashEngine match.
    /// </summary>
    private Guid? ResolveSpectatedMatchId(Player player)
    {
        var focus = _broker.GetInterface<IMatchFocus>();
        if (focus is null) return null;
        try
        {
            var match = focus.GetFocusedMatch(player);
            if (match is ClashMatchData cmd) return cmd.Match.MatchId;
            return null;
        }
        finally
        {
            _broker.ReleaseInterface(ref focus);
        }
    }

    /// <summary>
    /// Fallback path used when <see cref="IMatchFocus"/> isn't loaded. Looks up an active match
    /// whose roster has at least one resolvable player currently in <paramref name="arena"/>.
    /// First match wins; works for the standard one-match-per-arena deployment.
    /// </summary>
    private Guid? FindMatchInArena(Arena? arena)
    {
        if (arena is null) return null;
        foreach (var (id, match) in _engine.ActiveMatches)
        {
            for (int t = 0; t < match.Teams.Count; t++)
            {
                for (int j = 0; j < match.Teams[t].Count; j++)
                {
                    var p = _resolver.Resolve(match.Teams[t][j]);
                    if (p?.Arena is { } a && string.Equals(a.Name, arena.Name, StringComparison.OrdinalIgnoreCase))
                        return id;
                }
            }
        }
        return null;
    }

    /// <summary>
    /// Best-effort queue name lookup. ActiveMatch doesn't carry the queue name directly; we
    /// match by GameType, which is unique per queue in current configurations. If multiple
    /// queues share a GameType the first registered queue wins -- still fine for the title
    /// label, which is informational.
    /// </summary>
    private string QueueNameFor(ActiveMatch match)
    {
        foreach (var def in _engine.Queues.Definitions)
            if (def.GameType == match.GameType) return def.Name;
        return string.Empty;
    }
}
