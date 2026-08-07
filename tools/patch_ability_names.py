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
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
from lib.nxd import PAC, decode_nxd_to_sqlite, encode_sqlite_to_nxd, deploy_nxd, unpack
from lib.nxd_patch import apply_patches, verify_only_intended_cells
from lib.paths import ROOT, MOD_ABILITY_NXD

TABLE = "Ability-en"
NXD_NAME = "ability.en.nxd"
PAC_INNER = "nxd/" + NXD_NAME

PRISTINE_DIR = ROOT / "working" / "nxd_ability"
PRISTINE_NXD = PRISTINE_DIR / NXD_NAME
PRISTINE = PRISTINE_DIR / "ability.sqlite"   # vanilla decode (do not mutate)
BUILD = ROOT / "working" / "nxd_out_ability" / "ability_build.sqlite"
ENC_DIR = ROOT / "working" / "nxd_out_ability"

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
    # ---------------------------------------------------------------------------------------
    # LW-123: the Defender's "Provoke" command. Key 189 is vanilla `Embrace`, a cut ability that
    # nothing in the game can reach -- no JobCommand record, no monster skillset, no innate slot,
    # no weapon proc -- which is what makes renaming it safe, since a rename is global to the id.
    # Chosen live 2026-07-22 over two other candidates because it alone cleared all three gates:
    # it renders in a command list, its cursor reaches a single enemy at range 5, and the engine
    # actually executes it (0 MP, no charge, 100% on a non-immune target).
    #
    # This row supplies the NAME and DESCRIPTION. The ability's EFFECT is a separate lever the
    # runtime owns (Provoke.Table.cs, shipped 2026-07-22): ability 189's action-row InflictStatus
    # byte is repointed at a hand-authored inflict row that plants StatusEffectData id 0, a blank
    # mark, NOT via this data edit -- the behaviour-table nxd stays parked after the Bloodpact
    # corruption. Arc 2a's ProvokeHold reads that mark and, while an enemy holds the turn, hides
    # every player unit except the bearer so the enemy AI funnels onto the bearer (PROVEN LIVE
    # 2026-07-22, WINDOW mode). The description below matches that shipped behaviour and replaces the
    # abandoned Berserk fiction ("blind rage / forgets its skills"), which described a different,
    # rejected design (index 53). See docs/PROVOKE_AC.md and the LIVE_LEDGER funnel row.
    189: {
        "Name": "Provoke",
        "Description": "Threaten a distant foe. Until it takes its turn, enemy attacks are drawn "
                       "onto the bearer, not your allies.",
        "IconId": 32,
    },
}


def ensure_pristine():
    """Extract + decode the vanilla ability table if the local cache is missing (the
    patch_status_names.py bootstrap, adopted for LW-156: working/ is an uncommitted build
    cache, so a hand-placed vanilla decode made the first run FileNotFoundError on any fresh
    checkout). Delete working/nxd_ability/ to force a re-extract after a game patch."""
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


def main() -> None:
    dry = "--dry" in sys.argv
    for key, cols in PATCHES.items():
        print(f"Key {key}: " + "; ".join(f"{c} = {v!r}" for c, v in cols.items()))
    if dry:
        return
    ensure_pristine()
    ENC_DIR.mkdir(parents=True, exist_ok=True)
    shutil.copy(PRISTINE, BUILD)
    apply_patches(BUILD, TABLE, PATCHES)
    out_nxd = encode_sqlite_to_nxd(BUILD, ENC_DIR, NXD_NAME)
    verify_only_intended_cells(out_nxd, PRISTINE, TABLE, PATCHES)
    deploy_nxd(out_nxd, MOD_ABILITY_NXD)
    print(f"deployed -> {MOD_ABILITY_NXD}")


if __name__ == "__main__":
    main()
