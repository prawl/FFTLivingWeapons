#!/usr/bin/env python
"""
Puppet probe (WRITE, reversible): hold the human-agency bit on every live AI enemy in the CURRENT
battle so you can DRIVE them (e.g. to test a kit-stamp cast). INDEPENDENT of the mod's PuppetHold,
which gates on flags1 (+0x06 & 0x20) and silently skips a battle whose enemies lack that flag --
this probe gates on "sane unit with the human bit CLEAR" instead (slot-sign/fingerprint, never the
fragile flag). Re-asserts every 150ms (the engine re-derives agency at setup events; the hold beats
it). Battle exit / game restart reverts.

Combat array: BASE 0x141853CE0, stride 0x200. Players already carry the human bit (skipped); AI
enemies do not. OR-sets +0x05 AND the shadow +0x1EE (both bit 0x08) -- exactly what PuppetHold does.

USAGE (game running, IN a live battle):
    python puppet_probe.py once     # single pass: set + read back (verify the bit lands), then exit
    python puppet_probe.py          # loop + HOLD until Ctrl+C (use this to drive the enemy team)
"""
import os
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from ct_probe import PROC, PV_W, find_pid, k32, rd, wr

BASE = 0x141853CE0
STRIDE = 0x200
N = 28
HUMAN = 0x08

OFF_AGENCY = 0x05
OFF_SHADOW = 0x1EE
OFF_JOB = 0x03
OFF_LEVEL = 0x29
OFF_BRAVE = 0x2A
OFF_FAITH = 0x2C
OFF_HPCUR = 0x30
OFF_HPMAX = 0x32


def u8(h, a):
    b = rd(h, a, 1)
    return b[0] if b else 0


def u16(h, a):
    b = rd(h, a, 2)
    return (b[0] | (b[1] << 8)) if b else 0


def sane_unit(h, slot):
    lvl = u8(h, slot + OFF_LEVEL)
    if lvl < 1 or lvl > 99:
        return False
    if u8(h, slot + OFF_BRAVE) > 100 or u8(h, slot + OFF_FAITH) > 100:
        return False
    mhp = u16(h, slot + OFF_HPMAX)
    cur = u16(h, slot + OFF_HPCUR)
    if mhp < 1 or mhp > 9999 or cur < 1 or cur > mhp:
        return False
    return True


def one_pass(h, verbose):
    """Set the human bit on every sane AI slot. Returns the list of slot indices it (re)set."""
    setlist = []
    for i in range(N):
        slot = BASE + i * STRIDE
        if not sane_unit(h, slot):
            continue
        ag = u8(h, slot + OFF_AGENCY)
        if ag & HUMAN:
            continue                                      # already human (player or already held)
        wr(h, slot + OFF_AGENCY, bytes([ag | HUMAN]))
        sh = u8(h, slot + OFF_SHADOW)
        wr(h, slot + OFF_SHADOW, bytes([sh | HUMAN]))
        setlist.append(i)
        if verbose:
            back = u8(h, slot + OFF_AGENCY)
            job = u8(h, slot + OFF_JOB)
            print(f"  slot {i:>2} 0x{slot:09X} job 0x{job:02X} lvl {u8(h, slot + OFF_LEVEL):>2}: "
                  f"agency 0x{ag:02X} -> 0x{back:02X} {'[HELD]' if back & HUMAN else '[FAILED to stick]'}")
    return setlist


def main():
    once = len(sys.argv) > 1 and sys.argv[1] == "once"
    pid = find_pid(PROC)
    if not pid:
        sys.exit(f"{PROC} not running")
    h = k32.OpenProcess(PV_W, False, pid)
    try:
        if once:
            print("single pass -- setting human bit on AI enemies and reading back:")
            got = one_pass(h, verbose=True)
            print(f"\n{len(got)} slot(s) set: {got}" if got else "\nno AI enemy slots found (wrong battle state?)")
            return
        print("Puppeting AI enemies, holding (Ctrl+C to stop)...")
        last = None
        while True:
            got = one_pass(h, verbose=False)
            msg = f"(re)set human on slots {got}" if got else "holding -- all sane units human"
            if msg != last:
                print(" ", msg)
                last = msg
            time.sleep(0.15)
    except KeyboardInterrupt:
        print("stopped.")
    finally:
        k32.CloseHandle(h)


if __name__ == "__main__":
    main()
