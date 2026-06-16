"""Grant the marquee buffs (Protect/Shell/Haste/Reflect) to ENEMY band units so Larceny's steal of
each can be FUNCTIONALLY tested -- does the stolen buff actually reduce damage / bounce magic / add
turns, or just paint the icon like Float did?

Enemies = band units whose (brave,faith) match NO player roster slot. Each enemy gets one buff,
cycled through a shuffled [Protect, Shell, Haste, Reflect] so all four appear; the status bit is set
on the live band entry (+0x48 / +0x49) where Larceny reads it. Run while in a live battle, then
attack a buffed enemy with the Arcanum wielder and watch the log + the wielder's actual effect.
"""
import os
import random
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from ct_probe import PROC, PV_W, find_pid, k32, rd, wr

ROSTER, RSTRIDE, RSLOTS = 0x1411A18D0, 0x258, 20
ANCHOR, STRIDE, ENTRY = 0x14184F890, 0x200, 0x1C
BBASE, BSLOTS = ANCHOR + ENTRY - 24 * STRIDE, 49

# (name, band-relative status byte, mask) -- from the confirmed FFT status map.
BUFFS = [("Protect", 0x48, 0x20), ("Shell", 0x48, 0x10), ("Haste", 0x48, 0x08), ("Reflect", 0x49, 0x02)]

pid = find_pid(PROC)
if not pid:
    sys.exit(f"{PROC} not running")
h = k32.OpenProcess(PV_W, False, pid)
try:
    # (brave,faith) of every live player roster slot -- used to exclude players from the buffing.
    players = set()
    for r in range(RSLOTS):
        b = rd(h, ROSTER + r * RSTRIDE, 0x20)
        if b and 1 <= b[0x1D] <= 99:
            players.add((b[0x1E], b[0x1F]))

    order = BUFFS[:]
    random.Random().shuffle(order)
    i = 0
    granted = 0
    for s in range(BSLOTS):
        addr = BBASE + s * STRIDE
        b = rd(h, addr, 0x60)
        if not b:
            continue
        mhp, lvl, br, fa = b[0x16] | (b[0x17] << 8), b[0x0D], b[0x0E], b[0x10]
        if not (1 <= mhp < 2000 and 1 <= lvl <= 99 and 1 <= br <= 100 and 1 <= fa <= 100):
            continue
        if (br, fa) in players:
            continue   # a player unit -> leave it alone
        name, off, mask = order[i % len(order)]
        i += 1
        cur = rd(h, addr + off, 1)
        if cur is None:
            continue
        wr(h, addr + off, bytes([cur[0] | mask]))
        granted += 1
        print(f"enemy slot {s:2} (hp {b[0x14] | (b[0x15] << 8)}/{mhp}, lvl {lvl}) at "
              f"({b[0x33]},{b[0x34]}) -> {name} (+0x{off:02X}/0x{mask:02X})")
    print(f"\ngranted a buff to {granted} enemy unit(s). Attack one with the Arcanum wielder to steal "
          f"+ test the effect (precedence picks the listed buff unless the foe also has Reraise).")
finally:
    k32.CloseHandle(h)
