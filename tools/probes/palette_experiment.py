#!/usr/bin/env python
"""THE EXPERIMENT: one family, three treatments, judged as a row at real size.

A = what ships now.                       loud, and 44% of the catalogue sits outside the
                                          artist's own hue vocabulary
B = same hues, art-level saturation.      tests "we were simply too loud"
C = the artist's committed vocabulary,    tests "we were in the wrong world"; items separate by
    items separated by VALUE not hue      VALUE and by metal, which is how the artist does it

python experiment.py <out.png> [family]
"""
import sys, os, pathlib
from PIL import Image, ImageDraw

ROOT = pathlib.Path(r"C:\Users\ptyRa\Dev\FFTLivingWeapons")
sys.path.insert(0, str(ROOT / "tools"))
import recolor_icons as ri

CACHE = pathlib.Path(os.environ.get("TEMP", ".")) / "vanilla_cache"

FAMILIES = {
    # id -> C treatment: a hue from the artist's committed vocabulary (0-59, 80-99, 210-239),
    # with the family separated by VALUE and by its existing metal rather than by hue.
    "rods": {
        51: (0.236, 0.55, 0.55),   # Wellspring   the 80-99 green pocket, dark
        52: (0.125, 0.80, 1.05),   # Spark        gold, brightest
        53: (0.042, 0.75, 0.80),   # Ember        red-orange, mid
        54: (0.611, 0.35, 1.02),   # Frost        the 210-239 violet pocket, pale
        55: (0.083, 0.50, 0.55),   # Hushward     amber, dark
        56: (0.639, 0.45, 0.45),   # Umbral       violet, darkest
        57: (0.139, 0.70, 0.85),   # Dragon       yellow, mid
        58: (0.111, 0.65, 1.15),   # Rod of Faith gold, palest
    },
    "swords": {
        19: (0.083, 0.55, 0.60), 20: (0.639, 0.40, 0.45), 21: (0.125, 0.70, 1.02),
        22: (0.236, 0.55, 0.55), 23: (0.014, 0.80, 0.72), 24: (0.611, 0.45, 0.95),
        25: (0.250, 0.50, 0.42), 26: (0.042, 0.85, 0.85), 27: (0.625, 0.35, 0.38),
        28: (0.139, 0.45, 1.10), 29: (0.653, 0.40, 0.30), 30: (0.611, 0.55, 0.62),
        31: (0.111, 0.80, 1.15), 32: (0.097, 0.30, 1.12), 67: (0.028, 0.70, 0.50),
    },
}


def van(iid, surface="small"):
    pfx = "ei_s" if surface == "small" else "ei"
    return Image.open(CACHE / f"{pfx}_{ri.SRC.get(iid, iid):03d}_uitx.dds").convert("RGBA")


def quieter(tint, factor=0.48):
    """Treatment B: the shipped hue, pulled back toward the art's own saturation."""
    h, s, v = tint
    return (h, s * factor, v)


def row(ids, render, label_h=0):
    s = Image.new("RGBA", (48 * len(ids) + 8 * (len(ids) + 1), 48 + 8), (28, 30, 34, 255))
    for k, i in enumerate(ids):
        im = render(i)
        s.paste(im, (8 + k * 56, 4), im)
    return s


def main(out_path, family="rods"):
    tbl = FAMILIES[family]
    ids = sorted(tbl)
    rows = [
        ("VANILLA  the artist's own sprites", lambda i: van(i)),
        ("A  what ships now", lambda i: ri.route(van(i).copy(), i, ri.ICON_TINTS[i], "small")),
        ("B  same hues, art-level saturation",
         lambda i: ri.route(van(i).copy(), i, quieter(ri.ICON_TINTS[i]), "small")),
        ("C  the artist's vocabulary, split by value",
         lambda i: ri.route(van(i).copy(), i, tbl[i], "small")),
    ]
    scale = 3
    strips = [(lab, row(ids, fn)) for lab, fn in rows]
    w = strips[0][1].width * scale
    h = (strips[0][1].height * scale + 22) * len(strips) + 10
    sheet = Image.new("RGBA", (w, h), (24, 24, 28, 255))
    d = ImageDraw.Draw(sheet)
    y = 5
    for lab, st in strips:
        sheet.paste(st.resize((st.width * scale, st.height * scale), Image.NEAREST), (0, y))
        y += st.height * scale
        d.text((6, y + 4), lab, fill=(235, 235, 225, 255))
        y += 22
    sheet.save(out_path)
    # report the saturation each treatment lands on
    import colorsys

    def sat(im):
        px = im.load(); t = 0.0; n = 0
        for yy in range(im.height):
            for xx in range(im.width):
                r, g, b, a = px[xx, yy]
                if a < ri.HALO_HI:
                    continue
                t += colorsys.rgb_to_hsv(r / 255, g / 255, b / 255)[1]; n += 1
        return t / n if n else 0
    for lab, fn in rows:
        m = sum(sat(fn(i)) for i in ids) / len(ids)
        print(f"  {lab:<45} mean saturation {m:.3f}")
    print(out_path)


if __name__ == "__main__":
    main(sys.argv[1], sys.argv[2] if len(sys.argv) > 2 else "rods")
