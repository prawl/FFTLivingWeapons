"""Shared nxd patch-script safety machinery (LW-149 Stage E; formerly 3 copied bodies:
patch_ability_names.py, patch_status_names.py, and audit_nxd_bakes.py's own row reader).

The two patch scripts share the same shape: apply a small {Key: {column: value}} PATCHES dict
to a working sqlite, re-encode it to nxd, then decode the FRESH BUILD back to sqlite and assert
that only the intended cells differ from a pristine vanilla decode before deploying. That verify
loop exists because of a real incident: the 2026-06-05 "Bloodpact" ability-table ship corrupted
unrelated abilities (docs/MECHANICS.md), so every patch script since verifies its own output
rather than trusting the encoder.

apply_patches / verify_only_intended_cells go through lib.nxd.decode_nxd_to_sqlite for every
decode, never a raw subprocess.run -- the two former copies of verify() shelled out to FF16Tools
directly, which is exactly the kind of duplicated, unguarded machinery this stage exists to fold
away.
"""
import sqlite3
import sys
import tempfile
from pathlib import Path

from .nxd import decode_nxd_to_sqlite


def apply_patches(db: Path, table: str, patches: dict) -> None:
    """UPDATE db[table] per a {Key: {column: value}} dict, one UPDATE per Key, each guarded on
    changes() == 1 so a typo'd or vanished Key fails loud instead of silently no-opping."""
    con = sqlite3.connect(db)
    for key, cols in patches.items():
        sets = ", ".join(f'"{c}" = ?' for c in cols)
        con.execute(f'UPDATE "{table}" SET {sets} WHERE Key = ?', [*cols.values(), key])
        if con.execute('SELECT changes()').fetchone()[0] != 1:
            sys.exit(f"FAIL: Key {key} did not update exactly one row")
    con.commit()
    con.close()


def rows(db: Path, table: str, return_cols: bool = False):
    """Read db[table] into a {Key: {column: value}} dict. return_cols=True also returns the
    column list ahead of it as (cols, data) -- the form audit_nxd_bakes.py needs, since it walks
    every column of the table rather than only the ones a PATCHES dict names."""
    con = sqlite3.connect(db)
    cols = [r[1] for r in con.execute(f'PRAGMA table_info("{table}")')]
    data = {r[0]: dict(zip(cols, r)) for r in con.execute(f'SELECT * FROM "{table}"')}
    con.close()
    return (cols, data) if return_cols else data


def verify_only_intended_cells(built_nxd: Path, pristine_sqlite: Path, table: str,
                                patches: dict) -> None:
    """Decode built_nxd back to sqlite and assert only the cells named in `patches` differ from
    pristine_sqlite, and that every one of them actually landed. SystemExit (refusing to deploy)
    on any unexpected diff, any row-set mismatch, or any patched cell that failed to land."""
    with tempfile.TemporaryDirectory(prefix="nxd_verify_") as td:
        decoded = decode_nxd_to_sqlite([built_nxd], Path(td), built_nxd.stem + "_verify.sqlite")

        vanilla, rebuilt = rows(pristine_sqlite, table), rows(decoded, table)
        if set(vanilla) != set(rebuilt):
            sys.exit(f"FAIL: row-key sets differ (vanilla {len(vanilla)} vs rebuilt {len(rebuilt)})")
        unexpected = []
        for key, vrow in vanilla.items():
            for col, vval in vrow.items():
                nval = rebuilt[key][col]
                if nval == vval:
                    continue
                if col in patches.get(key, {}) and nval == patches[key][col]:
                    continue
                unexpected.append((key, col, vval, nval))
        if unexpected:
            for key, col, vval, nval in unexpected[:20]:
                print(f"  UNEXPECTED diff Key {key} {col}: {vval!r} -> {nval!r}")
            sys.exit(f"FAIL: {len(unexpected)} unexpected cell diffs -- refusing to deploy")
        for key, cols in patches.items():
            for col, val in cols.items():
                if rebuilt[key][col] != val:
                    sys.exit(f"FAIL: Key {key} {col} did not land in the rebuilt table")
    print(f"  verify PASS: only the intended {sum(len(c) for c in patches.values())} cells differ")
