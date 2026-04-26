# ClashEngine

A skill-based matchmaking engine for [SubspaceServer .NET](https://github.com/gigamon-dev/SubspaceServer). ClashEngine forms balanced matches from a queue of players, places them on ships in a configured arena, runs the match through a staging → countdown → live → cleanup lifecycle, tracks per-player stats, and updates persistent OpenSkill ratings on completion.

---

## Repository layout

| Path | Purpose |
|---|---|
| `src/ClashEngine.Core/` | Pure-logic library: queues, matcher, team balancer, end-policies, FSMs, rating math, stats accounting. **No SubspaceServer dependencies.** |
| `src/ClashEngine/` | The SubspaceServer plug-in: adapters that wire the engine to `IGame`, `IChat`, `IPersist`, `IArenaManager`, MatchFocus / MatchLvz callbacks, etc. |
| `tests/ClashEngine.Core.Tests/` | xUnit test suite for the core library (≈500 tests). |
| `schema/match.schema.json` | JSON schema for the end-of-match upload envelope. |

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

The plug-in's post-build step copies `ClashEngine.dll` (and `ClashEngine.Core.dll`) into `SubspaceServer/Zone/bin/modules/ClashEngine/`.

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

### 3. Configure ClashEngine in `global.conf`

All engine-wide settings live under one `[ClashEngine]` section in `global.conf`. The section has three concerns:

1. **Plug-in tuning** — log verbosity, upload endpoint, replay recording.
2. **Game types** — the rules of a match (team shape, win condition, lives, spawn locations).
3. **Queues** — the matchmaking policies that draw players into a game type (matchmaking strictness, the arena to play in, look-ahead).

A complete worked example is at the end of this section. Each subsection below documents the keys.

### 4. Configure the match arena (`arena.conf`)

Each match arena needs `MatchFocus` + `MatchLvz` attached, and optionally a default queue for `?play` with no argument. Example:

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
```

### 5. Grant the chat commands

Player-facing commands have to be granted in `groupdef.dir/default` (or whichever group covers your players). Add to the bottom of the file:

```
; Clash
cmd_play
cmd_party
cmd_partymode
cmd_leaveparty
cmd_queue
cmd_rating
cmd_cancel
cmd_accept
cmd_decline
cmd_veto
cmd_helpclash
```

`?stats` and `?clashlog` are reserved for higher-privileged groups by default; grant them where appropriate.

---

## `[ClashEngine]` section reference

### Plug-in tuning

| Key | Type | Default | Notes |
|---|---|---|---|
| `LogVerbosity` | `Off` / `Normal` / `Verbose` / `Trace` | `Normal` | Runtime-mutable via `?clashlog`. `Verbose` adds debug for command inputs/outcomes, engine event translations, orchestrator phase transitions. `Trace` adds every wire event (connect/disconnect/ship/freq/kill). |
| `UploadUrl` | URL | (unset) | Stats-upload endpoint. Receives `multipart/form-data` POST with metadata JSON + replay file. |
| `UploadApiKey` | string | (unset) | Sent as `X-Api-Key` header. Both `UploadUrl` and `UploadApiKey` must be set for HTTP upload; if either is missing, ClashEngine writes match envelopes as JSON files under `<AppContext.BaseDirectory>/matches` instead. |
| `StatsViewUrl` | URL template | (unset) | Per-match viewer URL. After the end-of-match scoreboard, an arena message `Match stats: <url>` is broadcast to everyone in the match arena. The template may include a literal `{matchId}` token (replaced with the dashed GUID, e.g. `8b34f35d-b3b5-4747-941a-5cb96153641e`); without one, the id is appended. Leave unset to suppress the line. |
| `RecordReplays` | 0/1 | 1 | Record every started match using the in-plug-in `MatchRecorder`. |
| `ReplayRecordingDir` | path | `<AppContext.BaseDirectory>/clash-replays` | Where in-flight `.replay` files land. Files are deleted after a successful upload. |
| `DistanceSampleHz` | int (1–50) | 5 | Frequency of the periodic distance-to-nearest-enemy sampler used for the scoreboard's `dE` column. Set to `0` to disable. |

### Game types

`GameTypeCount = N` → ClashEngine reads `GameType1` … `GameTypeN`. A game type is the rules of one match shape; multiple queues can reference one game type.

#### Rules

| Key | Type | Default | Notes |
|---|---|---|---|
| `GameType<i>Name` | string | required | Referenced by `Queue<j>GameType`. Case-insensitive. |
| `GameType<i>Id` | int 0–255 | `i` | Bucket the rating store keys ratings under. **Two queues with different rules but the same Id share ratings** — set distinct Ids for distinct rulesets. |
| `GameType<i>TeamCount` | int ≥ 2 | 2 | Number of teams. |
| `GameType<i>PlayersPerTeam` | int ≥ 1 | 4 | Players per team. |
| `GameType<i>KillTarget` | int ≥ 0 | 30 (when nothing else set) | Per-team kills required to win. `0` = unset (use `TimeLimit` instead). |
| `GameType<i>TimeLimit` | `HH:MM:SS` | (unset) | Leader at this elapsed time wins. A tie at the limit triggers unlimited sudden-death overtime — next kill that breaks the tie wins. |
| `GameType<i>Lives` | int ≥ 0 | 0 = unlimited | Elimination matches: each player has this many lives; a player at 0 lives is eliminated and released from the match-roster (cooldown applies). |
| `GameType<i>TeamCollapseGrace` | seconds or `HH:MM:SS` | `10` | How long an entire team can be without any Active or Pending players before forfeiting. Distinct from per-player grace; a team-wide simultaneous drop gets this window to recover before the surviving teams take a forfeit win. |
| `GameType<i>ShipChangeGracePeriod` | seconds or `HH:MM:SS` | `10` | After a non-fatal death, the player has this long to change ships before being re-locked to whatever ship they're currently in for the rest of the life. Mid-life ship changes are otherwise forbidden because each ship transition refreshes Continuum item counts. Freq (team) changes are blocked outright for match participants regardless of this value. Set to `0` to forbid every in-match ship change. Knockouts (last life) don't open this window — the orchestrator's `KnockoutSpecDelay` handles that path. |

`KillTarget` and `TimeLimit` may both be set, in which case whichever fires first ends the match.

#### Spawns and warp behavior

These keys move the match's pre-GO physical setup off the arena's default spawn into something deterministic. All spawn behavior is gated by `WarpOnSpawn`; with the gate off, the configured coordinates are silently ignored and players use the arena's normal spawn points.

| Key | Type | Default | Notes |
|---|---|---|---|
| `GameType<i>WarpOnSpawn` | 0/1 | 0 (off) | Master switch. Off → no warp; arena defaults apply. |
| `GameType<i>Team<t>Spawns` | `x,y; x,y; ...` | (unset) | Per-team **set** of candidate spawn coordinates in pixels. At setup the orchestrator picks one entry uniformly at random and warps every player on that team to it. Multiple coords give a team a rotating spawn-point pool; a single coord is fine too. |
| `GameType<i>MaxSpawnDrift` | int ≥ 0 (tiles) | (unset) | Maximum drift in tiles (1 tile = 16 px) a player may travel from the team's chosen spawn during Staging and Countdown. Drifters get warped back. `null` or `0` disables drift enforcement. |

**Pre-GO drift enforcement.** While `WarpOnSpawn = 1`, every position packet during Staging and Countdown is checked against the team's chosen spawn; players past the threshold are warped back to that spawn. At GO, the entire team is re-warped to the chosen spawn one final time so any sub-threshold drift is snapped flush.

#### Per-slot ships

| Key | Type | Default | Notes |
|---|---|---|---|
| `GameType<i>Team<t>Ships` | comma-separated names or 0..7 | (unset → all Warbird) | Per-team list of ships, one per slot. Names are case-insensitive (`warbird`, `javelin`, `spider`, `leviathan`/`lev`, `terrier`/`terr`, `weasel`, `lancaster`/`lanc`, `shark`); numbers are the canonical Continuum ship indices. Each team's count must equal `PlayersPerTeam`; a mismatch (or any team unset while another is set) skips the override and reverts to all-Warbird. |

#### Match-flow timings (warmup / countdown / spec grace)

| Key | Type | Default | Notes |
|---|---|---|---|
| `GameType<i>StagingDuration` | seconds or `HH:MM:SS` | `10` | Length of the warmup window between match formation and the pre-GO countdown. Players have this long to demonstrate non-idleness (move/rotate/fire). The first detected movement DMs the player a confirmation; players still idle at the end fail the readiness check and the match is cancelled. |
| `GameType<i>CountdownDuration` | seconds or `HH:MM:SS` | `3` | Length of the pre-GO countdown. The orchestrator broadcasts a tick every second (`Starting in N seconds!` → `-2-` → `-1-` → `GO!`); set this to `N` seconds to control how many ticks players see. |
| `GameType<i>KnockoutSpecDelay` | seconds or `HH:MM:SS` | `0` | Grace between a player's last-life death and the forced spec, so residual mines/bombs they just fired can still land. Only meaningful for elimination matches (`Lives > 0`); match-end cleanup specs everyone immediately regardless. |

### Queues

`QueueCount = N` → ClashEngine reads `Queue1` … `QueueN`. Each queue is one matchmaking pool that produces matches under a particular game type.

| Key | Type | Default | Notes |
|---|---|---|---|
| `Queue<i>Name` | string | required | Chat-facing queue name (e.g. `?play 1v1`). Case-insensitive. |
| `Queue<i>GameType` | string | required | Must match a `GameType<j>Name`. Inherits the game type's spawn config. |
| `Queue<i>Matchmaking` | `competitive` / `casual` | `competitive` | Sets quality-policy bands and ship-balancer strictness. Casual relaxes faster and tolerates wider rating spreads. |
| `Queue<i>MatchArena` | string | (none) | Arena to send players to for the match. Recommended: dedicated, in `PermanentArenas`. |
| `Queue<i>LookAhead` | int ≥ 0 | 0 (strict FIFO) | Extra candidates above the minimum required (`TotalPlayers`) the matcher considers when looking for the best partition. `LookAhead = 4` on a 4v4 means a pool of 12 candidates. |
| `Queue<i>RelaxTime` | `HH:MM:SS` | `0:01:30` (comp), `0:00:45` (casual) | Quality-relaxation duration: how fast the quality threshold falls from `qStart` to `qFloor`. Longer = stricter early but eventually accepts weaker matches; shorter = takes whatever it can sooner. |
| `Queue<i>HoldWindow` | `HH:MM:SS` | `0:00:10` | Once a viable partition is found, the matcher waits up to this duration to see if a better one arrives. Set to `0` to pop immediately. |
| `Queue<i>QualityCeiling` | float [0,1] | `0.9` | If a held candidate's quality reaches this, pop without waiting out the hold window. |
| `Queue<i>VetoesRequired` | int ≥ 1 | `2` | Number of distinct match participants who must `?veto` a pending griefing penalty within `VetoWindow` to rescind it. |
| `Queue<i>VetoWindow` | `HH:MM:SS` | `0:01:00` | Open period for vetos after a griefing flag fires. Penalty becomes final at the end of the window if the threshold wasn't reached. |
| `Queue<i>PromoteWinners` | 0/1 | `0` | KOTH ("king of the hill") mode: the winning team's players are auto-re-enqueued at the head of this queue after a Completed match. Off by default. |
| `Queue<i>MaxConsecutiveDefenses` | int ≥ 1 | `3` | Max consecutive wins a champion can defend before being sent to the back of the queue to give challengers a clean shot. Only meaningful with `PromoteWinners = 1`. |

### Per-arena `DefaultQueue` (in `arena.conf`)

```ini
[ClashEngine]
DefaultQueue = 1v1
```

Sets the queue `?play` resolves to when the player issues the command without an explicit queue name *from this arena*. Optional; without it, `?play` requires the player to name a queue.

### Worked example: 1v1 elimination

The 1v1 elimination setup bundled with the SubspaceServer test zone (under `SubspaceServer/Zone/conf/global.conf` and `SubspaceServer/Zone/arenas/1v1comp/`) is the canonical reference for what these keys look like in production. Reproduced here so README and zone stay in sync.

`global.conf`:

```ini
[ClashEngine]

; --- 1. Plug-in tuning ---
LogVerbosity        = Verbose
UploadUrl           = http://localhost:8080/api/matches
UploadApiKey        = <secret>
RecordReplays       = 1
ReplayRecordingDir  = clash-replays
DistanceSampleHz    = 5

; --- 2. Game types ---
GameTypeCount = 1

GameType1Name           = elimination_1v1
GameType1Id             = 1
GameType1TeamCount      = 2
GameType1PlayersPerTeam = 1
GameType1KillTarget     = 3
;GameType1TimeLimit     = 0:10:00          ; uncomment to add a time cap
GameType1Lives          = 0
GameType1WarpOnSpawn       = 1
GameType1Team1Spawns       = 7680,4096; 7680,4112; 7680,4128
GameType1Team2Spawns       = 8704,4096; 8704,4112; 8704,4128
GameType1MaxSpawnDrift     = 6             ; tiles
GameType1StagingDuration   = 8             ; seconds (default 10)
GameType1CountdownDuration = 3             ; seconds (default 3)

; --- 3. Queues (two queues, same ruleset, different strictness) ---
QueueCount = 2

; Ranked competitive: small look-ahead, hold-window for late better arrivals.
Queue1Name           = 1v1
Queue1GameType       = elimination_1v1
Queue1Matchmaking    = competitive
Queue1MatchArena     = 1v1comp
Queue1LookAhead      = 4
Queue1RelaxTime      = 0:01:30
Queue1HoldWindow     = 0:00:10
Queue1QualityCeiling = 0.90

; Casual: same arena & rules; pops fast, half rating weight.
Queue2Name           = 1v1casual
Queue2GameType       = elimination_1v1
Queue2Matchmaking    = casual
Queue2MatchArena     = 1v1comp
Queue2LookAhead      = 0
Queue2RelaxTime      = 0:00:30
Queue2HoldWindow     = 0:00:00
Queue2QualityCeiling = 0.70
```

This shows off:

- **Multiple queues sharing a game type.** Both `1v1` and `1v1casual` run under `elimination_1v1`, so they share the same rules (and rating bucket via `Id = 1`). They differ only in matchmaking strictness.
- **Per-team spawn pools.** Three candidate spawn points per team — the orchestrator picks one at random for each match, so consecutive games don't always start in identical positions.
- **Drift enforcement.** `MaxSpawnDrift = 6` warps anyone who wanders more than 6 tiles from the chosen spawn back during Staging and Countdown.
- **Explicit matchmaker tuning.** `LookAhead`, `RelaxTime`, `HoldWindow`, and `QualityCeiling` are spelled out for both queues — competitive holds out for balance, casual takes whatever it can get fast.

`Zone/arenas/1v1comp/arena.conf` is the standard arena conf with `MatchFocus` + `MatchLvz` attached, `InitialSpec = 1`, `[SS.Matchmaking.MatchFocus] FilterKillPackets = 1`, and `[ClashEngine] DefaultQueue = 1v1`. Add `1v1comp` to `PermanentArenas` and grant the chat commands in `groupdef.dir/default`. That's the full setup.

---

## Match lifecycle

A match progresses through five orchestrator phases:

1. **Setup.** Players are warped into the configured `MatchArena`, set to their assigned ship + freq, ship-locked, and (if `WarpOnSpawn`) warped to the team's chosen spawn.
2. **Staging** (`StagingDuration`, default 10 s). Idle detection: each player must demonstrate non-idleness via rotation, movement, or weapon fire. The first detected movement DMs the player `Got it -- you're ready. Standby for the countdown.` so they know the readiness check has registered. Drift enforcement runs here. Any player still idle at the end → match cancelled, idle players flagged AFK.
3. **Countdown** (`CountdownDuration`, default 3 s). Broadcasts `All set! Starting in N seconds!` → `-(N-1)-` → … → `-1-` → `GO!`. Drift enforcement still active. At `GO!` the team re-warps to the chosen spawn and is unlocked.
4. **Live.** Engine FSM runs end-policy, kill counting, lives tracking, team-collapse detection (if a team has no live members for the team-collapse grace window, they forfeit; surviving teams hear a 10 s warning).
5. **Cleanup.** Match-end summary chat broadcast (`Match over! Team A/B wins.` plus the full scoreboard table). Players are unlocked and sent to spec.

---

## Stats and replays

When a match ends, ClashEngine builds a `MatchPayload` (schema in `schema/match.schema.json`) containing:

- Match metadata (id, queue, game type, arena, start/end times, final state).
- Ranked teams with their scores.
- Every participant's full stats: K/D/A/KOs/teamkills, decay-weighted KillDamage and ForcedRepelDamage/Credit, per-weapon shot accounting, item uses, wasted items, distance samples, life timeline, pre- and post-match rating.

If `UploadUrl` + `UploadApiKey` are configured, the payload + replay file are POSTed as `multipart/form-data`. Otherwise the payload is written to disk under `<AppContext.BaseDirectory>/matches/{matchId}.json` for batch upload by another process.

The same payload structure is used to render the in-game scoreboard at match end and the on-demand `?stats` table for spectators (resolved through `IMatchFocus`).

---

## Player commands

| Command | Group | Notes |
|---|---|---|
| `?play [comp\|casual] <queue>` | player | Queue for the next match. Tier defaults to `comp`. Without a queue name, falls back to the arena's `DefaultQueue`. |
| `?queue <queue>` | player | Show who's queued and how long they've been waiting. |
| `?cancel` | player | Leave every queue you're in. |
| `?party <player1>[,<player2>,...]` | player | Invite players to a group; group queues atomically and is matched as a unit. |
| `?accept [inviter]` | player | Accept a pending group invitation. |
| `?decline [inviter]` | player | Decline a pending invitation. |
| `?rating` | player | Show your skill rating per game type. |
| `?stats` | player | Show the live scoreboard for the match you're in or spectating. |
| `?veto <player>` | player | Vote to overturn a pending griefing penalty against a match participant. |
| `?helpclash` | player | List the player commands. |
| `?clashlog [off\|normal\|verbose\|trace]` | mod | Read or set ClashEngine debug verbosity at runtime. |
