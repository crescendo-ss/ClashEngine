# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

ClashEngine is a skill-based matchmaking engine packaged as a [SubspaceServer .NET](https://github.com/gigamon-dev/SubspaceServer) plug-in. It forms balanced matches from player queues, runs them through a staging → countdown → live → cleanup lifecycle, tracks per-player stats, and updates persistent OpenSkill ratings. `README.md` is the authoritative reference for the `[ClashEngine]` config surface and player commands — consult it for any config-key question; this file covers architecture and workflow.

## Build & test

Targets **.NET 10** (`net10.0`). Nullable and ImplicitUsings are on; `TreatWarningsAsErrors` is off (set in `Directory.Build.props`). Solution file is `ClashEngine.slnx` (the newer XML solution format).

```sh
# Full solution — REQUIRES a sibling SubspaceServer checkout (see below)
dotnet build

# Core library + its tests build/run with NO SubspaceServer dependency
dotnet build src/ClashEngine.Core/ClashEngine.Core.csproj
dotnet test  tests/ClashEngine.Core.Tests/ClashEngine.Core.Tests.csproj

# A single test class / method (xUnit filter)
dotnet test tests/ClashEngine.Core.Tests/ClashEngine.Core.Tests.csproj --filter "FullyQualifiedName~MatcherTests"
dotnet test tests/ClashEngine.Core.Tests/ClashEngine.Core.Tests.csproj --filter "FullyQualifiedName~MatcherTests.Enqueue_into_known_queue_succeeds_and_indexes"
```

- The **plug-in** project (`src/ClashEngine`) references SubspaceServer's `Core`, `Utilities`, `Packets`, `Replay`, and `Matchmaking` csproj's via a relative path, defaulting to `../SubspaceServer/src/`. Override with `-p:SubspaceServerRoot=<path-to-src>` if your layout differs. SS references use `ExcludeAssets=all` / `Private=false` — they're compile-time only; the running server already provides those assemblies.
- The **Core** project and its **test** project have no SS dependency — prefer building/testing those alone when your change is engine-only (faster, no SS checkout needed).
- Build output lands in `SubspaceServer/Zone/bin/modules/ClashEngine/` inside this repo, and a post-build step (`DeployToSubspaceServer`) copies it to the sibling SS zone for live deployment.
- **MSB3021 / MSB3027 "cannot copy ... being used by another process" is usually NOT a real failure** — it means compilation succeeded but the deploy-copy was blocked because a SubspaceServer instance has the old DLL loaded. Stop the server (or ignore if you only needed the compile) rather than treating it as a build break.

### End-to-end regression testing — the ClashRig repo

The `tests/` here are pure-Core unit tests. **End-to-end / CI regression testing lives in the sibling [`ClashRig`](../ClashRig) repo.** It boots a real SubspaceServer with this plug-in, drives headless `zero` bots through matchmaking scenarios (happy-path completion, abandon/cancel/decline/veto, negative config, ratings pull) over a live control channel, and asserts on the gameplay plumbing + the three HTTP edges (see *Integration surface* below) against a fake stats server — coverage the in-repo unit tests can't reach. ClashRig pins this repo and builds it against **vanilla** `gigamon-dev/SubspaceServer` (its minimal module set sidesteps upstream's launch-breaking `TeamVersus.conf`), and it `ProjectReference`s `ClashEngine.Core` so the shim + assertions use the real wire DTOs (no drift). When you change a wire shape (`schema/*.json` / the DTOs) or the lifecycle, expect to update a ClashRig scenario too. See `ClashRig/CLAUDE.md` and `ClashRig/RUNBOOK.md`.

## Two-assembly architecture (the core rule)

Everything is organized around one boundary:

- **`src/ClashEngine.Core/`** — pure matchmaking logic with **zero SubspaceServer dependencies**: queues, matcher, team balancer, end-policies, match FSM (`ActiveMatch`), rating math, penalties, stats accounting. This is what the test suite exercises in isolation (~650 xUnit tests). Time comes through `IClock`; observability goes out through `IMatchmakingTelemetry`. The pure layer **never blocks** on telemetry and never calls SS APIs directly.
- **`src/ClashEngine/`** — the SS plug-in: adapters that wire the engine to `IGame`/`IChat`/`IPersist`/`IArenaManager`, the physical match orchestrator, config parsing, replay recording, HTTP/file I/O sinks.

