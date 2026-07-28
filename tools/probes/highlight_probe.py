#!/usr/bin/env python
"""
Move-highlight quad-feed hunt (`gate` read / `dump` / `snap` / `cmp` READ-ONLY; `gate --hold`
and `pokef` write).

WHERE THIS PICKS UP (LIVE_LEDGER Contradicted row, "Treasure Master via the move-range
HIGHLIGHT"): the on/off GATE at 0x140C64C68 (u8, ~4 idle, 13 in move) is PROVEN -- holding it
nonzero keeps the highlight rendered out of Move mode (~3.4M holds live 2026-06-11). But the
gate is not the SOURCE: holding a custom tile list into 0x140C66315 changed nothing, because
the engine draws the blue quads from FLOAT WORLD-COORDS at 0x140c80000+. The ledger's stated
next step is to find the buffer that FEEDS that quad render, or learn the tile->world mapping
and hold the quads directly.

THE NEW INFORMATION SINCE THAT ROW WAS WRITTEN: the tile->world mapping is now PROVEN --
world X = 28*tile + 14, Y = 28*tile + 14, Z = -12*height (position-write-desync, owner-verified
swaps). So a float in the quad region equal to 28*t+14 for small integer t is a TILE COORDINATE
in disguise, and `dump` flags exactly those. If the quads decode cleanly, painting arbitrary
tiles = write our own quad records + hold the gate, and the Treasure Master output half plus
zone-control mechanics come free.

SEQUENCE
  1. python -u highlight_probe.py snap idle          # in battle, NOT in move mode
  2. select a unit, press Move so the blue range shows, do NOT pick a tile, then:
     python -u highlight_probe.py snap move
  3. python -u highlight_probe.py cmp idle move      # what appeared: count fields + quad records
  4. python -u highlight_probe.py dump               # still in move mode: decode the floats
  5. python -u highlight_probe.py pokef <addr> <float> --hold 20
     While the highlight is up, nudge ONE float by a tile (28.0) and LOOK: if one blue quad
     slides, that address is render-feed, live, and ours.
  6. python -u highlight_probe.py gate --hold 30     # re-prove the gate if needed

Snapshots cover the count/list band 0x140C64C00..0x140C67800 AND the quad arena
0x140C80000..0x140C88000. Files land under %TEMP%\\fft_probes\\highlight.
"""
import argparse
import os
import pathlib
import struct
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from ct_probe import PROC, PV_W, find_pid, k32, rd, wr  # noqa: E402

# RE-ANCHORED 2026-07-27: the gate/count/list band is PRE-1.5 and read identical across an
# idle-vs-move cmp (dead addresses), while the quad arena at its OLD address produced live
# move-mode records with perfect 28*t+14 / -12*h+0.25 floats on an 0x88 stride. So the gate
# cluster moved (+0x676C, the PauseFlag delta from PORT_1.5_OFFSETS.md) and the arena did not.
DELTA_15 = 0x676C
GATE = 0x140C64C68 + DELTA_15      # 0x140C6B3D4; old move-tile list 0x140C66315 -> 0x140C6CA81
BANDS = [(0x140C6B000, 0x2C00), (0x140C80000, 0x8000)]
SNAP_DIR = pathlib.Path(os.environ.get("TEMP", ".")) / "fft_probes" / "highlight"


def open_game():
    pid = find_pid(PROC)
    if not pid:
        sys.exit(f"{PROC} not running")
    h = k32.OpenProcess(PV_W, False, pid)
    if not h:
        sys.exit(f"could not open pid {pid}")
    return h


def tile_of(f):
    """If f == 28*t + 14 (+-0.6) for tile t in 0..31, return t, else None."""
    t = round((f - 14.0) / 28.0)
    if 0 <= t <= 31 and abs(28.0 * t + 14.0 - f) <= 0.6:
        return t
    return None


def cmd_gate(a):
    h = open_game()
    b = rd(h, GATE, 1)
    print(f"gate 0x{GATE:X} = {b[0] if b else '?'}   (~4 idle, 13 in move mode)")
    if not a.hold:
        return
    old = b
    print(f"holding 13 for {a.hold}s -- the highlight should stay rendered out of move mode")
    try:
        end = time.time() + a.hold
        while time.time() < end:
            cur = rd(h, GATE, 1)
            if cur and cur[0] != 13:
                wr(h, GATE, bytes([13]))
            time.sleep(0.05)
    except KeyboardInterrupt:
        pass
    finally:
        if old:
            wr(h, GATE, old)
        print(f"\nrestored gate to {old[0] if old else '?'}")


