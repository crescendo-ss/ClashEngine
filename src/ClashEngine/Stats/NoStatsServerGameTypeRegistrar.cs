using System.Threading;
using System.Threading.Tasks;
using ClashEngine.Core.GameType;
using SS.Core.ComponentInterfaces;

namespace ClashEngine.Stats;

/// <summary>
/// Fail-closed <see cref="IGameTypeRegistrar"/> used when no <c>[ClashEngine] UploadUrl</c>
/// is configured. Every registration attempt is reported as
/// <see cref="RegistrationStatus.Unreachable"/> so the host's policy (which says "only
/// stats-server-accepted gametypes appear in the registry") drops the gametype with a warn
/// and skips any queues that referenced it.
/// </summary>
/// <remarks>
/// The constructor logs a one-time WARN so operators see the consequence of leaving
/// <c>UploadUrl</c> unset: no gametypes, no queues, no matches. This mirrors the
/// <see cref="JsonFileMatchUploader"/> fallback note for the match-upload path, but the
/// gametype path is strictly stricter -- there is no local-file fallback that could
/// substitute for stats-server validation.
/// </remarks>
internal sealed class NoStatsServerGameTypeRegistrar : IGameTypeRegistrar
{
    private const string LogCategory = nameof(NoStatsServerGameTypeRegistrar);

    public NoStatsServerGameTypeRegistrar(ILogManager log)
    {
        log?.LogM(LogLevel.Warn, LogCategory,
            "No [ClashEngine] UploadUrl configured; gametype registration cannot reach a stats server. " +
            "Every parsed gametype will be dropped (fail-closed), and queues referencing them will be skipped.");
    }

    public Task<RegistrationResult> TryRegisterAsync(
        GameTypeRegistration registration, CancellationToken ct = default) =>
        Task.FromResult(RegistrationResult.Unreachable("no UploadUrl configured"));
}
