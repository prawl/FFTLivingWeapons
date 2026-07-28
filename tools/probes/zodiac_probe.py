#!/usr/bin/env python
"""
Zodiac sign / compatibility hunt (READ-ONLY).

WHY THIS EXISTS
---------------
FFT's zodiac compatibility is a hidden multiplier on damage, hit rate and status landing, and this
repo has never once looked at it. If the sign is a readable per-unit byte, an entire design axis
opens: signature effects that key on compatibility, a card that finally tells the player why that
attack whiffed, or a weapon that rewrites its wielder's sign.

METHOD: identification against KNOWN TRUTH, with no assumption about the encoding.
Every prior attempt in this repo to guess a field's encoding from its shape has cost a cycle
(the AREC xref byte, the +0x02 team word). So this probe assumes only that the sign is a
BIJECTION: two units with the same sign must read the same byte, two units with different signs
must read different bytes. It never assumes the order Aries..Pisces, nor a 0 base, nor a width.
The mapping it discovers is an OUTPUT, printed for you to sanity-check, not an input.

HOW TO RUN IT
-------------
1. Open the party/status screen and write down each unit's ZODIAC SIGN and its slot.
   You need at least FOUR units, and it is much stronger if TWO OF THEM SHARE A SIGN: a shared
   sign is what kills offsets that merely happen to differ per unit (level, HP, nameId all differ
   per unit and would otherwise survive every filter).
2. python -u zodiac_probe.py find 0=Aquarius 1=Leo 2=Leo 3=Virgo 4=Cancer

VERBS
-----
  python -u zodiac_probe.py census [--space roster|combat]
      What the probe can see: populated slots with level/HP/nameId, so you can match slots to the
      units on screen before spending a real pass.

  python -u zodiac_probe.py find <slot>=<Sign> [<slot>=<Sign> ...] [--space both]
      The pass. Prints every byte offset consistent with the signs you supplied, in both the
      roster row (stride 0x258) and the combat struct (stride 0x200), with the encoding each
      offset implies. Also prints the CHANCE BASELINE: how many offsets a random assignment would
      be expected to pass, so a single survivor can be told from noise.

  python -u zodiac_probe.py watch <space> <slot> <off> [--secs 30]
      Confirm a candidate is stable: a real sign never changes. Anything that drifts is not it.

SIGNS
-----
Serpentarius is included because FFT has it (Elidibus and friends). Spelling is forgiving:
case and a leading "the" are ignored, and Ophiuchus is accepted for Serpentarius.
"""
import argparse
import os
import sys
import time
from collections import defaultdict

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from ct_probe import PROC, PV_W, find_pid, k32, rd  # noqa: E402

# Constants mirrored from LivingWeapon/Offsets.cs (1.5.x, no ASLR).
ROSTER_BASE, ROSTER_STRIDE, ROSTER_SLOTS = 0x1411A7D10, 0x258, 50
COMBAT_BASE, COMBAT_STRIDE, COMBAT_SLOTS = 0x141853CE0, 0x200, 21
R_NAMEID = 0x230
C_LVL, C_HP, C_MAXHP = 0x29, 0x30, 0x32

SIGNS = ["Aries", "Taurus", "Gemini", "Cancer", "Leo", "Virgo", "Libra", "Scorpio",
         "Sagittarius", "Capricorn", "Aquarius", "Pisces", "Serpentarius"]
ALIASES = {"ophiuchus": "serpentarius"}


def norm_sign(s):
    k = s.strip().lower().replace("the ", "")
    k = ALIASES.get(k, k)
    for real in SIGNS:
        if real.lower() == k:
            return real
    sys.exit(f"unknown sign {s!r}; expected one of: {', '.join(SIGNS)}")


def open_game():
    pid = find_pid(PROC)
    if not pid:
        sys.exit(f"{PROC} not running")
    h = k32.OpenProcess(PV_W, False, pid)
    if not h:
        sys.exit(f"could not open pid {pid}")
    return h


def space_of(name):
    if name == "roster":
        return ROSTER_BASE, ROSTER_STRIDE, ROSTER_SLOTS
    return COMBAT_BASE, COMBAT_STRIDE, COMBAT_SLOTS


def row(h, space, slot):
    base, stride, _ = space_of(space)
    return rd(h, base + slot * stride, stride)


def u16(b, o):
    return b[o] | (b[o + 1] << 8) if b and o + 1 < len(b) else None


def cmd_census(a):
    h = open_game()
    for space in (["roster", "combat"] if a.space == "both" else [a.space]):
        base, stride, slots = space_of(space)
        print(f"\n=== {space} (base 0x{base:X}, stride 0x{stride:X}) ===")
        for s in range(slots):
            b = row(h, space, s)
            if not b:
                continue
            if space == "roster":
                lvl, nid = b[0x29] if 0x29 < len(b) else 0, u16(b, R_NAMEID)
                if not (1 <= lvl <= 99):
                    continue
                print(f"  slot {s:>2}  level {lvl:>2}  nameId {nid}")
            else:
                lvl, hp, mhp = b[C_LVL], u16(b, C_HP), u16(b, C_MAXHP)
                if not (1 <= lvl <= 99 and 1 <= hp <= mhp <= 9999):
                    continue
                print(f"  slot {s:>2}  level {lvl:>2}  hp {hp}/{mhp}")


