#!/usr/bin/env python
"""
Reaction-ability GRANT / force hunt (`read`, `list`, `map` are READ-ONLY; `set` writes).

WHY THIS EXISTS
---------------
We can already SUPPRESS a reaction: the shipped Cripple signature hold-zeroes the 4-byte reaction
field at combat +0x94 and a Counter stopped firing through five hits (LIVE_LEDGER, 2026-06-09).
The inverse has never been tried. If OR-setting a bit makes a unit reaction it does not own start
firing, then "your blade teaches its wielder to counter" is a signature, and so is loading an
enemy up with reactions it should not have.

WHAT IS ACTUALLY KNOWN, AND WHAT IS NOT
---------------------------------------
KNOWN: combat +0x94 is 4 bytes, and zeroing it stops reactions. That is the whole of it.
NOT KNOWN: which bit is which. The repo's RSM convention is `rsmId = Ability-en Key - 256`, but
reaction Keys sit around 427-455, so their RSM ids (~171-199) do NOT fit in 32 bits. There must be
a reaction-range base, and NOBODY HAS MEASURED IT. This probe therefore refuses to guess: `map`
DERIVES the base from units whose reactions you can read off the screen, and reports whether the
derivation is self-consistent across them. Bit order (MSB-first vs LSB-first per byte) is likewise
derived, not assumed, because both conventions appear elsewhere in this game.

THE SEQUENCE
------------
1. python -u reaction_force_probe.py list
       Ability-en Keys that look like reactions, with names, so you can name what you see.
2. Open two or three units' ability screens and note their equipped REACTION.
3. python -u reaction_force_probe.py read <slot>
       The raw 4 bytes with every set bit enumerated under both bit orders.
4. python -u reaction_force_probe.py map 16=Counter 17="Auto-Potion" 18=none
       Derives (bit order, base) and prints the implied bit for every reaction. `none` is useful:
       a unit with NO reaction should read all zeros, which is the cheapest possible control.
5. python -u reaction_force_probe.py set <slot> <AbilityKeyOrName> [--secs 60]
       Grants the bit and HOLDS it (the engine may re-derive the field, exactly as stat growth
       does), then restores. Now get that unit hit and watch for the reaction to fire.

SAFETY
------
`set` refuses any slot that does not read as a sane live unit, snapshots the original 4 bytes,
and restores them on exit including Ctrl-C. It never writes any other address. Everything the
write does is battle-transient; nothing here touches the persistent roster.
"""
import argparse
import os
import sqlite3
import sys
import time
from pathlib import Path

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from ct_probe import PROC, PV_W, find_pid, k32, rd, wr  # noqa: E402

ROOT = Path(__file__).resolve().parents[2]
ABILITY_SQLITE = ROOT / "working" / "nxd_ability" / "ability.sqlite"

COMBAT_BASE, COMBAT_STRIDE, COMBAT_SLOTS = 0x141853CE0, 0x200, 21
C_REACT = 0x94          # 4 bytes; Cripple hold-zeroes exactly this
C_LVL, C_HP, C_MAXHP = 0x29, 0x30, 0x32
RSM_OFFSET = 256        # rsmId = Ability-en Key - 256 (repo convention)

# Reactions cluster in this Key band; `list` prints it rather than hardcoding a set, because the
# exact membership is what the operator needs to confirm against the screen.
REACTION_KEY_LO, REACTION_KEY_HI = 420, 460


def names():
    if not ABILITY_SQLITE.exists():
        return {}
    con = sqlite3.connect(ABILITY_SQLITE)
    try:
        return {r[0]: r[1] for r in con.execute('SELECT Key, Name FROM "Ability-en"')}
    finally:
        con.close()


def open_game():
    pid = find_pid(PROC)
    if not pid:
        sys.exit(f"{PROC} not running")
    h = k32.OpenProcess(PV_W, False, pid)
    if not h:
        sys.exit(f"could not open pid {pid}")
    return h


def slot_addr(slot):
    return COMBAT_BASE + slot * COMBAT_STRIDE


def sane_unit(h, slot):
    b = rd(h, slot_addr(slot), COMBAT_STRIDE)
    if not b:
        return None
    lvl = b[C_LVL]
    hp = b[C_HP] | (b[C_HP + 1] << 8)
    mhp = b[C_MAXHP] | (b[C_MAXHP + 1] << 8)
    return b if (1 <= lvl <= 99 and 1 <= hp <= mhp <= 9999) else None


def bits_set(raw, msb_first):
    """Indices of set bits across 4 bytes, under one convention."""
    out = []
    for byte_i, byte in enumerate(raw):
        for k in range(8):
            bit = (byte >> (7 - k)) & 1 if msb_first else (byte >> k) & 1
            if bit:
                out.append(byte_i * 8 + k)
    return out


def cmd_list(_):
    nm = names()
    if not nm:
        sys.exit(f"{ABILITY_SQLITE} missing; run the ability decode first")
    print(f"Ability-en Keys {REACTION_KEY_LO}..{REACTION_KEY_HI} (rsmId = Key - {RSM_OFFSET})")
    for k in range(REACTION_KEY_LO, REACTION_KEY_HI + 1):
        n = nm.get(k)
        if n:
            print(f"  Key {k:>3}  rsm {k - RSM_OFFSET:>3}  {n}")


