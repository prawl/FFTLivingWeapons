#!/usr/bin/env python
"""LW-316 lane probes: the Faith and MaxHP halves of the three growth-lane questions.

Plain version: before the multi-lane growth engine (LW-317) ships, we prove live that the
two "held stat" lanes it needs actually work. Can we hold a unit's Faith higher so a magic
gun's spell hits harder? Can we hold a unit's Max HP higher without confusing the mod about
who earned a kill, and without the unit looking half-dead? This probe holds those bytes on
one chosen unit while the owner reads damage numbers and the HP bar on screen.
(The third LW-316 question, the WP table write, uses the existing instrument:
  python tools\\probes\\warbrand_damage_probe.py --hold 4=<newWP> --id <gunId>
stats byte[4] = WP; table base re-verified TRUSTWORTHY on this install 2026-08-25.)

Verbs
-----
  python tools\\probes\\lw316_lane_probes.py list
      Show every valid band unit (slot, side, level, brave/faith orig + CURRENT faith,
      hp/maxhp, tile) so the owner picks a slot for the verbs below. Read-only.

  python tools\\probes\\lw316_lane_probes.py faith_hold <slot> <value>
      Hold CURRENT faith at <value> on that band slot, re-asserted every 150ms (the engine
      may re-derive current from orig at turn boundaries; the hold loop wins). Ctrl+C
      restores the original byte.
      Offset provenance: current faith = combat +0x2D = band +0x11 (orig +0x2C/+0x10 is a
      re-normalizing decoy that never displays; proven lane: FFTMultiplayer StatHold,
      commit ead64b7, memory brave-faith-current-vs-orig-offsets).
      PRE-REGISTERED (write the interpretation before the run):
        PASS = the wielder's magic-gun shot (Glacial 74 / Blaze 75 / Blaster 76, formula 4)
               damage moves with the held faith; spell damage scales ~ (casterFa/100), so
               faith 60 -> 90 reads as roughly x1.5 on screen against the same target.
        FAIL = damage identical across shots with faith visibly held on the card
               -> CONTRADICTED: formula 4 does not read live current faith; magic guns
               demote to Faith-only-display or WP-only per the LW-316 row's fallback.

  python tools\\probes\\lw316_lane_probes.py maxhp_hold <slot> <newmax>
      Hold MaxHP (u16, band +0x16) at <newmax> (clamped to 999, the engine HP ceiling) on
      that ONE band slot, re-asserted every 50ms. CURRENT hp is deliberately NOT touched:
      whether it follows the raised max or leaves the unit reading hurt is one of the
      questions. Warns first if another live unit shares (level, <newmax>): the actor
      resolver keys on (maxHp, hp, level) and a collision resolves ambiguous (the godmode
      mis-credit lesson, tools/probes/condensed_fp_probe.py). While holding, it watches the
      condensed active-unit struct and prints whether its MaxHP mirror reads the RAISED or
      the ORIGINAL value when this unit acts. Ctrl+C restores MaxHP (and clamps hp back
      down if it exceeds the real max).
      PRE-REGISTERED:
        PASS = (a) the condensed mirror reads the RAISED max on the unit's own turn, and
               (b) a kill by the held unit credits ITS weapon in livingweapon.log
               (no "could not determine" / no mis-credit), and (c) the HP bar renders
               hp/newmax sanely (reading hurt is acceptable and worth recording; a glitched
               bar or a heal-lock is not).
        FAIL = mirror stuck on the ORIGINAL value (resolver mismatch -> misses), or the
               kill mis-credits, or the bar glitches -> CONTRADICTED: Knight Swords stay
               on PA per the LW-316 row's fallback.

  python tools\\probes\\lw316_lane_probes.py --selftest
      Offline math checks, no game needed.

Safety: every access goes through battle_cheats' guarded RPM/WPM (cross-process, an AV
cannot occur in our address space). Writes are single bytes/u16s on validated band entries,
originals saved first, restored on exit.
"""
import os
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import battle_cheats as bc

