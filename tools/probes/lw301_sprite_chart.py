#!/usr/bin/env python
"""LW-301: render every weapon-sheet tile as a NUMBERED chart, so shapes can be named by eye.

Page 1 of FFTPack file 71 (rows 0-255) is the weapon art ([lw251-wep-spr-palette-proven]).
lw301_sprite_boxes.json holds the 150 connected-component boxes found in it. Automated shape
matching failed here repeatedly; the owner's eye did not. This just makes looking cheap.

Every tile is drawn in ONE high-contrast ramp regardless of its real palette, because we are
reading SHAPE, not colour, and vanilla steel-on-steel is the worst case for a silhouette.

USAGE:
  python lw301_sprite_chart.py [out.png] [--cols N] [--zoom N]
"""
import json, pathlib, struct, sys, tempfile
from PIL import Image, ImageDraw

HERE = pathlib.Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))
import lw251_wep_spr_forge as forge

SHEET_W = 256
PAL_BYTES = 512
# 16 steps dark->light; index 0 stays transparent so the cutout survives.
RAMP = [None] + [(int(18 + 15 * i), int(18 + 15 * i), int(24 + 14 * i)) for i in range(15)]


def decode_page1(raw):
    pix = raw[PAL_BYTES:PAL_BYTES + 32768]
    im = Image.new("RGBA", (SHEET_W, 256), (0, 0, 0, 0))
    px = im.load()
    for i in range(len(pix) * 2):
        b = pix[i // 2]
        n = (b & 0xF) if i % 2 == 0 else (b >> 4)
        if n:
            px[i % SHEET_W, i // SHEET_W] = RAMP[n] + (255,)
    return im


def main():
    out = HERE / "lw301_sprite_chart.png"
    cols, zoom = 10, 5
    args = sys.argv[1:]
    for i, a in enumerate(args):
        if a == "--cols": cols = int(args[i + 1])
        elif a == "--zoom": zoom = int(args[i + 1])
        elif not a.startswith("--"): out = pathlib.Path(a)

    wd = tempfile.mkdtemp(prefix="lw301chart_")
    raw = forge.load_vanilla(wd)
    raw = raw[0] if isinstance(raw, tuple) else raw
    page = decode_page1(raw)
    boxes = json.loads((HERE / "lw301_sprite_boxes.json").read_text(encoding="utf-8"))

    cw = max(b["w"] for b in boxes) * zoom + 14
    ch = max(b["h"] for b in boxes) * zoom + 26
    rows = (len(boxes) + cols - 1) // cols
    im = Image.new("RGBA", (cols * cw, rows * ch), (28, 28, 34, 255))
    d = ImageDraw.Draw(im)
    for k, b in enumerate(boxes):
        cx, cy = (k % cols) * cw, (k // cols) * ch
        d.rectangle([cx + 1, cy + 1, cx + cw - 2, cy + ch - 2], outline=(70, 70, 84, 255))
        tile = page.crop((b["x"], b["y"], b["x"] + b["w"], b["y"] + b["h"]))
        tile = tile.resize((b["w"] * zoom, b["h"] * zoom), Image.NEAREST)
        im.alpha_composite(tile, (cx + (cw - tile.width) // 2, cy + 20))
        d.text((cx + 5, cy + 5), str(b["i"]), fill=(255, 210, 120, 255))
    im.save(out)
    print(f"{out}  {len(boxes)} tiles, {im.width}x{im.height}")


main()
