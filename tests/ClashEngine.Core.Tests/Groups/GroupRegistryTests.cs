using ClashEngine.Core.Groups;
using ClashEngine.Core.Identity;

namespace ClashEngine.Core.Tests.Groups;

public class GroupRegistryTests
{
    private static PlayerKey K(string n) => new(n);
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static GroupRegistry Reg() => new(TimeSpan.FromSeconds(15));

    [Fact]
    public void New_player_has_no_group()
    {
        Assert.Null(Reg().GroupOf(K("A")));
    }

    [Fact]
    public void Self_invite_rejected()
    {
        Assert.Equal(InviteResult.SelfInvite, Reg().Invite(K("A"), K("A"), T0));
    }

    [Fact]
    public void Inviting_busy_player_returns_InviteeBusy()
    {
        var r = Reg();
        r.Invite(K("A"), K("B"), T0);
        r.Accept(K("B"), K("A"), T0.AddSeconds(1), out _, out _);
        // B is now in a group with A.

        Assert.Equal(InviteResult.InviteeBusy, r.Invite(K("C"), K("B"), T0.AddSeconds(2)));
    }

    [Fact]
    public void Duplicate_invite_returns_AlreadyInvited()
    {
        var r = Reg();
        Assert.Equal(InviteResult.Sent, r.Invite(K("A"), K("B"), T0));
        Assert.Equal(InviteResult.AlreadyInvited, r.Invite(K("A"), K("B"), T0.AddSeconds(5)));
    }

    [Fact]
    public void Accept_with_inviter_creates_a_group_for_solo_inviter()
    {
        var r = Reg();
        r.Invite(K("A"), K("B"), T0);

        var status = r.Accept(K("B"), K("A"), T0.AddSeconds(2), out var groupId, out _);

        Assert.Equal(AcceptResult.Joined, status);
        Assert.False(groupId.IsDefault);
        Assert.Equal(groupId, r.GroupOf(K("A")));
        Assert.Equal(groupId, r.GroupOf(K("B")));
        Assert.Equal(2, r.MembersOf(groupId).Count);
    }

    [Fact]
    public void Accept_invitee_joins_existing_group_when_inviter_already_grouped()
    {
        var r = Reg();
        r.Invite(K("A"), K("B"), T0);
        r.Accept(K("B"), K("A"), T0.AddSeconds(1), out var groupId, out _);
        // {A, B} group exists.

        r.Invite(K("A"), K("C"), T0.AddSeconds(2));
        var status = r.Accept(K("C"), K("A"), T0.AddSeconds(3), out var sameGroupId, out _);

        Assert.Equal(AcceptResult.Joined, status);
        Assert.Equal(groupId, sameGroupId);
        Assert.Equal(3, r.MembersOf(groupId).Count);
    }

    [Fact]
    public void Accept_with_no_inviter_resolves_when_only_one_pending()
    {
        var r = Reg();
        r.Invite(K("A"), K("B"), T0);
        Assert.Equal(AcceptResult.Joined, r.Accept(K("B"), null, T0.AddSeconds(1), out _, out _));
    }

    [Fact]
    public void Accept_with_no_inviter_is_ambiguous_when_multiple_pending()
    {
        var r = Reg();
        r.Invite(K("A"), K("X"), T0);
        r.Invite(K("B"), K("X"), T0);

        Assert.Equal(AcceptResult.AmbiguousMustSpecify, r.Accept(K("X"), null, T0.AddSeconds(1), out _, out _));
    }

    [Fact]
    public void Accept_no_pending_returns_NoPendingInvite()
    {
        Assert.Equal(AcceptResult.NoPendingInvite, Reg().Accept(K("A"), null, T0, out _, out _));
    }

    [Fact]
    public void Accept_after_ttl_returns_NoSuchInvite()
    {
        var r = Reg();
        r.Invite(K("A"), K("B"), T0);
        Assert.Equal(AcceptResult.NoSuchInvite, r.Accept(K("B"), K("A"), T0.AddSeconds(20), out _, out _));
    }

    [Fact]
    public void Accept_clears_other_pending_invitations_for_invitee()
    {
        var r = Reg();
        r.Invite(K("A"), K("X"), T0);
        r.Invite(K("B"), K("X"), T0);

        r.Accept(K("X"), K("A"), T0.AddSeconds(1), out _, out _);

        Assert.Empty(r.PendingFor(K("X"), T0.AddSeconds(2)));
    }

    [Fact]
    public void Decline_targeted_removes_one_invitation()
    {
        var r = Reg();
        r.Invite(K("A"), K("X"), T0);
        r.Invite(K("B"), K("X"), T0);

        Assert.Equal(DeclineResult.Declined, r.Decline(K("X"), K("A"), T0.AddSeconds(1), out _));
        Assert.Single(r.PendingFor(K("X"), T0.AddSeconds(2)));
    }

