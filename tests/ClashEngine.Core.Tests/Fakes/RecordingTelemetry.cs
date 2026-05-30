using System.Collections.Generic;
using ClashEngine.Core.Adapter;
using ClashEngine.Core.Identity;
using ClashEngine.Core.Matches;
using ClashEngine.Core.Matching;
using ClashEngine.Core.Penalties;

namespace ClashEngine.Core.Tests.Fakes;

public sealed class RecordingTelemetry : IMatchmakingTelemetry
{
    public List<(PlayerKey Player, string Queue, PlayerKey? Initiator)> QueueAdds { get; } = new();
    public List<(PlayerKey Player, string Queue, QueueRemovalReason Reason)> QueueRemovals { get; } = new();
    public List<(PlayerKey Player, string Queue, TimeSpan Dwell)> DwellWarnings { get; } = new();
    public List<(PlayerKey Player, string DiscordAlias)> DiscordLinkRequests { get; } = new();
    public List<(PlayerKey Player, string Queue, int DefensesUsed, int MaxDefenses, bool SentToBack)> WinnerPromotions { get; } = new();
    public List<(PlayerKey Player, string Queue)> AutoQueued { get; } = new();
    public List<PlayerKey> AutoQueueDisabledByAfk { get; } = new();
    public List<MatchProposal> Proposed { get; } = new();
    public List<ActiveMatch> Started { get; } = new();
    public List<MatchOutcome> Ended { get; } = new();
    public List<(PlayerKey Player, int OffenseCount, DateTimeOffset Until)> Abandonments { get; } = new();
    public List<(IReadOnlyCollection<PlayerKey> Survivors, PlayerKey Abandoner, Guid MatchId, DateTimeOffset At)> TeammateAbandoned { get; } = new();
    public List<(PlayerKey Player, Guid MatchId, DateTimeOffset At)> PlayerReleases { get; } = new();
    public List<(Guid MatchId, int TeamIdx, DateTimeOffset Since, DateTimeOffset ForfeitAt)> TeamsCollapsing { get; } = new();
    public List<(Guid MatchId, int TeamIdx)> TeamsRecovered { get; } = new();
    public List<PendingGriefingPenalty> GriefingFlagged { get; } = new();
    public List<(PendingGriefingPenalty Pending, PlayerKey Voter)> VetoesRecorded { get; } = new();
    public List<PendingGriefingPenalty> GriefingVetoed { get; } = new();
    public List<PendingGriefingPenalty> GriefingConfirmed { get; } = new();
    public List<(string Queue, IReadOnlyList<PlayerKey> Waiting, int WaitingCount, int Needed)> QueueNearFulls { get; } = new();
    public List<(string Queue, IReadOnlyList<PlayerKey> Candidates, double Quality, TimeSpan HoldWindow)> HoldStarted { get; } = new();
    public List<(string Queue, IReadOnlyList<PlayerKey> Candidates, double OldQ, double NewQ)> HoldImproved { get; } = new();

    public void OnQueueAdded(PlayerKey player, string queueName, DateTimeOffset at, PlayerKey? initiator = null) =>
        QueueAdds.Add((player, queueName, initiator));
    public void OnQueueRemoved(PlayerKey player, string queueName, DateTimeOffset at,
        QueueRemovalReason reason = QueueRemovalReason.Cancel) =>
        QueueRemovals.Add((player, queueName, reason));
    public void OnQueueDwellWarning(PlayerKey player, string queueName, DateTimeOffset at, TimeSpan dwell) =>
        DwellWarnings.Add((player, queueName, dwell));
    public void OnDiscordLinkRequested(PlayerKey player, string discordAlias, DateTimeOffset at) =>
        DiscordLinkRequests.Add((player, discordAlias));
    public void OnWinnerPromoted(PlayerKey player, string queueName, DateTimeOffset at,
        int defensesUsed, int maxDefenses, bool sentToBack) =>
        WinnerPromotions.Add((player, queueName, defensesUsed, maxDefenses, sentToBack));
    public void OnAutoQueued(PlayerKey player, string queueName, DateTimeOffset at) =>
        AutoQueued.Add((player, queueName));
    public void OnAutoQueueDisabledByAfk(PlayerKey player, DateTimeOffset at) =>
        AutoQueueDisabledByAfk.Add(player);
    public void OnQueueNearFull(string queueName, IReadOnlyList<PlayerKey> waiting, int waitingCount, int needed) =>
        QueueNearFulls.Add((queueName, waiting, waitingCount, needed));
    public void OnQueueHoldStarted(string queueName, IReadOnlyList<PlayerKey> candidates, double currentQuality, TimeSpan holdWindow) =>
        HoldStarted.Add((queueName, candidates, currentQuality, holdWindow));
    public void OnQueueHoldImproved(string queueName, IReadOnlyList<PlayerKey> candidates, double oldQuality, double newQuality) =>
        HoldImproved.Add((queueName, candidates, oldQuality, newQuality));
    public void OnMatchProposed(MatchProposal proposal) => Proposed.Add(proposal);
    public void OnMatchStarted(ActiveMatch match) => Started.Add(match);
    public void OnMatchEnded(MatchOutcome outcome) => Ended.Add(outcome);
    public void OnAbandonment(PlayerKey player, int offenseCount, DateTimeOffset timeoutUntil) =>
        Abandonments.Add((player, offenseCount, timeoutUntil));
    public void OnTeammateAbandoned(IReadOnlyCollection<PlayerKey> survivors, PlayerKey abandoner, Guid matchId, DateTimeOffset at) =>
        TeammateAbandoned.Add((survivors, abandoner, matchId, at));
    public void OnPlayerReleasedFromMatch(PlayerKey player, Guid matchId, DateTimeOffset at) =>
        PlayerReleases.Add((player, matchId, at));
    public void OnTeamCollapsing(ActiveMatch m, int teamIdx, DateTimeOffset since, DateTimeOffset forfeitAt) =>
        TeamsCollapsing.Add((m.MatchId, teamIdx, since, forfeitAt));
    public void OnTeamRecovered(ActiveMatch m, int teamIdx) => TeamsRecovered.Add((m.MatchId, teamIdx));
    public void OnGriefingFlagged(PendingGriefingPenalty pending, DateTimeOffset timeoutUntil) =>
        GriefingFlagged.Add(pending);
    public void OnVetoRecorded(PendingGriefingPenalty pending, PlayerKey voter) =>
        VetoesRecorded.Add((pending, voter));
    public void OnGriefingVetoed(PendingGriefingPenalty pending) => GriefingVetoed.Add(pending);
    public void OnGriefingConfirmed(PendingGriefingPenalty pending, DateTimeOffset timeoutUntil) => GriefingConfirmed.Add(pending);
}
