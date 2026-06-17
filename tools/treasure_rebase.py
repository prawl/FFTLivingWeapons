"""Treasure Master 1.5 re-anchor: measure per-region address deltas from a SAMPLE
recapture, decide if the captured-on-1.x flag addresses can be REBASED in bulk
(instead of re-hovering all ~284 tiles), and -- only if the deltas are uniform --
emit the rebased data/treasure_addrs.json.

WHY: the 1.5 recompile moved every absolute flag address. The full campaign is 71
maps / 284 tiles / ~1230 distinct addresses spread across ~5 static-data regions
(0x140d.., 0x140e.., 0x140f.., 0x141000.., 0x141100..). If each region moved as a
unit (uniform per-region delta), a 1-2 map fresh capture measures the deltas and the
other ~69 maps rebase by arithmetic -- no re-hover. If the deltas are NOT uniform
within a region, that region's maps need a real recapture. This tool decides which.

It is PURE FILE ANALYSIS -- it never touches the game. The live recapture is done by
tools/probes/treasure_flags.py (session verb); this tool consumes its before/after.

VERBS
  python tools/treasure_rebase.py analyze <old.json> <new.json>
      <old.json> = the pre-1.5 capture (e.g. `git show HEAD:data/treasure_addrs.json`
        dumped to a file). <new.json> = data/treasure_addrs.json AFTER sample-recapturing
        1-2 maps on 1.5. Reports the dominant delta per region, how tightly the matched
        pairs agree (uniform vs scattered), and which full-dataset regions the sample did
        NOT cover (so you know if the sample is sufficient to rebase everything).

  python tools/treasure_rebase.py rebase <old.json> <new.json> <out.json>
      Apply the measured per-region deltas to every map NOT freshly recaptured, restamp
      capturedBuild to the live 1.5 key (taken from the recaptured maps), and write
      <out.json>. REFUSES if any region in play is non-uniform or uncovered by the sample
      (a partial rebase would silently ship wrong addresses). Recaptured maps pass through
      unchanged. Spot-verify the result in-game before trusting it.

  python tools/treasure_rebase.py --selftest
"""
import collections
import json
import sys

# A real flag address moved by its region's delta; the deltas we have bracket this span
# (0x140C6 ~ +0x66xx..+0x67xx, 0x1411A ~ +0x6440), so a generous window catches them all
# while rejecting coincidental cross-region matches.
DELTA_WINDOW = 0x20000
# A region (keyed by 0x100000 page) is "uniform" when this fraction of its old addresses
# map onto a captured new address under the single dominant delta.
UNIFORM_THRESHOLD = 0.98
# The volatile detail-store family the bake already drops -- never rebase it.
VOLATILE_BASE = 0x142000000


def _region(addr):
    return addr >> 20   # 1 MB page


def _addrs_of(maprec):
    out = []
    for t in maprec.get("tiles", []):
        for pair in t.get("addrs", []):
            out.append(int(pair[0], 16))
    return out


def _recaptured_maps(old, new):
    """Map ids present in both whose capturedBuild changed = freshly recaptured on 1.5."""
    out = []
    om, nm = old.get("maps", {}), new.get("maps", {})
    for mid, nrec in nm.items():
        orec = om.get(mid)
        if orec is None:
            continue
        if nrec.get("capturedBuild") != orec.get("capturedBuild"):
            out.append(mid)
    return out


