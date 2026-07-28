#!/usr/bin/env python
"""
Terrain-grid decode + mutation probe (`dump`/`cursor`/`snap`/`diff` READ-ONLY; `poke` writes).

WHAT IS KNOWN (all ledger-cited, LIVE_LEDGER.md):
  - Terrain grid at 0x140C65000, 7 bytes per tile (PROVEN row, via FFTHandsFree CommandWatcher
    + mark_probe diffs). Described there as a "9x8 window, STATIC map data" -- but the WINDOW
    claim is unverified (may be camera-relative) and "static" is DISPROVEN: LIVE INCIDENT #4
    showed fields {2,3,4,5} mutate mid-battle when units act, and INCIDENT #5 showed RAIN
    perturbs them. So the grid is live state, not baked geometry, and live state can be a lever.
  - Cursor tile is PROVEN readable: X u8 0x140C64A54, Y u8 0x140C6496C, linear idx u8 0x140C64E7C.

WHAT THIS PROBE SETTLES
  1. INDEXING (`cursor`): is record N at grid + idx*7 where idx is the cursor's linear index?
     Park the cursor, read the record, move one tile, watch which record tracks. This also
     settles the 9x8-window question: if the same tile's record CHANGES when the camera pans,
     it is a window; if it holds, the grid is whole-map and idx-addressed.
  2. MEANING (`cursor` + eyes): hover tiles of different HEIGHT / surface (water, grass, roof)
     and watch which of the 7 fields tracks which on-screen property.
  3. MUTABILITY (`poke`): write one field on one hovered tile and watch the game: does the
     hover card's height change, does move range flow differently, does a water tile dry out?
     A honored write = terrain mutation as a mechanic (walls, pits, lava).

VERBS
  python -u terrain_probe.py dump [count=96] [--base idx]     # raw records, 7 bytes each
  python -u terrain_probe.py cursor [--secs 60]               # live: cursor moves -> print its record
  python -u terrain_probe.py snap <label>                     # snapshot 256 tiles to %TEMP%
  python -u terrain_probe.py diff <labelA> <labelB>           # which tiles/fields changed
  python -u terrain_probe.py poke <idx> <field> <val> [--secs 30]   # hold one field byte, restore

The poke holds (50ms re-assert) because incident #4 proved the engine rewrites these fields on
its own schedule; a one-shot write racing the engine proves nothing either way. Restore on exit
including Ctrl-C. Never poke while a unit is MOVING through the tile (the mover owns transforms;
unknown whether it owns terrain reads too -- cheap to avoid).
"""
import argparse
import os
import pathlib
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from ct_probe import PROC, PV_W, find_pid, k32, rd, wr  # noqa: E402

# RE-ANCHOR HISTORY (2026-07-27, two failed guesses then the answer):
#  - The June-11 trio 0x140C64A54/0x140C6496C/0x140C64E7C is PRE-1.5; FFTHandsFree marks the
#    pair "TODO: re-find for 1.5" (NavigationActions.Movement.cs:401).
#  - A uniform +0x676C shift (PauseFlag's port delta) read frozen garbage: the cluster was
#    REARRANGED, not shifted -- X moved +0x6564 and Y +0x6440, per-address.
#  - LIVE pair: FFTHandsFree's AddrTargetCursorX/Y (NavigationActions.Battle.cs:865-866), the
#    battle targeting/free-roam reticle, used by HandsFree on 1.5 daily, beside the 1.5-verified
#    PauseFlag 0x140C6B1C8 / BattleSubState 0x140C6B1CC.
#  - No 1.5 linear-idx equivalent is known; tile index is computed as x + y*width instead
#    (--width, default 9 from the June "9x8 window" note; if records read wrong, try 16/17).
# GRID RE-FOUND 2026-07-27 by two-map stable-differential (weather_probe cmp mapA mapB): a dense
# band of stable-in-both-maps, different-between-maps runs on a PERFECT 7-byte lattice starting
# 0x140C6B3C1 (run starts 3C1/3C8/3CF/3D6/... observed for 1300+ bytes = whole-map sized, so the
# June "9x8 window" note undersold it). Base is PROVISIONAL to +-6 bytes (the lattice pins the
# stride, not field-0); slide with --gbase if hover fields look split across records.
# The old June base 0x140C65000 read all zeros across 90 tiles on 1.5.2 = dead.
# THE REAL GRID, found 2026-07-27 by tailing the pathfinder's occupancy read (CE what-accesses
# on a bystander's combat +0x4F at Move-open; reader 0x14027B25A computes
# idx = x + y*width + (unit+0x51 >> 7)*0x100 and reads [0x140000010 + idx*8 + 0xD8DCB2/B3]).
# 8 bytes per tile, 256-tile layer planes (the *0x100 term = FFT's upper/lower bridge layer).
# LIVE-VERIFIED same evening: Bomb (3,10) and player (4,10) both decoded height 2 at f2 low5;
# f2 top3 = slope family; f0 differed by bit 0x40 between the two (flag semantics OPEN).
# The stride-7 band at 0x140C6B3C1 was pathfinding SCRATCH, not this; June's 0x140C65000 is dead.
GRID = 0x140D8DCC0
REC = 8
CUR_X = 0x140C6AFB8
CUR_Y = 0x140C6ADAC
# Map-dimensions candidate: u8 pair right beside cursor Y, stable-different across maps in the
# same cmp (0b 0c -> 0a 12 = 11x12 vs 10x18?). If it verifies, the cursor verb's --width guess
# becomes a read.
MAP_WH = 0x140C6AD6A
SNAP_TILES = 256                      # covers any FFT map (max ~17x17)
SNAP_DIR = pathlib.Path(os.environ.get("TEMP", ".")) / "fft_probes" / "terrain"


