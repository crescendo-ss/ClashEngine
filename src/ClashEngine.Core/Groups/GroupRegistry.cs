using System;
using System.Collections.Generic;
using ClashEngine.Core.Identity;

namespace ClashEngine.Core.Groups;

/// <summary>
/// A party's invite-control mode. Defaults to <see cref="Open"/> on group creation.
/// </summary>
public enum GroupMode
{
    /// <summary>Leaderless. Any current member can invite new players.</summary>
    Open,

    /// <summary>Leader-driven. Only the leader can invite. If the leader leaves, the group disbands.</summary>
    Closed,
}

/// <summary>
/// Tracks active groups and pending invitations. Solo players are not tracked; a group only
/// materializes when at least two players are bound. Groups dissolve automatically when their
/// membership drops to one (open mode) or when the leader leaves (closed mode).
/// </summary>
public sealed class GroupRegistry
{
    private static readonly IReadOnlySet<PlayerKey> EmptyMembers = new HashSet<PlayerKey>();
    private static readonly IReadOnlyCollection<PlayerKey> EmptyKeys = Array.Empty<PlayerKey>();

    private readonly Dictionary<PlayerKey, GroupId> _playerToGroup = new();
    private readonly Dictionary<GroupId, HashSet<PlayerKey>> _groupMembers = new();
    private readonly Dictionary<GroupId, GroupMode> _mode = new();
    private readonly Dictionary<GroupId, PlayerKey> _leader = new();
    private readonly InvitationLedger _ledger;

    public GroupRegistry(TimeSpan invitationTtl)
    {
        _ledger = new InvitationLedger(invitationTtl);
    }

    public TimeSpan InvitationTtl => _ledger.Ttl;

    public GroupId? GroupOf(PlayerKey player) =>
        _playerToGroup.TryGetValue(player, out var id) ? id : null;

    public IReadOnlySet<PlayerKey> MembersOf(GroupId group) =>
        _groupMembers.TryGetValue(group, out var set) ? set : EmptyMembers;

    public GroupMode ModeOf(GroupId group) =>
        _mode.TryGetValue(group, out var m) ? m : GroupMode.Open;

    /// <summary>The recorded leader for <paramref name="group"/>, or <see langword="null"/> if
    /// the group doesn't exist. The leader field is meaningful only for
    /// <see cref="GroupMode.Closed"/> groups; in <see cref="GroupMode.Open"/> mode it tracks the
    /// player who'd be promoted to leader if the group flipped to closed.</summary>
    public PlayerKey? LeaderOf(GroupId group) =>
        _leader.TryGetValue(group, out var k) ? k : null;

    public bool IsLeader(PlayerKey player, GroupId group) =>
        _leader.TryGetValue(group, out var k) && k == player;

    public IReadOnlyList<GroupInvitation> PendingFor(PlayerKey invitee, DateTimeOffset at) =>
        _ledger.Pending(invitee, at);

    public InviteResult Invite(PlayerKey inviter, PlayerKey invitee, DateTimeOffset at)
    {
        if (inviter.IsDefault || invitee.IsDefault)
            throw new ArgumentException("Players must not be default.");
        if (inviter == invitee) return InviteResult.SelfInvite;

        var inviterGroup = GroupOf(inviter);
        var inviteeGroup = GroupOf(invitee);

        if (inviterGroup is GroupId ig && inviteeGroup == ig) return InviteResult.AlreadyInGroup;
        if (inviteeGroup is not null) return InviteResult.InviteeBusy;

        // Closed-party gating: only the leader of a closed party may invite. Solo inviters (no
        // group yet) and Open-party members are unrestricted.
        if (inviterGroup is GroupId existingGroup
            && ModeOf(existingGroup) == GroupMode.Closed
            && !IsLeader(inviter, existingGroup))
        {
            return InviteResult.NotLeader;
        }

        return _ledger.Add(inviter, invitee, at) ? InviteResult.Sent : InviteResult.AlreadyInvited;
    }

    public AcceptResult Accept(PlayerKey invitee, PlayerKey? inviter, DateTimeOffset at, out GroupId groupId)
    {
        groupId = default;
        if (invitee.IsDefault) throw new ArgumentException("Player must not be default.", nameof(invitee));

        if (GroupOf(invitee) is not null) return AcceptResult.AlreadyInGroup;

        GroupInvitation invite;
        if (inviter is PlayerKey i)
        {
            var maybe = _ledger.Get(invitee, i, at);
            if (maybe is null)
            {
                // Distinguish expired vs never existed by peeking at any-state record.
                return AcceptResult.NoSuchInvite;
            }
            invite = maybe.Value;
        }
        else
        {
            var pending = _ledger.Pending(invitee, at);
            if (pending.Count == 0) return AcceptResult.NoPendingInvite;
            if (pending.Count > 1) return AcceptResult.AmbiguousMustSpecify;
            invite = pending[0];
        }

        // Add invitee to inviter's group, creating one if the inviter is solo.
        var inviterCurrent = GroupOf(invite.Inviter);
        if (inviterCurrent is GroupId existing)
        {
            _groupMembers[existing].Add(invitee);
            _playerToGroup[invitee] = existing;
            groupId = existing;
        }
        else
        {
            var fresh = GroupId.New();
            var members = new HashSet<PlayerKey> { invite.Inviter, invitee };
            _groupMembers[fresh] = members;
            _playerToGroup[invite.Inviter] = fresh;
            _playerToGroup[invitee] = fresh;
            // New groups default to Open. The inviter is recorded as the would-be leader so that
            // a later `?partymode closed` toggle can promote them without asking.
            _mode[fresh] = GroupMode.Open;
            _leader[fresh] = invite.Inviter;
            groupId = fresh;
        }

        // Clear the invitee's other pending invitations now that they're committed.
        _ledger.RemoveAllFor(invitee);
        return AcceptResult.Joined;
    }