def measure_deltas(old, new, sample_ids):
    """For the recaptured sample, find the dominant (new - old) delta per region and how
    uniform it is. Returns {region: {delta, support, total, frac, addrs_new}}."""
    # Pool old and new addresses (from the recaptured maps) per region.
    old_by_region = collections.defaultdict(list)
    new_by_region = collections.defaultdict(set)
    for mid in sample_ids:
        for a in _addrs_of(old["maps"][mid]):
            old_by_region[_region(a)].append(a)
        for a in _addrs_of(new["maps"][mid]):
            new_by_region[_region(a)].add(a)

    result = {}
    for region, olds in old_by_region.items():
        news = new_by_region.get(region, set())
        # Also consider new addrs that landed one region up/down (a delta can cross a page).
        nearby_new = set()
        for r in (region - 1, region, region + 1):
            nearby_new |= new_by_region.get(r, set())
        # Count candidate deltas within the window.
        votes = collections.Counter()
        for o in olds:
            for n in nearby_new:
                d = n - o
                if 0 <= d <= DELTA_WINDOW:
                    votes[d] += 1
        total = len(olds)
        if not votes:
            result[region] = {"delta": None, "support": 0, "total": total, "frac": 0.0}
            continue
        # Score EVERY candidate delta by real support = old addrs whose (o+delta) is a captured
        # new addr. Pick the best, but REFUSE the region if a second DISTINCT delta also clears
        # the uniform threshold -- a silent tie-break could otherwise ship wrong addresses
        # (adversarial review finding 2026-06-17: votes.most_common ties on insertion order, and
        # frac alone doesn't catch a spurious co-winner in a dense adjacent-region pool).
        scored = sorted(((sum(1 for o in olds if (o + d) in nearby_new), d) for d in votes),
                        reverse=True)
        best_support, delta = scored[0]
        frac = best_support / total if total else 0.0
        clearing = [d for sup, d in scored if total and sup / total >= UNIFORM_THRESHOLD]
        if len(clearing) > 1:
            result[region] = {"delta": None, "support": best_support, "total": total,
                              "frac": frac, "ambiguous": sorted(clearing)}
        else:
            result[region] = {"delta": delta, "support": best_support, "total": total, "frac": frac}
    return result


def full_regions(old):
    """All regions used across the FULL old dataset (what a rebase must cover), minus volatile."""
    regs = collections.Counter()
    for mid, rec in old.get("maps", {}).items():
        for a in _addrs_of(rec):
            if a >= VOLATILE_BASE:
                continue
            regs[_region(a)] += 1
    return regs


def cmd_analyze(old, new):
    sample = _recaptured_maps(old, new)
    if not sample:
        print("No recaptured maps found (no map's capturedBuild changed between old and new).")
        print("Recapture 1-2 maps on 1.5 with treasure_flags.py session first.")
        return 1
    print(f"Sample (recaptured on 1.5): {len(sample)} map(s) -> {', '.join(sorted(sample, key=int))}\n")

    deltas = measure_deltas(old, new, sample)
    full = full_regions(old)

    print(f"{'region':<16}{'delta':>10}{'uniform':>12}{'sample/full addrs':>20}")
    print("-" * 58)
    uniform_regions = {}
    for region in sorted(full):
        base = region << 20
        full_cnt = full[region]
        d = deltas.get(region)
        if d is None or d["delta"] is None:
            verdict = "AMBIGUOUS" if (d and d.get("ambiguous")) else "NOT SAMPLED"
            dstr = "--"
        else:
            frac = d["frac"]
            ok = frac >= UNIFORM_THRESHOLD
            verdict = f"{'OK' if ok else 'SCATTERED'} {frac*100:.0f}%"
            dstr = f"+0x{d['delta']:X}"
            if ok:
                uniform_regions[region] = d["delta"]
        sample_cnt = d["total"] if d else 0
        print(f"0x{base:011x}{dstr:>10}{verdict:>12}{f'{sample_cnt}/{full_cnt}':>20}")

    print()
    covered = all((deltas.get(r) and deltas[r]["delta"] is not None
                   and deltas[r]["frac"] >= UNIFORM_THRESHOLD) for r in full)
    if covered:
        print("VERDICT: every in-play region is uniform AND covered by the sample.")
        print("  -> Bulk rebase is viable. Run 'rebase', then spot-verify a few maps in-game.")
    else:
        missing = [r for r in full if not deltas.get(r) or deltas[r]["delta"] is None]
        scattered = [r for r in full if deltas.get(r) and deltas[r]["delta"] is not None
                     and deltas[r]["frac"] < UNIFORM_THRESHOLD]
        if missing:
            print(f"VERDICT: {len(missing)} region(s) NOT covered by the sample "
                  f"({', '.join(f'0x{r<<20:011x}' for r in missing)}).")
            print("  -> Recapture a map whose flags land in those regions, then re-analyze.")
        if scattered:
            print(f"VERDICT: {len(scattered)} region(s) SCATTERED (non-uniform delta) "
                  f"({', '.join(f'0x{r<<20:011x}' for r in scattered)}).")
            print("  -> Those regions' maps need real recapture; the rest can still rebase.")
    return 0


