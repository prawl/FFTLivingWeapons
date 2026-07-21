"""Roster span probe (LW-96): does the save-roster block extend past slot 19?

The DLL walks Offsets.RosterSlots = 20 rows at RosterBase, but IVC's party cap is 50
(owner screenshot 46/50, 2026-07-21). This probe reads slots 0..DUMP_SLOTS-1 at the
DLL's own constants (RosterBase 0x1411A7D10, stride 0x258 -- LW-93: trust Offsets.cs,
not the stale probe-lib filters) and prints each row's identity fields, so we can see
whether rows 20+ hold real units at the SAME stride/shape (-> the fix is a constant
bump) or garbage (-> a second bank exists somewhere else).

  python tools\probes\roster_span_probe.py            # dump slots 0..59
  python tools\probes\roster_span_probe.py --selftest # offline: row-address math only

A row is called OCCUPIED with the DLL's own rule (RLevel 1..99). Read-only probe.
"""
import struct
import sys

sys.path.insert(0, __import__("os").path.dirname(__file__))
from battle_cheats import _open_process, rpm  # noqa: E402

ROSTER_BASE = 0x1411A7D10   # Offsets.RosterBase (1.5 confirmed)
STRIDE      = 0x258         # Offsets.RosterStride
DUMP_SLOTS  = 60            # past the believed 50-cap on purpose: show where the block ends

R_SPRITE  = 0x00   # Offsets.RSprite  (u8: generics 0x80/0x81, story < 0x80, monsters >= 0x82)
R_RHAND   = 0x14   # Offsets.RRHand   (u16)
R_NAMEID  = 0x230  # Offsets.RNameId  (u16)
R_LEVEL   = 0x1D   # Offsets.RLevel (u8, 0 = empty slot)
R_BRAVE   = 0x1E   # Offsets.RBrave
R_FAITH   = 0x1F   # Offsets.RFaith


def row_addr(slot: int) -> int:
    return ROSTER_BASE + slot * STRIDE


def dump():
    _open_process()
    occupied = 0
    last_occupied = -1
    for s in range(DUMP_SLOTS):
        raw = rpm(row_addr(s), STRIDE)
        if raw is None:
            print(f"slot {s:2}: <unreadable>")
            continue
        sprite = raw[R_SPRITE]
        rhand = struct.unpack_from("<H", raw, R_RHAND)[0]
        nameid = struct.unpack_from("<H", raw, R_NAMEID)[0]
        lvl, brave, faith = raw[R_LEVEL], raw[R_BRAVE], raw[R_FAITH]
        occ = 1 <= lvl <= 99
        if occ:
            occupied += 1
            last_occupied = s
        tag = "OCC " if occ else "    "
        window = "" if s < 20 else "  <-- PAST the DLL's RosterSlots window"
        print(f"slot {s:2}: {tag}lvl={lvl:3} brave={brave:3} faith={faith:3} "
              f"sprite=0x{sprite:02X} rhand={rhand:5} nameId={nameid:5}{window}")
    print(f"\n{occupied} occupied rows, last at slot {last_occupied} "
          f"(DLL window ends at 19)")


def selftest():
    assert row_addr(0) == ROSTER_BASE
    assert row_addr(1) - row_addr(0) == STRIDE
    assert row_addr(19) == ROSTER_BASE + 19 * 0x258
    assert R_NAMEID < STRIDE and R_FAITH < STRIDE
    print("selftest OK")


if __name__ == "__main__":
    if "--selftest" in sys.argv:
        selftest()
    else:
        dump()
