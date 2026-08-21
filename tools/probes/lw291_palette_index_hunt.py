#!/usr/bin/env python
"""
LW-291 PROBE: is the weapon's PALETTE INDEX resolved per-unit into the combat struct?

WHY THIS AND NOT A DRAW HOOK. The ledger row [weapon-palette-assignment-walled] closed four data
channels: ItemData <Palette>, ItemData <SpriteID>, a mod-shipped battle_bin.bin, and a direct write
to the resident copy of the classic item-graphics table. Its conclusion was that the assignment is
"resolved once at startup into a render-side structure nobody has found", and that reopening needs
a hook on the weapon draw path.

But "render-side structure" was an inference, not a sighting. There is a cheaper candidate nobody
checked: if the game resolves weapon -> palette when it builds a unit for battle, the resolved
index plausibly lives in that unit's own combat struct, which this repo already maps in detail.
If so, per-weapon battle colour needs no hook at all, only a guarded byte write per unit, which is
the same shape as every other signature this mod already ships.

THE METHOD is a multi-unit simultaneous fingerprint, which is what makes it cheap and hard to fool.
For every unit on the field we know its equipped weapon (combat struct) and therefore its EXPECTED
palette (tools/probes/lw289_weapon_palette_map.json). We scan each unit's struct for byte offsets
holding that unit's own expected value. A single unit yields dozens of coincidences; requiring the
SAME offset to be correct for every unit at once, where the units want different palettes, collapses
that to near zero by chance. The probe reports how many distinct expected values were in play,
because an "all units agree" hit is worthless if every unit wanted the same number.

READ-ONLY. It reports candidate offsets; it does not write. Confirming a candidate means writing it
live and looking at the swung weapon, which is a separate deliberate step.
"""
import collections
import json
import pathlib
import struct
import sys

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
from battle_cheats import rpm, ru8, _require_game

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parents[1]))
from lib import offsets as _offsets

BATTLE_MODE, SLOT9, _CA, _CS, _BE = _offsets.require(
    ["BattleMode", "Slot9", "CombatAnchor", "CombatStride", "BandEntry"])
BAND = _CA + _BE - 24 * _CS
BSTRIDE, BSLOTS = _CS, 49
AWEAPON = 0x04          # band-relative equipped weapon id (== CWeapon - BandEntry)
ALVL, ABR, AFA = 0x0D, 0x0E, 0x10
AHP, AMHP = 0x14, 0x16
SCAN_LO, SCAN_HI = -0x40, 0x200   # combat-base-relative window to search


def u16(a):
    b = rpm(a, 2)
    return struct.unpack("<H", b)[0] if b else None


def gate():
    _require_game()
    s9 = rpm(SLOT9, 4)
    s9 = struct.unpack("<I", s9)[0] if s9 else 0
    bm = rpm(BATTLE_MODE, 4)
    bm = struct.unpack("<I", bm)[0] if bm else 0
    if s9 != 0xFFFFFFFF or bm == 0:
        print(f"need a live battle (battleMode={bm}, slot9={s9:#x}).")
        sys.exit(1)


def units():
    out = []
    for s in range(BSLOTS):
        a = BAND + s * BSTRIDE
        lvl, br, fa = ru8(a + ALVL), ru8(a + ABR), ru8(a + AFA)
        mhp = u16(a + AMHP)
        if None in (lvl, br, fa, mhp):
            continue
        if not (1 <= lvl <= 99 and 1 <= br <= 100 and 1 <= fa <= 100 and 1 <= mhp < 2000):
            continue
        wep = ru8(a + AWEAPON)
        out.append({"slot": s, "base": a - _BE, "wep": wep, "lvl": lvl, "br": br, "fa": fa})
    return out


def main():
    gate()
    pmap = {w["id"]: w for w in json.load(
        open(pathlib.Path(__file__).resolve().parent / "lw289_weapon_palette_map.json"))}
    us = units()
    live = []
    for u in us:
        w = pmap.get(u["wep"])
        if w:
            u["pal"], u["gfx"], u["wname"] = w["weaponPalette"], w["graphic"], w["name"]
            live.append(u)
    print(f"=== {len(us)} band unit(s), {len(live)} carrying a mapped weapon ===")
    for u in live:
        print(f"  s{u['slot']:>2} wep {u['wep']:>3} {u['wname']:22s} -> palette {u['pal']:>2} "
              f"(art {u['gfx']})")
    if len(live) < 2:
        print("\nNeed at least 2 units with mapped weapons. Equip more units and re-run.")
        return
    vals = collections.Counter(u["pal"] for u in live)
    print(f"\ndistinct expected palettes on the field: {len(vals)} {dict(vals)}")
    if len(vals) < 2:
        print("WARNING: every unit wants the SAME palette, so any 'all agree' hit below is")
        print("meaningless. Equip weapons from different palettes and re-run.")

    # An offset survives only if it holds each unit's OWN expected palette.
    hits = []
    for off in range(SCAN_LO, SCAN_HI):
        ok = True
        for u in live:
            v = ru8(u["base"] + off)
            if v is None or v != u["pal"]:
                ok = False
                break
        if ok:
            hits.append(off)
    print(f"\noffsets holding every unit's OWN expected palette: {len(hits)}")
    for off in hits:
        vs = ", ".join(f"s{u['slot']}={ru8(u['base'] + off)}" for u in live)
        print(f"  combat +0x{off & 0xFF:02x} (raw {off:+#x}): {vs}")
    if not hits:
        print("  none. The resolved index is not a plain byte in this window of the combat struct.")
        print("  Next cheapest: widen SCAN_HI, try the u16 lane, or diff the same unit across two")
        print("  battles with different weapons equipped.")


main()
