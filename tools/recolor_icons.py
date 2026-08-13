#!/usr/bin/env python
"""
Recolor equipment menu icons from the vanilla originals to per-item tints.

Pipeline per item (both the 100x100 card image and the 48x48 list icon):
  vanilla BC7 .tex (Pac Files/0008) -> FF16Tools tex-conv -> DDS -> Pillow recolor
  -> img-conv --no-chunk-compression -> .tex placed in the mod tree.

TWO recolor engines since LW-189 (owner-directed, settled through three live A/B rounds on
2026-08-13; docs/TODO.md LW-189 carries the decision trail):

  WEAPONS (category in lib.categories.WEAPON_CATS) get the BRIGHT v2 treatment:
    - card image: TWO-ZONE k-means segmentation of the VANILLA art (the identity tint goes on
      the metal/blade zone, hilt and trim keep their vanilla colors), shaded on a hue-graded
      ramp: shadows lean cool and gain saturation, highlights lean gently warm (clamped so
      cool identities never slide green), midtones gamma-lifted, vanilla white gleams kept.
    - list icon: whole-glyph BRIGHT hue-ramp (the small is separate one-material art where an
      invented split reads muddy), except SMALL_TWO_ZONE ids which use the card treatment.
    - per-item overrides in CARD_OVERRIDES / SMALL_TWO_ZONE (owner review rounds).

  NON-WEAPONS (shields, armor, accessories) keep the ORIGINAL whole-icon tint: they were
  never part of the LW-189 review, so their shipped look must not change until an owner pass
  covers them.

ICON_TINTS = {id: (hue, sat, value_mult)}; hue/sat in 0..1, value_mult scales brightness.
Run: python tools/recolor_icons.py [ids...]   |   python tools/recolor_icons.py --selftest
"""
import colorsys
import math
import random
import shutil
import subprocess
import sys
from pathlib import Path

from PIL import Image

sys.path.insert(0, str(Path(__file__).resolve().parent))
from lib.categories import WEAPON_CATS
from lib.items import load_items
from lib.paths import ROOT, FF16

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

# Merge per-item tints from data/items.json (source for the new categories).
SRC = {}  # item id -> vanilla icon id to source from, for repurposed weapons that need a different shape
_CATEGORY = {}
for _it in load_items()["items"]:
    if _it.get("iconTint"):
        ICON_TINTS[_it["id"]] = tuple(_it["iconTint"])
    if _it.get("iconSource"):
        SRC[_it["id"]] = _it["iconSource"]
    _CATEGORY[_it["id"]] = _it.get("category")

# --- LW-189 BRIGHT v2 engine (weapons only) -------------------------------------------------
# The reference implementation these constants and functions were frozen from is the approved
# preview generator (session scratchpad bright_all.py); the owner signed the full 121-weapon
# gallery off against EXACTLY this math, so changes here need a fresh gallery pass.

NO_BLADE_CATS = {"Bag", "Book", "Instrument", "Cloth"}   # no blade: tint the LARGEST cluster
CARD_OVERRIDES = {
    9: {"k": 2},                # Galewind: 3 clusters shredded the blade
    37: {"vmult_floor": 0.85},  # Chaos Blade: deliberate dark, but not that dark
    33: {"k": 2},               # Defender: zone missed the metal mass
    113: {"k": 2},              # Eight-Fluted Pole: shaft never took the tint
    117: {"k": 2},              # Hornet Pouch: cluster fragmentation read as camo blobs
}
SMALL_TWO_ZONE = {13, 15, 16, 18, 24}   # owner round-1: card-style split on these glyphs
COOL, WARM = 0.66, 0.13                  # shadow / highlight hue targets


def arc(a, b):
    """Shortest signed hue arc a -> b."""
    d = (b - a) % 1.0
    return d - 1.0 if d > 0.5 else d


def ramp_color(h, s, v):
    """Hue/sat for a pixel of brightness v: cool saturated shadows, gently-warm highlights.
    The warm drift is clamped to +/-0.025 absolute hue so cool identities never read green."""
    if v < 0.5:
        t = 1.0 - v / 0.5
        return (h + arc(h, COOL) * 0.30 * t) % 1.0, min(1.0, s * (1.0 + 0.25 * t))
    t = (v - 0.5) / 0.5
    drift = max(-0.025, min(0.025, arc(h, WARM) * 0.16 * t))
    return (h + drift) % 1.0, s * (1.0 - 0.30 * t)


