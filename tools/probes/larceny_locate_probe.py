"""Diagnose Larceny's wielder-locate miss (wielderLocated=False while the kill-tracker sees mainHand
id=30). Dumps (1) roster slots holding Arcanum (30) in the MAIN hand and their (lvl,br,fa), and
(2) every live band entry's locate-relevant fields -- weapon (+0x04), level (+0x0D), brave (+0x0E),
faith (+0x10), grid pos (+0x33/0x34). Compare: Wielder.TryResolveMainHand needs exactly ONE roster
Arcanum slot; Wielder.Locate needs a band entry whose weapon == 30 AND brave/faith == the roster's.
"""
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from ct_probe import PROC, PV_W, find_pid, k32, rd

ARCANUM = 30
ROSTER, RSTRIDE, RSLOTS = 0x1411A18D0, 0x258, 20
ANCHOR, STRIDE, ENTRY = 0x14184F890, 0x200, 0x1C
BBASE, BSLOTS = ANCHOR + ENTRY - 24 * STRIDE, 49

pid = find_pid(PROC)
if not pid:
    sys.exit(f"{PROC} not running")
h = k32.OpenProcess(PV_W, False, pid)
try:
    print("=== ROSTER slots holding Arcanum (30) in the MAIN hand (RRHand +0x14) ===")
    rcount = 0
    for r in range(RSLOTS):
        b = rd(h, ROSTER + r * RSTRIDE, 0x240)
        if not b:
            continue
        lvl = b[0x1D]
        if not (1 <= lvl <= 99):
            continue
        rh, lh, oh = b[0x14] | (b[0x15] << 8), b[0x16] | (b[0x17] << 8), b[0x18] | (b[0x19] << 8)
        if rh == ARCANUM:
            rcount += 1
            print(f"  slot {r}: lvl={lvl} br={b[0x1E]} fa={b[0x1F]} rh={rh} lh={lh} oh={oh}")
    print(f"  -> {rcount} main-hand Arcanum slot(s) (TryResolveMainHand needs exactly 1)")

    print("=== BAND entries (weapon +0x04, lvl +0x0D, br +0x0E, fa +0x10, pos +0x33/34) ===")
    for s in range(BSLOTS):
        b = rd(h, BBASE + s * STRIDE, 0x60)
        if not b:
            continue
        mhp, lvl, br, fa = b[0x16] | (b[0x17] << 8), b[0x0D], b[0x0E], b[0x10]
        if not (1 <= mhp < 2000 and 1 <= lvl <= 99 and 1 <= br <= 100 and 1 <= fa <= 100):
            continue
        wid, gx, gy = b[0x04] | (b[0x05] << 8), b[0x33], b[0x34]
        flag = "  <== WEAPON 30 (Arcanum)" if wid == ARCANUM else ""
        print(f"  slot {s:2}: weapon={wid:5} lvl={lvl:2} br={br:3} fa={fa:3} pos=({gx:2},{gy:2}) mhp={mhp}{flag}")
finally:
    k32.CloseHandle(h)
