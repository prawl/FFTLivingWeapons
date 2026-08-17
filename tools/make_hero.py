#!/usr/bin/env python
"""Build the Nexus GALLERY main image (gallery slot 1), the mod's thumbnail in listings.

Not the same asset as the page banner. The banner (tools/make_banner.py) is a wide strip that
Nexus draws its own title over, so it must carry no words of its own. This is the first picture
in the gallery carousel, which is also what represents the mod in listings and search results.
Nothing is overlaid on it, so this is the one that can carry the pitch.

It sits on the SAME brushed steel plate as the banner (tools/lib/plate.py) so the page reads as
one set: icon wall knocked back to a texture on the right, the pitch on the left, and a few
showpiece icons at a size that survives a thumbnail.

Why it exists: the old main image was a dense grid of every icon on flat black. At listing size
that reads as grey mush, and it sells a texture pack, not a mod whose headline is that weapons
count kills and grow.

Run: python tools/make_hero.py [out.png] [--size WxH]     (default working/header/hero.png,
                                                           1600x900)
"""
import sys
from pathlib import Path
from PIL import Image, ImageDraw, ImageEnhance, ImageFont

sys.path.insert(0, str(Path(__file__).resolve().parent))
from lib.items import load_items
from lib.paths import ROOT
from lib.plate import steel_plate

CACHE = ROOT / "working" / "icons"
WORK = ROOT / "working" / "header"

W, H = 1600, 900
for i, a in enumerate(sys.argv):
    if a == "--size" and i + 1 < len(sys.argv):
        W, H = (int(v) for v in sys.argv[i + 1].lower().split("x"))
_pos, _skip = [], False
for _a in sys.argv[1:]:
    if _skip:
        _skip = False
        continue
    if _a == "--size":
        _skip = True
    elif not _a.startswith("--"):
        _pos.append(_a)
OUT = Path(_pos[0]) if _pos else WORK / "hero.png"

INK = (238, 232, 220)
INK_SOFT = (176, 188, 202)
GOLD = (214, 175, 96)

FONTS = Path("C:/Windows/Fonts")


def font(name, size):
    return ImageFont.truetype(str(FONTS / name), size)


def icon(item_id, small=False):
    p = CACHE / f"{'ei_s_' if small else 'ei_'}{item_id:03d}_uitx.png"
    return Image.open(p).convert("RGBA") if p.exists() else None


NATIVE = 48     # the ei_s_ menu icons are 48px square


def main():
    WORK.mkdir(parents=True, exist_ok=True)
    items = {it["id"]: it for it in load_items()["items"]}
    canvas = steel_plate(W, H)

    # RIGHT: the icon wall, knocked back so it reads as texture behind the showpieces rather
    # than competing with them. Same content as the banner, deliberately.
    ids = sorted(items)
    tile, gap = round(W * 0.032), round(W * 0.004)
    x0, y0 = round(W * 0.375), round(H * 0.03)
    cols = (W - x0) // (tile + gap) + 1
    faded = Image.new("RGBA", (W, H), (0, 0, 0, 0))
    for idx, iid in enumerate(ids):
        im = icon(iid, small=True)
        if im is None:
            continue
        r, c = divmod(idx, cols)
        y = y0 + r * (tile + gap)
        if y > H:
            break
        faded.alpha_composite(im.resize((tile, tile), Image.LANCZOS),
                              (x0 + c * (tile + gap), y))
    canvas.alpha_composite(Image.blend(Image.new("RGBA", (W, H), (0, 0, 0, 0)), faded, 0.34))

    # Showpieces: the MENU icons, not the painted art. Two reasons. They match the banner and
    # the backdrop, so the page is one set; and their alpha is a clean cutout, whereas the
    # painted art carries a faint light halo field that shows as a pale box on a lit plate.
    # Blown up at a whole multiple of 48 with NEAREST, so the pixels stay square and deliberate
    # instead of turning to mush.
    # Chosen for SILHOUETTE variety as much as colour: a lineup of round shield-and-helm blobs
    # reads as one shape repeated, however vivid it is. Sword, harp and crown break that up.
    #   143 Aegis Prime (blue/gold shield)   35 Excalibur (gold sword)
    #   155 Genji Helm (red/gold)            82 Siegebolt (purple/gold crossbow)
    #   164 Hierophant Miter (violet arch)  166 Mana Coronet (cyan/gold)
    for iid, cx, cy, mult in [(143, round(W * 0.616), round(H * 0.428), 8),
                              (35, round(W * 0.853), round(H * 0.239), 7),
                              (155, round(W * 0.869), round(H * 0.711), 6),
                              (82, round(W * 0.640), round(H * 0.805), 5),
                              (164, round(W * 0.481), round(H * 0.194), 4),
                              (166, round(W * 0.790), round(H * 0.489), 4)]:
        im = icon(iid, small=True)
        if im is None:
            continue
        size = NATIVE * mult
        im = ImageEnhance.Color(im).enhance(1.10).resize((size, size), Image.NEAREST)
        canvas.alpha_composite(im, (cx - size // 2, cy - size // 2))

    d = ImageDraw.Draw(canvas, "RGBA")
    L = round(W * 0.055)
    d.text((L, round(H * 0.12)), "LIVING", font=font("constanb.ttf", round(W * 0.072)), fill=INK)
    d.text((L, round(H * 0.215)), "WEAPONS", font=font("constanb.ttf", round(W * 0.072)), fill=INK)
    d.line([(L, round(H * 0.345)), (round(W * 0.375), round(H * 0.345))],
           fill=INK_SOFT + (150,), width=3)
    d.text((L, round(H * 0.375)), "every kill makes the blade stronger",
           font=font("constani.ttf", round(W * 0.0245)), fill=GOLD)

    body = font("constan.ttf", round(W * 0.0175))
    for i, line in enumerate([
        "Weapons count their kills, grow your",
        "stats, and awaken abilities of their own.",
        "",
        "All 234 equippable items rebalanced so",
        "nothing you like goes obsolete, and every",
        "menu icon repainted family by family.",
    ]):
        d.text((L, round(H * 0.48) + i * round(H * 0.048)), line, font=body, fill=INK_SOFT)

    d.text((L, round(H * 0.86)), "FINAL FANTASY TACTICS  -  The Ivalice Chronicles",
           font=font("constan.ttf", round(W * 0.0145)), fill=(126, 140, 156))

    canvas.convert("RGB").save(OUT)
    print(f"saved {OUT}  ({W}x{H})")


if __name__ == "__main__":
    main()
