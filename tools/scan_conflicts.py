#!/usr/bin/env python
"""
Conflict scanner: which OTHER installed mods edit the same item ids we do?

Two full item overhauls can't cleanly stack (the modloader resolves field
collisions by load order). This reports the overlap so you know which mods
collide and that ours should load after them. See docs/DESIGN.md.

Usage: python tools/scan_conflicts.py [data/items.json] [reloaded_mods_dir]
"""
import json, sys, re
from pathlib import Path
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parent.parent
ITEMS = Path(sys.argv[1]) if len(sys.argv) > 1 else ROOT / "data" / "items.json"
MODS = Path(sys.argv[2]) if len(sys.argv) > 2 else Path(
    r"C:\program files (x86)\steam\steamapps\common\FINAL FANTASY TACTICS - The Ivalice Chronicles\Reloaded\Mods")

OUR_MOD_ID = "prawl.fft.itemoverhaul"
ITEM_TABLES = ["ItemData", "ItemWeaponData", "ItemArmorData", "ItemAccessoryData",
               "ItemShieldData", "ItemEquipBonusData", "ItemShopsData", "MapTrapFormationData"]


def ids_in_xml(path):
    """Return the set of <Id> values an item table XML edits (sparse override)."""
    try:
        root = ET.fromstring(path.read_text(encoding="utf-8"))
    except Exception:
        return set()
    return {int(e.text) for e in root.iter("Id") if e.text and e.text.strip().isdigit()}


def main():
    doc = json.loads(ITEMS.read_text(encoding="utf-8"))
    our_ids = {it["id"] for it in doc["items"]}
    # which tables WE touch (pilot: weapons only, but generalize by category later)
    our_tables = {"ItemWeaponData"}
    print(f"Our mod edits {len(our_ids)} ids in {sorted(our_tables)}: {sorted(our_ids)}\n")

    if not MODS.exists():
        print(f"(mods dir not found: {MODS})"); return

    found_conflict = False
    for mod in sorted(MODS.iterdir()):
        if not mod.is_dir() or mod.name == OUR_MOD_ID:
            continue
        tdir = mod / "FFTIVC" / "tables" / "enhanced"
        if not tdir.exists():
            continue
        hits = []
        for tbl in ITEM_TABLES:
            f = tdir / f"{tbl}.xml"
            if not f.exists():
                continue
            ids = ids_in_xml(f)
            overlap = (ids & our_ids) if tbl in our_tables else set()
            if ids and tbl in our_tables:
                hits.append((tbl, ids, overlap))
        if hits:
            tag = "CONFLICT" if any(o for _, _, o in hits) else "adjacent"
            print(f"[{tag}] {mod.name}")
            for tbl, ids, overlap in hits:
                mark = f"  COLLIDES on ids {sorted(overlap)}" if overlap else "  (no id overlap on our pilot set)"
                print(f"    {tbl}: edits {len(ids)} ids.{mark}")
                if overlap:
                    found_conflict = True
            print()

    if found_conflict:
        print("=> Load this mod AFTER the conflicting mods so our complete-per-item entries win.")
    else:
        print("=> No id collisions on our current pilot set.")


if __name__ == "__main__":
    main()
