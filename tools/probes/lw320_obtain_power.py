"""LW-320: audit every living weapon's power against how it is obtained.

The dominance gate (analyze.py) checks living weapons like everything else (only the
Materia Blade, id 32, carries the livingWeapon exemption flag), but the gate never
crosses power with OBTAIN: a shop item may legally tie or beat an earned one. This
instrument audits exactly that ground for the owner's rulings pass. Authority for
obtain is the grid (docs/living_weapon_grid.csv, per the LW-320 seat); power fields
come from data/items.json proposed values and the baked lanes in LivingWeapon/meta.json.

Three lenses, all judgment-free (the owner rules; this only surfaces matchups):
  A. Global WP band: who holds top-shelf WP, and how each copy is obtained.
  B. In-category shop spikes: shop weapons selling at or above the WP of the
     earned weapons in their own category (the Warbrand/Ravager disease).
  C. Hunt value: for each earned weapon vs each shop peer in-category, the axes the
     earned weapon wins (gate vocabulary: wp/evade/range, earlier tier, element,
     on-hit, equip-bonus rider, abnormal formula, lane kind, forced +3 ability). An
     earned weapon with a shop peer it beats on NO axis is a wasted hunt.

Output: terminal report + tools/probes/lw320_obtain_power.json (feeds the owner chart).
Run: python tools/probes/lw320_obtain_power.py
"""
import json
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "tools"))
import analyze  # noqa: E402  (the gate's own dominance vocabulary, reused verbatim)
from lib.items import load_items  # noqa: E402
from lib.flavor import PROC, CAST  # noqa: E402  (the card text's own proc-id names)

# Effort scale for the chart's x axis. A PRESENTATION scale only, not a ruling:
# multi-method cells score their CHEAPEST path (a player takes the easiest route).
EFFORT = {"Shop": 0, "Join": 1, "Steal": 2, "Move-Find": 3, "Poach": 3}
EFFORT_LABELS = {0: "Shop", 1: "Join", 2: "Steal", 3: "Hunt", 4: "Rare hunt"}


def effort_of(cell):
    """Cheapest route wins, and each route carries its OWN rarity bump (the verify
    round caught 'Move-Find (rare)/Steal' scoring the Move-Find rare bump on top of
    the Steal base). Unknown tokens fail loud rather than KeyError."""
    parts, depth, cur = [], 0, []
    for ch in cell:
        if ch == "(":
            depth += 1
        elif ch == ")":
            depth = max(0, depth - 1)
        elif ch == "/" and depth == 0:
            parts.append("".join(cur))
            cur = []
            continue
        cur.append(ch)
    parts.append("".join(cur))
    toks, best = [], None
    for seg in parts:
        tok = re.sub(r"\s*\([^)]*\)", "", seg).strip()
        if tok not in EFFORT:
            raise SystemExit(f"obtain token {tok!r} has no effort rank (cell {cell!r})")
        toks.append(tok)
        e = EFFORT[tok] + (1 if EFFORT[tok] >= 2 and "rare" in seg.lower() else 0)
        best = e if best is None else min(best, e)
    return best, toks, "rare" in cell.lower()


def lanes_of(token):
    return set(token.split("+")) if token else set()


def onhit_label(p):
    """The on-hit rider as the PLAYER sees it: mirrors tools/lib/flavor.mechanics()
    branch for branch, because the prose onHit field says None on ~20 weapons whose
    real proc lives only in onHitAbilityId (verify-round find). Where mechanics()
    stays silent on a nonzero id (formula-2 non-elemental, id not in CAST), the id
    is returned separately as an OPEN proc: unverified in-game, reported as hygiene,
    never counted as a power axis."""
    f = p.get("formula", 1)
    el = p.get("element", "None")
    oai = p.get("onHitAbilityId", 0) or 0
    prose = p.get("onHit", "None")
    if prose not in ("None", "", None):
        return prose, 0
    if f == 4 or not oai:
        return "None", 0  # magic gun: the element IS the attack; element axis carries it
    if f == 45 and oai in PROC:
        return f"Always inflicts {PROC[oai]}", 0
    if f == 2:
        if oai == 147:
            return "May knock back", 0
        if oai in CAST:
            return f"May cast {CAST[oai].split(' (')[0]}", 0
        if el not in ("None", "", None):
            spell = {"Lightning": "Thunder", "Fire": "Fire", "Ice": "Blizzard"}.get(el, el)
            return f"May cast {spell}", 0
        return "None", oai  # silent cast id: the card never mentions it
    if oai == 55:
        return "May strip buffs", 0
    if oai == 95:
        return "May Stop, petrify, or kill", 0
    if oai == 41:
        return "May instantly kill", 0
    if oai in PROC:
        return f"May inflict {PROC[oai]}", 0
    return "None", oai


