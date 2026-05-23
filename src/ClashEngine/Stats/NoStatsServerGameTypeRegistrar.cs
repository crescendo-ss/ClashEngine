using System.Threading;
using System.Threading.Tasks;
using ClashEngine.Core.GameType;
using SS.Core.ComponentInterfaces;

namespace ClashEngine.Stats;

/// <summary>
/// Local-only <see cref="IGameTypeRegistrar"/> used when no <c>[ClashEngine] UploadUrl</c>
/// is configured. Every gametype is reported as <see cref="RegistrationStatus.Accepted"/>
/// so the engine's local <c>GameTypeRegistry</c> (and queues that reference it) work
/// without a stats server.
/// </summary>
/// <remarks>
/// <para>This is the deliberate split between two failure modes the host treats differently:</para>
/// <list type="bullet">
///   <item><b>Server configured but rejecting</b> (4xx) or <b>unreachable</b> (5xx, network):
///         <see cref="HttpGameTypeRegistrar"/> reports <see cref="RegistrationStatus.Rejected"/>
///         / <see cref="RegistrationStatus.Unreachable"/> and the host drops the gametype --
///         the operator opted into stats-server validation, so we honor that contract.</item>
///   <item><b>No server configured at all</b> (<c>UploadUrl</c> unset): the operator
///         explicitly opted out of stats-server validation. Treat parsed gametypes as
///         accepted so the engine runs in local-only mode -- match envelopes still write to
///         JSON files via <see cref="JsonFileMatchUploader"/>, ratings still persist via
///         <see cref="Persistence.PersistRatingStore"/>, and ?play / matches / scoring all
///         work end-to-end.</item>
/// </list>
///
/// <para>The constructor logs a one-time INFO line so operators see clearly which mode
/// they're in when the engine loads.</para>
/// </remarks>
internal sealed class NoStatsServerGameTypeRegistrar : IGameTypeRegistrar
{
    private const string LogCategory = nameof(NoStatsServerGameTypeRegistrar);

    public NoStatsServerGameTypeRegistrar(ILogManager log)
    {
        log?.LogM(LogLevel.Info, LogCategory,
            "No [ClashEngine] UploadUrl configured; running in local-only mode. " +
            "Parsed gametypes are accepted into the local registry without server validation; " +
            "match envelopes will write to JSON files (see JsonFileMatchUploader).");
    }

    public Task<RegistrationResult> TryRegisterAsync(
        GameTypeRegistration registration, CancellationToken ct = default) =>
        Task.FromResult(RegistrationResult.Accepted());
}
