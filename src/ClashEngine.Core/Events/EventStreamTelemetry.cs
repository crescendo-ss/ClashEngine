using System;
using System.Collections.Generic;
using ClashEngine.Core.Adapter;
using ClashEngine.Core.Identity;
using ClashEngine.Core.Matches;
using ClashEngine.Core.Matching;
using ClashEngine.Core.Queue;

namespace ClashEngine.Core.Events;

/// <summary>
/// Pure telemetry → event-stream mapper. Implements <see cref="IMatchmakingTelemetry"/> and
/// translates the queue-membership and match-lifecycle events the engine emits into normalized
/// <see cref="EventEnvelope"/>s handed to an <see cref="IEventSink"/>. Lives in Core (no
/// SubspaceServer dependency) so it can be unit-tested against a fake sink; the HTTP transport
/// lives in the plug-in.
/// </summary>
/// <remarks>
/// v1 maps exactly the queue-membership events (<see cref="OnQueueAdded"/>,
/// <see cref="OnQueueRemoved"/>, <see cref="OnQueueNearFull"/>, <see cref="OnQueueDwellWarning"/>)
/// and the match-lifecycle events (<see cref="OnMatchProposed"/>, <see cref="OnMatchStarted"/>,
/// <see cref="OnMatchEnded"/>). Every other telemetry event keeps the interface's default no-op
/// body (social / penalty / KOTH / team-collapse / hold events are intentionally not emitted in
/// v1).
/// </remarks>
public sealed class EventStreamTelemetry : IMatchmakingTelemetry
{
    private readonly IEventSink _sink;
    private readonly QueueRegistry _queues;
    private readonly IClock _clock;

