#!/usr/bin/env python
"""
Build-diversity invariant checker for the FFT item overhaul.

Core rule (the whole point of the mod): within a category, NO item may be strictly
dominated by another. B dominates A iff B is >= A on every axis -- WP, evade,
identity riders (element / on-hit status / special formula), AND availability tier
(B unlocks no later than A) -- and is strictly better somewhere. If A carries a
rider B lacks, OR A unlocks earlier, A has a niche and is NOT dominated.

Usage: python tools/analyze.py [data/items.json] [--baseline]
Exit 1 if any 'proposed' item is strictly dominated (invariant violated).
"""
import json, sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
ITEMS = Path(sys.argv[1]) if len(sys.argv) > 1 and not sys.argv[1].startswith("-") else ROOT / "data" / "items.json"
CHECK_BASELINE = "--baseline" in sys.argv


def riders(s, normal_formulas):
    """Identity tokens that make an item non-dominated (a dominator must also have all)."""
    r = set()
    if s.get("element", "None") not in ("None", "", None):
        r.add("elem:" + s["element"])
    oh = s.get("onHit", "None")
    if oh not in ("None", "", None):
        r.add("onhit:" + oh)
    if s.get("formula", 1) not in normal_formulas:
        r.add("formula:" + str(s["formula"]))
    return r


def dominates(bi, ai, key, normal_formulas):
    """Does item B strictly dominate item A? (full items; uses bi[key] stats + tier)"""
    b, a = bi[key], ai[key]
    # numeric "more is better" axes: WP, evade, and range (range defaults to 1 = melee for non-ranged)
    br, ar = b.get("range", 1), a.get("range", 1)
    if not (b["wp"] >= a["wp"] and b["evade"] >= a["evade"] and br >= ar):
        return False
    tb, ta = bi.get("tier", 0), ai.get("tier", 0)
    if tb > ta:
        return False  # B unlocks later -> A owns the availability niche
    ra, rb = riders(a, normal_formulas), riders(b, normal_formulas)
    if not ra.issubset(rb):
        return False  # A has a rider B lacks -> niche
    strictly_better = (b["wp"] > a["wp"]) or (b["evade"] > a["evade"]) or (br > ar) or (rb > ra) or (tb < ta)
    return strictly_better


def check(items, key, normal_formulas):
    by_cat = {}
    for it in items:
        by_cat.setdefault(it["category"], []).append(it)
    violations = []
    for group in by_cat.values():
        for a in group:
            doms = [b for b in group if b["id"] != a["id"] and dominates(b, a, key, normal_formulas)]
            if doms:
                violations.append((a, doms))
    return violations


def fmt(it, key, nf):
    s = it[key]
    rid = riders(s, nf)
    extra = (" " + " ".join(sorted(rid))) if rid else ""
    name = it.get("name") if it.get("name") not in (None, "TBD") else it["vanillaName"]
    return f"id{it['id']:>2} {name:18} T{it.get('tier','?')} WP{s['wp']:>2} Ev{s['evade']:>3}%{extra}"


def main():
    doc = json.loads(ITEMS.read_text(encoding="utf-8"))
    nf = set(doc["_meta"].get("normalFormulaIds", [1]))
    items = doc["items"]

    print(f"=== {ITEMS.name}: {len(items)} items ===\n")
    for it in sorted(items, key=lambda x: x["id"]):
        print("  " + fmt(it, "proposed", nf))

    rc = 0
    keys = [("baseline", "BASELINE"), ("proposed", "PROPOSED")] if CHECK_BASELINE else [("proposed", "PROPOSED")]
    for key, label in keys:
        v = check(items, key, nf)
        print(f"\n--- {label} dominance check ---")
        if not v:
            print("  PASS: no item is strictly dominated. Build-diversity invariant holds.")
        else:
            for a, doms in v:
                an = a.get("name") if a.get("name") not in (None, "TBD") else a["vanillaName"]
                dl = ", ".join(f"id{b['id']}" for b in doms)
                print(f"  DOMINATED: id{a['id']} {an} -- by {dl}")
            if label == "PROPOSED":
                rc = 1
    sys.exit(rc)


if __name__ == "__main__":
    main()
