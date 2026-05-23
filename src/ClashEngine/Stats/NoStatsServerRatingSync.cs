using System.Threading;
using System.Threading.Tasks;
using ClashEngine.Core.Ratings;
using SS.Core.ComponentInterfaces;

namespace ClashEngine.Stats;

/// <summary>
/// Fallback <see cref="IRatingSync"/> used when no <c>[ClashEngine] UploadUrl</c> is
/// configured. Pull returns <see langword="null"/>, so the coordinator leaves a never-seen
/// player on <see cref="Rating.Default"/> until they play their first match.
/// </summary>
/// <remarks>
/// Construction logs a one-time WARN. Operators running without a stats server still get a
/// functional engine -- matches form, ratings persist locally via
/// <see cref="Persistence.PersistRatingStore"/>, and new players start at default.
/// </remarks>
internal sealed class NoStatsServerRatingSync : IRatingSync
{
    private const string LogCategory = nameof(NoStatsServerRatingSync);

    public NoStatsServerRatingSync(ILogManager log)
    {
        log?.LogM(LogLevel.Info, LogCategory,
            "No [ClashEngine] UploadUrl configured; rating pull-on-connect is inert. " +
            "Ratings persist locally via PersistRatingStore and update as matches finish -- " +
            "the only thing missing is cross-zone reconciliation.");
    }

    public Task<Rating?> TryPullAsync(string playerName, string gameType, CancellationToken ct = default) =>
        Task.FromResult<Rating?>(null);
}
