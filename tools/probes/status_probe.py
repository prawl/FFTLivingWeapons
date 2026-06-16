"""Dump live band units' status bitfield (+0x45..+0x49) decoded via the full FFT status map.

The band-relative status bytes equal the PSX "current status" 5-byte field (byte N = band +0x45+N);
confirmed by six cross-checks against the repo's proven bits (Dead/Undead +0x45, Reraise/Transparent
+0x47, Poison +0x48, Doom +0x49). Use it to confirm a buff's bit is live before wiring it into
LarcenyPolicy.Stealable -- e.g. Regen (+0x48/0x40) and Float (+0x47/0x40).
"""
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from ct_probe import PROC, PV_W, find_pid, k32, rd

ANCHOR = 0x14184F890   # Offsets.CombatAnchor
STRIDE = 0x200         # Offsets.CombatStride
ENTRY  = 0x1C          # Offsets.BandEntry
BASE   = ANCHOR + ENTRY - 24 * STRIDE   # n=-24 band anchor (Offsets.BandReadBase)
SLOTS  = 49

# band-relative status byte -> (mask, name), straight from FFTHandsFree StatusDecoder.StatusMap
STATUS = [
    (0x45, [(0x40, "Crystal"), (0x20, "Dead"), (0x10, "Undead"), (0x08, "Charging"),
            (0x04, "Jump"), (0x02, "Defending"), (0x01, "Performing")]),
    (0x46, [(0x80, "Petrify"), (0x40, "Invite"), (0x20, "Blind"), (0x10, "Confuse"),
            (0x08, "Silence"), (0x04, "Vampire"), (0x02, "Cursed"), (0x01, "Treasure")]),
    (0x47, [(0x80, "Oil"), (0x40, "Float"), (0x20, "Reraise"), (0x10, "Transparent"),
            (0x08, "Berserk"), (0x04, "Chicken"), (0x02, "Frog"), (0x01, "Critical")]),
    (0x48, [(0x80, "Poison"), (0x40, "Regen"), (0x20, "Protect"), (0x10, "Shell"),
            (0x08, "Haste"), (0x04, "Slow"), (0x02, "Stop"), (0x01, "Wall")]),
    (0x49, [(0x80, "Faith"), (0x40, "Innocent"), (0x20, "Charm"), (0x10, "Sleep"),
            (0x08, "DontMove"), (0x04, "DontAct"), (0x02, "Reflect"), (0x01, "DeathSentence")]),
]

pid = find_pid(PROC)
if not pid:
    sys.exit(f"{PROC} not running")
h = k32.OpenProcess(PV_W, False, pid)
try:
    for s in range(SLOTS):
        addr = BASE + s * STRIDE
        b = rd(h, addr, 0x60)
        if not b:
            continue
        mhp = b[0x16] | (b[0x17] << 8)
        lvl, br, fa = b[0x0D], b[0x0E], b[0x10]
        hp = b[0x14] | (b[0x15] << 8)
        if not (1 <= mhp < 2000 and 1 <= lvl <= 99 and 1 <= br <= 100 and 1 <= fa <= 100):
            continue
        gx, gy = b[0x33], b[0x34]
        active = [name for off, bits in STATUS for mask, name in bits if b[off] & mask]
        raw = " ".join(f"{b[o]:02X}" for o, _ in STATUS)
        side = "enemy" if (gx == 0 and gy == 0) else "field"
        print(f"slot {s:2} hp={hp:>4}/{mhp:<4} lvl={lvl:2} pos=({gx:2},{gy:2}) [{side}] +45..49={raw}"
              + (f" -> {', '.join(active)}" if active else " -> (none)"))
finally:
    k32.CloseHandle(h)
