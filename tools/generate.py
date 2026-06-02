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
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
ITEMS = Path(sys.argv[1]) if len(sys.argv) > 1 else ROOT / "data" / "items.json"
MOD_TABLES = ROOT / "mod" / "FFTIVC" / "tables" / "enhanced"
OUT = Path(sys.argv[2]) if len(sys.argv) > 2 else ROOT / "out"

WEAPON_CATEGORIES = {"Knife", "NinjaBlade", "Sword", "KnightSword", "Katana", "Axe", "Rod", "Staff",
                     "Flail", "Gun", "Crossbow", "Bow", "Instrument", "Book", "Polearm", "Pole", "Bag", "Cloth"}
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
    name = it.get("name") if it.get("name") not in (None, "TBD") else it["vanillaName"]
    return name + (f" (was {it['vanillaName']})" if name != it["vanillaName"] else "")


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


# Sparse ItemData overrides for items NOT in items.json: the recycled-consumable grenades' shop
# timing + bumping Remedy to Chapter 1 (it became the only cure). ShopAvailability is honored via
# ItemData for weapons (confirmed on Sasori); whether it sticks for CONSUMABLES is being verified
# (their Price is nex-overridden). Price is NOT settable here -- it lives in the base item.nxd.
EXTRA_ITEMDATA = {
    246: {"shop": "Chapter1_Start"},  # Venom Flask  (Poison)  - early
    247: {"shop": "Chapter1_Start"},  # Smoke Bomb   (Blind)   - early
    249: {"shop": "Chapter2_Start"},  # Oil Flask    (Oil)     - earlier (situational Fire combo)
    248: {"shop": "Chapter3_Start"},  # Hush Vial    (Silence) - later (more universally useful)
    250: {"shop": "Chapter4_Start"},  # Sludge Bomb  (Slow)    - late
    252: {"shop": "Chapter1_Start"},  # Remedy: now the only cure, must be buyable in Chapter 1
}


def extra_itemdata_entry(iid, fields):
    body = f"      <ShopAvailability>{fields['shop']}</ShopAvailability>\n" if fields.get("shop") else ""
    return f"    <Item>\n      <Id>{iid}</Id>\n{body}    </Item>\n"


def itemdata_entry(it):
    s = it["proposed"]
    body = ""
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


def main():
    doc = json.loads(ITEMS.read_text(encoding="utf-8"))
    items = sorted(doc["items"], key=lambda x: x["id"])
    new_eb = doc.get("_equipBonus", {})
    OUT.mkdir(parents=True, exist_ok=True)
    MOD_TABLES.mkdir(parents=True, exist_ok=True)
    wrote = []

    weapons = [it for it in items if it["category"] in WEAPON_CATEGORIES]
    (MOD_TABLES / "ItemWeaponData.xml").write_text(
        hdr("ItemWeaponTable") + "".join(weapon_entry(it) for it in weapons) + "  </Entries>\n</ItemWeaponTable>\n", encoding="utf-8")
    wrote.append(f"ItemWeaponData.xml ({len(weapons)} weapons)")

    shields = [it for it in items if it["category"] in SHIELD_CATEGORIES]
    if shields:
        (MOD_TABLES / "ItemShieldData.xml").write_text(
            hdr("ItemShieldTable") + "".join(shield_entry(it) for it in shields) + "  </Entries>\n</ItemShieldTable>\n", encoding="utf-8")
        wrote.append(f"ItemShieldData.xml ({len(shields)} shields)")

    armor = [it for it in items if it["category"] in ARMOR_CATEGORIES]
    if armor:
        (MOD_TABLES / "ItemArmorData.xml").write_text(
            hdr("ItemArmorTable") + "".join(armor_entry(it) for it in armor) + "  </Entries>\n</ItemArmorTable>\n", encoding="utf-8")
        wrote.append(f"ItemArmorData.xml ({len(armor)} armor)")

    accessories = [it for it in items if it["category"] in ACCESSORY_CATEGORIES]
    if accessories:
        (MOD_TABLES / "ItemAccessoryData.xml").write_text(
            hdr("ItemAccessoryTable") + "".join(accessory_entry(it) for it in accessories) + "  </Entries>\n</ItemAccessoryTable>\n", encoding="utf-8")
        wrote.append(f"ItemAccessoryData.xml ({len(accessories)} accessories)")

    # ItemData: every item that sets an equipBonusId (shields + any weapon w/ a rider, e.g. Arcanum MA+2)
    data_items = [it for it in items if "equipBonusId" in it["proposed"] or it["proposed"].get("categoryOverride") or it["proposed"].get("typeFlagsOverride") or it["proposed"].get("shopOverride")]
    if data_items or EXTRA_ITEMDATA:
        body = "".join(itemdata_entry(it) for it in data_items)
        body += "".join(extra_itemdata_entry(i, f) for i, f in sorted(EXTRA_ITEMDATA.items()))
        (MOD_TABLES / "ItemData.xml").write_text(
            hdr("ItemTable") + body + "  </Entries>\n</ItemTable>\n", encoding="utf-8")
        wrote.append(f"ItemData.xml ({len(data_items)} entries + {len(EXTRA_ITEMDATA)} consumable shop overrides)")

    if new_eb:
        rows = "".join(equipbonus_entry(int(k), v) for k, v in sorted(new_eb.items(), key=lambda kv: int(kv[0])))
        (MOD_TABLES / "ItemEquipBonusData.xml").write_text(
            hdr("ItemEquipBonusTable") + rows + "  </Entries>\n</ItemEquipBonusTable>\n", encoding="utf-8")
        wrote.append(f"ItemEquipBonusData.xml ({len(new_eb)} new rows: {sorted(int(k) for k in new_eb)})")

    names = {str(it["id"]): {"name": it.get("name"), "vanillaName": it["vanillaName"]}
             for it in items if it.get("name") not in (None, "TBD")}
    (OUT / "names.json").write_text(json.dumps(names, indent=2, ensure_ascii=False), encoding="utf-8")

    for w in wrote:
        print("  wrote " + w)


if __name__ == "__main__":
    main()
