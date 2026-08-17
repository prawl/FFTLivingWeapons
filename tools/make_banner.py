#!/usr/bin/env python
"""Build the Nexus page banner: the wall of every repainted item icon.

Same concept as the Color Customizer banner (a wall showing the full range of what the mod
does), refreshed for the icon re-pass work. Versus make_header.py:

  1. It reads the CURRENT recolour cache, so it picks up the shield / helmet / hat / crossbow
     re-passes instead of the older washed-out art.
  2. The ground is a warm charcoal with a broad centre glow, grain and a vignette, rather than
     a flat near-black.
  3. The grid is sized so all 234 items land in an exact block with no ragged last row.

TWO ICON SOURCES, and they want different treatment:

  ei_   (default, 100px)  the painted equip-card art. Detailed, but each one carries a soft
        near-black halo baked into its own art, so the ground must stay DARKER than that halo
        or every tile reads as a grey smudge box.
  ei_s_ (--small, 48px)   the small menu icons. Chunky, saturated, clean alpha cutout, no
        halo. They read far better at wall density and they free the ground to run warmer,
        so --small uses a lifted ground. Upscaled 2x NEAREST to keep the pixels crisp.

CANONICAL: the banner currently on the Nexus page is

    python tools/make_banner.py --small --hue --metal --drop-dull 18 --mock

Rerun exactly that after any icon re-pass and re-upload the result; the icons are read live
from the recolour cache, so new art lands in the banner with no other change. The _mock.png it
writes alongside is a PREVIEW ONLY (Nexus's crop plus its own title overlay) - never upload it.
Do NOT bake a title into the banner: Nexus draws its own over the lower left, which is also why
the steel plate is kept darkest there.

Run: python tools/make_banner.py [out.png] [--small] [--hue] [--bg NAME] [--flat] [--mock]
  --small   build from the small menu icons (denser, bolder wall)
  --hue     sort tiles by dominant hue (colour bands) instead of id order (category groups)
  --shuffle scatter the tiles at random instead; overrides --hue
  --seed N  reroll the --shuffle arrangement (default 7; seeded so a good one is repeatable)
  --drop-dull N  cull the N least colourful CHEST pieces (Armor/Clothing/Robe), which read as
            a row of near-identical dark shirt blobs. The grid re-fits to whatever survives.
  --bg NAME ground palette: dracula (default), navy, plum, teal, ink, warm
  --flat    skip the glow/grain ground and use the old flat near-black, for comparison
  --mock    also write <out>_mock.png: the wide crop with Nexus's own bottom scrim and mod
            title laid over it, to check the title stays legible before uploading
"""
import colorsys, random, sys
from pathlib import Path
from PIL import Image, ImageDraw, ImageEnhance, ImageFilter, ImageFont

sys.path.insert(0, str(Path(__file__).resolve().parent))
from lib.items import load_items
from lib.paths import ROOT
from lib.plate import steel_plate

CACHE = ROOT / "working" / "icons"
WORK = ROOT / "working" / "header"

SMALL = "--small" in sys.argv
HUE = "--hue" in sys.argv
FLAT = "--flat" in sys.argv
MOCK = "--mock" in sys.argv

SHUFFLE = "--shuffle" in sys.argv

# positional = the output path. Skip flags AND the value that follows a value-taking flag, or
# "--bg navy" would hand us "navy" as an output filename.
_VALUE_FLAGS = {"--bg", "--seed", "--drop-dull", "--size", "--scale"}
_pos, _skip = [], False
for _a in sys.argv[1:]:
    if _skip:
        _skip = False
        continue
    if _a in _VALUE_FLAGS:
        _skip = True
    elif not _a.startswith("--"):
        _pos.append(_a)

SEED = 7
for i, a in enumerate(sys.argv):
    if a == "--seed" and i + 1 < len(sys.argv):
        SEED = int(sys.argv[i + 1])

# Body armour reads as a row of near-identical shirt blobs, and the dullest of them are dark
# or desaturated enough to be dead space on the wall. --drop-dull N removes the N least
# colourful CHEST pieces only; weapons and headgear are never culled, however drab.
DROP_DULL = 0
for i, a in enumerate(sys.argv):
    if a == "--drop-dull" and i + 1 < len(sys.argv):
        DROP_DULL = int(sys.argv[i + 1])
CHEST_CATEGORIES = {"Armor", "Clothing", "Robe"}

