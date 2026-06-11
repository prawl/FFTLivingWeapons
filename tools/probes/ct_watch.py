#!/usr/bin/env python
"""Watch every valid band unit's scheduler-CT byte (+0x25) live and print transitions.
Decides whether band CT actually TICKS (readable live) or only accepts writes
(the ExtraTurn slam proved write-effective, not read-live). RPM only.
Usage: ct_watch.py [seconds=75]"""
import os
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from ct_probe import BAND_ANCHOR, PROC, STRIDE, find_pid, k32, rd, u16


def main():
    secs = int(sys.argv[1]) if len(sys.argv) > 1 else 75
    off = int(sys.argv[2], 16) if len(sys.argv) > 2 else 0x25
    pid = find_pid("FFT_enhanced.exe")
    if not pid:
        sys.exit("FFT_enhanced.exe not running")
    h = k32.OpenProcess(PROC, False, pid)
    last = {}
    t0 = time.time()
    print(f"watching band entry +0x{off:02X} for {secs}s -- play a turn or two...")
    while time.time() - t0 < secs:
        for n in range(-24, 25):
            addr = BAND_ANCHOR + 0x1C + n * STRIDE
            b = rd(h, addr, 0x30)
            if b is None:
                continue
            lvl, br, fa = b[0x0D], b[0x0E], b[0x10]
            mhp, hp = u16(b, 0x16), u16(b, 0x14)
            if not (1 <= lvl <= 99 and 1 <= br <= 100 and 1 <= fa <= 100 and 1 <= mhp < 2000):
                continue
            ct = b[off]
            if n not in last:
                last[n] = ct
                print(f"  t={time.time()-t0:5.1f}s slot {n+24:>2} ({lvl}/{br}/{fa} hp {hp}/{mhp}) CT={ct}")
            elif last[n] != ct:
                print(f"  t={time.time()-t0:5.1f}s slot {n+24:>2} ({lvl}/{br}/{fa} hp {hp}/{mhp}) CT {last[n]} -> {ct}")
                last[n] = ct
        time.sleep(0.2)
    print("done")


if __name__ == "__main__":
    main()
