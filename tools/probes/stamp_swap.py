#!/usr/bin/env python
"""
Stamp-swap probe (WRITE, reversible): when an AI action stamps a PLAYER frame as its pending
victim, transplant the stamp onto a chosen magnet unit and clear it on the original victim.

Discovered via frame_diff.py (skeleton cast, 2026-07-02): at AI decision time -- seconds before
impact -- the VICTIM's combat frame (array 0x141853CE0, stride 0x200) receives:
    +0x1BB = 2            incoming-action marker
    +0x1CE/+0x1CF u16     incoming ability id (0x1B9 = 441 for the observed skeleton magic)
    +0x1E6                forecast damage (0x78 = the observed 120)
    (+0x1C4/+0x1D8/+0x1E5 follow in a second batch)
This probe answers whether those stamps are AUTHORITATIVE (execution follows them -> the hit
lands on the magnet = a provoke/redirect lever) or FORECAST-ONLY (UI preview; the real target
lives elsewhere and the original victim still bleeds).

Transplant = copy {1BB,1C4,1CE,1CF,1D8,1E5,1E6} victim -> magnet, then zero 1BB on the victim.
Stamps are battle-transient (engine clears them at action end), so stopping the probe leaves
no persistent state; battle exit reverts everything.

USAGE (game running, IN a live battle):
    python -u stamp_swap.py <magnetSlot> <watchSlot> [watchSlot ...]
    # e.g. stamp_swap.py 17 16 18   -- transplant any stamp on slots 16/18 onto slot 17
"""
import os
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from ct_probe import PROC, PV_W, find_pid, k32, rd, wr

BASE = 0x141853CE0
STRIDE = 0x200
MARK = 0x1BB
FIELDS = [0x1BB, 0x1C4, 0x1CE, 0x1CF, 0x1D8, 0x1E5, 0x1E6]


def u8(h, a):
    b = rd(h, a, 1)
    return b[0] if b else 0


def main():
    if len(sys.argv) < 3:
        print(__doc__)
        return
    magnet = int(sys.argv[1], 0)
    watch = [int(x, 0) for x in sys.argv[2:]]
    pid = find_pid(PROC)
    if not pid:
        sys.exit(f"{PROC} not running")
    h = k32.OpenProcess(PV_W, False, pid)
    ms = BASE + magnet * STRIDE
    print(f"watching slots {watch} for incoming-action stamps; transplanting onto slot {magnet}. Ctrl+C stops.")
    swaps = 0
    try:
        while True:
            for v in watch:
                if v == magnet:
                    continue
                vs = BASE + v * STRIDE
                if u8(h, vs + MARK) != 2:
                    continue
                vals = [u8(h, vs + f) for f in FIELDS]
                for f, val in zip(FIELDS, vals):
                    wr(h, ms + f, bytes([val]))
                wr(h, vs + MARK, b"\x00")
                swaps += 1
                t = time.strftime("%H:%M:%S")
                abid = vals[2] | (vals[3] << 8)
                print(f"  {t} swap #{swaps}: slot {v} was stamped (ability 0x{abid:04X}={abid}, "
                      f"dmg {vals[6]}) -> transplanted to slot {magnet}, victim mark cleared")
            time.sleep(0.03)
    except KeyboardInterrupt:
        print(f"stopped. {swaps} transplant(s).")
    finally:
        k32.CloseHandle(h)


if __name__ == "__main__":
    main()
