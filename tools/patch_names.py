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
import tempfile
from pathlib import Path
import sqlite3
sys.path.insert(0, str(Path(__file__).resolve().parent))
from lib.categories import WEAPON_CATS
from lib.flavor import assemble_desc, is_living, plural
from lib.items import load_items
from lib.nxd import PAC, decode_nxd_to_sqlite, encode_sqlite_to_nxd, deploy_nxd, unpack
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


def build_sort_map(named):
    """Regroup weapon SortOrder by ACTUAL type (fixes repurposed-in-place scatter). Within a type,
    order by (tier, id) for a clean weak->strong progression. Non-weapons keep their stock
    SortOrder. Pulled out of main() so both apply_patches (the writer) and a verify caller can
    share the exact same derivation."""
    sort_map, by_group = {}, {}
    for it in named:
        eff = it["proposed"].get("categoryOverride") or it.get("category")
        if eff in WEAPON_CATS:
            by_group.setdefault(UICAT[eff], []).append(it)
    for uicat, items_in in by_group.items():
        rank = GROUP_RANK.get(uicat, 19)
        for i, it in enumerate(sorted(items_in, key=lambda x: (x.get("tier", 99) or 99, x["id"])), start=1):
            sort_map[it["id"]] = rank * 100 + i
    return sort_map


def _guarded_update(con, sql, params, what):
    """Run one UPDATE and refuse to continue unless it touched EXACTLY one row (the
    patch_ability_names.py / patch_status_names.py apply_patches shape, LW-148). A Key with no
    matching row -- a typo, an id that fell out of items.json, a stale table -- used to silently
    match zero rows here and the item kept shipping its old (often plain vanilla) text forever."""
    con.execute(sql, params)
    if con.execute('SELECT changes()').fetchone()[0] != 1:
        sys.exit(f"FAIL: {what} did not update exactly one Item-en row")


def apply_patches(con, named, sort_map):
    """Run every Item-en UPDATE for the named items, guarding each one. Returns
    intent: {(id, column): value} for every cell this run wrote, so a caller can confirm the same
    values actually round-tripped through the nxd encode/decode (verify_against_vanilla)."""
    intent = {}
    for it in named:
        # Full card text (flavor + mechanics + range line + signature block + Kills scaffold)
        # comes from the shared assembler so analyze.py's desc-budget gate sees the exact bake.
        desc = assemble_desc(it, scaffold=SCAFFOLD_LIVING)
        clean = it["name"]
        eff_cat = it["proposed"].get("categoryOverride") or it.get("category")
        # --- Living Weapon display scaffolding (every weapon grows as it kills) ---
        # Two trailing spaces = a 2-char name-suffix SLOT the companion paints +/+2/+3 into
        # (spaces render as nothing, so tier 0 reads clean). The desc-side scaffold (the FIRST
        # line's Kills tier-progress meter and the "+N Ability" block) is baked by assemble_desc
        # above -- lib/flavor.py owns that layout now so the analyze.py budget gate cannot
        # drift from the bake. is_living = the shared noGrowth/category predicate, in lockstep
        # with gen_living_weapon_meta.
        name = clean + "  " if (SCAFFOLD_LIVING and is_living(it)) else clean
        _guarded_update(con,
            'UPDATE "Item-en" SET Name=?, NameSingular=?, NamePlural=?, Name2=?, Description=? WHERE Key=?',
            (name, clean.lower(), plural(clean), name, desc, it["id"]),
            f"id{it['id']} ({clean!r}) name/description")
        for col, val in (("Name", name), ("NameSingular", clean.lower()), ("NamePlural", plural(clean)),
                         ("Name2", name), ("Description", desc)):
            intent[(it["id"], col)] = val
        # card type-label = the override category if repurposed, else the native category. Setting it for EVERY
        # weapon also auto-corrects vanilla mislabels (e.g. Birchwood Staff shipped as a KnightSword).
        if eff_cat in UICAT:
            _guarded_update(con, 'UPDATE "Item-en" SET UiItemCategoryId=? WHERE Key=?',
                            (UICAT[eff_cat], it["id"]), f"id{it['id']} ({clean!r}) UiItemCategoryId")
            intent[(it["id"], "UiItemCategoryId")] = UICAT[eff_cat]
        if it["id"] in sort_map:
            _guarded_update(con, 'UPDATE "Item-en" SET SortOrder=? WHERE Key=?',
                            (sort_map[it["id"]], it["id"]), f"id{it['id']} ({clean!r}) SortOrder")
            intent[(it["id"], "SortOrder")] = sort_map[it["id"]]
    return intent


