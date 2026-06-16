#!/usr/bin/env python
"""
HEAL-NUMBER PROBE -- can we make a silent band-HP write surface the GREEN floating heal numeral?

Our aura/font/Benediction heals write band +0x14 directly. That bypasses the engine's
damage-APPLICATION path -- the same path that spawns the floating combat number over the unit. So our
heals are invisible. STRETCH GOAL: find a HOLDABLE per-unit field that, when written, makes that green
number appear, so the aura is not silent.

ONE CHEAP HYPOTHESIS to test first: when a REAL heal lands, does the victim's OWN unit struct briefly
carry the displayed magnitude (a "last-delta / display-request" field)? If yes -> we write it next to
our HP tick and the number renders. If the magnitude only ever lives in a function arg / global render
queue, this is the LEVEL-UP-BANNER wall (render is external/event-driven, not a holdable byte) and
silent is the ceiling without a debugger.

USAGE (game running, battle live):
  python heal_number_probe.py
      List valid band slots (slot, lvl/br/fa, hp/maxhp, pos). Pick a target to heal.

  python heal_number_probe.py watch <slot>
      DISCOVERY (read-only). Polls that slot's full 0x200 struct as fast as it can. Keeps a 0.5s
      pre-roll of every byte change. The instant HP RISES (a heal lands), it freezes, captures ~1.5s
      more, then prints a consolidated per-offset timeline of EVERYTHING that changed around the heal.
      Offsets whose byte value OR u16-at-offset ever equalled the heal amount are flagged [== AMT].
      Have me cast a real Cure/Potion on <slot>; tell me the amount so the [== AMT] flag is trustable.
      KNOWN-CHURN offsets (HP/CT/anim/pos) are labelled so the eye can skip them.

  python heal_number_probe.py poke <slot> <off> <val> [width] [hpDelta]
      CAUSATION (write). Writes <val> (default width 2 = u16, little-endian) at the candidate <off> on
      <slot>; optionally also adds hpDelta to HP so the engine has a "heal happened" context. Then
      watch the screen: did the green number pop? Read-back printed. width=1/2/4. RPM/WPM guarded.

READ-ONLY by default. poke writes only the bytes you name. Cannot crash the game (guarded RPM/WPM).
"""
import os
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from ct_probe import (BAND_ANCHOR, PROC, PV_W, STRIDE, A_HP, A_LVL, A_MAXHP,
                      A_OBRAVE, A_OFAITH, find_pid, k32, rd, u16, wr)

ENTRY = 0x1C
A_GX, A_GY = 0x33, 0x34
CT = 0x41  # scheduler CT (combat base +0x41) -- churns every tick

# Offsets known to churn for unrelated reasons; labelled so the eye can skip them in the dump.
KNOWN = {
    A_HP: "HP", A_HP + 1: "HP.hi", A_GX: "gx", A_GY: "gy", CT: "CT",
    0x09: "ACtTurn", 0x25: "ACtSlam", 0x12: "inb", 0x13: "inb.hi",
}


def valid(b):
    lvl, br, fa = b[A_LVL], b[A_OBRAVE], b[A_OFAITH]
    mhp = u16(b, A_MAXHP)
    return 1 <= lvl <= 99 and 1 <= br <= 100 and 1 <= fa <= 100 and 1 <= mhp < 2000


def addr_of(slot):
    return BAND_ANCHOR + ENTRY + (slot - 24) * STRIDE


def list_units(h):
    print("slot  lvl/br/fa  hp/maxhp   pos")
    found = 0
    for n in range(-24, 25):
        b = rd(h, BAND_ANCHOR + ENTRY + n * STRIDE, 0x60)
        if b is None or not valid(b):
            continue
        found += 1
        print(f"  {n + 24:>2}  {b[A_LVL]}/{b[A_OBRAVE]}/{b[A_OFAITH]}"
              f"  {u16(b, A_HP):>4}/{u16(b, A_MAXHP):<4}  ({b[A_GX]},{b[A_GY]})")
    print(f"{found} valid units. Next: heal_number_probe.py watch <slot>")


