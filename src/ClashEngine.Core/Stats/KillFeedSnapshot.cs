using System.Collections.Generic;
using ClashEngine.Core.Identity;

namespace ClashEngine.Core.Stats;

/// <summary>
/// Single assister entry on a kill feed line: the player who contributed damage and the raw
/// (recovery-adjusted, non-decayed) amount they dealt to the victim before death.
/// </summary>
public readonly record struct KillFeedAssister(PlayerKey Player, int Damage);

/// <summary>
/// Per-kill attribution snapshot meant for the in-arena kill-feed chat message. Built from the
/// victim's recovery state at the moment of death; the killer's blow is the largest single
/// contributor and assisters are the remaining opposite-team attackers, sorted by descending
/// damage.
/// </summary>
public sealed record KillFeedSnapshot(int KillerDamage, IReadOnlyList<KillFeedAssister> Assisters);
