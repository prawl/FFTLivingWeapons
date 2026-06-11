#!/usr/bin/env python
# RETIRED (2026-06-10): the ids 246-250 renames are folded into patch_names.py's EXTRA_NAMES,
# which re-asserts them on every rename run. This script UPDATEd the working sqlite in place,
# so the renames survived only because the mutated cache persisted -- a re-decode would have
# silently dropped them. Kept in tools/oneoff/ for provenance; running it is harmless but
# redundant.
"""
Rename + re-describe the 5 recycled cure consumables into offensive status grenades, then
re-encode item.en.nxd. Pairs with the offensive ItemConsumableData.xml override.

ItemConsumable rows 6-10 map to item ids 246-250 (Antidote / Eye Drops / Echo Herbs /
Maiden's Kiss / Gold Needle) -- the cures Remedy already covers, so curing capability is
unchanged. We repoint their behavior (in ItemConsumableData.xml) to inflict a status, and
rename the items here so the menu matches.
"""
import sqlite3
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from lib.nxd import encode_sqlite_to_nxd, deploy_nxd
from lib.paths import ROOT, MOD_ITEM_NXD

SQLITE = ROOT / "working" / "pilot_item.sqlite"
ENC_DIR = ROOT / "working" / "nxd_out"

# item id -> (name, description). Recycled from the 5 Remedy-covered single-cures.
GRENADES = {
    246: ("Venom Flask",     "Distilled toxin in a fragile vial. Inflicts Poison on the target."),
    247: ("Smoke Bomb",      "A burst of acrid smoke. Inflicts Blind on the target."),
    248: ("Hush Vial",       "A throat-stilling draught in a fragile vial. Inflicts Silence on the target."),
    249: ("Oil Flask",       "Clinging oil. Inflicts Oil on the target, doubling the Fire damage they take."),
    250: ("Sludge Bomb",     "A splash of clinging mire. Inflicts Slow on the target."),
}


def plural(name):
    low = name.lower()
    return low + ("es" if low.endswith(("s", "x", "z", "ch", "sh")) else "s")


def main():
    con = sqlite3.connect(SQLITE)
    for iid, (name, desc) in GRENADES.items():
        old = con.execute('SELECT Name FROM "Item-en" WHERE Key=?', (iid,)).fetchone()
        print(f"  {iid}: {old[0] if old else '???'!r} -> {name!r}")
        con.execute('UPDATE "Item-en" SET Name=?, NameSingular=?, NamePlural=?, Name2=?, Description=? WHERE Key=?',
                    (name, name.lower(), plural(name), name, desc, iid))
    con.commit(); con.close()
    out = encode_sqlite_to_nxd(SQLITE, ENC_DIR, "item.en.nxd")
    deploy_nxd(out, MOD_ITEM_NXD)
    print(f"renamed {len(GRENADES)} grenades, wrote {MOD_ITEM_NXD} ({out.stat().st_size} bytes)")


if __name__ == "__main__":
    main()
