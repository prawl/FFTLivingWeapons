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
from lib.flavor import flavor, mechanics, plural
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

# Grenade renames for the 5 recycled cure consumables (ids 246-250) -- folded from the retired
# patch_grenades.py (tools/oneoff/). These ids are NOT in items.json (their behavior lives in
# ItemConsumableData.xml + generate.py's EXTRA_ITEMDATA), so before the fold the renames survived
# only because the mutated working sqlite persisted; now every rename run re-asserts the rows.
EXTRA_NAMES = {
    246: ("Venom Flask",     "Distilled toxin in a fragile vial. Inflicts Poison on the target."),
    247: ("Smoke Bomb",      "A burst of acrid smoke. Inflicts Blind on the target."),
    248: ("Hush Vial",       "A throat-stilling draught in a fragile vial. Inflicts Silence on the target."),
    249: ("Oil Flask",       "Clinging oil. Inflicts Oil on the target, doubling the Fire damage they take."),
    250: ("Sludge Bomb",     "A splash of clinging mire. Inflicts Slow on the target."),
}


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
        custom = it.get("desc")
        if custom:
            desc = custom
        else:
            # flavorOverride lets an item keep a hand-written flavor line while STILL auto-appending its
            # mechanics (so the stats stay in sync if we retune them); plain flavor() is the fallback.
            fl, mech = (it.get("flavorOverride") or flavor(it)), mechanics(it)
            desc = fl + ("\n" + mech if mech else "")
        # reach line appended uniformly (custom + generated) so every ranged weapon phrases range identically
        rng = it["proposed"].get("range", 1) or 1
        if it["category"] in WEAPON_CATS and rng >= 2:
            desc = desc.rstrip()
            if desc and not desc.endswith((".", "!", "?")):
                desc += "."
            desc += f" Strikes from up to {rng} tiles away."
        clean = it["name"]
        eff_cat = it["proposed"].get("categoryOverride") or it.get("category")
        # --- Living Weapon display scaffolding (every weapon grows as it kills) ---
        # Two trailing spaces = a 2-char name-suffix SLOT the companion paints +/+2/+3 into
        # (spaces render as nothing, so tier 0 reads clean). A fixed-width "Kills 0000" line
        # gives the per-weapon counter a stable overwrite target. Same proven loaded-string
        # overwrite the Living Blade MVP used, now armed on all 121 weapons.
        name = clean
        # noGrowth weapons (Materia Blade, the bombs) are excluded from the Living Weapon
        # system (gen_living_weapon_meta.py skips them), so they must not carry the scaffold
        # either -- a baked "Kills: 0" on a weapon the runtime never paints is a dead counter
        # lying on the card. Keep this predicate in lockstep with gen_living_weapon_meta.
        if SCAFFOLD_LIVING and eff_cat in WEAPON_CATS and not it.get("noGrowth"):
            name = clean + "  "
            # p3Desc goes BEFORE the Kills/Grant scaffolding so gameplay prose stays grouped above
            # the tracker lines (the Kills/Grant anchors key on the flavor line + literal prefixes).
            sig = it.get("signature")
            p3 = sig.get("p3Desc") if sig else None
            if p3:
                # Card section for the +N ability: a blank line, then a header that NAMES the
                # ability ("+3 Ability -- Infatuation"), then the bare effect. The name comes from
                # sigName (the curated flavor name), falling back to displayLabel (the additive
                # supports store the granted ability's own name there). The "+N" gate lives in the
                # header, so the effect line stays a clean sentence -- no "Must be equipped at +3".
                sname = sig.get("sigName") or sig.get("displayLabel", "")
                at = sig.get("atTier", 3)
                header = f"+{at} Ability — {sname}" if sname else f"+{at} Ability"
                desc = desc.rstrip() + f"\n\n{header}\n{p3}"
            # bake "Kills: 0   " (digit + 3 spaces) as the LAST line of EVERY weapon card -- the
            # counter reads as consistent UI when it always closes the card. The DLL paints
            # left-aligned variable digits into this fixed 4-char slot (KillsSlot helper); the
            # baked value must match the left-aligned pattern the slot validator accepts. The
            # "Kills: " literal MUST stay in lockstep with Display/DisplayScan + ByteScan.KillsDigits.
            # (No painted "Grant" line anymore: the baked "While this weapon is equipped at +3, ..."
            # sentence states the ability, and unpainted cards showed the slot as a bare "Grant".)
            desc = desc.rstrip() + "\n\nKills: 0   "   # blank line sets the tracker off from the description
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
    # Re-assert the grenade rows (ids 246-250) on every run -- see EXTRA_NAMES above.
    for iid, (gname, gdesc) in sorted(EXTRA_NAMES.items()):
        if dry:
            print(f"id{iid:>3} {gname!r}\n      {gdesc!r}")
            continue
        con.execute('UPDATE "Item-en" SET Name=?, NameSingular=?, NamePlural=?, Name2=?, Description=? WHERE Key=?',
                    (gname, gname.lower(), plural(gname), gname, gdesc, iid))
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
