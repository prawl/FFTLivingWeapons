#!/usr/bin/env python
"""LW-289: the weapon-to-palette map, read straight out of BATTLE.BIN. SOLVED, no census needed.

WHAT THIS ANSWERS. Battle weapon colour comes from the 512-byte palette block of FFTPack file 71
(LIVE_LEDGER [wep-spr-palette-block], PROVEN 2026-08-19). The open question was which of the
sixteen palettes each weapon draws from. It is not the ItemData <Palette> byte and it is not the
<SpriteID> byte; both were tested live and both are inert for battle art (three launches, twelve
battle loads, four items). It is a two-byte record per item in **BATTLE.BIN**, which the classic
FFT community documented long ago (FFHacktics, "Item Graphics"):

    offset(itemId) = 0x02D3E6 + (itemId - 1) * 2        itemId is 1-based, matching ItemData ids
    byte 0 high nibble  X  = palette index into battle_wep_spr.bin for the WEAPON itself
    byte 0 low  nibble  Y  = palette index into battle_wep_spr.bin for the swing arc / effect
    byte 1              ZZ = which graphic is drawn, interpreted RELATIVE to the item's category
                             (a knife with ZZ 00 is a different graphic from a sword with ZZ 00)

TWO INDEPENDENT CONFIRMATIONS THAT THIS IS THE REAL SELECTOR, both done before it was believed.

1. LIVE, four for four. With all sixteen palettes forged to distinct labelling colours and the
   serve proven from the loader log, the owner swung four swords and the measured palettes were:
       item 19 Broadsword     measured 14   battle_bin X = 14
       item 21 Iron Sword     measured  3   battle_bin X =  3
       item 22 Mythril Sword  measured 15   battle_bin X = 15
       item 26 Sleep Blade    measured 15   battle_bin X = 15
   All four carry Y = 0 and all four showed a palette-0 red slash arc in the same frames, so the
   effect nibble checks out too.

2. OFFLINE, a prediction the file could have failed. Independently of BATTLE.BIN, a connected
   component pass over the sheet found that palettes 1 and 2 hold only five non-zero colours
   each (slots 11-15) and can fully render just 49 of the 229 blobs, every one of them in the
   swing-arc and sparkle rows, so NO weapon tile on the sheet can be drawn with palette 1 or 2.
   BATTLE.BIN agrees exactly: across all 127 weapons, palettes 1 and 2 appear as an effect
   palette Y twenty times and as a weapon palette X **zero** times.

The remaster did not move any of this. Fifteen spot offsets checked against the published PSX
table match byte for byte, which is the gate this script runs every time before it reads.

THE WRITE LEVER. battle_bin.bin is FFTPack file 0 and the loader serves it through the same
channel as the sprite sheet (it is not on the hook's excluded list, which is only file 17 and
741-749). The game requests it once per battle load, at offset 0. So a mod can ship its own copy
and ASSIGN a palette to any weapon, rather than merely discovering the one it has. That is what
makes a per-weapon icon-matched recolour possible at all.

USAGE:
  python lw289_battle_bin_palette_map.py --selftest        # pure checks, no game files
  python lw289_battle_bin_palette_map.py <work_dir>        # extract, gate, print the full map
  python lw289_battle_bin_palette_map.py <work_dir> --json out.json    # ...and write it as JSON
"""
import json
import os
import re
import subprocess
import sys
import xml.etree.ElementTree as ET
from collections import Counter, defaultdict

GAME = (r"C:\Program Files (x86)\Steam\steamapps\common"
        r"\FINAL FANTASY TACTICS - The Ivalice Chronicles")
# battle_bin lives in the CLASSIC tree, locale-suffixed, even though the game runs Enhanced.
# It is NOT in any data/enhanced pac; that is why a search there comes up empty.
SOURCE_PAC = os.path.join(GAME, "data", "classic", "0002.en.pac")
INNER = "fftpack/battle_bin.en.bin"
FF16TOOLS = (r"C:\Users\ptyRa\Downloads\FF16Tools.CLI-1.13.2-win-x64"
             r"\win-x64\FF16Tools.CLI.exe")
