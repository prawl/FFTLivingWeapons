"""Add a dmgScaling column to docs/living_weapon_grid.csv from data/items.json truth.

Damage scaling is keyed by the mod's item CATEGORY (items.json is the source of truth;
the mod re-categorized vanilla axes/flails on purpose), with per-weapon formula overrides:
  f99 = Speed-scaled custom (Swiftfang/Swiftedge), f67/f69 = missing-HP, f4 = magic-gun Faith spell.
Also repairs row 108 (Ironreed Pole) whose opRisk/sigNote/Verified columns had drifted,
and clears row 28's onHit (the formula note now lives in dmgScaling).
"""
import csv
import json
import sys

REPO = r"C:\Users\ptyRa\Dev\FFTItemOverhaul"

CAT_DMG = {
    "Knife": "(PA+Sp)/2 x WP",
    "NinjaBlade": "(PA+Sp)/2 x WP",
    "Bow": "(PA+Sp)/2 x WP",
    "Sword": "PA x WP",
    "Crossbow": "PA x WP",
    "Polearm": "PA x WP",
    "Rod": "PA x WP",
    "KnightSword": "PA x Br/100 x WP",
    "Katana": "PA x Br/100 x WP",
    "Staff": "MA x WP",
    "Pole": "MA x WP",
    "Book": "(PA+MA)/2 x WP",
    "Instrument": "(PA+MA)/2 x WP",
    "Cloth": "(PA+MA)/2 x WP",
    "Gun": "WP x WP (no stats)",
    "Bag": "Rand(1..PA) x WP",
}

FORMULA_OVERRIDE = {
    99: "Speed-scaled (f99)",
    67: "missing-HP (f67)",
    69: "missing-HP (f69)",
    4: "Faith spell (f4)",
}

with open(f"{REPO}\\data\\items.json", encoding="utf-8") as f:
    data = json.load(f)
items = data["items"] if isinstance(data, dict) and "items" in data else data
by_id = {it["id"]: it for it in items}

path = f"{REPO}\\docs\\living_weapon_grid.csv"
with open(path, encoding="utf-8-sig", newline="") as f:
    rows = list(csv.reader(f))

header = rows[0]
if "dmgScaling" in header:
    sys.exit("dmgScaling column already present; aborting")
wp_idx = header.index("WP")
header.insert(wp_idx + 1, "dmgScaling")

for row in rows[1:]:
    if not row or not row[0].strip().isdigit():
        continue
    iid = int(row[0])
    it = by_id.get(iid)
    if it is None:
        sys.exit(f"grid id {iid} not found in items.json")
    formula = it["proposed"].get("formula")
    dmg = FORMULA_OVERRIDE.get(formula) or CAT_DMG[it["category"]]
    row.insert(wp_idx + 1, dmg)

    cols = {name: i for i, name in enumerate(header)}
    if iid == 108:  # Ironreed Pole: columns had drifted one slot
        row[cols["opRisk"]] = "med"
        row[cols["sigNote"]] = (
            "DRAFT grant ideas — +: Reflexes (reaction: dodge-first, springy and swift); "
            "+2: Reflexes + Attack Boost (raw stat lane); "
            "+3: First Strike (reaction: acts first after dodging) "
            "(T2 baseline reach for monks who want raw speed)"
        )
        row[cols["Verified Live?"]] = ""
    if iid == 28:  # Swiftedge: formula note moves out of onHit into dmgScaling
        row[cols["onHit"]] = "—"

with open(path, "w", encoding="utf-8", newline="") as f:
    csv.writer(f).writerows(rows)
print(f"wrote {len(rows) - 1} rows with dmgScaling column")
