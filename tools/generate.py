#!/usr/bin/env python
"""
Generate the modloader item tables from data/items.json (the only hand-edited source).

Emits into the deployable mod tree (mod/FFTIVC/tables/enhanced/), all sparse + complete-per-item so load
order yields a clean winner against other item mods (load AFTER them):
  - ItemWeaponData.xml      (weapons: WP/evade/element/proc/formula/range/flags)
  - ItemShieldData.xml      (shields: phys/mag evade)
  - ItemData.xml            (any item with an equipBonusId -> sets its EquipBonusId pointer)
  - ItemEquipBonusData.xml  (the NEW EquipBonus rows from _equipBonus, placed in free slots)
Plus out/names.json (handoff for the item.en.nxd rename patch).

Armor/shield riders REUSE existing vanilla EquipBonus rows where possible; only genuinely-new combos go in
the 8 free slots (0,40,74-79). See docs/DESIGN.md.
"""
import json, sys
import xml.etree.ElementTree as ET
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
from lib.categories import WEAPON_TABLE_CATS   # the 18 wieldable cats + Throwing/Bomb (table rows, no growth)
from lib.items import load_items, display_name
from lib.paths import ROOT, MOD_TABLES, MOD_EXTENDED
from xml.sax.saxutils import escape as xml_escape

ITEMS = Path(sys.argv[1]) if len(sys.argv) > 1 else None   # default: data/items.json (lib.paths)
OUT = Path(sys.argv[2]) if len(sys.argv) > 2 else ROOT / "out"

SHIELD_CATEGORIES = {"Shield"}
SHIELD_DATA_BASE = 128  # shield item id 128 -> ItemShieldData row 0
ARMOR_CATEGORIES = {"Helmet", "Hat", "HairAdornment", "Armor", "Clothing", "Robe"}      # -> ItemArmorData (HP/MP)
ACCESSORY_CATEGORIES = {"Shoes", "Armguard", "Ring", "Armlet", "Cloak", "Perfume"}      # -> ItemAccessoryData (evade)
# AdditionalDataId per global item id; maps each item to its type-table row. Sourced from the committed
# data/additional_data_ids.json (so generate.py is self-contained on CI / any checkout); falls back to the
# decode_tables.py dev ref under working/ if the committed map is absent.
_ADD_FILE = ROOT / "data" / "additional_data_ids.json"
_REF = ROOT / "working" / "ref" / "itemdata.json"
if _ADD_FILE.exists():
    ADD_DATA_ID = {int(k): v for k, v in json.loads(_ADD_FILE.read_text(encoding="utf-8")).items()}
elif _REF.exists():
    ADD_DATA_ID = {int(k): v["additionalDataId"] for k, v in json.loads(_REF.read_text(encoding="utf-8")).items()}
else:
    ADD_DATA_ID = {}
# EquipBonus row fields in canonical emit order
EB_FIELDS = ["PABonus", "MABonus", "SpeedBonus", "MoveBonus", "JumpBonus", "InnateStatus", "ImmuneStatus",
             "StartingStatus", "AbsorbElements", "NullifyElements", "HalveElements", "WeakElements",
             "StrongElements", "BoostJP"]
# Defaults for unset fields so a custom row fully REPLACES the vanilla slot (no sparse-inherited leftovers,
# e.g. row 56 silently keeping the vanilla Cursed Ring's Undead/Traitor statuses).
EB_DEFAULTS = {"PABonus": 0, "MABonus": 0, "SpeedBonus": 0, "MoveBonus": 0, "JumpBonus": 0,
               "InnateStatus": "None", "ImmuneStatus": "None", "StartingStatus": "None",
               "AbsorbElements": "None", "NullifyElements": "None", "HalveElements": "None",
               "WeakElements": "None", "StrongElements": "None", "BoostJP": "false"}


def hdr(table):
    return (f'<?xml version="1.0" encoding="utf-8"?>\n'
            f'<!-- built from data/items.json by tools/generate.py; edits get clobbered. load after other item mods. -->\n'
            f'<{table}>\n  <Version>1</Version>\n  <Entries>\n')


