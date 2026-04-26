using System.Collections.Generic;
using ClashEngine.Core.Identity;

namespace ClashEngine.Core.Groups;

public enum InviteResult
{
    /// <summary>Invitation recorded.</summary>
    Sent,

    /// <summary>Inviter and invitee passed are the same player.</summary>
    SelfInvite,

    /// <summary>The invitee is already in the inviter's group; nothing to do.</summary>
    AlreadyInGroup,

    /// <summary>The invitee is already in some other group.</summary>
    InviteeBusy,

    /// <summary>An identical, still-valid invitation already exists.</summary>
    AlreadyInvited,

    /// <summary>The inviter's group is in <see cref="GroupMode.Closed"/> mode and the inviter
    /// isn't the leader. Only the leader can issue invitations in a closed party.</summary>
    NotLeader,
}

public enum AcceptResult
{
    /// <summary>Invitee joined a group (new or existing). The resulting <c>GroupId</c> is set.</summary>
    Joined,

    /// <summary>No matching pending invitation exists.</summary>
    NoSuchInvite,

    /// <summary>The invitee has no pending invitations.</summary>
    NoPendingInvite,

    /// <summary>The invitee has multiple pending invitations and did not specify which.</summary>
    AmbiguousMustSpecify,

    /// <summary>The invitation has already expired.</summary>
    InviteExpired,

    /// <summary>The invitee is already in a group.</summary>
    AlreadyInGroup,
}

public enum DeclineResult
{
    Declined,
    NoSuchInvite,
    NoPendingInvite,
    AmbiguousMustSpecify,
}

public enum SetModeResult
{
    /// <summary>The mode was changed.</summary>
    Changed,

    /// <summary>The group already had the requested mode.</summary>
    Unchanged,

    /// <summary>The caller isn't in any group.</summary>
    NotInGroup,

    /// <summary>The group is closed and the caller isn't the leader.</summary>
    NotLeader,
}

/// <summary>Why a group was dissolved.</summary>
public enum DisbandReason
{
    /// <summary>A non-leader member left a multi-member group; the group still exists.</summary>
    MemberLeft,

    /// <summary>A leader left a closed party. The entire group was dissolved.</summary>
    LeaderLeft,

    /// <summary>A non-leader member left and the group dropped to one member, so it dissolved.</summary>
    LastMemberDropped,
}

/// <summary>
/// Outcome of <see cref="GroupRegistry.Leave"/> rich enough for the engine to (a) decide which
/// players to dequeue from queues, and (b) chat-notify the right surviving members.
/// </summary>
public readonly record struct LeaveOutcome(
    GroupId? GroupId,
    bool GroupDissolved,
    IReadOnlyCollection<PlayerKey> RemovedMembers,
    IReadOnlyCollection<PlayerKey> SurvivingMembers,
    DisbandReason Reason);
