#!/usr/bin/env python
"""Snapshot the vanilla job and item tables into data/vanilla_tables.json.

Why. make_jobequip.py and the dodge math both need the unmodded job rows, item levels and
rarity, and shield and accessory evade. The only copy is the modloader's table dump under the
Steam folder, which the build server does not have. This file is that dump, committed, with the
loader version and a hash of each source so a loader update that touches the tables shows up.

What. JobData.xml (every row, monsters too), ItemData.xml ids 0 to 255, ItemShieldData.xml and
ItemAccessoryData.xml keyed by AdditionalDataId. A job name is the first word group of the XML
comment after its Id, the way make_jobequip.py has always read it; an unnamed row keeps "?".

Usage. python tools/snapshot_vanilla_tables.py
Rerun after a modloader update that changes TableData, in the same commit as the new snapshot.
"""
import hashlib
import json
import re
import sys
import xml.etree.ElementTree as ET
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
from lib.paths import TABLE_DATA, VANILLA_TABLES

SOURCES = ("JobData.xml", "ItemData.xml", "ItemShieldData.xml", "ItemAccessoryData.xml")
LAST_ITEM_ID = 255
NAME_RE = re.compile(r"<Id>(\d+)</Id>\s*<!--\s*([^/]+?)\s*/")


def _int(node, tag):
    return int((node.findtext(tag) or "0").strip() or 0)


def _rows(folder, name, tag):
    return ET.parse(folder / name).getroot().find("Entries").findall(tag)


def _jobs(folder):
    raw = (folder / "JobData.xml").read_text(encoding="utf-8")
    names = {int(m.group(1)): m.group(2).strip() for m in NAME_RE.finditer(raw)}
    jobs = {}
    for j in _rows(folder, "JobData.xml", "Job"):
        jid = _int(j, "Id")
        equip = [c.strip() for c in (j.findtext("EquippableItems") or "").split(",") if c.strip()]
        jobs[str(jid)] = {
            "name": names.get(jid, "?"),
            "equip": equip,
            "cev": _int(j, "CharacterEvasion"),
            "innate": [_int(j, f"InnateAbilityId{i}") for i in (1, 2, 3, 4)],
            "monsterGraphic": _int(j, "MonsterGraphic"),
        }
    return jobs


def _items(folder):
    items = {}
    for it in _rows(folder, "ItemData.xml", "Item"):
        iid = _int(it, "Id")
        if iid > LAST_ITEM_ID:
            continue
        flags = [t.strip() for t in (it.findtext("TypeFlags") or "").split(",")]
        items[str(iid)] = {
            "category": (it.findtext("ItemCategory") or "").strip(),
            "requiredLevel": _int(it, "RequiredLevel"),
            "rare": "Rare" in flags,
            "additionalDataId": _int(it, "AdditionalDataId"),
        }
    return items


def _evade(folder, name, tag):
    return {str(_int(r, "Id")): {"physEv": _int(r, "PhysicalEvasion"), "magEv": _int(r, "MagicalEvasion")}
            for r in _rows(folder, name, tag)}


def source_hashes(folder):
    return {n: hashlib.sha256((folder / n).read_bytes()).hexdigest() for n in SOURCES}


def loader_version(folder):
    cfg = json.loads((folder.parent / "ModConfig.json").read_text(encoding="utf-8"))
    return cfg["ModVersion"]


def snapshot(folder):
    """The snapshot document for one TableData folder, deterministic so a rerun diffs clean."""
    return {
        "_meta": {
            "source": "fftivc.utility.modloader TableData dump, the vanilla tables",
            "loaderVersion": loader_version(folder),
            "sha256": source_hashes(folder),
            "tool": "python tools/snapshot_vanilla_tables.py",
        },
        "jobs": _jobs(folder),
        "items": _items(folder),
        "shields": _evade(folder, "ItemShieldData.xml", "ItemShield"),
        "accessories": _evade(folder, "ItemAccessoryData.xml", "ItemAccessory"),
    }


def main():
    doc = snapshot(TABLE_DATA)
    VANILLA_TABLES.write_text(json.dumps(doc, indent=1, sort_keys=True) + "\n", encoding="utf-8")
    print(f"wrote {VANILLA_TABLES} ({len(doc['jobs'])} jobs, {len(doc['items'])} items, "
          f"loader {doc['_meta']['loaderVersion']})")


if __name__ == "__main__":
    main()