def name_comment(it):
    name = display_name(it)
    text = name + (f" (was {it['vanillaName']})" if name != it["vanillaName"] else "")
    # '--' is illegal inside an XML comment; the modloader SILENTLY drops the whole table
    # over it (same guard as make_jobequip.py). write_table's parse-check is the backstop.
    return text.replace("--", "-")


def write_table(path, text):
    ET.fromstring(text)  # parse-check before shipping (raises on malformed XML the modloader would silently drop)
    path.write_text(text, encoding="utf-8")


def remove_stale(path):
    """LW-148: a conditionally-emitted table (shields/armor/accessories/ItemData/EquipBonus) used
    to just skip writing when its source set went empty, silently leaving a PRIOR run's XML in
    place to keep deploying forever. If the set is empty this run, the table has nothing to say and
    the stale file must go instead. A no-op (no print) when there was nothing to remove."""
    if path.exists():
        path.unlink()
        print(f"  removed {path.name} (empty set): this is an UNSHIPPABLE state until the "
              f"tools/pipeline.ps1 $RequiredModFiles row for {path.name} is consciously retired, "
              f"since the next deploy/package will otherwise fail with a misleading "
              f"'{path.name} missing' error instead of naming this as the real cause")


def weapon_entry(it):
    # Sparse: omit AttackFlags (preserve vanilla TwoSwords/Throwable/TwoHands) unless a design explicitly sets it.
    s = it["proposed"]
    oai = s.get("onHitAbilityId", 0) or 0
    if oai > 255:  # OptionsAbilityId is a BYTE. A value >255 makes the modloader throw YAXBadlyFormedInput and
        # SILENTLY reject the ENTIRE ItemWeaponData file (every weapon reverts to vanilla). ET.parse won't catch it.
        raise SystemExit(f"item {it['id']} ({it.get('name')}): onHitAbilityId={oai} > 255 -- OptionsAbilityId is a byte; "
                         f">255 silently kills the whole ItemWeaponData table. Use an ability id <= 255.")
    flags = f"      <AttackFlags>{s['attackFlags']}</AttackFlags>\n" if s.get("attackFlags") else ""
    return (f"    <ItemWeapon>\n      <Id>{it['id']}</Id> <!-- {name_comment(it)} -->\n"
            f"      <Range>{s.get('range', 1)}</Range>\n{flags}"
            f"      <Formula>{s.get('formula', 1)}</Formula>\n"
            f"      <Power>{s['wp']}</Power>\n      <Evasion>{s['evade']}</Evasion>\n"
            f"      <Elements>{s.get('element', 'None')}</Elements>\n      <OptionsAbilityId>{s.get('onHitAbilityId', 0)}</OptionsAbilityId>\n    </ItemWeapon>\n")


def shield_entry(it):
    s = it["proposed"]
    return (f"    <ItemShield>\n      <Id>{it['id'] - SHIELD_DATA_BASE}</Id> <!-- {name_comment(it)} -->\n"
            f"      <PhysicalEvasion>{s['physEv']}</PhysicalEvasion>\n      <MagicalEvasion>{s['magEv']}</MagicalEvasion>\n    </ItemShield>\n")


def _add_id(it):
    aid = ADD_DATA_ID.get(it["id"])
    if aid is None:
        raise SystemExit(f"No AdditionalDataId for item {it['id']} ({it.get('name')}). Run tools/decode_tables.py first.")
    return aid


def armor_entry(it):
    s = it["proposed"]
    return (f"    <ItemArmor>\n      <Id>{_add_id(it)}</Id> <!-- {name_comment(it)} -->\n"
            f"      <HPBonus>{s.get('hp', 0)}</HPBonus>\n      <MPBonus>{s.get('mp', 0)}</MPBonus>\n    </ItemArmor>\n")


def accessory_entry(it):
    s = it["proposed"]
    return (f"    <ItemAccessory>\n      <Id>{_add_id(it)}</Id> <!-- {name_comment(it)} -->\n"
            f"      <PhysicalEvasion>{s.get('physEv', 0)}</PhysicalEvasion>\n      <MagicalEvasion>{s.get('magEv', 0)}</MagicalEvasion>\n    </ItemAccessory>\n")


