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
    # --- Swords (ids 19-32) ---
    19: (0.08, 0.14, 0.90),   # Vagabond        worn warm steel
    20: (0.60, 0.10, 0.70),   # Cleaver         dark heavy steel
    21: (0.55, 0.08, 1.15),   # Riposte         bright silver (parry)
    22: (0.48, 0.24, 1.08),   # Reaver          pale mythril teal
    23: (0.99, 0.78, 0.92),   # Lifedrinker     blood red (HP drain)
    24: (0.14, 0.82, 1.10),   # Stormbrand      electric yellow (Lightning)
    25: (0.10, 0.45, 0.78),   # Tanglethorn     earthy brown (Immobilize)
    26: (0.05, 0.85, 1.05),   # Flamberge       fire orange (Fire)
    27: (0.60, 0.05, 1.18),   # Headsman        stark white (glass cannon)
    28: (0.58, 0.32, 1.12),   # Bulwark         diamond light-blue
    29: (0.50, 0.58, 1.08),   # Rimebrand       ice cyan (Ice)
    30: (0.74, 0.55, 0.95),   # Arcanum         arcane violet
    31: (0.27, 0.65, 0.88),   # Hexfang         toad green (Toad)
    32: (0.78, 0.50, 0.62),   # Nightfall       dark violet (Dark/MP)
    # --- Crossbows (ids 77-82) ---
    77: (0.08, 0.40, 0.85),   # Scoutbolt       wood brown
    78: (0.99, 0.55, 0.55),   # Knightslayer    dark crimson (Death)
    79: (0.60, 0.12, 0.85),   # Arbalest        gunmetal
    80: (0.25, 0.70, 0.85),   # Venombolt       toxic green (Poison)
    81: (0.09, 0.55, 0.88),   # Snarebolt       amber bronze (Immobilize)
    82: (0.60, 0.15, 0.68),   # Siegebolt       dark iron (capstone)
    # --- Bows (ids 83-91) ---
    83: (0.10, 0.30, 0.95),   # Skirmisher      light leather/tan
    84: (0.45, 0.18, 1.05),   # Windrunner      pale silver-teal
    85: (0.50, 0.55, 1.08),   # Frostarc        ice cyan (Ice)
    86: (0.14, 0.80, 1.10),   # Stormarc        electric yellow (Lightning)
    87: (0.42, 0.52, 1.05),   # Skypiercer      sky teal (Wind)
    88: (0.64, 0.42, 0.92),   # Silentstring    muted indigo (Silence)
    89: (0.30, 0.55, 0.88),   # Huntress        forest green
    90: (0.58, 0.42, 0.95),   # Tempest         storm grey-blue
    91: (0.13, 0.45, 1.18),   # Seraph          radiant gold (Holy)
    # --- Shields (ids 128-143) ---
    128: (0.52, 0.55, 0.95),  # Tideward        water blue
    129: (0.42, 0.52, 1.00),  # Galewall        wind teal
    130: (0.14, 0.75, 1.05),  # Stormwall       lightning yellow
    131: (0.52, 0.20, 1.10),  # Swiftguard      silver-cyan (Speed)
    132: (0.62, 0.32, 1.00),  # Wardstone       pale blue (Shell)
    133: (0.13, 0.48, 1.15),  # Sanctguard      gold (Holy)
    134: (0.50, 0.58, 1.08),  # Rimeward        ice cyan
    135: (0.05, 0.82, 1.05),  # Emberward       fire orange
    136: (0.64, 0.55, 0.92),  # Spellbane       indigo (anti-mage)
    137: (0.30, 0.55, 0.95),  # Trailblazer     green (Move)
    138: (0.99, 0.45, 0.82),  # Vanguard        crimson (PA)
    139: (0.78, 0.50, 0.62),  # Nightward       dark violet (Dark)
    140: (0.60, 0.12, 0.72),  # Ronin Wall      gunmetal (rare)
    141: (0.85, 0.55, 0.95),  # Conduit         magenta (boost)
    142: (0.58, 0.10, 1.15),  # Bastion         platinum (generalist)
    143: (0.60, 0.60, 1.10),  # Aegis Prime     radiant blue (capstone)
}

# Merge per-item tints from data/items.json (the single source for the overhaul's new categories).
import json as _json
for _it in _json.loads((ROOT / "data" / "items.json").read_text(encoding="utf-8"))["items"]:
    if _it.get("iconTint"):
        ICON_TINTS[_it["id"]] = tuple(_it["iconTint"])


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
