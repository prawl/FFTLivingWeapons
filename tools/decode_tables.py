#!/usr/bin/env python
"""
Decode the modloader's full vanilla template tables into JSON references under working/ref/.
These are the authoritative baseline for armor/accessory/equipbonus builds.

Outputs:
  working/ref/itemdata.json   id -> {name, typeFlags, category, additionalDataId, equipBonusId, price, shop}
  working/ref/equipbonus.json id -> {raw fields} (0..84)
  working/ref/armor.json      additionalDataId -> {hp, mp, name}      (ItemArmorData, 0..63)
  working/ref/accessory.json  additionalDataId -> {physEv, magEv, name} (ItemAccessoryData, 0..31)

Also prints a report: the EquipBonus effect index, which EB ids are REFERENCED by a vanilla
item (=> not free) and which are FREE to repurpose, so riders can be snapped to existing rows.
"""
import json, re, sys
import xml.etree.ElementTree as ET
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
from lib.paths import ROOT, TABLE_DATA

TPL = TABLE_DATA
REF = ROOT / "working" / "ref"

EB_NUM = ["PABonus", "MABonus", "SpeedBonus", "MoveBonus", "JumpBonus"]
EB_STATUS = ["InnateStatus", "ImmuneStatus", "StartingStatus"]
EB_ELEM = ["AbsorbElements", "NullifyElements", "HalveElements", "WeakElements", "StrongElements"]


def _text(node, tag, default=""):
    el = node.find(tag)
    return el.text.strip() if el is not None and el.text else default


def parse_eb():
    """id -> raw dict (all 14 fields)."""
    root = ET.parse(TPL / "ItemEquipBonusData.xml").getroot()
    rows = {}
    for n in root.iter("ItemEquipBonus"):
        i = int(_text(n, "Id"))
        row = {}
        for f in EB_NUM:
            row[f] = int(_text(n, f, "0"))
        for f in EB_STATUS + EB_ELEM:
            row[f] = _text(n, f, "None")
        row["BoostJP"] = _text(n, "BoostJP", "false").lower() == "true"
        rows[i] = row
    return rows


def parse_itemdata():
    root = ET.parse(TPL / "ItemData.xml").getroot()
    items = {}
    for n in root.iter("Item"):
        i = int(_text(n, "Id"))
        # name from the leading comment is not in ET; pull via regex fallback later. Use category/flags here.
        items[i] = {
            "typeFlags": _text(n, "TypeFlags"),
            "category": _text(n, "ItemCategory"),
            "additionalDataId": int(_text(n, "AdditionalDataId", "0")),
            "equipBonusId": int(_text(n, "EquipBonusId", "0")),
            "price": int(_text(n, "Price", "0")),
            "shop": _text(n, "ShopAvailability"),
        }
    # names live in XML comments <!-- Name / jp / fr / de -->; grab the English part per Id
    txt = (TPL / "ItemData.xml").read_text(encoding="utf-8")
    for m in re.finditer(r"<Id>(\d+)</Id>\s*<!--\s*([^/]+?)\s*/", txt):
        i = int(m.group(1))
        if i in items:
            items[i]["name"] = m.group(2).strip()
    return items


def parse_simple(fname, tag, fields):
    root = ET.parse(TPL / fname).getroot()
    out = {}
    for n in root.iter(tag):
        i = int(_text(n, "Id"))
        out[i] = {k: int(_text(n, v, "0")) for k, v in fields.items()}
    txt = (TPL / fname).read_text(encoding="utf-8")
    for m in re.finditer(r"<Id>(\d+)</Id>\s*<!--\s*([^/]+?)\s*/", txt):
        i = int(m.group(1))
        if i in out:
            out[i]["name"] = m.group(2).strip()
    return out


def main():
    REF.mkdir(parents=True, exist_ok=True)
    eb = parse_eb()
    items = parse_itemdata()
    armor = parse_simple("ItemArmorData.xml", "ItemArmor", {"hp": "HPBonus", "mp": "MPBonus"})
    acc = parse_simple("ItemAccessoryData.xml", "ItemAccessory", {"physEv": "PhysicalEvasion", "magEv": "MagicalEvasion"})

    (REF / "equipbonus.json").write_text(json.dumps(eb, indent=1), encoding="utf-8")
    (REF / "itemdata.json").write_text(json.dumps(items, indent=1), encoding="utf-8")
    (REF / "armor.json").write_text(json.dumps(armor, indent=1), encoding="utf-8")
    (REF / "accessory.json").write_text(json.dumps(acc, indent=1), encoding="utf-8")

    referenced = sorted({it["equipBonusId"] for it in items.values()})
    free = [i for i in range(85) if i not in referenced]

    def summ(row):
        parts = []
        for f in EB_NUM:
            if row[f]:
                parts.append(f.replace("Bonus", "") + "+" + str(row[f]))
        for f in EB_STATUS:
            if row[f] != "None":
                parts.append(f.replace("Status", "").lower() + ":" + row[f])
        for f in EB_ELEM:
            if row[f] != "None":
                parts.append(f.replace("Elements", "").lower() + "(" + row[f] + ")")
        if row.get("BoostJP"):
            parts.append("BoostJP")
        return ", ".join(parts) if parts else "(empty)"

    print("=== EquipBonus index (85 rows) ===")
    for i in sorted(eb):
        ref = "REF" if i in referenced else "free"
        print(f"  {i:2d} [{ref}] {summ(eb[i])}")
    print(f"\nReferenced by a vanilla item (NOT free): {referenced}")
    print(f"FREE EquipBonus slots (repurposable): {free}  ({len(free)} total)")
    print(f"\nParsed: {len(items)} items, {len(armor)} armor rows, {len(acc)} accessory rows, {len(eb)} EB rows.")
    print(f"Refs written to {REF}")


if __name__ == "__main__":
    main()
