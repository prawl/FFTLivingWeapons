#!/usr/bin/env python
"""
MP-offset hunt on the auth band: HP=+0x14, maxHP=+0x16 -- MP should be a nearby
u16 pair (cur <= max, max in 1..999, mages high / knights low). Dumps every
valid band unit's u16s at +0x18..+0x2E so the MP/maxMP pair stands out.
RPM only. Run while a battle is live.
"""
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from ct_probe import (BAND_ANCHOR, PROC, STRIDE, A_HP, A_LVL, A_MAXHP,
                      A_OBRAVE, A_OFAITH, find_pid, k32, rd, u16)

BAND_ENTRY = 0x1C


def main():
    pid = find_pid("FFT_enhanced.exe")
    if not pid:
        sys.exit("FFT_enhanced.exe not running")
    h = k32.OpenProcess(PROC, False, pid)
    cols = list(range(0x18, 0x30, 2))
    print("slot lvl  hp/maxhp   " + " ".join(f"+{c:02X}" for c in cols))
    for n in range(-24, 25):
        addr = BAND_ANCHOR + BAND_ENTRY + n * STRIDE
        b = rd(h, addr, 0x60)
        if b is None:
            continue
        lvl, br, fa = b[A_LVL], b[A_OBRAVE], b[A_OFAITH]
        mhp, hp = u16(b, A_MAXHP), u16(b, A_HP)
        if not (1 <= lvl <= 99 and 1 <= br <= 100 and 1 <= fa <= 100 and 1 <= mhp < 2000):
            continue
        vals = " ".join(f"{u16(b, c):>4}" for c in cols)
        print(f"  {n + 24:>2}  {lvl:>2} {hp:>4}/{mhp:<4}  {vals}")


if __name__ == "__main__":
    main()
