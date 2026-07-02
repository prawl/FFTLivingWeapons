#!/usr/bin/env python
"""
Intercept probe (WRITE, reversible): while the ACTING unit is an AI enemy, hold a chosen
victim's tile into the actor's band target bytes (+0x4F/+0x50 -- the DoAction target that
the proven confirm-time redirect rewrites) so the enemy's swing lands on the victim's tile.

Tests whether the redirect window exists on AI turns (no confirm-dialog pause): the engine
authors the target when the AI commits, DoAction reads it at execution -- a 30ms hold should
own the bytes at read time unless author->execute is sub-frame.

Acting unit: G_ACTOR 0x14186AF68 holds a pointer to the acting unit's combat frame
(array base 0x141853CE0, stride 0x200); band entry = frame + 0x1C. Team byte: frame +0x04
(players 0x00, enemies 0x03 in the field battles probed so far); flags1 +0x06 bit 0x20 = enemy.
Victim tile read live from the victim frame's band gx/gy (+0x33/+0x34).

Battle exit reverts everything (the engine re-authors targets per action); Ctrl+C just stops.

USAGE (game running, IN a live battle):
    python intercept_probe.py status          # read-only: acting ptr/slot, teams, tiles
    python intercept_probe.py hold <slot>     # hold victim <slot>'s tile into every AI actor's
                                              # target bytes @30ms until Ctrl+C
"""
import os
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from ct_probe import PROC, PV, PV_W, find_pid, k32, rd, wr

G_ACTOR = 0x14186AF68
BASE = 0x141853CE0
STRIDE = 0x200
N = 28
ENTRY = 0x1C            # band entry = frame + 0x1C
OFF_TEAM = 0x04
OFF_FLAGS1 = 0x06       # bit 0x20 = enemy
OFF_LEVEL = 0x29
OFF_HPCUR = 0x30
OFF_HPMAX = 0x32
B_GX = 0x33             # band-relative
B_GY = 0x34
B_TGX = 0x4F            # band-relative target tile (DoAction reads this)
B_TGY = 0x50


def u8(h, a):
    b = rd(h, a, 1)
    return b[0] if b else 0


def u16(h, a):
    b = rd(h, a, 2)
    return (b[0] | (b[1] << 8)) if b else 0


def u64(h, a):
    b = rd(h, a, 8)
    return int.from_bytes(b, "little") if b else 0


def sane(h, s):
    lvl = u8(h, s + OFF_LEVEL)
    mhp = u16(h, s + OFF_HPMAX)
    cur = u16(h, s + OFF_HPCUR)
    return 1 <= lvl <= 99 and 1 <= mhp <= 9999 and 1 <= cur <= mhp


def actor_slot(h):
    """(frame_ptr, slot) of the acting unit, or (0, -1) when G_ACTOR is empty/foreign."""
    p = u64(h, G_ACTOR)
    if p < BASE or p >= BASE + N * STRIDE:
        return 0, -1
    off = p - BASE
    if off % STRIDE:
        return 0, -1
    return p, off // STRIDE


def cmd_status(h):
    p, slot = actor_slot(h)
    print(f"G_ACTOR -> 0x{p:012X} (slot {slot})" if slot >= 0 else "G_ACTOR empty / not a combat frame")
    for i in range(N):
        s = BASE + i * STRIDE
        if not sane(h, s):
            continue
        e = s + ENTRY
        print(f"  slot {i:>2} team 0x{u8(h, s + OFF_TEAM):02X} flags1 0x{u8(h, s + OFF_FLAGS1):02X} "
              f"lvl {u8(h, s + OFF_LEVEL):>2} tile ({u8(h, e + B_GX)},{u8(h, e + B_GY)}) "
              f"target ({u8(h, e + B_TGX)},{u8(h, e + B_TGY)}){'   <-- ACTING' if i == slot else ''}")


def cmd_hold(h, victim):
    """Blanket hold: pin EVERY enemy slot's target bytes at the victim's tile (not just the
    G_ACTOR unit -- the actor pointer proved stale/phase-bound on AI turns; kill attribution
    hits the same gap, hence its tq-fallback). A unit's own action is the only consumer of its
    target bytes, so pre-pinning all enemies is safe. Stomp log: every write prints the value
    the ENGINE had put there since our last pass -- victim-coord-shaped old values right before
    an action are the engine authoring its own target (we are racing it)."""
    vs = BASE + victim * STRIDE
    if not sane(h, vs):
        print(f"victim slot {victim} is not a sane unit -- refusing")
        return
    print(f"pinning ALL enemy targets to victim slot {victim}'s tile @30ms; Ctrl+C stops.")
    writes = 0
    try:
        while True:
            ve = vs + ENTRY
            gx, gy = u8(h, ve + B_GX), u8(h, ve + B_GY)
            for i in range(N):
                if i == victim:
                    continue
                s = BASE + i * STRIDE
                if not sane(h, s) or not (u8(h, s + OFF_FLAGS1) & 0x20):
                    continue                                    # enemies only
                ae = s + ENTRY
                ox, oy = u8(h, ae + B_TGX), u8(h, ae + B_TGY)
                if ox != gx or oy != gy:
                    wr(h, ae + B_TGX, bytes([gx]))
                    wr(h, ae + B_TGY, bytes([gy]))
                    writes += 1
                    t = time.strftime("%H:%M:%S")
                    print(f"  {t} enemy slot {i}: target was ({ox},{oy}) -> pinned ({gx},{gy})  [write #{writes}]")
            time.sleep(0.03)
    except KeyboardInterrupt:
        print(f"stopped. {writes} target write(s).")


def main():
    args = sys.argv[1:]
    mode = args[0] if args else ""
    if mode not in ("status", "hold"):
        print(__doc__)
        return
    pid = find_pid(PROC)
    if not pid:
        print(f"{PROC} not running")
        return
    h = k32.OpenProcess(PV_W if mode == "hold" else PV, False, pid)
    if not h:
        print("OpenProcess failed")
        return
    try:
        if mode == "status":
            cmd_status(h)
        else:
            cmd_hold(h, int(args[1], 0))
    finally:
        k32.CloseHandle(h)


if __name__ == "__main__":
    main()