def build_records():
    doc = load_items()
    nf = set(doc["_meta"].get("normalFormulaIds", [1]))
    items = {it["id"]: it for it in doc["items"]}
    meta = json.loads((ROOT / "LivingWeapon" / "meta.json").read_text(encoding="utf-8"))
    grid = analyze.load_grid_rows()
    recs = []
    for rid, row in sorted(grid.items()):
        it = items[rid]
        p = it["proposed"]
        cell = (row.get("obtain") or "").strip()
        effort, methods, rare = effort_of(cell)
        onhit, silent_proc = onhit_label(p)
        recs.append({
            "id": rid, "name": row["name"], "vanilla": row.get("Prev Name", ""),
            "cat": row["type"], "tier": int(row["tier"]), "wp": int(row["WP"]),
            "evade": p.get("evade", 0), "range": p.get("range", 1),
            "element": p.get("element", "None"), "onHit": onhit,
            "silentProcId": silent_proc, "rider": p.get("rider") or "None",
            "formula": p.get("formula", 1), "abnormalFormula": p.get("formula", 1) not in nf,
            "lanes": sorted(lanes_of(meta[str(rid)]["lane"])),
            "plus3": (row.get("+3 ability") or "").strip(),
            "obtain": cell, "methods": methods, "rare": rare,
            "shop": methods == ["Shop"], "effort": effort,
            "item": it,  # stripped before the JSON dump; feeds analyze.dominates
        })
    return recs, nf


def axis_wins(e, s, nf):
    """Axes on which earned weapon e beats shop peer s. Gate vocabulary, plus the
    living-weapon axes the gate never sees (lanes, forced +3 ability)."""
    wins = []
    for ax in ("wp", "evade", "range"):
        if e[ax] > s[ax]:
            wins.append(ax)
    if e["tier"] < s["tier"]:
        wins.append("tier")
    if e["element"] not in ("None", "") and e["element"] != s["element"]:
        wins.append("elem:" + e["element"])
    if e["onHit"] not in ("None", "") and e["onHit"] != s["onHit"]:
        wins.append("onhit:" + e["onHit"])
    if e["rider"] not in ("None", "") and e["rider"] != s["rider"]:
        wins.append("rider:" + e["rider"])
    if e["abnormalFormula"] and e["formula"] != s["formula"]:
        wins.append("formula:" + str(e["formula"]))
    lane_edge = set(e["lanes"]) - set(s["lanes"])
    if lane_edge:
        wins.append("lane:" + "+".join(sorted(lane_edge)))
    if e["plus3"] and e["plus3"] != s["plus3"]:
        wins.append("plus3")
    return wins


