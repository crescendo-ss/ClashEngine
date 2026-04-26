using ClashEngine.Core.Groups;
using ClashEngine.Core.Identity;

namespace ClashEngine.Core.Tests.Groups;

public class InvitationLedgerTests
{
    private static PlayerKey K(string n) => new(n);
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Add_then_Get_within_ttl_returns_invitation()
    {
        var l = new InvitationLedger(TimeSpan.FromSeconds(15));
        l.Add(K("A"), K("B"), T0);

        var got = l.Get(K("B"), K("A"), T0.AddSeconds(5));
        Assert.NotNull(got);
        Assert.Equal(K("A"), got!.Value.Inviter);
        Assert.Equal(K("B"), got.Value.Invitee);
        Assert.Equal(T0.AddSeconds(15), got.Value.ExpiresAt);
    }

    [Fact]
    public void Get_after_ttl_returns_null()
    {
        var l = new InvitationLedger(TimeSpan.FromSeconds(15));
        l.Add(K("A"), K("B"), T0);
        Assert.Null(l.Get(K("B"), K("A"), T0.AddSeconds(20)));
    }

    [Fact]
    public void Add_duplicate_within_ttl_is_rejected()
    {
        var l = new InvitationLedger(TimeSpan.FromSeconds(15));
        Assert.True(l.Add(K("A"), K("B"), T0));
        Assert.False(l.Add(K("A"), K("B"), T0.AddSeconds(5)));
    }

    [Fact]
    public void Add_after_expiry_replaces_old_invitation()
    {
        var l = new InvitationLedger(TimeSpan.FromSeconds(15));
        l.Add(K("A"), K("B"), T0);
        Assert.True(l.Add(K("A"), K("B"), T0.AddSeconds(20)));
        Assert.NotNull(l.Get(K("B"), K("A"), T0.AddSeconds(25)));
    }

    [Fact]
    public void Pending_returns_only_unexpired_for_invitee()
    {
        var l = new InvitationLedger(TimeSpan.FromSeconds(15));
        l.Add(K("A"), K("X"), T0);
        l.Add(K("B"), K("X"), T0.AddSeconds(20));
        l.Add(K("C"), K("Y"), T0.AddSeconds(20));

        var pending = l.Pending(K("X"), T0.AddSeconds(25));
        Assert.Single(pending);
        Assert.Equal(K("B"), pending[0].Inviter);
    }

    [Fact]
    public void GetSingle_returns_the_only_pending_or_null()
    {
        var l = new InvitationLedger(TimeSpan.FromSeconds(15));
        l.Add(K("A"), K("X"), T0);
        Assert.NotNull(l.GetSingle(K("X"), T0.AddSeconds(5)));

        l.Add(K("B"), K("X"), T0);
        Assert.Null(l.GetSingle(K("X"), T0.AddSeconds(5)));   // ambiguous
    }

    [Fact]
    public void Remove_targeted_invitation()
    {
        var l = new InvitationLedger(TimeSpan.FromSeconds(15));
        l.Add(K("A"), K("X"), T0);
        Assert.True(l.Remove(K("X"), K("A")));
        Assert.False(l.Remove(K("X"), K("A")));
    }

    [Fact]
    public void RemoveAllFor_clears_all_invitations_to_invitee()
    {
        var l = new InvitationLedger(TimeSpan.FromSeconds(15));
        l.Add(K("A"), K("X"), T0);
        l.Add(K("B"), K("X"), T0);
        l.Add(K("A"), K("Y"), T0);

        Assert.Equal(2, l.RemoveAllFor(K("X")));
        Assert.Empty(l.Pending(K("X"), T0.AddSeconds(5)));
        Assert.Single(l.Pending(K("Y"), T0.AddSeconds(5)));
    }

    [Fact]
    public void PruneExpired_removes_only_expired()
    {
        var l = new InvitationLedger(TimeSpan.FromSeconds(15));
        l.Add(K("A"), K("X"), T0);
        l.Add(K("B"), K("X"), T0.AddSeconds(20));

        Assert.Equal(1, l.PruneExpired(T0.AddSeconds(20)));
        Assert.Equal(1, l.Count);
    }

    [Fact]
    public void Constructor_rejects_non_positive_ttl()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new InvitationLedger(TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() => new InvitationLedger(TimeSpan.FromSeconds(-1)));
    }
}
