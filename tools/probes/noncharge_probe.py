#!/usr/bin/env python
"""
Non-charge probe -- does GRANTING a unit the Non-charge support live actually make its
magick cast instantly, or is it build-time-only like Doublehand? (4th-staff "instant cast"
feasibility -- docs/TODO.md + HANDOFF "instant spell casting for X turns".)

THE QUESTION. IC ships the ability natively (ability.en Key 483 "Non-charge" -- "Removes the
time needed to cast magick spells."; Key 482 "Swiftspell" is the half-charge version). We do
NOT touch the cursed overrideabilityactiondata.nxd; we grant the EXISTING support by OR-setting
its bit in the live combat-struct support bitfield -- the same primitive Maim/Cripple proved.
The ONLY unknown is whether the engine honors a LIVE support grant when it computes charge time:
the live ledger (2026-06-08) shows calculation-gated supports take effect (Concentration) but
HP/MP Boost + Doublehand/Dual Wield are build-time-only (read back fine, zero effect). Charge
time is resolved when you QUEUE a spell mid-battle, so Non-charge has a real shot at being
calc-gated -- but it must be SEEN, not assumed.

OFFSETS (Offsets.cs ground truth). Passive bitfields live on the COMBAT struct, MSB-first:
  reaction +0x94 (4B, base id 166), support +0x98 (4B, base 198), movement +0x9C (3B, base 230).
The band entry locate_blocking returns = combat base + 0x1C, hence the -0x1C rebase. So:
  support field        = band +0x7C, 4 bytes
  Non-charge support id = 483 - 256 = 227  ->  227-198 = bit 29  ->  byte 3, mask 0x80>>5 = 0x04
  Swiftspell  support id = 482 - 256 = 226  ->  bit 28  ->  byte 3, mask 0x80>>4 = 0x08

ORACLE (game running, in a LIVE battle):
  Pick a PLAYER caster you control that has a charge-time spell (any Black/White Mage -- Fire,
  Cure, etc.). It must NOT already have Non-charge equipped (the test would prove nothing).
    python ct_probe.py dump                        # grab the caster's mhp + lvl
    python noncharge_probe.py show <mhp> <lvl>     # dump +0x70..0x88; decode its set supports
    python noncharge_probe.py grant <mhp> <lvl> [noncharge|swift] [seconds=180]
        # OR-sets the bit on the AUTH band copy and HOLDS it (30ms cadence). THEN:
        #   take that unit's turn and cast a CHARGE spell (Fire / Cure / etc).
        #     fires THIS turn, no charging state   -> Non-charge LIVE-HONORED (build the staff!)
        #     unit enters the usual charging/CT delay -> build-time-only (dead end; needs the
        #                                                per-action CT route instead)
        # Judge by CAST BEHAVIOR, not the menu -- the menu reads a separate loadout list and may
        # not show the bit-granted ability. re-asserts printed = engine rewriting the field.
        # The unit's ORIGINAL bit state is RESTORED on exit (Ctrl+C safe); other bits untouched.

DON'T restart the battle mid-probe. RPM/WPM only -- cannot crash the game.
"""
import os
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from ct_probe import PROC, PV_W, find_pid, k32, rd, u16, wr
from poison_probe import locate_blocking, band_ok

# band-relative (combat offset - BandEntry 0x1C). MSB-first bitfields.
SPANS = {"r": (0x94 - 0x1C, 4, 166, "reaction"),
         "s": (0x98 - 0x1C, 4, 198, "support"),
         "m": (0x9C - 0x1C, 3, 230, "movement")}
SUP_OFF, SUP_N, SUP_BASE, _ = SPANS["s"]   # 0x7C, 4, 198

# ability.en Key - 256 = live RSM id; both land in the support range 198..229.
GRANTS = {"noncharge": 227, "swift": 226}


def bit_for(ability_id):
    """(byte_off, mask) of an id within a band MSB-first bitfield based at SUP_BASE."""
    off = ability_id - SUP_BASE
    return off // 8, 0x80 >> (off % 8)


def decode(field, base_id, n):
    """MSB-first bitfield -> ability ids (mirrors Signatures.ResolveSupport / cripple_probe)."""
    ids = []
    for byte_off in range(n):
        for bit in range(8):
            if field[byte_off] & (0x80 >> bit):
                ids.append(base_id + byte_off * 8 + bit)
    return ids


