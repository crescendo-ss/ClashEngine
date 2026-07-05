# `[ClashEngine]` config knobs — by file scope

A whitelist-oriented breakdown of every key ClashEngine reads from the `[ClashEngine]`
section, organized by **which file each key is allowed in**. The section header is
`[ClashEngine]` for everything below.

Source of truth: the parsers under `src/ClashEngine/Config/` and `ClashModule.cs`
(mirrored, with code line references, in `schema/config-knobs.json`).

---

## `global.conf` only — zone scope (plug-in tuning)

Read **only** from `global.conf`. Flat keys, not indexed.

| Key | Type | Default | Purpose |
|---|---|---|---|
| `LogVerbosity` | enum: `Off`/`Normal`/`Verbose`/`Trace` | `Normal` | Log detail (runtime-mutable via `?clashlog`) |
| `MaxPenaltyHours` | int > 0 | `6` | Hard ceiling on any penalty timeout |
| `UploadUrl` | URL | unset | Stats-upload endpoint (multipart POST) |
| `UploadApiKey` | string | unset | `X-Api-Key` for uploads |
| `StatsViewUrl` | URL template | unset | Per-match viewer URL broadcast after scoreboard |
| `RecordReplays` | 0/1 | `1` | Record matches with MatchRecorder |
| `ReplayRecordingDir` | path | `<BaseDir>/clash-replays` | Where in-flight `.replay` files land |
| `DistanceSampleHz` | int 1–50 (0 disables) | `5` | Scoreboard distance-sampler frequency |
| `ShowQueueOnEnter` | 0/1 | `1` | Auto-show the `?queue` table on arena entry |
| `EventStreamUrl` | URL | unset | Outbound queue/match event-stream endpoint |
| `EventStreamApiKey` | string | unset | `X-Api-Key` for events (falls back to `UploadApiKey`) |

---

## `arena.conf` (+ its `#include`s) — arena scope (game types & queues)

### Control / non-indexed keys

| Key | Type | Default | Purpose |
|---|---|---|---|
| `GameTypeCount` | int | `0` | How many `GameType<i>` blocks to read |
| `QueueCount` | int | `0` | How many `Queue<i>` blocks to read |
| `DefaultQueue` | string | unset | Queue `?play` resolves to with no name given |

### `GameType<i>` family — `i` = `1 … GameTypeCount`

| Key pattern | Type | Default | Notes |
|---|---|---|---|
| `GameType<i>Name` | string | — | **required** |
| `GameType<i>Label` | string | = Name | |
| `GameType<i>Description` | string | unset | |
| `GameType<i>TeamCount` | int ≥ 2 | `2` | |
| `GameType<i>PlayersPerTeam` | int ≥ 1 | `4` | |
| `GameType<i>KillTarget` | int ≥ 0 | `0` | 0 = unset |
| `GameType<i>TimeLimit` | duration | unset | |
| `GameType<i>Lives` | int ≥ 0 | `0` | 0 = unlimited |
| `GameType<i>UseStartLocation` | 0/1 | `0` | gate for the start-location keys below |
| `GameType<i>Team<t>Starts` | `x,y; x,y; …` | unset | **double-indexed**: `t` = `1 … TeamCount` (tiles) |
| `GameType<i>MaxStartDrift` | int ≥ 0 (tiles) | unset | |
| `GameType<i>Team<t>SpawnCenter` | `x,y` | unset | **double-indexed** respawn box center (tiles); self-gating |
| `GameType<i>Team<t>SpawnRadius` | int 0–511 (tiles) | `0` | per-team respawn box radius |
| `GameType<i>StagingDuration` | duration > 0 | 10s | |
| `GameType<i>CountdownDuration` | duration > 0 (min 5s) | 10s | |
| `GameType<i>KnockoutSpecDelay` | duration ≥ 0 | 0 | |
| `GameType<i>EliminationCooldown` | duration ≥ 0 | 60s | |
| `GameType<i>TeamCollapseGrace` | duration ≥ 0 | 10s | |
| `GameType<i>ShipChangeGracePeriod` | duration ≥ 0 | 10s | |
| `GameType<i>ReturnItemsAction` | enum: `full`/`restore`/`burn` | `full` | |

### `Queue<i>` family — `i` = `1 … QueueCount`

| Key pattern | Type | Default | Notes |
|---|---|---|---|
| `Queue<i>Name` | string | — | **required** |
| `Queue<i>GameType` | string | — | **required**; must match a `GameType<j>Name` |
| `Queue<i>Label` | string | = Name | |
| `Queue<i>Preset` | enum: `casual` | unset | shifts several defaults below |
| `Queue<i>MatchArena` | string | unset | |
| `Queue<i>LookAhead` | int ≥ 0 | `0` | extra candidates above `TotalPlayers`, from the front of the queue |
| `Queue<i>AlwaysChooseLongestWaiter` | 0/1 | `1` | off lets the matcher pass the queue head over for better balance |
| `Queue<i>RelaxTime` | duration > 0 | 120s (45s casual) | |
| `Queue<i>HoldWindow` | duration ≥ 0 | 10s | |
| `Queue<i>QualityCeiling` | float 0–1 | `0.9` | |
| `Queue<i>VetoesRequired` | int ≥ 1 | `2` | |
| `Queue<i>VetoWindow` | duration > 0 | 60s | |
| `Queue<i>PromoteWinners` | 0/1 | `0` | KOTH |
| `Queue<i>MaxConsecutiveDefenses` | int ≥ 1 | `3` | KOTH |
| `Queue<i>AfkWarn` | duration | 900s | 0 disables warn **and** cull |
| `Queue<i>AfkCull` | duration | 1200s | 0 = never cull |

---

## Notes for the whitelist logic

1. **Scoping is strict, and mismatched placement is silently ignored** (not an error).
   `GameType*` / `Queue*` / `GameTypeCount` / `QueueCount` / `DefaultQueue` set in
   `global.conf` are **ignored** (ClashEngine logs a one-time warning). Conversely, the
   10 plug-in-tuning keys are only ever read from `global.conf`. The whitelist can be two
   disjoint sets, one per file.

2. **Indexed keys need pattern matching, not literals.** `GameType<i>…` and `Queue<i>…`
   expand by the count keys, and `GameType<i>Team<t>Starts` (plus the respawn-box keys) have *two* indices. Suggested
   patterns:
   - `^GameType\d+(Name|Label|Description|TeamCount|PlayersPerTeam|KillTarget|TimeLimit|Lives|UseStartLocation|MaxStartDrift|StagingDuration|CountdownDuration|KnockoutSpecDelay|EliminationCooldown|TeamCollapseGrace|ShipChangeGracePeriod|ReturnItemsAction)$`
   - `^GameType\d+Team\d+(Starts|SpawnCenter|SpawnRadius)$`
   - `^Queue\d+(Name|GameType|Label|Preset|MatchArena|LookAhead|AlwaysChooseLongestWaiter|RelaxTime|HoldWindow|QualityCeiling|VetoesRequired|VetoWindow|PromoteWinners|MaxConsecutiveDefenses|AfkWarn|AfkCull)$`
