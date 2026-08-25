#!/usr/bin/env python
"""LW-319: measure the exact on-screen RGB of each ruled Grows-line color slot.

WRITES GAME MEMORY (same-length flavor-line pokes, the LW-307 proven mechanism) and
RESTORES on exit, including Ctrl+C. Undo bank: lw307_markup_undo.json (shared with the
base probe; its `restore` verb is the manual fallback if this probe or the game dies
mid-poke: a stranded card keeps the last poked color until restored).

WHY. The glow arc (docs/TODO.md LW-319) paints weapon glows in the same hues the card
text already wears. The card's <color=NN> slots are TEXT renderer indices whose real
rendered colors live only on screen, so the owner asked for measurement over eyeballing:
poke each ruled slot onto a live card, screenshot it, and read the glyph pixels.

METHOD. Reuses lw307_card_markup_probe wholesale (scan / poke-all / restore). For each
of the eight ruled lane slots (tools/lib/flavor.py LANE_COLOR_SLOT, the pinned ruling
table, imported so this probe cannot drift from the bake): poke the weapon's flavor line
with that slot, the owner flicks the equip-list cursor off and back to force a redraw,
and the probe grabs a full-screen frame (PIL ImageGrab). Analysis needs no window
geometry: a pixel belongs to the tagged span iff it differs from the SAME pixel in two
OTHER slots' frames (identical text layout, only glyph color moves), minus an animation
mask built from two pristine baseline frames a second apart. The modal exact RGB over
those pixels is the slot's color: glyph cores share one value while backgrounds vary.

OUTPUT. lw319_text_rgb_map.json (lane -> slot, modal RGB hex, sample counts) plus one
cropped provenance PNG per lane (lw319_sample_<lane>.png, the candidate-pixel bbox).
Two lanes measuring near-identical RGB means a redraw flick was missed; the probe warns
and those slots should be re-run.

USAGE (game running, a living weapon's equip card open and cursor parked on it):
  python lw319_text_sampler.py "Warbrand"
At each step: flick the cursor off and back onto the weapon, then press Enter. 10 frames.
"""
import json
import pathlib
import sys
import time

import numpy as np
from PIL import Image, ImageGrab

HERE = pathlib.Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))
import lw307_card_markup_probe as base  # noqa: E402

sys.path.insert(0, str(HERE.parent))
from lib.flavor import LANE_COLOR_SLOT  # noqa: E402

RESULTS = HERE / "lw319_text_rgb_map.json"
DIFF_T = 24   # any-channel step that counts as "changed between two slot frames"
ANIM_T = 8    # any-channel step that brands a pixel as animating between baselines
MIN_PX = 200  # fewer candidates than this = the redraw flick probably missed
NEAR_T = 12   # two lanes' modal RGBs within this per channel = a missed flick warning


def grab():
    return np.asarray(ImageGrab.grab().convert("RGB")).astype(np.int16)


def moved(a, b, t):
    return (np.abs(a - b) > t).any(axis=2)


def modal_top5(frame, mask):
    px = frame[mask].astype(np.uint8).reshape(-1, 3)
    tuples, counts = np.unique(px, axis=0, return_counts=True)
    order = np.argsort(counts)[::-1][:5]
    return [(tuples[i].tolist(), int(counts[i])) for i in order]


def crop_png(frame, mask, path, pad=8):
    ys, xs = np.nonzero(mask)
    y0, y1 = max(0, ys.min() - pad), ys.max() + pad
    x0, x1 = max(0, xs.min() - pad), xs.max() + pad
    Image.fromarray(frame[y0:y1, x0:x1].astype(np.uint8)).save(path)


def capture(lanes):
    """The owner-paced sitting: two pristine baselines, then one poked frame per lane.
    Restore runs on ANY exit; frames come back in memory for the offline analysis."""
    print(f"\n2 baselines + {len(lanes)} slots. After each poke: flick the cursor off and")
    print("back onto the weapon so the card redraws, then press Enter here.\n")
    frames = {}
    try:
        input("pristine card showing? press Enter for baseline 1... ")
        b1 = grab()
        time.sleep(1.0)
        b2 = grab()
        anim = moved(b1, b2, ANIM_T)
        print(f"  animation mask: {int(anim.sum())} px excluded")
        for lane, slot in lanes:
            base.cmd_poke("all", slot)
            input(f"  [{lane}] slot {slot} poked; flick cursor, then Enter... ")
            frames[lane] = grab()
    finally:
        base.cmd_restore()
    return frames, anim


def analyze(frames, anim):
    results = {}
    names = list(frames)
    if len(names) < 3:
        sys.exit("need at least 3 captured slots to isolate glyphs by cross-slot diff")
    for lane in names:
        ref = [n for n in names if n != lane][:2]
        m = (moved(frames[lane], frames[ref[0]], DIFF_T)
             & moved(frames[lane], frames[ref[1]], DIFF_T) & ~anim)
        n = int(m.sum())
        slot = LANE_COLOR_SLOT[lane]
        if n < MIN_PX:
            print(f"  [{lane}] only {n} candidate px; flick likely missed, NOT recorded")
            continue
        top = modal_top5(frames[lane], m)
        (r, g, b), cnt = top[0]
        crop_png(frames[lane], m, HERE / f"lw319_sample_{lane.replace('+', '_')}.png")
        results[lane] = {
            "slot": slot,
            "rgb": [r, g, b],
            "hex": "#{:02X}{:02X}{:02X}".format(r, g, b),
            "candidate_px": n,
            "modal_share": round(cnt / n, 3),
            "top5": [{"rgb": t, "n": c} for t, c in top],
        }
        print(f"  [{lane}] slot {slot}: #{r:02X}{g:02X}{b:02X} over {n} px "
              f"(modal share {cnt / n:.0%})")
    for i, a in enumerate(results):
        for b in list(results)[i + 1:]:
            pa, pb = results[a]["rgb"], results[b]["rgb"]
            if max(abs(pa[c] - pb[c]) for c in range(3)) < NEAR_T:
                print(f"  WARNING: {a} and {b} measured near-identical RGB; a redraw "
                      f"flick likely missed. Re-run those slots.")
    return results


def main():
    key = sys.argv[1] if len(sys.argv) > 1 else "Warbrand"
    base.cmd_scan(key)
    st = json.loads(base.UNDO.read_text(encoding="utf8"))
    while not st["hits"]:
        input("no copies found; open the weapon's equip card, then press Enter to re-scan... ")
        base.cmd_scan(key)
        st = json.loads(base.UNDO.read_text(encoding="utf8"))
    frames, anim = capture(sorted(LANE_COLOR_SLOT.items()))
    results = analyze(frames, anim)
    if not results:
        sys.exit("nothing measured; no map written")
    out = dict(results)
    out["_provenance"] = (f"measured live sitting {time.strftime('%Y-%m-%d')}, '{key}' card, "
                          "lw307 poke mechanism; modal glyph RGB per LANE_COLOR_SLOT slot; "
                          "crops lw319_sample_*.png")
    RESULTS.write_text(json.dumps(out, indent=1, sort_keys=True) + "\n", encoding="utf8")
    print(f"\nRGB map ({len(results)} lanes) -> {RESULTS}")


if __name__ == "__main__":
    main()
