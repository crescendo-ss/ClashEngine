using System.Collections.Generic;
using ClashEngine.Core.Adapter;
using ClashEngine.Core.Identity;
using ClashEngine.Core.Matches;
using ClashEngine.Core.Matching;
using ClashEngine.Core.Penalties;

namespace ClashEngine.Core.Tests.Fakes;

public sealed class RecordingTelemetry : IMatchmakingTelemetry
{
    public List<(PlayerKey, string)> QueueAdds { get; } = new();
    public List<(PlayerKey, string)> QueueRemovals { get; } = new();
    public List<MatchProposal> Proposed { get; } = new();
    public List<ActiveMatch> Started { get; } = new();
    public List<MatchOutcome> Ended { get; } = new();
    public List<(PlayerKey Player, int OffenseCount, DateTimeOffset Until)> Abandonments { get; } = new();
    public List<(Guid MatchId, int TeamIdx, DateTimeOffset Since, DateTimeOffset ForfeitAt)> TeamsCollapsing { get; } = new();
    public List<(Guid MatchId, int TeamIdx)> TeamsRecovered { get; } = new();
    public List<PendingGriefingPenalty> GriefingFlagged { get; } = new();
    public List<(PendingGriefingPenalty Pending, PlayerKey Voter)> VetoesRecorded { get; } = new();
    public List<PendingGriefingPenalty> GriefingVetoed { get; } = new();
    public List<PendingGriefingPenalty> GriefingConfirmed { get; } = new();

    public void OnQueueAdded(PlayerKey player, string queueName, DateTimeOffset at) =>
        QueueAdds.Add((player, queueName));
    public void OnQueueRemoved(PlayerKey player, string queueName, DateTimeOffset at) =>
        QueueRemovals.Add((player, queueName));
    public void OnMatchProposed(MatchProposal proposal) => Proposed.Add(proposal);
    public void OnMatchStarted(ActiveMatch match) => Started.Add(match);
    public void OnMatchEnded(MatchOutcome outcome) => Ended.Add(outcome);
    public void OnAbandonment(PlayerKey player, int offenseCount, DateTimeOffset timeoutUntil) =>
        Abandonments.Add((player, offenseCount, timeoutUntil));
    public void OnTeamCollapsing(ActiveMatch m, int teamIdx, DateTimeOffset since, DateTimeOffset forfeitAt) =>
        TeamsCollapsing.Add((m.MatchId, teamIdx, since, forfeitAt));
    public void OnTeamRecovered(ActiveMatch m, int teamIdx) => TeamsRecovered.Add((m.MatchId, teamIdx));
    public void OnGriefingFlagged(PendingGriefingPenalty pending) => GriefingFlagged.Add(pending);
    public void OnVetoRecorded(PendingGriefingPenalty pending, PlayerKey voter) =>
        VetoesRecorded.Add((pending, voter));
    public void OnGriefingVetoed(PendingGriefingPenalty pending) => GriefingVetoed.Add(pending);
    public void OnGriefingConfirmed(PendingGriefingPenalty pending) => GriefingConfirmed.Add(pending);
}