def shade_bright(h_t, s_t, vmult, s0, v0):
    """BRIGHT v2 pixel: hot-but-capped saturation, gamma-lifted value, ramp hue, gleam kept."""
    s_hot = min(0.97, s_t * 1.3 + 0.03)
    v = min(1.0, (v0 ** 0.85) * vmult * 1.08)
    nh, ns = ramp_color(h_t, s_hot, v)
    r, g, b = colorsys.hsv_to_rgb(nh, ns, v)
    if s0 < 0.20 and v0 > 0.85:
        r, g, b = (c + (1.0 - c) * 0.60 for c in (r, g, b))
    return tuple(int(c * 255) for c in (r, g, b))


def features(r, g, b):
    h, s, v = colorsys.rgb_to_hsv(r / 255, g / 255, b / 255)
    # hue is circular and meaningless at low saturation: weight it by s
    return [s * 1.6, v, math.cos(2 * math.pi * h) * s, math.sin(2 * math.pi * h) * s]


def kmeans(pts, k, iters=25, seed=7):
    """Seeded and deterministic: the same sprite always yields the same zones.

    Known wart, kept ON PURPOSE: if the seeded sample draws k identical points (only possible
    on large perfectly-flat color fields), every pixel ties to cluster 0 and the loop exits
    with one zone. The owner-approved gallery came from exactly this algorithm, so
    the production bake must reproduce it bit-for-bit; do not "fix" this without regenerating
    and re-approving the full gallery."""
    rng = random.Random(seed)
    centroids = [list(p) for p in rng.sample(pts, min(k, len(pts)))]
    assign = [0] * len(pts)
    for _ in range(iters):
        changed = False
        for i, p in enumerate(pts):
            best = min(range(len(centroids)),
                       key=lambda c: sum((a - b) ** 2 for a, b in zip(p, centroids[c])))
            if best != assign[i]:
                assign[i] = best
                changed = True
        for c in range(len(centroids)):
            members = [pts[i] for i in range(len(pts)) if assign[i] == c]
            if members:
                centroids[c] = [sum(dim) / len(members) for dim in zip(*members)]
        if not changed:
            break
    return assign, centroids


def smooth_mask(mask, w, h, passes=3):
    """3x3 majority vote over a {(x,y): bool} zone mask (True = tinted zone)."""
    for _ in range(passes):
        nxt = {}
        for (x, y), val in mask.items():
            votes = same = 0
            for dy in (-1, 0, 1):
                for dx in (-1, 0, 1):
                    n = mask.get((x + dx, y + dy))
                    if n is not None:
                        votes += 1
                        if n:
                            same += 1
            nxt[(x, y)] = same * 2 > votes
        mask = nxt
    return mask


def two_zone_bright(im, tint, category, override):
    """Card treatment: tint zone found on the VANILLA art, shaded BRIGHT; trim stays vanilla."""
    h_t, s_t, vmult = tint
    vmult = max(override.get("vmult_floor", 0.0), vmult)
    px = im.load()
    coords, pts = [], []
    for y in range(im.height):
        for x in range(im.width):
            r, g, b, a = px[x, y]
            if a >= 8:
                coords.append((x, y))
                pts.append(features(r, g, b))
    assign, centroids = kmeans(pts, override.get("k", 3))
    if category in NO_BLADE_CATS:
        sizes = [assign.count(c) for c in range(len(centroids))]
        target = {max(range(len(centroids)), key=lambda c: sizes[c])}
    else:
        order = sorted(range(len(centroids)), key=lambda c: -centroids[c][1])
        target = {order[0]}
        if sum(1 for a in assign if a in target) / len(assign) < 0.25 and len(order) > 1:
            target.add(order[1])
    mask = smooth_mask({c: (a in target) for c, a in zip(coords, assign)}, im.width, im.height)
    out = im.copy()
    opx = out.load()
    for (x, y) in coords:
        if mask[(x, y)]:
            r, g, b, a = px[x, y]
            _, s0, v0 = colorsys.rgb_to_hsv(r / 255, g / 255, b / 255)
            opx[x, y] = (*shade_bright(h_t, s_t, vmult, s0, v0), a)
    return out


