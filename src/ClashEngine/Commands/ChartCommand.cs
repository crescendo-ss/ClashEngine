using System;
using System.Collections.Generic;
using ClashEngine.Adapter;
using ClashEngine.Core;
using ClashEngine.Core.Identity;
using ClashEngine.Core.Matches;
using ClashEngine.Core.Ratings;
using ClashEngine.Core.Stats;
using SS.Core;
using SS.Core.ComponentInterfaces;

namespace ClashEngine.Commands;

/// <summary>
/// Implements the <c>?chart</c> command: prints the same scoreboard that's broadcast at match
/// end, but for the in-progress match the requester is associated with. See
/// <see cref="ActiveMatchLookup"/> for the participant-vs-spectator resolution rules.
/// </summary>
public sealed class ChartCommand
{
    private readonly MatchmakingEngine _engine;
    private readonly ActiveMatchLookup _lookup;
    private readonly ICommandManager _commands;
    private readonly IChat _chat;

    public ChartCommand(
        MatchmakingEngine engine,
        ActiveMatchLookup lookup,
        ICommandManager commands,
        IChat chat)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _lookup = lookup ?? throw new ArgumentNullException(nameof(lookup));
        _commands = commands ?? throw new ArgumentNullException(nameof(commands));
        _chat = chat ?? throw new ArgumentNullException(nameof(chat));
    }

    public void RegisterArena(Arena arena)
    {
        _commands.AddCommand("chart", Run, arena, helpText:
            "?chart -- Show the live scoreboard for your match (or the match you're spectating in this arena).");
    }

    public void UnregisterArena(Arena arena)
    {
        _commands.RemoveCommand("chart", Run, arena);
    }

    private void Run(ReadOnlySpan<char> name, ReadOnlySpan<char> parameters, Player player, ITarget target)
    {
        if (_lookup.TryFind(player, out var err) is not { } found)
        {
            _chat.SendMessage(player, err);
            return;
        }

        var match = found.Match;
        var recorder = found.Recorder;

        // Snapshot every participant's rating, defaulting new players to Rating.Default so the
        // scoreboard's +/- column has a pre value for every row (otherwise only veterans would
        // show a delta and unrated players would render "-", which reads as a bug).
        var ratingsAtStart = new Dictionary<PlayerKey, RatingPayload>();
        var liveOrdinalByName = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        for (int t = 0; t < match.Teams.Count; t++)
        {
            for (int j = 0; j < match.Teams[t].Count; j++)
            {
                var key = match.Teams[t][j];
                var r = _engine.Ratings.TryGet(key, match.GameType, out var existing)
                    ? existing
                    : Rating.Default;
                ratingsAtStart[key] = new RatingPayload(r.Mu, r.Sigma, r.GamesPlayed);
                liveOrdinalByName[key.Name] = r.Ordinal;
            }
        }

        var (qUniqueId, qLabel) = QueueIdentForMatch(match);
        var payload = LiveScoreboardBuilder.Build(
            matchId: match.MatchId,
            queueName: qUniqueId,
            queueLabel: qLabel,
            gameType: match.GameType.Value,
            arena: player.Arena?.Name,
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
    /// Best-effort queue identity lookup for a live match. ActiveMatch doesn't carry the queue
    /// name directly; we match by GameType. Under the per-arena model multiple queues can
    /// share a GameType (many-to-one), so the first registered queue with this GameType wins
    /// -- still fine for the title label, which is informational.
    /// </summary>
    private (string UniqueId, string Label) QueueIdentForMatch(ActiveMatch match)
    {
        foreach (var def in _engine.Queues.Definitions)
            if (def.GameType == match.GameType) return (def.UniqueId, def.Label);
        return (string.Empty, string.Empty);
    }
}
