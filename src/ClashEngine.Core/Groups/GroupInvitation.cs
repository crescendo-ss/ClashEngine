using System;
using ClashEngine.Core.Identity;

namespace ClashEngine.Core.Groups;

/// <summary>
/// One pending group invitation. Carries no <see cref="GroupId"/> directly: at the moment of
/// acceptance, the invitee is added to whatever group the inviter is currently in (creating a
/// new group with both members if the inviter was solo).
/// </summary>
public readonly record struct GroupInvitation(
    PlayerKey Inviter,
    PlayerKey Invitee,
    DateTimeOffset SentAt,
    DateTimeOffset ExpiresAt);
