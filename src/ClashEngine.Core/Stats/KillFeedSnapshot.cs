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

/// <summary>
/// Match state captured immediately before <c>engine.OnKill</c> processes a kill, then handed
/// to the deferred kill-feed broadcast 200 ms later. Captures only the values that change
/// between pre- and post-engine state -- everything stable in 200 ms (team rosters, match
/// start time, lives-per-player config) is read live off <see cref="ClashEngine.Core.Matches.ActiveMatch"/>.
/// </summary>
/// <param name="VictimLivesPreKill">Victim's <c>LivesRemaining</c> entry before the engine
/// decremented it. Negative when the match has no lives system or the victim has no entry.</param>
/// <param name="VictimAlreadyExitedPreKill"><see langword="true"/> when the victim was already
/// in <c>ExitedAt</c> before this kill (ghost kill on an out player).</param>
/// <param name="PreKillScores">Per-team kill counts before the engine added this kill's point.
/// Indexed by team index -- snapshot copy of <c>match.KillsByTeam</c> at pre-engine time.</param>
public sealed record KillFeedPreState(
    int VictimLivesPreKill,
    bool VictimAlreadyExitedPreKill,
    IReadOnlyList<int> PreKillScores);
