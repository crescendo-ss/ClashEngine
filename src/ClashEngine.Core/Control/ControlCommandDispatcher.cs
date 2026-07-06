using System;
using System.Collections.Generic;
using ClashEngine.Core.Groups;
using ClashEngine.Core.Identity;

namespace ClashEngine.Core.Control;

/// <summary>
/// Pure command → engine mapper: the inbound mirror of
/// <see cref="ClashEngine.Core.Events.EventStreamTelemetry"/>. Validates one
/// <see cref="ControlCommand"/>, translates it into the corresponding
/// <see cref="MatchmakingEngine"/> call, and folds the engine's rich result back into a
/// <see cref="ControlResponse"/> wire envelope. Lives in Core (no SubspaceServer dependency) so
/// the whole command surface is unit-testable against a real engine; the HTTP transport and the
/// mainloop marshaling live in the plug-in.
/// </summary>
/// <remarks>
/// NOT thread-safe: <see cref="Execute"/> and <see cref="PlanRatingSeed"/> touch engine state and
/// MUST be invoked on the engine's mainloop thread (the host bounces each request onto it).
/// <see cref="Execute"/> never throws on malformed input -- bad commands come back as
/// <c>status=error</c> responses.
/// </remarks>
public sealed class ControlCommandDispatcher
{
    private readonly MatchmakingEngine _engine;