# THE BANNER MUST MATCH THE NEXUS BOX, which is 1300 x 372 (aspect 3.49:1). Build anything
# taller and Nexus crops the top and bottom rows off. Everything below is derived from that
# box: the canvas is an exact multiple of it, and fit_grid picks the factorisation whose shape
# is closest to it, so the wall fills the frame instead of being cropped into.
TARGET_W, TARGET_H = 1300, 372
for i, a in enumerate(sys.argv):
    if a == "--size" and i + 1 < len(sys.argv):
        TARGET_W, TARGET_H = (int(v) for v in sys.argv[i + 1].lower().split("x"))

# Render at a whole multiple of the box: a clean 1/N downscale on Nexus's side beats an
# arbitrary resample of pixel art.
SCALE = 2
for i, a in enumerate(sys.argv):
    if a == "--scale" and i + 1 < len(sys.argv):
        SCALE = int(sys.argv[i + 1])

TARGET_ASPECT = TARGET_W / TARGET_H
W, H = TARGET_W * SCALE, TARGET_H * SCALE
COLS, ROWS = 26, 9      # replaced in main() once the surviving icon count is known


def fit_grid(n):
    """The exact cols x rows factorisation of n whose shape is closest to TARGET_ASPECT."""
    best = None
    for rows in range(1, n + 1):
        if n % rows:
            continue
        cols = n // rows
        err = abs(cols / rows - TARGET_ASPECT)
        if best is None or err < best[0]:
            best = (err, cols, rows)
    return best[1], best[2]
PREFIX = "ei_s_" if SMALL else "ei_"
GAP = 2 * SCALE if SMALL else 5 * SCALE
MARGIN = 6 * SCALE

# Ground palettes: base, centre-glow, vignette edge. All stay dark, for two reasons that are
# not negotiable: Nexus lays a white mod title over the lower left, and the painted ei_ art
# carries a near-black halo that starts reading as a grey box the moment the ground outruns it.
# The icons span the whole hue wheel, so a saturated ground fights half of them; these are all
# low-saturation so the wall stays the colour event.
PALETTES = {
    # Dracula At Night, read from the installed VS Code theme
    # (bceskavich.theme-dracula-at-night/theme/dracula-at-night.json): editor.background
    # #0E1419 as the base, the #253340 chrome slate as the centre glow.
    "dracula": ((14, 20, 25), (37, 51, 64), (8, 11, 14)),
    "navy":  ((18, 21, 32), (36, 48, 74), (8, 9, 15)),      # FFT menu blue-slate
    "plum":  ((26, 18, 30), (58, 35, 66), (12, 8, 14)),
    "teal":  ((13, 23, 25), (28, 53, 57), (6, 11, 12)),
    "ink":   ((16, 17, 21), (40, 42, 52), (8, 8, 10)),      # near-neutral blue-black
    "warm":  ((28, 24, 27), (72, 57, 50), (10, 9, 11)),     # the original warm charcoal
}
BG = "dracula"
for i, a in enumerate(sys.argv):
    if a == "--bg" and i + 1 < len(sys.argv):
        BG = sys.argv[i + 1]
if BG not in PALETTES:
    raise SystemExit(f"unknown --bg {BG!r}; choose from {', '.join(PALETTES)}")

# --metal replaces the flat ground with the shared brushed slate-steel plate (tools/lib/plate.py,
# shared with make_hero.py so both Nexus images sit on the same surface).
METAL = "--metal" in sys.argv

if FLAT:
    GROUND, GLOW, EDGE, GLOW_A = (9, 10, 14), (9, 10, 14), (9, 10, 14), 0
else:
    GROUND, GLOW, EDGE = PALETTES[BG]
    GLOW_A = 150 if SMALL else 120
    if not SMALL:
        # the painted art needs a tighter leash: darken base and glow so the halos stay hidden
        GROUND = tuple(round(v * 0.62) for v in GROUND)
        GLOW = tuple(round(v * 0.68) for v in GLOW)


def default_out():
    stem = "banner"
    if SMALL:
        stem += "_small"
    if SHUFFLE:
        stem += f"_shuffle{SEED}"
    elif HUE:
        stem += "_hue"
    if DROP_DULL:
        stem += f"_cull{DROP_DULL}"
    if FLAT:
        stem += "_flat"
    elif METAL:
        stem += "_metal"      # --metal builds its own plate and ignores the --bg palette
    else:
        stem += f"_{BG}"
    return WORK / f"{stem}.png"


