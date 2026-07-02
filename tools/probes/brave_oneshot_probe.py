#!/usr/bin/env python
"""
Brave re-normalize probe (WRITE, battle-transient): one-shot raise a unit's CURRENT brave
(combat +0x2B) by a delta, then watch (orig, current) at 10Hz and log every change -- settles
the Kobu-rework premise: does a one-shot current write STICK, or does the engine re-normalize
current back from orig (+0x2A), and exactly WHEN (turn edge / action / continuous)?

Delta may be negative (a capped-brave unit has no raise headroom); the result is floored at 25 --
comfortably above the ~10-15 brave-chicken threshold -- and capped at 97. Orig is never written
(it is the Wielder.Locate fingerprint and the persistent base). Current is rebuilt from orig at
battle exit, so the write cannot leak out of the battle.

USAGE (game running, IN a live battle; then let the unit take a turn):
    python -u brave_oneshot_probe.py <combatSlot> [delta=5]   (delta may be negative, e.g. -5)
"""
import os
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from ct_probe import PROC, PV_W, find_pid, k32, rd, wr

BASE, STRIDE = 0x141853CE0, 0x200
O_BR, C_BR = 0x2A, 0x2B
O_LVL, O_HP, O_MHP = 0x29, 0x30, 0x32


def u8(h, a):
    b = rd(h, a, 1)
    return b[0] if b else 0


def u16(h, a):
    b = rd(h, a, 2)
    return (b[0] | (b[1] << 8)) if b else 0


def main():
    if len(sys.argv) < 2:
        print(__doc__)
        return
    slot = int(sys.argv[1], 0)
    delta = int(sys.argv[2], 0) if len(sys.argv) > 2 else 5
    pid = find_pid(PROC)
    if not pid:
        sys.exit(f"{PROC} not running")
    h = k32.OpenProcess(PV_W, False, pid)
    try:
        s = BASE + slot * STRIDE
        lvl, mhp, hp = u8(h, s + O_LVL), u16(h, s + O_MHP), u16(h, s + O_HP)
        if not (1 <= lvl <= 99) or not (1 <= hp <= mhp <= 9999):
            print(f"slot {slot} not a sane live unit -- refusing")
            return
        ob, cb = u8(h, s + O_BR), u8(h, s + C_BR)
        target = max(25, min(97, cb + delta))   # floor 25: never approach the brave-chicken band
        if target == cb:
            print(f"slot {slot}: orig {ob} current {cb} -- delta {delta:+d} is a no-op after clamping; "
                  f"pick a different delta/unit")
            return
        print(f"slot {slot}: orig {ob} current {cb} -> one-shot current := {target} (orig untouched)")
        wr(h, s + C_BR, bytes([target]))
        last = (u8(h, s + O_BR), u8(h, s + C_BR))
        print(f"written; watching (orig, current) @10Hz -- play the unit's turn. Ctrl+C/stop when done.")
        print(f"  t=0 orig {last[0]} current {last[1]}")
        t0 = time.time()
        while True:
            cur = (u8(h, s + O_BR), u8(h, s + C_BR))
            if cur != last:
                print(f"  t={time.time() - t0:7.2f}s  orig {last[0]}->{cur[0]}  current {last[1]}->{cur[1]}")
                last = cur
            time.sleep(0.1)
    except KeyboardInterrupt:
        pass
    finally:
        k32.CloseHandle(h)


if __name__ == "__main__":
    main()