# Sparse ItemData overrides for consumable items NOT in items.json (shop timing / price). Empty
# since the Offensive Chemist grenades were removed 2026-07-04; kept as the seam for any future
# consumable override. ShopAvailability + Price are both honored here via cell-level merge (the
# ItemData template carries <Price> for all 261 rows; there is no separate item.nxd Price table).
EXTRA_ITEMDATA = {}


def extra_itemdata_entry(iid, fields):
    body = f"      <ShopAvailability>{fields['shop']}</ShopAvailability>\n" if fields.get("shop") else ""
    if fields.get("price"):
        body += f"      <Price>{fields['price']}</Price>\n"
    return f"    <Item>\n      <Id>{iid}</Id>\n{body}    </Item>\n"


def itemdata_entry(it):
    s = it["proposed"]
    body = ""
    # SpriteID (ItemData byte 0x01) is the MENU ICON graphic. It does NOT pick the weapon
    # a unit swings in battle, and an earlier version of this comment said it did.
    # Disproven twice, independently:
    #   2026-06-26, spriteIdOverride on the retyped weapons was a confirmed live no-op.
    #   2026-08-19, the field was rewritten live from a sword to an axe with the write
    #   visible in the loader log, and the swung weapon never changed shape (ledger row
    #   [weapon-palette-assignment-walled], lever 2).
    # The battle swing model is welded to the ITEM ID through the live combat struct
    # (CWeapon +0x20), which is the same field that drives the damage maths, so there is no
    # art-only data lever. Consequence for the seven retyped weapons (48/49/50 axes and
    # 67/68/69/70 flails, the only categoryOverride rows in items.json): they keep swinging
    # their vanilla axe and flail art despite equipping and fighting as swords, knives,
    # katana and poles. That is ACCEPTED and harmless, not a bug to fix here, and it is NOT
    # fixable by setting SpriteID. Do not re-add spriteIdOverride to chase it; zero rows use
    # it today and that is deliberate.
    # 0 is a valid id (Nothing Equipped) -> test "is not None".
    if s.get("spriteIdOverride") is not None:
        body += f"      <SpriteID>{s['spriteIdOverride']}</SpriteID>\n"
    if "equipBonusId" in s:
        body += f"      <EquipBonusId>{s['equipBonusId']}</EquipBonusId>\n"
    if s.get("categoryOverride"):
        body += f"      <ItemCategory>{s['categoryOverride']}</ItemCategory>\n"
    if s.get("typeFlagsOverride"):
        body += f"      <TypeFlags>{s['typeFlagsOverride']}</TypeFlags>\n"
    if s.get("shopOverride"):
        body += f"      <ShopAvailability>{s['shopOverride']}</ShopAvailability>\n"
    return f"    <Item>\n      <Id>{it['id']}</Id> <!-- {name_comment(it)} -->\n{body}    </Item>\n"


def equipbonus_entry(eid, fields):
    body = "".join(f"      <{f}>{fields.get(f, EB_DEFAULTS[f])}</{f}>\n" for f in EB_FIELDS)
    return f"    <ItemEquipBonus>\n      <Id>{eid}</Id>\n{body}    </ItemEquipBonus>\n"


# --- LW-346: the extended inventory (ids 261+) ---------------------------------------------------
# An `extended` block on an items.json row marks an item the GAME has no slot for: LivingWeapon.dll
# builds its catalog record and weapon-stat row in memory at boot and redirects the game's per-item
# accessors to per-item DONORS (docs/research/ITEM_CAP_261_BREAK_JOURNEY.md, the 2026-08-27 03:20
# port blueprint). The three files below carry the modloader's OWN row names (Item / ItemWeapon
# field for field, so anyone who can edit a vanilla row can author one of ours) plus one small
# ItemExtended block for what no vanilla table can express. They are written to mod/extended_inventory/
# and read by the DLL, never by the modloader: a 261-row XML under FFTIVC/tables is silently dropped.
EXTENDED_FIRST_ID = 261
EXTENDED_LAST_ID = 511
# ITEM_SHOPS_DATA.ShopFlags names (fftivc.utility.modloader.Interfaces/Tables/Structures/ITEM_SHOPS_DATA.cs).
EXTENDED_SHOP_NAMES = {"None", "Gollund", "Dorter", "Zaland", "Goug", "Warjilis", "Bervenia", "SalGhidos", "Unused",
                       "Lesalia", "Riovanes", "Eagrose", "Lionel", "Limberry", "Zeltennia", "Gariland", "Yardrow"}


