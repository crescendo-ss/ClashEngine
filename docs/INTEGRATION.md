# ClashEngine integration surface

ClashEngine's data shapes are stable wire contracts. If you want to build a
visualizer, dashboard, alternate stats backend, or a live notifier (e.g. a
Discord bot), you don't need to fork the host project — implement the four Core
interfaces below against your own transport, and the engine will talk to your
code instead of (or alongside) the bundled HTTP implementations. For a service
that also needs to *drive* matchmaking (a web UI or Discord bot where players
queue and start private matches by clicking buttons), the engine additionally
hosts an inbound HTTP [control surface](#the-control-surface-inbound).

All four interfaces live in the `ClashEngine.Core` assembly. The DTOs they
shuttle match the schema files in [`schema/`](../schema/) 1:1. Read the schemas
for the wire format; read the interfaces for the engine-facing API.

## Wire formats

| Schema file | Direction | What it carries |
|---|---|---|
| [`schema/match.schema.json`](../schema/match.schema.json) | Outgoing | One finalized match envelope per game: metadata, ranked teams, every participant's stats, optional replay pointer. |
| [`schema/gametype.schema.json`](../schema/gametype.schema.json) | Outgoing | One gametype registration per parsed `[ClashEngine]` block: name, label, description, shape metadata, origin arena. |
| [`schema/rating.schema.json`](../schema/rating.schema.json) | Incoming | One `(player, gameType)` rating row: μ, σ, gamesPlayed, updatedAt. |
| [`schema/event.schema.json`](../schema/event.schema.json) | Outgoing | A normalized stream of queue-membership, match-lifecycle, and player events for live advertising/notification (e.g. a Discord bot). Keyed by in-game player name. |
| [`schema/command.schema.json`](../schema/command.schema.json) | Incoming | One control command: enqueue/dequeue a player (or party), form a private match from self-organized teams, cancel a Forming match, set the auto-requeue preference. |
| [`schema/commandresponse.schema.json`](../schema/commandresponse.schema.json) | Outgoing (reply) | The per-command outcome envelope: `ok` / `rejected` / `error` plus a machine-readable result and, where relevant, the blocking player or the new match id. |
| [`schema/snapshot.schema.json`](../schema/snapshot.schema.json) | Outgoing (on demand) | Full state: every queue with waiters, every in-flight match, every active penalty timeout. The self-heal companion to the event stream. |

## Engine integration interfaces

| Interface | Path | Direction | Triggered by |
|---|---|---|---|
| [`IMatchUploader`](../src/ClashEngine.Core/Stats/IMatchUploader.cs) | `ClashEngine.Core.Stats` | Push (fire-and-forget) | Every finalized match. |
| [`IGameTypeRegistrar`](../src/ClashEngine.Core/GameType/IGameTypeRegistrar.cs) | `ClashEngine.Core.GameType` | Push (with accept/reject) | Once per parsed gametype on every config load / hot reload. |
| [`IRatingsProvider`](../src/ClashEngine.Core/Ratings/IRatingsProvider.cs) | `ClashEngine.Core.Ratings` | Pull (per gametype) | Per registered gametype on every player connect (deduplicated). |
| [`IEventSink`](../src/ClashEngine.Core/Events/IEventSink.cs) | `ClashEngine.Core.Events` | Push (fire-and-forget) | Queue join/leave (with reason), near-full, AFK dwell warnings, match teams-locked/started/ended, and `?connect discord` link requests. |

The bundled host implementations live under `src/ClashEngine/Stats/` and
`src/ClashEngine/Events/` and serve as worked examples:

- `HttpMatchUploader` + `JsonFileMatchUploader` (push to HTTP or write to disk)
- `HttpGameTypeRegistrar` + `NoStatsServerGameTypeRegistrar` (HTTP POST or
  local-only fail-open)
- `HttpRatingsProvider` + `NoStatsServerRatingsProvider` (HTTP GET or no-op)
- `HttpEventSink` (push to HTTP) + `NoOpEventSink` (drop; the default when no
  `EventStreamUrl` is configured)

The event stream is the only edge whose mapping logic is itself in Core and
unit-tested: `EventStreamTelemetry` (an `IMatchmakingTelemetry`) translates
engine telemetry into `EventEnvelope`s for the sink. The engine is
identity-agnostic — events carry in-game player names, and any Discord-account
link and per-player opt-in live entirely in the consuming service. The
`?connect discord <alias>` command is a pure relay: it emits a
`player.discord_link_requested` event and stores nothing.

**Queue-timeout penalties** surface as `player.penalized` (`penaltyReason`
`abandonment` — covering the abandonment and staging-AFK ladders — or
`griefing`, with `penaltyUntil` and, for abandonment, `offenseCount`). Only a
*confirmed* griefing penalty is emitted, so a later veto never leaves a phantom
timeout on the wire. The timeout is assessed and escalated regardless of
`Queue<i>IgnorePenalties`; that flag only governs whether a queue *admits* a
penalized player.

**Future extensions (not yet emitted):** queue created/removed (hot-reload)
lifecycle events, periodic full-state snapshots, and the remaining social
events (group invites, KOTH winner promotion, team-collapse, veto progress).
Those telemetry events exist and could be mapped later; the stream currently
covers queue membership, match lifecycle, queue-timeout penalties, and the
Discord link relay.

Replay files (`.replay`) are emitted alongside the match envelope. They are
captured by the host's `ClashReplayRecorder` and referenced by
`match.recordingPath`. If your backend needs the binary, the standard pattern
is to attach it as a multipart form field on the match POST — see
`HttpMatchUploader` for how the bundled impl does it.

## The control surface (inbound)

The event stream broadcasts state *out*; the control surface lets your service
change it. Unlike the four interfaces above it is not something you implement —
ClashEngine hosts it: set `ControlListenUrl` (+ `ControlApiKey`, falling back
to `UploadApiKey`) under `[ClashEngine]` in global.conf and the plug-in serves
two `X-Api-Key`-authenticated routes:

- `POST <prefix>commands` — one [`command.schema.json`](../schema/command.schema.json)
  body per request, answered with
  [`commandresponse.schema.json`](../schema/commandresponse.schema.json).
- `GET <prefix>state` — the full
  [`snapshot.schema.json`](../schema/snapshot.schema.json) snapshot. Rebuild
  your world from this on (re)connect, then apply the event stream
  incrementally (it drops on overflow by design).

The whole REST contract is also published as an OpenAPI 3.1 document,
[`schema/control.openapi.yaml`](../schema/control.openapi.yaml), which `$ref`s
the JSON Schema files above (they remain the source of truth). Use it for
client codegen, Swagger UI, or contract tests; its `info.description` also
spells out the status-code semantics (HTTP status is transport-only — a
`rejected` command is still a 200).

Command → engine mapping is a Core, unit-tested component
(`ControlCommandDispatcher` in `ClashEngine.Core.Control`, the inbound mirror
of `EventStreamTelemetry`), so the text `?commands` and the HTTP surface are
two thin front-ends over the same engine calls — identical validation,
identical telemetry, no behavioral drift. The engine stays the authority on
validity: a command from your service can still come back `rejected` (player
disconnected, in a match, serving a penalty, roster doesn't fit the shape),
with the blocking player named.

`form_match` is the private-matchmaking seam: your service collects a full
self-organized roster (e.g. 8 players arranged as two 4s), then starts the
match directly — no queue, no matcher — anchored on a configured queue whose
game type, arena, and rules it borrows. Everything downstream (staging,
countdown, stats, ratings per the anchor queue's `RatingWeight`, upload,
events) runs exactly as for a matcher-formed match; see the README's *Control
surface* section for the semantic details (queue-membership sweep/restore, no
KOTH/auto re-adds, blameless `cancel_match`).

## Example: a custom `IRatingsProvider`

Suppose your visualizer keeps a local SQLite snapshot of every player's
rating and you want ClashEngine to seed new sessions from it instead of
hitting your HTTP endpoint. Implement the one method:

```csharp
using System.Threading;
using System.Threading.Tasks;
using ClashEngine.Core.Ratings;
using Microsoft.Data.Sqlite;

public sealed class SqliteRatingsProvider : IRatingsProvider
{
    private readonly string _connStr;

    public SqliteRatingsProvider(string connectionString) =>
        _connStr = connectionString;

    public async Task<Rating?> TryPullAsync(
        string playerName, string gameType, CancellationToken ct = default)
    {
        try
        {
            await using var conn = new SqliteConnection(_connStr);
            await conn.OpenAsync(ct).ConfigureAwait(false);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText =
                @"SELECT mu, sigma, gamesPlayed, updatedAt
                  FROM playerRatings
                  WHERE playerName = $name COLLATE NOCASE AND gameType = $gt";
            cmd.Parameters.AddWithValue("$name", playerName);
            cmd.Parameters.AddWithValue("$gt", gameType);

            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (!await reader.ReadAsync(ct).ConfigureAwait(false)) return null;

            return new Rating(
                Mu: reader.GetDouble(0),
                Sigma: reader.GetDouble(1),
                GamesPlayed: (uint)reader.GetInt64(2),
                LastSeen: reader.GetDateTime(3));
        }
        catch
        {
            // Contract: never throw. A transport failure surfaces as null and the
            // coordinator leaves the local cache untouched.
            return null;
        }
    }
}
```

Wire it into the host by replacing the call to `BuildRatingsProvider()` in
`ClashModule.cs` (or by writing a small SS module that registers your impl
in place of the default).

## What is NOT extensible

The integration surface is the I/O edge only. The engine's internals are
not pluggable:

- **Matchmaking** (team balance, partition quality, look-ahead, hold windows).
  An external service can *bypass* the matcher for a specific match via the
  control surface's `form_match`, but the matcher's own behavior is not
  pluggable.
- **Scoring** (OpenSkill updates, margin-of-victory weighting, per-player
  weights)
- **Penalties** (abandonment ladders, griefing detection, vetoes)
- **Match lifecycle** (Forming → Live → Completed/Abandoned/Cancelled, the
  staging/countdown phases, knockout-spec delay)
- **Replay capture** (the binary `.replay` format and the `MatchRecorder`)
- **Persistence** (`PersistRatingStore` / `PersistPenaltyStore` — the on-disk
  blob format is the engine's internal cache, not an integration point)

If you need to influence any of those, the answer is a patch against the
engine itself, not a custom provider.
