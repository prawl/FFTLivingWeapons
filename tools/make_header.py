#!/usr/bin/env python
"""
Build the mod-page header: a dense grid of every recolored item icon on black -- the item-mod
rhyme of the color mod's "wall of recolored sprites" header.

Loads each item's recolored icon PNG (from the recolor working cache; decodes the committed mod .tex
as a fallback), tiles them in id order (which groups by category), saves working/header/header.png.

Run: python tools/make_header.py [cols] [tile] [--hue]
  --hue  sort tiles by dominant hue (rainbow) instead of id order (category groups)
"""
import json, subprocess, shutil, sys, colorsys
from pathlib import Path
from PIL import Image, ImageFilter

ROOT = Path(__file__).resolve().parent.parent
FF16 = Path(r"C:\Users\ptyRa\Downloads\FF16Tools.CLI-1.13.2-win-x64\win-x64\FF16Tools.CLI.exe")
MODICON = ROOT / "mod" / "FFTIVC" / "data" / "enhanced" / "ui" / "ffto" / "icon" / "equip_item" / "texture"
CACHE = ROOT / "working" / "icons"
WORK = ROOT / "working" / "header"
COLS = int(sys.argv[1]) if len(sys.argv) > 1 and sys.argv[1].isdigit() else 24
TILE = int(sys.argv[2]) if len(sys.argv) > 2 and sys.argv[2].isdigit() else 72
HUE = "--hue" in sys.argv
GAP, PAD = 3, 20


def load_icon(i):
    name = f"ei_{i:03d}_uitx"
    png = CACHE / f"{name}.png"
    if png.exists():
        return Image.open(png).convert("RGBA")
    tex = MODICON / f"{name}.tex"
    if not tex.exists():
        return None
    WORK.mkdir(parents=True, exist_ok=True)
    w = WORK / f"{name}.tex"; shutil.copy(tex, w)
    subprocess.run([str(FF16), "tex-conv", "-i", str(w)], capture_output=True)
    dds = WORK / f"{name}.dds"
    return Image.open(dds).convert("RGBA") if dds.exists() else None


def dom_hue(im):
    s = im.resize((16, 16)); px = s.load(); hs = []
    for y in range(16):
        for x in range(16):
            r, g, b, a = px[x, y]
            if a < 40:
                continue
            h, sat, v = colorsys.rgb_to_hsv(r / 255, g / 255, b / 255)
            if sat > 0.25 and v > 0.2:
                hs.append(h)
    return sum(hs) / len(hs) if hs else 1.0


def main():
    WORK.mkdir(parents=True, exist_ok=True)
    doc = json.loads((ROOT / "data" / "items.json").read_text(encoding="utf-8"))
    ids = sorted(it["id"] for it in doc["items"])
    icons = []
    for i in ids:
        im = load_icon(i)
        if im is not None:
            icons.append(im)
    print(f"loaded {len(icons)} / {len(ids)} icons")
    if HUE:
        icons.sort(key=dom_hue)

    rows = (len(icons) + COLS - 1) // COLS
    W = PAD * 2 + COLS * TILE + (COLS - 1) * GAP
    H = PAD * 2 + rows * TILE + (rows - 1) * GAP
    # subtle vertical gradient background (near-black) + faint vignette
    bg = Image.new("RGB", (W, H))
    for y in range(H):
        t = y / H
        c = (int(10 + 8 * (1 - abs(0.5 - t) * 2)), int(11 + 9 * (1 - abs(0.5 - t) * 2)), int(15 + 12 * (1 - abs(0.5 - t) * 2)))
        for x in range(0, W, 1):
            pass
    bg = Image.new("RGB", (W, H), (9, 10, 14))
    canvas = bg.convert("RGBA")
    for idx, ic in enumerate(icons):
        r, c = divmod(idx, COLS)
        ic2 = ic.resize((TILE, TILE), Image.LANCZOS)
        x = PAD + c * (TILE + GAP); y = PAD + r * (TILE + GAP)
        canvas.alpha_composite(ic2, (x, y))
    out = WORK / ("header_hue.png" if HUE else "header.png")
    canvas.convert("RGB").save(out)
    print(f"saved {out}  ({W}x{H}, {COLS} cols x {rows} rows)")


if __name__ == "__main__":
    main()
