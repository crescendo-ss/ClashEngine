#!/usr/bin/env python3
"""Interactive CLI that walks the user through one ClashEngine game type plus one or
more queues attached to it, then emits a [ClashEngine] config block on stdout.

Questions and validation mirror the parsers under src/ClashEngine/Config/ so the
output can be pasted straight into global.conf. Prompts are printed to stderr so
the output is pipeable: `python tools/queue_designer.py > my.conf`.
"""

from __future__ import annotations

import re
import sys
from dataclasses import dataclass, field
from typing import Callable, TypeVar

# Ship-name vocabulary mirrors src/ClashEngine/Config/ShipBySlotParser.cs.
SHIP_NAMES = ["warbird", "javelin", "spider", "leviathan", "terrier", "weasel", "lancaster", "shark"]
SHIP_ALIASES = {"lev": 3, "terr": 4, "lanc": 6}

# Mirrors ClashEngine.Core.Queue.ItemsAction. If the enum gains a value, add it here.
ITEMS_ACTIONS = ["full", "restore", "burn"]

T = TypeVar("T")


# ----- prompt helpers -----

def _err(msg: str = "") -> None:
    print(msg, file=sys.stderr)


def _hint(msg: str) -> None:
    print(f"  ({msg})", file=sys.stderr)


def _ask(prompt: str) -> str:
    sys.stderr.write(prompt)
    sys.stderr.flush()
    line = sys.stdin.readline()
    if line == "":
        raise KeyboardInterrupt
    return line.rstrip("\n").rstrip("\r")


def _retry(prompt: str, parse: Callable[[str], T]) -> T:
    while True:
        try:
            return parse(_ask(prompt))
        except ValueError as ex:
            _hint(str(ex))


def required_string(label: str) -> str:
    def parse(s: str) -> str:
        s = s.strip()
        if not s:
            raise ValueError("required")
        return s
    return _retry(f"{label}: ", parse)


def optional_string(label: str) -> str | None:
    s = _ask(f"{label}: ").strip()
    return s or None


def int_default(label: str, default: int, *, low: int | None = None, high: int | None = None) -> int:
    def parse(s: str) -> int:
        s = s.strip()
        if not s:
            return default
        try:
            v = int(s)
        except ValueError:
            raise ValueError("not a valid integer")
        if low is not None and v < low:
            raise ValueError(f"must be >= {low}")
        if high is not None and v > high:
            raise ValueError(f"must be <= {high}")
        return v
    return _retry(f"{label} [{default}]: ", parse)


def optional_int(label: str, *, low: int | None = None, high: int | None = None) -> int | None:
    def parse(s: str) -> int | None:
        s = s.strip()
        if not s:
            return None
        try:
            v = int(s)
        except ValueError:
            raise ValueError("not a valid integer")
        if low is not None and v < low:
            raise ValueError(f"must be >= {low}")
        if high is not None and v > high:
            raise ValueError(f"must be <= {high}")
        return v
    return _retry(f"{label}: ", parse)


def optional_float(label: str, *, low: float | None = None, high: float | None = None) -> float | None:
    def parse(s: str) -> float | None:
        s = s.strip()
        if not s:
            return None
        try:
            v = float(s)
        except ValueError:
            raise ValueError("not a valid number")
        if low is not None and v < low:
            raise ValueError(f"must be >= {low}")
        if high is not None and v > high:
            raise ValueError(f"must be <= {high}")
        return v
    return _retry(f"{label}: ", parse)


# Accepts HH:MM:SS, MM:SS, or bare integer seconds; mirrors ConfigReadHelpers.TryReadTimeSpan.
_TIME_RE = re.compile(r"^(?:(\d+):)?(\d+):(\d+)$")


def _parse_timespan(s: str) -> int:
    """Returns total seconds. Raises ValueError on bad input."""
    if s.isdigit() or (s.startswith("-") and s[1:].isdigit()):
        return int(s)
    m = _TIME_RE.match(s)
    if not m:
        raise ValueError("expected HH:MM:SS, MM:SS, or integer seconds")
    h = int(m.group(1)) if m.group(1) else 0
    mm = int(m.group(2))
    ss = int(m.group(3))
    return h * 3600 + mm * 60 + ss


def optional_timespan(
    label: str, *, positive: bool = False, non_negative: bool = False, minimum_seconds: int | None = None,
) -> int | None:
    def parse(s: str) -> int | None:
        s = s.strip()
        if not s:
            return None
        v = _parse_timespan(s)
        if positive and v <= 0:
            raise ValueError("must be > 0")
        if non_negative and v < 0:
            raise ValueError("must be >= 0")
        if minimum_seconds is not None and v < minimum_seconds:
            raise ValueError(f"must be >= {minimum_seconds}s")
        return v
    return _retry(f"{label}: ", parse)


