"""LW-318: hunt the dominance gate's blind spot -- items alive on a single thin niche.

The gate (analyze.py) proves NO item is strictly dominated: every item keeps at least
one protection against every would-be dominator (a numeric axis it wins, an earlier
tier, or a rider token the other lacks). What the gate cannot see is HOW THIN that
protection is. An item whose only shield against a clearly stronger sibling is one
point of evade is legally alive and practically vendor trash. This instrument finds
exactly those cases, using the gate's own vocabulary verbatim (analyze.dominates'
axis/tier/rider law, same category groups, same slot groups, same access rule, same
Materia Blade exemption), and reports every pair (A, B) where A survives B on
EXACTLY ONE protection, plus true stat twins (no protection either way, no strict
edge -- two identical stat lines sharing one niche).

Judgment-free: the owner rules what is "too thin"; this only surfaces the matchups,
ranked with the smallest numeric margins first. Feeds the LW-318 fairness pass.

Run: python tools/probes/lw318_thin_niche.py            (terminal report)
"""
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "tools"))
import analyze  # noqa: E402  (the gate's own dominance vocabulary, reused verbatim)
from lib.items import load_items  # noqa: E402


def protections(a, b, nf):
    """Every shield keeping A un-dominated by B, mirroring analyze.dominates' tests.
    Returns a list of (kind, detail, margin) -- margin is numeric thinness where it
    applies (axis point gap, tier gap), None for rider tokens."""
    shields = []
    pa, pb = a["proposed"], b["proposed"]
    for axis, default in analyze.NUMERIC_AXES.items():
        av, bv = pa.get(axis, default), pb.get(axis, default)
        if bv < av:
            shields.append(("axis", f"{axis} {av} vs {bv}", av - bv))
    ta, tb = a.get("tier", 0), b.get("tier", 0)
    if tb > ta:
        shields.append(("tier", f"unlocks T{ta} vs B's T{tb}", tb - ta))
    ra = analyze.riders(pa, nf)
    rb = analyze.riders(pb, nf)
    for tok in sorted(ra - rb):
        shields.append(("rider", tok, None))
    return shields


def b_edge(a, b, nf):
    """What B holds over A (the price A pays for its niche) -- axes B strictly wins,
    riders B has that A lacks, and an earlier tier."""
    edges = []
    pa, pb = a["proposed"], b["proposed"]
    for axis, default in analyze.NUMERIC_AXES.items():
        av, bv = pa.get(axis, default), pb.get(axis, default)
        if bv > av:
            edges.append(f"{axis} +{bv - av}")
    ta, tb = a.get("tier", 0), b.get("tier", 0)
    if tb < ta:
        edges.append(f"T{tb} (earlier by {ta - tb})")
    extra = analyze.riders(pb, nf) - analyze.riders(pa, nf)
    edges.extend(sorted(extra))
    return edges


def pairs(items):
    """Every ordered (A, B) the gate itself compares: same category, plus the shared
    slot groups under the access rule. Deduped."""
    seen = set()
    by_cat = {}
    for it in items:
        by_cat.setdefault(it["category"], []).append(it)
    for group in by_cat.values():
        for a in group:
            for b in group:
                if a["id"] != b["id"] and (a["id"], b["id"]) not in seen:
                    seen.add((a["id"], b["id"]))
                    yield a, b
    groups = {}
    for it in items:
        g = analyze.SLOT_GROUP.get(it["category"])
        if g:
            groups.setdefault(g, []).append(it)
    for grp in groups.values():
        for a in grp:
            for b in grp:
                if a["id"] != b["id"] and analyze.can_dominate_access(b, a) \
                        and (a["id"], b["id"]) not in seen:
                    seen.add((a["id"], b["id"]))
                    yield a, b


def main():
    doc = load_items(analyze.ITEMS)
    nf = set(doc["_meta"].get("normalFormulaIds", [1]))
    items = [it for it in doc["items"] if not it.get("livingWeapon") and "proposed" in it]

    thin, twins = [], []
    for a, b in pairs(items):
        shields = protections(a, b, nf)
        if len(shields) == 1:
            thin.append((a, b, shields[0], b_edge(a, b, nf)))
        elif not shields and not analyze.dominates(b, a, "proposed", nf):
            # no shield yet not dominated == nothing strict anywhere: a stat twin
            if a["id"] < b["id"]:
                twins.append((a, b))

    # thinnest first: numeric margins ascending, then tier gaps, then rider-token shields
    order = {"axis": 0, "tier": 1, "rider": 2}
    thin.sort(key=lambda t: (order[t[2][0]], t[2][2] if t[2][2] is not None else 99))

    print(f"=== thin-niche report: {len(items)} gate-checked items ===")
    print(f"pairs alive on EXACTLY ONE protection: {len(thin)}\n")
    for a, b, (kind, detail, margin), edges in thin:
        print(f"  {a['name']:<20} (id{a['id']:>3} T{a.get('tier',0)}) survives "
              f"{b['name']:<20} (id{b['id']:>3} T{b.get('tier',0)}) ONLY by "
              f"[{kind}] {detail}")
        print(f"      price paid: {', '.join(edges) if edges else 'nothing (pure tie elsewhere)'}")
    print(f"\nstat twins (identical on every gate axis, tier and rider): {len(twins)}")
    for a, b in twins:
        print(f"  {a['name']} (id{a['id']}) == {b['name']} (id{b['id']})")


if __name__ == "__main__":
    main()