def hdr_ext(table):
    return (f'<?xml version="1.0" encoding="utf-8"?>\n'
            f'<!-- built from data/items.json by tools/generate.py; edits get clobbered. Read by LivingWeapon.dll at boot (the extended inventory, ids 261+), NOT by the modloader. -->\n'
            f'<{table}>\n  <Version>1</Version>\n  <Entries>\n')


# LW-351 fix round 6: an extended row has no vanilla row to inherit its AttackFlags from, so the
# author types them, and the first moved design (the Terrastaff, an Axe design turned Pole) shipped
# with the flags its OLD slot lent it. The game read that fine on the card and refused nothing, but
# the flags decide how the weapon reaches its target (its DELIVERY class), so a Pole carrying the
# axe's Striking is a mis-authored row even when it happens to work. The rule: a moved design
# authors its NEW category's vanilla grammar. The table below is each weapon category's delivery
# flag as the game ships it (read from the modloader's vanilla TableData/ItemWeaponData.xml on
# 2026-08-30: every vanilla row of a category carries exactly one of these four); the grip flags
# (Throwable / TwoHands / TwoSwords / ForcedTwoHands) are the designer's call and are not gated.
EXTENDED_DELIVERY_FLAGS = ("Striking", "Lunging", "Direct", "Arc")
CATEGORY_DELIVERY = {
    "Knife": "Striking", "NinjaBlade": "Striking", "Sword": "Striking", "KnightSword": "Striking",
    "Katana": "Striking", "Axe": "Striking", "Rod": "Striking", "Staff": "Striking", "Flail": "Striking",
    "Bag": "Striking", "Gun": "Direct", "Crossbow": "Direct", "Instrument": "Direct", "Book": "Direct",
    "Throwing": "Direct", "Bomb": "Direct", "Bow": "Arc", "Polearm": "Lunging", "Pole": "Lunging",
    "Cloth": "Lunging",
}
# Owner-ruled exceptions: id -> the delivery flag that row may carry instead of its category's.
# Empty today; a row goes here only with the owner's ruling cited beside it, never to silence the gate.
EXTENDED_DELIVERY_EXCEPTIONS = {}


# LW-351 stage-1 close (owner ruling 2026-08-30): an extended weapon plays at its CLONE DONOR's
# reach, not at the reach typed on its own row. The Terrastaff shipped with `range: 1` (the plan's
# C17 ruling, meant to keep the Ironreed Pole alive) and the owner watched it strike two tiles away
# on the round-6 build; the weapon-stat stub does hand the game a row whose byte 0 reads 1, so the
# targeting code takes its reach from somewhere else: either the donor's own row through the
# sibling accessors (donor 108 Ironreed Pole = 2) or the Lunging delivery class itself (every
# vanilla Lunging category is range 2, so the two cannot be told apart from that observation; the
# mechanism is Backlog row LW-364). The operational rule is settled: the row must record the number
# the game will play, i.e. the donor's shipped range (the donor's own items.json `proposed.range`,
# which is what mod/FFTIVC/tables/enhanced/ItemWeaponData.xml emits for it). A row that says
# otherwise lies to analyze.py's dominance math and to anyone reading the data.
# Owner-ruled exceptions: extended id -> the range the owner allows that row to record instead.
# Empty today; an entry needs the owner's ruling cited beside it, never a silent way past the gate.
EXTENDED_RANGE_EXCEPTIONS = {}


def check_extended_range(it, all_items):
    """Refuse an extended weapon row whose proposed.range differs from its clone donor's shipped range."""
    i, s = it["id"], it["proposed"]
    donor_id = it["extended"]["cloneDonor"]
    donor = next(d for d in all_items if d["id"] == donor_id)
    donor_range = donor["proposed"].get("range")
    if donor_range is None:
        raise SystemExit(f"extended inventory: id {i} ({it.get('name')}) clone donor {donor_id} "
                         f"({donor.get('name')}) has no proposed.range to inherit reach from")
    want = EXTENDED_RANGE_EXCEPTIONS.get(i, donor_range)
    if s.get("range") != want:
        raise SystemExit(f"extended inventory: id {i} ({it.get('name')}) records range {s.get('range')!r} but the "
                         f"game plays an extended weapon at its clone donor's reach, and donor {donor_id} "
                         f"({donor.get('name')}) ships range {donor_range}. Record {want} so the row tells the "
                         f"truth (the reach cannot be changed by this number; pick a donor with the reach "
                         f"you want, or the owner rules an EXTENDED_RANGE_EXCEPTIONS entry).")