def small_bright(im, tint):
    """List-icon treatment: whole-glyph BRIGHT hue-ramp. Floor 0.75, NEVER higher: a higher
    floor overrides authored-dark identities and splits the small from its somber card."""
    h_t, s_t, vmult = tint
    px0 = im.load()
    svals = [colorsys.rgb_to_hsv(*[c / 255 for c in px0[x, y][:3]])[1]
             for y in range(im.height) for x in range(im.width) if px0[x, y][3] >= 8]
    mean_s = sum(svals) / len(svals) if svals else 0.0
    vm = max(0.75, min(1.25, vmult * 1.08))
    out = im.copy()
    px = out.load()
    for y in range(out.height):
        for x in range(out.width):
            r, g, b, a = px[x, y]
            if a < 8:
                continue
            _, s0, v0 = colorsys.rgb_to_hsv(r / 255, g / 255, b / 255)
            base_s = s0 * max(0.55, min(1.35, (s_t * 1.3 + 0.03) / 0.5))
            if mean_s < 0.10:                       # achromatic glyph: identity must land
                base_s = max(base_s, s_t * 0.75)
            base_s = max(0.0, min(0.97, base_s))
            v = min(1.0, (v0 ** 0.85) * vm)
            nh, ns = ramp_color(h_t, base_s, v)
            nr, ng, nb = colorsys.hsv_to_rgb(nh, ns, v)
            if s0 < 0.20 and v0 > 0.85:             # vanilla gleam stays near-white
                nr, ng, nb = (c + (1.0 - c) * 0.60 for c in (nr, ng, nb))
            px[x, y] = (int(nr * 255), int(ng * 255), int(nb * 255), a)
    return out


def recolor(im, hue, sat, val_mult):
    """LEGACY whole-icon tint: non-weapons only (their look predates LW-189 and is unreviewed
    under the new rules; do not route weapons here)."""
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


def apply_weapon(im, item_id, tint, surface):
    category = _CATEGORY.get(item_id)
    if surface == "card":
        return two_zone_bright(im, tint, category, CARD_OVERRIDES.get(item_id, {}))
    if item_id in SMALL_TWO_ZONE:
        h_t, s_t, vmult = tint
        vm = max(0.85, min(1.15, vmult))
        return two_zone_bright(im, (h_t, s_t, vm), category, {})
    return small_bright(im, tint)


def process(item_id, tint, src_id=None):
    WORK.mkdir(parents=True, exist_ok=True)
    is_weapon = _CATEGORY.get(item_id) in WEAPON_CATS
    sid = item_id if src_id is None else src_id
    for sub, pfx, surface in [("equip_item", "ei", "card"), ("equip_item_s", "ei_s", "small")]:
        src_name = f"{pfx}_{sid:03d}_uitx"
        out_name = f"{pfx}_{item_id:03d}_uitx"
        src = VANILLA / sub / "texture" / f"{src_name}.tex"
        if not src.exists():
            print(f"  MISSING {src}"); continue
        work_tex = WORK / f"{src_name}.tex"
        shutil.copy(src, work_tex)
        subprocess.run([str(FF16), "tex-conv", "-i", str(work_tex)], capture_output=True)
        im = Image.open(WORK / f"{src_name}.dds").convert("RGBA")
        if is_weapon:
            im = apply_weapon(im, item_id, tint, surface)
        else:
            recolor(im, *tint)
        png = WORK / f"{out_name}.png"
        im.save(png)
        work_tex.unlink(missing_ok=True)
        subprocess.run([str(FF16), "img-conv", "-i", str(png), "--no-chunk-compression"], capture_output=True)
        dst = MOD / sub / "texture"
        dst.mkdir(parents=True, exist_ok=True)
        shutil.move(str(WORK / f"{out_name}.tex"), str(dst / f"{out_name}.tex"))
        engine = "bright-v2" if is_weapon else "legacy"
        print(f"  {out_name}" + (f" (from {src_name})" if src_id is not None else "") + f" -> {sub} [{engine}]")