def yes_no(label: str, default: bool) -> bool:
    hint = "Y/n" if default else "y/N"
    def parse(s: str) -> bool:
        s = s.strip().lower()
        if not s:
            return default
        if s in ("y", "yes"):
            return True
        if s in ("n", "no"):
            return False
        raise ValueError("answer y or n")
    return _retry(f"{label} [{hint}]: ", parse)


def choice(label: str, options: list[str], default: str) -> str:
    assert default in options
    def parse(s: str) -> str:
        s = s.strip().lower()
        if not s:
            return default
        if s in options:
            return s
        raise ValueError("one of: " + "/".join(options))
    return _retry(f"{label} [{default}]: ", parse)


def spawn_list(label: str) -> list[tuple[int, int]]:
    """Parses 'x,y; x,y; ...' with x,y in short range -- mirrors SpawnSetParser."""
    def parse(s: str) -> list[tuple[int, int]]:
        s = s.strip()
        if not s:
            raise ValueError("at least one point required, e.g. '8000,4000; 8016,4000'")
        out: list[tuple[int, int]] = []
        for pair in (p.strip() for p in s.split(";") if p.strip()):
            xy = [t.strip() for t in pair.split(",")]
            if len(xy) != 2:
                raise ValueError(f"'{pair}' is not 'x,y'")
            try:
                x, y = int(xy[0]), int(xy[1])
            except ValueError:
                raise ValueError(f"'{pair}' contains a non-integer coordinate")
            if not (-32768 <= x <= 32767 and -32768 <= y <= 32767):
                raise ValueError(f"'{pair}' coordinates must fit in a signed 16-bit short")
            out.append((x, y))
        if not out:
            raise ValueError("at least one point required")
        return out
    return _retry(f"{label}: ", parse)


def ship_list(label: str, expected: int) -> list[int]:
    def parse(s: str) -> list[int]:
        s = s.strip()
        if not s:
            raise ValueError("required")
        out: list[int] = []
        for token in (t.strip() for t in s.split(",") if t.strip()):
            lower = token.lower()
            if lower.isdigit():
                idx = int(lower)
                if not (0 <= idx <= 7):
                    raise ValueError(f"'{token}' must be 0..7")
            elif lower in SHIP_NAMES:
                idx = SHIP_NAMES.index(lower)
            elif lower in SHIP_ALIASES:
                idx = SHIP_ALIASES[lower]
            else:
                raise ValueError(f"'{token}' is not a ship name or 0..7")
            out.append(idx)
        if len(out) != expected:
            raise ValueError(f"need exactly {expected}, got {len(out)}")
        return out
    return _retry(f"{label} ({expected} comma-separated): ", parse)


# ----- data -----

@dataclass
class GameType:
    name: str
    id: int
    team_count: int
    players_per_team: int
    kill_target: int
    lives: int
    time_limit_s: int | None
    team_collapse_grace_s: int | None
    ship_change_grace_s: int | None
    return_items_action: str
    warp_on_spawn: bool
    spawns: list[list[tuple[int, int]]] | None
    max_spawn_drift_tiles: int | None
    ships: list[list[int]] | None
    staging_duration_s: int | None
    countdown_duration_s: int | None
    knockout_spec_delay_s: int | None


@dataclass
class Queue:
    name: str
    casual: bool
    match_arena: str | None
    look_ahead: int | None
    relax_time_s: int | None
    hold_window_s: int | None
    quality_ceiling: float | None
    vetoes_required: int | None
    veto_window_s: int | None
    promote_winners: bool
    max_consecutive_defenses: int | None


# ----- forms -----