FFTPACK_ID = 0                 # fftpack.txt line 1: 0|battle_bin.bin
ITEM_GFX_BASE = 0x02D3E6       # FFHacktics: first weapon record (item id 1, Dagger)
FIRST_ITEM, LAST_ITEM = 1, 127  # the weapon block; 128+ are consumables and gear

# Fifteen records from the published PSX table, spread across every weapon family, used as the
# format gate. If the remaster ever moves this block these stop matching and the script refuses
# rather than silently reading garbage.
ANCHORS = {
    0x01: (0xE, 0, 0x00), 0x02: (0xF, 0, 0x02), 0x03: (0xE, 0, 0x04),
    0x04: (0x3, 0, 0x06), 0x05: (0xD, 0, 0x00), 0x13: (0xE, 0, 0x00),
    0x14: (0xE, 0, 0x02), 0x15: (0x3, 0, 0x04), 0x16: (0xF, 0, 0x06),
    0x17: (0x6, 0, 0x04), 0x1A: (0xF, 0, 0x00), 0x21: (0x8, 0, 0x0C),
    0x26: (0xD, 0, 0x10), 0x31: (0xB, 0, 0x00), 0x33: (0xE, 0, 0x16),
}
# Owner-captured, measured numerically off daylight screenshots with the serve proven from the
# loader log. These are the LIVE half of the proof and the script asserts the file agrees.
LIVE_MEASURED = {19: 14, 21: 3, 22: 15, 26: 15}


def record_offset(item_id):
    return ITEM_GFX_BASE + (item_id - 1) * 2


def extract(work_dir):
    out = os.path.join(work_dir, "pac_unpack")
    path = os.path.join(out, *INNER.split("/"))
    if not os.path.isfile(path):
        subprocess.run([FF16TOOLS, "unpack", "-i", SOURCE_PAC, "-f", INNER, "-o", out,
                        "-g", "fft"], capture_output=True)
    if not os.path.isfile(path):
        sys.exit(f"unpack produced no {path}")
    return open(path, "rb").read()


def gate(raw):
    """Refuse to read the map unless the published anchors still match, so a patched or
    re-laid-out BATTLE.BIN fails loudly instead of producing a plausible wrong table."""
    bad = []
    for iid, want in sorted(ANCHORS.items()):
        off = record_offset(iid)
        if off + 1 >= len(raw):
            sys.exit(f"battle_bin is only {len(raw)} bytes, too short for record 0x{iid:02X}")
        b0, b1 = raw[off], raw[off + 1]
        if (b0 >> 4, b0 & 0xF, b1) != want:
            bad.append((iid, (b0 >> 4, b0 & 0xF, b1), want))
    if bad:
        for iid, got, want in bad:
            print(f"  anchor 0x{iid:02X}: got {got}, expected {want}")
        sys.exit(f"{len(bad)}/{len(ANCHORS)} anchors differ; the item-graphics block moved. STOP "
                 f"and re-find it before trusting any map.")
    print(f"anchor gate: {len(ANCHORS)}/{len(ANCHORS)} published PSX records match byte for byte")


def item_names():
    """Vanilla names and categories from the modloader's own template dump."""
    mods = os.environ.get("RELOADEDIIMODS")
    if not mods:
        return {}
    p = os.path.join(mods, "FFTIVC_Mod_Loader", "TableData", "ItemData.xml")
    if not os.path.isfile(p):
        return {}
    raw = open(p, encoding="utf-8").read()
    names = {int(m.group(1)): m.group(2)
             for m in re.finditer(r"<Id>(\d+)</Id>\s*<!--\s*([^/]+?)\s*/", raw)}
    out = {}
    for n in ET.fromstring(raw).iter("Item"):
        g = lambda t, d="": (n.find(t).text.strip()
                             if n.find(t) is not None and n.find(t).text else d)
        i = int(g("Id"))
        out[i] = (names.get(i, "?"), g("ItemCategory"), "Weapon" in g("TypeFlags"))
    return out