def check_extended_flag_grammar(it):
    """Refuse an extended weapon row whose delivery flag is not its category's vanilla one."""
    i, cat = it["id"], it["category"]
    tokens = {t.strip() for t in str(it["proposed"]["attackFlags"]).split(",") if t.strip()}
    delivery = sorted(t for t in tokens if t in EXTENDED_DELIVERY_FLAGS)
    want = EXTENDED_DELIVERY_EXCEPTIONS.get(i, CATEGORY_DELIVERY.get(cat))
    if want is None:
        raise SystemExit(f"extended inventory: id {i} ({it.get('name')}) category {cat!r} has no delivery-flag "
                         f"grammar in CATEGORY_DELIVERY; add it from the vanilla ItemWeaponData rows")
    if delivery != [want]:
        raise SystemExit(f"extended inventory: id {i} ({it.get('name')}) is a {cat} but its attackFlags "
                         f"{it['proposed']['attackFlags']!r} carry delivery {delivery or 'none'}; every vanilla "
                         f"{cat} row carries {want!r}. A moved design authors its NEW category's grammar "
                         f"(delivery flag + grip), not the flags its old slot lent it. If this row is a "
                         f"deliberate exception, the owner rules it into EXTENDED_DELIVERY_EXCEPTIONS.")


def validate_extended(ext, all_items):
    """Loud, offline validation of the extended rows so a bad row fails generate.py, not the game.
    Ids must be contiguous from 261 (the DLL's donor tables are indexed by id - 261 and a gap would
    read a neighbour's donor); V1 is weapons only; every donor must be a real vanilla-range item."""
    ids = sorted(it["id"] for it in ext)
    if ids != list(range(EXTENDED_FIRST_ID, EXTENDED_FIRST_ID + len(ids))):
        raise SystemExit(f"extended inventory: ids must be contiguous from {EXTENDED_FIRST_ID}, got {ids}")
    if ids and ids[-1] > EXTENDED_LAST_ID:
        raise SystemExit(f"extended inventory: id {ids[-1]} is past the accessor mask's last slot {EXTENDED_LAST_ID}")
    known = {it["id"] for it in all_items if it["id"] < 256}
    for it in ext:
        e, s, i = it["extended"], it["proposed"], it["id"]
        if it["category"] not in WEAPON_TABLE_CATS:
            raise SystemExit(f"extended inventory: id {i} ({it.get('name')}) category {it['category']!r} is not a weapon (V1 is weapons only)")
        for key in ("cloneDonor", "artDonor"):
            d = e.get(key, e.get("cloneDonor"))
            if not isinstance(d, int) or d < 1 or d > 255 or d not in known:
                raise SystemExit(f"extended inventory: id {i} ({it.get('name')}) {key}={d!r} must name a vanilla-range item (1..255) present in items.json")
        if not s.get("attackFlags"):
            raise SystemExit(f"extended inventory: id {i} ({it.get('name')}) needs proposed.attackFlags (no vanilla row to inherit them from)")
        check_extended_flag_grammar(it)
        check_extended_range(it, all_items)
        # LW-354: the towns whose shop stocks it, in the modloader's own ItemShopsData names.
        for tok in [x.strip() for x in str(e.get("shops", "None")).split(",") if x.strip()]:
            if tok not in EXTENDED_SHOP_NAMES:
                raise SystemExit(f"extended inventory: id {i} ({it.get('name')}) shops token {tok!r} is not a town "
                                 f"(vocabulary: {', '.join(sorted(EXTENDED_SHOP_NAMES))})")
        if not it.get("name") or it["name"] == "TBD":
            raise SystemExit(f"extended inventory: id {i} needs a real name (it is the item.en.nxd row too)")
        # The menu icon is a .tex plus a .utexpt parts file per surface; a vanilla id gets its parts
        # files from the game's pac, a new id ships all four (tools/recolor_icons.py <id> for the
        # pictures, tools/bake_extended_icon_parts.py for the parts; owner-observed 2026-08-26).
        icon = ROOT / "mod" / "FFTIVC" / "data" / "enhanced" / "ui" / "ffto" / "icon"
        for sub, pfx in (("equip_item", "ei"), ("equip_item_s", "ei_s")):
            for rel in (f"{sub}/texture/{pfx}_{i:03d}_uitx.tex", f"{sub}/textureparts/{pfx}_{i:03d}_uitx.utexpt"):
                if not (icon / rel).exists():
                    raise SystemExit(f"extended inventory: id {i} ({it.get('name')}) is missing its icon file {rel}; "
                                     f"run tools/recolor_icons.py {i} and tools/bake_extended_icon_parts.py")


