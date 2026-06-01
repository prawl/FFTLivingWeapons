#!/usr/bin/env python
"""
Rename + re-describe the 5 recycled cure consumables into offensive status grenades, then
re-encode item.en.nxd. Pairs with the offensive ItemConsumableData.xml override.

ItemConsumable rows 6-10 map to item ids 246-250 (Antidote / Eye Drops / Echo Herbs /
Maiden's Kiss / Gold Needle) -- the cures Remedy already covers, so curing capability is
unchanged. We repoint their behavior (in ItemConsumableData.xml) to inflict a status, and
rename the items here so the menu matches.
"""
import sqlite3, subprocess, shutil
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
SQLITE = ROOT / "working" / "pilot_item.sqlite"
FF16 = Path(r"C:\Users\ptyRa\Downloads\FF16Tools.CLI-1.13.2-win-x64\win-x64\FF16Tools.CLI.exe")
MOD_NXD = ROOT / "mod" / "FFTIVC" / "data" / "enhanced" / "nxd" / "item.en.nxd"
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
    ENC_DIR.mkdir(parents=True, exist_ok=True)
    r = subprocess.run([str(FF16), "sqlite-to-nxd", "-i", str(SQLITE), "-o", str(ENC_DIR), "-g", "fft"],
                       capture_output=True, text=True)
    out = ENC_DIR / "item.en.nxd"
    if not out.exists():
        print("ENCODE FAILED:\n" + r.stdout + r.stderr); raise SystemExit(1)
    shutil.copy(out, MOD_NXD)
    print(f"renamed {len(GRENADES)} grenades, wrote {MOD_NXD} ({out.stat().st_size} bytes)")


if __name__ == "__main__":
    main()
