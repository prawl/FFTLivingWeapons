#!/usr/bin/env python
"""LW-329: map the card's numeric color slots to hues, one owner sitting.

WRITES GAME MEMORY (same-length flavor-line pokes, the LW-307 proven mechanism) and
RESTORES on exit, including Ctrl+C. Undo bank: lw307_markup_undo.json (shared with the
base probe; its `restore` verb is the manual fallback).

WHY. The Grows-line color ruling (docs/TODO.md LW-329) needs each ruled hue (green, red,
blue, orange, cyan, gold, purple, magenta) matched to a real <color=NN> slot the card
actually renders. Only 80 = yellow is proven. This drives one sitting: it pokes a weapon's
flavor line with each candidate NN in turn; the owner reads the card and types what hue
they see; the answers land in lw329_palette_map.json, the citable palette map.

METHOD. Reuses lw307_card_markup_probe wholesale: scan banks every heap copy of the
weapon's flavor line, each step re-tags ALL copies at identical byte length with the next
two-digit color, the final step restores the pristine line. Between steps the owner flicks
the equip-list cursor off and back onto the weapon to force a card redraw.

USAGE (game running, the target weapon's equip card reachable):
  python lw329_palette_cycler.py "Warbrand"             # default sweep: 80 first, then 10..95 step 5
  python lw329_palette_cycler.py "Warbrand" 60 90 2     # fine sweep for a promising band
At each step type the hue you see (enter = unreadable/skip, q = quit early). Restore runs
on any exit; re-run `lw307_card_markup_probe.py restore` if the game or probe dies mid-poke.
"""
import json
import pathlib
import sys

HERE = pathlib.Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))
import lw307_card_markup_probe as base  # noqa: E402

RESULTS = HERE / "lw329_palette_map.json"


def sweep_values(argv):
    if len(argv) >= 4:
        lo, hi, step = int(argv[2]), int(argv[3]), int(argv[4]) if len(argv) > 4 else 1
        vals = list(range(lo, hi + 1, step))
    else:
        vals = [80] + [v for v in range(10, 96, 5) if v != 80]  # 80 leads: the proven yellow calibrates
    bad = [v for v in vals if not 10 <= v <= 99]
    if bad:
        sys.exit(f"two-digit colors only (the proven form): out of range {bad}")
    return vals


def main():
    if len(sys.argv) < 2:
        print(__doc__)
        return
    key = sys.argv[1]
    vals = sweep_values(sys.argv)
    base.cmd_scan(key)
    st = json.loads(base.UNDO.read_text(encoding="utf8"))
    while not st["hits"]:
        input("no copies found; open the weapon's equip card, then press Enter to re-scan... ")
        base.cmd_scan(key)
        st = json.loads(base.UNDO.read_text(encoding="utf8"))
    prior = json.loads(RESULTS.read_text(encoding="utf8")) if RESULTS.exists() else {}
    results = dict(prior)
    print(f"\n{len(vals)} steps. After each poke: flick the cursor off and back on the weapon,")
    print("read the tagged span, type the hue (enter = skip, q = quit). 80 should read yellow.\n")
    try:
        for v in vals:
            base.cmd_poke("all", f"{v:02d}")
            ans = input(f"  color {v:02d} -> hue? ").strip()
            if ans.lower() == "q":
                break
            if ans:
                results[f"{v:02d}"] = ans
    finally:
        base.cmd_restore()
        RESULTS.write_text(json.dumps(results, indent=1, sort_keys=True) + "\n", encoding="utf8")
        print(f"\npalette map ({len(results)} entries) -> {RESULTS}")


if __name__ == "__main__":
    main()
