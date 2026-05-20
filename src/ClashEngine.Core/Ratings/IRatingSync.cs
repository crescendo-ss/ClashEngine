using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ClashEngine.Core.Ratings;

/// <summary>
/// Outcome of a rating push attempt. Maps the stats server's HTTP response into the three
/// outcomes the caller cares about: definitely persisted, definitely not reachable, or
/// rejected by the server (a permanent-class failure the caller should not silently retry).
/// </summary>
public enum RatingPushStatus
{
    /// <summary>The server returned 200 and reported all entries either stored or skipped via
    /// the <c>updatedAt</c> guard. Includes the partial case where some entries were stored
    /// and others were ignored as stale -- both are an "ok" outcome from our side.</summary>
    Ok,

    /// <summary>Network error, timeout, or HTTP 5xx. The push did not land; the caller may
    /// retry on the next disconnect / flush cycle.</summary>
    Unreachable,

    /// <summary>HTTP 4xx -- the server rejected the body. Typically malformed input, unknown
    /// gametype, or auth failure. Not retried automatically; the operator must investigate.</summary>
    Rejected,
}

/// <summary>
/// Aggregated outcome of a bulk rating push. <see cref="Accepted"/> + <see cref="Skipped"/>
/// describes how the server handled the entries: the server returns these counts in its
/// <c>{ ok, accepted, skipped }</c> 200 body so the caller can log a single line per flush
/// instead of one per entry.
/// </summary>
public readonly record struct RatingPushResult(RatingPushStatus Status, int Accepted, int Skipped, string Detail)
{
    public static RatingPushResult Ok(int accepted, int skipped) =>
        new(RatingPushStatus.Ok, accepted, skipped, string.Empty);
    public static RatingPushResult Unreachable(string detail) =>
        new(RatingPushStatus.Unreachable, 0, 0, detail);
    public static RatingPushResult Rejected(string detail) =>
        new(RatingPushStatus.Rejected, 0, 0, detail);
}

/// <summary>
/// Two-way bridge between ClashEngine's local <see cref="IRatingStore"/> and the stats server.
/// Implementations are expected to be HTTP-backed in production and stubbed for tests / for
/// the no-stats-server fallback.
/// </summary>
/// <remarks>
/// <para><b>Pull side:</b> <see cref="TryPullAsync"/> fetches one <c>(player, gameType)</c>
/// row, returning <see langword="null"/> when the server has no row for that pair.
/// <see cref="RatingSyncCoordinator"/> calls this per registered gametype on player connect to
/// seed the local cache with the server's authoritative state.</para>
///
/// <para><b>Push side:</b> <see cref="TryPushBatchAsync"/> ships an entire batch of
/// (player, gameType, rating) tuples to the bulk endpoint. The stats server treats each
/// entry independently and uses <see cref="Rating.LastSeen"/> as the <c>updatedAt</c> guard,
/// so resending an unchanged row is a no-op rather than an error -- the coordinator pushes
/// every row a disconnecting player owns without filtering for "what changed".</para>
///
/// <para>Implementations must never throw from these methods; transport failures should be
/// captured into <see cref="RatingPushStatus.Unreachable"/> / <see langword="null"/> pulls
/// and logged inside the implementation.</para>
/// </remarks>
public interface IRatingSync
{
    /// <summary>
    /// Fetch one <c>(player, gameType)</c> rating from the stats server. Returns
    /// <see langword="null"/> when the server has no stored row (a 200 response with null
    /// fields per the contract) or when the call could not be completed -- the coordinator
    /// treats both as "no remote state, keep whatever the local cache had".
    /// </summary>
    Task<Rating?> TryPullAsync(string playerName, string gameType, CancellationToken ct = default);

    /// <summary>
    /// Push a batch of rating snapshots to <c>POST /api/ratings</c>. The server upserts each
    /// entry independently using <c>updatedAt</c> as the guard; <c>accepted</c> in the
    /// returned <see cref="RatingPushResult"/> is the count the server actually stored.
    /// </summary>
    Task<RatingPushResult> TryPushBatchAsync(
        IReadOnlyList<RatingEntry> entries, CancellationToken ct = default);
}