# Current faith: combat +0x2D -> band-relative 0x2D - 0x1C = 0x11 (see docstring provenance).
A_FAITH_CUR = 0x11
# Current brave sibling (combat +0x2B -> band +0x0F), read-only here, printed for context.
A_BRAVE_CUR = 0x0F
HP_CEILING = 999          # engine HP ceiling, owner-confirmed 2026-08-25 (LW-317 row)
FAITH_MIN, FAITH_MAX = 3, 97


def _side(slot: int) -> str:
    return "player" if slot >= bc.PLAYER_SLOT_THRESHOLD else "enemy "


def _valid_units():
    for s in range(bc.BAND_SLOTS):
        e = bc._band_entry_addr(s)
        if bc._is_valid_entry(e):
            yield s, e


def cmd_list() -> None:
    bc._require_game()
    print("slot  side    lvl  brOrig/brCur  faOrig/faCur   hp/maxhp   tile")
    n = 0
    for s, e in _valid_units():
        n += 1
        print(f"  {s:>2}  {_side(s)}  {bc.ru8(e + bc.A_LEVEL):>3}"
              f"  {bc.ru8(e + bc.A_BRAVE):>3}/{bc.ru8(e + A_BRAVE_CUR):<3}"
              f"      {bc.ru8(e + bc.A_FAITH):>3}/{bc.ru8(e + A_FAITH_CUR):<3}"
              f"     {bc.ru16(e + bc.A_HP):>4}/{bc.ru16(e + bc.A_MAXHP):<4}"
              f"  ({bc.ru8(e + bc.A_GX)},{bc.ru8(e + bc.A_GY)})")
    print(f"{n} valid units.")


def _checked_entry(slot: int) -> int:
    e = bc._band_entry_addr(slot)
    if not bc._is_valid_entry(e):
        sys.exit(f"slot {slot} is not a valid unit right now (run the list verb)")
    return e


def cmd_faith_hold(slot: int, value: int) -> None:
    bc._require_game()
    value = max(FAITH_MIN, min(FAITH_MAX, value))
    e = _checked_entry(slot)
    orig = bc.ru8(e + A_FAITH_CUR)
    orig_o = bc.ru8(e + bc.A_FAITH)
    print(f"slot {slot} ({_side(slot).strip()}): current faith {orig} (orig copy {orig_o})"
          f" -> holding {value} every 150ms.  Ctrl+C restores.")
    print("Owner: fire the magic gun at the same target before and after; read both numbers.")
    try:
        first = True
        while True:
            ok = bc.wu8(e + A_FAITH_CUR, value)
            if first:
                rb = bc.ru8(e + A_FAITH_CUR)
                print(f"  first write {'OK' if ok and rb == value else 'FAILED'} (read-back {rb})")
                first = False
            time.sleep(0.15)
    except KeyboardInterrupt:
        bc.wu8(e + A_FAITH_CUR, orig)
        rb = bc.ru8(e + A_FAITH_CUR)
        print(f"\nrestored current faith to {orig} (read-back {rb}).")


def _collision_report(slot: int, level: int, newmax: int) -> None:
    for s, e in _valid_units():
        if s == slot:
            continue
        if bc.ru8(e + bc.A_LEVEL) == level and bc.ru16(e + bc.A_MAXHP) == newmax:
            print(f"  WARNING: slot {s} ({_side(s).strip()}) already reads level {level}"
                  f" maxhp {newmax}: the (maxHp, hp, level) resolver key can collide"
                  f" -> ambiguous resolves. Pick a different newmax.")