OUT = Path(_pos[0]) if _pos else default_out()


def icon(item_id):
    """The recoloured icon, or None. items.json carries a few rows that ship no icon of their
    own, so a miss is expected; only a mass miss means the recolour cache is unbuilt."""
    p = CACHE / f"{PREFIX}{item_id:03d}_uitx.png"
    return Image.open(p).convert("RGBA") if p.exists() else None


def dom_hue(im):
    s = im.resize((16, 16))
    px = s.load()
    hs = []
    for y in range(16):
        for x in range(16):
            r, g, b, a = px[x, y]
            if a < 40:
                continue
            h, sat, v = colorsys.rgb_to_hsv(r / 255, g / 255, b / 255)
            if sat > 0.25 and v > 0.2:
                hs.append(h)
    return sum(hs) / len(hs) if hs else 1.0


def metal_ground():
    """The shared brushed slate-steel plate, at this banner's canvas size."""
    return steel_plate(W, H)


def ground():
    """Warm charcoal with a broad centre glow, grain and a vignette. Seeded: reproducible."""
    if METAL and not FLAT:
        return metal_ground()
    im = Image.new("RGB", (W, H), GROUND)
    if FLAT:
        return im.convert("RGBA")
    rnd = random.Random(23)

    glow = Image.new("L", (W, H), 0)
    ImageDraw.Draw(glow).ellipse([W * 0.10, -H * 0.55, W * 0.90, H * 1.55], fill=GLOW_A)
    glow = glow.filter(ImageFilter.GaussianBlur(230))
    im = Image.composite(Image.new("RGB", (W, H), GLOW), im, glow)

    d = ImageDraw.Draw(im, "RGBA")
    for _ in range(70):
        x, y = rnd.randrange(-160, W), rnd.randrange(-160, H)
        r = rnd.randrange(120, 520)
        c = (0, 0, 0) if rnd.random() < 0.55 else (92, 74, 62)
        d.ellipse([x - r, y - r, x + r, y + r], fill=c + (rnd.randrange(8, 22),))
    im = im.filter(ImageFilter.GaussianBlur(46))

    px = im.load()
    for y in range(H):
        for x in range(W):
            n = rnd.randrange(-5, 6)
            r, g, b = px[x, y]
            px[x, y] = (max(0, min(255, r + n)), max(0, min(255, g + n)), max(0, min(255, b + n)))

    vig = Image.new("L", (W, H), 0)
    ImageDraw.Draw(vig).ellipse([-W * 0.16, -H * 0.42, W * 1.16, H * 1.42], fill=255)
    vig = vig.filter(ImageFilter.GaussianBlur(150))
    return Image.composite(im, Image.new("RGB", (W, H), EDGE), vig).convert("RGBA")


def colourfulness(im):
    """Mean saturation carried at mean brightness, over the opaque pixels. Low means the icon
    is dark, washed out, or both: dead space on a wall whose whole job is showing colour."""
    px = im.load()
    s = v = n = 0.0
    for y in range(im.height):
        for x in range(im.width):
            r, g, b, a = px[x, y]
            if a < 60:
                continue
            _, ss, vv = colorsys.rgb_to_hsv(r / 255, g / 255, b / 255)
            s += ss
            v += vv
            n += 1
    return (s / n) * (v / n) if n else 0.0