def prompt_game_type() -> GameType:
    _err("--- Game type ---")
    name = required_string("Name (referenced by Queue<i>GameType)")
    id_ = int_default("Id (rating bucket, 0..255)", 1, low=0, high=255)
    team_count = int_default("TeamCount (>=2)", 2, low=2)
    players_per_team = int_default("PlayersPerTeam (>=1)", 4, low=1)
    kill_target = int_default("KillTarget (per-team kills to win; 0 = unset)", 0, low=0)
    time_limit = optional_timespan("TimeLimit (blank to skip)", positive=True)
    if kill_target == 0 and time_limit is None:
        _hint("note: with no KillTarget and no TimeLimit, the engine falls back to 30 kills")
    lives = int_default("Lives per player (0 = unlimited)", 0, low=0)
    team_collapse = optional_timespan("TeamCollapseGrace (blank = 10s)", non_negative=True)
    ship_change = optional_timespan("ShipChangeGracePeriod (blank = 10s)", non_negative=True)
    return_action = choice("ReturnItemsAction", ITEMS_ACTIONS, "full")

    _err()
    _err("Spawns:")
    warp = yes_no("WarpOnSpawn (warp teams to chosen spawn at setup/GO)?", False)
    spawns: list[list[tuple[int, int]]] | None = None
    max_drift: int | None = None
    if warp:
        spawns = [spawn_list(f"  Team{t} spawn points (x,y; x,y; ...)") for t in range(1, team_count + 1)]
        max_drift = optional_int("  MaxSpawnDrift (tiles; blank to disable)", low=0)

    _err()
    ships: list[list[int]] | None = None
    if yes_no("Configure per-team ship overrides?", False):
        _err("  Names: " + ", ".join(SHIP_NAMES) + " (lev/terr/lanc abbreviations also accepted).")
        ships = [ship_list(f"  Team{t} ships", players_per_team) for t in range(1, team_count + 1)]

    _err()
    _err("Match-flow timings (blank = engine default):")
    staging = optional_timespan("  StagingDuration (default 10s)", positive=True)
    countdown = optional_timespan("  CountdownDuration (default 5s, min 5s)", minimum_seconds=5)
    knockout = optional_timespan("  KnockoutSpecDelay (default 0)", non_negative=True)

    return GameType(
        name=name, id=id_, team_count=team_count, players_per_team=players_per_team,
        kill_target=kill_target, lives=lives, time_limit_s=time_limit,
        team_collapse_grace_s=team_collapse, ship_change_grace_s=ship_change,
        return_items_action=return_action,
        warp_on_spawn=warp, spawns=spawns, max_spawn_drift_tiles=max_drift,
        ships=ships,
        staging_duration_s=staging, countdown_duration_s=countdown, knockout_spec_delay_s=knockout,
    )


def prompt_queue() -> Queue:
    name = required_string("Name (e.g. 1v1_competitive)")
    casual = choice("Matchmaking tier", ["competitive", "casual"], "competitive") == "casual"
    arena = optional_string("MatchArena (blank for none)")
    look_ahead = optional_int("LookAhead (extra candidates above the minimum; blank = 0)", low=0)
    relax_default = "0:00:45" if casual else "0:01:30"
    relax = optional_timespan(f"RelaxTime (blank = {relax_default})", positive=True)
    hold = optional_timespan("HoldWindow (blank = 10s; 0 = pop immediately)", non_negative=True)
    qc = optional_float("QualityCeiling 0..1 (blank = 0.9)", low=0.0, high=1.0)
    vetoes = optional_int("VetoesRequired (blank = 2)", low=1)
    veto_window = optional_timespan("VetoWindow (blank = 60s)", positive=True)
    promote = yes_no("PromoteWinners (KOTH mode)?", False)
    max_def = optional_int("MaxConsecutiveDefenses (blank = 3)", low=1) if promote else None
    return Queue(
        name=name, casual=casual, match_arena=arena, look_ahead=look_ahead,
        relax_time_s=relax, hold_window_s=hold, quality_ceiling=qc,
        vetoes_required=vetoes, veto_window_s=veto_window,
        promote_winners=promote, max_consecutive_defenses=max_def,
    )


def prompt_queues(gt: GameType) -> list[Queue]:
    queues: list[Queue] = []
    n = 1
    while True:
        _err()
        _err(f"--- Queue {n} (will reference game type '{gt.name}') ---")
        queues.append(prompt_queue())
        if not yes_no("Add another queue attached to this game type?", False):
            return queues
        n += 1


# ----- emit -----

def _format_time(seconds: int) -> str:
    """Round-trips through ConfigReadHelpers.TryReadTimeSpan: bare seconds for sub-minute,
    otherwise H:MM:SS."""
    if seconds == 0:
        return "0"
    if 0 < seconds < 60:
        return str(seconds)
    h, rem = divmod(seconds, 3600)
    m, s = divmod(rem, 60)
    return f"{h}:{m:02d}:{s:02d}"


def _kv(out: list[str], key: str, value: str) -> None:
    out.append(f"{key:<28} = {value}")