    [Fact]
    public void Decline_with_no_inviter_resolves_single_pending()
    {
        var r = Reg();
        r.Invite(K("A"), K("X"), T0);
        Assert.Equal(DeclineResult.Declined, r.Decline(K("X"), null, T0.AddSeconds(1), out _));
    }

    [Fact]
    public void Decline_with_no_inviter_is_ambiguous_when_multiple()
    {
        var r = Reg();
        r.Invite(K("A"), K("X"), T0);
        r.Invite(K("B"), K("X"), T0);
        Assert.Equal(DeclineResult.AmbiguousMustSpecify, r.Decline(K("X"), null, T0.AddSeconds(1), out _));
    }

    [Fact]
    public void Leaving_a_group_of_two_dissolves_it()
    {
        var r = Reg();
        r.Invite(K("A"), K("B"), T0);
        r.Accept(K("B"), K("A"), T0.AddSeconds(1), out var groupId, out _);

        Assert.True(r.Leave(K("A"), out var outcome));

        Assert.Null(r.GroupOf(K("A")));
        Assert.Null(r.GroupOf(K("B")));   // group dissolved on the way down to 1 member
        Assert.Empty(r.MembersOf(groupId));
        Assert.True(outcome.GroupDissolved);
        Assert.Equal(DisbandReason.LastMemberDropped, outcome.Reason);
        Assert.Equal(2, outcome.RemovedMembers.Count);
    }

    [Fact]
    public void Leaving_a_group_of_three_keeps_it_with_two()
    {
        var r = Reg();
        r.Invite(K("A"), K("B"), T0);
        r.Accept(K("B"), K("A"), T0.AddSeconds(1), out _, out _);
        r.Invite(K("A"), K("C"), T0.AddSeconds(2));
        r.Accept(K("C"), K("A"), T0.AddSeconds(3), out var groupId, out _);

        Assert.True(r.Leave(K("C"), out var outcome));

        Assert.Equal(2, r.MembersOf(groupId).Count);
        Assert.Null(r.GroupOf(K("C")));
        Assert.Equal(groupId, r.GroupOf(K("A")));
        Assert.Equal(groupId, r.GroupOf(K("B")));
        Assert.False(outcome.GroupDissolved);
        Assert.Equal(DisbandReason.MemberLeft, outcome.Reason);
        Assert.Equal(2, outcome.SurvivingMembers.Count);
    }

    [Fact]
    public void Leave_for_non_member_returns_false()
    {
        Assert.False(Reg().Leave(K("Ghost"), out _));
    }

    // ---- mode + leader behavior

    [Fact]
    public void New_groups_default_to_open_mode_and_record_inviter_as_would_be_leader()
    {
        var r = Reg();
        r.Invite(K("A"), K("B"), T0);
        r.Accept(K("B"), K("A"), T0.AddSeconds(1), out var groupId, out _);

        Assert.Equal(GroupMode.Open, r.ModeOf(groupId));
        Assert.Equal(K("A"), r.LeaderOf(groupId));   // leader field tracked for promotion-on-close
    }

    [Fact]
    public void Open_party_lets_any_member_invite()
    {
        var r = Reg();
        r.Invite(K("A"), K("B"), T0);
        r.Accept(K("B"), K("A"), T0.AddSeconds(1), out _, out _);

        // B (non-leader) can invite C while the party is Open.
        Assert.Equal(InviteResult.Sent, r.Invite(K("B"), K("C"), T0.AddSeconds(2)));
    }

    [Fact]
    public void SetMode_open_to_closed_promotes_caller_to_leader()
    {
        var r = Reg();
        r.Invite(K("A"), K("B"), T0);
        r.Accept(K("B"), K("A"), T0.AddSeconds(1), out var groupId, out _);

        Assert.Equal(SetModeResult.Changed, r.SetMode(K("B"), GroupMode.Closed));
        Assert.Equal(GroupMode.Closed, r.ModeOf(groupId));
        Assert.Equal(K("B"), r.LeaderOf(groupId));   // promoted to leader on the close
    }

    [Fact]
    public void SetMode_unchanged_returns_unchanged()
    {
        var r = Reg();
        r.Invite(K("A"), K("B"), T0);
        r.Accept(K("B"), K("A"), T0.AddSeconds(1), out _, out _);

        Assert.Equal(SetModeResult.Unchanged, r.SetMode(K("A"), GroupMode.Open));
    }

    [Fact]
    public void SetMode_closed_to_open_requires_leader()
    {
        var r = Reg();
        r.Invite(K("A"), K("B"), T0);
        r.Accept(K("B"), K("A"), T0.AddSeconds(1), out _, out _);
        r.SetMode(K("A"), GroupMode.Closed);   // A becomes leader

        Assert.Equal(SetModeResult.NotLeader, r.SetMode(K("B"), GroupMode.Open));
        Assert.Equal(SetModeResult.Changed, r.SetMode(K("A"), GroupMode.Open));
    }

