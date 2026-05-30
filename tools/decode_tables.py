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

The EquipBonus resolver (resolve_rider) is importable by other tools: given a structured rider
spec (subset of EB fields), it returns an existing EB id with that exact effect, or None.
"""
import json, re
import xml.etree.ElementTree as ET
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
TPL = Path(r"C:\program files (x86)\steam\steamapps\common\FINAL FANTASY TACTICS - The Ivalice Chronicles\Reloaded\Mods\FFTIVC_Mod_Loader\TableData")
REF = ROOT / "working" / "ref"

EB_NUM = ["PABonus", "MABonus", "SpeedBonus", "MoveBonus", "JumpBonus"]
EB_STATUS = ["InnateStatus", "ImmuneStatus", "StartingStatus"]
EB_ELEM = ["AbsorbElements", "NullifyElements", "HalveElements", "WeakElements", "StrongElements"]


def _text(node, tag, default=""):
    el = node.find(tag)
    return el.text.strip() if el is not None and el.text else default


def _setify(s):
    """'Ice, Lightning, Fire' -> frozenset({'Ice','Lightning','Fire'}); 'None'/'' -> empty."""
    if not s or s.strip().lower() == "none":
        return frozenset()
    return frozenset(p.strip() for p in s.split(",") if p.strip())


def parse_eb():
    """id -> raw dict (all 14 fields) ; plus a canonical form for matching."""
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


def canon(row):
    """Canonical comparable signature of an EB row (order-insensitive for lists)."""
    sig = tuple(row[f] for f in EB_NUM)
    statuses = tuple(_setify(row[f]) for f in EB_STATUS)
    elems = tuple(_setify(row[f]) for f in EB_ELEM)
    return (sig, statuses, elems, bool(row.get("BoostJP", False)))


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


# ---- importable resolver --------------------------------------------------
_EB_CACHE = None

def _eb():
    global _EB_CACHE
    if _EB_CACHE is None:
        _EB_CACHE = parse_eb()
    return _EB_CACHE


def resolve_rider(query):
    """query: dict with any subset of EB fields (others assumed default). Returns existing EB id or None.
    Lists may be given as 'A, B' strings or python lists/sets."""
    norm = {f: 0 for f in EB_NUM}
    for f in EB_STATUS + EB_ELEM:
        norm[f] = "None"
    norm["BoostJP"] = False
    for k, v in query.items():
        if k in EB_NUM:
            norm[k] = int(v)
        elif k in EB_STATUS + EB_ELEM:
            norm[k] = ", ".join(v) if isinstance(v, (list, set, tuple)) else str(v)
        elif k == "BoostJP":
            norm[k] = bool(v)
    target = canon(norm)
    for i, row in sorted(_eb().items()):
        if canon(row) == target:
            return i
    return None


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
