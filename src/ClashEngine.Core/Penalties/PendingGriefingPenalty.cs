using System;
using System.Collections.Generic;
using ClashEngine.Core.Identity;

namespace ClashEngine.Core.Penalties;

/// <summary>
/// A griefing penalty currently in its veto window. The penalty has already been recorded in the
/// <see cref="PenaltyTracker"/>; it is rescinded if enough match participants veto before the
/// window expires.
/// </summary>
public sealed record PendingGriefingPenalty(
    Guid MatchId,
    PlayerKey Target,
    string Reason,
    DateTimeOffset PenaltyAppliedAt,
    DateTimeOffset VetoWindowEndsAt,
    int VetoesRequired,
    IReadOnlySet<PlayerKey> EligibleVoters,
    IReadOnlySet<PlayerKey> VotesReceived)
{
    public int VetoesRemaining => Math.Max(0, VetoesRequired - VotesReceived.Count);
}

public enum VetoResult
{
    /// <summary>Vote counted; more vetoes still needed to rescind the penalty.</summary>
    RecordedNeedMore,

    /// <summary>Threshold reached; penalty has been rescinded.</summary>
    PenaltyRescinded,

    /// <summary>Voter has already voted on this penalty.</summary>
    AlreadyVoted,

    /// <summary>Voter is not eligible (not a match participant, or is the target).</summary>
    NotEligible,

    /// <summary>No pending griefing penalty exists for this (match, target).</summary>
    NoPendingPenalty,

    /// <summary>The veto window has already expired; penalty is now confirmed.</summary>
    WindowExpired,
}