def cmd_rebase(old, new, out_path, drop_uncovered=False):
    sample = _recaptured_maps(old, new)
    if not sample:
        print("No recaptured maps -- nothing to base the deltas on. Aborting.")
        return 1
    deltas = measure_deltas(old, new, sample)
    full = full_regions(old)
    # A region is GOOD when covered by the sample AND uniform.
    good = {r for r in full if deltas.get(r) and deltas[r]["delta"] is not None
            and deltas[r]["frac"] >= UNIFORM_THRESHOLD}
    bad = [r for r in full if r not in good]
    if bad and not drop_uncovered:
        print(f"REFUSING to rebase: {len(bad)} region(s) are uncovered or non-uniform "
              f"({', '.join(f'0x{r<<20:011x}' for r in sorted(bad))}).")
        print("  Run 'analyze' to see which. Re-run with --drop-uncovered to DROP those")
        print("  addresses (the tile keeps its other copies; gen_treasure_db's >=3 floor")
        print("  decides if it still ships), or capture a map covering them.")
        return 1
    if bad:
        print(f"--drop-uncovered: dropping addresses in {len(bad)} unmeasured region(s): "
              f"{', '.join(f'0x{r<<20:011x}' for r in sorted(bad))}")

    region_delta = {r: deltas[r]["delta"] for r in good}
    # The live 1.5 build key, lifted from a recaptured map.
    new_key = new["maps"][sample[0]].get("capturedBuild")

    out = json.loads(json.dumps(old))   # deep copy
    sample_set = set(sample)
    rebased_maps = rebased_addrs = dropped_vol = dropped_unc = 0
    thin_tiles = []   # tiles left below the bake's >=3 floor after drops
    for mid, rec in out["maps"].items():
        if mid in sample_set:
            rec.update(new["maps"][mid])   # pass the freshly captured map through verbatim
            continue
        for t in rec.get("tiles", []):
            kept = []
            for pair in t.get("addrs", []):
                a = int(pair[0], 16)
                if a >= VOLATILE_BASE:        # the volatile detail-store family; the bake drops it
                    dropped_vol += 1
                    continue
                d = region_delta.get(_region(a))
                if d is None:                 # an unmeasured/scattered region (only under --drop-uncovered)
                    dropped_unc += 1
                    continue
                kept.append([hex(a + d), pair[1]])
                rebased_addrs += 1
            t["addrs"] = kept
            if 0 < len(kept) < 3:
                thin_tiles.append(f"map {mid} tile ({t['x']},{t['y']}): {len(kept)} addr(s)")
        if rec.get("capturedBuild") is not None:
            rec["capturedBuild"] = new_key   # assert validity on the 1.5 build
        rebased_maps += 1

    with open(out_path, "w", encoding="utf-8") as f:
        json.dump(out, f, indent=2, ensure_ascii=False)
        f.write("\n")
    print(f"Rebased {rebased_addrs} addresses across {rebased_maps} map(s) -> {out_path}")
    print(f"  dropped {dropped_vol} volatile + {dropped_unc} unmeasured-region address(es)")
    print(f"  (sample maps {', '.join(sorted(sample, key=int))} passed through unchanged)")
    if thin_tiles:
        print(f"  WARNING: {len(thin_tiles)} tile(s) now below the bake's >=3 floor (will not ship):")
        for w in thin_tiles:
            print(f"    {w}")
    print("  NEXT: gen_treasure_db.py + spot-verify a few rebased maps in-game (hold-test).")
    return 0


