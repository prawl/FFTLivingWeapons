#!/usr/bin/env python
"""
LW-78: rebase the nxd bake inputs onto the CURRENT vanilla tables (the PORT runbook's
"re-decode the base nxds" step, docs/research/PORT_1.5.md Track B step 4).

The two bake scripts build from local pristine state that goes stale every game patch:
  - patch_names.py edits working/pilot_item.sqlite in place and encodes it whole, so every
    cell patch_names does not own ships at whatever vintage the sqlite last decoded from.
  - patch_ability_names.py rebuilds from working/nxd_ability/ability.sqlite (its PRISTINE).
This tool re-anchors both onto a fresh extract from the installed game's 0004.en.pac:

  item:    back up pilot_item.sqlite (.bak), replace it with the fresh vanilla decode,
           re-apply lib/bake_intent.py's ALLOWED_ITEM_CELLS (the deliberate hand edits),
           and copy the ALLOWED_EXTRA_ROWS rows over from the backup (the cap-break row).
  ability: replace working/nxd_ability/ability.en.nxd (+ .bak) and re-decode ability.sqlite.

Then re-run, in order: patch_names.py, patch_ability_names.py, audit_nxd_bakes.py (must
exit green). This tool never touches the mod tree itself; the patch scripts do.

Usage: python tools/rebase_nxd_pristine.py
"""
import shutil
import sqlite3
import sys
import tempfile
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
from lib.bake_intent import ALLOWED_EXTRA_ROWS, ALLOWED_ITEM_CELLS
from lib.nxd import decode_nxd_to_sqlite
from lib.paths import ROOT
from audit_nxd_bakes import PAC, unpack

PILOT = ROOT / "working" / "pilot_item.sqlite"
ABILITY_NXD = ROOT / "working" / "nxd_ability" / "ability.en.nxd"
ABILITY_SQLITE = ROOT / "working" / "nxd_ability" / "ability.sqlite"


def copy_row(src_db, dst_db, table, key):
    src, dst = sqlite3.connect(src_db), sqlite3.connect(dst_db)
    cols = [r[1] for r in src.execute(f'PRAGMA table_info("{table}")')]
    row = src.execute(f'SELECT * FROM "{table}" WHERE Key=?', (key,)).fetchone()
    if row is None:
        sys.exit(f"FAIL: allowed extra row Key {key} missing from the pilot backup")
    dst.execute(f'INSERT INTO "{table}" ({", ".join(chr(34)+c+chr(34) for c in cols)}) '
                f'VALUES ({", ".join("?"*len(cols))})', row)
    dst.commit()
    src.close(); dst.close()


def main():
    with tempfile.TemporaryDirectory(prefix="nxd_rebase_") as td:
        tmp = Path(td)
        fresh_item = unpack(PAC, "nxd/item.en.nxd", tmp / "pacout")
        fresh_ability = unpack(PAC, "nxd/ability.en.nxd", tmp / "pacout")

        # --- item: pilot_item.sqlite = fresh vanilla + hand edits + carried extra rows ---
        bak = PILOT.with_suffix(".sqlite.bak")
        shutil.copy(PILOT, bak)
        fresh_db = decode_nxd_to_sqlite([fresh_item], tmp, "fresh_item.sqlite")
        shutil.copy(fresh_db, PILOT)
        con = sqlite3.connect(PILOT)
        for (key, col), (val, reason) in sorted(ALLOWED_ITEM_CELLS.items()):
            con.execute(f'UPDATE "Item-en" SET "{col}"=? WHERE Key=?', (val, key))
            if con.execute("SELECT changes()").fetchone()[0] != 1:
                sys.exit(f"FAIL: hand edit Key {key} {col} did not update exactly one row")
            print(f"  hand edit: Key {key} {col} = {val}  ({reason})")
        con.commit(); con.close()
        for key in sorted(ALLOWED_EXTRA_ROWS.get("Item-en", ())):
            copy_row(bak, PILOT, "Item-en", key)
            print(f"  carried extra row: Key {key} (from {bak.name})")
        print(f"rebased {PILOT.name} onto current vanilla (backup: {bak.name})")

        # --- ability: pristine nxd + its decode ---
        if ABILITY_NXD.exists():
            shutil.copy(ABILITY_NXD, ABILITY_NXD.with_suffix(".nxd.bak"))
        shutil.copy(fresh_ability, ABILITY_NXD)
        fresh_ab_db = decode_nxd_to_sqlite([fresh_ability], tmp, "fresh_ability.sqlite")
        shutil.copy(fresh_ab_db, ABILITY_SQLITE)
        print(f"rebased {ABILITY_NXD.name} + {ABILITY_SQLITE.name} onto current vanilla")
    print("NEXT: python tools/patch_names.py && python tools/patch_ability_names.py "
          "&& python tools/audit_nxd_bakes.py")


if __name__ == "__main__":
    main()
