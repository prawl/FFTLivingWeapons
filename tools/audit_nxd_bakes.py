#!/usr/bin/env python
"""
LW-78: audit the shipped full-table nxd text bakes against the CURRENT vanilla tables.

Why: the modloader applies an nxd override by diffing it PER-CELL against the vanilla table
of the RUNNING game ("only the actual property changes will be tracked", Nenkai's
creating_mods_fft docs; the same semantics patch_ability_names.py builds and self-verifies
on). Our item.en.nxd / ability.en.nxd bakes were authored against pre-1.5 vanilla decodes,
so any text cell a later game patch changed silently turns the stale bake into an unintended
override: we would ship yesterday's vanilla text over the game's own fix (first measured
2026-07-14: 61 such ability cells, e.g. the game's 1.5.x Mighty Guard -> Thunder Breath fix).

Method: extract current vanilla from the installed game's 0004.en.pac at run time (so this
tool re-runs after every future game patch), decode vanilla and the two shipped bakes, and
classify every cell where bake != vanilla (the loader applies exactly these) against DESIGN
INTENT, not against a historical snapshot (the one-time working/nxd_th "pristine" turned out
to be an old bake of this very mod, not vanilla):
  Item-en intent = the same derivation patch_names.py bakes (items.json named rows via
  lib.flavor.assemble_desc, the UiItemCategoryId map, the SortOrder regrouping) plus
  lib/bake_intent.py's ALLOWED_ITEM_CELLS (deliberate hand edits the bake carries; each
  entry cites its design reason; the rebase tool re-applies the same list) plus the
  SortOrder orphan-sweep pattern on unnamed weapon rows.
  Ability-en intent = patch_ability_names.PATCHES.
Anything the loader applies that intent does not explain is UNINTENDED (stale vanilla text or
pilot-sqlite residue: both design bugs). An intended cell whose bake value no longer matches
the current items.json derivation is DRIFT (the bake predates an items.json edit: re-run
patch_names.py). Row-set rules: every current-vanilla row must exist in the bake (missing
rows apply as RemovedRows); extra bake rows must be in ALLOWED_EXTRA_ROWS.

Exit 1 on any UNINTENDED or DRIFT cell or row-set violation. Needs the local Steam install
plus FF16Tools (paths in lib/paths.py), so CI (Linux) cannot run it; it is an on-box auditor
(the PORT runbook's re-diff step), not a pipeline gate.

Usage:
  python tools/audit_nxd_bakes.py          # summary + detail for UNINTENDED and DRIFT
  python tools/audit_nxd_bakes.py --full   # also list every INTENDED cell
"""
import sqlite3
import subprocess
import sys
import tempfile
from datetime import datetime
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
from lib.bake_intent import ALLOWED_EXTRA_ROWS, ALLOWED_ITEM_CELLS
from lib.categories import WEAPON_CATS
from lib.flavor import assemble_desc, is_living, plural
from lib.items import load_items
from lib.nxd import PAC, unpack   # re-exported: rebase_nxd_pristine imports them from here
from lib.paths import FF16, MOD_ABILITY_NXD, MOD_ITEM_NXD, MOD_STATUS_NXD, STEAM_FFT
from patch_ability_names import PATCHES as ABILITY_PATCHES
from patch_status_names import PATCHES as STATUS_PATCHES
from patch_names import GROUP_RANK, SCAFFOLD_LIVING, UICAT



def item_intent():
    """(Key, column) -> expected value, exactly as patch_names.py derives them."""
    doc = load_items()
    named = [it for it in doc["items"] if it.get("name") and it["name"] != "TBD"]
    sort_map, by_group = {}, {}
    for it in named:
        eff = it["proposed"].get("categoryOverride") or it.get("category")
        if eff in WEAPON_CATS:
            by_group.setdefault(UICAT[eff], []).append(it)
    for uicat, items_in in by_group.items():
        rank = GROUP_RANK.get(uicat, 19)
        for i, it in enumerate(sorted(items_in, key=lambda x: (x.get("tier", 99) or 99, x["id"])), start=1):
            sort_map[it["id"]] = rank * 100 + i
    intent = {}
    for it in named:
        clean = it["name"]
        name = clean + "  " if (SCAFFOLD_LIVING and is_living(it)) else clean
        intent[(it["id"], "Name")] = name
        intent[(it["id"], "NameSingular")] = clean.lower()
        intent[(it["id"], "NamePlural")] = plural(clean)
        intent[(it["id"], "Name2")] = name
        intent[(it["id"], "Description")] = assemble_desc(it, scaffold=SCAFFOLD_LIVING)
        eff = it["proposed"].get("categoryOverride") or it.get("category")
        if eff in UICAT:
            intent[(it["id"], "UiItemCategoryId")] = UICAT[eff]
        if it["id"] in sort_map:
            intent[(it["id"], "SortOrder")] = sort_map[it["id"]]
    return intent


