"""LW-386 offline audit: does every Poacher's Den carcass hand out the reward its card promises?

READ-ONLY and OFFLINE (no game needed). Reads the game's own poach sheet decode that
tools/extract_poach_map.py caches in working/nxd_poachitem/poachitem.sqlite (run that script once
if the cache is missing), and data/items.json at HEAD and at a release tag, then for each of the
96 carcass rows reports:

  produced   the item id the Den makes from the carcass (column Unknown2C; keys 87 to 90 give
             120, 57, 239 and 105, matching the 2026-09-01 in-memory read of the live sheet)
  promise    the reward the carcass description names ("Used to produce a dragon rod.")
  HEAD/tag   that item's name and category in each build, when the mod owns the item

Flags per build:
  TYPE       the mod renamed the reward AND moved it to another weapon category, so the carcass
             promises one kind of weapon and the Den hands out another (the 2.3.3 axe and flail
             bug); category is compared to the same id's category in the vanilla-named HEAD row
             or, when HEAD also renamed it, reported for the owner to judge
  STALE      the mod renamed the reward, so the description names the vanilla item while the
             Den hands out the renamed one (the mod never patches poachitem.en.nxd)

  python tools/probes/lw386_poach_audit.py [tag]      # tag defaults to v2.3.3
"""
import json
import sqlite3
import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
SHEET = ROOT / "working" / "nxd_poachitem" / "poachitem.sqlite"


def items_at(rev):
    if rev == "HEAD-worktree":
        text = (ROOT / "data" / "items.json").read_text(encoding="utf-8")
    else:
        text = subprocess.run(["git", "-C", str(ROOT), "show", f"{rev}:data/items.json"],
                              capture_output=True, check=True).stdout.decode("utf-8")
    return {i["id"]: i for i in json.loads(text)["items"]}


def main():
    tag = sys.argv[1] if len(sys.argv) > 1 else "v2.3.3"
    if not SHEET.exists():
        print(f"missing {SHEET}; run python tools\\extract_poach_map.py once to build it.")
        return 2
    rows = sqlite3.connect(str(SHEET)).execute(
        'SELECT Key, Unknown8, Unknown18, Unknown2C, IsRare FROM "PoachItem-en" ORDER BY Key').fetchall()
    if len(rows) != 96:
        print(f"expected 96 poach rows, got {len(rows)}; the sheet layout moved, stop.")
        return 2
    head, old = items_at("HEAD-worktree"), items_at(tag)
    counts = {"HEAD": {"TYPE": [], "STALE": []}, tag: {"TYPE": [], "STALE": []}}
    print(f"key  rare  produced  carcass -> promise | HEAD | {tag}")
    for key, name, desc, produced, rare in rows:
        promise = desc.split("Used to produce", 1)[-1].strip().rstrip(".") if desc else "?"
        cells = []
        for label, items in (("HEAD", head), (tag, old)):
            it = items.get(produced)
            if it is None:
                cells.append("not a mod item")
                continue
            renamed = it["name"] != it["vanillaName"]
            vanilla_cat = head[produced]["category"] if produced in head and head[produced]["name"] == head[produced]["vanillaName"] else None
            flags = []
            if renamed:
                flags.append("STALE")
                counts[label]["STALE"].append(key)
                if vanilla_cat is not None and it["category"] != vanilla_cat:
                    flags.append("TYPE")
                    counts[label]["TYPE"].append(key)
            cells.append(f"{it['name']} ({it['category']}){' ' + '+'.join(flags) if flags else ''}"
                         + (f" [vanilla {it['vanillaName']}]" if renamed else ""))
        print(f"{key:3}  {rare:4}  {produced:8}  {name} -> {promise} | {cells[0]} | {cells[1]}")
    print()
    for label in ("HEAD", tag):
        c = counts[label]
        print(f"{label}: {len(c['TYPE'])} carcass(es) hand out a different weapon type than promised "
              f"{c['TYPE']}; {len(c['STALE'])} description(s) name the vanilla reward the mod renamed")
    return 0


if __name__ == "__main__":
    sys.exit(main())