def extended_itemdata_entry(it):
    e, s = it["extended"], it["proposed"]
    return (f"    <Item>\n      <Id>{it['id']}</Id> <!-- {name_comment(it)} -->\n"
            f"      <Palette>{e.get('palette', 0)}</Palette>\n"
            f"      <SpriteID>{e.get('spriteId', 0)}</SpriteID>\n"
            f"      <RequiredLevel>{e.get('requiredLevel', 0)}</RequiredLevel>\n"
            f"      <TypeFlags>{e.get('typeFlags', 'Weapon')}</TypeFlags>\n"
            f"      <AdditionalDataId>{e['cloneDonor']}</AdditionalDataId>\n"
            f"      <ItemCategory>{it['category']}</ItemCategory>\n"
            f"      <EquipBonusId>{s.get('equipBonusId', 0)}</EquipBonusId>\n"
            f"      <Price>{e.get('price', 10)}</Price>\n"
            f"      <ShopAvailability>{e.get('shopAvailability', 'Blank')}</ShopAvailability>\n    </Item>\n")


def extended_entry(it):
    e = it["extended"]
    return (f"    <ItemExtended>\n      <Id>{it['id']}</Id> <!-- {name_comment(it)} -->\n"
            f"      <Name>{xml_escape(display_name(it))}</Name>\n"
            f"      <CloneDonorId>{e['cloneDonor']}</CloneDonorId>\n"
            f"      <ArtDonorId>{e.get('artDonor', e['cloneDonor'])}</ArtDonorId>\n"
            f"      <SeedCount>{e.get('seedCount', 0)}</SeedCount>\n"
            f"      <Shops>{e.get('shops', 'None')}</Shops>\n    </ItemExtended>\n")


def write_extended_tables(ext, wrote):
    """Always writes all three files (empty Entries when there are no extended rows) so the
    required-file manifest stays stable and the DLL's loader can tell 'no rows' from 'no file'."""
    MOD_EXTENDED.mkdir(parents=True, exist_ok=True)
    write_table(MOD_EXTENDED / "ItemData.xml",
                hdr_ext("ItemTable") + "".join(extended_itemdata_entry(it) for it in ext) + "  </Entries>\n</ItemTable>\n")
    write_table(MOD_EXTENDED / "ItemWeaponData.xml",
                hdr_ext("ItemWeaponTable") + "".join(weapon_entry(it) for it in ext) + "  </Entries>\n</ItemWeaponTable>\n")
    write_table(MOD_EXTENDED / "ItemExtendedData.xml",
                hdr_ext("ItemExtendedTable") + "".join(extended_entry(it) for it in ext) + "  </Entries>\n</ItemExtendedTable>\n")
    wrote.append(f"extended_inventory/ItemData.xml + ItemWeaponData.xml + ItemExtendedData.xml ({len(ext)} extended items: {[it['id'] for it in ext]})")


