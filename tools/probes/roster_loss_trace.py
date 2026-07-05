#!/usr/bin/env python
"""
ROSTER LOSS TRACE -- the single test that unblocks Bait-n-Switch AND Divine Intervention.

Both features (keep-broken-gear, stop-crystallization -- see docs/research/NOT_LOSE_WEAPON.md) hinge on ONE
unknown: WHEN does the game commit a break/crystal loss to the PERSISTENT roster (0x1411A18D0) --
mid-battle, or only at the battle-end copy-back? This probe watches TWO structures so the answer is
unambiguous, and writes NOTHING:

  [BAND]   the IN-BATTLE unit copy (0x14184C8AC). A break/steal lands HERE first -- this is your
           live confirmation the event actually happened (weapon/shield are at a known offset;
           head/body armor may sit elsewhere -- if a body/helm break shows no [BAND] line, test
           with a WEAPON or SHIELD steal/break instead; the timing answer is identical).
  [ROSTER] the PERSISTENT world-map loadout. The loss reaches HERE at battle-end reconcile (or
           never). Its mode tag (OUT vs IN-BATTLE) is the verdict.

WHAT EACH OUTCOME MEANS (read off the [ROSTER] line's tag):
  - [ROSTER] loss line with tag OUT (after the BATTLE EXIT line) => commits at battle-end =>
    the safe snapshot-on-enter / restore-on-exit path WORKS. Ship both features.
  - [ROSTER] loss line with tag IN-BATTLE(n) => commits LIVE => Bait-n-Switch still fine (diff
    mid-battle), but Divine Intervention's safe path is DEAD (nothing left to restore on exit).
  - NO [ROSTER] line even after a full battle + return to the party menu, despite a [BAND] break =>
    IC may not PERMANENTLY destroy the gear at the roster level (feature could be moot) -- confirm
    by checking the unit's equipment at the formation screen after the fight.

USAGE (game running):
  python roster_loss_trace.py
      1. Start it BEFORE the battle (prints the baseline roster, then watches both structures).
      2. Enter a battle. Break/steal a piece -> watch for a [BAND] line (proof it landed).
      3. To test crystallize: let a NON-Ramza generic stay KO'd 3 of its turns (don't revive).
      4. FINISH the battle (win/lose/exit) and return toward the party menu -- watch for [ROSTER].
      5. Ctrl-C. Tell me what you broke/crystallized so a silent roster is interpretable.

READ-ONLY. Polls ~50ms.
"""
import pathlib
import struct
import sys
import time

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
from treasure_flags import rpm, _require_game

# --- persistent roster (world-map loadout) ---
ROSTER_BASE, ROSTER_STRIDE, ROSTER_SLOTS = 0x1411A18D0, 0x258, 20
RLEVEL, RNAME = 0x1D, 0x230
EQUIP_FIELDS = [
    (0x0E, "head"), (0x10, "body"), (0x12, "accessory"), (0x14, "Rhand"),
    (0x16, "Lhand"), (0x18, "reserved"), (0x1A, "shield"),
]
ARMOR_SLOTS = {"head", "body", "shield"}


def _broke(before, after):
    """A real item id became an empty/broken sentinel. LIVE capture: an emptied slot reads 0x00FF
    (NOT 0xFFFF -- the earlier 0xFFFF-only check silently missed every real break)."""
    return before not in (0x0000, 0x00FF, 0xFFFF) and after in (0x00FF, 0xFFFF)

# --- in-battle unit copy (the "band"); equip-id cluster sits at entry +0x04..+0x0C ---
BAND_BASE, BAND_STRIDE, BAND_SLOTS = 0x14184C8AC, 0x200, 49
BLVL, BBR, BFA = 0x0D, 0x0E, 0x10           # occupancy + identity
BEQ_LO, BEQ_HI = 0x04, 0x0C                  # weapon(+0x04) / Lhand(+0x06) / shield(+0x0a) cluster

BATTLE_MODE = 0x140900650
TICK = 0.05


def _u16(blob, o):
    return struct.unpack_from("<H", blob, o)[0]


def read_roster():
    blob = rpm(ROSTER_BASE, ROSTER_SLOTS * ROSTER_STRIDE)
    if blob is None:
        return None
    out = []
    for s in range(ROSTER_SLOTS):
        o = s * ROSTER_STRIDE
        out.append({"lvl": blob[o + RLEVEL], "name": _u16(blob, o + RNAME),
                    "eq": {nm: _u16(blob, o + off) for off, nm in EQUIP_FIELDS},
                    "raw": bytes(blob[o:o + ROSTER_STRIDE])})
    return out


def read_band():
    blob = rpm(BAND_BASE, BAND_SLOTS * BAND_STRIDE)
    if blob is None:
        return None
    out = {}
    for s in range(BAND_SLOTS):
        o = s * BAND_STRIDE
        lvl, br, fa = blob[o + BLVL], blob[o + BBR], blob[o + BFA]
        if not (1 <= lvl <= 99 and 1 <= br <= 100 and 1 <= fa <= 100):
            continue
        out[s] = {"br": br, "fa": fa, "equip": bytes(blob[o + BEQ_LO:o + BEQ_HI])}
    return out


