#!/usr/bin/env python
"""
Offline per-tile record-geometry solver over the Treasure Master capture corpus. READ-ONLY,
no game needed: it eats data/treasure_addrs.json and nothing else.

WHY THIS EXISTS
---------------
The mark-bit research (2026-06-11) proved per-tile FLAG bytes exist at module-static
addresses, but left "address an ARBITRARY tile" open: every capture found THIS tile's
copies by differential toggle, never the array's layout. Meanwhile the capture corpus has
quietly grown to 71 maps / 284 tiles / ~2300 (addr, restingValue) pairs -- massively
over-constrained ground truth for solving the layout outright.

MODEL
-----
A per-tile state array is global (module-static), reused by every map; only the map's WIDTH
changes how (x, y) maps to a record index:

    addr = BASE + idx * STRIDE + fieldOff
    idx  = x + y*W   (row-major)   or   y + x*H   (column-major)

BASE, STRIDE, order are GLOBAL. W (or H) is per-map, 4..18. Multiple fields per record are
expected (a tile's several captured copies inside one family) and fall out as distinct
fieldOff clusters over the same BASE.

METHOD
------
1. Cluster all captured addresses (across every map) by proximity: any global array spans
   at most ~maxIdx*STRIDE + fields, comfortably under 128KB, so clusters with >=0x20000
   gaps are distinct families. Addresses >=0x142000000 are dropped (volatile arena,
   relocates -- same rule as gen_treasure_db.py).
2. For each family, brute-force (order, STRIDE 1..512): each map votes with its best W;
   a candidate scores by how many addresses across ALL maps land on a small set of shared
   (BASE + fieldOff) values. The right geometry snaps into one loud consensus; wrong ones
   scatter.
3. Report per family: best (order, stride), the implied BASE, field offsets, per-map W
   table, and the fraction of addresses explained.

A solved family = arbitrary-tile addressing for that array: the next live session reads a
known map's records beside walls/water/cliffs and identifies the passability field, which
is the Bulwark / zone-control input (LW-142's big sibling).

    python tools\\probes\\tile_geometry_solver.py            # solve everything
    python tools\\probes\\tile_geometry_solver.py --family 0x141146000   # one family, verbose
"""
import argparse
import json
from collections import Counter, defaultdict
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
ADDRS = ROOT / "data" / "treasure_addrs.json"

VOLATILE_BASE = 0x142000000
GAP = 0x20000
STRIDES = range(1, 513)
WIDTHS = range(4, 19)


def load():
    d = json.loads(ADDRS.read_text(encoding="utf-8"))
    # obs[mapId] = list of (x, y, addr)
    obs = defaultdict(list)
    for mid, m in d["maps"].items():
        for t in m.get("tiles", []):
            for a, _off in t.get("addrs", []):
                addr = int(a, 16)
                if addr < VOLATILE_BASE:
                    obs[int(mid)].append((t["x"], t["y"], addr))
    return obs


def clusters(obs):
    every = sorted({a for tiles in obs.values() for (_x, _y, a) in tiles})
    out, cur = [], [every[0]]
    for a in every[1:]:
        if a - cur[-1] > GAP:
            out.append((cur[0], cur[-1]))
            cur = [a]
        else:
            cur.append(a)
    out.append((cur[0], cur[-1]))
    return out


def solve_family(obs, lo, hi, verbose=False):
    """Best (order, stride) by cross-map base consensus."""
    fam = {mid: [(x, y, a) for (x, y, a) in tiles if lo <= a <= hi]
           for mid, tiles in obs.items()}
    fam = {mid: t for mid, t in fam.items() if t}
    n_addr = sum(len(t) for t in fam.values())
    if n_addr < 8:
        return None

    best = None
    for order in ("row", "col"):
        for s in STRIDES:
            # each map votes its best W's base multiset; consensus across maps wins
            base_votes = Counter()
            per_map_w = {}
            for mid, tiles in fam.items():
                best_w, best_bases = None, None
                for w in WIDTHS:
                    bases = Counter()
                    for x, y, a in tiles:
                        idx = x + y * w if order == "row" else y + x * w
                        bases[a - idx * s] += 1
                    # a map's self-consistency: its addrs collapsing onto few bases
                    score = max(bases.values())
                    if best_bases is None or score > max(best_bases.values()):
                        best_w, best_bases = w, bases
                per_map_w[mid] = best_w
                base_votes.update(best_bases)
            # consensus: how many addresses across all maps share the top bases
            # (allow up to 4 field offsets = 4 base values)
            top = base_votes.most_common(4)
            explained = sum(c for _b, c in top)
            if best is None or explained > best["explained"]:
                best = {"order": order, "stride": s, "explained": explained,
                        "n": n_addr, "tops": top, "w": dict(per_map_w)}
    if verbose and best:
        print(f"  per-map W: {best['w']}")
    return best


def main():
    p = argparse.ArgumentParser()
    p.add_argument("--family", type=lambda x: int(x, 0), default=None)
    a = p.parse_args()

    obs = load()
    n = sum(len(t) for t in obs.values())
    fams = clusters(obs)
    print(f"{len(obs)} maps, {n} static addr observations, {len(fams)} address families\n")

    for lo, hi in fams:
        if a.family is not None and not (lo <= a.family <= hi):
            continue
        r = solve_family(obs, lo, hi, verbose=a.family is not None)
        if r is None:
            print(f"family 0x{lo:X}..0x{hi:X}: too few observations, skipped")
            continue
        frac = r["explained"] / r["n"]
        verdict = "SOLVED" if frac >= 0.8 else ("partial" if frac >= 0.5 else "no fit")
        print(f"family 0x{lo:X}..0x{hi:X}  ({r['n']} addrs)")
        print(f"  best: {r['order']}-major, stride {r['stride']} (0x{r['stride']:X})  "
              f"explains {r['explained']}/{r['n']} ({frac:.0%})  -> {verdict}")
        for b, c in r["tops"]:
            print(f"    base 0x{b:X}  ({c} addrs)")
        print()


if __name__ == "__main__":
    main()