def main():
    doc = load_items(ITEMS)
    all_items = sorted(doc["items"], key=lambda x: x["id"])
    new_eb = doc.get("_equipBonus", {})
    OUT.mkdir(parents=True, exist_ok=True)
    MOD_TABLES.mkdir(parents=True, exist_ok=True)
    wrote = []

    # LW-346: extended-inventory rows (ids 261+) never reach the modloader tables (a row past the
    # 261 cap is dropped at best and kills the whole table at worst); they get their own files.
    extended = [it for it in all_items if it.get("extended")]
    validate_extended(extended, all_items)
    write_extended_tables(extended, wrote)
    items = [it for it in all_items if not it.get("extended")]

    weapons = [it for it in items if it["category"] in WEAPON_TABLE_CATS]
    write_table(MOD_TABLES / "ItemWeaponData.xml",
                hdr("ItemWeaponTable") + "".join(weapon_entry(it) for it in weapons) + "  </Entries>\n</ItemWeaponTable>\n")
    wrote.append(f"ItemWeaponData.xml ({len(weapons)} weapons)")

    shields = [it for it in items if it["category"] in SHIELD_CATEGORIES]
    shield_path = MOD_TABLES / "ItemShieldData.xml"
    if shields:
        write_table(shield_path,
                    hdr("ItemShieldTable") + "".join(shield_entry(it) for it in shields) + "  </Entries>\n</ItemShieldTable>\n")
        wrote.append(f"ItemShieldData.xml ({len(shields)} shields)")
    else:
        remove_stale(shield_path)

    armor = [it for it in items if it["category"] in ARMOR_CATEGORIES]
    armor_path = MOD_TABLES / "ItemArmorData.xml"
    if armor:
        write_table(armor_path,
                    hdr("ItemArmorTable") + "".join(armor_entry(it) for it in armor) + "  </Entries>\n</ItemArmorTable>\n")
        wrote.append(f"ItemArmorData.xml ({len(armor)} armor)")
    else:
        remove_stale(armor_path)

    accessories = [it for it in items if it["category"] in ACCESSORY_CATEGORIES]
    accessory_path = MOD_TABLES / "ItemAccessoryData.xml"
    if accessories:
        write_table(accessory_path,
                    hdr("ItemAccessoryTable") + "".join(accessory_entry(it) for it in accessories) + "  </Entries>\n</ItemAccessoryTable>\n")
        wrote.append(f"ItemAccessoryData.xml ({len(accessories)} accessories)")
    else:
        remove_stale(accessory_path)

    # ItemData: every item that sets an equipBonusId (shields + any weapon w/ a rider, e.g. Arcanum MA+2)
    data_items = [it for it in items if "equipBonusId" in it["proposed"] or it["proposed"].get("categoryOverride") or it["proposed"].get("typeFlagsOverride") or it["proposed"].get("shopOverride") or it["proposed"].get("spriteIdOverride") is not None]
    itemdata_path = MOD_TABLES / "ItemData.xml"
    if data_items or EXTRA_ITEMDATA:
        body = "".join(itemdata_entry(it) for it in data_items)
        body += "".join(extra_itemdata_entry(i, f) for i, f in sorted(EXTRA_ITEMDATA.items()))
        write_table(itemdata_path,
                    hdr("ItemTable") + body + "  </Entries>\n</ItemTable>\n")
        wrote.append(f"ItemData.xml ({len(data_items)} entries + {len(EXTRA_ITEMDATA)} consumable shop overrides)")
    else:
        remove_stale(itemdata_path)

    equipbonus_path = MOD_TABLES / "ItemEquipBonusData.xml"
    if new_eb:
        rows = "".join(equipbonus_entry(int(k), v) for k, v in sorted(new_eb.items(), key=lambda kv: int(kv[0])))
        write_table(equipbonus_path,
                    hdr("ItemEquipBonusTable") + rows + "  </Entries>\n</ItemEquipBonusTable>\n")
        wrote.append(f"ItemEquipBonusData.xml ({len(new_eb)} new rows: {sorted(int(k) for k in new_eb)})")
    else:
        remove_stale(equipbonus_path)

    names = {str(it["id"]): {"name": it.get("name"), "vanillaName": it["vanillaName"]}
             for it in all_items if it.get("name") not in (None, "TBD")}   # extended rows ride the rename handoff too
    (OUT / "names.json").write_text(json.dumps(names, indent=2, ensure_ascii=False), encoding="utf-8")

    for w in wrote:
        print("  wrote " + w)


if __name__ == "__main__":
    main()