# ---------------------------------------------------------------------------
def _selftest():
    ok = True
    # Synthetic: two regions, known uniform deltas; one map recaptured, one not.
    DA, DB = 0x6500, 0x6440
    old = {"maps": {
        "10": {"name": "Sample", "capturedBuild": {"timeDateStamp": 1, "sizeOfImage": 9},
               "tiles": [{"x": 0, "y": 0, "addrs": [["0x140d00100", "0x1"], ["0x141100200", "0x1"]]},
                         {"x": 1, "y": 1, "addrs": [["0x140d00500", "0x1"], ["0x141100600", "0x1"]]}]},
        "20": {"name": "Other", "capturedBuild": {"timeDateStamp": 1, "sizeOfImage": 9},
               "tiles": [{"x": 2, "y": 2, "addrs": [["0x140d00900", "0x1"], ["0x141100a00", "0x1"]]}]},
    }}
    # New: only map 10 recaptured (build key changed); addrs shifted by the region deltas.
    new = {"maps": {
        "10": {"name": "Sample", "capturedBuild": {"timeDateStamp": 2, "sizeOfImage": 99},
               "tiles": [{"x": 0, "y": 0, "addrs": [[hex(0x140d00100 + DA), "0x1"],
                                                    [hex(0x141100200 + DB), "0x1"]]},
                         {"x": 1, "y": 1, "addrs": [[hex(0x140d00500 + DA), "0x1"],
                                                    [hex(0x141100600 + DB), "0x1"]]}]},
    }}
    sample = _recaptured_maps(old, new)
    assert sample == ["10"], f"recaptured detection: {sample}"
    deltas = measure_deltas(old, new, sample)
    rA = deltas[_region(0x140d00100)]
    rB = deltas[_region(0x141100200)]
    if rA["delta"] == DA and rA["frac"] == 1.0:
        print(f"  region 0x140d delta = +0x{rA['delta']:X} (frac {rA['frac']:.0%})  OK")
    else:
        print(f"  region 0x140d: FAIL {rA}"); ok = False
    if rB["delta"] == DB and rB["frac"] == 1.0:
        print(f"  region 0x141100 delta = +0x{rB['delta']:X} (frac {rB['frac']:.0%})  OK")
    else:
        print(f"  region 0x141100: FAIL {rB}"); ok = False

    # Rebase map 20 (not recaptured) and confirm its addrs shifted by the right region deltas.
    import tempfile, os
    out_path = os.path.join(tempfile.mkdtemp(), "out.json")
    cmd_rebase(old, new, out_path)
    res = json.load(open(out_path))
    m20 = res["maps"]["20"]["tiles"][0]["addrs"]
    want = {hex(0x140d00900 + DA), hex(0x141100a00 + DB)}
    got = {p[0] for p in m20}
    if got == want and res["maps"]["20"]["capturedBuild"]["timeDateStamp"] == 2:
        print(f"  rebase of non-sample map 20: OK ({got})")
    else:
        print(f"  rebase map 20: FAIL got={got} key={res['maps']['20']['capturedBuild']}"); ok = False

    # Scattered region must refuse: corrupt one new addr so frac drops below threshold.
    new_bad = json.loads(json.dumps(new))
    new_bad["maps"]["10"]["tiles"][0]["addrs"][0][0] = "0x140d09999"   # off-delta
    rc = cmd_rebase(old, new_bad, out_path)
    if rc == 1:
        print("  scattered-region refusal: OK")
    else:
        print("  scattered-region refusal: FAIL (should have refused)"); ok = False
    return ok


def main():
    a = sys.argv[1:]
    if not a or a[0] in ("-h", "--help"):
        print(__doc__); return
    if a[0] == "--selftest":
        sys.exit(0 if _selftest() else 1)
    if a[0] == "analyze" and len(a) >= 3:
        old = json.load(open(a[1], encoding="utf-8"))
        new = json.load(open(a[2], encoding="utf-8"))
        sys.exit(cmd_analyze(old, new))
    if a[0] == "rebase" and len(a) >= 4:
        old = json.load(open(a[1], encoding="utf-8"))
        new = json.load(open(a[2], encoding="utf-8"))
        sys.exit(cmd_rebase(old, new, a[3], drop_uncovered="--drop-uncovered" in a))
    print(__doc__)
    sys.exit(2)


if __name__ == "__main__":
    main()
