using System;
using System.Collections.Generic;

namespace ClashEngine.Core.Events;

/// <summary>
/// One outbound event in the normalized stream. A flat, serialize-only DTO: only one of
/// <see cref="Queue"/> / <see cref="Match"/> is populated per event (the other stays null and is
/// dropped on the wire by the HTTP sink's <c>WhenWritingNull</c> policy). The wire shape is
/// described by <c>schema/event.schema.json</c>; keep the two in sync 1:1.
/// </summary>
/// <remarks>
/// We never deserialize these in this repo, so there is no need for System.Text.Json polymorphic
/// type discrimination -- the <see cref="Type"/> string is the discriminator and the consumer owns
/// its own DTOs against the schema.
/// </remarks>
public sealed record EventEnvelope(
    int SchemaVersion,
    string Type,
    DateTimeOffset OccurredAt,
    QueueEventPayload? Queue = null,
    MatchEventPayload? Match = null,
    PlayerEventPayload? Player = null);

/// <summary>
/// Payload for <c>queue.*</c> events. Every queue event carries enough state
/// (<see cref="QueueName"/> / <see cref="QueueLabel"/> / <see cref="GameType"/> /
/// <see cref="Count"/> / <see cref="Capacity"/>) for a consumer to render or edit a live "queue
/// board" message from any single event. <see cref="Count"/> always reflects the queue's
/// occupancy AFTER the change this event describes.
/// </summary>
public sealed record QueueEventPayload(
    string QueueName,
    string QueueLabel,
    string? GameType,
    int Count,
    int Capacity,
    string? Player = null,
    string? Reason = null,
    IReadOnlyList<string>? Waiting = null,
    double? DwellSeconds = null);

/// <summary>Payload for <c>match.*</c> events. <see cref="MatchId"/> is absent on
/// <c>match.teams_locked</c> (the proposal carries no id yet) and present on
/// <c>match.started</c> / <c>match.ended</c>, which a consumer can correlate by it.
/// <see cref="GameLabel"/> is the game type's human-readable label (e.g. "Elimination");
/// it is optional/nullable, and a consumer falls back to <see cref="GameType"/> when it is
/// absent.</summary>
public sealed record MatchEventPayload(
    Guid? MatchId,
    string GameType,
    string? GameLabel = null,
    string? QueueName = null,
    string? QueueLabel = null,
    string? Arena = null,
    IReadOnlyList<EventRankedTeam>? Teams = null,
    string? FinalState = null,
    DateTimeOffset? StartedAt = null,
    DateTimeOffset? EndedAt = null,
    double? DurationSeconds = null,
    IReadOnlyList<string>? AbandonedBy = null);

/// <summary>
/// Payload for <c>player.*</c> events. Carries the in-game player name and, for
/// <c>player.discord_link_requested</c>, the Discord alias the player asked to be linked with.
/// ClashEngine neither stores nor validates the alias — the external consumer performs the link.
/// </summary>
public sealed record PlayerEventPayload(string Player, string? DiscordAlias = null);

/// <summary>One team's roster in a match event. <see cref="Rank"/> is 1-based; for
/// <c>match.teams_locked</c>/<c>match.started</c> it's the team index +1 with <see cref="Score"/>
/// 0 (no result yet), and for <c>match.ended</c> it carries the final placement and score.</summary>
public sealed record EventRankedTeam(int Rank, int Score, IReadOnlyList<string> Players);

/// <summary>Wire-format version of the event stream. A brand-new contract, independent of the
/// match-upload schema version. Increment on any breaking change to the envelope or payloads.</summary>
public static class EventSchema
{
    public const int Version = 1;
}

/// <summary>The <see cref="EventEnvelope.Type"/> discriminator strings emitted in v1.</summary>
public static class ClashEventTypes
{
    public const string QueueJoined = "queue.joined";
    public const string QueueLeft = "queue.left";
    public const string QueueNearFull = "queue.near_full";
    public const string QueueDwellWarning = "queue.dwell_warning";
    public const string MatchTeamsLocked = "match.teams_locked";
    public const string MatchStarted = "match.started";
    public const string MatchEnded = "match.ended";
    public const string PlayerDiscordLinkRequested = "player.discord_link_requested";
}
