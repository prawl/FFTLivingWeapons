#!/usr/bin/env python
"""
Batch-patch item.en.nxd names + descriptions from data/items.json.

For every renamed item it updates the Item-en row (Name / NameSingular / NamePlural / Name2 / Description),
then re-encodes the working sqlite to item.en.nxd via FF16Tools and drops it in the mod tree.

Description = one flavor line + one mechanics line, the mechanics derived from the proposed stats
(element, on-hit status, or EquipBonus rider).

Usage:
  python tools/patch_names.py            # patch all named items, re-encode, deploy nxd to mod tree
  python tools/patch_names.py --dry      # print the name/description that WOULD be written, no write
"""
import sys
from pathlib import Path
import sqlite3
sys.path.insert(0, str(Path(__file__).resolve().parent))
from lib.categories import WEAPON_CATS
from lib.flavor import assemble_desc, is_living, plural
from lib.items import load_items
from lib.nxd import encode_sqlite_to_nxd, deploy_nxd
from lib.paths import ROOT, MOD_ITEM_NXD

SQLITE = ROOT / "working" / "pilot_item.sqlite"
ENC_DIR = ROOT / "working" / "nxd_out"

# Living Weapon display scaffolding: bake a fixed 2-char name-suffix SLOT (companion paints +/+2/+3)
# and a fixed-width Kills line onto every weapon, so the in-card "it leveled up" overwrite works.
SCAFFOLD_LIVING = True
MELEE1_CATS = {"Knife", "NinjaBlade", "Sword", "KnightSword", "Katana", "Axe", "Rod", "Staff", "Flail", "Bag"}
# UiItemCategoryId = the equip-card weapon-TYPE label (item.en.nxd Item-en table). A repurposed weapon
# keeps its BASE slot's type id, so the card mislabels it (e.g. a KnightSword on the Giant's Axe slot
# reads "Axe"). Patch it to match the categoryOverride so the card reads right. Ids dumped from vanilla.
UICAT = {"Knife": 1, "NinjaBlade": 2, "Sword": 3, "KnightSword": 4, "Katana": 5, "Axe": 6,
         "Rod": 7, "Staff": 8, "Flail": 9, "Gun": 10, "Crossbow": 11, "Bow": 12, "Instrument": 13,
         "Book": 14, "Polearm": 15, "Pole": 16, "Bag": 17, "Cloth": 18}
# Weapon menu order = SortOrder, whose hundreds digit groups by type. Repurposing items in-place left them
# with their OLD slot's value (a Sword stuck in the knight-sword range, etc.). We regenerate SortOrder for
# every weapon so it groups with its ACTUAL type. GROUP_RANK = the base nxd's vanilla type order, keyed by
# UiItemCategoryId -> hundreds (derived from the dominant SortOrder//100 per category in the stock data).
GROUP_RANK = {1: 1, 3: 2, 4: 3, 12: 4, 11: 5, 8: 6, 7: 7, 10: 8, 14: 9, 16: 10,
              6: 11, 15: 12, 5: 13, 2: 14, 9: 15, 13: 16, 18: 17, 17: 18}

# rider_text / mechanics / flavor / plural -- the deterministic description bake -- moved to
# lib/flavor.py so analyze.py (the CI gate) and gen_living_weapon_meta.py import them from a
# library instead of from this deploy script.

# (Offensive Chemist grenade renames for ids 246-250 removed 2026-07-04 -- the feature was cut;
# those ids revert to their vanilla cure-consumable names via the pristine base table.)