    public ControlCommandDispatcher(MatchmakingEngine engine)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
    }

    /// <summary>Executes one command against the engine at time <paramref name="at"/>.</summary>
    public ControlResponse Execute(ControlCommand command, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (command.SchemaVersion != ControlSchema.Version)
            return Error(command, ControlResults.UnsupportedSchemaVersion,
                $"This engine speaks schemaVersion {ControlSchema.Version}.");

        return command.Type switch
        {
            ControlCommandTypes.Enqueue => ExecuteEnqueue(command, at),
            ControlCommandTypes.EnqueueGroup => ExecuteEnqueueGroup(command, at),
            ControlCommandTypes.Dequeue => ExecuteDequeue(command, at),
            ControlCommandTypes.FormMatch => ExecuteFormMatch(command, at),
            ControlCommandTypes.CancelMatch => ExecuteCancelMatch(command, at),
            ControlCommandTypes.SetAuto => ExecuteSetAuto(command, at),
            _ => Error(command, ControlResults.UnknownType,
                $"Unknown command type '{command.Type}'."),
        };
    }

    /// <summary>
    /// Which (player, gameType) rating pulls the host should await before executing
    /// <paramref name="command"/> -- the control-surface equivalent of <c>?play</c>'s
    /// pull-then-enqueue flow, so the matcher and the rating updater see fresh ratings for
    /// everyone the command drags into matchmaking. Returns <see langword="null"/> when the
    /// command doesn't admit players into matchmaking, or when it is malformed / names an
    /// unknown queue (no seeding needed; <see cref="Execute"/> will produce the error itself).
    /// Expands an <c>enqueue</c> to the player's in-game party, mirroring what
    /// <see cref="ExecuteEnqueue"/> will do.
    /// </summary>
    public RatingSeedPlan? PlanRatingSeed(ControlCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (command.SchemaVersion != ControlSchema.Version) return null;
        if (string.IsNullOrWhiteSpace(command.Queue)) return null;
        if (!_engine.Queues.TryGet(command.Queue, out var def)) return null;
        if (string.IsNullOrEmpty(def.GameType)) return null;

        var players = new List<PlayerKey>();
        switch (command.Type)
        {
            case ControlCommandTypes.Enqueue:
                if (string.IsNullOrWhiteSpace(command.Player)) return null;
                var key = new PlayerKey(command.Player);
                if (_engine.Groups.GroupOf(key) is GroupId g)
                    foreach (var m in _engine.Groups.MembersOf(g)) players.Add(m);
                else
                    players.Add(key);
                break;

            case ControlCommandTypes.EnqueueGroup:
                if (command.Players is null) return null;
                foreach (var name in command.Players)
                {
                    if (string.IsNullOrWhiteSpace(name)) return null;
                    players.Add(new PlayerKey(name));
                }
                break;

            case ControlCommandTypes.FormMatch:
                if (command.Teams is null) return null;
                foreach (var team in command.Teams)
                {
                    if (team is null) return null;
                    foreach (var name in team)
                    {
                        if (string.IsNullOrWhiteSpace(name)) return null;
                        players.Add(new PlayerKey(name));
                    }
                }
                break;

            default:
                return null;
        }

        return players.Count > 0 ? new RatingSeedPlan(def.GameType, players) : null;
    }

    private ControlResponse ExecuteEnqueue(ControlCommand command, DateTimeOffset at)
    {
        if (string.IsNullOrWhiteSpace(command.Player))
            return Error(command, ControlResults.MissingField, "enqueue requires 'player'.");
        if (string.IsNullOrWhiteSpace(command.Queue))
            return Error(command, ControlResults.MissingField, "enqueue requires 'queue'.");

        // Mirror ?play: a player in an in-game party queues the whole party atomically, so the
        // two front-ends can't diverge on the "parties stay together" invariant.
        var key = new PlayerKey(command.Player);
        EnqueueResult result;
        string? offender = null;
        if (_engine.Groups.GroupOf(key) is GroupId g)
        {
            var members = new List<PlayerKey>();
            foreach (var m in _engine.Groups.MembersOf(g)) members.Add(m);
            result = _engine.TryEnqueueGroup(members, command.Queue, at, out _, existingGroup: g, initiator: key);
        }
        else
        {
            result = _engine.TryEnqueue(key, command.Queue, at);
            offender = command.Player;
        }
        return MapEnqueueResult(command, result, offender);
    }

    private ControlResponse ExecuteEnqueueGroup(ControlCommand command, DateTimeOffset at)
    {
        if (command.Players is null || command.Players.Count == 0)
            return Error(command, ControlResults.MissingField, "enqueue_group requires 'players'.");
        if (string.IsNullOrWhiteSpace(command.Queue))
            return Error(command, ControlResults.MissingField, "enqueue_group requires 'queue'.");

        var members = new List<PlayerKey>(command.Players.Count);
        foreach (var name in command.Players)
        {
            if (string.IsNullOrWhiteSpace(name))
                return Error(command, ControlResults.InvalidField, "'players' contains a blank name.");
            members.Add(new PlayerKey(name));
        }

        var result = _engine.TryEnqueueGroup(members, command.Queue, at, out _);
        return MapEnqueueResult(command, result, offender: null);
    }

    private ControlResponse ExecuteDequeue(ControlCommand command, DateTimeOffset at)
    {
        if (string.IsNullOrWhiteSpace(command.Player))
            return Error(command, ControlResults.MissingField, "dequeue requires 'player'.");

        var key = new PlayerKey(command.Player);
        IReadOnlyList<string> removed;
        if (string.IsNullOrWhiteSpace(command.Queue))
        {
            removed = _engine.DequeueEverywhere(key, at);
        }
        else
        {
            if (!_engine.Queues.Contains(command.Queue))
                return Rejected(command, ControlResults.UnknownQueue,
                    $"Queue '{command.Queue}' is not registered.");
            removed = _engine.Dequeue(key, command.Queue, at)
                ? new[] { command.Queue }
                : Array.Empty<string>();
        }

        // Idempotent by design: "not queued" already IS the requested state, so an empty
        // removal list still reports ok.
        return new ControlResponse(ControlSchema.Version, command.CommandId, ControlStatus.Ok,
            ControlResults.Dequeued, RemovedQueues: removed);
    }

    private ControlResponse ExecuteFormMatch(ControlCommand command, DateTimeOffset at)
    {
        if (string.IsNullOrWhiteSpace(command.Queue))
            return Error(command, ControlResults.MissingField, "form_match requires 'queue'.");
        if (command.Teams is null || command.Teams.Count == 0)
            return Error(command, ControlResults.MissingField, "form_match requires 'teams'.");

        var teams = new IReadOnlyList<PlayerKey>[command.Teams.Count];
        for (int t = 0; t < command.Teams.Count; t++)
        {
            var team = command.Teams[t];
            if (team is null || team.Count == 0)
                return Error(command, ControlResults.InvalidField, $"'teams[{t}]' is empty.");
            var roster = new PlayerKey[team.Count];
            for (int j = 0; j < team.Count; j++)
            {
                if (string.IsNullOrWhiteSpace(team[j]))
                    return Error(command, ControlResults.InvalidField, $"'teams[{t}]' contains a blank name.");
                roster[j] = new PlayerKey(team[j]);
            }
            teams[t] = roster;
        }

        var outcome = _engine.TryFormMatch(command.Queue, teams, at);
        return outcome.Result switch
        {
            FormMatchResult.Ok => new ControlResponse(
                ControlSchema.Version, command.CommandId, ControlStatus.Ok,
                ControlResults.MatchFormed, MatchId: outcome.MatchId),
            FormMatchResult.UnknownQueue => Rejected(command, ControlResults.UnknownQueue,
                $"Queue '{command.Queue}' is not registered."),
            FormMatchResult.ShapeMismatch => Rejected(command, ControlResults.ShapeMismatch,
                "Roster does not match the anchor queue's shape."),
            FormMatchResult.DuplicatePlayer => Rejected(command, ControlResults.DuplicatePlayer,
                $"'{outcome.Player.Name}' appears more than once.", outcome.Player.Name),
            FormMatchResult.NotConnected => Rejected(command, ControlResults.NotConnected,
                $"'{outcome.Player.Name}' is not connected.", outcome.Player.Name),
            FormMatchResult.InMatch => Rejected(command, ControlResults.InMatch,
                $"'{outcome.Player.Name}' is already in a match.", outcome.Player.Name),
            FormMatchResult.InTimeout => Rejected(command, ControlResults.InTimeout,
                $"'{outcome.Player.Name}' is serving a queue timeout.", outcome.Player.Name),
            _ => Error(command, ControlResults.InternalError, $"Unhandled result {outcome.Result}."),
        };
    }

    private ControlResponse ExecuteCancelMatch(ControlCommand command, DateTimeOffset at)
    {
        if (command.MatchId is not Guid matchId)
            return Error(command, ControlResults.MissingField, "cancel_match requires 'matchId'.");

        if (!_engine.ActiveMatches.ContainsKey(matchId))
            return Rejected(command, ControlResults.UnknownMatch, "No in-flight match has that id.");
        // Blameless: a service cancel is the organizer voiding the lobby, not players failing a
        // readiness check -- nobody gets an abandonment flag or penalty.
        if (!_engine.CancelForming(matchId, at, blameless: true))
            return Rejected(command, ControlResults.NotForming,
                "Match is already live; only Forming matches can be cancelled.");

        return new ControlResponse(ControlSchema.Version, command.CommandId, ControlStatus.Ok,
            ControlResults.MatchCancelled, MatchId: matchId);
    }

    private ControlResponse ExecuteSetAuto(ControlCommand command, DateTimeOffset at)
    {
        if (string.IsNullOrWhiteSpace(command.Player))
            return Error(command, ControlResults.MissingField, "set_auto requires 'player'.");
        if (command.Enabled is not bool enabled)
            return Error(command, ControlResults.MissingField, "set_auto requires 'enabled'.");

        _engine.AutoQueue.Set(new PlayerKey(command.Player), enabled);
        return new ControlResponse(ControlSchema.Version, command.CommandId, ControlStatus.Ok,
            ControlResults.AutoSet);
    }

    /// <summary>Folds an <see cref="EnqueueResult"/> into the wire envelope. AlreadyQueued(+Refreshed)
    /// report ok -- the requested state (queued) already holds. <paramref name="offender"/> is set
    /// only when the caller knows which single player the rejection is about (solo enqueue); group
    /// results don't identify the blocking member.</summary>
    private ControlResponse MapEnqueueResult(ControlCommand command, EnqueueResult result, string? offender)
    {
        return result switch
        {
            EnqueueResult.Ok => Ok(command, ControlResults.Queued),
            EnqueueResult.AlreadyQueued => Ok(command, ControlResults.AlreadyQueued),
            EnqueueResult.AlreadyQueuedRefreshed => Ok(command, ControlResults.AlreadyQueuedRefreshed),
            EnqueueResult.UnknownQueue => Rejected(command, ControlResults.UnknownQueue,
                $"Queue '{command.Queue}' is not registered."),
            EnqueueResult.NotConnected => Rejected(command, ControlResults.NotConnected,
                "A player is not connected to the zone.", offender),
            EnqueueResult.InMatch => Rejected(command, ControlResults.InMatch,
                "A player is already in a match.", offender),
            EnqueueResult.InTimeout => Rejected(command, ControlResults.InTimeout,
                "A player is serving a queue timeout.", offender),
            EnqueueResult.GroupTooLarge => Rejected(command, ControlResults.GroupTooLarge,
                "The group has more members than the queue's players-per-team."),
            _ => Error(command, ControlResults.InternalError, $"Unhandled result {result}."),
        };
    }

    private static ControlResponse Ok(ControlCommand command, string result) =>
        new(ControlSchema.Version, command.CommandId, ControlStatus.Ok, result);

    private static ControlResponse Rejected(
        ControlCommand command, string result, string detail, string? player = null) =>
        new(ControlSchema.Version, command.CommandId, ControlStatus.Rejected, result, detail, player);

    private static ControlResponse Error(ControlCommand command, string result, string detail) =>
        new(ControlSchema.Version, command.CommandId, ControlStatus.Error, result, detail);
}

/// <summary>The rating pulls to await before executing a command: every listed player's rating
/// for <see cref="GameType"/> should be seeded (pull-if-absent) so the enqueue / match formation
/// runs against fresh values. Produced by
/// <see cref="ControlCommandDispatcher.PlanRatingSeed"/>.</summary>
public sealed record RatingSeedPlan(string GameType, IReadOnlyList<PlayerKey> Players);
