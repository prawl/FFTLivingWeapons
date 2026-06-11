#!/usr/bin/env python
"""
Cripple probe -- does CLEARING a unit's passive bitfields actually suppress them
at trigger time? (TODO 'HIGH CONFIDENCE: Suppress the enemies Reactions';
HANDOFF open thread 5 'Silencer'.)

Band-copy passive fields (HINTS in poison_probe, grant-tech research):
  reaction +0x74..0x77 (4B, ability id 166 + bitpos, MSB-first)
  support  +0x78..0x7B (4B, id 198 + bitpos)
  movement +0x7D..     (base id TBD -- dump first, zero last)

ORACLE: pick an enemy with a known reaction (goblin Counter is ideal).
  python ct_probe.py dump                       # grab its mhp/lvl
  python cripple_probe.py show <mhp> <lvl>      # dump +0x70..0x80 on BOTH copies,
                                                # decode set reaction/support ids
  python cripple_probe.py zero <mhp> <lvl> r [seconds=120]
        # hold the reaction field to ZERO (30ms cadence) on the AUTH band copy.
        # NOW HIT THE UNIT with a melee attack:
        #   no Counter fires        -> suppression WORKS (ship Cripple)
        #   Counter still fires     -> engine reads a build-time copy (dead end,
        #                              or the live source is another struct)
        # re-asserts printed = engine rewriting the field (normalize source).
        # Baseline bytes are RESTORED on exit (Ctrl+C safe).
  python cripple_probe.py zero <mhp> <lvl> s|m|all [seconds]   # same for the rest
DON'T restart the battle mid-probe. RPM/WPM only -- cannot crash the game.
"""
import os
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from ct_probe import (PROC, PV_W, find_pid, k32, rd, u16, wr)
from poison_probe import locate_blocking, band_ok

# Offsets.cs ground truth: passives live on the COMBAT struct (reaction +0x94 4B base 166,
# support +0x98 4B base 198, movement +0x9C 3B base 230, MSB-first). The band entry that
# locate_blocking returns = combat base + 0x1C, hence the -0x1C rebase here.
SPANS = {"r": (0x94 - 0x1C, 4, 166, "reaction"),
         "s": (0x98 - 0x1C, 4, 198, "support"),
         "m": (0x9C - 0x1C, 3, 230, "movement")}


def decode(field, base_id, n):
    """MSB-first bitfield -> ability ids (mirrors Signatures.ResolveSupport)."""
    ids = []
    if base_id is None:
        return ids
    for byte_off in range(n):
        for bit in range(8):
            if field[byte_off] & (0x80 >> bit):
                ids.append(base_id + byte_off * 8 + bit)
    return ids


def cmd_show(h, mhp, lvl):
    u = locate_blocking(h, mhp, lvl)
    print(f"fp={u['key']}  static@{u['static']:012X}  band@{u['band']:012X}")
    print(" " * 10 + " ".join(f"{o:02X}" for o in range(0x70, 0x88)))
    for name, base in (("static", u["static"]), ("band", u["band"])):
        b = rd(h, base + 0x70, 0x18)
        hx = " ".join(f"{x:02X}" for x in b) if b else "??"
        print(f"{name:8}  {hx}")
        if b:
            for key, (off, n, base_id, label) in SPANS.items():
                ids = decode(b[off - 0x70:off - 0x70 + n], base_id, n)
                if ids:
                    print(f"          {label}: set ids = {ids}")


def cmd_zero(h, mhp, lvl, which, seconds):
    spans = list(SPANS.values()) if which == "all" else [SPANS[which]]
    u = locate_blocking(h, mhp, lvl)
    baseline = {}
    for off, n, _, label in spans:
        b = rd(h, u["band"] + off, n)
        if b is None:
            print(f"can't read +{off:#x}; aborting")
            return
        baseline[off] = (bytes(b), n, label)
        print(f"baseline {label} +0x{off:02X}: {' '.join(f'{x:02X}' for x in b)}")
    if all(not any(b) for b, _, _ in baseline.values()):
        print("WARNING: all chosen fields are already zero -- pick a unit with a "
              "known reaction (show first), or the test proves nothing.")
    print(f"\nHOLDING ZERO for {seconds:.0f}s. HIT THE UNIT NOW -- does its "
          f"reaction still fire?  (Ctrl+C restores + exits)\n")
    reasserts = 0
    t0 = time.time()
    last_s = -1
    try:
        while time.time() - t0 < seconds:
            if not band_ok(h, u["band"], mhp, lvl):
                u = locate_blocking(h, mhp, lvl)
            for off, (orig, n, label) in baseline.items():
                cur = rd(h, u["band"] + off, n)
                if cur is None:
                    continue
                if any(cur):
                    wr(h, u["band"] + off, b"\x00" * n)
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
        for off, (orig, n, label) in baseline.items():
            wr(h, u["band"] + off, orig)
            print(f"restored {label} +0x{off:02X}")
    print(f"done. engine re-asserted nonzero {reasserts}x "
          f"(0 = field is ours to own; many = a normalize source exists, "
          f"hold-zero still wins if the reaction stayed quiet).")


def main():
    a = sys.argv
    mode = a[1] if len(a) > 1 else ""
    if mode not in ("show", "zero"):
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
            which = a[4] if len(a) > 4 else "r"
            secs = float(a[5]) if len(a) > 5 else 120
            cmd_zero(h, int(a[2]), int(a[3]), which, secs)
    finally:
        k32.CloseHandle(h)


if __name__ == "__main__":
    main()