def cmd_maxhp_hold(slot: int, newmax: int) -> None:
    bc._require_game()
    newmax = max(1, min(HP_CEILING, newmax))
    e = _checked_entry(slot)
    lvl = bc.ru8(e + bc.A_LEVEL)
    omax = bc.ru16(e + bc.A_MAXHP)
    ohp = bc.ru16(e + bc.A_HP)
    print(f"slot {slot} ({_side(slot).strip()}): level {lvl}, hp {ohp}/{omax}"
          f" -> holding MaxHP {newmax} every 50ms (current hp untouched).  Ctrl+C restores.")
    _collision_report(slot, lvl, newmax)
    print("Owner: watch (1) this unit's HP bar, (2) a kill by this unit crediting its weapon"
          " in livingweapon.log, (3) the mirror lines below on its turn.")
    last_line = None
    last_cond = None
    try:
        t0 = time.time()
        while True:
            bc.wu16(e + bc.A_MAXHP, newmax)
            if time.time() - t0 >= 0.5:
                t0 = time.time()
                hp = bc.ru16(e + bc.A_HP)
                mhp = bc.ru16(e + bc.A_MAXHP)
                line = f"  band: hp {hp}/{mhp}"
                if line != last_line:
                    print(line + ("   (unit reads hurt)" if hp is not None and mhp and hp < mhp else ""))
                    last_line = line
                c_lvl = bc.ru16(bc.CONDENSED_BASE + bc.TQ_LEVEL)
                c_hp = bc.ru16(bc.CONDENSED_BASE + bc.TQ_HP)
                c_max = bc.ru16(bc.CONDENSED_BASE + bc.TQ_MAXHP)
                if c_lvl == lvl and c_max in (omax, newmax, mhp):
                    verdict = "RAISED (mirror follows: resolver key consistent)" \
                        if c_max == newmax else "ORIGINAL (mirror stale: resolver mismatch)"
                    cond = f"  condensed mirror: lvl {c_lvl} hp {c_hp} maxhp {c_max} -> {verdict}"
                    if cond != last_cond:
                        print(cond)
                        last_cond = cond
            time.sleep(0.05)
    except KeyboardInterrupt:
        bc.wu16(e + bc.A_MAXHP, omax)
        hp = bc.ru16(e + bc.A_HP)
        if hp is not None and hp > omax:
            bc.wu16(e + bc.A_HP, omax)
        print(f"\nrestored MaxHP to {omax} (read-back {bc.ru16(e + bc.A_MAXHP)}),"
              f" hp clamped to {bc.ru16(e + bc.A_HP)}.")


def _selftest() -> bool:
    ok = True

    def check(name, cond):
        nonlocal ok
        print(f"  {'ok ' if cond else 'FAIL'} {name}")
        ok = ok and cond

    # Band-relative current-copy math against the combat-struct truth (0x1C band offset).
    check("current faith band offset = combat 0x2D - 0x1C", A_FAITH_CUR == 0x2D - 0x1C)
    check("current brave band offset = combat 0x2B - 0x1C", A_BRAVE_CUR == 0x2B - 0x1C)
    check("orig faith stays battle_cheats' 0x10", bc.A_FAITH == 0x10)
    # Address math matches the shared harness exactly (one row above the anchor slot).
    check("band addr math matches battle_cheats",
          bc._band_entry_addr(24) == bc.COMBAT_ANCHOR + bc.BAND_ENTRY)
    # Clamps.
    check("faith clamp low", max(FAITH_MIN, min(FAITH_MAX, 0)) == FAITH_MIN)
    check("faith clamp high", max(FAITH_MIN, min(FAITH_MAX, 200)) == FAITH_MAX)
    check("maxhp clamp = engine ceiling", max(1, min(HP_CEILING, 5000)) == 999)
    # Condensed struct constants present in the shared harness.
    check("condensed constants importable",
          bc.CONDENSED_BASE == 0x1407832A0 and bc.TQ_MAXHP == 0x10 and bc.TQ_HP == 0x0C)
    print("SELFTEST", "PASS" if ok else "FAIL")
    return ok


def main() -> None:
    args = sys.argv[1:]
    if not args or args[0] in ("-h", "--help"):
        print(__doc__)
        return
    if args[0] == "--selftest":
        sys.exit(0 if _selftest() else 1)
    if args[0] == "list":
        cmd_list()
        return
    if args[0] == "faith_hold" and len(args) == 3:
        cmd_faith_hold(int(args[1]), int(args[2]))
        return
    if args[0] == "maxhp_hold" and len(args) == 3:
        cmd_maxhp_hold(int(args[1]), int(args[2]))
        return
    print(__doc__)
    sys.exit(2)


if __name__ == "__main__":
    main()
