#!/usr/bin/env python
"""
Direct HP/MP write probe: prove the band addressing + provisional MP pair on screen.
  hp_poke.py                    -> list all valid band units (slot, id-fields, hp, mp pair)
  hp_poke.py <slot> <dhp> <dmp> -> add dhp to hp (clamped to max, never from 0) and dmp to the
                                   provisional mp u16 (+0x18, clamped to +0x1A max) on that
                                   band slot; prints before/after with read-backs.
RPM/WPM only -- guarded, cannot crash the game. Run while the battle is live.
"""
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from ct_probe import (BAND_ANCHOR, PROC, STRIDE, A_HP, A_LVL, A_MAXHP,
                      A_OBRAVE, A_OFAITH, find_pid, k32, rd, u16, wr)

A_MP, A_MAXMP = 0x18, 0x1A
A_GX, A_GY = 0x33, 0x34
ENTRY = 0x1C


def valid(b):
    lvl, br, fa = b[A_LVL], b[A_OBRAVE], b[A_OFAITH]
    mhp = u16(b, A_MAXHP)
    return 1 <= lvl <= 99 and 1 <= br <= 100 and 1 <= fa <= 100 and 1 <= mhp < 2000


def main():
    pid = find_pid("FFT_enhanced.exe")
    if not pid:
        sys.exit("FFT_enhanced.exe not running")
    h = k32.OpenProcess(PROC, False, pid)

    if len(sys.argv) < 4:
        print("slot  lvl/br/fa  hp/maxhp   mp/maxmp(+0x18/+0x1A)  pos")
        found = 0
        for n in range(-24, 25):
            addr = BAND_ANCHOR + ENTRY + n * STRIDE
            b = rd(h, addr, 0x40)
            if b is None or not valid(b):
                continue
            found += 1
            print(f"  {n + 24:>2}  {b[A_LVL]}/{b[A_OBRAVE]}/{b[A_OFAITH]}"
                  f"  {u16(b, A_HP):>4}/{u16(b, A_MAXHP):<4}"
                  f"  {u16(b, A_MP):>4}/{u16(b, A_MAXMP):<4}"
                  f"  ({b[A_GX]},{b[A_GY]})")
        print(f"{found} valid units. Rerun: hp_poke.py <slot> <dhp> <dmp>")
        return

    slot, dhp, dmp = int(sys.argv[1]), int(sys.argv[2]), int(sys.argv[3])
    addr = BAND_ANCHOR + ENTRY + (slot - 24) * STRIDE
    b = rd(h, addr, 0x40)
    if b is None or not valid(b):
        sys.exit(f"slot {slot} is not a valid unit right now")
    hp, mhp = u16(b, A_HP), u16(b, A_MAXHP)
    mp, mmp = u16(b, A_MP), u16(b, A_MAXMP)
    print(f"before: hp {hp}/{mhp}  mp {mp}/{mmp}")
    if hp > 0 and dhp:
        nhp = min(mhp, hp + dhp)
        wr(h, addr + A_HP, nhp.to_bytes(2, "little"))
    if dmp:
        nmp = min(mmp, mp + dmp) if mmp else mp + dmp
        wr(h, addr + A_MP, nmp.to_bytes(2, "little"))
    b2 = rd(h, addr, 0x40)
    print(f"after:  hp {u16(b2, A_HP)}/{u16(b2, A_MAXHP)}  mp {u16(b2, A_MP)}/{u16(b2, A_MAXMP)}"
          if b2 else "after: unreadable?!")


if __name__ == "__main__":
    main()