def orphan_sweep(con, sort_map):
    """Orphan weapons not in items.json (e.g. DLC dupes like the id254 Moonblade) keep a stale
    SortOrder -- sweep any that don't match their type group to the END of that group, so none
    stray to the front. Every row here came straight off a SELECT of the same table in the same
    transaction, so its Key is guaranteed present; the guard still runs for the same reason every
    UPDATE in this file gets one (LW-148: no unguarded UPDATE, full stop)."""
    grp_max = {}
    for so in sort_map.values():
        grp_max[so // 100] = max(grp_max.get(so // 100, 0), so % 100)
    for key, uicat, so in con.execute(
            'SELECT Key, UiItemCategoryId, SortOrder FROM "Item-en" WHERE UiItemCategoryId BETWEEN 1 AND 18').fetchall():
        rank = GROUP_RANK.get(uicat)
        if key not in sort_map and rank and so // 100 != rank:
            grp_max[rank] = grp_max.get(rank, 0) + 1
            _guarded_update(con, 'UPDATE "Item-en" SET SortOrder=? WHERE Key=?',
                            (rank * 100 + grp_max[rank], key), f"orphan id{key} SortOrder sweep")


def verify_against_vanilla(built_nxd, own_intent):
    """Decode the freshly-built nxd and diff it cell-by-cell against CURRENT vanilla, refusing to
    deploy unless exactly the intended cells differ (the patch_ability_names.py / patch_status_names.py
    verify() shape, LW-148). Reuses tools/audit_nxd_bakes.py's own audit (item_intent,
    ALLOWED_ITEM_CELLS, the orphan-sweep allowance, and its LW-148 MISS check for a silently
    no-opped rename) rather than a second, weaker reimplementation of the same classification --
    the audit tool is already the thing that knows what counts as intentional for this table.
    Imported lazily (not at module load) to dodge the one real circular edge: audit_nxd_bakes.py
    itself imports GROUP_RANK/SCAFFOLD_LIVING/UICAT from this module.

    own_intent (apply_patches' own {(id, col): value} record of what THIS run tried to write) gets
    one extra, narrower check on top: the patch_ability_names.py-style final loop confirming every
    cell this run actually attempted to write landed byte-for-byte in the decoded rebuild. This is
    deliberately redundant with the vanilla-diff audit above (independent evidence beats a single
    path that could itself have a bug)."""
    import audit_nxd_bakes as audit

    with tempfile.TemporaryDirectory(prefix="patch_names_verify_") as td:
        tmp = Path(td)
        fresh_vanilla_nxd = unpack(PAC, "nxd/item.en.nxd", tmp / "pacout")
        v_cols, vanilla = audit.rows(decode_nxd_to_sqlite([fresh_vanilla_nxd], tmp, "van_item.sqlite"), "Item-en")
        _, bake = audit.rows(decode_nxd_to_sqlite([built_nxd], tmp, "bake_item.sqlite"), "Item-en")
        problems = audit.audit_table("Item-en", v_cols, vanilla, bake, audit.item_intent(),
                                     audit.ALLOWED_ITEM_CELLS, audit.ALLOWED_EXTRA_ROWS.get("Item-en", set()),
                                     full=False)
        if problems:
            sys.exit(f"FAIL: decode-verify found {problems} problem(s) against vanilla -- refusing to "
                     f"deploy. Re-run tools/audit_nxd_bakes.py for the detail.")
        landed = [(key, col, want, bake[key][col]) for (key, col), want in own_intent.items()
                 if key not in bake or col not in bake[key] or bake[key][col] != want]
        if landed:
            for key, col, want, got in landed[:20]:
                print(f"  UNLANDED: id{key} {col}: wanted {want!r}, decoded {got!r}")
            sys.exit(f"FAIL: {len(landed)} of this run's own writes did not land in the rebuilt table "
                     f"-- refusing to deploy")
    print("  verify PASS: decoded bake matches vanilla + intent exactly, and every cell this run "
          "wrote landed (see tools/audit_nxd_bakes.py for detail)")


def main():
    dry = "--dry" in sys.argv
    doc = load_items()
    named = [it for it in doc["items"] if it.get("name") and it["name"] != "TBD"]
    sort_map = build_sort_map(named)
    if dry:
        for it in named:
            desc = assemble_desc(it, scaffold=SCAFFOLD_LIVING)
            clean = it["name"]
            name = clean + "  " if (SCAFFOLD_LIVING and is_living(it)) else clean
            if it["id"] >= 11:  # show the new ones
                print(f"id{it['id']:>3} {name!r}\n      {desc!r}")
        return
    con = sqlite3.connect(SQLITE)
    intent = apply_patches(con, named, sort_map)
    orphan_sweep(con, sort_map)
    con.commit(); con.close()
    print(f"Patched {len(named)} rows in {SQLITE.name}. Re-encoding to nxd...")
    out_nxd = encode_sqlite_to_nxd(SQLITE, ENC_DIR, "item.en.nxd")
    print("Decode-verifying the built nxd against vanilla before deploy...")
    verify_against_vanilla(out_nxd, intent)
    deploy_nxd(out_nxd, MOD_ITEM_NXD)
    print(f"Wrote {MOD_ITEM_NXD} ({out_nxd.stat().st_size} bytes).")


if __name__ == "__main__":
    main()
