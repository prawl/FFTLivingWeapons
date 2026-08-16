#!/usr/bin/env python
"""Sample every vanilla equipment sprite and describe the artist's palette.

python palette_sample.py <out.json>

Rules of the sample, each one load-bearing:
  - SOLID pixels only (alpha >= HALO_HI). Every sprite sits in a neutral haze the artist drew
    around it; counting that would report the haze as the game's favourite colour.
  - BOTH surfaces, counted separately as well as together, because the 100px card and the 48px
    icon are different drawings and the icons are far more saturated.
  - Two weightings, because they answer different questions: PIXELS (how much of the art is this
    colour) and ITEMS (how many different items use it at all). A book is 2,600 solid pixels and
    a knife is 150, so a pixel count alone is a book-and-bag census.
  - Neutrals split out at saturation < 0.18, since that is where the artist's greys and steels
    live and they would otherwise swamp every chromatic reading.
"""
import sys, os, pathlib, shutil, subprocess, json, colorsys, math
from collections import defaultdict
from PIL import Image

ROOT = pathlib.Path(r"C:\Users\ptyRa\Dev\FFTLivingWeapons")
sys.path.insert(0, str(ROOT / "tools"))
import recolor_icons as ri
from lib.paths import FF16

CACHE = pathlib.Path(os.environ.get("TEMP", ".")) / "vanilla_cache"
CACHE.mkdir(parents=True, exist_ok=True)
NEUTRAL_SAT = 0.18
HUE_BINS = 36           # 10 degrees each


def decode(sub, pfx, iid):
    """Decode once, keep the dds: 468 sprites through the texture tool is slow to redo."""
    dds = CACHE / f"{pfx}_{iid:03d}_uitx.dds"
    if not dds.exists():
        src = ri.VANILLA / sub / "texture" / f"{pfx}_{iid:03d}_uitx.tex"
        if not src.exists():
            return None
        w = CACHE / f"{pfx}_{iid:03d}_uitx.tex"
        shutil.copy(src, w)
        subprocess.run([str(FF16), "tex-conv", "-i", str(w)], capture_output=True)
        w.unlink(missing_ok=True)
    if not dds.exists():
        return None
    return Image.open(dds).convert("RGBA")


items = {i: c for i, c in ri._CATEGORY.items() if c}
pix = {"card": defaultdict(int), "small": defaultdict(int)}      # hue bin -> pixels (chromatic)
pix_items = {"card": defaultdict(set), "small": defaultdict(set)}
sat_hist = {"card": defaultdict(int), "small": defaultdict(int)}  # sat decile -> pixels
val_hist = {"card": defaultdict(int), "small": defaultdict(int)}
neutral = {"card": 0, "small": 0}
total = {"card": 0, "small": 0}
swatches = defaultdict(lambda: {"n": 0, "r": 0, "g": 0, "b": 0, "items": set()})
per_item = {}

for iid in sorted(items):
    if iid not in ri.ICON_TINTS and ri._CATEGORY.get(iid) is None:
        continue
    row = {"id": iid, "category": items[iid], "name": ri._NAME.get(iid)}
    for sub, pfx, sf in (("equip_item", "ei", "card"), ("equip_item_s", "ei_s", "small")):
        im = decode(sub, pfx, ri.SRC.get(iid, iid))
        if im is None:
            continue
        px = im.load()
        n = nchrom = 0
        hsum = [0.0, 0.0]
        for y in range(im.height):
            for x in range(im.width):
                r, g, b, a = px[x, y]
                if a < ri.HALO_HI:
                    continue
                h, s, v = colorsys.rgb_to_hsv(r / 255, g / 255, b / 255)
                n += 1
                total[sf] += 1
                sat_hist[sf][min(9, int(s * 10))] += 1
                val_hist[sf][min(9, int(v * 10))] += 1
                if s < NEUTRAL_SAT:
                    neutral[sf] += 1
                    continue
                nchrom += 1
                hb = min(HUE_BINS - 1, int(h * HUE_BINS))
                pix[sf][hb] += 1
                pix_items[sf][hb].add(iid)
                hsum[0] += math.cos(2 * math.pi * h) * s * v
                hsum[1] += math.sin(2 * math.pi * h) * s * v
                key = (hb, min(4, int(s * 5)), min(4, int(v * 5)))
                sw = swatches[key]
                sw["n"] += 1; sw["r"] += r; sw["g"] += g; sw["b"] += b
                sw["items"].add(iid)
        row[f"{sf}_solid"] = n
        row[f"{sf}_chromatic"] = nchrom
    per_item[iid] = row
    if iid % 40 == 0:
        print(f"  ...{iid}")

out = {
    "neutral_share": {k: round(neutral[k] / total[k] * 100, 1) for k in total},
    "total_solid": total,
    "hue_pixels": {k: {str(b): v for b, v in sorted(pix[k].items())} for k in pix},
    "hue_items": {k: {str(b): len(v) for b, v in sorted(pix_items[k].items())} for k in pix_items},
    "sat_hist": {k: {str(b): v for b, v in sorted(sat_hist[k].items())} for k in sat_hist},
    "val_hist": {k: {str(b): v for b, v in sorted(val_hist[k].items())} for k in val_hist},
    "swatches": sorted(
        ({"hue_bin": k[0], "sat_band": k[1], "val_band": k[2], "pixels": v["n"],
          "items": len(v["items"]),
          "rgb": [round(v["r"] / v["n"]), round(v["g"] / v["n"]), round(v["b"] / v["n"])]}
         for k, v in swatches.items() if v["n"] >= 200),
        key=lambda d: -d["pixels"]),
    "per_item": list(per_item.values()),
}
pathlib.Path(sys.argv[1]).write_text(json.dumps(out), encoding="utf-8")
print(f"\nneutral share (sat < {NEUTRAL_SAT}): card {out['neutral_share']['card']}%, "
      f"icon {out['neutral_share']['small']}%")
print(f"solid pixels sampled: card {total['card']}, icon {total['small']}")
print(f"swatch cells with >=200 pixels: {len(out['swatches'])}")
print(f"-> {sys.argv[1]}")
