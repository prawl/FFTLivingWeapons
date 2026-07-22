"""KNOCKBACK, both lanes (LW-116): the composed imitation we can run today, and the watcher
that hunts the engine's own shove order during a REAL in-game Rush.

v2, 2026-07-22: full rewrite. v1 (2026-06-09, in git history) predates the render-position
crack, wrote only the band/static gx/gy mirrors, and rode the ct_probe harness that LW-93
records as dead on 1.5.1. One durable v1 fact carried forward: may-cast PROC RATES are a
Denuvo-locked engine byte, so the data lane (granting a knockback-carrying formula to a
weapon) procs at whatever native rate rides the formula, never a rate we choose.

LANE 1, shove: the staged imitation from proven parts. Play a flinch-with-displacement page
first (the owner's sweep cataloged 0x37 'pushed to the left then froze' and 0x38 its
other-direction sibling; which page matches which push vector is UNMAPPED, so --page exists),
then move the victim one tile via the live-proven teleport triple-write (combat logic tile,
node AI tile key, node world; swap_units.py is the crib source and the proof). Guards: refuses
a mid-move unit (layers disagree), refuses an OCCUPIED destination (co-tiled units = target
shadowing + movement soft-lock, proven live). Height caveat: destination tile height is
unknowable from here, so world Z is left untouched; on a height change the unit renders
floated/sunk until the engine re-stamps at its next move or turn-open (visual only,
self-heals). Do not shove the current actor.

LANE 2 is not in this file: it is a TABLE experiment (assign a Dash-family formula id to a
test weapon, restart, watch whether hits push natively). See docs/TODO.md LW-116.

LANE 3, watch: the differential tape. Samples the victim's position cluster and the
unexplored node neighborhoods at high rate and prints CHANGES only (the probe-tape habit),
tee'd to a jsonl in %TEMP%. Run it while the owner Rushes the watched unit for real.
PRE-REGISTERED HUNT: any field that changes BEFORE the world coords (n+0x4c/n+0x50) start
marching is a candidate INPUT (a destination tile or displacement order the mover then
executes); find one and knockback becomes write-the-order, the animation-register shape, and
Lane 1 retires. Wanted on the same session for contrast: one plain walk of the victim (the
mover's ordinary lerp signature) and one non-knockback hit (flinch without displacement).

    python tools\\probes\\knockback_probe.py table                       # slots + tiles
    python tools\\probes\\knockback_probe.py shove <slot> <dx> <dy> [--page H]
    python tools\\probes\\knockback_probe.py watch <slot> [secs]
"""
import json
import os
import pathlib
import struct
import sys
import time

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
from battle_cheats import rpm, ru8, ru16, wu8, wu16, _require_game

UNITS = 0x141853CE0
HEAD = 0x140D3A410
REQ = 0x10            # anim request register (input poke PASSED live 2026-07-21)
FLINCH_PUSHED = 0x37  # owner catalog: 'pushed to the left then froze'; 0x38 = other direction


def u64(a):
    b = rpm(a, 8)
    return None if b is None else struct.unpack("<Q", b)[0]


def node_of(slot):
    cur = u64(HEAD)
    for _ in range(64):
        if not cur:
            return None
        if u64(cur + 0x148) == UNITS + slot * 0x200:
            return cur
        cur = u64(cur)
    return None


def tiles_in_use():
    """Every noded unit's logic tile, for the occupancy refusal."""
    out = {}
    cur = u64(HEAD)
    for _ in range(64):
        if not cur:
            break
        c = u64(cur + 0x148)
        off = (c or 0) - UNITS
        if c and 0 <= off < 21 * 0x200 and off % 0x200 == 0:
            out[(ru8(c + 0x4F), ru8(c + 0x50))] = off // 0x200
        cur = u64(cur)
    return out


def cmd_table():
    for (x, y), s in sorted(tiles_in_use().items(), key=lambda kv: kv[1]):
        print(f"slot {s:>2}  tile ({x},{y})")


TILE_MAX = 30       # the repo's own gx/gy sanity ceiling (battle_cheats._is_valid_entry)
TURNFLAG_C = 0x1B8  # combat-relative per-unit turn flag (band +0x19C, band at combat +0x1C)


def _validate(slot, node, dx, dy):
    """Every precondition, in one place, so it can be re-run immediately before the writes.
    Returns (combat_addr, nx, ny) or exits. Guards: the unit is not mid-move (layers agree),
    the destination is on the map, unoccupied, and the unit's turn is not open."""
    c = UNITS + slot * 0x200
    flag = ru8(c + TURNFLAG_C)
    if flag is None or flag == 1:
        print("that unit's turn is OPEN (or unreadable); shoving the current actor is refused.")
        sys.exit(1)
    gx, gy = ru8(c + 0x4F), ru8(c + 0x50)
    tx, ty = ru8(node + 0x88), ru8(node + 0x89)
    if None in (gx, gy, tx, ty):
        print("position unreadable; refusing.")
        sys.exit(1)
    if (gx, gy) != (tx, ty):
        print(f"layers disagree ({gx},{gy}) vs ({tx},{ty}): unit mid-move, refusing.")
        sys.exit(1)
    nx, ny = gx + dx, gy + dy
    if not (0 <= nx <= TILE_MAX and 0 <= ny <= TILE_MAX):
        print(f"destination ({nx},{ny}) is off the map; refusing (a negative would mask to 255).")
        sys.exit(1)
    occ = tiles_in_use()
    if (nx, ny) in occ:
        print(f"destination ({nx},{ny}) occupied by slot {occ[(nx, ny)]}: refusing (co-tile soft-lock).")
        sys.exit(1)
    return c, nx, ny