**When adding logic, decide which side it belongs on.** If it can be expressed without SS types, it goes in Core and gets a unit test. If it needs the server (placing ships, sending chat, reading conf), it goes in the plug-in as an adapter that translates SS callbacks into calls on the Core engine.

### Core: `MatchmakingEngine` facade

`MatchmakingEngine` (`src/ClashEngine.Core/MatchmakingEngine.cs`) is the top-level facade holding the queue registry, game-type registry, in-flight matches, ratings, penalties, eligibility, and groups. The adapter drives it by translating SS callbacks into method calls (`OnPlayerConnected`, `OnKill`, `TryEnqueue`, …) and calling **`Tick(now)`** on a timer. `Tick` is where time-based transitions happen: it ticks every active match (possibly finalizing), expires veto/invite windows, then pops as many viable match proposals as the queues allow. State changes are surfaced as `IMatchmakingTelemetry` events.

### Plug-in: `ClashModule` lifecycle & telemetry composite

`ClashModule` (`src/ClashEngine/ClashModule.cs`) is the SS module entry point. Key wiring facts:

- It builds the engine with a no-op telemetry sink, constructs every listener (which need the engine reference), then swaps in the real composite via `engine.SetTelemetry(new CompositeTelemetry(...))`.
- **Listener registration order is load-bearing.** The composite list is built in a specific order in `PostLoadAsync` (orchestrators → replay recorder → stats telemetry → event listener → LVZ → freq advisor). `EngineEventListener` is intentionally last so its post-match DMs drain *after* the scoreboard broadcast. Don't reorder casually.
- `MatchKillRouter` is the single owner of the broker's `KillCallback`; it calls `engine.OnKill` then fans out to per-event readers. `StatsListener` is a **pre-engine** reader (the final kill must be recorded before `OnMatchEnded` tears the recorder down).
- Teardown uses a LIFO `_unregisterActions` list — every `Register()` callsite pushes its matching `Unregister`, so `PreUnloadAsync` tears down in reverse-construction order. Follow this pattern when adding new registered components.

### Threading model

Everything engine-facing runs on the **SS mainloop thread** (`OnTick` is a mainloop timer at 500 ms driving `engine.Tick`). The one exception is config hot-reload: `OnArenaClashChanged` runs on a background `Task.Run`, so all registry mutation (game-type commit, queue reconcile, attach/detach) is serialized through the `_clashRegistryGate` semaphore. The awaited HTTP game-type registration deliberately runs *outside* that gate so the synchronous unload path never blocks on a slow stats server. Keep this invariant: registry writes under the gate, network I/O outside it.

## Match lifecycle — two state machines

Don't conflate them:

- **Engine match state** (`MatchState`, in Core): `Forming → Live → Completed | Abandoned | Cancelled`. Owned by `ActiveMatch`; driven by kills, departures, and `Tick`.
- **Orchestrator phase** (`MatchPhase`, in the plug-in): `Setup → Staging → Countdown → Live → Cleanup`. Owned by `MatchOrchestrator` (one instance per active match); this is the *physical* conduct of the match — warping players in, idle-detection during staging, the countdown ticks, spawn-drift enforcement, returning to spec. At "GO!" the orchestrator calls `engine.MarkMatchLive`.

## Config model

The `[ClashEngine]` section is read from two scopes, and the scoping rules are strict (the engine warns on misplaced blocks):

- **Zone scope** (`global.conf`): plug-in tuning ONLY (log verbosity, upload endpoint, replay settings). Game types and queues here are **ignored** (`WarnOnStrandedZoneClashBlocks` surfaces this).
- **Arena scope** (`arena.conf` + its `#include`s): game types and queues.

