#!/usr/bin/env python
# RETIRED / SUPERSEDED by patch_ability_names.py (2026-06-10).
# That script folds keys 374-378 into its self-verifying PATCHES dict so the
# grenade names survive a pristine sqlite re-decode without a manual re-run of
# this file.  Do NOT run this script -- it mutates the pristine sqlite in place
# and will cause patch_ability_names.py's self-verify to fail with unexpected diffs.
"""Rename the 5 Chemist Item-command USE-abilities to match the grenades.

The grenade rework renamed the ITEMS (Item-en, item.en.nxd via patch_grenades.py) but
the Chemist's ability-LEARN menu shows the item-USE ability names, which live in a SEPARATE
table: Ability-en (ability.en.nxd). Keys 374-378 = Antidote/Eye Drops/Echo Herbs/Maiden's
Kiss/Gold Needle, the use-abilities for items 246-250. This repoints their Name+Description
to the grenades so the learn menu stops advertising removed cures.

Full-table round-trip (edit 5 rows, re-encode whole nxd) -- same pattern as patch_grenades.py.
Modloader falls back to vanilla if the nxd is malformed. Reversible: delete the deployed
ability.en.nxd + restart.
"""
import sqlite3, subprocess, shutil
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
SQLITE = ROOT / "working" / "nxd_ability" / "ability.sqlite"
FF16 = Path(r"C:\Users\ptyRa\Downloads\FF16Tools.CLI-1.13.2-win-x64\win-x64\FF16Tools.CLI.exe")
MOD_NXD = ROOT / "mod" / "FFTIVC" / "data" / "enhanced" / "nxd" / "ability.en.nxd"
LIVE_NXD = Path(r"C:\Program Files (x86)\Steam\steamapps\common\FINAL FANTASY TACTICS - The Ivalice Chronicles\Reloaded\Mods\prawl.fft.itemoverhaul\FFTIVC\data\enhanced\nxd\ability.en.nxd")
ENC_DIR = ROOT / "working" / "nxd_out_ability"

# ability Key -> (name, description). Mirrors patch_grenades.py items 246-250.
GRENADES = {
    374: ("Venom Flask", "Hurl a Venom Flask to inflict Poison on the target."),
    375: ("Smoke Bomb",  "Hurl a Smoke Bomb to inflict Blind on the target."),
    376: ("Hush Vial",   "Hurl a Hush Vial to inflict Silence on the target."),
    377: ("Oil Flask",   "Hurl an Oil Flask to coat the target in Oil, doubling Fire damage taken."),
    378: ("Sludge Bomb", "Hurl a Sludge Bomb to inflict Slow on the target."),
}


def main():
    con = sqlite3.connect(SQLITE)
    for k, (name, desc) in GRENADES.items():
        old = con.execute('SELECT Name FROM "Ability-en" WHERE Key=?', (k,)).fetchone()
        print(f"  {k}: {old[0] if old else '???'!r} -> {name!r}")
        con.execute('UPDATE "Ability-en" SET Name=?, Description=? WHERE Key=?', (name, desc, k))
    con.commit()
    nrows = con.execute('SELECT COUNT(*) FROM "Ability-en"').fetchone()[0]
    con.close()
    print(f"Ability-en total rows: {nrows}  (sanity: should be the full table, ~450)")

    ENC_DIR.mkdir(parents=True, exist_ok=True)
    r = subprocess.run([str(FF16), "sqlite-to-nxd", "-i", str(SQLITE), "-o", str(ENC_DIR), "-g", "fft"],
                       capture_output=True, text=True)
    out = ENC_DIR / "ability.en.nxd"
    if not out.exists():
        print("ENCODE FAILED:\n" + r.stdout + r.stderr); raise SystemExit(1)
    sz = out.stat().st_size
    print(f"encoded ability.en.nxd: {sz} bytes")
    if sz < 1000:
        print("REFUSING to deploy: encoded nxd suspiciously small"); raise SystemExit(1)

    MOD_NXD.parent.mkdir(parents=True, exist_ok=True)
    shutil.copy(out, MOD_NXD)
    print(f"staged source: {MOD_NXD}")
    if LIVE_NXD.parent.exists():
        shutil.copy(out, LIVE_NXD)
        print(f"deployed live: {LIVE_NXD}")
    else:
        print(f"LIVE nxd dir missing (not deployed): {LIVE_NXD.parent}")


if __name__ == "__main__":
    main()
