using System.Threading;
using System.Threading.Tasks;

namespace ClashEngine.Core.Ratings;

/// <summary>
/// Integration surface for incoming ratings. Implement this interface to plug a custom
/// backend (visualizer, dashboard, alternate stats server, offline file cache, ...) into
/// ClashEngine. The Core DTOs (<see cref="Rating"/>) match <c>schema/rating.schema.json</c>
/// 1:1 -- a consumer who references <c>ClashEngine.Core</c> and reads that schema has
/// everything needed to write an alternate implementation without forking the host project.
/// </summary>
/// <remarks>
/// <para>See also <see cref="ClashEngine.Core.GameType.IGameTypeRegistrar"/> (outgoing
/// gametype registrations, <c>schema/gametype.schema.json</c>) and
/// <see cref="Stats.IMatchUploader"/> (outgoing match envelopes,
/// <c>schema/match.schema.json</c>) for the other halves of the integration surface. Those
/// two are push, this one is pull -- together they describe the full edge between
/// ClashEngine and whatever sits behind it.</para>
///
/// <para>This abstraction is intentionally <i>pull-only</i>. The primary write path back to
/// the stats server is the match-upload envelope (<see cref="Stats.MatchPayload"/>): every
/// finalized match carries each participant's post-match rating, so the server's
/// <c>playerRatings</c> table stays current as a side effect of normal play. The pull side
/// exists only to seed a player's local cache the first time they connect to a zone that
/// has no persisted rating for them -- typically a new install, a fresh PersistRatingStore,
/// or a player who has played on a different zone instance.</para>
///
/// <para>There is intentionally no push API here. The engine relies on the match-envelope
/// channel + the local rating store to keep the server's <c>playerRatings</c> table in
/// sync; see the commit history for the rationale.</para>
///
/// <para>Implementations must never throw from <see cref="TryPullAsync"/>; transport
/// failures should be captured into a <see langword="null"/> return and logged inside the
/// implementation. The coordinator treats <see langword="null"/> as "no remote state, keep
/// whatever the local cache had" (which for a never-seen player is <see cref="Rating.Default"/>).</para>
/// </remarks>
public interface IRatingsProvider
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
