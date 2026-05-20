using System.Threading;
using System.Threading.Tasks;

namespace ClashEngine.Core.Ratings;

/// <summary>
/// One-way pull bridge from the stats server into ClashEngine's local <see cref="IRatingStore"/>.
/// Implementations are expected to be HTTP-backed in production and stubbed for tests / for
/// the no-stats-server fallback.
/// </summary>
/// <remarks>
/// <para>This abstraction is intentionally <i>pull-only</i>. The primary write path back to
/// the stats server is the match-upload envelope (<see cref="MatchPayload"/>): every
/// finalized match carries each participant's post-match rating, so the server's
/// <c>playerRatings</c> table stays current as a side effect of normal play. The pull side
/// exists only to seed a player's local cache the first time they connect to a zone that
/// has no persisted rating for them -- typically a new install, a fresh PersistRatingStore,
/// or a player who has played on a different zone instance.</para>
///
/// <para>There is intentionally no push API here. A separate
/// <c>ratings-batch.schema.json</c> documents the bulk-push wire shape for a future durable
/// upload-queue feature, but until that lands the engine relies on the match-envelope
/// channel + local PersistRatingStore. See the commit history for the rationale.</para>
///
/// <para>Implementations must never throw from <see cref="TryPullAsync"/>; transport
/// failures should be captured into a <see langword="null"/> return and logged inside the
/// implementation. The coordinator treats <see langword="null"/> as "no remote state, keep
/// whatever the local cache had" (which for a never-seen player is <see cref="Rating.Default"/>).</para>
/// </remarks>
public interface IRatingSync
{
    /// <summary>
    /// Fetch one <c>(player, gameType)</c> rating from the stats server. Returns
    /// <see langword="null"/> when the server has no stored row (a 200 response with null
    /// fields per the contract) or when the call could not be completed. The coordinator
    /// only invokes this when the local cache has no rows at all for the player; a null
    /// response leaves the cache empty, which surfaces <see cref="Rating.Default"/> on
    /// subsequent <see cref="IRatingStore.Get"/> reads.
    /// </summary>
    Task<Rating?> TryPullAsync(string playerName, string gameType, CancellationToken ct = default);
}
