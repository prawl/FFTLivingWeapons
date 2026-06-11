#!/usr/bin/env python
"""
Knockback probe -- can we WRITE a unit's grid position and have the engine
accept it? (The 'guaranteed knockback' question. Data path is walled: proc
rate is the Denuvo-locked engine byte, so may-cast Rush ~25% is the data
ceiling; a position write is the only guaranteed path.)

Band-copy grid position: gx=+0x33, gy=+0x34 (u8 each; the twin-filter reads
these, so READS are proven -- writes are NOT, that's this probe).

RUN THIS IN A THROWAWAY BATTLE. Outcomes to watch after a write:
  - sprite visibly relocates + unit acts from the new tile  -> FEASIBLE
  - bytes revert instantly                                  -> band is a mirror;
        try 'which=static' / hunt the real position struct
  - sprite stays, engine uses new tile (desync)             -> infeasible as-is
  - anything weird (stuck cursor, frozen AI)                -> infeasible; restart

USAGE (game running, battle live):
  python ct_probe.py dump                                  # target mhp/lvl
  python knockback_probe.py pos  <mhp> <lvl>               # read gx/gy both copies
  python knockback_probe.py push <mhp> <lvl> <dx> <dy> [band|static|both]
        # one-shot gx+=dx, gy+=dy, then streams the bytes for 12s so you can
        # watch accept/revert. Prints the restore command before writing.
  python knockback_probe.py setpos <mhp> <lvl> <x> <y> [band|static|both]
RPM/WPM only -- cannot crash the game (the ENGINE might dislike the value;
that result is the answer, not a bug).
"""
import os
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from ct_probe import (PROC, PV_W, find_pid, k32, rd, u16, wr)
from poison_probe import locate_blocking, band_ok

GX, GY = 0x33, 0x34


def read_pos(h, base):
    b = rd(h, base + GX, 2)
    return (b[0], b[1]) if b else (None, None)


def cmd_pos(h, mhp, lvl):
    u = locate_blocking(h, mhp, lvl)
    for name, base in (("static", u["static"]), ("band", u["band"])):
        x, y = read_pos(h, base)
        print(f"{name:8} @{base:012X}  gx={x} gy={y}")


def targets(u, which):
    return {"band": [("band", u["band"])],
            "static": [("static", u["static"])],
            "both": [("band", u["band"]), ("static", u["static"])]}[which]


def write_and_watch(h, u, mhp, lvl, nx, ny, which):
    for name, base in targets(u, which):
        ox, oy = read_pos(h, base)
        print(f"{name}: ({ox},{oy}) -> ({nx},{ny})   "
              f"[restore: setpos {mhp} {lvl} {ox} {oy} {name}]")
        wr(h, base + GX, bytes([nx & 0xFF]))
        wr(h, base + GY, bytes([ny & 0xFF]))
    print("\nwatching both copies for 12s (does the engine accept, revert, or re-derive?)")
    t0 = time.time()
    last = {}
    while time.time() - t0 < 12:
        if not band_ok(h, u["band"], mhp, lvl):
            print("  band struct moved (reloc/death?) -- stopping watch")
            break
        for name, base in (("band", u["band"]), ("static", u["static"])):
            x, y = read_pos(h, base)
            if last.get(name) != (x, y):
                print(f"  t={time.time() - t0:5.1f}s  {name:8} gx={x} gy={y}")
                last[name] = (x, y)
        time.sleep(0.1)
    print("watch over. VERDICT IS VISUAL: did the sprite move / does the unit "
          "act from the new tile / can you target it there?")


def main():
    a = sys.argv
    mode = a[1] if len(a) > 1 else ""
    if mode not in ("pos", "push", "setpos"):
        print(__doc__)
        return
    pid = find_pid(PROC)
    if not pid:
        print(f"{PROC} not running")
        return
    h = k32.OpenProcess(PV_W, False, pid)
    if not h:
        print("OpenProcess failed")
        return
    try:
        mhp, lvl = int(a[2]), int(a[3])
        if mode == "pos":
            cmd_pos(h, mhp, lvl)
            return
        u = locate_blocking(h, mhp, lvl)
        which = (a[6] if len(a) > 6 else "band").lower()
        if mode == "push":
            bx, by = read_pos(h, u["band"])
            if bx is None:
                print("can't read band position")
                return
            write_and_watch(h, u, mhp, lvl, bx + int(a[4]), by + int(a[5]), which)
        else:
            write_and_watch(h, u, mhp, lvl, int(a[4]), int(a[5]), which)
    finally:
        k32.CloseHandle(h)


if __name__ == "__main__":
    main()