def analyse(h, space, known):
    """Offsets whose per-slot byte is a BIJECTION with the supplied signs."""
    base, stride, slots = space_of(space)
    rows = {}
    for slot in known:
        b = row(h, space, slot)
        if b is None:
            sys.exit(f"{space} slot {slot} unreadable")
        rows[slot] = b

    survivors = []
    for off in range(stride):
        by_sign, by_val = defaultdict(set), defaultdict(set)
        ok = True
        for slot, sign in known.items():
            v = rows[slot][off]
            by_sign[sign].add(v)
            by_val[v].add(sign)
            if v > 12:                      # 13 signs; anything larger is not an index
                ok = False
                break
        if not ok:
            continue
        # same sign => same byte, different sign => different byte
        if any(len(vs) != 1 for vs in by_sign.values()):
            continue
        if any(len(ss) != 1 for ss in by_val.values()):
            continue
        survivors.append((off, {s: next(iter(v)) for s, v in by_sign.items()}))
    return survivors, rows


def analyse_nibble(h, space, known):
    """Same bijection test, but over NIBBLES.

    Added 2026-07-27 after both the whole-byte bijection and the birthday pair returned zero
    survivors on eight units. PSX FFT packs zodiac into a nibble beside other unit flags, and a
    nibble is invisible to any byte-level test: the low half of a byte can be a clean sign index
    while the byte as a whole looks like noise because the high half carries something else."""
    base, stride, _ = space_of(space)
    rows = {}
    for slot in known:
        b = row(h, space, slot)
        if b is None:
            sys.exit(f"{space} slot {slot} unreadable")
        rows[slot] = b

    survivors = []
    for off in range(stride):
        for half in ("hi", "lo"):
            by_sign, by_val = defaultdict(set), defaultdict(set)
            for slot, sign in known.items():
                byte = rows[slot][off]
                v = (byte >> 4) if half == "hi" else (byte & 0xF)
                if v > 12:
                    by_sign.clear()
                    break
                by_sign[sign].add(v)
                by_val[v].add(sign)
            if not by_sign:
                continue
            if any(len(vs) != 1 for vs in by_sign.values()):
                continue
            if any(len(ss) != 1 for ss in by_val.values()):
                continue
            survivors.append((off, half, {s: next(iter(v)) for s, v in by_sign.items()}))
    return survivors


def cmd_nibble(a):
    h = open_game()
    known = {}
    for pair in a.pairs:
        slot_s, sign = pair.split("=", 1)
        known[int(slot_s, 0)] = norm_sign(sign)
    for space in (["roster", "combat"] if a.space == "both" else [a.space]):
        survivors = analyse_nibble(h, space, known)
        _, stride, _ = space_of(space)
        print(f"\n=== {space}: {len(survivors)} surviving nibble(s) of {stride * 2} ===")
        for off, half, mapping in survivors:
            pretty = ", ".join(f"{s}={v}" for s, v in sorted(mapping.items(), key=lambda x: x[1]))
            print(f"  +0x{off:03X} {half}   {pretty}")
        if not survivors:
            print("  nothing survived at nibble granularity either.")


def cmd_find(a):
    h = open_game()
    known = {}
    for pair in a.pairs:
        if "=" not in pair:
            sys.exit(f"expected <slot>=<Sign>, got {pair!r}")
        slot, sign = pair.split("=", 1)
        known[int(slot, 0)] = norm_sign(sign)

    distinct = len(set(known.values()))
    shared = len(known) - distinct
    print(f"{len(known)} units, {distinct} distinct signs, {shared} sharing a sign")
    if len(known) < 4:
        print("WARNING: fewer than 4 units. Expect many false survivors.")
    if shared == 0:
        print("WARNING: no two units share a sign, so every per-unit-unique byte (level, HP, "
              "nameId, position) will survive. Add a pair that shares a sign to make this decisive.")

    for space in (["roster", "combat"] if a.space == "both" else [a.space]):
        survivors, _ = analyse(h, space, known)
        base, stride, _ = space_of(space)
        # Chance baseline: with N units over 13 possible values, a random byte passes the
        # bijection test with probability p; expected survivors = p * stride. Reported so a
        # single hit can be told from noise instead of assumed to be the answer.
        n = len(known)
        p = 1.0
        for i in range(n):
            p *= max(0, (13 - i)) / 13.0 if i else 1.0
        expected = p * stride / (13.0 ** 0)
        print(f"\n=== {space}: {len(survivors)} surviving offset(s) of {stride} "
              f"(rough chance baseline ~{expected:.1f}) ===")
        for off, mapping in survivors:
            pretty = ", ".join(f"{s}={v}" for s, v in sorted(mapping.items(), key=lambda x: x[1]))
            print(f"  +0x{off:03X}   {pretty}")
        if not survivors:
            print("  nothing survived: either the sign is not a byte in this struct, or a slot "
                  "number is wrong. Re-check with `census`.")


