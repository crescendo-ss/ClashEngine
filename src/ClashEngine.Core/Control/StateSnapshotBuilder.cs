using System;
using System.Collections.Generic;
using ClashEngine.Core.Identity;
using ClashEngine.Core.Penalties;

namespace ClashEngine.Core.Control;

/// <summary>
/// Serializes the engine's live state into a <see cref="StateSnapshot"/> for the control
/// surface's <c>GET /state</c> endpoint. Pure read composed from the engine's public accessors;
/// like everything engine-facing it MUST run on the mainloop thread (the host bounces the request
/// onto it). Output ordering is deterministic (queues by name, matches by proposal time, penalty
/// rows by player) so snapshots diff cleanly in tests and logs.
/// </summary>
public static class StateSnapshotBuilder
{
    public static StateSnapshot Build(MatchmakingEngine engine, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(engine);

        var queues = new List<QueueStateSnapshot>();
        foreach (var def in engine.Queues.Definitions)
        {
            var entries = def.Queue.Snapshot();
            var members = new QueueMemberSnapshot[entries.Count];
            for (int i = 0; i < entries.Count; i++)
            {
                var e = entries[i];
                members[i] = new QueueMemberSnapshot(
                    e.Player.Name, e.EnqueuedAt, e.Group?.ToString());
            }
            queues.Add(new QueueStateSnapshot(
                def.UniqueId,
                def.Label,
                string.IsNullOrEmpty(def.GameType) ? null : def.GameType,
                def.Shape.TotalPlayers,
                def.Shape.TeamCount,
                def.Shape.PlayersPerTeam,
                def.IgnorePenalties,
                members));
        }
        queues.Sort(static (a, b) =>
            string.Compare(a.QueueName, b.QueueName, StringComparison.OrdinalIgnoreCase));

        var matches = new List<MatchStateSnapshot>();
        foreach (var (matchId, match) in engine.ActiveMatches)
        {
            var teams = new IReadOnlyList<string>[match.Teams.Count];
            for (int t = 0; t < match.Teams.Count; t++)
                teams[t] = Names(match.Teams[t]);
            matches.Add(new MatchStateSnapshot(
                matchId,
                match.GameType,
                engine.TryGetMatchQueue(matchId, out var queueDef) ? queueDef.UniqueId : null,
                match.State.ToString(),
                match.StartedAt,
                teams));
        }
        matches.Sort(static (a, b) => a.MatchId.CompareTo(b.MatchId));

        var penalties = new List<PenaltyStateSnapshot>();
        foreach (var status in engine.Penalties.ActiveStatuses(now))
        {
            // ActiveStatuses includes ladders that are armed but already served; the snapshot
            // only reports timeouts still in force.
            if (status.TimeoutEnd <= now) continue;
            penalties.Add(new PenaltyStateSnapshot(
                status.Player.Name,
                KindWire(status.Kind),
                status.OffenseCount,
                status.TimeoutEnd));
        }
        penalties.Sort(static (a, b) =>
        {
            int byPlayer = string.Compare(a.Player, b.Player, StringComparison.OrdinalIgnoreCase);
            return byPlayer != 0 ? byPlayer : string.Compare(a.Kind, b.Kind, StringComparison.Ordinal);
        });

        return new StateSnapshot(ControlSchema.Version, now, queues, matches, penalties);
    }

    private static IReadOnlyList<string> Names(IReadOnlyList<PlayerKey> players)
    {
        var arr = new string[players.Count];
        for (int i = 0; i < players.Count; i++) arr[i] = players[i].Name;
        return arr;
    }

    // Wire strings are decoupled from the C# enum identifiers so renaming the enum can't silently
    // change the contract (mirrors EventStreamTelemetry.ReasonWire).
    private static string KindWire(PenaltyKind kind) => kind switch
    {
        PenaltyKind.Abandonment => "abandonment",
        PenaltyKind.Griefing => "griefing",
        PenaltyKind.EliminationCooldown => "elimination_cooldown",
        PenaltyKind.StagingAfk => "staging_afk",
        _ => "abandonment",
    };
}