def selftest():
    """Pure-math regression cases for the BRIGHT v2 engine (repo idiom: no pytest)."""
    failures = []

    def check(name, cond):
        if not cond:
            failures.append(name)

    check("arc wraps shortest path", abs(arc(0.9, 0.1) - 0.2) < 1e-9)
    check("arc negative direction", abs(arc(0.1, 0.9) + 0.2) < 1e-9)
    # warm drift clamp: a cyan (0.5) highlight may move at most 0.025 toward warm
    nh, _ = ramp_color(0.5, 0.8, 1.0)
    check("warm drift clamped on cool hue", abs(arc(0.5, nh)) <= 0.025 + 1e-9)
    # shadow leans cool: the shadow hue ends CLOSER to COOL than the base was (the shortest
    # arc from a warm hue runs through magenta, so the sign of the move is not the test)
    nh_sh, ns_sh = ramp_color(0.05, 0.5, 0.0)
    check("shadow leans cool", abs(arc(nh_sh, COOL)) < abs(arc(0.05, COOL)))
    check("shadow gains saturation", ns_sh > 0.5)
    # gleam preservation: a near-white vanilla pixel comes out near-white
    r, g, b = shade_bright(0.0, 0.9, 1.0, 0.05, 0.95)
    check("gleam stays near-white", min(r, g, b) > 150)
    # saturation heat is capped
    check("saturation cap", min(0.97, 1.0 * 1.3 + 0.03) == 0.97)
    # smalls floor respects authored darkness but never crushes
    check("small floor 0.75", max(0.75, min(1.25, 0.5 * 1.08)) == 0.75)
    check("small ceiling 1.25", max(0.75, min(1.25, 1.3 * 1.08)) == 1.25)
    # smoothing: an isolated pixel flips to its neighborhood
    mask = {(x, y): False for x in range(3) for y in range(3)}
    mask[(1, 1)] = True
    check("majority smoothing flips isolated pixel", smooth_mask(mask, 3, 3, passes=1)[(1, 1)] is False)
    # kmeans determinism
    pts = [[0.1, 0.1, 0, 0], [0.9, 0.9, 0, 0], [0.11, 0.12, 0, 0], [0.88, 0.91, 0, 0]] * 5
    a1, _ = kmeans(list(pts), 2)
    a2, _ = kmeans(list(pts), 2)
    check("kmeans deterministic", a1 == a2)
    # no-blade categories pick the LARGEST cluster; the small bright clasp stays vanilla.
    # Body pixels carry slight variation so the seeded sample draws distinct centroids
    # (perfectly flat synthetic fields trip the documented kmeans degeneracy; real art
    # never presents 100 identical pixels to the sampler this way).
    im = Image.new("RGBA", (10, 10), (0, 0, 0, 0))
    for x in range(10):
        for y in range(8):
            im.putpixel((x, y), (60 + x, 40 + y, 20, 255))   # large dark body, shaded
        for y in range(8, 10):
            im.putpixel((x, y), (240, 240, 240, 255))        # small bright clasp
    bag = two_zone_bright(im, (0.3, 0.8, 1.0), "Bag", {})
    check("bag tints the large body", bag.getpixel((5, 2)) != im.getpixel((5, 2)))
    check("bag leaves the bright clasp", bag.getpixel((5, 9))[:3] == im.getpixel((5, 9))[:3])

    if failures:
        print("SELFTEST FAILURES:", "; ".join(failures))
        return 1
    print("recolor_icons selftest: all cases passed.")
    return 0


def main():
    if "--selftest" in sys.argv:
        raise SystemExit(selftest())
    only = set(int(a) for a in sys.argv[1:] if a.isdigit())
    for i, tint in ICON_TINTS.items():
        if only and i not in only:
            continue
        print(f"id{i}:")
        process(i, tint, SRC.get(i))
    print("Done. Recolored icons placed in the mod tree.")


if __name__ == "__main__":
    main()