def emit(gt: GameType, queues: list[Queue]) -> str:
    out: list[str] = []
    out.append("[ClashEngine]")
    out.append("")
    out.append("; --- Game type ---")
    out.append("GameTypeCount = 1")
    out.append("")
    _emit_game_type(out, gt, index=1)
    out.append("")
    out.append("; --- Queues ---")
    out.append(f"QueueCount = {len(queues)}")
    for i, q in enumerate(queues, start=1):
        out.append("")
        _emit_queue(out, q, gt, index=i)
    return "\n".join(out) + "\n"


def _emit_game_type(out: list[str], gt: GameType, index: int) -> None:
    p = f"GameType{index}"
    _kv(out, f"{p}Name", gt.name)
    _kv(out, f"{p}Id", str(gt.id))
    _kv(out, f"{p}TeamCount", str(gt.team_count))
    _kv(out, f"{p}PlayersPerTeam", str(gt.players_per_team))
    if gt.kill_target > 0:
        _kv(out, f"{p}KillTarget", str(gt.kill_target))
    if gt.time_limit_s is not None:
        _kv(out, f"{p}TimeLimit", _format_time(gt.time_limit_s))
    if gt.lives > 0:
        _kv(out, f"{p}Lives", str(gt.lives))
    if gt.team_collapse_grace_s is not None:
        _kv(out, f"{p}TeamCollapseGrace", _format_time(gt.team_collapse_grace_s))
    if gt.ship_change_grace_s is not None:
        _kv(out, f"{p}ShipChangeGracePeriod", _format_time(gt.ship_change_grace_s))
    if gt.return_items_action != "full":
        _kv(out, f"{p}ReturnItemsAction", gt.return_items_action)

    if gt.warp_on_spawn:
        _kv(out, f"{p}WarpOnSpawn", "1")
        for t, team in enumerate(gt.spawns or [], start=1):
            _kv(out, f"{p}Team{t}Spawns", "; ".join(f"{x},{y}" for x, y in team))
        if gt.max_spawn_drift_tiles is not None:
            _kv(out, f"{p}MaxSpawnDrift", str(gt.max_spawn_drift_tiles))

    if gt.ships is not None:
        for t, team in enumerate(gt.ships, start=1):
            _kv(out, f"{p}Team{t}Ships", ",".join(SHIP_NAMES[i] for i in team))

    if gt.staging_duration_s is not None:
        _kv(out, f"{p}StagingDuration", _format_time(gt.staging_duration_s))
    if gt.countdown_duration_s is not None:
        _kv(out, f"{p}CountdownDuration", _format_time(gt.countdown_duration_s))
    if gt.knockout_spec_delay_s is not None:
        _kv(out, f"{p}KnockoutSpecDelay", _format_time(gt.knockout_spec_delay_s))


def _emit_queue(out: list[str], q: Queue, gt: GameType, index: int) -> None:
    p = f"Queue{index}"
    _kv(out, f"{p}Name", q.name)
    _kv(out, f"{p}GameType", gt.name)
    _kv(out, f"{p}Matchmaking", "casual" if q.casual else "competitive")
    if q.match_arena:
        _kv(out, f"{p}MatchArena", q.match_arena)
    if q.look_ahead is not None:
        _kv(out, f"{p}LookAhead", str(q.look_ahead))
    if q.relax_time_s is not None:
        _kv(out, f"{p}RelaxTime", _format_time(q.relax_time_s))
    if q.hold_window_s is not None:
        _kv(out, f"{p}HoldWindow", _format_time(q.hold_window_s))
    if q.quality_ceiling is not None:
        _kv(out, f"{p}QualityCeiling", f"{q.quality_ceiling:g}")
    if q.vetoes_required is not None:
        _kv(out, f"{p}VetoesRequired", str(q.vetoes_required))
    if q.veto_window_s is not None:
        _kv(out, f"{p}VetoWindow", _format_time(q.veto_window_s))
    if q.promote_winners:
        _kv(out, f"{p}PromoteWinners", "1")
    if q.max_consecutive_defenses is not None:
        _kv(out, f"{p}MaxConsecutiveDefenses", str(q.max_consecutive_defenses))


# ----- main -----

def main() -> int:
    _err("=== ClashEngine Queue Designer ===")
    _err("Walks you through one game type and one or more queues attached to it.")
    _err("Press Enter to accept the [default] shown in brackets. Ctrl+C to abort.")
    _err()
    try:
        gt = prompt_game_type()
        queues = prompt_queues(gt)
    except KeyboardInterrupt:
        _err()
        _err("Cancelled.")
        return 130

    _err()
    _err("=== Generated config ===")
    sys.stdout.write(emit(gt, queues))
    return 0


if __name__ == "__main__":
    sys.exit(main())
