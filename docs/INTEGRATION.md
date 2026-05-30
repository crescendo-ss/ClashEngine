# ClashEngine integration surface

ClashEngine's data shapes are stable wire contracts. If you want to build a
visualizer, dashboard, alternate stats backend, or a live notifier (e.g. a
Discord bot), you don't need to fork the host project — implement the four Core
interfaces below against your own transport, and the engine will talk to your
code instead of (or alongside) the bundled HTTP implementations.

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

**Future extensions (not in v1):** the stream does not yet emit queue
created/removed (hot-reload) lifecycle events, periodic full-state snapshots,
or social/penalty events (group invites, abandonment, griefing, KOTH winner
promotion, team-collapse). Those telemetry events exist and could be mapped
later; the v1 stream covers queue membership, match lifecycle, and the Discord
link relay.

Replay files (`.replay`) are emitted alongside the match envelope. They are
captured by the host's `ClashReplayRecorder` and referenced by
`match.recordingPath`. If your backend needs the binary, the standard pattern
is to attach it as a multipart form field on the match POST — see
`HttpMatchUploader` for how the bundled impl does it.

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

- **Matchmaking** (team balance, partition quality, look-ahead, hold windows)
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
