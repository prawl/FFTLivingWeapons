#!/usr/bin/env python
"""LW-319: offline re-read of the 2026-08-25 sampler sitting's provenance crops.

READS PNGs ONLY, touches no game memory. The live sitting (lw319_text_sampler.py)
captured the full dual-monitor desktop, so its cross-slot diff mask drowned in the
probe's own scrolling terminal and the modal RGB landed on terminal pixels instead of
glyphs. The crops it saved still contain the recolored flavor tail at full desktop
resolution, and the owner's typed hue reads in each crop's visible transcript
independently confirm every slot rendered its ruled hue. This script measures the RGB
the failed analysis should have produced, from those same crops.

METHOD. Per lane: crop a hand-picked FRACTIONAL box around the colored flavor-tail
text (fractions survive any viewer scaling; boxes were read off the crops by eye and
each excludes the equip-list icons, the second monitor, and the wallpaper), filter to
pixels inside that lane's expected hue window (HSV; windows are broad families, the
measured value is still whatever RGB the game actually drew), and take the modal exact
RGB: glyph cores share one value while backgrounds and gradients fragment. Writes
lw319_text_rgb_map.json and a lw319_read_<lane>.png mini-crop per lane so the sampled
region can be eyeballed against the numbers.
"""
import colorsys
import json
import pathlib
import sys

import numpy as np
from PIL import Image

HERE = pathlib.Path(__file__).resolve().parent
RESULTS = HERE / "lw319_text_rgb_map.json"

# lane -> (crop file suffix, slot, fractional box x0,y0,x1,y1, hue window lo,hi deg, s_min, v_min)
LANES = {
    "HP":          ("HP",          "81", (0.00, 0.50, 0.34, 1.00), (12, 48),  0.45, 0.55),
    "MA":          ("MA",          "50", (0.42, 0.15, 0.59, 0.50), (205, 250), 0.30, 0.55),
    "PA":          ("PA",          "30", (0.00, 0.20, 0.34, 1.00), (335, 25), 0.40, 0.35),
    "PA+MA":       ("PA_MA",       "95", (0.38, 0.10, 0.54, 0.50), (255, 300), 0.20, 0.45),
    "PA+MA+Brave": ("PA_MA_Brave", "94", (0.38, 0.10, 0.54, 0.50), (210, 250), 0.20, 0.60),
    "Speed":       ("Speed",       "40", (0.38, 0.05, 0.54, 0.50), (80, 160), 0.25, 0.45),
    "WP":          ("WP",          "83", (0.30, 0.63, 0.45, 0.79), (160, 205), 0.30, 0.55),
    "WP+Faith":    ("WP_Faith",    "60", (0.30, 0.63, 0.45, 0.79), (30, 60),  0.30, 0.45),
}
MIN_PX = 300


def hue_mask(px, lo, hi, s_min, v_min):
    r, g, b = px[..., 0] / 255.0, px[..., 1] / 255.0, px[..., 2] / 255.0
    mx, mn = np.max(px / 255.0, axis=-1), np.min(px / 255.0, axis=-1)
    v = mx
    s = np.where(mx > 0, (mx - mn) / np.where(mx > 0, mx, 1), 0)
    h = np.zeros_like(v)
    d = np.where(mx - mn > 0, mx - mn, 1)
    sel = mx == r
    h[sel] = (60 * ((g - b) / d) % 360)[sel]
    sel = mx == g
    h[sel] = (60 * ((b - r) / d) + 120)[sel]
    sel = mx == b
    h[sel] = (60 * ((r - g) / d) + 240)[sel]
    in_hue = (h >= lo) & (h <= hi) if lo <= hi else (h >= lo) | (h <= hi)
    return in_hue & (s >= s_min) & (v >= v_min)


def main():
    results = {}
    for lane, (suffix, slot, (x0, y0, x1, y1), (lo, hi), s_min, v_min) in LANES.items():
        src = HERE / f"lw319_sample_{suffix}.png"
        if not src.exists():
            print(f"  [{lane}] MISSING crop {src.name}; skipped")
            continue
        im = np.asarray(Image.open(src).convert("RGB"))
        H, W = im.shape[:2]
        box = im[int(y0 * H):int(y1 * H), int(x0 * W):int(x1 * W)]
        Image.fromarray(box).save(HERE / f"lw319_read_{suffix}.png")
        m = hue_mask(box.astype(np.float64), lo, hi, s_min, v_min)
        n = int(m.sum())
        if n < MIN_PX:
            print(f"  [{lane}] only {n} px in the hue window; box or window wrong, skipped")
            continue
        tuples, counts = np.unique(box[m].reshape(-1, 3), axis=0, return_counts=True)
        order = np.argsort(counts)[::-1][:5]
        top = [(tuples[i].tolist(), int(counts[i])) for i in order]
        (r, g, b), cnt = top[0]
        results[lane] = {
            "slot": slot,
            "rgb": [int(r), int(g), int(b)],
            "hex": "#{:02X}{:02X}{:02X}".format(r, g, b),
            "window_px": n,
            "modal_share": round(cnt / n, 3),
            "top5": [{"rgb": [int(c) for c in t], "n": c2} for t, c2 in top],
        }
        print(f"  [{lane}] slot {slot}: #{r:02X}{g:02X}{b:02X} over {n} px "
              f"(modal share {cnt / n:.0%})")
    if len(results) < len(LANES):
        sys.exit(f"only {len(results)}/{len(LANES)} lanes measured; map NOT written")
    out = dict(results)
    out["_provenance"] = ("offline re-read 2026-08-25 of the lw319_sample_*.png sitting crops "
                          "('Warbrand' card, lw307 pokes); modal glyph RGB inside a hand-picked "
                          "text box + hue window per lane; boxes eyeballable in lw319_read_*.png; "
                          "owner's live hue reads visible in the crops' terminal transcript")
    RESULTS.write_text(json.dumps(out, indent=1, sort_keys=True) + "\n", encoding="utf8")
    print(f"\nRGB map ({len(results)} lanes) -> {RESULTS}")


if __name__ == "__main__":
    main()