def cmd_dump(a):
    h = open_game()
    base, span = BANDS[1]
    buf = rd(h, base, span)
    if not buf:
        sys.exit("quad region unreadable")
    shown = 0
    print("floats in the quad arena that are FINITE and plausible; [t=N] marks a 28*t+14 match:")
    for off in range(0, span - 3, 4):
        (f,) = struct.unpack_from("<f", buf, off)
        if f != f or abs(f) > 4000 or f == 0.0:
            continue
        t = tile_of(f)
        tag = f"  [t={t}]" if t is not None else ""
        # Z candidates: -12*h for h 0..30
        if t is None and -370 <= f < 0 and abs(f / -12.0 - round(f / -12.0)) < 0.05:
            tag = f"  [h={round(f / -12.0)}?]"
        if tag or a.all:
            print(f"  0x{base + off:X}  {f:>10.2f}{tag}")
            shown += 1
            if shown >= a.limit:
                print(f"  ... capped at {a.limit}; re-run with --limit for more")
                break
    if not shown:
        print("  nothing plausible -- are you actually in move mode with the range showing?")


def cmd_snap(a):
    h = open_game()
    SNAP_DIR.mkdir(parents=True, exist_ok=True)
    for i, (base, span) in enumerate(BANDS):
        buf = rd(h, base, span)
        if not buf:
            sys.exit(f"band {i} (0x{base:X}) unreadable")
        (SNAP_DIR / f"{a.label}_{i}.bin").write_bytes(buf)
    print(f"snapped {len(BANDS)} bands -> {SNAP_DIR}\\{a.label}_*.bin")


def cmd_cmp(a):
    for i, (base, span) in enumerate(BANDS):
        pa, pb = SNAP_DIR / f"{a.a}_{i}.bin", SNAP_DIR / f"{a.b}_{i}.bin"
        if not pa.exists() or not pb.exists():
            sys.exit(f"missing snapshot for band {i}; run `snap {a.a}` and `snap {a.b}` first")
        A, B = pa.read_bytes(), pb.read_bytes()
        print(f"\n=== band 0x{base:X} ===")
        run_start, shown = None, 0
        for off in range(min(len(A), len(B))):
            if A[off] != B[off]:
                if run_start is None:
                    run_start = off
            elif run_start is not None:
                n = off - run_start
                print(f"  0x{base + run_start:X}  {n:>3}B   {A[run_start:off].hex(' ')[:48]} -> {B[run_start:off].hex(' ')[:48]}")
                run_start = None
                shown += 1
                if shown >= a.limit:
                    print(f"  ... capped at {a.limit} runs")
                    break
        if shown == 0 and run_start is None:
            print("  identical")


def cmd_pokef(a):
    h = open_game()
    old = rd(h, a.addr, 4)
    if old is None:
        sys.exit(f"0x{a.addr:X} unreadable")
    (was,) = struct.unpack("<f", old)
    new = struct.pack("<f", a.value)
    print(f"0x{a.addr:X}: {was:.2f} -> {a.value:.2f}, holding {a.hold}s. WATCH THE BLUE QUADS.")
    try:
        end = time.time() + a.hold
        while time.time() < end:
            cur = rd(h, a.addr, 4)
            if cur != new:
                wr(h, a.addr, new)
            time.sleep(0.05)
    except KeyboardInterrupt:
        pass
    finally:
        wr(h, a.addr, old)
        print(f"\nrestored {was:.2f}")


def main():
    p = argparse.ArgumentParser(description=__doc__,
                                formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = p.add_subparsers(dest="cmd", required=True)

    g = sub.add_parser("gate")
    g.add_argument("--hold", type=int, default=0)
    g.set_defaults(fn=cmd_gate)

    d = sub.add_parser("dump")
    d.add_argument("--all", action="store_true", help="print every plausible float, not just tile/height matches")
    d.add_argument("--limit", type=int, default=120)
    d.set_defaults(fn=cmd_dump)

    s = sub.add_parser("snap")
    s.add_argument("label")
    s.set_defaults(fn=cmd_snap)

    c = sub.add_parser("cmp")
    c.add_argument("a")
    c.add_argument("b")
    c.add_argument("--limit", type=int, default=80)
    c.set_defaults(fn=cmd_cmp)

    k = sub.add_parser("pokef")
    k.add_argument("addr", type=lambda x: int(x, 0))
    k.add_argument("value", type=float)
    k.add_argument("--hold", type=int, default=20)
    k.set_defaults(fn=cmd_pokef)

    a = p.parse_args()
    a.fn(a)


if __name__ == "__main__":
    main()