def open_game():
    pid = find_pid(PROC)
    if not pid:
        sys.exit(f"{PROC} not running")
    h = k32.OpenProcess(PV_W, False, pid)
    if not h:
        sys.exit(f"could not open pid {pid}")
    return h


def u8(h, a):
    b = rd(h, a, 1)
    return b[0] if b else None


def record(h, idx):
    return rd(h, GRID + idx * REC, REC)


def fmt(recb):
    return recb.hex(" ") + "   dec " + " ".join(f"{b:>3}" for b in recb)


def cmd_dump(a):
    h = open_game()
    for i in range(a.base, a.base + a.count):
        b = record(h, i)
        if b is None:
            print(f"  [{i:>3}] unreadable")
            continue
        print(f"  [{i:>3}] {fmt(b)}")


def cmd_cursor(a):
    h = open_game()
    base = a.gbase if a.gbase else GRID
    wh = rd(h, MAP_WH, 2)
    width = a.width
    if wh:
        print(f"map-dimensions candidate @0x{MAP_WH:X}: {wh[0]}x{wh[1]} "
              f"(eyeball vs the actual map; if right, use --width {wh[0]})")
        if a.width == 0:
            width = wh[0]
    if width == 0:
        width = 9
    print(f"grid base 0x{base:X}, width {width}. Hover ALONG A ROW first, then DOWN A COLUMN.")
    print("If fields look split across records, slide with --gbase (base-6 .. base+6).\n")
    last = None
    end = time.time() + a.secs
    while time.time() < end:
        x, y = u8(h, CUR_X), u8(h, CUR_Y)
        if (x, y) != last and x is not None and y is not None:
            last = (x, y)
            if x > 40 or y > 40:
                print(f"  cursor reads ({x},{y}) -- not tile-plausible; the pair may need a re-find")
            else:
                i = x + y * width
                b = rd(h, base + i * REC, REC)
                print(f"  cursor ({x:>2},{y:>2}) idx {i:>3}  ->  {fmt(b) if b else 'unreadable'}")
        time.sleep(0.2)


def cmd_snap(a):
    h = open_game()
    buf = rd(h, GRID, SNAP_TILES * REC)
    if not buf:
        sys.exit("grid unreadable")
    SNAP_DIR.mkdir(parents=True, exist_ok=True)
    (SNAP_DIR / f"{a.label}.bin").write_bytes(buf)
    print(f"snapped {SNAP_TILES} tiles -> {SNAP_DIR / (a.label + '.bin')}")


def cmd_diff(a):
    pa, pb = SNAP_DIR / f"{a.a}.bin", SNAP_DIR / f"{a.b}.bin"
    if not pa.exists() or not pb.exists():
        sys.exit("missing snapshot; run `snap <label>` twice first")
    A, B = pa.read_bytes(), pb.read_bytes()
    changed = 0
    for i in range(min(len(A), len(B)) // REC):
        ra, rb = A[i * REC:(i + 1) * REC], B[i * REC:(i + 1) * REC]
        if ra != rb:
            changed += 1
            fields = " ".join(f"f{k}:{ra[k]}->{rb[k]}" for k in range(REC) if ra[k] != rb[k])
            print(f"  tile [{i:>3}]  {fields}")
    print(f"{changed} tile(s) changed of {min(len(A), len(B)) // REC}")


def cmd_poke(a):
    if not (0 <= a.field < REC):
        sys.exit(f"field must be 0..{REC - 1}")
    h = open_game()
    addr = GRID + a.idx * REC + a.field
    old = rd(h, addr, 1)
    if old is None:
        sys.exit(f"tile [{a.idx}] unreadable")
    print(f"tile [{a.idx}] field {a.field}: {old[0]} -> {a.val}, holding {a.secs}s (Ctrl-C restores)")
    print("watch: hover card height, move-range flow, surface behavior.")
    try:
        end = time.time() + a.secs
        while time.time() < end:
            cur = rd(h, addr, 1)
            if cur and cur[0] != a.val:
                wr(h, addr, bytes([a.val]))
            time.sleep(0.05)
    except KeyboardInterrupt:
        pass
    finally:
        wr(h, addr, old)
        print(f"\nrestored field to {old[0]}")


def main():
    p = argparse.ArgumentParser(description=__doc__,
                                formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = p.add_subparsers(dest="cmd", required=True)

    d = sub.add_parser("dump")
    d.add_argument("count", nargs="?", type=int, default=96)
    d.add_argument("--base", type=int, default=0)
    d.set_defaults(fn=cmd_dump)

    c = sub.add_parser("cursor")
    c.add_argument("--secs", type=int, default=60)
    c.add_argument("--width", type=int, default=0, help="0 = use the map-dimensions read, fallback 9")
    c.add_argument("--gbase", type=lambda x: int(x, 0), default=0, help="override the grid base to slide record alignment")
    c.set_defaults(fn=cmd_cursor)

    s = sub.add_parser("snap")
    s.add_argument("label")
    s.set_defaults(fn=cmd_snap)

    f = sub.add_parser("diff")
    f.add_argument("a")
    f.add_argument("b")
    f.set_defaults(fn=cmd_diff)

    k = sub.add_parser("poke")
    k.add_argument("idx", type=lambda x: int(x, 0))
    k.add_argument("field", type=lambda x: int(x, 0))
    k.add_argument("val", type=lambda x: int(x, 0))
    k.add_argument("--secs", type=int, default=30)
    k.set_defaults(fn=cmd_poke)

    a = p.parse_args()
    a.fn(a)


if __name__ == "__main__":
    main()
