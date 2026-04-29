using System;
using System.Collections.Generic;
using ClashEngine.Core.Identity;

namespace ClashEngine.Core.Groups;

/// <summary>
/// Tracks pending group invitations keyed by <c>(invitee, inviter)</c>. Expired invitations are
/// filtered out of all queries and can be physically removed via <see cref="PruneExpired"/>.
/// </summary>
public sealed class InvitationLedger
{
    private readonly Dictionary<(PlayerKey Invitee, PlayerKey Inviter), GroupInvitation> _byKey = new();

    public InvitationLedger(TimeSpan ttl)
    {
        if (ttl <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(ttl), "Must be positive.");
        Ttl = ttl;
    }

    public TimeSpan Ttl { get; }

    /// <summary>
    /// Records an invitation. Returns <see langword="false"/> if a still-valid invitation
    /// for this <c>(invitee, inviter)</c> pair already exists.
    /// </summary>
    public bool Add(PlayerKey inviter, PlayerKey invitee, DateTimeOffset at)
    {
        var key = (invitee, inviter);
        if (_byKey.TryGetValue(key, out var existing) && existing.ExpiresAt > at)
            return false;
        _byKey[key] = new GroupInvitation(inviter, invitee, at, at + Ttl);
        return true;
    }

    public GroupInvitation? Get(PlayerKey invitee, PlayerKey inviter, DateTimeOffset at)
    {
        if (!_byKey.TryGetValue((invitee, inviter), out var invite)) return null;
        return invite.ExpiresAt > at ? invite : null;
    }

    public IReadOnlyList<GroupInvitation> Pending(PlayerKey invitee, DateTimeOffset at)
    {
        var list = new List<GroupInvitation>();
        foreach (var kvp in _byKey)
            if (kvp.Key.Invitee == invitee && kvp.Value.ExpiresAt > at)
                list.Add(kvp.Value);
        return list;
    }

    /// <summary>Returns the only pending invitation for <paramref name="invitee"/>, or null if zero or multiple.</summary>
    public GroupInvitation? GetSingle(PlayerKey invitee, DateTimeOffset at)
    {
        var pending = Pending(invitee, at);
        return pending.Count == 1 ? pending[0] : null;
    }

    public bool Remove(PlayerKey invitee, PlayerKey inviter) =>
        _byKey.Remove((invitee, inviter));

    /// <summary>Removes all pending invitations for <paramref name="invitee"/>. Used on accept.</summary>
    public int RemoveAllFor(PlayerKey invitee)
    {
        var toRemove = new List<(PlayerKey, PlayerKey)>();
        foreach (var key in _byKey.Keys)
            if (key.Invitee == invitee) toRemove.Add(key);
        foreach (var k in toRemove) _byKey.Remove(k);
        return toRemove.Count;
    }

    public int PruneExpired(DateTimeOffset at)
    {
        var toRemove = new List<(PlayerKey, PlayerKey)>();
        foreach (var kvp in _byKey)
            if (kvp.Value.ExpiresAt <= at) toRemove.Add(kvp.Key);
        foreach (var k in toRemove) _byKey.Remove(k);
        return toRemove.Count;
    }

    /// <summary>Variant of <see cref="PruneExpired"/> that returns the pruned invitations so the
    /// caller can fire per-invitation telemetry. Returns an empty list if nothing expired.</summary>
    public IReadOnlyList<GroupInvitation> PruneExpiredAndReport(DateTimeOffset at)
    {
        List<GroupInvitation>? expired = null;
        List<(PlayerKey, PlayerKey)>? keys = null;
        foreach (var kvp in _byKey)
        {
            if (kvp.Value.ExpiresAt <= at)
            {
                (expired ??= new List<GroupInvitation>()).Add(kvp.Value);
                (keys ??= new List<(PlayerKey, PlayerKey)>()).Add(kvp.Key);
            }
        }
        if (keys is not null) foreach (var k in keys) _byKey.Remove(k);
        return expired ?? (IReadOnlyList<GroupInvitation>)Array.Empty<GroupInvitation>();
    }

    /// <summary>Removes every invitation involving <paramref name="player"/> on either side
    /// (inviter or invitee). Used when a player disconnects so stale invites don't outlive the
    /// session.</summary>
    public int RemoveAllInvolving(PlayerKey player)
    {
        var toRemove = new List<(PlayerKey, PlayerKey)>();
        foreach (var key in _byKey.Keys)
            if (key.Invitee == player || key.Inviter == player) toRemove.Add(key);
        foreach (var k in toRemove) _byKey.Remove(k);
        return toRemove.Count;
    }

    public int Count => _byKey.Count;
}