def read_mode():
    b = rpm(BATTLE_MODE, 4)
    return struct.unpack("<I", b)[0] if b else -1


def mode_label(m):
    # battleMode PULSES 1/2/3/4/5 within a single battle (intra-battle phase flicker, NOT
    # transitions). Only the 0-boundary matters: 0 = out of battle, any nonzero = in a battle.
    return "OUT" if m == 0 else f"IN-BATTLE({m})"


def main():
    _require_game()
    prev_roster = read_roster()
    prev_band = read_band()
    if prev_roster is None:
        print("could not read the roster. Is the game running?")
        sys.exit(1)
    prev_mode = read_mode()

    print("=== baseline roster (populated player slots) ===")
    for s, u in enumerate(prev_roster):
        if u["lvl"] == 0:
            continue
        eq = " ".join(f"{nm}={u['eq'][nm]:#06x}" for _, nm in EQUIP_FIELDS)
        print(f"  s{s:>2} nameId={u['name']:>4} lvl={u['lvl']:>2}  {eq}")
    print(f"\nbattleMode={prev_mode} ({mode_label(prev_mode)}). Watching roster + in-battle band @ "
          f"{int(TICK*1000)}ms.")
    print("[BAND] = the break landed in-battle (proof). [ROSTER] = it reached the persistent loadout.")
    print("Break/steal a piece -> FINISH the battle -> return toward the party menu. Ctrl-C when done.\n")

    t0 = time.monotonic()
    try:
        while True:
            mode = read_mode()
            roster = read_roster()
            band = read_band()
            now = time.monotonic() - t0

            in_now, in_prev = (mode != 0), (prev_mode != 0)
            if in_now != in_prev:
                print(f"[{now:7.2f}s | {mode_label(mode):13}] "
                      f"*** BATTLE {'ENTER' if in_now else 'EXIT'} (mode {prev_mode}->{mode}) ***")
            prev_mode = mode

            # IN-BATTLE break confirmation
            if band is not None and prev_band is not None:
                for s, u in band.items():
                    p = prev_band.get(s)
                    if p is None or p["equip"] == u["equip"]:
                        continue
                    ch = ", ".join(f"+0x{BEQ_LO+i:02x} {p['equip'][i]:#04x}->{u['equip'][i]:#04x}"
                                   for i in range(len(u["equip"])) if p["equip"][i] != u["equip"][i])
                    print(f"[{now:7.2f}s | {mode_label(mode):13}] [BAND]   s{s} (br{u['br']}/fa{u['fa']}) "
                          f"equip {ch}")
            if band is not None:
                prev_band = band

            # PERSISTENT roster commit
            if roster is not None:
                for s in range(ROSTER_SLOTS):
                    a, b = prev_roster[s], roster[s]
                    diffs = []
                    if a["lvl"] != b["lvl"] or a["name"] != b["name"]:
                        diffs.append(f"level {a['lvl']}->{b['lvl']} nameId {a['name']}->{b['name']}")
                    for _, nm in EQUIP_FIELDS:
                        if a["eq"][nm] != b["eq"][nm]:
                            diffs.append(f"{nm} {a['eq'][nm]:#06x}->{b['eq'][nm]:#06x}")
                    if not diffs:
                        # Crystallization may zero a field we don't name. Out of battle (low churn),
                        # log ANY full-slot change so the removal marker can't hide from us.
                        if mode == 0 and a["raw"] != b["raw"]:
                            offs = [i for i in range(min(len(a["raw"]), len(b["raw"])))
                                    if a["raw"][i] != b["raw"][i]]
                            shown = ", ".join(f"+0x{i:03x}:{a['raw'][i]:#04x}->{b['raw'][i]:#04x}"
                                              for i in offs[:12])
                            print(f"[{now:7.2f}s | {mode_label(mode):13}] [ROSTER*] s{s} "
                                  f"(nameId={a['name']}): non-watched change ({len(offs)}B) {shown}")
                        continue
                    tag = ""
                    if b["lvl"] == 0 and a["lvl"] != 0:
                        tag = "  [SLOT CLEARED -> CRYSTALLIZED?]"
                    elif any(_broke(a["eq"][nm], b["eq"][nm]) for _, nm in EQUIP_FIELDS):
                        lost = [nm for _, nm in EQUIP_FIELDS if _broke(a["eq"][nm], b["eq"][nm])]
                        tag = f"  [{'ARMOR' if (set(lost) & ARMOR_SLOTS) else 'EQUIP'} LOST -> break/steal]"
                    print(f"[{now:7.2f}s | {mode_label(mode):13}] [ROSTER] s{s} (nameId={a['name']}): "
                          f"{'; '.join(diffs)}{tag}")
                prev_roster = roster
            time.sleep(TICK)
    except KeyboardInterrupt:
        print("\nstopped. Verdict reads off the [ROSTER] loss line's tag:")
        print("  OUT (after BATTLE EXIT) -> commits at battle-end -> safe restore works (ship both).")
        print("  IN-BATTLE(n)            -> commits live -> Divine Intervention's safe path is dead.")
        print("  no [ROSTER] line at all (despite a [BAND] break) -> IC may keep the gear; check the")
        print("  unit's equipment at the formation screen after the fight.")


if __name__ == "__main__":
    main()
