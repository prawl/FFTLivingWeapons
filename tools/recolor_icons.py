#!/usr/bin/env python
"""
Recolor equipment menu icons from the vanilla originals to per-item tints.

Pipeline per item (both the 100x100 full icon and the 48x48 small icon):
  vanilla BC7 .tex (Pac Files/0008) -> FF16Tools tex-conv -> DDS -> Pillow recolor
  (HSV: fix hue + saturation, scale value to preserve shading) -> img-conv --no-chunk-compression
  -> .tex placed in the mod tree.

ICON_TINTS = {id: (hue, sat, value_mult)} -- hue/sat in 0..1, value_mult scales brightness.
Run: python tools/recolor_icons.py
"""
import subprocess, shutil, colorsys, sys
from pathlib import Path
from PIL import Image

ROOT = Path(__file__).resolve().parent.parent
FF16 = Path(r"C:\Users\ptyRa\Downloads\FF16Tools.CLI-1.13.2-win-x64\win-x64\FF16Tools.CLI.exe")
VANILLA = Path(r"C:\Users\ptyRa\OneDrive\Desktop\Pac Files\0008\ui\ffto\icon")
WORK = ROOT / "working" / "icons"
MOD = ROOT / "mod" / "FFTIVC" / "data" / "enhanced" / "ui" / "ffto" / "icon"

# id -> (hue, saturation, value_mult). Chosen to match each knife's identity.
ICON_TINTS = {
    1:  (0.09, 0.50, 0.92),   # Cutpurse        tarnished bronze
    2:  (0.52, 0.20, 1.12),   # Quicksilver     pale silver-blue
    3:  (0.79, 0.72, 0.80),   # Gloomfang       dark violet (shadow)
    4:  (0.60, 0.58, 0.95),   # Hushblade       cold blue (silence)
    5:  (0.60, 0.06, 1.18),   # Argent Dirk     bright platinum
    6:  (0.985, 0.72, 1.00),  # Sanguine Gauche crimson (HP-leech)
    7:  (0.60, 0.12, 0.76),   # Adamant Fang    dark gunmetal
    8:  (0.27, 0.60, 0.90),   # Mortal Coil     necrotic green (Doom)
    9:  (0.46, 0.66, 1.05),   # Galewind        storm teal (Wind)
    10: (0.72, 0.42, 1.10),   # Dreamsever      dream lavender (Sleep)
}


def recolor(im, hue, sat, val_mult):
    px = im.load()
    for y in range(im.height):
        for x in range(im.width):
            r, g, b, a = px[x, y]
            if a < 8:
                continue
            _, _, v = colorsys.rgb_to_hsv(r / 255, g / 255, b / 255)
            nr, ng, nb = colorsys.hsv_to_rgb(hue, sat, min(1.0, v * val_mult))
            px[x, y] = (int(nr * 255), int(ng * 255), int(nb * 255), a)
    return im


def process(item_id, hue, sat, val_mult):
    WORK.mkdir(parents=True, exist_ok=True)
    for sub, prefix in [("equip_item", f"ei_{item_id:03d}"), ("equip_item_s", f"ei_s_{item_id:03d}")]:
        name = f"{prefix}_uitx"
        src = VANILLA / sub / "texture" / f"{name}.tex"
        if not src.exists():
            print(f"  MISSING {src}"); continue
        work_tex = WORK / f"{name}.tex"
        shutil.copy(src, work_tex)
        subprocess.run([str(FF16), "tex-conv", "-i", str(work_tex)], capture_output=True)
        im = Image.open(WORK / f"{name}.dds").convert("RGBA")
        recolor(im, hue, sat, val_mult)
        png = WORK / f"{name}.png"
        im.save(png)
        work_tex.unlink(missing_ok=True)
        subprocess.run([str(FF16), "img-conv", "-i", str(png), "--no-chunk-compression"], capture_output=True)
        dst = MOD / sub / "texture"
        dst.mkdir(parents=True, exist_ok=True)
        shutil.move(str(WORK / f"{name}.tex"), str(dst / f"{name}.tex"))
        print(f"  {name} -> {sub}")


def main():
    only = set(int(a) for a in sys.argv[1:] if a.isdigit())
    for i, (h, s, v) in ICON_TINTS.items():
        if only and i not in only:
            continue
        print(f"id{i}:")
        process(i, h, s, v)
    print("Done. Recolored icons placed in the mod tree.")


if __name__ == "__main__":
    main()