    public EventStreamTelemetry(IEventSink sink, QueueRegistry queues, IClock clock)
    {
        _sink = sink ?? throw new ArgumentNullException(nameof(sink));
        _queues = queues ?? throw new ArgumentNullException(nameof(queues));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public void OnQueueAdded(PlayerKey player, string queueName, DateTimeOffset at, PlayerKey? initiator = null)
    {
        var (label, gameType, capacity, count) = ResolveQueue(queueName);
        Emit(ClashEventTypes.QueueJoined,
            new QueueEventPayload(queueName, label, gameType, count, capacity, Player: player.Name));
    }

    public void OnQueueRemoved(PlayerKey player, string queueName, DateTimeOffset at,
        QueueRemovalReason reason = QueueRemovalReason.Cancel)
    {
        var (label, gameType, capacity, count) = ResolveQueue(queueName);
        Emit(ClashEventTypes.QueueLeft,
            new QueueEventPayload(queueName, label, gameType, count, capacity,
                Player: player.Name, Reason: ReasonWire(reason)));
    }

    public void OnQueueNearFull(string queueName, IReadOnlyList<PlayerKey> waiting, int waitingCount, int needed)
    {
        var (label, gameType, _, _) = ResolveQueue(queueName);
        Emit(ClashEventTypes.QueueNearFull,
            new QueueEventPayload(queueName, label, gameType, waitingCount, needed, Waiting: Names(waiting)));
    }

    public void OnQueueDwellWarning(PlayerKey player, string queueName, DateTimeOffset at, TimeSpan dwell)
    {
        var (label, gameType, capacity, count) = ResolveQueue(queueName);
        Emit(ClashEventTypes.QueueDwellWarning,
            new QueueEventPayload(queueName, label, gameType, count, capacity,
                Player: player.Name, DwellSeconds: dwell.TotalSeconds));
    }

    public void OnDiscordLinkRequested(PlayerKey player, string discordAlias, DateTimeOffset at)
    {
        _sink.Emit(new EventEnvelope(EventSchema.Version, ClashEventTypes.PlayerDiscordLinkRequested,
            _clock.UtcNow, Player: new PlayerEventPayload(player.Name, discordAlias)));
    }

    public void OnMatchProposed(MatchProposal proposal)
    {
        string gameType = string.Empty;
        string? label = null;
        string? arena = null;
        if (_queues.TryGet(proposal.QueueName, out var def))
        {
            gameType = def.GameType;
            label = def.Label;
            arena = def.MatchArenaName;
        }
        // No match id exists yet at proposal time; consumers correlate teams_locked to started by
        // roster, and started to ended by match id.
        Emit(ClashEventTypes.MatchTeamsLocked, new MatchEventPayload(
            MatchId: null,
            GameType: gameType,
            QueueName: proposal.QueueName,
            QueueLabel: label,
            Arena: arena,
            Teams: BuildRosters(proposal.Teams)));
    }

    public void OnMatchStarted(ActiveMatch match)
    {
        Emit(ClashEventTypes.MatchStarted, new MatchEventPayload(
            MatchId: match.MatchId,
            GameType: match.GameType,
            Teams: BuildRosters(match.Teams),
            StartedAt: match.StartedAt));
    }

    public void OnMatchEnded(MatchOutcome outcome)
    {
        var teams = new EventRankedTeam[outcome.RankedTeams.Count];
        for (int i = 0; i < outcome.RankedTeams.Count; i++)
        {
            var rt = outcome.RankedTeams[i];
            teams[i] = new EventRankedTeam(rt.Rank, rt.Score, Names(rt.Players));
        }

        Emit(ClashEventTypes.MatchEnded, new MatchEventPayload(
            MatchId: outcome.MatchId,
            GameType: outcome.GameType,
            Teams: teams,
            FinalState: outcome.FinalState.ToString(),
            EndedAt: outcome.EndedAt,
            DurationSeconds: outcome.Duration?.TotalSeconds,
            AbandonedBy: Names(outcome.AbandonedBy)));
    }

    private void Emit(string type, QueueEventPayload queue) =>
        _sink.Emit(new EventEnvelope(EventSchema.Version, type, _clock.UtcNow, Queue: queue));

    private void Emit(string type, MatchEventPayload match) =>
        _sink.Emit(new EventEnvelope(EventSchema.Version, type, _clock.UtcNow, Match: match));

    /// <summary>Resolves a queue's display/shape fields from the registry. Falls back to the bare
    /// name and zeroed shape if the queue isn't registered (shouldn't happen for engine-sourced
    /// names, but keeps the mapper total).</summary>
    private (string Label, string? GameType, int Capacity, int Count) ResolveQueue(string queueName)
    {
        if (_queues.TryGet(queueName, out var def))
            return (def.Label, string.IsNullOrEmpty(def.GameType) ? null : def.GameType,
                def.Shape.TotalPlayers, def.Queue.Count);
        return (queueName, null, 0, 0);
    }

    private static IReadOnlyList<EventRankedTeam> BuildRosters(IReadOnlyList<IReadOnlyList<PlayerKey>> teams)
    {
        var arr = new EventRankedTeam[teams.Count];
        for (int t = 0; t < teams.Count; t++)
            arr[t] = new EventRankedTeam(t + 1, 0, Names(teams[t]));
        return arr;
    }

    private static IReadOnlyList<string> Names(IReadOnlyList<PlayerKey> players)
    {
        var arr = new string[players.Count];
        for (int i = 0; i < players.Count; i++) arr[i] = players[i].Name;
        return arr;
    }

    // Wire strings are decoupled from the C# enum identifiers so renaming the enum can't silently
    // change the contract.
    private static string ReasonWire(QueueRemovalReason reason) => reason switch
    {
        QueueRemovalReason.Cancel => "cancel",
        QueueRemovalReason.Disconnect => "disconnect",
        QueueRemovalReason.Matched => "matched",
        QueueRemovalReason.AfkCull => "afk_cull",
        QueueRemovalReason.GroupChange => "group_change",
        QueueRemovalReason.Reset => "reset",
        _ => "cancel",
    };
}