def build_map(raw):
    meta = item_names()
    rows = []
    for iid in range(FIRST_ITEM, LAST_ITEM + 1):
        name, cat, is_weapon = meta.get(iid, ("?", "?", True))
        if not is_weapon:
            continue
        off = record_offset(iid)
        b0, b1 = raw[off], raw[off + 1]
        rows.append({"id": iid, "name": name, "category": cat, "offset": off,
                     "weaponPalette": b0 >> 4, "effectPalette": b0 & 0xF, "graphic": b1})
    return rows


def report(rows):
    for iid, want in sorted(LIVE_MEASURED.items()):
        got = next(r["weaponPalette"] for r in rows if r["id"] == iid)
        assert got == want, f"item {iid}: file says palette {got}, the game showed {want}"
    print(f"live cross-check: {len(LIVE_MEASURED)}/{len(LIVE_MEASURED)} owner-measured palettes "
          f"match the file")
    print()
    xs = Counter(r["weaponPalette"] for r in rows)
    ys = Counter(r["effectPalette"] for r in rows)
    print(f"{len(rows)} weapons")
    print("  weapon palettes in use:", ", ".join(f"{k}({v})" for k, v in sorted(xs.items())))
    print("  effect palettes in use:", ", ".join(f"{k}({v})" for k, v in sorted(ys.items())))
    overlap = set(xs) & set(ys)
    print(f"  palettes used BOTH as a weapon and as an effect: "
          f"{sorted(overlap) if overlap else 'NONE, the two sets are cleanly disjoint'}")
    print(f"  free palettes, used by neither: "
          f"{sorted(set(range(16)) - set(xs) - set(ys)) or 'none'}")
    print()
    by_pal = defaultdict(list)
    for r in rows:
        by_pal[r["weaponPalette"]].append(r)
    for pal in sorted(by_pal):
        group = by_pal[pal]
        cats = ", ".join(f"{c}x{n}" for c, n in
                         Counter(r["category"] for r in group).most_common())
        print(f"  palette {pal:2}: {len(group):2} weapons  [{cats}]")
        print(f"              {', '.join(r['name'] for r in group)}")


def selftest():
    assert record_offset(1) == 0x02D3E6
    assert record_offset(0x13) == 0x02D40A, "offset formula disagrees with the published table"
    assert record_offset(0x33) == 0x02D44A
    # A record is two bytes, so consecutive ids must be two apart and never overlap.
    offs = [record_offset(i) for i in range(FIRST_ITEM, LAST_ITEM + 1)]
    assert all(b - a == 2 for a, b in zip(offs, offs[1:])), "records are not a flat 2-byte array"
    assert len(set(offs)) == len(offs), "two items share a record offset"
    for iid, (x, y, zz) in ANCHORS.items():
        assert 0 <= x <= 15 and 0 <= y <= 15 and 0 <= zz <= 255, f"anchor 0x{iid:02X} out of range"
    # The anchors must actually exercise the nibble split, otherwise the gate is vacuous: a table
    # of all-zero-Y records would pass even if the halves were swapped.
    assert len({x for x, _, _ in ANCHORS.values()}) >= 5, "anchors do not span enough X values"
    assert any(zz != 0 for _, _, zz in ANCHORS.values()), "no anchor exercises the ZZ byte"
    packed = {iid: (x << 4) | y for iid, (x, y, _) in ANCHORS.items()}
    for iid, b0 in packed.items():
        assert (b0 >> 4, b0 & 0xF) == ANCHORS[iid][:2], "nibble pack/unpack is not a round trip"
    print("selftest OK")


def main():
    if "--selftest" in sys.argv:
        selftest()
        return
    selftest()
    if len(sys.argv) < 2 or sys.argv[1].startswith("--"):
        sys.exit(__doc__.split("USAGE:")[1])
    work_dir = sys.argv[1]
    os.makedirs(work_dir, exist_ok=True)
    raw = extract(work_dir)
    print(f"battle_bin: {len(raw)} bytes (0x{len(raw):X})")
    gate(raw)
    rows = build_map(raw)
    report(rows)
    if "--json" in sys.argv:
        dst = sys.argv[sys.argv.index("--json") + 1]
        json.dump(rows, open(dst, "w", encoding="utf-8"), indent=1)
        print(f"\nwrote {dst}")


if __name__ == "__main__":
    main()
