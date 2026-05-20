using System.Threading;
using System.Threading.Tasks;

namespace ClashEngine.Core.GameType;

/// <summary>
/// Result of a single gametype-registration attempt against the stats server. Drives the
/// "fail-closed" policy in <see cref="GameTypeRegistry"/>: only <see cref="Accepted"/>
/// gametypes are committed to the local registry; rejected and unreachable gametypes are
/// dropped, and queues referencing them are skipped with a warn.
/// </summary>
public enum RegistrationStatus
{
    /// <summary>The stats server accepted the POST (2xx). The gametype may be committed to
    /// the local registry and used to back queues.</summary>
    Accepted,

    /// <summary>The stats server rejected the POST (4xx). Typically an originArena change
    /// for an existing name (403) or a malformed body (400). The gametype is dropped and
    /// will not be retried by the engine -- the operator must fix the conf or coordinate
    /// with the stats server.</summary>
    Rejected,

    /// <summary>The stats server could not be reached (network error, timeout, 5xx). The
    /// gametype is dropped for this load pass; a subsequent reload (or a manual retry) will
    /// re-attempt registration.</summary>
    Unreachable,
}

/// <summary>
/// Outcome envelope from a registration attempt. <see cref="Detail"/> is best-effort human
/// readable text suitable for logging; it is empty when the status is <see cref="RegistrationStatus.Accepted"/>.
/// </summary>
public readonly record struct RegistrationResult(RegistrationStatus Status, string Detail)
{
    public static RegistrationResult Accepted() => new(RegistrationStatus.Accepted, string.Empty);
    public static RegistrationResult Rejected(string detail) => new(RegistrationStatus.Rejected, detail);
    public static RegistrationResult Unreachable(string detail) => new(RegistrationStatus.Unreachable, detail);
}

/// <summary>
/// Sink for gametype registration POSTs. <see cref="ClashEngine.ClashModule"/> invokes this
/// once per parsed <see cref="GameTypeDef"/> before committing the gametype to
/// <see cref="GameTypeRegistry"/>. Queue parsing is gated on the results so a queue cannot
/// reference an unregistered (or rejected) gametype.
/// </summary>
public interface IGameTypeRegistrar
{
    Task<RegistrationResult> TryRegisterAsync(GameTypeRegistration registration, CancellationToken ct = default);
}
