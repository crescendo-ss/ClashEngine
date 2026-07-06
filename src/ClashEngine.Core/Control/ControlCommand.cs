using System;
using System.Collections.Generic;

namespace ClashEngine.Core.Control;

/// <summary>
/// One inbound control command from an external matchmaking service (web UI, Discord bot): the
/// write half of the control surface, deserialized from the body of a POST to the host's control
/// listener. The wire shape is described by <c>schema/command.schema.json</c>; keep the two in
/// sync 1:1. Every field beyond <see cref="SchemaVersion"/>/<see cref="Type"/> is nullable --
/// which ones are required depends on <see cref="Type"/>, and
/// <see cref="ControlCommandDispatcher"/> owns that validation (a malformed command yields a
/// <c>status=error</c> response, never an exception).
/// </summary>
public sealed record ControlCommand(
    int SchemaVersion,
    string? Type,
    string? CommandId = null,
    string? Player = null,
    IReadOnlyList<string>? Players = null,
    string? Queue = null,
    IReadOnlyList<IReadOnlyList<string>>? Teams = null,
    Guid? MatchId = null,
    bool? Enabled = null);

/// <summary>Wire-format version of the control surface (commands, responses, and the state
/// snapshot). Independent of the match-upload and event-stream schema versions. Increment on any
/// breaking change.</summary>
public static class ControlSchema
{
    public const int Version = 1;
}

/// <summary>The <see cref="ControlCommand.Type"/> discriminator strings accepted in v1.</summary>
public static class ControlCommandTypes
{
    public const string Enqueue = "enqueue";
    public const string EnqueueGroup = "enqueue_group";
    public const string Dequeue = "dequeue";
    public const string FormMatch = "form_match";
    public const string CancelMatch = "cancel_match";
    public const string SetAuto = "set_auto";
}