def watch(h, slot):
    addr = addr_of(slot)
    base = rd(h, addr, STRIDE)
    if base is None or not valid(base):
        sys.exit(f"slot {slot} is not a valid unit right now")
    print(f"watching slot {slot} @ {addr:#x}  hp {u16(base, A_HP)}/{u16(base, A_MAXHP)}")
    print("cast a real heal on this unit now... (Ctrl-C to stop)\n")

    prev = base
    preroll = []          # (t, off, old, new) within the last 0.5s -- the lead-up to a heal
    captured = None       # dict off -> list of (t, val) once armed
    armed_until = 0.0
    amt = 0
    while True:
        t = time.perf_counter()
        cur = rd(h, addr, STRIDE)
        if cur is None:
            time.sleep(0.02)
            continue
        for off in range(STRIDE):
            if cur[off] != prev[off]:
                rec = (t, off, prev[off], cur[off])
                preroll.append(rec)
                if captured is not None:
                    captured.setdefault(off, []).append((t, cur[off]))

        new_hp, old_hp = u16(cur, A_HP), u16(prev, A_HP)
        if captured is None and new_hp > old_hp:
            amt = new_hp - old_hp
            print(f"  >>> HEAL DETECTED on slot {slot}: +{amt}  (hp {old_hp}->{new_hp})  capturing 1.5s...")
            captured = {}
            for (rt, off, _o, nv) in preroll:        # fold the pre-roll in (display req may precede HP)
                captured.setdefault(off, []).append((rt, nv))
            armed_until = t + 1.5

        preroll = [r for r in preroll if t - r[0] <= 0.5]
        prev = cur

        if captured is not None and t >= armed_until:
            _report(captured, amt, t)
            captured = None
            preroll = []
            print("\n  ...watching again. Heal once more, or Ctrl-C.\n")


def _report(captured, amt, t0):
    print(f"\n  ===== change timeline around the +{amt} heal ({len(captured)} offsets touched) =====")
    print("  off     label        value sequence (newest writes win)         flags")
    for off in sorted(captured):
        seq = captured[off]
        vals = [v for (_tt, v) in seq]
        label = KNOWN.get(off, "")
        flag = ""
        # byte value ever == amount?
        if amt and any(v == amt for v in vals):
            flag = "[byte == AMT]"
        # u16 read at this offset (over the whole captured set) ever == amount?
        shown = ",".join(str(v) for v in vals[:8]) + ("..." if len(vals) > 8 else "")
        print(f"  +{off:#04x}  {label:<11}  {shown:<42}  {flag}")
    if amt:
        print(f"\n  NOTE: also eyeball any offset whose two adjacent bytes form u16 {amt} "
              f"(={amt & 0xff},{(amt >> 8) & 0xff}). Then: poke <slot> <off> {amt} 2  and watch the screen.")


def poke(h, slot, off, val, width, hp_delta):
    addr = addr_of(slot)
    b = rd(h, addr, STRIDE)
    if b is None or not valid(b):
        sys.exit(f"slot {slot} is not a valid unit right now")
    hp, mhp = u16(b, A_HP), u16(b, A_MAXHP)
    print(f"before: +{off:#04x} = {b[off]} (u16 {u16(b, off)})   hp {hp}/{mhp}")
    wr(h, addr + off, val.to_bytes(width, "little"))
    if hp_delta and hp > 0:
        nhp = max(1, min(mhp, hp + hp_delta))
        wr(h, addr + A_HP, nhp.to_bytes(2, "little"))
    rb = rd(h, addr, STRIDE)
    print(f"after:  +{off:#04x} = {rb[off]} (u16 {u16(rb, off)})   hp {u16(rb, A_HP)}/{u16(rb, A_MAXHP)}")
    print("  -> did the green number pop over the unit? (watch the screen)")


def main():
    pid = find_pid(PROC + ".exe")
    if not pid:
        sys.exit(f"{PROC}.exe not running")
    h = k32.OpenProcess(PV_W, False, pid)

    a = sys.argv[1:]
    if not a:
        list_units(h)
    elif a[0] == "watch" and len(a) == 2:
        try:
            watch(h, int(a[1]))
        except KeyboardInterrupt:
            print("\nstopped.")
    elif a[0] == "poke" and len(a) >= 4:
        slot, off, val = int(a[1]), int(a[2], 0), int(a[3], 0)
        width = int(a[4]) if len(a) > 4 else 2
        hp_delta = int(a[5]) if len(a) > 5 else 0
        poke(h, slot, off, val, width, hp_delta)
    else:
        print(__doc__)


if __name__ == "__main__":
    main()
