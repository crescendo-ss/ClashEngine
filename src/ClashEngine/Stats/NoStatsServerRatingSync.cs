using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ClashEngine.Core.Ratings;
using SS.Core.ComponentInterfaces;

namespace ClashEngine.Stats;

/// <summary>
/// Fallback <see cref="IRatingSync"/> used when no <c>[ClashEngine] UploadUrl</c> is
/// configured. Pull returns <see langword="null"/> (so the coordinator keeps whatever the
/// local persist cache has); push reports <see cref="RatingPushStatus.Unreachable"/>
/// without any HTTP attempt so logs make it clear the data has not actually been backed up
/// off-box.
/// </summary>
/// <remarks>
/// Construction logs a one-time WARN. Operators running without a stats server still get a
/// functional engine (matches form, ratings persist locally via <see cref="Persistence.PersistRatingStore"/>),
/// but the cross-instance reconciliation the sync was designed for is inert.
/// </remarks>
internal sealed class NoStatsServerRatingSync : IRatingSync
{
    private const string LogCategory = nameof(NoStatsServerRatingSync);

    public NoStatsServerRatingSync(ILogManager log)
    {
        log?.LogM(LogLevel.Warn, LogCategory,
            "No [ClashEngine] UploadUrl configured; rating pull-on-connect and push-on-disconnect are inert. " +
            "Local PersistRatingStore still works, but ratings will not reconcile across zone instances.");
    }

    public Task<Rating?> TryPullAsync(string playerName, string gameType, CancellationToken ct = default) =>
        Task.FromResult<Rating?>(null);

    public Task<RatingPushResult> TryPushBatchAsync(
        IReadOnlyList<RatingEntry> entries, CancellationToken ct = default) =>
        Task.FromResult(RatingPushResult.Unreachable("no UploadUrl configured"));
}
