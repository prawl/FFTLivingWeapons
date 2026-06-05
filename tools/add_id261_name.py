#!/usr/bin/env python
"""Add a Key=261 'Moonblade' row to the Item-en localization table (cloned from
id257), re-encode item.en.nxd, and copy it to the mod tree + deployed mod folder.
Tests the NXD-name + pre-boot-catalog-seed combo for the 261 cap break."""
import sqlite3, subprocess, shutil
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
SQLITE = ROOT / "working" / "pilot_item.sqlite"
FF16 = Path(r"C:\Users\ptyRa\Downloads\FF16Tools.CLI-1.13.2-win-x64\win-x64\FF16Tools.CLI.exe")
ENC_DIR = ROOT / "working" / "nxd_out"
MOD_NXD = ROOT / "mod" / "FFTIVC" / "data" / "enhanced" / "nxd" / "item.en.nxd"
DEPLOY_NXD = Path(r"C:\program files (x86)\steam\steamapps\common\FINAL FANTASY TACTICS - The Ivalice Chronicles\Reloaded\Mods\prawl.fft.itemoverhaul\FFTIVC\data\enhanced\nxd\item.en.nxd")

c = sqlite3.connect(SQLITE)
cur = c.execute('SELECT * FROM "Item-en" WHERE Key=257')
cols = [d[0] for d in cur.description]
row = dict(zip(cols, cur.fetchone()))
row.update(Key=261, Name="Moonblade", NameSingular="moonblade", NamePlural="moonblades",
           Name2="Moonblade", Description="A pale crescent blade that drinks the moonlight.",
           SortOrder=217)
c.execute('DELETE FROM "Item-en" WHERE Key=261')
qcols = ",".join('"%s"' % x for x in cols)
c.execute(f'INSERT INTO "Item-en" ({qcols}) VALUES ({",".join("?" * len(cols))})',
          [row[x] for x in cols])
c.commit()
print("Item-en rows now:", c.execute('SELECT COUNT(*),MAX(Key) FROM "Item-en"').fetchone())
c.close()

ENC_DIR.mkdir(parents=True, exist_ok=True)
r = subprocess.run([str(FF16), "sqlite-to-nxd", "-i", str(SQLITE), "-o", str(ENC_DIR), "-g", "fft"],
                   capture_output=True, text=True)
out = ENC_DIR / "item.en.nxd"
if not out.exists():
    print("ENCODE FAILED:\n", r.stdout[-500:], r.stderr[-500:]); raise SystemExit(1)
print("encoded item.en.nxd size:", out.stat().st_size)
shutil.copy(out, MOD_NXD); print("copied ->", MOD_NXD)
DEPLOY_NXD.parent.mkdir(parents=True, exist_ok=True)
shutil.copy(out, DEPLOY_NXD); print("deployed ->", DEPLOY_NXD)
