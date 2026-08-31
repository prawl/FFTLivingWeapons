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
  python tools/patch_names.py --selftest # the SortOrder uniqueness gate (pure; no game, no sqlite)
"""
import sys
import tempfile
from pathlib import Path
import sqlite3
sys.path.insert(0, str(Path(__file__).resolve().parent))
from lib.categories import WEAPON_CATS
from lib.flavor import assemble_desc, badge_for, is_living, plural
from lib.items import load_items
from lib.nxd import PAC, decode_nxd_to_sqlite, encode_sqlite_to_nxd, deploy_nxd, unpack
from lib.paths import ROOT, MOD_ITEM_NXD

SQLITE = ROOT / "working" / "pilot_item.sqlite"
ENC_DIR = ROOT / "working" / "nxd_out"

# Living Weapon display scaffolding (unconditional; the never-False SCAFFOLD_LIVING knob was
# deleted in LW-155): bake a fixed 2-char name-suffix SLOT (companion paints +/+2/+3) and a
# fixed-width Kills line onto every weapon, so the in-card "it leveled up" overwrite works.
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

# The weapon-category Item-en rows the BASE GAME ships that data/items.json never names: the two
# DLC swords. Key -> (UiItemCategoryId, the game's own SortOrder). We do not own these rows and
# never write them, so the numbers below are exactly what the shipped table keeps -- which means
# the regeneration above has to treat them as TAKEN and number around them. It did not, and the
# LW-351 live pass (2026-08-30) paid for it: Key 256 and the Warbrand both read 215, Key 257 and
# the Moonblade both read 216. The game rebuilds its two weapon order tables slot-by-sort-key, so
# each duplicate cost one of the pair its slot -- the Moonblade fell out of the inventory list,
# the Terrastaff out of the equip picker, and a stale entry stayed visible in each hole.
# Reproduce (needs the local install): decode the game's own nxd/item.en.nxd the way
# tools/audit_nxd_bakes.py does, then
#   SELECT Key, UiItemCategoryId, SortOrder FROM "Item-en" WHERE UiItemCategoryId BETWEEN 1 AND 18
# and drop every Key data/items.json names. Vanilla 1.5.2: 123 weapon rows, no duplicate SortOrder
# among them, and these two are the only un-named ones. check_stock_rows (below) refuses to bake
# if the installed game stops matching this list, and --selftest refuses if the regenerated keys
# ever land on one of these values again.
STOCK_WEAPON_ROWS = {256: (3, 215),   # Materia Blade+ (Sword)
                     257: (3, 216)}   # Akademy Blade  (Sword)

# rider_text / mechanics / flavor / plural -- the deterministic description bake -- moved to
# lib/flavor.py so analyze.py (the CI gate) and gen_living_weapon_meta.py import them from a
# library instead of from this deploy script.

# (Offensive Chemist grenade renames for ids 246-250 removed 2026-07-04 -- the feature was cut;
# those ids revert to their vanilla cure-consumable names via the pristine base table.)


def named_items():
    """The rows the bake writes: named, non-placeholder items of data/items.json. Shared with
    audit_nxd_bakes.py so the writer and the checker can never disagree about which rows are in
    scope (LW-156)."""
    return [it for it in load_items()["items"] if it.get("name") and it["name"] != "TBD"]


# LW-346: the extended inventory (ids 261+) has no vanilla Item-en row to UPDATE. Seed one by
# cloning a template weapon row under the new Key so the guarded rename loop below lands like any
# other item; item_intent then overwrites Name/Description/UiItemCategoryId/SortOrder. 257 (a real
# weapon row) supplies sane defaults for every column. Idempotent (guarded by row existence); the
# audit's ALLOWED_EXTRA_ROWS (lib/bake_intent.py) is derived from the same items.json rows.
EXTENDED_TEMPLATE_KEY = 257


def seed_extended_rows(con, named):
    cols = [d[0] for d in con.execute('SELECT * FROM "Item-en" LIMIT 1').description]
    tmpl = None
    seeded = []
    for it in named:
        iid = it["id"]
        if not it.get("extended") or con.execute('SELECT 1 FROM "Item-en" WHERE Key=?', (iid,)).fetchone():
            continue
        if tmpl is None:
            tmpl = con.execute('SELECT * FROM "Item-en" WHERE Key=?', (EXTENDED_TEMPLATE_KEY,)).fetchone()
            if tmpl is None:
                sys.exit(f"FAIL: template row Key={EXTENDED_TEMPLATE_KEY} missing; cannot seed extended-inventory rows")
        row = dict(zip(cols, tmpl))
        row["Key"] = iid
        qcols = ",".join('"%s"' % c for c in cols)
        con.execute(f'INSERT INTO "Item-en" ({qcols}) VALUES ({",".join("?" for _ in cols)})', [row[c] for c in cols])
        seeded.append(iid)
        print(f"  seeded extended-inventory Item-en row Key={iid} (cloned from {EXTENDED_TEMPLATE_KEY})")
    return seeded


def build_sort_map(named, stock=None):
    """Regroup weapon SortOrder by ACTUAL type (fixes repurposed-in-place scatter). Within a type,
    order by (tier, id) for a clean weak->strong progression, SKIPPING the numbers the game's own
    un-named weapon rows already hold (STOCK_WEAPON_ROWS): those rows stay exactly where the game
    put them, so the only way for every weapon row to end up on a distinct key is for ours to
    number around theirs. Non-weapons keep their stock SortOrder. Pulled out of main() so both
    apply_patches (the writer) and a verify caller can share the exact same derivation."""
    stock = STOCK_WEAPON_ROWS if stock is None else stock
    sort_map, by_group = {}, {}
    for it in named:
        eff = it["proposed"].get("categoryOverride") or it.get("category")
        if eff in WEAPON_CATS:
            by_group.setdefault(UICAT[eff], []).append(it)
    for uicat, items_in in by_group.items():
        rank = GROUP_RANK.get(uicat, 19)
        taken = {so for cat, so in stock.values() if GROUP_RANK.get(cat, 19) == rank}
        slot = 0
        for it in sorted(items_in, key=lambda x: (x.get("tier", 99) or 99, x["id"])):
            slot += 1
            while rank * 100 + slot in taken:
                slot += 1
            sort_map[it["id"]] = rank * 100 + slot
    return sort_map


def sort_order_collisions(sort_map, stock=None):
    """Every SortOrder claimed by more than one weapon row of the SHIPPED table, {value: [keys]}.
    That table is our regenerated rows (sort_map) plus the stock rows we leave alone
    (STOCK_WEAPON_ROWS), so both halves are counted here. Empty is the only healthy answer: the
    game's weapon order tables are built slot-by-sort-key, and a shared key costs one of the two
    rows its slot."""
    stock = STOCK_WEAPON_ROWS if stock is None else stock
    claims = {}
    for iid, so in sort_map.items():
        claims.setdefault(so, []).append(iid)
    for key, (_cat, so) in stock.items():
        claims.setdefault(so, []).append(key)
    return {so: sorted(keys) for so, keys in claims.items() if len(keys) > 1}


def selftest(named=None, sort_map=None):
    """Build gate (wired into tools/pipeline.ps1, so BuildLinked, Publish and CI all run it, and
    called by main() so a manual bake cannot route around it): no two weapon rows in the shipped
    item table may share a SortOrder.

    Pure derivation -- data/items.json plus STOCK_WEAPON_ROWS -- so it needs neither the game
    install nor the working sqlite, and it fails on the AUTHORING mistake (an items.json edit that
    grows a weapon group onto a stock row's number) rather than after a re-bake. It exists because
    the failure it guards is invisible from the data: everything reads fine, the bake verifies
    clean, and the loss only shows up as an item missing from an in-game list."""
    named = named_items() if named is None else named
    sort_map = build_sort_map(named) if sort_map is None else sort_map
    label = {it["id"]: it["name"] for it in named}
    clash = sort_order_collisions(sort_map)
    for so, keys in sorted(clash.items()):
        who = ", ".join(f"Key {k} ({label.get(k, 'stock row, not ours')})" for k in keys)
        print(f"  SortOrder {so} is claimed by {len(keys)} weapon rows: {who}")
    # Same failure through the other door: an extended row (ids 261+) is SEEDED by cloning
    # Key EXTENDED_TEMPLATE_KEY, so it starts life holding that row's SortOrder and only the
    # weapon branch of item_intent ever replaces it. A non-weapon extended item would therefore
    # ship a second row on the template's key without one line of this file changing.
    cloned_key = sorted(it["id"] for it in named if it.get("extended") and it["id"] not in sort_map)
    if cloned_key:
        print(f"  extended rows with no regenerated menu key: {cloned_key} -- each was cloned from "
              f"Key {EXTENDED_TEMPLATE_KEY} and would ship that row's SortOrder as a duplicate")
    overlap = sorted(set(STOCK_WEAPON_ROWS) & {it["id"] for it in named})
    if overlap:
        print(f"  STOCK_WEAPON_ROWS lists ids data/items.json also owns: {overlap}")
    if clash or overlap or cloned_key:
        sys.exit(f"FAIL: {len(clash)} duplicate weapon SortOrder(s), {len(overlap)} stock/owned id "
                 f"clash(es), {len(cloned_key)} extended row(s) on a cloned key -- the game's "
                 f"order-table rebuild drops one row per collision")
    print(f"  selftest PASS: {len(sort_map) + len(STOCK_WEAPON_ROWS)} weapon rows "
          f"({len(STOCK_WEAPON_ROWS)} of them the game's own), all on distinct SortOrder values")


def item_intent(named, sort_map=None):
    """(Key, column) -> value for EVERY cell the bake writes for the named items: THE single
    derivation (LW-156). apply_patches (the writer) drives its UPDATEs from this dict and
    audit_nxd_bakes.item_intent (the checker) classifies against the same dict, so an edit to
    the bake rules can no longer leave the checker holding a stale private copy and blaming the
    wrong file. This keeps build_sort_map's original promise (writer and verifier share the
    exact same derivation) for the whole cell set, not just SortOrder."""
    if sort_map is None:
        sort_map = build_sort_map(named)
    intent = {}
    for it in named:
        clean = it["name"]
        # --- Living Weapon display scaffolding (every weapon grows as it kills) ---
        # Two trailing spaces = a 2-char name-suffix SLOT the companion paints +/+2/+3 into
        # (spaces render as nothing, so tier 0 reads clean). The desc-side scaffold (the FIRST
        # line's Kills tier-progress meter and the "+N Ability" block) is baked by assemble_desc
        # below; lib/flavor.py owns that layout so the analyze.py budget gate cannot drift from
        # the bake. is_living = the shared noGrowth/category predicate, in lockstep with
        # gen_living_weapon_meta.
        name = clean + "  " if is_living(it) else clean
        intent[(it["id"], "Name")] = name
        intent[(it["id"], "NameSingular")] = clean.lower()
        intent[(it["id"], "NamePlural")] = plural(clean)
        intent[(it["id"], "Name2")] = name
        intent[(it["id"], "Description")] = assemble_desc(it)
        # card type-label = the override category if repurposed, else the native category. Setting
        # it for EVERY weapon also auto-corrects vanilla mislabels (e.g. Birchwood Staff shipped
        # as a KnightSword).
        eff = it["proposed"].get("categoryOverride") or it.get("category")
        if eff in UICAT:
            intent[(it["id"], "UiItemCategoryId")] = UICAT[eff]
        if it["id"] in sort_map:
            intent[(it["id"], "SortOrder")] = sort_map[it["id"]]
        # LW-352: the Special Effect badge follows the formula (lib.flavor.badge_for), written for
        # every weapon so a vanilla badge can never outlive the mechanic it advertised.
        badge = badge_for(it)
        if badge is not None:
            intent[(it["id"], "UiStatusEffectId")] = badge
    return intent


def _guarded_update(con, sql, params, what):
    """Run one UPDATE and refuse to continue unless it touched EXACTLY one row (the
    patch_ability_names.py / patch_status_names.py apply_patches shape, LW-148). A Key with no
    matching row -- a typo, an id that fell out of items.json, a stale table -- used to silently
    match zero rows here and the item kept shipping its old (often plain vanilla) text forever."""
    con.execute(sql, params)
    if con.execute('SELECT changes()').fetchone()[0] != 1:
        sys.exit(f"FAIL: {what} did not update exactly one Item-en row")


def apply_patches(con, named, intent):
    """Run every Item-en UPDATE for the named items, guarding each one. The VALUES come from
    item_intent (the shared derivation above); this function owns only the writing. Returns the
    same intent dict so a caller can confirm the values actually round-tripped through the nxd
    encode/decode (verify_against_vanilla)."""
    for it in named:
        i, clean = it["id"], it["name"]
        _guarded_update(con,
            'UPDATE "Item-en" SET Name=?, NameSingular=?, NamePlural=?, Name2=?, Description=? WHERE Key=?',
            (intent[(i, "Name")], intent[(i, "NameSingular")], intent[(i, "NamePlural")],
             intent[(i, "Name2")], intent[(i, "Description")], i),
            f"id{i} ({clean!r}) name/description")
        if (i, "UiItemCategoryId") in intent:
            _guarded_update(con, 'UPDATE "Item-en" SET UiItemCategoryId=? WHERE Key=?',
                            (intent[(i, "UiItemCategoryId")], i), f"id{i} ({clean!r}) UiItemCategoryId")
        if (i, "SortOrder") in intent:
            _guarded_update(con, 'UPDATE "Item-en" SET SortOrder=? WHERE Key=?',
                            (intent[(i, "SortOrder")], i), f"id{i} ({clean!r}) SortOrder")
        if (i, "UiStatusEffectId") in intent:
            _guarded_update(con, 'UPDATE "Item-en" SET UiStatusEffectId=? WHERE Key=?',
                            (intent[(i, "UiStatusEffectId")], i), f"id{i} ({clean!r}) UiStatusEffectId")
    return intent


def check_stock_rows(con, named_ids):
    """Refuse to bake unless the table's un-named weapon rows are EXACTLY the ones
    STOCK_WEAPON_ROWS describes, category and SortOrder both.

    This is the on-box half of the LW-351 fix. build_sort_map numbers our rows around these
    values, so a stale list is not a cosmetic problem: a game patch that adds a weapon row, or
    renumbers one of these two, would put a stock row back inside our range and cost some item its
    slot in an in-game list, with nothing else on the bake path noticing. We never write these
    rows, so their values stay the game's own and this stays idempotent (it is a read, not a
    sweep). Its predecessor swept off-group orphans to the end of their group; nothing has been
    off-group since 1.5.x, and the sweep could not have seen these two (both sit in the group they
    belong to), which is exactly how the duplicate survived to a live pass."""
    actual = {key: (cat, so) for key, cat, so in con.execute(
        'SELECT Key, UiItemCategoryId, SortOrder FROM "Item-en" WHERE UiItemCategoryId BETWEEN 1 AND 18')
        if key not in named_ids}
    if actual != STOCK_WEAPON_ROWS:
        sys.exit(f"FAIL: the item table's un-named weapon rows are {actual}, not the "
                 f"STOCK_WEAPON_ROWS {STOCK_WEAPON_ROWS} tools/patch_names.py numbers around. "
                 f"Update that table (Key -> (UiItemCategoryId, SortOrder)) from the installed "
                 f"game, re-run `python tools/patch_names.py --selftest`, then bake again.")


def verify_against_vanilla(built_nxd, own_intent):
    """Decode the freshly-built nxd and diff it cell-by-cell against CURRENT vanilla, refusing to
    deploy unless exactly the intended cells differ (the patch_ability_names.py / patch_status_names.py
    verify() shape, LW-148). Reuses tools/audit_nxd_bakes.py's own audit (item_intent,
    ALLOWED_ITEM_CELLS, and its LW-148 MISS check for a silently no-opped rename) rather than a
    second, weaker reimplementation of the same classification -- the audit tool is already the
    thing that knows what counts as intentional for this table.
    Imported lazily (not at module load) to dodge the one real circular edge: audit_nxd_bakes.py
    itself imports item_intent/named_items from this module.

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
    if "--selftest" in sys.argv:
        selftest()
        return
    dry = "--dry" in sys.argv
    named = named_items()
    sort_map = build_sort_map(named)
    intent = item_intent(named, sort_map)
    if dry:
        for it in named:
            if it["id"] >= 11:  # show the new ones
                print(f"id{it['id']:>3} {intent[(it['id'], 'Name')]!r}\n"
                      f"      {intent[(it['id'], 'Description')]!r}")
        return
    selftest(named, sort_map)   # never bake a table two weapon rows would share a menu slot in
    con = sqlite3.connect(SQLITE)
    seed_extended_rows(con, named)   # LW-346: rows for ids 261+ before the guarded UPDATEs need them
    apply_patches(con, named, intent)
    check_stock_rows(con, {it["id"] for it in named})
    con.commit(); con.close()
    print(f"Patched {len(named)} rows in {SQLITE.name}. Re-encoding to nxd...")
    out_nxd = encode_sqlite_to_nxd(SQLITE, ENC_DIR, "item.en.nxd")
    print("Decode-verifying the built nxd against vanilla before deploy...")
    verify_against_vanilla(out_nxd, intent)
    deploy_nxd(out_nxd, MOD_ITEM_NXD)
    print(f"Wrote {MOD_ITEM_NXD} ({out_nxd.stat().st_size} bytes).")


if __name__ == "__main__":
    main()