def orphan_sort_ok(bake_row, vanilla_row, value):
    """patch_names sweeps unnamed weapon-type rows to the END of their type group, so an
    orphan's regenerated SortOrder is rank*100 + n for its UiItemCategoryId. Only vouches
    when the bake keeps the vanilla category: a row whose category is itself off-intent
    (e.g. a blanked vanilla row we resurrect) gets no SortOrder pass from this rule."""
    cat = bake_row.get("UiItemCategoryId")
    if cat != vanilla_row.get("UiItemCategoryId"):
        return False
    rank = GROUP_RANK.get(cat)
    return rank is not None and isinstance(value, int) and value // 100 == rank




def rows(db, table):
    con = sqlite3.connect(db)
    cols = [r[1] for r in con.execute(f'PRAGMA table_info("{table}")')]
    data = {r[0]: dict(zip(cols, r)) for r in con.execute(f'SELECT * FROM "{table}"')}
    con.close()
    return cols, data


def clip(v, n=70):
    s = repr(v)
    return s if len(s) <= n else s[: n - 3] + "..."


def audit_table(table, vanilla_cols, vanilla, bake, intent, allowed_cells, allowed_extra, full):
    problems = 0
    missing = sorted(set(vanilla) - set(bake))
    extra = sorted(set(bake) - set(vanilla) - allowed_extra)
    if missing:
        print(f"  ROWSET: {len(missing)} vanilla rows MISSING from the bake "
              f"(applied as RemovedRows): {missing}")
        problems += 1
    if extra:
        print(f"  ROWSET: unexpected extra bake rows: {extra}")
        problems += 1
    counts = {"INTENDED": 0, "ALLOWED": 0, "DRIFT": 0, "UNINTENDED": 0}
    detail = []
    for key in sorted(set(vanilla) & set(bake)):
        for col in vanilla_cols:
            if col == "Key" or col not in bake[key]:
                continue
            b, v = bake[key][col], vanilla[key][col]
            if b == v:
                continue
            if (key, col) in allowed_cells:
                want = allowed_cells[(key, col)][0]
                cls = "ALLOWED" if b == want else "UNINTENDED"
            elif (key, col) in intent:
                cls = "INTENDED" if b == intent[(key, col)] else "DRIFT"
            elif table == "Item-en" and col == "SortOrder" and orphan_sort_ok(bake[key], vanilla[key], b):
                cls = "INTENDED"  # the orphan sweep regroups unnamed weapon rows
            else:
                cls = "UNINTENDED"
            counts[cls] += 1
            if cls in ("UNINTENDED", "DRIFT") or full:
                detail.append((cls, key, col, v, b))
    print("  " + "  ".join(f"{k}: {n}" for k, n in counts.items()))
    problems += counts["UNINTENDED"] + counts["DRIFT"]
    for cls, key, col, v, b in detail:
        print(f"  [{cls}] Key {key} {col}:")
        print(f"      vanilla {clip(v)}\n      bake    {clip(b)}")
    return problems


def main():
    full = "--full" in sys.argv
    stamp = datetime.fromtimestamp(PAC.stat().st_mtime).strftime("%Y-%m-%d %H:%M")
    print(f"pac: {PAC} (mtime {stamp})")
    ability_intent = {(k, c): v for k, cols in ABILITY_PATCHES.items() for c, v in cols.items()}
    status_intent = {(k, c): v for k, cols in STATUS_PATCHES.items() for c, v in cols.items()}
    problems = 0
    with tempfile.TemporaryDirectory(prefix="nxd_audit_") as td:
        tmp = Path(td)
        from lib.nxd import decode_nxd_to_sqlite
        for table, inner, bake_nxd, intent, allowed in [
            ("Item-en", "nxd/item.en.nxd", MOD_ITEM_NXD, item_intent(), ALLOWED_ITEM_CELLS),
            ("Ability-en", "nxd/ability.en.nxd", MOD_ABILITY_NXD, ability_intent, {}),
            ("UIStatusEffect-en", "nxd/uistatuseffect.en.nxd", MOD_STATUS_NXD, status_intent, {}),
        ]:
            fresh = unpack(PAC, inner, tmp / "pacout")
            name = Path(inner).name
            v_cols, vanilla = rows(decode_nxd_to_sqlite([fresh], tmp, f"van_{name}.sqlite"), table)
            _, bake = rows(decode_nxd_to_sqlite([bake_nxd], tmp, f"bake_{name}.sqlite"), table)
            print(f"\n=== {table}: vanilla {len(vanilla)} rows | bake {len(bake)} rows ===")
            problems += audit_table(table, v_cols, vanilla, bake, intent, allowed,
                                    ALLOWED_EXTRA_ROWS.get(table, set()), full)
    print(f"\n{'AUDIT RED: ' + str(problems) + ' problem(s)' if problems else 'AUDIT GREEN'}")
    sys.exit(1 if problems else 0)


if __name__ == "__main__":
    main()
