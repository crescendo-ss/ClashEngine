namespace ClashEngine.Core.GameType;

/// <summary>
/// Request body for the stats server's <c>POST /api/gametypes</c> endpoint. Mirrors
/// <c>schema/gametype.schema.json</c> exactly: the wire format the consumer ingests to
/// register or update a GameType. Built from <see cref="GameTypeDef"/> + the arena posting it.
/// </summary>
/// <remarks>
/// <para>The server stamps a version on each accepted POST (clients do not pick the version).
/// First POST for a name locks in <see cref="OriginArena"/> permanently; subsequent POSTs from
/// the same arena append a new version; from a different arena, the server rejects with 403.</para>
/// <para>Property names are PascalCase here; the HTTP serializer flips them to camelCase to
/// match the schema before sending.</para>
/// </remarks>
public sealed record GameTypeRegistration(
    string Name,
    string Label,
    string? Description,
    GameTypeMetadata Metadata,
    string OriginArena);