def cmd_read(a):
    h = open_game()
    b = sane_unit(h, a.slot)
    if b is None:
        sys.exit(f"combat slot {a.slot} is not a sane live unit")
    raw = b[C_REACT:C_REACT + 4]
    print(f"slot {a.slot} @0x{slot_addr(a.slot) + C_REACT:X}  reaction field = {raw.hex(' ')}")
    print(f"  MSB-first set bits: {bits_set(raw, True)}")
    print(f"  LSB-first set bits: {bits_set(raw, False)}")
    if not any(raw):
        print("  field is ZERO: this unit has no reaction equipped (a good control unit).")


def cmd_map(a):
    """Derive (bit order, base) from units whose equipped reaction you can see on screen."""
    h = open_game()
    nm = names()
    by_name = {str(v).lower(): k for k, v in nm.items() if v}

    obs = []
    for pair in a.pairs:
        slot_s, want = pair.split("=", 1)
        slot = int(slot_s, 0)
        b = sane_unit(h, slot)
        if b is None:
            sys.exit(f"combat slot {slot} is not a sane live unit")
        raw = b[C_REACT:C_REACT + 4]
        if want.strip().lower() in ("none", "-", "0"):
            obs.append((slot, None, raw))
            continue
        key = by_name.get(want.strip().lower())
        if key is None:
            sys.exit(f"no Ability-en row named {want!r}; try `list`")
        obs.append((slot, key, raw))

    for slot, key, raw in obs:
        label = nm.get(key, "none") if key else "none"
        print(f"  slot {slot:>2}  {str(label)[:20]:<20} field {raw.hex(' ')}")

    controls = [o for o in obs if o[1] is None]
    for o in controls:
        if any(o[2]):
            print(f"\nWARNING: slot {o[0]} was declared reaction-less but its field is NONZERO. "
                  f"Either the slot is wrong or +0x94 is not purely a reaction bitfield.")

    print("\nderiving (bit order, base) from units that DO carry a reaction:")
    found = False
    for msb in (True, False):
        bases = {}
        ok = True
        for slot, key, raw in obs:
            if key is None:
                continue
            sb = bits_set(raw, msb)
            if len(sb) != 1:
                ok = False
                bases[slot] = f"{len(sb)} bits set, cannot attribute"
                continue
            bases[slot] = (key - RSM_OFFSET) - sb[0]
        vals = [v for v in bases.values() if isinstance(v, int)]
        order = "MSB-first" if msb else "LSB-first"
        if ok and vals and len(set(vals)) == 1:
            found = True
            print(f"  {order}: CONSISTENT, base = {vals[0]}  "
                  f"(bit = rsmId - {vals[0]}; rsmId = Key - {RSM_OFFSET})")
        else:
            print(f"  {order}: inconsistent {bases}")
    if not found:
        print("  neither order gave one base. Most likely a unit carries more than one reaction, "
              "or +0x94 packs something besides reactions. Add more units and a `none` control.")


def cmd_set(a):
    h = open_game()
    nm = names()
    key = a.ability if isinstance(a.ability, int) else None
    if key is None:
        by_name = {str(v).lower(): k for k, v in nm.items() if v}
        key = by_name.get(str(a.ability).lower())
        if key is None:
            sys.exit(f"no Ability-en row named {a.ability!r}; try `list`")
    if a.base is None or a.bit_order is None:
        sys.exit("pass --base and --bit-order from a successful `map` run; this probe will not "
                 "guess the encoding")

    bit = (key - RSM_OFFSET) - a.base
    if not (0 <= bit < 32):
        sys.exit(f"derived bit {bit} is outside the 32-bit field; base or ability is wrong")

    b = sane_unit(h, a.slot)
    if b is None:
        sys.exit(f"combat slot {a.slot} is not a sane live unit")
    addr = slot_addr(a.slot) + C_REACT
    original = bytes(b[C_REACT:C_REACT + 4])

    byte_i, k = bit // 8, bit % 8
    mask = (1 << (7 - k)) if a.bit_order == "msb" else (1 << k)
    target = bytearray(original)
    target[byte_i] |= mask

    print(f"slot {a.slot}: {nm.get(key)} -> bit {bit} (byte {byte_i} mask 0x{mask:02X})")
    print(f"  {original.hex(' ')} -> {bytes(target).hex(' ')}")
    print(f"  holding for {a.secs}s. GET THIS UNIT HIT and watch for the reaction. Ctrl-C restores.")
    try:
        end = time.time() + a.secs
        while time.time() < end:
            cur = rd(h, addr, 4)
            if cur != bytes(target):
                wr(h, addr, bytes(target))
            time.sleep(0.05)
    except KeyboardInterrupt:
        pass
    finally:
        wr(h, addr, original)
        back = rd(h, addr, 4)
        print(f"\nrestored to {original.hex(' ')} (read back {back.hex(' ') if back else '??'})")


def main():
    p = argparse.ArgumentParser(description=__doc__,
                                formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = p.add_subparsers(dest="cmd", required=True)
    sub.add_parser("list").set_defaults(fn=cmd_list)

    r = sub.add_parser("read")
    r.add_argument("slot", type=lambda x: int(x, 0))
    r.set_defaults(fn=cmd_read)

    m = sub.add_parser("map")
    m.add_argument("pairs", nargs="+", metavar="slot=Reaction|none")
    m.set_defaults(fn=cmd_map)

    s = sub.add_parser("set")
    s.add_argument("slot", type=lambda x: int(x, 0))
    s.add_argument("ability")
    s.add_argument("--base", type=int, default=None)
    s.add_argument("--bit-order", choices=["msb", "lsb"], default=None)
    s.add_argument("--secs", type=int, default=60)
    s.set_defaults(fn=cmd_set)

    a = p.parse_args()
    a.fn(a)


if __name__ == "__main__":
    main()