# Western date ranges, which FFT uses. (start_month, start_day) inclusive, in wheel order.
ZODIAC_RANGES = [
    ("Capricorn", 12, 22), ("Aquarius", 1, 20), ("Pisces", 2, 19), ("Aries", 3, 21),
    ("Taurus", 4, 20), ("Gemini", 5, 21), ("Cancer", 6, 21), ("Leo", 7, 23),
    ("Virgo", 8, 23), ("Libra", 9, 23), ("Scorpio", 10, 23), ("Sagittarius", 11, 22),
]


def sign_of(month, day):
    """Sign for a (month, day) birthday, or None if the date is impossible."""
    if not (1 <= month <= 12 and 1 <= day <= 31):
        return None
    best = "Capricorn"
    for name, m, d in ZODIAC_RANGES:
        if (month, day) >= (m, d):
            best = name
    # December is the wrap point and the ordered scan above cannot get it right on its own:
    # Capricorn's (12,22) sits FIRST in wheel order, so a later entry always overwrites it.
    if month == 12:
        best = "Capricorn" if day >= 22 else "Sagittarius"
    return best


def cmd_birthday(a):
    """Hunt a BIRTHDAY pair rather than a sign index.

    Written after the eight-unit bijection pass returned zero survivors in both spaces
    (2026-07-27). If the game stores month+day and derives the sign, then same-sign units have
    DIFFERENT bytes and a bijection test rejects the correct field by construction. This test
    assumes only the standard western date ranges: for every offset it reads (month, day) as an
    adjacent byte pair, in both orders, and keeps the offset only if the derived sign matches the
    supplied sign for EVERY unit. A wrong offset has to satisfy all of them at once."""
    h = open_game()
    known = {}
    for pair in a.pairs:
        slot_s, sign = pair.split("=", 1)
        known[int(slot_s, 0)] = norm_sign(sign)

    for space in (["roster", "combat"] if a.space == "both" else [a.space]):
        base, stride, _ = space_of(space)
        rows = {}
        for slot in known:
            b = row(h, space, slot)
            if b is None:
                sys.exit(f"{space} slot {slot} unreadable")
            rows[slot] = b

        hits = []
        for off in range(stride - 1):
            for order in ("md", "dm"):
                ok, sample = True, {}
                for slot, want in known.items():
                    a0, b0 = rows[slot][off], rows[slot][off + 1]
                    month, day = (a0, b0) if order == "md" else (b0, a0)
                    got = sign_of(month, day)
                    if got != want:
                        ok = False
                        break
                    sample[slot] = f"{month}/{day}"
                if ok:
                    hits.append((off, order, sample))
        print(f"\n=== {space}: {len(hits)} offset(s) whose byte pair derives every supplied sign ===")
        for off, order, sample in hits:
            dates = ", ".join(f"s{s}={v}" for s, v in sorted(sample.items()))
            print(f"  +0x{off:03X} ({order})  {dates}")
        if not hits:
            print("  nothing. Either the birthday is not two adjacent bytes here, or a slot/sign "
                  "pairing is wrong, or the sign is stored somewhere other than the unit struct.")


def cmd_watch(a):
    h = open_game()
    base, stride, _ = space_of(a.space)
    addr = base + a.slot * stride + a.off
    last = None
    print(f"watching {a.space} slot {a.slot} +0x{a.off:X} (0x{addr:X}) for {a.secs}s")
    end = time.time() + a.secs
    while time.time() < end:
        b = rd(h, addr, 1)
        v = b[0] if b else None
        if v != last:
            print(f"  {time.strftime('%H:%M:%S')}  {last} -> {v}")
            last = v
        time.sleep(0.25)
    print("done. A real sign never moved.")


def main():
    p = argparse.ArgumentParser(description=__doc__,
                                formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = p.add_subparsers(dest="cmd", required=True)

    c = sub.add_parser("census")
    c.add_argument("--space", choices=["roster", "combat", "both"], default="both")
    c.set_defaults(fn=cmd_census)

    f = sub.add_parser("find")
    f.add_argument("pairs", nargs="+", metavar="slot=Sign")
    f.add_argument("--space", choices=["roster", "combat", "both"], default="both")
    f.set_defaults(fn=cmd_find)

    bd = sub.add_parser("birthday")
    bd.add_argument("pairs", nargs="+", metavar="slot=Sign")
    bd.add_argument("--space", choices=["roster", "combat", "both"], default="combat")
    bd.set_defaults(fn=cmd_birthday)

    nb = sub.add_parser("nibble")
    nb.add_argument("pairs", nargs="+", metavar="slot=Sign")
    nb.add_argument("--space", choices=["roster","combat","both"], default="both")
    nb.set_defaults(fn=cmd_nibble)

    w = sub.add_parser("watch")
    w.add_argument("space", choices=["roster", "combat"])
    w.add_argument("slot", type=lambda x: int(x, 0))
    w.add_argument("off", type=lambda x: int(x, 0))
    w.add_argument("--secs", type=int, default=30)
    w.set_defaults(fn=cmd_watch)

    a = p.parse_args()
    a.fn(a)


if __name__ == "__main__":
    main()