    public DeclineResult Decline(PlayerKey invitee, PlayerKey? inviter, DateTimeOffset at)
    {
        if (invitee.IsDefault) throw new ArgumentException("Player must not be default.", nameof(invitee));

        if (inviter is PlayerKey i)
            return _ledger.Remove(invitee, i) ? DeclineResult.Declined : DeclineResult.NoSuchInvite;

        var pending = _ledger.Pending(invitee, at);
        if (pending.Count == 0) return DeclineResult.NoPendingInvite;
        if (pending.Count > 1) return DeclineResult.AmbiguousMustSpecify;

        _ledger.Remove(invitee, pending[0].Inviter);
        return DeclineResult.Declined;
    }

    /// <summary>
    /// Switch <paramref name="caller"/>'s current group to <paramref name="newMode"/>.
    /// In <see cref="GroupMode.Open"/> any member may flip to closed (the caller is promoted to
    /// leader). In <see cref="GroupMode.Closed"/> only the leader may flip back to open.
    /// </summary>
    public SetModeResult SetMode(PlayerKey caller, GroupMode newMode)
    {
        if (GroupOf(caller) is not GroupId group) return SetModeResult.NotInGroup;
        var current = ModeOf(group);
        if (current == newMode) return SetModeResult.Unchanged;

        // Closed -> Open is leader-only; Open -> Closed is open to any member, with the caller
        // becoming the leader of the now-closed party.
        if (current == GroupMode.Closed && !IsLeader(caller, group))
            return SetModeResult.NotLeader;

        _mode[group] = newMode;
        if (newMode == GroupMode.Closed)
            _leader[group] = caller;
        return SetModeResult.Changed;
    }

    /// <summary>
    /// Removes <paramref name="player"/> from their current group. The returned outcome lets the
    /// engine decide which queues to sweep and which surviving members to notify.
    /// </summary>
    /// <remarks>
    /// <para>In <see cref="GroupMode.Closed"/> mode, if the leaver is the leader the entire
    /// group is dissolved and every member ends up in <c>RemovedMembers</c>.</para>
    /// <para>Otherwise the leaver is dropped; if the remaining count falls to 1 the group
    /// dissolves with the lone surviving member promoted to <c>RemovedMembers</c> too.</para>
    /// </remarks>
    public bool Leave(PlayerKey player, out LeaveOutcome outcome)
    {
        if (!_playerToGroup.TryGetValue(player, out var group))
        {
            outcome = new LeaveOutcome(null, false, EmptyKeys, EmptyKeys, DisbandReason.MemberLeft);
            return false;
        }

        var members = _groupMembers[group];
        bool isLeader = IsLeader(player, group);
        bool closed = ModeOf(group) == GroupMode.Closed;

        // Closed-party leader leaves -> dissolve the whole party.
        if (closed && isLeader)
        {
            var removed = new List<PlayerKey>(members);
            DropGroup(group, members);
            outcome = new LeaveOutcome(group, true, removed, EmptyKeys, DisbandReason.LeaderLeft);
            return true;
        }

        // Drop just the leaver.
        _playerToGroup.Remove(player);
        members.Remove(player);

        if (members.Count <= 1)
        {
            // Group dissolves; report both the leaver and the lone remaining member.
            var lastOne = new PlayerKey[members.Count];
            int idx = 0;
            foreach (var m in members) lastOne[idx++] = m;
            var allRemoved = new PlayerKey[1 + lastOne.Length];
            allRemoved[0] = player;
            for (int i = 0; i < lastOne.Length; i++) allRemoved[i + 1] = lastOne[i];

            DropGroup(group, members);
            outcome = new LeaveOutcome(group, true, allRemoved, EmptyKeys, DisbandReason.LastMemberDropped);
            return true;
        }

        // Group survives. Surface the remaining members so the engine can dequeue them too.
        var surviving = new PlayerKey[members.Count];
        {
            int idx = 0;
            foreach (var m in members) surviving[idx++] = m;
        }
        outcome = new LeaveOutcome(group, false, new[] { player }, surviving, DisbandReason.MemberLeft);
        return true;
    }

    /// <summary>Removes all expired invitations. Returns the number pruned.</summary>
    public int PruneExpiredInvitations(DateTimeOffset at) => _ledger.PruneExpired(at);

    public int PendingInvitationCount => _ledger.Count;

    /// <summary>Tear down a group's tracking maps. Caller must already have decided the group
    /// is dissolving.</summary>
    private void DropGroup(GroupId group, HashSet<PlayerKey> members)
    {
        foreach (var m in members) _playerToGroup.Remove(m);
        _groupMembers.Remove(group);
        _mode.Remove(group);
        _leader.Remove(group);
    }
}