def main():
    dry = "--dry" in sys.argv
    doc = load_items()
    named = [it for it in doc["items"] if it.get("name") and it["name"] != "TBD"]
    # Regroup weapon SortOrder by ACTUAL type (fixes repurposed-in-place scatter). Within a type, order by
    # (tier, id) for a clean weak->strong progression. Non-weapons keep their stock SortOrder.
    sort_map, by_group = {}, {}
    for it in named:
        eff = it["proposed"].get("categoryOverride") or it.get("category")
        if eff in WEAPON_CATS:
            by_group.setdefault(UICAT[eff], []).append(it)
    for uicat, items_in in by_group.items():
        rank = GROUP_RANK.get(uicat, 19)
        for i, it in enumerate(sorted(items_in, key=lambda x: (x.get("tier", 99) or 99, x["id"])), start=1):
            sort_map[it["id"]] = rank * 100 + i
    con = sqlite3.connect(SQLITE)
    n = 0
    for it in named:
        # Full card text (flavor + mechanics + range line + signature block + Kills scaffold)
        # comes from the shared assembler so analyze.py's desc-budget gate sees the exact bake.
        desc = assemble_desc(it, scaffold=SCAFFOLD_LIVING)
        clean = it["name"]
        eff_cat = it["proposed"].get("categoryOverride") or it.get("category")
        # --- Living Weapon display scaffolding (every weapon grows as it kills) ---
        # Two trailing spaces = a 2-char name-suffix SLOT the companion paints +/+2/+3 into
        # (spaces render as nothing, so tier 0 reads clean). The desc-side scaffold (the "+N
        # Ability" block and the fixed "Kills: 0   " counter line) is baked by assemble_desc
        # above -- lib/flavor.py owns that layout now so the analyze.py budget gate cannot
        # drift from the bake. is_living = the shared noGrowth/category predicate, in lockstep
        # with gen_living_weapon_meta.
        name = clean + "  " if (SCAFFOLD_LIVING and is_living(it)) else clean
        if dry:
            if it["id"] >= 11:  # show the new ones
                print(f"id{it['id']:>3} {name!r}\n      {desc!r}")
            continue
        con.execute('UPDATE "Item-en" SET Name=?, NameSingular=?, NamePlural=?, Name2=?, Description=? WHERE Key=?',
                    (name, clean.lower(), plural(clean), name, desc, it["id"]))
        # card type-label = the override category if repurposed, else the native category. Setting it for EVERY
        # weapon also auto-corrects vanilla mislabels (e.g. Birchwood Staff shipped as a KnightSword).
        if eff_cat in UICAT:
            con.execute('UPDATE "Item-en" SET UiItemCategoryId=? WHERE Key=?', (UICAT[eff_cat], it["id"]))
        if it["id"] in sort_map:
            con.execute('UPDATE "Item-en" SET SortOrder=? WHERE Key=?', (sort_map[it["id"]], it["id"]))
        n += 1
    if dry:
        con.close(); return
    # Orphan weapons not in items.json (e.g. DLC dupes like the id254 Moonblade) keep a stale SortOrder --
    # sweep any that don't match their type group to the END of that group, so none stray to the front.
    grp_max = {}
    for so in sort_map.values():
        grp_max[so // 100] = max(grp_max.get(so // 100, 0), so % 100)
    for key, uicat, so in con.execute(
            'SELECT Key, UiItemCategoryId, SortOrder FROM "Item-en" WHERE UiItemCategoryId BETWEEN 1 AND 18').fetchall():
        rank = GROUP_RANK.get(uicat)
        if key not in sort_map and rank and so // 100 != rank:
            grp_max[rank] = grp_max.get(rank, 0) + 1
            con.execute('UPDATE "Item-en" SET SortOrder=? WHERE Key=?', (rank * 100 + grp_max[rank], key))
    con.commit(); con.close()
    print(f"Patched {n} rows in {SQLITE.name}. Re-encoding to nxd...")
    out_nxd = encode_sqlite_to_nxd(SQLITE, ENC_DIR, "item.en.nxd")
    deploy_nxd(out_nxd, MOD_ITEM_NXD)
    print(f"Wrote {MOD_ITEM_NXD} ({out_nxd.stat().st_size} bytes).")


if __name__ == "__main__":
    main()
