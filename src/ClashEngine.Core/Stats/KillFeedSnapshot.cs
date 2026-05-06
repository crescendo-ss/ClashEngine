using System;
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
/// Per-kill mutable match state captured immediately before <c>engine.OnKill</c> processes a
/// kill, then handed to the deferred kill-feed broadcast 200 ms later. Pairs with
/// <see cref="KillFeedMatchContext"/>, which carries the structurally stable per-match fields.
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

/// <summary>
/// Per-match structural fields the kill-feed broadcast needs (team rosters, lives-per-player
/// gate, match start time). Captured at kill time and held on the deferred-kill record so the
/// 200 ms-late broadcast can render correctly even when the match-ending kill has already
/// triggered <c>FinalizeMatch</c>, which removes the <c>ActiveMatch</c> from the engine's
/// dictionary before the deferred timer (or the match-end drain) fires.
/// </summary>
/// <remarks>
/// Without this snapshot, <c>KillFeedReporter</c> would early-out on the
/// <c>_engine.ActiveMatches.TryGetValue</c> lookup for the eliminating kill of a match -- the
/// player would never see the final "X kb Y" / "X is OUT" / "Score: a-b" lines. Same idea as
/// <see cref="KillFeedPreState"/> capturing dynamic state, just for the structural fields.
/// </remarks>
public sealed record KillFeedMatchContext(
    IReadOnlyList<IReadOnlyList<PlayerKey>> Teams,
    int? LivesPerPlayer,
    DateTimeOffset? StartedAt);
