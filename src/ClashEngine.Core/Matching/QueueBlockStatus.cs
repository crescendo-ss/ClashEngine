using System;

namespace ClashEngine.Core.Matching;

/// <summary>
/// Why a queue that already has enough waiters (<see cref="MatchShape.TotalPlayers"/> or more) is
/// not currently being turned into a match. Surfaced by the <see cref="Matcher"/> for diagnostics:
/// logged at Verbose on change and pulled by the <c>?queue</c> command. A queue with too few
/// waiters has no status (it isn't "blocked", just under-filled).
/// </summary>
public enum QueueBlockReason
{
    /// <summary>
    /// The best partition the balancer could form scores below the quality floor the queue's
    /// <see cref="ClashEngine.Core.Queue.QueueDefinition.QualityPolicy"/> requires for the
    /// longest-waiter's current wait. Teams are too imbalanced to start (yet).
    /// </summary>
    BelowQualityThreshold,

    /// <summary>
    /// No valid team assignment exists at all -- e.g. a party is only partially inside the
    /// look-ahead pool, or <see cref="MatchShape.MaxOrdinalSpread"/> / <see cref="MatchShape.MaxLiabilityGap"/>
    /// reject every partition. Distinct from <see cref="BelowQualityThreshold"/>, where a partition
    /// exists but is too imbalanced.
    /// </summary>
    NoViableTeams,

    /// <summary>
    /// A viable, above-threshold partition was found, but the matcher is holding it through the
    /// look-ahead / hold window in case better arrivals improve the teams. Matchmaking has
    /// effectively succeeded; it is intentionally waiting (see <see cref="QueueBlockStatus.HoldUntil"/>).
    /// </summary>
    HoldingForArrivals,
}

/// <summary>
/// A human-translatable snapshot of why a full-enough queue has not produced a match this tick.
/// Produced by the <see cref="Matcher"/> each evaluation and cached per queue; the host adapter
/// formats it into the operator-facing line (one formatter shared by the Verbose log and the
/// <c>?queue</c> reply). All numbers are as-of the producing tick.
/// </summary>
/// <param name="Reason">Which blocking case this is.</param>
/// <param name="BestQuality">
/// Quality (in [0,1]) of the best partition the balancer found. For
/// <see cref="QueueBlockReason.BelowQualityThreshold"/> this is the sub-threshold best; for
/// <see cref="QueueBlockReason.HoldingForArrivals"/> it is the held candidate's quality. Meaningless
/// (0) for <see cref="QueueBlockReason.NoViableTeams"/>, where no partition exists.
/// </param>
/// <param name="Threshold">
/// The minimum quality the queue currently requires to start, from its quality-relaxation policy
/// evaluated at the longest waiter's wait time.
/// </param>
/// <param name="HoldUntil">
/// For <see cref="QueueBlockReason.HoldingForArrivals"/>, the absolute time the hold window expires
/// (the adapter renders the live remaining seconds against "now"). <see langword="null"/> otherwise.
/// </param>
public readonly record struct QueueBlockStatus(
    QueueBlockReason Reason,
    double BestQuality,
    double Threshold,
    DateTimeOffset? HoldUntil);