def main():
    global COLS, ROWS, W, H
    WORK.mkdir(parents=True, exist_ok=True)

    entries = []
    for it in sorted(load_items()["items"], key=lambda t: t["id"]):
        im = icon(it["id"])
        if im is not None:
            entries.append((it, im))
    if len(entries) < 200:
        raise SystemExit(f"only {len(entries)} {PREFIX}* icons found - run tools/recolor_icons.py first")

    if DROP_DULL:
        chest = [e for e in entries if e[0]["category"] in CHEST_CATEGORIES]
        chest.sort(key=lambda e: colourfulness(e[1]))
        dropped = {id(e[1]) for e in chest[:DROP_DULL]}
        names = [e[0]["name"] for e in chest[:DROP_DULL]]
        entries = [e for e in entries if id(e[1]) not in dropped]
        print(f"dropped {len(names)} dull chest pieces: {', '.join(names)}")

    icons = [im for _, im in entries]
    COLS, ROWS = fit_grid(len(icons))
    # largest tile that fits the fixed canvas in BOTH axes, so nothing is ever cropped
    tile = min((W - 2 * MARGIN - (COLS - 1) * GAP) // COLS,
               (H - 2 * MARGIN - (ROWS - 1) * GAP) // ROWS)
    # tile is set by whichever axis binds first; spread the slack on the other axis into that
    # axis's gaps so the wall reaches both edges instead of floating in a wide margin
    gapx = max(GAP, (W - 2 * MARGIN - COLS * tile) // max(1, COLS - 1))
    gapy = max(GAP, (H - 2 * MARGIN - ROWS * tile) // max(1, ROWS - 1))
    grid_w = COLS * tile + (COLS - 1) * gapx
    grid_h = ROWS * tile + (ROWS - 1) * gapy
    ox, oy = (W - grid_w) // 2, (H - grid_h) // 2
    # NEAREST only on an exact integer upscale of the 48px source; otherwise it would double
    # some pixel rows and not others, which shows badly on this art
    native = 48 if SMALL else 100
    resample = Image.NEAREST if (SMALL and tile % native == 0) else Image.LANCZOS

    if SHUFFLE:
        # seeded, so a layout you like is reproducible; --seed N rerolls it
        random.Random(SEED).shuffle(icons)
    elif HUE:
        icons.sort(key=dom_hue)

    # The small icons are already high-saturation; only the painted art needs the lift to keep
    # its colour from sinking into a dark ground at wall scale.
    sat, bright = (1.04, 1.02) if SMALL else (1.18, 1.08)

    canvas = ground()
    for idx, ic in enumerate(icons[:COLS * ROWS]):
        if HUE and not SHUFFLE:
            # Fill COLUMN-major so the hue sweep runs left to right and every row carries the
            # whole spectrum. Row-major would stack reds at the top and greys at the bottom,
            # and Nexus crops the banner to a wide strip: a row-major sweep shows the visitor
            # one colour band and hides the rest.
            c, r = divmod(idx, ROWS)
        else:
            r, c = divmod(idx, COLS)
        ic = ImageEnhance.Color(ic).enhance(sat)
        ic = ImageEnhance.Brightness(ic).enhance(bright)
        ic = ic.resize((tile, tile), resample)
        x, y = ox + c * (tile + gapx), oy + r * (tile + gapy)
        if METAL and not FLAT:
            # the steel plate is lighter than the old near-black ground, so dark brown and
            # grey icons lose their edge against it. A soft cast shadow lifts them back off.
            sh = Image.new("RGBA", (tile, tile), (0, 0, 0, 0))
            sh.putalpha(ic.getchannel("A").point(lambda v: int(v * 0.55)))
            sh = sh.filter(ImageFilter.GaussianBlur(1.5 * SCALE))
            canvas.alpha_composite(sh, (x + SCALE, y + 2 * SCALE))
        canvas.alpha_composite(ic, (x, y))

    canvas.convert("RGB").save(OUT)
    print(f"saved {OUT}  ({W}x{H} = {SCALE}x the {TARGET_W}x{TARGET_H} Nexus box, "
          f"{COLS}x{ROWS} @ {tile}px, {len(icons)} {PREFIX}* icons)")
    if MOCK:
        write_mock(canvas.convert("RGB"))


def write_mock(banner):
    """Nexus crops the banner to a wide strip and lays its own dark bottom scrim plus the mod
    title over the lower left. Reproduce that so the title's legibility is checked here rather
    than discovered after upload. Do NOT bake a title into the banner itself: Nexus draws its
    own on top and you would end up with two."""
    CW, CH = 1440, 330
    im = banner.resize((CW, round(banner.height * CW / banner.width)), Image.LANCZOS)
    im = im.crop((0, (im.height - CH) // 2, CW, (im.height - CH) // 2 + CH))
    scrim = Image.new("L", (CW, CH), 0)
    d = ImageDraw.Draw(scrim)
    for y in range(CH):
        d.line([(0, y), (CW, y)], fill=int(215 * max(0.0, (y - CH * 0.30) / (CH * 0.70)) ** 1.5))
    im = Image.composite(Image.new("RGB", (CW, CH), (6, 6, 8)), im, scrim)
    ImageDraw.Draw(im).text(
        (28, CH - 92), "Living Weapons",
        font=ImageFont.truetype("C:/Windows/Fonts/segoeuib.ttf", 46), fill=(240, 240, 244))
    out = OUT.with_name(OUT.stem + "_mock.png")
    im.save(out)
    print(f"saved {out}  (Nexus header mock)")


if __name__ == "__main__":
    main()