Two subtleties the code enforces:
- **Game-type names form a single zone-wide, sticky namespace.** A type declared in any arena is globally referenceable by `Queue<i>GameType` in any other arena, resolution is order-independent, the first declarer wins, and a registered type persists for the process lifetime even after its arena detaches (`ReconcileAllArenaQueues` re-resolves all arenas' queues whenever any arena's contribution changes).
- **Hot reload**: editing arena.conf or any included file re-parses and atomically swaps the registry; queues removed/changed by the new content are drained first and affected players are chat-notified.

See `README.md` for the full key-by-key reference and a worked 1v1 example.

## Integration surface (the only extensibility points)

Per `docs/INTEGRATION.md`, exactly four Core interfaces are pluggable I/O edges, plus one inbound HTTP surface the engine hosts; everything else (matchmaking, scoring, penalties, lifecycle, replay format, persistence) is internal and not meant to be swapped:

| Interface | Direction | Wire schema |
|---|---|---|
| `IMatchUploader` (`Core/Stats`) | push, per finalized match | `schema/match.schema.json` |
| `IGameTypeRegistrar` (`Core/GameType`) | push w/ accept/reject, per config load | `schema/gametype.schema.json` |
| `IRatingsProvider` (`Core/Ratings`) | pull, per gametype on first connect | `schema/rating.schema.json` |
| `IEventSink` (`Core/Events`) | push, per queue/match/player event | `schema/event.schema.json` |

`ClashModule.Build*()` picks the HTTP-backed impl when `UploadUrl`+`UploadApiKey` are set, otherwise a local fallback (`JsonFileMatchUploader` / `NoStatsServer*` / `NoOpEventSink`). The DTOs match the `schema/*.json` files 1:1 — keep them in sync when changing wire shapes.

**Control surface (inbound).** With `ControlListenUrl`(+`ControlApiKey`) set, `ClashModule.BuildControlListener()` hosts a BCL `HttpListener` (`src/ClashEngine/Control/`; deliberately no ASP.NET Core in the plug-in ALC) serving `POST /commands` (`schema/command.schema.json` → `schema/commandresponse.schema.json`) and `GET /state` (`schema/snapshot.schema.json`); the REST contract is published as `schema/control.openapi.yaml`, which `$ref`s those schema files — update it alongside them. The command→engine mapping and snapshot builder are Core and unit-tested (`ClashEngine.Core/Control/`: `ControlCommandDispatcher`, `StateSnapshotBuilder`) — the listener only does transport + marshaling: every engine touch bounces onto the mainloop via `MainloopDispatcher` (`QueueMainWorkItem` + `TaskCompletionSource`), and enqueue-flavored commands first run the same rating pre-pull `?play` does (`PlanRatingSeed`). `form_match` is the private-matchmaking seam: `MatchmakingEngine.TryFormMatch` forms a match from an externally-supplied roster anchored on a queue's rules, and the ordinary orchestrator lifecycle takes over; finalization skips KOTH/auto re-adds for such matches and `cancel_match` cancels blamelessly.

## OpenSkillSharp ALC gotcha

The Core csproj references `OpenSkillSharp` with `<ExcludeAssets>runtime</ExcludeAssets>` so its DLL is **not** copied into the plug-in deploy folder. At runtime `ClashModule.EnsureSharedAssemblyResolver` installs an `AssemblyLoadContext.Resolving` hook that redirects `OpenSkillSharp` lookups to the copy already loaded by SS.Matchmaking's ALC — so both plug-ins share one `OpenSkillSharp` type identity and the `IMatchConfiguration` vtable binds without a `TypeLoadException`. The **test** project re-adds the runtime asset (tests run in a single ALC with no SS present). If you touch OpenSkill wiring or see "method does not have an implementation" type-load errors, this is the mechanism to check.

## Test conventions

xUnit. Test methods use `snake_case` names describing the scenario (`Enqueue_into_unknown_queue_returns_false`). Suites build a local `Harness` class wiring the unit-under-test against fakes in `tests/.../Fakes/` (`FakeClock` for deterministic time, `RecordingTelemetry` for asserting emitted events). Mirror that style — never sleep for time; advance `FakeClock`.
