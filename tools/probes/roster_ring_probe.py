"""Diagnose RingGate: which roster slots hold the Scholar's Ring, and are those
ring-bearers actually DEPLOYED in the live battle band?

RingGate.ScholarRingEquipped currently scans the roster (all 20 party slots), so a
BENCHED ring-bearer opens the gate. The intended behavior is battle-only: the ring must
be on a unit deployed in the current battle. That requires cross-checking each ring-bearing
roster unit's (brave,faith) fingerprint against the live battle band.

This probe proves the premise: it dumps the band and reports whether each ring-bearing
roster unit appears in it. If a benched ring-bearer does NOT appear, a band-scan fix works.

Read-only. Run while a battle is loaded.
"""
import pathlib
import struct
import sys

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
from treasure_flags import rpm, ru8, _require_game

# Roster
ROSTER, RSTRIDE, RSLOTS = 0x1411A18D0, 0x258, 20
RACC, RLVL, RBR, RFA = 0x12, 0x1D, 0x1E, 0x1F
RING = 260

# Live battle band (static base, no ASLR): CombatAnchor 0x14184F890 + 0x1C - 24*0x200.
BAND, BSTRIDE, BSLOTS = 0x14184C8AC, 0x200, 49
ALVL, ABR, AFA, AMHP, AGX, AGY = 0x0D, 0x0E, 0x10, 0x16, 0x33, 0x34


def u16(addr):
    b = rpm(addr, 2)
    return struct.unpack("<H", b)[0] if b else None


def band_units():
    """(brave,faith)->(lvl,mhp,gx,gy) for every sane band entry."""
    out = {}
    for s in range(BSLOTS):
        a = BAND + s * BSTRIDE
        lvl, br, fa = ru8(a + ALVL), ru8(a + ABR), ru8(a + AFA)
        mhp, gx, gy = u16(a + AMHP), ru8(a + AGX), ru8(a + AGY)
        if None in (lvl, br, fa, mhp, gx, gy):
            continue
        if not (1 <= lvl <= 99 and 1 <= br <= 100 and 1 <= fa <= 100
                and 1 <= mhp < 2000 and gx <= 30 and gy <= 30):
            continue
        out.setdefault((br, fa), (lvl, mhp, gx, gy))
    return out


def main():
    _require_game()
    band = band_units()
    print(f"=== live band: {len(band)} sane unit(s) (brave,faith -> lvl,mhp,gx,gy) ===")
    for (br, fa), (lvl, mhp, gx, gy) in sorted(band.items()):
        print(f"  br={br} fa={fa}  lvl={lvl} mhp={mhp} at ({gx},{gy})")

    print("\n=== roster ring-bearers (acc==260) and whether they're DEPLOYED ===")
    any_bearer = False
    for s in range(RSLOTS):
        b = ROSTER + s * RSTRIDE
        if u16(b + RACC) != RING:
            continue
        any_bearer = True
        br, fa, lvl = ru8(b + RBR), ru8(b + RFA), ru8(b + RLVL)
        deployed = (br, fa) in band
        print(f"  roster slot {s}: br={br} fa={fa} rosterLvl={lvl}  "
              f"-> {'DEPLOYED (in band)' if deployed else 'BENCHED (not in band)'}")
    if not any_bearer:
        print("  (no roster slot has the ring)")
    print("\nIf the bearer shows BENCHED, the battle-only band-scan fix is valid.")


if __name__ == "__main__":
    main()
