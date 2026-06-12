# ClashEngine

A skill-based matchmaking engine for [SubspaceServer .NET](https://github.com/gigamon-dev/SubspaceServer). ClashEngine forms balanced matches from a queue of players, places them on ships in a configured arena, runs the match through a staging → countdown → live → cleanup lifecycle, tracks per-player stats, and updates persistent OpenSkill ratings on completion.

---

## Repository layout

| Path | Purpose |
|---|---|
| `src/ClashEngine.Core/` | Pure-logic library: queues, matcher, team balancer, end-policies, FSMs, rating math, stats accounting. **No SubspaceServer dependencies.** |
| `src/ClashEngine/` | The SubspaceServer plug-in: adapters that wire the engine to `IGame`, `IChat`, `IPersist`, `IArenaManager`, MatchFocus / MatchLvz callbacks, etc. |
| `tests/ClashEngine.Core.Tests/` | xUnit test suite for the core library (≈650 tests). |
| `schema/` | JSON schemas for the engine's wire formats — `match.schema.json` (outgoing match envelopes), `gametype.schema.json` (outgoing gametype registrations), `rating.schema.json` (incoming rating pulls). |
| `docs/INTEGRATION.md` | How to plug a custom backend (visualizer, dashboard, alternate stats server) into ClashEngine — the three Core interfaces and their wire formats. |

The plug-in is loaded as a SubspaceServer module; the core library is the engine. Pure logic lives where it can be tested in isolation; everything that needs SS APIs lives in the plug-in.

---

## Building

The plug-in references SubspaceServer's `Core.csproj` and `Utilities.csproj` via a relative path. By default it expects:

```
../SubspaceServer/src/Core/Core.csproj
../SubspaceServer/src/Utilities/Utilities.csproj
```

Override with `-p:SubspaceServerRoot=...` if your layout differs.

```sh
dotnet build
dotnet test tests/ClashEngine.Core.Tests/ClashEngine.Core.Tests.csproj
```

Build output lands in `SubspaceServer/Zone/bin/modules/ClashEngine/` inside this repo (`ClashEngine.dll` and `ClashEngine.Core.dll`). When `$(SubspaceServerRoot)` is set (the default points at `../SubspaceServer/src/`), a post-build step also copies the staged files to `$(SubspaceServerRoot)SubspaceServer/Zone/bin/modules/ClashEngine/` for direct deployment.

---

## Installing into a SubspaceServer zone

Four configuration touch-points wire ClashEngine into a zone. Each is shown end-to-end with the example used by the bundled 1v1 test setup.

### 1. Register the plug-in module

`SubspaceServer/Zone/conf/Modules.config`:

```xml
<module type="ClashEngine.ClashModule" path="bin/modules/ClashEngine/ClashEngine.dll" />
```

Place this after the SS matchmaking module entries (`SS.Matchmaking.Modules.MatchFocus`, `SS.Matchmaking.Modules.MatchLvz`, etc.), since ClashEngine relies on them being available.

### 2. Add the match arena to `PermanentArenas`

ClashEngine matches run in a dedicated arena (one per queue). Add the arena's base name to `PermanentArenas` in `global.conf` so the zone keeps it alive even when no players are inside:

```ini
[ General ]
PermanentArenas = 1v1comp 2v2pub ...
```

### 3. Configure ClashEngine

ClashEngine reads a `[ClashEngine]` section from two scopes:

- **Zone scope (`global.conf`):** plug-in tuning and any game types you want available across every arena.
- **Per-arena scope (`arena.conf`):** that arena's queues and any arena-specific game types.

The section can live directly in `global.conf` / `arena.conf`, or in any file `#include`'d from them — the host resolves keys across the full document and watches every constituent file for changes, so a save to an included file triggers the same hot reload as editing the main conf. The four concerns in a `[ClashEngine]` section are:

1. **Plug-in tuning** — log verbosity, upload endpoint, replay recording. *(Zone scope only.)*
2. **Game types** — the rules of a match (team shape, win condition, lives, spawn locations). Zone-scope game types are shared; arena-scope game types are only visible to that arena's queues.
3. **Queues** — the matchmaking policies that draw players into a game type. *(Arena scope only — every queue is owned by an arena.)*
4. **DefaultQueue** — the queue `?play` resolves to in this arena when the player gives no name. *(Arena scope only.)*

A complete worked example is at the end of this section. Each subsection below documents the keys.

### 4. Configure the match arena (`arena.conf`)

Each match arena needs `MatchFocus` + `MatchLvz` attached, and its `[ClashEngine]` section declares the queues it owns plus the optional `DefaultQueue` for `?play` with no argument. Minimal skeleton (the worked example below shows full queue keys):

```ini
[ Modules ]
AttachModules = \
    SS.Matchmaking.Modules.MatchFocus \
    SS.Matchmaking.Modules.MatchLvz

[ Team ]
InitialSpec = 1

[SS.Matchmaking.MatchFocus]
FilterKillPackets = 1

[ClashEngine]
DefaultQueue = 1v1
QueueCount   = 1
Queue1Name      = 1v1
Queue1GameType  = elimination_1v1
Queue1MatchArena = 1v1comp
```

The `[ClashEngine]` block can live directly in `arena.conf` (as above) or in any file `#include`'d from it; the host watches the whole document for changes either way.

### 5. Grant the chat commands

Player-facing commands have to be granted in `groupdef.dir/default` (or whichever group covers your players). Add to the bottom of the file:

```
; Clash
cmd_play
cmd_party
cmd_partymode
cmd_leaveparty
cmd_queue
cmd_showline
cmd_auto
cmd_rating
cmd_cancel
cmd_accept
cmd_decline
cmd_forgive
cmd_helpclash
cmd_chart
cmd_penalties
```

`?clashlog` and `?resetplayer` are reserved for higher-privileged groups by default; grant them where appropriate.

---

## `[ClashEngine]` section reference

### Plug-in tuning

| Key | Type | Default | Notes |
|---|---|---|---|
| `LogVerbosity` | `Off` / `Normal` / `Verbose` / `Trace` | `Normal` | Runtime-mutable via `?clashlog`. `Verbose` adds debug for command inputs/outcomes, engine event translations, orchestrator phase transitions. `Trace` adds every wire event (connect/disconnect/ship/freq/kill). |
| `MaxPenaltyHours` | int > 0 | `6` | Hard ceiling (in hours) on any assessed matchmaking penalty timeout. Applied to every penalty policy (abandonment, griefing, staging-AFK, elimination cooldown) so no escalation ladder can exceed it — an operator escape hatch against a runaway-escalation bug. `<= 0` falls back to the engine's built-in default cap. |
| `UploadUrl` | URL | (unset) | Stats-upload endpoint. Receives `multipart/form-data` POST with metadata JSON + replay file. |
| `UploadApiKey` | string | (unset) | Sent as `X-Api-Key` header. Both `UploadUrl` and `UploadApiKey` must be set for HTTP upload; if either is missing, ClashEngine writes match envelopes as JSON files under `<AppContext.BaseDirectory>/matches` instead. |
| `StatsViewUrl` | URL template | (unset) | Per-match viewer URL. After the end-of-match scoreboard, an arena message `Match stats: <url>` is broadcast to everyone in the match arena. The template may include a literal `{matchId}` token (replaced with the dashed GUID, e.g. `8b34f35d-b3b5-4747-941a-5cb96153641e`); without one, the id is appended. Leave unset to suppress the line. |
| `RecordReplays` | 0/1 | 1 | Record every started match using the in-plug-in `MatchRecorder`. |
| `ReplayRecordingDir` | path | `<AppContext.BaseDirectory>/clash-replays` | Where in-flight `.replay` files land. Files are deleted after a successful upload. |
| `DistanceSampleHz` | int (1–50) | 5 | Frequency of the periodic distance-to-nearest-enemy sampler used for the scoreboard's `dE` column. Set to `0` to disable. |
| `QueueActivityRefresh` | 0/1 | 1 | Queued players' in-game activity — turning their ship, firing any weapon, or sending any chat message — automatically refreshes their AFK dwell clock (`Queue<i>AfkWarn`/`AfkCull`), so present players never need to re-issue `?play`. Spectators emit no position data, so a queued spectator refreshes via chat or `?play` only. `0` restores `?play`-only refresh (and skips the position/chat observation entirely). |
| `EventStreamUrl` | URL | (unset) | Outbound [event stream](#event-stream) endpoint. Receives one `application/json` POST per queue/match/player event for live advertising/notification (e.g. a Discord bot). Unset disables event emission. Never derived from `UploadUrl` — point it at the consuming service. |
| `EventStreamApiKey` | string | (unset) | Sent as `X-Api-Key` on event POSTs. Falls back to `UploadApiKey` when unset, so a single gateway terminating both needs no extra config. |

### Game types

`GameTypeCount = N` → ClashEngine reads `GameType1` … `GameTypeN`. A game type is the rules of one match shape; multiple queues can reference one game type. Read from **arena scope only** (arena.conf plus its `#include`s) — a `GameType` block in global.conf is ignored (ClashEngine logs a warning if it finds one).

Game-type **names are a single, zone-wide namespace** and are **globally referenceable**: declare a game type once in *any* arena.conf, and a `Queue<i>GameType` in *any* arena can reference it by that name (resolution is order-independent — a queue whose game type hasn't loaded yet resolves as soon as some arena declares it). Two consequences:

- **Declare each name once.** If two arenas both declare the same game-type name, the second is rejected (the first definer wins). Don't `#include` the same `GameType` block into multiple arenas — that's a collision, not sharing. To share, declare it in one arena and reference the name from the others. A dedicated "definitions" arena (a permanent arena with `GameType` blocks and no queues) is a clean place to host shared game types.
- **Game types are sticky.** Once registered, a game type stays for the server's lifetime even if its declaring arena detaches — so queues elsewhere that reference it keep working, and its name stays owned by the first definer (no other arena can re-claim it until restart). Re-attaching the declaring arena re-commits (and can update) its own game types.

#### Rules

| Key | Type | Default | Notes |
|---|---|---|---|
| `GameType<i>Name` | string | required | Referenced by `Queue<j>GameType`. Case-insensitive. |
| `GameType<i>TeamCount` | int ≥ 2 | 2 | Number of teams. |
| `GameType<i>PlayersPerTeam` | int ≥ 1 | 4 | Players per team. |
| `GameType<i>KillTarget` | int ≥ 0 | 30 (when nothing else set) | Per-team kills required to win. `0` = unset (use `TimeLimit` instead). |
| `GameType<i>TimeLimit` | `HH:MM:SS` | (unset) | Leader at this elapsed time wins. A tie at the limit triggers unlimited sudden-death overtime — next kill that breaks the tie wins. |
| `GameType<i>Lives` | int ≥ 0 | 0 = unlimited | Elimination matches: each player gets this many lives total, counting the initial spawn (so `Lives - 1` respawns). A player whose last life ends is eliminated and released from the match-roster (the `EliminationCooldown` below applies). |
| `GameType<i>EliminationCooldown` | seconds or `HH:MM:SS` | `60` (1 min) | Only meaningful for elimination matches (`Lives > 0`). When a player loses their last life, they're released from the match and must wait this long before `?play` re-queues them into a new match. Auto-rescinded if the match ends before the cooldown elapses, so a player never waits longer than the match itself ran. Omit for the 1-minute default; set `0` to disable (eliminated players may requeue immediately) for this game type. |
| `GameType<i>TeamCollapseGrace` | seconds or `HH:MM:SS` | `10` | How long an entire team can be without any Active or Pending players before forfeiting. Distinct from per-player grace; a team-wide simultaneous drop gets this window to recover before the surviving teams take a forfeit win. |
| `GameType<i>ShipChangeGracePeriod` | seconds or `HH:MM:SS` | `10` | After a non-fatal death, the player has this long to change ships before being re-locked to whatever ship they're currently in for the rest of the life. Mid-life ship changes are otherwise forbidden because each ship transition refreshes Continuum item counts. Freq (team) changes are blocked outright for match participants regardless of this value. Set to `0` to forbid every in-match ship change. Knockouts (last life) don't open this window — the orchestrator's `KnockoutSpecDelay` handles that path. |
| `GameType<i>ReturnItemsAction` | `full` / `restore` / `burn` | `full` | Inventory policy on `?return` after self-spec. `full` keeps the freshly-spawned ship's loadout (Continuum default). `restore` deducts items back down to whatever the player had at the moment they specced — closes the burst/repel free-reload loophole. `burn` zeros the loadout entirely. |
| `GameType<i>DisallowItems` | 0/1 | 0 (items allowed) | No-items mode. While a match of this game type runs, every participant's per-ship item-max client settings (`BurstMax`, `RepelMax`, `DecoyMax`, `BrickMax`, `ThorMax`, `RocketMax`, `PortalMax`) are overridden to 0 — ships spawn with no stockpilable items and greens can't grant any. The override is per-player (same mechanism as the respawn boxes below), applied at placement/`?return` and removed when the player leaves the match, so it never leaks into the rest of the arena. |
| `GameType<i>DisallowAntiwarp` | 0/1 | 0 (arena settings apply) | No-antiwarp mode. While a match of this game type runs, every participant's per-ship `AntiWarpStatus` client setting is overridden to 0 (all eight ships) — nobody can carry, green, or start with antiwarp, regardless of the arena's ship settings. Same per-player override mechanism and lifecycle as `DisallowItems`. |

`KillTarget` and `TimeLimit` may both be set, in which case whichever fires first ends the match.

#### Match start locations and respawn boxes

Two independent concerns share this group. **Start locations** move the match's pre-GO physical setup off the arena's default spawn into something deterministic (a one-time server warp), gated by `UseStartLocation`; with the gate off, the configured start coordinates are silently ignored and players use the arena's normal spawn points. **Respawn boxes** override each client's native `[Spawn]` settings so the *client* respawns players inside a per-team box after every death during the match (a port of SubspaceServer's `SendSpawnOverrides`); they are self-gating — configuring a team's `SpawnCenter` turns them on for that team.

| Key | Type | Default | Notes |
|---|---|---|---|
| `GameType<i>UseStartLocation` | 0/1 | 0 (off) | Master switch for start locations. Off → no start warp; arena defaults apply. Does not affect respawn boxes. |
| `GameType<i>Team<t>Starts` | `x,y; x,y; ...` | (unset) | Per-team **set** of candidate match-start coordinates in tiles. At setup the orchestrator picks one entry uniformly at random and warps every player on that team to it. Multiple coords give a team a rotating start-point pool; a single coord is fine too. |
| `GameType<i>MaxStartDrift` | int ≥ 0 (tiles) | (unset) | Maximum drift in tiles (1 tile = 16 px) a player may travel from the team's chosen start during Staging and Countdown. Drifters get warped back. `null` or `0` disables drift enforcement. |
| `GameType<i>Team<t>SpawnCenter` | `x,y` | (unset) | Per-team **respawn** box center, in tiles. When set, the orchestrator overrides that team's players' native `[Spawn]` client settings so the client respawns them here after every death (and on `?return`). Self-gating; independent of `UseStartLocation`. |
| `GameType<i>Team<t>SpawnRadius` | int 0–511 (tiles) | 0 | Radius in tiles of the respawn box; the client spawns at a random point within it. `0` = respawn exactly at the center. Ignored without a matching `SpawnCenter`. |

**Pre-GO drift enforcement.** While `UseStartLocation = 1`, every position packet during Staging and Countdown is checked against the team's chosen start; players past the threshold are warped back to that start. Enforcement stops at GO — players are not re-warped when the match goes live, so any sub-threshold drift accumulated during the countdown is where the match starts from.

**Start vs respawn.** The start location is a one-time server warp at match setup (and the drift-back target before GO). The respawn box is a client-settings override that governs where the client spawns the ship — on the initial spawn *and* every respawn after a death — and is cleared when a player leaves the match. An elimination game type (1 life) effectively only uses the start location; a multi-life or kill-target game type uses the respawn box on each death.

#### Presence zone ("stay in the zone or lose")

An optional extra end condition: one shared box that every team must keep **at least one** active player inside while the match is live. A team with nobody in the zone is warned in chat ("Team X has left the zone — back in Ns or they forfeit!"); if no one re-enters within `ZoneForfeitTimeout`, the team forfeits and is ranked last ("Match over! Team X failed to hold the zone — Team Y wins."). The clock starts at GO! — a team must also *enter* the zone within the timeout — and resets every time a team member is seen inside. If *every* team deserts the zone past the timeout, the match ends in a **draw** ("Match drawn — every team abandoned the zone."): all teams tie at rank 1, so ratings treat it as a tie rather than a win for anyone.

| Key | Type | Default | Notes |
|---|---|---|---|
| `GameType<i>ZoneCenter` | `x,y` (tiles) | (unset = no zone) | Center of the zone box. Setting this (plus a radius) enables the rule for every queue using this game type. |
| `GameType<i>ZoneRadius` | int ≥ 1 (tiles) | required with center | Half-width of the box: the zone spans `center ± radius` on each axis (a square, like the respawn-box radius). Missing or `< 1` disables the zone with a warning. |
| `GameType<i>ZoneForfeitTimeout` | seconds or `HH:MM:SS` | `30` | How long a team may be entirely absent from the zone before forfeiting. |

Interactions worth knowing: presence is counted only from **active, in-ship** participants (spectating, in-grace, and eliminated players don't hold the zone); a team that has *no* active players at all is governed by `TeamCollapseGrace`, not the zone clock — the zone clock restarts fresh when they recover; and knocked-out/eliminated players ceasing to count can hand the zone burden to the survivors mid-match, which is the intended pressure in elimination game types.

#### Match-flow timings (warmup / countdown / spec grace)

| Key | Type | Default | Notes |
|---|---|---|---|
| `GameType<i>StagingDuration` | seconds or `HH:MM:SS` | `10` | Upper bound on the warmup window between match formation and the pre-GO countdown. Each player must demonstrate non-idleness (move/rotate/fire); the first detected movement DMs the player a confirmation, and staging ends early as soon as every participant has flipped non-idle. Any player still idle at the time limit fails the readiness check and the match is cancelled. |
| `GameType<i>CountdownDuration` | seconds or `HH:MM:SS` | `10` | Length of the pre-GO countdown. The orchestrator broadcasts `All set! Pick your final ship -- Ns until lock, then GO.` up-front, then ticks `-3-` → `-2-` → `-1-` → `GO!` over the final 3 s. Ships lock 5 s before GO, so values above that leave a ship-pick window at the start of the countdown. Minimum 5 seconds. |
| `GameType<i>KnockoutSpecDelay` | seconds or `HH:MM:SS` | `0` | Grace between a player's last-life death and the forced spec, so residual mines/bombs they just fired can still land. Only meaningful for elimination matches (`Lives > 0`); match-end cleanup specs everyone immediately regardless. |

#### Griefing penalties

Two automated griefing detectors can run at match end; players they flag receive a queue-timeout that match participants may collectively veto (`?forgive`, governed by the queue's `VetoesRequired` / `VetoWindow`). Both detectors are **opt-in per game type and default off** — a game type that enables neither assesses no automated griefing penalties:

- **Early-exit** flags a player who burned through all their lives unusually fast, leaving teammates out to dry. It only ever fires in limited-lives matches (`Lives > 0`), and it's intended for team shapes — on a solo-team shape (1v1, FFA) it would flag players for simply losing fast, so think twice before enabling it there.
- **Teamkill threshold** flags a player whose teamkill count exceeds a threshold.

With both enabled they run together; a player flagged by both gets one penalty (the higher-severity flag).

| Key | Type | Default | Notes |
|---|---|---|---|
| `GameType<i>EarlyExitPenalty` | 0/1 | 0 (off) | Enables the early-exit detector for this game type. Has no effect without `Lives > 0` (warned). |
| `GameType<i>EarlyExitMinimumDuration` | seconds or `HH:MM:SS` | `0:02:00` | A player who uses up all their lives less than this long into the match is flagged. Severity scales with how badly they missed the bar. Raise it for game types where bowing out early is more suspect; lower it for riskier, faster modes. Ignored (warned) unless `EarlyExitPenalty = 1`. |
| `GameType<i>TeamkillPenalty` | 0/1 | 0 (off) | Enables the teamkill detector for this game type. |
| `GameType<i>TeamkillThreshold` | int ≥ 0 | `2` (`3` with `Queue<j>Preset = casual`) | A player whose teamkill count strictly exceeds this is flagged. The default comes from the queue's preset; an explicit value here wins for every queue using this game type. Ignored (warned) unless `TeamkillPenalty = 1`. |

### Queues

`QueueCount = N` → ClashEngine reads `Queue1` … `QueueN`. Each queue is one matchmaking pool that produces matches under a particular game type.

| Key | Type | Default | Notes |
|---|---|---|---|
| `Queue<i>Name` | string | required | Lookup identifier for the queue. Players type it with `?play <name>`; multiple space-separated tokens get joined with `_` before lookup, so `?play casual 4v4` looks up `casual_4v4`. Case-insensitive. |
| `Queue<i>Label` | string | (defaults to `Name`) | Pretty operator-chosen string used in chat output and the JSON match-stats payload (e.g. `4v4 (Casual)`). Decouples display from the lookup name. |
| `Queue<i>GameType` | string | required | Must match a `GameType<j>Name`. Inherits the game type's spawn config. Many queues can reference the same game type. |
| `Queue<i>Preset` | `casual` | (none) | Opt-in shortcut for the lenient bundle of defaults: q-start/q-floor 0.4/0.10 (vs 0.6/0.30), no `MaxLiabilityGap` cap, `RelaxTime` 45 s, `RatingWeight` 0.5, and a slightly higher griefing threshold. Each individual knob can still be overridden by an explicit `Queue<i><Key>` below. Omit for the standard (stricter) defaults. |
| `Queue<i>MatchArena` | string | (none) | Arena to send players to for the match. Recommended: dedicated, in `PermanentArenas`. |
| `Queue<i>LookAhead` | int ≥ 0 | 0 (strict FIFO) | Extra candidates above the minimum required (`TotalPlayers`) the matcher considers when looking for the best partition. `LookAhead = 4` on a 4v4 means a pool of 12 candidates. The pool is the **front** of the queue, so anyone past `TotalPlayers + LookAhead` isn't evaluated until they move up. |
| `Queue<i>AlwaysChooseLongestWaiter` | 0/1 | 1 (on) | When on, the longest-waiting player (queue head) is pinned into every candidate set, so they're never passed over (strict FIFO fairness). Turn **off** to let the matcher exclude the head in favor of a better-balanced subset from the look-ahead pool — useful when one outlier-rated player at the head stalls the queue. Trade-off: that head may then wait longer, since quality relaxation no longer guarantees they're picked. |
| `Queue<i>RelaxTime` | `HH:MM:SS` | `0:02:00` (standard), `0:00:45` (with `Preset = casual`) | Quality-relaxation duration: how fast the quality threshold falls from `qStart` to `qFloor`. Longer = stricter early but eventually accepts weaker matches; shorter = takes whatever it can sooner. |
| `Queue<i>HoldWindow` | `HH:MM:SS` | `0:00:10` | Once a viable partition is found, the matcher waits up to this duration to see if a better one arrives. Set to `0` to pop immediately. |
| `Queue<i>QualityCeiling` | float [0,1] | `0.9` | If a held candidate's quality reaches this, pop without waiting out the hold window. |
| `Queue<i>IgnorePenalties` | 0/1 | 0 (enforced) | When on, this queue does **not** enforce penalty timeouts: players serving any queue-timeout (abandonment, griefing, staging AFK, elimination cooldown) may still `?play` and be matched here. Penalties are still assessed, escalated, and shown by `?penalties` — and every other queue still enforces them — so this makes a queue a "penalty-exempt" pool (e.g. an unranked free-play queue) without weakening enforcement elsewhere. |
| `Queue<i>VetoesRequired` | int ≥ 1 | `2` | Number of distinct match participants who must `?forgive` a pending griefing penalty within `VetoWindow` to rescind it. |
| `Queue<i>VetoWindow` | `HH:MM:SS` | `0:01:00` | Open period for vetos after a griefing flag fires. Penalty becomes final at the end of the window if the threshold wasn't reached. |
| `Queue<i>PromoteWinners` | 0/1 | `0` | KOTH ("king of the hill") mode: the winning team's players are auto-re-enqueued at the head of this queue after a Completed match. Off by default. |
| `Queue<i>MaxConsecutiveDefenses` | int ≥ 1 | `3` | Max consecutive wins a champion can defend before being sent to the back of the queue to give challengers a clean shot. Only meaningful with `PromoteWinners = 1`. |
| `Queue<i>AfkWarn` | `HH:MM:SS` or seconds | `0:15:00` | In-queue dwell time before a one-time "still there?" AFK warning fires (a `queue.dwell_warning` [event](#event-stream) plus an in-game DM). `0` disables both the warning **and** the cull for this queue. Re-queuing, repeating `?play`, or any in-game activity (turning your ship, firing, chatting — see `QueueActivityRefresh`) resets the timer. |
| `Queue<i>AfkCull` | `HH:MM:SS` or seconds | `0:20:00` | In-queue dwell time before the player is auto-dequeued for inactivity (a `queue.left` event with `reason = afk_cull`). `0` keeps the warning but never culls. A value below `AfkWarn` is raised to `AfkWarn`. |

### Per-arena `DefaultQueue`

```ini
[ClashEngine]
DefaultQueue = 1v1
```

Sets the queue `?play` resolves to when the player issues the command without an explicit queue name *from this arena*. Optional; without it, `?play` requires the player to name a queue. Read from arena scope only.

### Worked example: 1v1 elimination

The 1v1 elimination setup bundled with the SubspaceServer test zone (under `SubspaceServer/Zone/conf/global.conf` and `SubspaceServer/Zone/arenas/1v1comp/`) is the canonical reference for what these keys look like in production. Reproduced here so README and zone stay in sync.

`global.conf` — plug-in tuning only. Game types and queues are **not** read at zone scope:

```ini
[ClashEngine]

; --- Plug-in tuning (zone-wide) ---
LogVerbosity        = Verbose
UploadUrl           = http://localhost:8080/api/matches
UploadApiKey        = <secret>
RecordReplays       = 1
ReplayRecordingDir  = clash-replays
DistanceSampleHz    = 5

; --- Outbound event stream (optional) ---
; One JSON POST per queue/match/player event for a Discord bot, dashboard, or webhook relay.
; EventStreamApiKey is omitted here, so it falls back to UploadApiKey above. Set it explicitly
; only when the event consumer needs a different key from the stats server.
EventStreamUrl      = http://localhost:9090/api/events
```

`Zone/arenas/1v1comp/arena.conf` — the standard arena conf plus the game type and the two queues owned by this arena:

```ini
[ Modules ]
AttachModules = \
    SS.Matchmaking.Modules.MatchFocus \
    SS.Matchmaking.Modules.MatchLvz

[ Team ]
InitialSpec = 1

[SS.Matchmaking.MatchFocus]
FilterKillPackets = 1

[ClashEngine]
DefaultQueue = 1v1

; --- Game type (arena-scoped, but its NAME is globally referenceable). Declare
;     it once here; queues in other arenas may reference "elimination_1v1" by name. ---
GameTypeCount = 1

GameType1Name           = elimination_1v1
GameType1TeamCount      = 2
GameType1PlayersPerTeam = 1
GameType1KillTarget     = 3
;GameType1TimeLimit     = 0:10:00          ; uncomment to add a time cap
GameType1Lives          = 0
GameType1UseStartLocation  = 1
GameType1Team1Starts       = 480,256; 480,257; 480,258
GameType1Team2Starts       = 544,256; 544,257; 544,258
GameType1MaxStartDrift     = 6             ; tiles
GameType1Team1SpawnCenter  = 480,256       ; respawn box center (tiles)
GameType1Team1SpawnRadius  = 4             ; tiles (random spread on each respawn)
GameType1Team2SpawnCenter  = 544,256
GameType1Team2SpawnRadius  = 4
GameType1StagingDuration   = 8             ; seconds, upper bound (default 10)
GameType1CountdownDuration = 10            ; seconds (min 5, default 10; ships lock 5s before GO)

; --- Queues owned by this arena ---
QueueCount   = 2

; Standard (stricter) queue: small look-ahead, hold-window for late better arrivals.
; The Label drives chat output and the JSON payload; the Name is what players type.
Queue1Name           = 1v1
Queue1Label          = 1v1 (Competitive)
Queue1GameType       = elimination_1v1
Queue1MatchArena     = 1v1comp
Queue1LookAhead      = 4
Queue1RelaxTime      = 0:01:30
Queue1HoldWindow     = 0:00:10
Queue1QualityCeiling = 0.90

; Casual: same game type & arena; pops fast, half rating weight via the preset.
; ?play casual 1v1 resolves to "casual_1v1" via the multi-word join.
Queue2Name           = casual_1v1
Queue2Label          = 1v1 (Casual)
Queue2GameType       = elimination_1v1
Queue2Preset         = casual
Queue2MatchArena     = 1v1comp
Queue2LookAhead      = 0
Queue2HoldWindow     = 0:00:00
Queue2QualityCeiling = 0.70
```

This shows off:

- **Multiple queues sharing a game type.** Both `1v1` and `casual_1v1` run under `elimination_1v1`, so they share the same rules (and rating bucket, keyed by the game type). They differ only in matchmaking strictness, which `Preset = casual` packages as a one-line opt-in for the lenient bundle.
- **Arena scope, global names.** Both the game type and the queues live in `arena.conf` — game types and queues are not parsed from `global.conf` (only plug-in tuning is). A hypothetical second 1v1 arena that wants the same rules does **not** redeclare `elimination_1v1` (that would collide) — it just references the name from its own queues, since game-type names are globally referenceable and sticky. Only the plug-in-tuning keys belong in `global.conf`.
- **Lookup name vs display label.** `Queue<i>Name` is what `?play` resolves against; `Queue<i>Label` is the pretty string shown in chat and the JSON payload. Decoupling them means you can rename one without disturbing the other.
- **Per-team start pools.** Three candidate start points per team — the orchestrator picks one at random for each match, so consecutive games don't always start in identical positions.
- **Drift enforcement.** `MaxStartDrift = 6` warps anyone who wanders more than 6 tiles from the chosen start back during Staging and Countdown.
- **Respawn boxes.** `Team<t>SpawnCenter` + `SpawnRadius` override each client's `[Spawn]` settings so post-death respawns land back in the team's area instead of the arena default.
- **Explicit matchmaker tuning.** `LookAhead`, `HoldWindow`, and `QualityCeiling` are spelled out for both queues; the standard queue holds out for balance, casual takes whatever it can get fast.

Add `1v1comp` to `PermanentArenas` and grant the chat commands in `groupdef.dir/default`. That's the full setup.

---

## Match lifecycle

A match progresses through five orchestrator phases:

1. **Setup.** Players are warped into the configured `MatchArena`, set to their assigned ship + freq, freq-locked, and (if `UseStartLocation`) warped to the team's chosen start location. The assigned ship is the last ship the player was in before they last went to spectator mode (tracked zone-wide — inside and outside matches — and persisted across sessions); players with no remembered ship start in a Warbird. If the game type configures respawn boxes, each player's `[Spawn]` client settings are overridden so post-death respawns land in their team's box. Ship changes are unrestricted through the end of Staging.
2. **Staging** (up to `StagingDuration`, default 10 s). Idle detection: each player must demonstrate non-idleness via rotation, movement, or weapon fire. The first detected movement DMs the player `Got it -- you're ready. Standby for the countdown.` Staging ends early as soon as every participant has flipped non-idle; if anyone is still idle at the time limit the match is cancelled and idle players are flagged AFK. Players may change ships freely during this phase. Drift enforcement runs here.
3. **Countdown** (`CountdownDuration`, default 10 s, min 5 s). Broadcasts `All set! Pick your final ship -- Ns until lock, then GO.` up-front (or just `All set!` for countdowns at the 5 s minimum), then ticks `-3-` → `-2-` → `-1-` → `GO!` over the final 3 s. Ships lock 5 s before GO; the seconds before that remain a free ship-pick window. Drift enforcement still active, and stops at `GO!` (players are not re-warped when the match goes live).
4. **Live.** Engine FSM runs end-policy, kill counting, lives tracking, team-collapse detection (if a team has no live members for the team-collapse grace window, they forfeit; surviving teams hear a 10 s warning). After each non-fatal death the player is DMed `You have Ns to change ships before being locked back to your current ship.` (`N` = `ShipChangeGracePeriod`); knockouts (last life) skip this since they go straight to spec.
5. **Cleanup.** Match-end summary chat broadcast (`Match over! Team A/B wins.` plus the full scoreboard table). Players are unlocked and sent to spec.

---

## Stats and replays

When a match ends, ClashEngine builds a `MatchPayload` (schema in `schema/match.schema.json`) containing:

- Match metadata (id, queue, game type, arena, start/end times, final state).
- Ranked teams with their scores.
- Every participant's full stats: K/D/A/KOs/teamkills, decay-weighted KillDamage and ForcedRepelDamage/Credit, per-weapon shot accounting, item uses, wasted items, distance samples, life timeline (each life carries `shipAtEnd` — the ship the player died / finished the match in), pre- and post-match rating.

If `UploadUrl` + `UploadApiKey` are configured, the payload + replay file are POSTed as `multipart/form-data`. Otherwise the payload is written to disk under `<AppContext.BaseDirectory>/matches/{matchId}.json` for batch upload by another process.

The same payload structure is used to render the in-game scoreboard at match end and the on-demand `?chart` table for spectators (resolved through `IMatchFocus`).

---

## Event stream

ClashEngine can push a normalized stream of queue- and match-state events to an HTTP endpoint so an **external service** — a Discord bot, a dashboard, a webhook relay — can advertise live queue state (e.g. by editing a "queue board" message) and notify players. Set `EventStreamUrl` (+ `EventStreamApiKey`, or it reuses `UploadApiKey`) under `[ClashEngine]` in global.conf to enable it; unset, emission is simply off.

The wire format is `schema/event.schema.json`; the integration contract (the `IEventSink` edge) is documented in [`docs/INTEGRATION.md`](docs/INTEGRATION.md). Each event is one fire-and-forget `application/json` POST with an `X-Api-Key` header; delivery is best-effort (drops on backend overflow), so a consumer should tolerate gaps — every queue event carries the queue's current `count`/`capacity`, so a board self-heals from the next event.

v1 emits:

- **Queue membership** — `queue.joined`, `queue.left` (with a `reason`: `cancel` / `disconnect` / `matched` / `afk_cull` / `group_change` / `reset`), `queue.near_full`, and `queue.dwell_warning`.
- **Match lifecycle** — `match.teams_locked` (proposal), `match.started` (GO), `match.ended` (outcome + ranked teams + duration).
- **Player** — `player.discord_link_requested` from `?connect discord <alias>`.

**ClashEngine is identity-agnostic:** events are keyed by in-game player name. Any Discord-account link and per-player opt-in live entirely in the consuming service — `?connect discord` just relays the alias; the engine stores nothing.

**AFK watchdog.** Players who sit in a queue too long are nudged then culled, per-queue via `Queue<i>AfkWarn` / `Queue<i>AfkCull` (defaults 15 min / 20 min; `AfkWarn = 0` disables). The warning surfaces as a `queue.dwell_warning` event and an in-game DM; the cull dequeues the player and surfaces as `queue.left` with `reason = afk_cull`. The timer resets on any proof of presence: in-game activity (turning your ship, firing any weapon, or sending any chat message — zone-wide toggle `QueueActivityRefresh`, on by default) or re-issuing `?play`. Deliberate input is required — a motionless client's keep-alive packets and a drifting ship's position changes don't count, and spectators (who emit no position data) refresh via chat or `?play` only.

---

## Player commands

| Command | Group | Notes |
|---|---|---|
| `?play <queue name>` | player | Queue for the next match. Multiple space-separated tokens are joined with `_` before lookup (so `?play casual 4v4` looks up `casual_4v4`). Without a name, falls back to the arena's `DefaultQueue`. You can wait in several queues at once; when one of them pops a match you're pulled out of **all** of them, and when that match ends your spots in the *other* queues are restored automatically (at the back of the line, with a notice — `?cancel` to leave). No opt-in needed: those queues were your own explicit `?play`s. Not restored if you abandoned the match, disconnected, are serving a timeout, or the queue was removed by a config reload meanwhile. |
| `?queue [name]` | player | List queues defined for the current arena, or show who's queued and how long they've been waiting in `<name>`. The no-argument listing is also shown automatically to each player on entering an arena that has queues (skipped for players currently in a match). Same multi-word lookup as `?play`. When a queue has enough players but no match has started, the reply explains why: too few players (how many more are needed), teams too imbalanced to meet the quality threshold (with the best achievable quality), no viable team assignment, or matchmaking succeeded and is holding for better arrivals (with the remaining seconds). The same explanation is logged at `Verbose` whenever the reason changes. |
| `?showline` | player | Show the detailed `?queue <name>` view for every queue you're currently in, one after another — works from any arena, since it's driven by your queue membership rather than the current arena's queue list. |
| `?cancel` | player | Leave every queue you're in. |
| `?return` | player | Rejoin the match you were specced from. Bypasses the per-match freq lock by placing you directly on your assigned ship and team freq. |
| `?forfeit` | player | Vote to forfeit your current match — the sanctioned way for a team to concede an unwinnable game without anyone being assessed an abandon. The first vote is broadcast to the eligible teammates (`X has asked to forfeit. Type ?forfeit to agree (1/4)`), later votes update the tally. When every **non-eliminated** team member has voted, the match ends immediately as a forfeit loss for the team (rating applies as a normal loss; no abandonment penalty for any voter). Votes are sticky — they can't be withdrawn — and survive a spec-out or even an expired grace window: if the vote later completes, an early-leaving voter's abandon is retroactively excused, while a teammate who left *without* voting keeps theirs. Eliminated players are out of the vote and can't block it; a lone remaining player's vote completes instantly. |
| `?party` / `?party <p1>[,<p2>,...]` | player | List your current party's members, or invite one or more players to your party. |
| `?accept [inviter]` | player | Accept a pending group invitation. Inviter is optional when only one is pending. |
| `?decline [inviter]` | player | Decline a pending invitation. |
| `?leaveparty` | player | Leave your current party. If you're the leader of a closed party, the party disbands. |
| `?partymode [open\|closed]` | player | View or change your party's mode. Closed parties have a leader who controls invites. |
| `?rating` | player | Show your skill rating per game type. |
| `?connect discord <alias>` | player | Relay a request to link your in-game name to a Discord alias, so the [event-stream](#event-stream) consumer (e.g. a Discord bot) can notify you. ClashEngine stores nothing — it emits a `player.discord_link_requested` event and the bot service performs the link/opt-in. |
| `?auto [on\|off]` | player | Set auto-queue, or **toggle** it with no argument. When **on**, you're automatically re-queued into a match's queue when it ends (the same queue you popped out of), so you keep cycling through matches without re-issuing `?play`. Governs only that formation queue — your memberships in any *other* queues you were waiting in are restored regardless of this setting (see `?play`). Persists across sessions. Independent of the KOTH `Queue<i>PromoteWinners` setting, and applies to winners and losers alike (added at the back, not the head). Auto-disabled — with a notice to turn it back on — if you're flagged for a staging AFK violation. |
| `?chart` | player | Show the live scoreboard for the match you're in or spectating. |
| `?forgive <player>` | player | Vote to overturn a pending griefing penalty against a match participant. |
| `?penalties` | player | List every player with live queue-timeout penalty state, one line per penalty ladder: the reason (abandonment, griefing, elimination cooldown, staging AFK), time remaining on the timeout (or "timeout served"), and the escalation multiplier with how long until it resets (the policy's memory window since the last offense). Players still serving a timeout sort first. |
| `?helpclash` | player | List the player commands. |
| `?clashlog [off\|normal\|verbose\|trace]` | mod | Read or set ClashEngine debug verbosity at runtime. |