    [Fact]
    public void SetMode_for_non_member_returns_NotInGroup()
    {
        Assert.Equal(SetModeResult.NotInGroup, Reg().SetMode(K("Ghost"), GroupMode.Closed));
    }

    [Fact]
    public void Closed_party_blocks_non_leader_invite()
    {
        var r = Reg();
        r.Invite(K("A"), K("B"), T0);
        r.Accept(K("B"), K("A"), T0.AddSeconds(1), out _, out _);
        r.SetMode(K("A"), GroupMode.Closed);   // A is leader

        Assert.Equal(InviteResult.NotLeader, r.Invite(K("B"), K("C"), T0.AddSeconds(2)));
        Assert.Equal(InviteResult.Sent, r.Invite(K("A"), K("C"), T0.AddSeconds(3)));
    }

    [Fact]
    public void Closed_party_leader_leave_disbands_everyone()
    {
        var r = Reg();
        r.Invite(K("A"), K("B"), T0);
        r.Accept(K("B"), K("A"), T0.AddSeconds(1), out var groupId, out _);
        r.Invite(K("A"), K("C"), T0.AddSeconds(2));
        r.Accept(K("C"), K("A"), T0.AddSeconds(3), out _, out _);
        r.SetMode(K("A"), GroupMode.Closed);

        Assert.True(r.Leave(K("A"), out var outcome));

        Assert.True(outcome.GroupDissolved);
        Assert.Equal(DisbandReason.LeaderLeft, outcome.Reason);
        Assert.Equal(3, outcome.RemovedMembers.Count);
        Assert.Empty(outcome.SurvivingMembers);
        Assert.Null(r.GroupOf(K("A")));
        Assert.Null(r.GroupOf(K("B")));
        Assert.Null(r.GroupOf(K("C")));
        Assert.Empty(r.MembersOf(groupId));
    }

    [Fact]
    public void Closed_party_non_leader_leave_drops_only_the_leaver()
    {
        var r = Reg();
        r.Invite(K("A"), K("B"), T0);
        r.Accept(K("B"), K("A"), T0.AddSeconds(1), out var groupId, out _);
        r.Invite(K("A"), K("C"), T0.AddSeconds(2));
        r.Accept(K("C"), K("A"), T0.AddSeconds(3), out _, out _);
        r.SetMode(K("A"), GroupMode.Closed);

        Assert.True(r.Leave(K("C"), out var outcome));

        Assert.False(outcome.GroupDissolved);
        Assert.Equal(DisbandReason.MemberLeft, outcome.Reason);
        Assert.Equal(groupId, r.GroupOf(K("A")));
        Assert.Equal(groupId, r.GroupOf(K("B")));
        Assert.Null(r.GroupOf(K("C")));
    }

    [Fact]
    public void PruneExpiredInvitations_removes_only_expired()
    {
        var r = Reg();
        r.Invite(K("A"), K("X"), T0);
        r.Invite(K("B"), K("X"), T0.AddSeconds(20));

        Assert.Equal(1, r.PruneExpiredInvitations(T0.AddSeconds(20)));
        Assert.Single(r.PendingFor(K("X"), T0.AddSeconds(20)));
    }

    [Fact]
    public void Invitation_ttl_is_15_seconds_by_convention_when_specified()
    {
        var r = new GroupRegistry(TimeSpan.FromSeconds(15));
        Assert.Equal(TimeSpan.FromSeconds(15), r.InvitationTtl);
    }

    [Fact]
    public void Invite_to_already_grouped_invitee_returns_InviteeBusy()
    {
        var r = Reg();
        r.Invite(K("A"), K("B"), T0);
        r.Accept(K("B"), K("A"), T0.AddSeconds(1), out _, out _);
        Assert.Equal(InviteResult.InviteeBusy, r.Invite(K("C"), K("A"), T0.AddSeconds(2)));
    }

    [Fact]
    public void Accept_while_already_in_a_group_returns_AlreadyInGroup()
    {
        // Single-party invariant: a player can never be in two groups. Even if a stale or
        // cross-arena invitation sneaks through, the second Accept is rejected.
        var r = Reg();
        r.Invite(K("A"), K("B"), T0);
        r.Accept(K("B"), K("A"), T0.AddSeconds(1), out var firstGroup, out _);

        // C tries to invite B into a different group. Invite alone is rejected as InviteeBusy
        // (B is in firstGroup); even if it landed somehow, Accept would still refuse.
        Assert.Equal(InviteResult.InviteeBusy, r.Invite(K("C"), K("B"), T0.AddSeconds(2)));
        Assert.Equal(AcceptResult.AlreadyInGroup, r.Accept(K("B"), K("C"), T0.AddSeconds(3), out _, out _));
        Assert.Equal(firstGroup, r.GroupOf(K("B")));
    }
}
