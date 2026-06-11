"""Merge living_weapon_knives.csv back into living_weapon_grid.csv under the knives schema:
id,tier,name,WP,parry%,grows,onHit,P,P2,P3,opRisk,sigNote,Verified Live?
- Knife rows come from the knives csv VERBATIM (they are the shipped truth).
- Every other weapon's facts (tier/WP/parry/onHit) recompute from data/items.json; grows follows
  the Tuning routing (Rod/Staff or formula 4 -> MA; formula 99 -> Speed; 67/69 -> none; else PA);
  P/P2/P3 state the shipped growth; the old grid's draft grant ideas fold into sigNote.
"""
import csv
import json
from pathlib import Path

ROOT = Path(r"C:\Users\ptyRa\Dev\FFTItemOverhaul")
GRID = ROOT / "docs" / "living_weapon_grid.csv"
KNIVES = ROOT / "docs" / "living_weapon_knives.csv"
ITEMS = ROOT / "data" / "items.json"

HEADER = ["id", "tier", "name", "WP", "parry%", "grows", "onHit",
          "P", "P2", "P3", "opRisk", "sigNote", "Verified Live?"]

GROWTH = {
    "PA":    ("PA +10%", "PA +20%", "PA +30%"),
    "MA":    ("MA +10%", "MA +20%", "MA +30%"),
    "Speed": ("Speed +5%", "Speed +10%", "Speed +15%"),
    "—":     ("—", "—", "—"),
}


def grows_for(item):
    f = item["proposed"].get("formula", 1)
    if f in (67, 69):
        return "—"
    if f == 99:
        return "Speed"
    cat = item["proposed"].get("categoryOverride") or item["category"]
    if cat in ("Rod", "Staff") or f == 4:
        return "MA"
    return "PA"


items = {it["id"]: it for it in json.load(open(ITEMS, encoding="utf-8"))["items"]}

knife_rows = {}
with open(KNIVES, encoding="utf-8", newline="") as f:
    for row in csv.DictReader(f):
        knife_rows[int(row["id"])] = [row[c] for c in HEADER]

grid_rows = {}
with open(GRID, encoding="utf-8", newline="") as f:
    for row in csv.DictReader(f):
        grid_rows[int(row["id"])] = row

out = {}
for wid, row in grid_rows.items():
    if wid in knife_rows:
        continue  # knives csv wins
    it = items.get(wid)
    if not it:
        print(f"WARN: grid id {wid} ({row['name']}) not in items.json -- skipped")
        continue
    p = it["proposed"]
    g = grows_for(it)
    p1, p2, p3 = GROWTH[g]
    on_hit = p.get("onHit") or "None"
    if on_hit == "None":
        on_hit = "—"
    draft = (f"DRAFT grant ideas — +: {row['plus']}; +2: {row['plus2']}; +3: {row['plus3']}"
             f" ({row['signature']})")
    out[wid] = [str(wid), str(it["tier"]), it["name"], str(p["wp"]), f"{p.get('evade', 0)}%",
                g, on_hit, p1, p2, p3, row["opRisk"], draft, row.get("Verified Live?", "")]

out.update(knife_rows)

with open(GRID, "w", encoding="utf-8", newline="") as f:
    w = csv.writer(f)
    w.writerow(HEADER)
    for wid in sorted(out):
        w.writerow(out[wid])

print(f"wrote {GRID.name}: {len(out)} weapons ({len(knife_rows)} from the knives sheet)")
