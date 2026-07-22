#!/usr/bin/env python
"""
Patch uistatuseffect.en.nxd name/caption rows: give a blank status its identity.

Covers:
  Key 1  - "Provoked" (LW-123, the Defender's Provoke mark)

WHY THIS TABLE EXISTS IN OUR MOD AT ALL
---------------------------------------
Provoke marks the enemy it goads. Choosing WHICH status to mark with was decided live on
2026-07-22 by trying four and rejecting three (see the LIVE_LEDGER row and docs/PROVOKE_AC.md):
a status that carries any engine behaviour is unusable as a marker, because the marker must be
inert. The winner is the status at band `+0x45` bit `0x80` -- StatusEffectData Id 0, UIStatusEffect
Key 1 -- which is the only candidate with `CheckFlags: 0` AND `Counter: 0`: no behaviour, no
duration, no pose, no tint, and it does not even appear in this repo's own status map. It also
lands on story bosses that resist 37 of the 38 statuses the immunity system knows.

The cost of an unused status is that it ships with a BLANK Name and Caption, so the player has no
idea what the icon over that unit means. This script writes those two cells. That is safe precisely
BECAUSE the row is blank: status text is global to the status, exactly as ability text is global to
the ability id, so renaming a status the game actually uses (Slow, Berserk) would rewrite every
legitimate occurrence of it in the game. A blank row has no legitimate occurrences to break.

  Key = status bit index + 1. Verified against five statuses whose band bits this repo already
  knows: Berserk (bit 20 -> Key 21), Invisibility (19 -> 20), Slow (29 -> 30), Charmed (34 -> 35),
  Immobilize (36 -> 37).

TWO MORE CELLS, decoded live 2026-07-22 after name-and-caption alone rendered NOTHING. Naming a
status is not enough to display it; two other columns gate that, and they were found by diffing a
row the game shows against a row it does not:

  Unknown14  the DISPLAY CATEGORY, and it gates rendering entirely. 0 = never rendered, which is
             why our mark and `Performing` stayed invisible no matter what text they carried.
             1 = the buff group, 2 = the debuff group.
  Unknown20  the ICON INDEX, where -1 means no icon. The index space is ORGANISED BY CATEGORY and
             each block is fully occupied: the U14=1 buffs use 1..10 (Protect 1, Shell 2, Regen 3,
             Reraise 4, Haste 5, Float 6, Reflect 7, Faith 8, Atheist 9, Invisibility 10) and the
             U14=2 debuffs use 20..39 (KO 20 through Doom 39). Values outside a category's own
             block do not resolve FOR that category: 102 renders under U14=1 and draws nothing
             under U14=2, observed live 2026-07-22 on a unit whose overhead icons visibly cycled
             between Protect and a blank slot.

NO OVERHEAD ICON IS AVAILABLE, and the row is filed as a debuff (U14=2) because that is what it
honestly is. Both categories were tried live 2026-07-22 with icon 102, the value `Wall` renders a
blue diamond from, and NEITHER drew anything for this Key. The mark is demonstrably IN the overhead
rotation -- on a unit already carrying Protect the icons visibly cycled between Protect and an empty
slot -- so the status is recognised and simply has no sprite. Since `Wall` renders that same index
from the same columns, the art must be keyed on the STATUS ID somewhere outside this table; the
generic `Icon` table in 0004.pac is not it, as its keys do not line up with the status blocks at all
(index 20 is a zodiac-sign path while KO uses 20). Chasing it further was capped as polish. The
player still gets "Provoked" plus its description in the status list, and a re-cast on an already
provoked unit reads 0%, which is honest feedback for free.

`Type` is NOT the gate and is deliberately still not written: Key 1, Key 21 Berserk and Key 32 Wall
are all Type 0, yet two of them render and one did not. Whatever Type selects, it is not visibility.

The icon chosen is 102, the one `Wall` points at. Berserk's 33 was the first pick and was rejected
on a good argument: Berserk occurs naturally in play, so sharing its icon would make a genuinely
enraged enemy read as provoked and vice versa. Icon 102 has no such problem because NOTHING in the
game inflicts `Wall` (its text is blank and no ability's action row points at a combination
containing it), so that art is effectively unused and becomes ours alone. It renders as a blue
diamond, which carries no prior meaning to a player.

Same safety contract as tools/patch_ability_names.py, and for the same reason: the 2026-06-05
"Bloodpact" ability-table ship corrupted unrelated abilities (docs/MECHANICS.md). This script
therefore VERIFIES ITS OWN OUTPUT -- it decodes the freshly-built nxd back to sqlite and asserts
that exactly the intended cells differ from pristine vanilla. A red verify refuses to deploy.

Unlike its sibling, this script BOOTSTRAPS its own pristine: working/ is a local build cache that
is never committed, so requiring a hand-placed vanilla decode would make the first run fail on any
fresh checkout. The vanilla table is extracted from the installed game's own pac on demand and
cached. Delete working/nxd_status/ to force a re-extract after a game patch.

Usage:
  python tools/patch_status_names.py          # build + verify + deploy into the mod tree
  python tools/patch_status_names.py --dry    # print planned edits, no writes
"""
import shutil
import sqlite3
import subprocess
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
from lib.nxd import PAC, decode_nxd_to_sqlite, encode_sqlite_to_nxd, deploy_nxd, unpack
from lib.paths import ROOT, FF16, MOD_STATUS_NXD