def cmd_show(h, mhp, lvl):
    u = locate_blocking(h, mhp, lvl)
    print(f"fp={u['key']}  static@{u['static']:012X}  band@{u['band']:012X}")
    print(" " * 10 + " ".join(f"{o:02X}" for o in range(0x70, 0x88)))
    band_support = []
    for name, base in (("static", u["static"]), ("band", u["band"])):
        b = rd(h, base + 0x70, 0x18)
        hx = " ".join(f"{x:02X}" for x in b) if b else "??"
        print(f"{name:8}  {hx}")
        if b:
            for off, n, base_id, label in SPANS.values():
                ids = decode(b[off - 0x70:off - 0x70 + n], base_id, n)
                if ids:
                    print(f"          {label}: set ids = {ids}")
                if name == "band" and label == "support":
                    band_support = ids
    bo, mask = bit_for(GRANTS["noncharge"])
    nc = GRANTS["noncharge"]
    print(f"\nNon-charge (id {nc}) -> support byte {bo} mask {mask:#04x} (band +{SUP_OFF + bo:#x}).")
    if nc in band_support:
        print(f"  -> {nc} is ALREADY equipped here: pick another caster (the grant would prove nothing).")
    else:
        print(f"  -> {nc} NOT equipped: good target. `grant` will add it.")


def cmd_grant(h, mhp, lvl, which, seconds):
    ability_id = GRANTS[which]
    byte_off, mask = bit_for(ability_id)
    u = locate_blocking(h, mhp, lvl)
    base = rd(h, u["band"] + SUP_OFF, SUP_N)
    if base is None:
        print("can't read the support field; aborting")
        return
    orig_set = bool(base[byte_off] & mask)
    print(f"target fp={u['key']}  band@{u['band']:012X}")
    print(f"support +{SUP_OFF:#x}: {' '.join(f'{x:02X}' for x in base)}  "
          f"(set ids = {decode(base, SUP_BASE, SUP_N)})")
    if orig_set:
        print(f"WARNING: this unit ALREADY has {which} (id {ability_id}). The test proves "
              f"nothing -- pick a caster WITHOUT it (run `show` first).")
    print(f"\nGRANTING {which} (id {ability_id}: support byte {byte_off} mask {mask:#04x}) and "
          f"HOLDING for {seconds:.0f}s.\nNOW take this unit's turn and CAST A CHARGE SPELL "
          f"(Fire / Cure / ...). Instant = Non-charge is honored.  (Ctrl+C restores + exits)\n")
    reasserts = 0
    t0 = time.time()
    last_s = -1
    try:
        while time.time() - t0 < seconds:
            if not band_ok(h, u["band"], mhp, lvl):
                u = locate_blocking(h, mhp, lvl)
            cur = rd(h, u["band"] + SUP_OFF + byte_off, 1)
            if cur is not None and not (cur[0] & mask):
                wr(h, u["band"] + SUP_OFF + byte_off, bytes([cur[0] | mask]))
                reasserts += 1
            s = int(time.time() - t0)
            if s != last_s:
                last_s = s
                hpb = rd(h, u["band"] + 0x14, 2)
                hp = u16(hpb, 0) if hpb else -1
                print(f"  t={s:>3}s  hp={hp:>4}  engine-reasserts={reasserts}")
            time.sleep(0.03)
    except KeyboardInterrupt:
        print("(interrupted)")
    finally:
        if not orig_set:                       # restore ONLY our bit; leave engine changes alone
            cur = rd(h, u["band"] + SUP_OFF + byte_off, 1)
            if cur is not None and (cur[0] & mask):
                wr(h, u["band"] + SUP_OFF + byte_off, bytes([cur[0] & ~mask]))
            print(f"restored support byte {byte_off} (cleared id {ability_id})")
        else:
            print(f"left id {ability_id} set (it was already on this unit)")
    print(f"done. engine re-asserted {reasserts}x "
          f"(0 = the bit is ours to hold; many = a normalize source rewrites it, "
          f"the hold still wins as long as the spell cast instantly).")


def main():
    a = sys.argv
    mode = a[1] if len(a) > 1 else ""
    if mode not in ("show", "grant"):
        print(__doc__)
        return
    pid = find_pid(PROC)
    if not pid:
        print(f"{PROC} not running")
        return
    h = k32.OpenProcess(PV_W, False, pid)
    if not h:
        print("OpenProcess failed")
        return
    try:
        if mode == "show":
            cmd_show(h, int(a[2]), int(a[3]))
        else:
            which = a[4] if len(a) > 4 and a[4] in GRANTS else "noncharge"
            secs = float(a[5]) if len(a) > 5 else 180
            cmd_grant(h, int(a[2]), int(a[3]), which, secs)
    finally:
        k32.CloseHandle(h)


if __name__ == "__main__":
    main()