def main():
    recs, nf = build_records()
    shop = [r for r in recs if r["shop"]]
    earned = [r for r in recs if not r["shop"]]
    by_cat = {}
    for r in recs:
        by_cat.setdefault(r["cat"], []).append(r)
    print(f"{len(recs)} living weapons: {len(shop)} shop / {len(earned)} earned")

    # Lens A: the global top-WP band.
    band = sorted(recs, key=lambda r: -r["wp"])
    cut = band[11]["wp"]  # WP of the 12th-highest weapon: the seat's own band
    lens_a = [r for r in band if r["wp"] >= cut]
    print(f"\n=== Lens A: global top-WP band (WP >= {cut}, {len(lens_a)} weapons) ===")
    for r in lens_a:
        tag = "SHOP <-- spike candidate" if r["shop"] else "earned"
        print(f"  WP {r['wp']:>2} t{r['tier']} {r['name']:<20} {r['cat']:<13} {tag}")

    # Lens B: shop weapons at/above earned WP in their own category.
    print("\n=== Lens B: in-category shop spikes ===")
    lens_b = []
    for cat, group in sorted(by_cat.items()):
        e = [r for r in group if not r["shop"]]
        s = [r for r in group if r["shop"]]
        if not e:
            print(f"  {cat}: no earned weapons (shop top WP {max(x['wp'] for x in s)}); category skipped")
            continue
        floor = min(x["wp"] for x in e)
        ceil = max(x["wp"] for x in e)
        for r in s:
            if r["wp"] >= floor:
                beats = [x["name"] for x in e if r["wp"] >= x["wp"]]
                sev = ("OUTGUNS ALL HUNTS" if r["wp"] > ceil
                       else "MATCHES TOP HUNT" if r["wp"] == ceil else "inside hunt band")
                lens_b.append({"id": r["id"], "name": r["name"], "cat": cat, "wp": r["wp"],
                               "tier": r["tier"], "severity": sev, "meets_or_beats": beats,
                               "earned_band": [floor, ceil]})
                print(f"  {cat}: {r['name']} (shop, WP {r['wp']}, t{r['tier']}) vs earned band "
                      f"{floor}-{ceil} -> {sev}; >= {', '.join(beats)}")

    # Lens C: every earned weapon must beat every shop peer on some axis.
    print("\n=== Lens C: hunt value (earned weapon vs each shop peer) ===")
    lens_c = []
    for e in sorted(earned, key=lambda r: (r["cat"], -r["wp"])):
        peers = [s for s in by_cat[e["cat"]] if s["shop"]]
        worst, worst_wins, zero = None, None, []
        dominated_by = []
        for s in peers:
            wins = axis_wins(e, s, nf)
            if analyze.dominates(s["item"], e["item"], "proposed", nf):
                dominated_by.append(s["name"])
            if not wins:
                zero.append(s["name"])
            if worst_wins is None or len(wins) < len(worst_wins):
                worst, worst_wins = s, wins
        lens_c.append({"id": e["id"], "name": e["name"], "cat": e["cat"], "wp": e["wp"],
                       "tier": e["tier"], "obtain": e["obtain"], "effort": e["effort"],
                       "zero_axis_vs": zero, "gate_dominated_by": dominated_by,
                       "worst_peer": worst["name"] if worst else None,
                       "worst_peer_wins": worst_wins or []})
        flag = ""
        if zero:
            flag = f"  VIOLATION: no axis win vs {', '.join(zero)}"
        if dominated_by:
            flag += f"  GATE-DOMINATED by {', '.join(dominated_by)}"
        wl = ",".join(worst_wins) if worst_wins else "-"
        print(f"  {e['name']:<20} {e['cat']:<13} WP {e['wp']:>2} t{e['tier']} "
              f"[{EFFORT_LABELS[e['effort']]:<9}] worst peer {worst['name'] if worst else '-':<18} "
              f"wins {wl}{flag}")

    silent = [(r["id"], r["name"], r["silentProcId"]) for r in recs if r["silentProcId"]]
    if silent:
        print("\n=== Hygiene: silent on-hit ability ids (card text never mentions them; live-verify before counting as power) ===")
        for sid, name, oai in silent:
            print(f"  id {sid:>3} {name:<20} onHitAbilityId {oai}")

    out = {
        "generated_for": "LW-320",
        "effort_scale": EFFORT_LABELS,
        "silent_procs": [{"id": a, "name": b, "abilityId": c} for a, b, c in silent],
        "weapons": [{k: v for k, v in r.items() if k != "item"} for r in recs],
        "lens_a_band_cut": cut,
        "lens_a": [r["id"] for r in lens_a],
        "lens_b": lens_b,
        "lens_c": lens_c,
    }
    dest = ROOT / "tools" / "probes" / "lw320_obtain_power.json"
    dest.write_text(json.dumps(out, indent=1) + "\n", encoding="utf-8")
    print(f"\nwrote {dest}")


if __name__ == "__main__":
    main()
