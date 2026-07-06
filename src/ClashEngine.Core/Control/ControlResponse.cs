using System;
using System.Collections.Generic;

namespace ClashEngine.Core.Control;

/// <summary>
/// The reply to one <see cref="ControlCommand"/>: a serialize-only DTO matching
/// <c>schema/commandresponse.schema.json</c> 1:1 (keep in sync). <see cref="Status"/> carries the
/// semantic outcome (<see cref="ControlStatus"/>), <see cref="Result"/> the machine-readable
/// reason (<see cref="ControlResults"/>); <see cref="Detail"/> is human-readable elaboration
/// only. Optional fields stay null and are dropped on the wire by the host's
/// <c>WhenWritingNull</c> policy.
/// </summary>
public sealed record ControlResponse(
    int SchemaVersion,
    string? CommandId,
    string Status,
    string? Result = null,
    string? Detail = null,
    string? Player = null,
    Guid? MatchId = null,
    IReadOnlyList<string>? RemovedQueues = null);

/// <summary>The <see cref="ControlResponse.Status"/> strings.</summary>
public static class ControlStatus
{
    /// <summary>The desired state now holds (including idempotent no-ops).</summary>
    public const string Ok = "ok";

    /// <summary>The engine refused the change; <see cref="ControlResponse.Result"/> says why.</summary>
    public const string Rejected = "rejected";

    /// <summary>The command itself was malformed; nothing was attempted.</summary>
    public const string Error = "error";
}

/// <summary>The <see cref="ControlResponse.Result"/> strings emitted in v1. Wire strings are
/// decoupled from the C# enum identifiers so renaming an engine enum can't silently change the
/// contract.</summary>
public static class ControlResults
{
    // status=ok
    public const string Queued = "queued";
    public const string AlreadyQueued = "already_queued";
    public const string AlreadyQueuedRefreshed = "already_queued_refreshed";
    public const string Dequeued = "dequeued";
    public const string MatchFormed = "match_formed";
    public const string MatchCancelled = "match_cancelled";
    public const string AutoSet = "auto_set";

    // status=rejected
    public const string UnknownQueue = "unknown_queue";
    public const string NotConnected = "not_connected";
    public const string InMatch = "in_match";
    public const string InTimeout = "in_timeout";
    public const string GroupTooLarge = "group_too_large";
    public const string ShapeMismatch = "shape_mismatch";
    public const string DuplicatePlayer = "duplicate_player";
    public const string UnknownMatch = "unknown_match";
    public const string NotForming = "not_forming";

    // status=error
    public const string UnsupportedSchemaVersion = "unsupported_schema_version";
    public const string UnknownType = "unknown_type";
    public const string MissingField = "missing_field";
    public const string InvalidField = "invalid_field";
    public const string InternalError = "internal_error";
}
