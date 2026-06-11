#!/usr/bin/env python
"""
Kill-credit oracle probe: why did a corpse read "not a captured enemy"?

Dumps BOTH sides of KillTracker's credit check, live:
  1. The static array slots the runtime captures from (array s=0..19, i.e.
     n=-19..0 by ct_probe indexing), applying CaptureEnemyIds' exact filter
     (lvl 1-99, br 1-100, fa 1-100, 1<=mhp<2000, NO inb gate) -> the oracle set.
  2. Every valid band slot (n=-24..+24 around the anchor; entry = +0x1C, static
     layout), printing slot index (KillTracker numbering: n+24), identity tuple,
     hp/maxHp, grid pos, and whether its tuple is IN the captured oracle.

A band unit (especially a corpse, hp=0) with ORACLE=MISS is the kill that
cannot credit. RPM only -- cannot crash the game. Run while the battle is live.
"""
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from ct_probe import (ARRAY_BASE, BAND_ANCHOR, PROC, STRIDE, A_HP, A_LVL,
                      A_MAXHP, A_OBRAVE, A_OFAITH, find_pid, k32, rd, u16)

A_INB = 0x12
A_GX, A_GY = 0x33, 0x34
BAND_ENTRY = 0x1C
BAND_LO, BAND_HI = -24, 24


def main():
    pid = find_pid("FFT_enhanced.exe")
    if not pid:
        sys.exit("FFT_enhanced.exe not running")
    h = k32.OpenProcess(PROC, False, pid)

    # --- side 1: the capture set (static array, runtime's slots 0..19 = n -19..0) ---
    oracle = set()
    print("=== static array (capture side, runtime slots 0..19) ===")
    for s in range(0, 20):
        addr = ARRAY_BASE + (s - 19) * STRIDE
        b = rd(h, addr, 0x40)
        if b is None:
            print(f"  s={s:>2} unreadable")
            continue
        lvl, br, fa = b[A_LVL], b[A_OBRAVE], b[A_OFAITH]
        mhp, hp, inb = u16(b, A_MAXHP), u16(b, A_HP), u16(b, A_INB)
        ok = 1 <= lvl <= 99 and 1 <= br <= 100 and 1 <= fa <= 100 and 1 <= mhp < 2000
        if ok:
            oracle.add((lvl, br, fa, mhp))
        flag = "CAPTURED" if ok else "skipped "
        print(f"  s={s:>2} lvl={lvl:>3} br={br:>3} fa={fa:>3} hp={hp:>4}/{mhp:<4} inb={inb} {flag}")

    # --- side 2: the band (credit side) ---
    print(f"\noracle = {len(oracle)} identities")
    print("\n=== band (n=-24..+24; KillTracker slot = n+24) ===")
    for n in range(BAND_LO, BAND_HI + 1):
        addr = BAND_ANCHOR + BAND_ENTRY + n * STRIDE
        b = rd(h, addr, 0x40)
        if b is None:
            continue
        lvl, br, fa = b[A_LVL], b[A_OBRAVE], b[A_OFAITH]
        mhp, hp = u16(b, A_MAXHP), u16(b, A_HP)
        gxb = rd(h, addr + A_GX, 2)
        gx, gy = (gxb[0], gxb[1]) if gxb else (255, 255)
        valid = 1 <= lvl <= 99 and 1 <= br <= 100 and 1 <= fa <= 100 \
            and 1 <= mhp < 2000 and gx < 64 and gy < 64
        if not valid:
            continue
        tup = (lvl, br, fa, mhp)
        mark = "ok  " if tup in oracle else "MISS"
        dead = "  DEAD" if hp == 0 else ""
        print(f"  slot={n + 24:>2} lvl={lvl:>3} br={br:>3} fa={fa:>3} "
              f"hp={hp:>4}/{mhp:<4} at ({gx:>2},{gy:>2}) oracle={mark}{dead}")


if __name__ == "__main__":
    main()