TABLE = "UIStatusEffect-en"
NXD_NAME = "uistatuseffect.en.nxd"
PAC_INNER = "nxd/" + NXD_NAME

PRISTINE_DIR = ROOT / "working" / "nxd_status"
PRISTINE_NXD = PRISTINE_DIR / NXD_NAME
PRISTINE = PRISTINE_DIR / "uistatuseffect.sqlite"     # vanilla decode (do not mutate)
BUILD = ROOT / "working" / "nxd_out_status" / "status_build.sqlite"
ENC_DIR = ROOT / "working" / "nxd_out_status"
DEC_DIR = ENC_DIR / "verify_decode"

# Key -> {column: value}. Key 1 is the blank row for StatusEffectData Id 0 (band +0x45 bit 0x80).
PATCHES = {
    1: {
        "Name": "Provoked",
        "Caption": "The unit has been goaded into a fury and sees nothing but the one who "
                   "called it out.",
        "Unknown14": 2,      # display category: 0 never renders; 1 = buff group, 2 = debuff group
        "Unknown20": 102,    # no sprite resolves for this Key in either category; kept as the best guess
    },
}


def ensure_pristine():
    """Extract + decode the vanilla status table if the local cache is missing."""
    if PRISTINE.exists() and PRISTINE_NXD.exists():
        return
    PRISTINE_DIR.mkdir(parents=True, exist_ok=True)
    print(f"  no cached vanilla decode; extracting {PAC_INNER} from the game pac...")
    fresh = unpack(PAC, PAC_INNER, PRISTINE_DIR / "pacout")
    shutil.copy(fresh, PRISTINE_NXD)
    decoded = decode_nxd_to_sqlite([PRISTINE_NXD], PRISTINE_DIR, PRISTINE.name)
    if decoded != PRISTINE:
        shutil.copy(decoded, PRISTINE)
    print(f"  cached -> {PRISTINE}")


def apply_patches(db: Path) -> None:
    con = sqlite3.connect(db)
    for key, cols in PATCHES.items():
        sets = ", ".join(f'"{c}" = ?' for c in cols)
        con.execute(f'UPDATE "{TABLE}" SET {sets} WHERE Key = ?', [*cols.values(), key])
        if con.execute('SELECT changes()').fetchone()[0] != 1:
            sys.exit(f"FAIL: Key {key} did not update exactly one row")
    con.commit()
    con.close()


def rows(db: Path) -> dict:
    con = sqlite3.connect(db)
    cols = [r[1] for r in con.execute(f'PRAGMA table_info("{TABLE}")')]
    data = {r[0]: dict(zip(cols, r)) for r in con.execute(f'SELECT * FROM "{TABLE}"')}
    con.close()
    return data


def verify(built_nxd: Path) -> None:
    """Decode the built nxd and assert only the intended cells differ from vanilla."""
    if DEC_DIR.exists():
        shutil.rmtree(DEC_DIR)
    in_dir = DEC_DIR / "in"
    in_dir.mkdir(parents=True)
    shutil.copy(built_nxd, in_dir / built_nxd.name)
    decoded = DEC_DIR / "status_verify.sqlite"
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
    ensure_pristine()
    # Guard the one assumption a blank-row patch rests on: if the row we are claiming is NOT blank
    # in this game version, we would be overwriting text the game actually shows.
    van = rows(PRISTINE)
    for key in PATCHES:
        if key not in van:
            sys.exit(f"FAIL: Key {key} is not in the vanilla status table")
        for col in ("Name", "Caption"):
            cur = van[key].get(col)
            if cur not in (None, "", "None"):
                sys.exit(f"FAIL: Key {key} {col} is not blank in vanilla (reads {cur!r}). "
                         f"Claiming a status the game already names would rewrite every "
                         f"legitimate use of it.")
    ENC_DIR.mkdir(parents=True, exist_ok=True)
    shutil.copy(PRISTINE, BUILD)
    apply_patches(BUILD)
    out_nxd = encode_sqlite_to_nxd(BUILD, ENC_DIR, NXD_NAME)
    verify(out_nxd)
    deploy_nxd(out_nxd, MOD_STATUS_NXD)
    print(f"deployed -> {MOD_STATUS_NXD}")


if __name__ == "__main__":
    main()
