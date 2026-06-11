#!/usr/bin/env python
"""Add a Key=261 'Moonblade' row to the Item-en localization table (cloned from
id257), re-encode item.en.nxd, and copy it to the mod tree + deployed mod folder.
Tests the NXD-name + pre-boot-catalog-seed combo for the 261 cap break.

PARKED (tools/oneoff/): the 261 display wall turned out to be a boot-built registry,
not a name/catalog problem -- see docs/ITEM_CAP_261_BREAK_JOURNEY.md. WARNING: this
script EXECUTES ON IMPORT and deploys straight into the live Reloaded Mods folder."""
import sqlite3
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from lib.nxd import encode_sqlite_to_nxd, deploy_nxd
from lib.paths import ROOT, MOD_ITEM_NXD, RELOADED_MODS

SQLITE = ROOT / "working" / "pilot_item.sqlite"
ENC_DIR = ROOT / "working" / "nxd_out"
DEPLOY_NXD = (RELOADED_MODS / "prawl.fft.itemoverhaul" / "FFTIVC" / "data"
              / "enhanced" / "nxd" / "item.en.nxd")

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

out = encode_sqlite_to_nxd(SQLITE, ENC_DIR, "item.en.nxd")
print("encoded item.en.nxd size:", out.stat().st_size)
deploy_nxd(out, MOD_ITEM_NXD); print("copied ->", MOD_ITEM_NXD)
deploy_nxd(out, DEPLOY_NXD); print("deployed ->", DEPLOY_NXD)
