#!/usr/bin/env python
"""Snapshot the vanilla weapon table out of the game exe into data/vanilla_weapon_rows.json.

Why. data/items.json carries a `baseline` block per weapon, the numbers the unmodded game ships,
and analyze.py's --baseline pass judges fairness against them. Those notes drifted twice (LW-414,
LW-419) and the build server has no game file to catch a third drift. This file is the game's
own bytes, committed, so analyze.py can hold every baseline to them anywhere.

What. The resident ItemWeaponData table (Offsets.ItemStatsBase, 0x14080F690) read from the exe
file image through the PE section table, rows 1 to 121, the vanilla weapons. Row 0 is blank and
rows 122 to 127 are the mod's own consumables. Eight bytes per row: range, attack flags, formula,
a 0xFF pad, power, evasion, elements, on hit ability (the ITEM_WEAPON_DATA order generate.py
emits and ExtendedRecords.EncodeWeaponRow packs). The exe's TimeDateStamp and SizeOfImage ride
along as provenance; analyze.py holds the stamp to LaunchGuard's expected build.

Usage. python tools/snapshot_vanilla_weapon_rows.py [path\\to\\fft_enhanced.exe]
Rerun after a game patch, in the same commit as the Offsets.cs re-anchor (docs/PATCH_REANCHOR.md).
"""
import json
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
from lib import offsets
from lib.paths import ROOT, STEAM_FFT
from lib.pe_image import pe_identity, read_span, sections

OUT = ROOT / "data" / "vanilla_weapon_rows.json"
# Read from Offsets.cs so a re-anchor that moves the table moves this tool with it.
TABLE_VA = offsets.load()["ItemStatsBase"]
ROW_SIZE = 8
FIRST_ID, LAST_ID = 1, 121
LAYOUT = ["range", "attackFlags", "formula", "pad", "wp", "evade", "elements", "onHitAbilityId"]


def snapshot(data):
    """The snapshot document for one exe image, deterministic so a rerun diffs clean."""
    tds, soi = pe_identity(data)
    secs = sections(data)
    rows = {}
    for iid in range(FIRST_ID, LAST_ID + 1):
        row = read_span(data, secs, TABLE_VA + iid * ROW_SIZE, ROW_SIZE)
        if row is None or len(row) != ROW_SIZE:
            raise SystemExit(f"row {iid} is not file backed at 0x{TABLE_VA + iid * ROW_SIZE:X}")
        rows[str(iid)] = row.hex(" ")
    return {
        "_meta": {
            "source": f"fft_enhanced.exe file image, ItemWeaponData at VA 0x{TABLE_VA:X} (Offsets.ItemStatsBase), row index = item id",
            "exeTimeDateStamp": f"0x{tds:08X}",
            "exeSizeOfImage": f"0x{soi:08X}",
            "rowBytes": ROW_SIZE,
            "layout": LAYOUT,
            "tool": "python tools/snapshot_vanilla_weapon_rows.py",
        },
        "rows": rows,
    }


def main():
    exe = Path(sys.argv[1]) if len(sys.argv) > 1 else STEAM_FFT / "fft_enhanced.exe"
    doc = snapshot(exe.read_bytes())
    OUT.write_text(json.dumps(doc, indent=1) + "\n", encoding="utf-8")
    print(f"wrote {OUT} ({len(doc['rows'])} rows, stamp {doc['_meta']['exeTimeDateStamp']})")


if __name__ == "__main__":
    main()
