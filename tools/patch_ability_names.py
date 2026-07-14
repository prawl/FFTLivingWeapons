#!/usr/bin/env python
"""
Patch ability.en.nxd name/description rows.

Covers:
  Key 358  - Barrage (Yoichi bow ability)
  Key 460  - Equip Axes (Description only, LW-77 reforged-note)

The modloader merges nxd tables CELL-level against vanilla ("only the actual property
changes will be tracked so that multiple mods can edit the same table" -- Nenkai's
creating_mods_fft docs), so a vanilla-faithful rebuild with only our rows changed
coexists with other installed ability.en.nxd mods (e.g. GenericJobs).

History note: the 2026-06-05 "Bloodpact" ability-table ship corrupted unrelated
abilities and was parked (docs/MECHANICS.md, Bloodpact bullet). This script therefore
VERIFIES its own output: it decodes the freshly-built nxd back to sqlite and asserts
that exactly the intended rows/cells differ from the pristine vanilla decode. A red
verify refuses to deploy the file.

Usage:
  python tools/patch_ability_names.py          # build + verify + deploy into the mod tree
  python tools/patch_ability_names.py --dry    # print planned edits, no writes
"""
import shutil
import sqlite3
import subprocess
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
from lib.nxd import encode_sqlite_to_nxd, deploy_nxd
from lib.paths import ROOT, FF16, MOD_ABILITY_NXD

PRISTINE = ROOT / "working" / "nxd_ability" / "ability.sqlite"   # vanilla decode (do not mutate)
BUILD = ROOT / "working" / "nxd_out_ability" / "ability_build.sqlite"
ENC_DIR = ROOT / "working" / "nxd_out_ability"
DEC_DIR = ROOT / "working" / "nxd_out_ability" / "verify_decode"

# Key -> {column: value}. IconId 32 = the standard action-ability icon (Aurablast/Rush use it).
PATCHES = {
    358: {
        "Name": "Barrage",
        "Description": "Unleash 4 attacks with the emphasis on speed. "
                       "Each strike inflicts half the usual damage.",
        "IconId": 32,
    },
    # LW-77: Name/IconId untouched, Description only. Every axe in this rebalance was reforged
    # into another weapon type (make_jobequip.py REMOVE), so the support has nothing left to
    # equip; this note replaces the mod JobCommandData.xml row that used to zero the learnable
    # slot (deleted, whole-row writeback collision, see tools/oneoff/make_jobcommand.py).
    460: {
        "Description": "Allows the unit to equip axes, regardless of job. Every axe in this "
                       "rebalance was reforged into another weapon type, so there is nothing "
                       "left to equip.",
    },
}


def apply_patches(db: Path) -> None:
    con = sqlite3.connect(db)
    for key, cols in PATCHES.items():
        sets = ", ".join(f'"{c}" = ?' for c in cols)
        con.execute(f'UPDATE "Ability-en" SET {sets} WHERE Key = ?', [*cols.values(), key])
        if con.execute('SELECT changes()').fetchone()[0] != 1:
            sys.exit(f"FAIL: Key {key} did not update exactly one row")
    con.commit()
    con.close()


def rows(db: Path) -> dict:
    con = sqlite3.connect(db)
    cols = [r[1] for r in con.execute('PRAGMA table_info("Ability-en")')]
    data = {r[0]: dict(zip(cols, r)) for r in con.execute('SELECT * FROM "Ability-en"')}
    con.close()
    return data


def verify(built_nxd: Path) -> None:
    """Decode the built nxd and assert only the intended cells differ from vanilla."""
    if DEC_DIR.exists():
        shutil.rmtree(DEC_DIR)
    in_dir = DEC_DIR / "in"
    in_dir.mkdir(parents=True)
    shutil.copy(built_nxd, in_dir / built_nxd.name)
    decoded = DEC_DIR / "ability_verify.sqlite"
    r = subprocess.run([str(FF16), "nxd-to-sqlite", "-i", str(in_dir),
                        "-o", str(decoded), "-g", "fft"], capture_output=True, text=True)
    if r.returncode != 0 or not decoded.exists():
        sys.exit(f"FAIL: verify decode failed:\n{r.stdout}\n{r.stderr}")

    vanilla, rebuilt = rows(PRISTINE), rows(decoded)
    if set(vanilla) != set(rebuilt):
        sys.exit(f"FAIL: row-key sets differ (vanilla {len(vanilla)} vs rebuilt {len(rebuilt)})")
    unexpected = []
    for key, vrow in vanilla.items():
        for col, vval in vrow.items():
            nval = rebuilt[key][col]
            if nval == vval:
                continue
            if col in PATCHES.get(key, {}) and nval == PATCHES[key][col]:
                continue
            unexpected.append((key, col, vval, nval))
    if unexpected:
        for key, col, vval, nval in unexpected[:20]:
            print(f"  UNEXPECTED diff Key {key} {col}: {vval!r} -> {nval!r}")
        sys.exit(f"FAIL: {len(unexpected)} unexpected cell diffs -- refusing to deploy")
    for key, cols in PATCHES.items():
        for col, val in cols.items():
            if rebuilt[key][col] != val:
                sys.exit(f"FAIL: Key {key} {col} did not land in the rebuilt table")
    print(f"  verify PASS: only the intended {sum(len(c) for c in PATCHES.values())} cells differ")


def main() -> None:
    dry = "--dry" in sys.argv
    for key, cols in PATCHES.items():
        print(f"Key {key}: " + "; ".join(f"{c} = {v!r}" for c, v in cols.items()))
    if dry:
        return
    ENC_DIR.mkdir(parents=True, exist_ok=True)
    shutil.copy(PRISTINE, BUILD)
    apply_patches(BUILD)
    out_nxd = encode_sqlite_to_nxd(BUILD, ENC_DIR, "ability.en.nxd")
    verify(out_nxd)
    deploy_nxd(out_nxd, MOD_ABILITY_NXD)
    print(f"deployed -> {MOD_ABILITY_NXD}")


if __name__ == "__main__":
    main()
