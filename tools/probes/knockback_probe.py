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
    python tools\\probes\\knockback_probe.py order <slot> <dx> <dy>     # WRITE the destination

LANE 3 SUCCEEDED 2026-07-22, first run. The owner Rushed a unit from (9,4) to (8,4) while `watch`
recorded, and the tape found the order: 18ms BEFORE any world coordinate moved, the engine wrote
mode `+0x12` 0x04 to 0x1A, counter `+0x8B` 0 to 7, and destination X `+0x8C` 0x09 to 0x08. The
current tile (`+0x88`) and the combat logic tile did not commit until 138ms later. So the node's
tile block is TWO triples: `+0x88/89/8A` = where the unit IS, `+0x8C/8D/8E` = where it is GOING.
The same lead-then-follow drives an ordinary walk on the same tape (dest steps, current follows
~84ms behind, mode 0x0D instead of 0x1A), which means this is not the knockback mechanism, it is
THE MOVEMENT MECHANISM and knockback is one mode of it. See the LIVE_LEDGER Uncertain row.

`order` is the write test that follows: set the destination and let the ENGINE move the unit,
animated and lerped, instead of our teleport's hard cut. Untested when written.
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


DEST_X, DEST_Y, DEST_L = 0x8C, 0x8D, 0x8E   # the destination triple (read 2026-07-22)
STEP = 0x8B                                  # step/phase counter: 0 at rest, 7 at a shove order
MOVE_MODE = 0x12                             # 0x04 rest, 0x0D walking, 0x1A knockback
KICK_MODE = 0x1A                             # the observed forced-move mode (--kick)
KICK_STEP = 0x07                             # the counter value the engine set at the shove


def cmd_order(slot, dx, dy, kick=False):
    """THE WRITE TEST for the ordered-movement read. Write only the DESTINATION and let the
    engine do the moving: if the premise holds, the unit slides there animated, and the engine
    itself commits the current tile and the combat logic tile with no help from us.

    PRE-REGISTERED OUTCOMES:
      PASS ......... the unit visibly slides one tile and `table` afterwards shows the combat
                     logic tile moved. The engine did the work; our teleport becomes a fallback.
      IGNORED ...... destination reverts to the current tile within a second and nothing moves:
                     the field is an OUTPUT of the mover, not an input. Ledger the negative; the
                     shove imitation stands.
      DESYNC ....... it slides but the logic tile never commits: a partial lever, needs the mode
                     byte and/or the counter set too. Try again with --mode.
    Round 1 writes the destination ONLY, one variable at a time; the mode byte and the counter
    are deliberately left alone so a pass proves the minimal claim.

    ROUND 1 RESULT (owner live 2026-07-22): INERT BUT STICKY, a fourth outcome none of the three
    above predicted. The destination held at the written tile across the full 1.2s sample and was
    never reverted, so nothing else owns the field at rest and it is not a per-frame output. But
    mode stayed 0x04 and the step counter stayed 0, and the unit did not move. Reading: the
    destination says WHERE, and the mode byte is what makes the mover RUN. Hence --kick.

    ROUND 2, --kick: write the destination FIRST, then the step counter, then the mode byte last,
    which is the order the engine's own knockback used (dest and counter at t=12.2472, motion at
    t=12.2652). Mode 0x1A is the observed forced-move mode. If the mover runs, it should lerp the
    world transform and commit both the current tile and the combat logic tile by itself.
    RISK, stated plainly: this half-drives an engine state machine. If it runs to a stale or
    illegal destination the unit could end up desynced or wedged mid-step. Use a throwaway
    battle, never the current actor, and expect that ending the battle clears any mess."""
    node = node_of(slot)
    if not node:
        print("unit not noded; aborting.")
        sys.exit(1)
    c, nx, ny = _validate(slot, node, dx, dy)
    cur = (ru8(node + 0x88), ru8(node + 0x89))
    dest = (ru8(node + DEST_X), ru8(node + DEST_Y))
    print(f"slot {slot}: current tile {cur}, destination field reads {dest}, "
          f"mode {ru8(node + MOVE_MODE):#04x}, step {ru8(node + STEP):#04x}")
    if cur != dest:
        print("destination already differs from current: the unit is mid-move. Refusing.")
        sys.exit(1)
    if kick:
        print(f"write plan: destination -> ({nx},{ny}), then step {KICK_STEP:#04x}, then mode "
              f"{KICK_MODE:#04x} LAST (the order the engine's own knockback used).")
        print("RISK: this half-drives the mover's state machine. Throwaway battle only.")
    else:
        print(f"write plan: destination -> ({nx},{ny}). Mode and step byte left UNTOUCHED (round 1).")
    if input("ORDER? (y/n) ").strip().lower() != "y":
        print("aborted.")
        return
    wu8(node + DEST_X, nx & 0xFF)
    wu8(node + DEST_Y, ny & 0xFF)
    if kick:
        wu8(node + STEP, KICK_STEP)       # counter first, as the engine did
        wu8(node + MOVE_MODE, KICK_MODE)  # mode LAST: this is the go signal
    for dt in (0.05, 0.15, 0.3, 0.6, 1.2):
        time.sleep(dt if dt == 0.05 else 0)
        print(f"  +{dt:4.2f}s dest=({ru8(node + DEST_X)},{ru8(node + DEST_Y)}) "
              f"cur=({ru8(node + 0x88)},{ru8(node + 0x89)}) "
              f"logic=({ru8(c + 0x4F)},{ru8(c + 0x50)}) mode={ru8(node + MOVE_MODE):#04x} "
              f"step={ru8(node + STEP):#04x}")
        time.sleep(dt)
    print("VERDICT: logic tile moved = PASS; destination snapped back = IGNORED (output, not input); "
          "slid without the logic tile committing = DESYNC.")


def main():
    _require_game()
    argv = sys.argv[1:]
    kick = "--kick" in argv
    argv = [a for a in argv if a != "--kick"]
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
    elif len(argv) >= 4 and argv[0] == "order":
        cmd_order(int(argv[1]), int(argv[2]), int(argv[3]), kick)
    else:
        print(__doc__)


if __name__ == "__main__":
    main()
