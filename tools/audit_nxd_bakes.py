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
  entry cites its design reason; the rebase tool re-applies the same list). Weapon rows
  items.json does not name keep the game's own SortOrder untouched (LW-351), so a diff on
  one of those is a finding, not a pattern to vouch for.
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
import subprocess
import sys
import tempfile
from datetime import datetime
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
from lib.bake_intent import ALLOWED_EXTRA_ROWS, ALLOWED_ITEM_CELLS
from lib.nxd import PAC, unpack   # re-exported: rebase_nxd_pristine imports them from here
from lib.nxd_patch import rows as _rows   # LW-149 Stage E: unify onto the shared row reader
from lib.paths import FF16, MOD_ABILITY_NXD, MOD_ITEM_NXD, MOD_STATUS_NXD, STEAM_FFT
from patch_ability_names import PATCHES as ABILITY_PATCHES
from patch_status_names import PATCHES as STATUS_PATCHES
from patch_names import item_intent as _bake_item_intent, named_items



def item_intent():
    """(Key, column) -> expected value, via patch_names.item_intent: the SAME derivation the
    writer bakes with. This used to be a token-identical inline copy (LW-156): an edit to the
    bake rules in patch_names.py alone would flip every touched cell to DRIFT here, turning the
    deploy red with a message blaming the audit instead of the real cause. One shared function
    makes that divergence unrepresentable."""
    return _bake_item_intent(named_items())


# (The orphan-sweep allowance is gone, LW-351: patch_names.py no longer rewrites the SortOrder of
# weapon rows data/items.json does not name. It numbers OUR rows around theirs instead, so a stock
# weapon row's key must now match vanilla exactly and any diff on one is a real finding. The rule
# it replaced vouched for any value in the right hundreds, which is also why it could not have
# caught the duplicate that cost the 2026-08-30 live pass its two items.)


def rows(db, table):
    """(cols, data) form this module's own callers and patch_names.py's audit.rows(...) expect
    (LW-149 Stage E: thin wrapper onto the shared lib.nxd_patch.rows, return_cols=True)."""
    return _rows(db, table, return_cols=True)


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
    seen_intended = set()   # (key, col) that actually showed up as a bake-vs-vanilla diff
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
                seen_intended.add((key, col))
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

    # MISS check (LW-148): the loop above only ever looks at cells where bake != vanilla, so an
    # intended change that never actually landed (bake still reads vanilla, e.g. a rename whose
    # UPDATE silently matched zero rows) never enters it at all -- a silently no-opped rename was
    # therefore invisible to this audit. Walk the intent set itself and demand every entry whose
    # target genuinely differs from vanilla actually shows up as a diff above.
    missed = []
    for (key, col), want in sorted(intent.items()):
        if key not in vanilla or key not in bake or col not in bake[key]:
            continue   # row-set problems (missing/extra rows) are already reported above
        if (key, col) in allowed_cells:
            continue   # the classifier loop above checks allowed_cells BEFORE intent, so a cell
                       # present in both never reaches seen_intended there either way (ALLOWED or
                       # UNINTENDED); re-checking it against intent's own target here would
                       # false-flag a landed, correctly-classified change as a silent no-op
        v = vanilla[key][col]
        if want == v:
            continue   # intent legitimately matches vanilla already; no diff was ever expected
        if (key, col) not in seen_intended:
            missed.append((key, col, v, want))
    if missed:
        print(f"  MISSED: {len(missed)} intended change(s) never diverged from vanilla (silent no-op):")
        for key, col, v, want in missed:
            print(f"      Key {key} {col}: still {clip(v)} (wanted {clip(want)})")
        problems += len(missed)

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