def cmd_shove(slot, dx, dy, page):
    if abs(dx) > 1 or abs(dy) > 1 or (dx == 0 and dy == 0):
        print("dx/dy each in -1..1 and not both zero; one tile per shove.")
        sys.exit(1)
    node = node_of(slot)
    if not node:
        print("unit not noded; aborting.")
        sys.exit(1)
    c, nx, ny = _validate(slot, node, dx, dy)
    print(f"slot {slot}: ({ru8(c + 0x4F)},{ru8(c + 0x50)}) -> ({nx},{ny}), flinch page {page:#04x} first")
    print("NOTE: tile EXISTENCE and walkability are not validated, only occupancy and bounds; "
          "eyeball the destination before saying yes.")
    if input("SHOVE? (y/n) ").strip().lower() != "y":
        print("aborted.")
        return
    wu16(node + REQ, (page + 1) & 0xFFFF)           # stagger theater first
    time.sleep(0.15)                                # let the flinch start reading
    # Re-validate: the confirm above is unbounded human time and the sleep is another 150ms, so
    # the world may have moved. Nothing has been written to a position layer yet, so an abort
    # here is clean (the flinch page self-heals at the unit's next event).
    c, nx, ny = _validate(slot, node, dx, dy)
    wu8(c + 0x4F, nx & 0xFF); wu8(c + 0x50, ny & 0xFF)                # logic tile
    wu8(node + 0x88, nx & 0xFF); wu8(node + 0x89, ny & 0xFF)          # AI tile key (layer byte kept)
    wu16(node + 0x4C, 28 * nx + 14); wu16(node + 0x50, 28 * ny + 14)  # world X/Y; Z untouched
    print("shoved. EYEBALL: staggered back one tile? The flinch pose holds until their next "
          "event (self-heals); a height change renders floated/sunk until the same re-stamp.")


# Watch regions: (base_kind, start, length). Combat covers the logic tile + CT neighborhood;
# the node spans cover the request register, the world transform, the tile key, the mode word,
# and the output block, PLUS the unexplored gaps between them; the hunt lives in the gaps.
REGIONS = [("c", 0x40, 0x20), ("n", 0x00, 0xB0), ("n", 0x120, 0x30), ("n", 0x420, 0x08)]


def cmd_watch(slot, secs):
    node = node_of(slot)
    if not node:
        print("unit not noded; aborting.")
        sys.exit(1)
    c = UNITS + slot * 0x200
    base = {"c": c, "n": node}
    tape_path = pathlib.Path(os.environ.get("TEMP", ".")) / f"knockback_watch_{time.strftime('%H%M%S')}.jsonl"
    tape = open(tape_path, "a", encoding="utf-8")
    print(f"slot {slot} combat 0x{c:X} node 0x{node:X}: watching {secs:.0f}s, changes only")
    print(f"tape: {tape_path}")
    prev = {}
    t0 = time.time()
    sweeps = 0
    try:
        while time.time() - t0 < secs:
            for kind, start, length in REGIONS:
                buf = rpm(base[kind] + start, length)
                if buf is None:
                    continue
                old = prev.get((kind, start))
                if old is not None and buf != old:
                    for i, (a, b) in enumerate(zip(old, buf)):
                        if a != b:
                            rec = {"t": round(time.time() - t0, 4),
                                   "at": f"{kind}+{start + i:#05x}", "old": a, "new": b}
                            print(f"  +{rec['t']:8.4f}s {rec['at']}: {a:#04x} -> {b:#04x}")
                            tape.write(json.dumps(rec) + "\n")
                    tape.flush()      # a Ctrl+C or a game crash must not eat the decisive lines
                prev[(kind, start)] = buf
            sweeps += 1
            time.sleep(0.004)         # ~250Hz: fast enough to catch an order before the lerp,
                                      # slow enough not to spin a whole core on RPM calls
    except KeyboardInterrupt:
        print("stopped early by Ctrl+C.")
    tape.close()
    print(f"done: {sweeps} sweeps in {time.time() - t0:.0f}s (~{sweeps / max(time.time() - t0, 1):.0f} Hz). "
          f"Tape kept at {tape_path}")
    print("HUNT: fields changing BEFORE n+0x4c/n+0x50 start marching = candidate shove ORDER.")


def main():
    _require_game()
    argv = sys.argv[1:]
    page = FLINCH_PUSHED
    if "--page" in argv:
        i = argv.index("--page")
        if i + 1 >= len(argv):
            print("--page needs a hex page id, e.g. --page 38"); sys.exit(1)
        page = int(argv[i + 1], 16); del argv[i:i + 2]
    if argv and argv[0] == "table":
        cmd_table()
    elif len(argv) >= 4 and argv[0] == "shove":
        cmd_shove(int(argv[1]), int(argv[2]), int(argv[3]), page)
    elif len(argv) >= 2 and argv[0] == "watch":
        cmd_watch(int(argv[1]), float(argv[2]) if len(argv) > 2 else 45.0)
    else:
        print(__doc__)


if __name__ == "__main__":
    main()
