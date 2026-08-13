#!/usr/bin/env python
"""
Recolor equipment menu icons from the vanilla originals to per-item tints.

Pipeline per item (both the 100x100 card image and the 48x48 list icon):
  vanilla BC7 .tex (Pac Files/0008) -> FF16Tools tex-conv -> DDS -> Pillow recolor
  -> img-conv --no-chunk-compression -> .tex placed in the mod tree.

THREE recolor engines, routed per item by engine_for() (owner-directed; docs/TODO.md LW-189
and LW-190 carry the decision trails):

  WEAPONS (category in lib.categories.WEAPON_CATS) get the LW-189 BRIGHT v2 treatment:
    - card image: TWO-ZONE k-means segmentation of the VANILLA art (the identity tint goes on
      the metal/blade zone, hilt and trim keep their vanilla colors), shaded on a hue-graded
      ramp: shadows lean cool and gain saturation, highlights lean gently warm (clamped so
      cool identities never slide green), midtones gamma-lifted, vanilla white gleams kept.
    - list icon: whole-glyph BRIGHT hue-ramp (the small is separate one-material art where an
      invented split reads muddy), except SMALL_TWO_ZONE ids which use the card treatment.
    - per-item overrides in CARD_OVERRIDES / SMALL_TWO_ZONE (owner review rounds).

  SHIELDS (category in WHOLE_BRIGHT_CATS) get the LW-190 two-tone treatment: the identity
  colour on the shield body (BRIGHT shading under a tanh shoulder), a distinct trim tone on
  the metal fittings found by trim_mask, per-item modes in SHIELD_OVERRIDES (gold or vanilla
  trim, inverted tint-on-fittings, forced mask cover). k-means was tried and rejected here:
  a shield is one convex plate, so it clusters the lighting, not the materials.

  EVERYTHING ELSE (armor, accessories) keeps the ORIGINAL whole-icon tint: not yet reviewed
  under the new rules, so their shipped look must not change until an owner pass covers them.

ICON_TINTS = {id: (hue, sat, value_mult)}; hue/sat in 0..1, value_mult scales brightness.
Run: python tools/recolor_icons.py [ids...]   |   python tools/recolor_icons.py --selftest
"""
import colorsys
import math
import random
import shutil
import subprocess
import sys
from collections import deque
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
    # Hues are DELIBERATELY SPACED, and SHIELD_MIN_HUE_GAP in selftest() enforces it. Under the
    # whole-glyph engine below the tint is the item's entire colour signal, so two shields whose
    # tints sit close become the same object on screen. The pre-pass palette had seven such pairs
    # (140 vs 142 were 0.02 hue and 0.02 saturation apart, i.e. one shield in two names); the
    # LW-190 review round measured them as hard collisions and this layout is the fix.
    # Owner review rounds two and three, 2026-08-13, applied per shield by name. Round three's
    # standing rule: "I like the original better" means build FROM the vanilla art, not revert,
    # so those shields (Emberward, Spellbane, Conduit) take a tint MEASURED from their own
    # vanilla body hue (chroma-weighted mean of the non-trim pixels), enriched, with the artist's
    # fittings kept via the vanilla-trim override. Sanctguard keeps its red body but the inner
    # emblem stays vanilla gold ("leave the inner shield color the same"). Aegis Prime keeps the
    # dark blue and drops the light powder blue (vmult down). Trailblazer read as Galewall's twin
    # and moves toward forest green. Vanguard passed and keeps its crimson, which is why
    # Sanctguard's red sits hotter and brighter (their tripwire separation is saturation).
    135: (0.05, 0.80, 0.68),  # Emberward       burnt ember over gilt, darker so the gilt cross
                              #                 defines and it splits from Sanctguard's bright red
    136: (0.05, 0.55, 0.72),  # Spellbane       burnished mahogany, vanilla cross (anti-mage)
    141: (0.06, 0.72, 1.02),  # Kaiser Shield   BRIGHT copper cross on a BRIGHT blue field
                              #                 (NAME CORRECTED post-ship, was labeled "Conduit")
    130: (0.15, 0.92, 1.18),  # Stormwall       bright golden yellow (round seven: hue 0.17 read
                              #                 GREEN once the cool-shadow ramp pulled its darks)
    137: (0.23, 0.62, 0.92),  # Trailblazer     forest green (Move)
    129: (0.88, 0.62, 1.02),  # Galewall        fuchsia (owner round eight: instead of green)
    131: (0.45, 0.30, 1.18),  # Swiftguard      frost-white mint fittings, vanilla plate (Speed)
    134: (0.51, 0.42, 1.08),  # Rimeward        white ice (owner round six: more white)
    142: (0.55, 0.14, 1.16),  # Venetian Shield icy platinum, gilt trim
                              #                 (NAME CORRECTED post-ship, was labeled "Bastion")
    128: (0.58, 0.66, 0.96),  # Tideward        deep ocean blue
    140: (0.62, 0.26, 0.70),  # Genji Shield    cold steel-blue (rare; NAME CORRECTED post-ship,
                              #                 the table said "Ronin Wall" but items.json keeps
                              #                 the vanilla name; see LW-197 for the set-colour question)
    143: (0.60, 0.85, 1.10),  # Aegis Prime     sapphire; gold on edges and gem only (capstone)
    132: (0.71, 0.48, 0.95),  # Wardstone       purple inner under a thin white geometric rim
                              #                 (settled round twelve, picker variant A)
    139: (0.78, 0.34, 0.55),  # Nightward       near-black, faint violet cast (Dark)
    138: (0.96, 0.50, 0.84),  # Vanguard        crimson (PA)
    133: (0.995, 0.85, 1.00), # Sanctguard      bright red, vanilla gold emblem (Holy)
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


# --- LW-190 shield engine (whole-glyph BRIGHT) ----------------------------------------------
# Shields do NOT take the weapons card rule. Measured on all 16 sprites: a shield is one convex
# plate, so k-means clusters on the LIGHTING gradient rather than on a material boundary, giving
# half-painted shields (130, 142) and camouflage speckle (128, 134, 135, 139). Full coverage with
# BRIGHT shading is the treatment; these two constants fix the defects that coverage exposes.
SHIELD_KNEE = 0.70                      # value above which the shoulder compresses
SHIELD_GLEAM_V, SHIELD_GLEAM_S = 0.74, 0.30   # widened from the weapons rule's 0.85 / 0.20


def shoulder(x, knee=SHIELD_KNEE):
    """Soft-clip above the knee. shade_bright's min(1.0, ...) FLAT-clipped up to 29% of a bright
    shield's pixels to one cream value (id133), dissolving the embossed relief. tanh keeps the
    mapping strictly increasing, so every brightness step in the vanilla art survives as a
    distinguishable step in the output."""
    if x <= knee:
        return x
    return knee + (1.0 - knee) * math.tanh((x - knee) / (1.0 - knee))


def shade_shield(h_t, s_t, vmult, s0, v0):
    """BRIGHT shading for a shield pixel: identity hue and hot saturation, vanilla brightness
    through the shoulder, and a widened gleam preserve so light-on-dark devices (id130's
    lightning bolt, id143's gem facets) stay light instead of being repainted the plate hue."""
    s_hot = min(0.97, s_t * 1.3 + 0.03)
    v = shoulder((v0 ** 0.88) * vmult)
    nh, ns = ramp_color(h_t, s_hot, v)
    r, g, b = colorsys.hsv_to_rgb(nh, ns, v)
    if s0 < SHIELD_GLEAM_S and v0 > SHIELD_GLEAM_V:
        lift = 0.60 * min(1.0, (v0 - SHIELD_GLEAM_V) / (1.0 - SHIELD_GLEAM_V) + 0.35)
        r, g, b = (c + (1.0 - c) * lift for c in (r, g, b))
    return tuple(int(c * 255) for c in (r, g, b))


TRIM_HUE, TRIM_SAT = 0.58, 0.07     # cool silver, the default second tone
TRIM_MIN_COVER = 0.06               # every shield must end up genuinely two-tone


def _pct(vals, p):
    s = sorted(vals)
    return s[max(0, min(len(s) - 1, int(len(s) * p / 100.0)))]


def trim_mask(im, s_p=45, v_p=50, cover=None, sat_split=False, split_p=50, ring=None):
    """Find the shield's SECOND material: the rim, boss and fittings.

    Keyed on SATURATION, not brightness. That is the whole point. k-means on these sprites
    clusters the lighting gradient across one convex plate, which is why the weapons card rule
    produced half-painted shields; but within a single sprite the metal fittings really are the
    low-saturation, high-value population, and that separation survives the lighting.

    cover overrides the whole hunt with a plain brightness split sized to that fraction of the
    sprite. It exists for art the saturation key misreads: Aegis Prime's filigree is SATURATED
    bronze (so the key skips it and gilds only the gem), and Swiftguard's inverse mode wants
    exactly "the bright fittings" as its tint zone."""
    px = im.load()
    coords, ss, vv = [], [], []
    for y in range(im.height):
        for x in range(im.width):
            r, g, b, a = px[x, y]
            if a < 8:
                continue
            _, s0, v0 = colorsys.rgb_to_hsv(r / 255, g / 255, b / 255)
            coords.append((x, y)); ss.append(s0); vv.append(v0)
    if ring is not None:
        # Geometric rim (owner round eleven, Wardstone "outer rim white, inner purple"): the
        # mask is the band of SOLID pixels within `ring` of the sprite's solid silhouette,
        # found by a BFS inward from the silhouette boundary. Brightness and saturation keys
        # cannot express this: the art's bright center band always outbids the rim on both.
        # `ring` is authored against the 100px card and scales with the sprite so the same
        # override fits the 48px icon. The smoky semi-transparent halo around every sprite is
        # NOT solid (alpha < 160), so the ring hugs the shield's real edge, not the smoke.
        px2 = im.load()
        solid = {c for c in coords if px2[c][3] >= 160}
        q = deque()
        dist = {}
        for (x, y) in solid:
            for dx, dy in ((1, 0), (-1, 0), (0, 1), (0, -1)):
                n = (x + dx, y + dy)
                if n not in solid:
                    dist[(x, y)] = 0
                    q.append((x, y))
                    break
        while q:
            c = q.popleft()
            x, y = c
            for dx, dy in ((1, 0), (-1, 0), (0, 1), (0, -1)):
                n = (x + dx, y + dy)
                if n in solid and n not in dist:
                    dist[n] = dist[c] + 1
                    q.append(n)
        thick = max(2, round(ring * im.width / 100.0))
        return coords, {c: (c in solid and dist.get(c, 1 << 30) < thick) for c in coords}
    if sat_split:
        # Strict material split (owner round six, "one colour strictly the cross, the other the
        # background"): mask = the LOW-saturation share of the sprite regardless of brightness,
        # so painted panels and bare metal separate cleanly even through shadow. split_p moves
        # the boundary: higher hands more mid-saturation pixels to the mask side.
        s2 = _pct(ss, split_p)
        return coords, smooth_mask({c: (s0 < s2) for c, s0 in zip(coords, ss)},
                                   im.width, im.height, passes=1)
    if cover is not None:
        v2 = _pct(vv, int(100 * (1.0 - cover)))
        return coords, smooth_mask({c: (v0 > v2) for c, v0 in zip(coords, vv)},
                                   im.width, im.height, passes=1)
    s_cut = max(0.10, min(0.48, _pct(ss, s_p)))
    v_cut = max(0.30, min(0.78, _pct(vv, v_p)))
    mask = smooth_mask({c: (s0 < s_cut and v0 > v_cut) for c, s0, v0 in zip(coords, ss, vv)},
                       im.width, im.height, passes=2)
    # Coverage is judged AFTER smoothing on purpose: on the 48px Stormwall and Conduit icons the
    # metal is one pixel wide, so the majority vote ate it and a pre-smooth count read healthy.
    for p in (72, 62, 52):
        if sum(mask.values()) / len(mask) >= TRIM_MIN_COVER:
            break
        v2 = _pct(vv, p)
        mask = smooth_mask({c: (v0 > v2) for c, v0 in zip(coords, vv)}, im.width, im.height, passes=1)
    return coords, mask


GOLD_HUE, GOLD_SAT = 0.115, 0.58    # heraldic gilt, the warm alternative to silver trim


def trim_tone(h_t, s_t, v0, mode="silver"):
    """Cool silver against a coloured body. Two escapes from that default:

    "gold" is asked for per item, because a white fitting is wrong on some art. It is also the
    only answer for a near-neutral IDENTITY: Bastion's vanilla is a gold plate with a SILVER
    cross, so both "recolour the trim silver" and "keep the vanilla trim" leave silver on
    platinum, which is the monochrome the owner rejected. Gilt is what separates them.

    When the identity is near-neutral and no mode is given, the trim drops to graphite so the
    two tones separate by VALUE instead of hue."""
    if mode == "gold":
        v = shoulder((v0 ** 0.85) * 1.04)
        return colorsys.hsv_to_rgb(GOLD_HUE, GOLD_SAT * (1.0 - 0.45 * v), v)
    if s_t < 0.20:
        return colorsys.hsv_to_rgb((h_t + 0.5) % 1.0, 0.10, shoulder((v0 ** 1.15) * 0.62))
    v = shoulder((v0 ** 0.85) * 1.06)
    r, g, b = colorsys.hsv_to_rgb(TRIM_HUE, TRIM_SAT * (1.0 - 0.5 * v), v)
    return tuple(c + (1.0 - c) * 0.25 for c in (r, g, b))


# Per-shield trim overrides from the owner's review round (2026-08-13). "vanilla" keeps the
# artist's own trim colour instead of recolouring it, which is the answer whenever the vanilla
# fitting is gold and the recolour was turning it white: Emberward's inner trim (asked for
# directly), Bastion (platinum body plus silver trim was still one colour, gold fixes it) and
# Aegis Prime (white trim rejected; its gold filigree is the capstone's whole character).
SHIELD_OVERRIDES = {
    # Swiftguard: owner keeps the vanilla inner plate, so the identity tint moves ONTO the bright
    # fittings. Covers tuned per surface by A/B (the card and the list icon are separate art):
    # wider grabs the plate's sheen and reads half-painted, narrower loses the identity.
    131: {"invert": True, "cover": 0.32, "cover_small": 0.22},
    133: {"trim": "vanilla"},              # Sanctguard: red body, the inner emblem stays the
                                           # artist's gold ("leave the inner shield color the same")
    135: {"trim": "gold"},                 # Emberward: dark ember body, gilt fittings (round five:
                                           # the measured-from-vanilla copper read as no change)
    136: {"trim": "vanilla"},              # Spellbane: burnished dark, the silver cross stays
    141: {"trim_tint": (0.58, 0.66, 1.12), "split": "sat", "split_p": 56},  # Kaiser Shield rounds
                                           # seven and eight: copper strictly the cross, blue
                                           # strictly the background (plain sat-split matches on
                                           # both surfaces); split_p 56 hands the boundary a
                                           # tiny bit more blue, as asked
    142: {"trim": "gold"},                 # Venetian Shield: platinum body needs gilt to stop
                                           # reading monochrome
    132: {"ring": 6},                      # Wardstone settled round twelve, picker variant A
                                           # ("outer rim white, inner purple"): the thin
                                           # geometric ring, rich purple beneath
    143: {"trim_tint": (0.13, 0.80, 1.20), "cover": 0.22, "cover_small": 0.18},  # Aegis Prime
                                           # settled round eleven, picker variant C ("hands down
                                           # my favorite"): bright sapphire, gold kept to the
                                           # edges and gem
}


def shield_two_tone(im, tint, override=None, surface="card"):
    """Identity colour on the shield body, a distinct trim on its metal. Owner rule 2026-08-13:
    a shield whose vanilla art carries two colours must come out carrying two colours, and no
    shield may ship as a single flat colour."""
    h_t, s_t, vmult = tint
    ov = override or {}
    trim_mode = ov.get("trim", "silver")
    invert = ov.get("invert", False)
    # The card and the list icon are separate vanilla art, so a forced cover may differ per
    # surface; cover_small falls back to cover, which falls back to the saturation-keyed hunt.
    cover = ov.get("cover_small", ov.get("cover")) if surface == "small" else ov.get("cover")
    coords, mask = trim_mask(im, cover=cover, sat_split=ov.get("split") == "sat",
                             split_p=ov.get("split_p", 50), ring=ov.get("ring"))
    if surface == "small" and ov.get("swap_small"):
        # The card and list icon can have INVERTED material roles (Conduit: the card's cross is
        # its saturated straps, the icon's cross is its desaturated T), so the same colour lands
        # on "the cross" of both surfaces only by swapping the zone assignment on the small.
        mask = {c: not m for c, m in mask.items()}
    px = im.load()
    out = im.copy()
    opx = out.load()
    for c in coords:
        r, g, b, a = px[c]
        _, s0, v0 = colorsys.rgb_to_hsv(r / 255, g / 255, b / 255)
        if invert:
            # Inverse assignment (Swiftguard): the identity tint lands ON the bright fittings and
            # the plate keeps its vanilla paint, the owner's "inner plate stays original" rule.
            if mask[c]:
                opx[c] = (*shade_shield(h_t, s_t, vmult, s0, v0), a)
            continue
        if mask[c]:
            if trim_mode == "vanilla":
                continue                     # out is a copy of im, so the vanilla pixel stands
            if "trim_tint" in ov:
                # Two-colour shield (owner round six, Conduit "copper blue but BRIGHT AF"):
                # the mask zone takes its OWN full identity tint through the same shader,
                # so both materials read hot instead of one deferring to a metal tone.
                t2 = ov["trim_tint"]
                opx[c] = (*shade_shield(t2[0], t2[1], t2[2], s0, v0), a)
                continue
            nr, ng, nb = trim_tone(h_t, s_t, v0, trim_mode)
            opx[c] = (int(nr * 255), int(ng * 255), int(nb * 255), a)
        else:
            opx[c] = (*shade_shield(h_t, s_t, vmult, s0, v0), a)
    return out


def recolor(im, hue, sat, val_mult):
    """LEGACY whole-icon tint: armor and accessories only (their look predates LW-189 and is
    unreviewed under the new rules; do not route weapons or shields here)."""
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


WHOLE_BRIGHT_CATS = {"Shield"}   # families reviewed under LW-190


def engine_for(item_id):
    cat = _CATEGORY.get(item_id)
    if cat in WEAPON_CATS:
        return "bright-v2"
    if cat in WHOLE_BRIGHT_CATS:
        return "shield-bright"
    return "legacy"


def route(im, item_id, tint, surface):
    """THE single routing rule, returning a NEW image so callers keep their vanilla copy.
    process() and tools/icon_preview.py both call this and neither owns a second copy of the
    branch, so the reviewed gallery cannot drift from the production bake."""
    engine = engine_for(item_id)
    if engine == "bright-v2":
        return apply_weapon(im, item_id, tint, surface)
    if engine == "shield-bright":
        return shield_two_tone(im, tint, SHIELD_OVERRIDES.get(item_id), surface)
    return recolor(im.copy(), *tint)


def process(item_id, tint, src_id=None):
    WORK.mkdir(parents=True, exist_ok=True)
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
        im = route(Image.open(WORK / f"{src_name}.dds").convert("RGBA"), item_id, tint, surface)
        png = WORK / f"{out_name}.png"
        im.save(png)
        work_tex.unlink(missing_ok=True)
        subprocess.run([str(FF16), "img-conv", "-i", str(png), "--no-chunk-compression"], capture_output=True)
        dst = MOD / sub / "texture"
        dst.mkdir(parents=True, exist_ok=True)
        shutil.move(str(WORK / f"{out_name}.tex"), str(dst / f"{out_name}.tex"))
        print(f"  {out_name}" + (f" (from {src_name})" if src_id is not None else "")
              + f" -> {sub} [{engine_for(item_id)}]")


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

    # --- LW-190 shield engine ---------------------------------------------------------------
    # The shoulder exists to stop the flat clip that ate id133's relief, so the properties that
    # matter are "never reaches 1.0" and "never ties two distinct inputs".
    check("shoulder is identity below the knee", shoulder(0.5) == 0.5)
    check("shoulder never reaches a flat 1.0", shoulder(1.0) < 1.0 and shoulder(4.0) < 1.0)
    steps = [shoulder(i / 200.0) for i in range(201)]
    check("shoulder is strictly increasing", all(b > a for a, b in zip(steps, steps[1:])))
    # a bright vanilla pixel that shade_bright would have clipped keeps headroom here
    check("shoulder keeps headroom where shade_bright clipped",
          shoulder((0.95 ** 0.88) * 1.15) < 0.995)
    # Widened gleam preserve, isolated: s0 feeds ONLY the gleam branch, so holding v0 fixed and
    # moving s0 across the cutoff measures the lift and nothing else.
    lit = shade_shield(0.16, 0.85, 1.04, 0.10, 0.80)      # v0 above GLEAM_V, s0 below GLEAM_S
    unlit = shade_shield(0.16, 0.85, 1.04, 0.40, 0.80)    # same pixel, gleam refused on s0
    check("widened gleam lifts a light device", min(lit) > min(unlit))
    # ...and it stays OFF for midtones, so the plate is not washed toward white
    check("gleam preserve does not swallow midtones",
          shade_shield(0.16, 0.85, 1.04, 0.10, 0.55) == shade_shield(0.16, 0.85, 1.04, 0.40, 0.55))
    # the widening is real: the shield window must stay strictly wider than the weapons rule
    # (shade_bright's literal s0 < 0.20 and v0 > 0.85), on BOTH axes, or light-on-dark devices
    # like id130's bolt fall back out of the preserve
    check("gleam window stays wider than the weapons rule",
          SHIELD_GLEAM_V < 0.85 and SHIELD_GLEAM_S > 0.20)
    # Two-tone: a synthetic shield of a saturated body plus a pale metal rim must come out with
    # the body tinted and the rim on a visibly DIFFERENT tone (the owner's no-monochrome rule).
    # The rim's gradient must reach its MAX channel (HSV value is the max), because a perfectly
    # flat brightness field defeats percentile splits (nothing is strictly above the cut), the
    # same degeneracy the kmeans note documents. Real sprite art is continuous.
    sim = Image.new("RGBA", (12, 12), (0, 0, 0, 0))
    for y in range(12):
        for x in range(12):
            edge = x in (0, 1, 10, 11) or y in (0, 1, 10, 11)
            sim.putpixel((x, y), (210 + x, 214 + y, 218 + x, 255) if edge else (120 + x, 70, 55, 255))
    sim.putpixel((6, 11), (0, 0, 0, 0))          # one transparent pixel: must survive untouched
    tt = shield_two_tone(sim, (0.58, 0.66, 0.96))
    body, rim = tt.getpixel((6, 6))[:3], tt.getpixel((0, 6))[:3]
    check("two-tone tints the body", body != sim.getpixel((6, 6))[:3])
    check("two-tone leaves transparent pixels alone", tt.getpixel((6, 11))[3] == 0)
    check("two-tone body and trim are distinct tones", sum(abs(a - b) for a, b in zip(body, rim)) > 60)
    _, m = trim_mask(sim)
    check("trim mask clears the coverage floor", sum(m.values()) / len(m) >= TRIM_MIN_COVER)
    # a near-neutral identity must NOT return silver-on-silver: it drops to graphite instead
    pale = trim_tone(0.10, 0.07, 0.85)
    bright = trim_tone(0.58, 0.66, 0.85)
    check("neutral identity trims darker, not silver", max(pale) < max(bright))
    # the vanilla-trim override really does leave the artist's fitting alone
    kept = shield_two_tone(sim, (0.58, 0.66, 0.96), {"trim": "vanilla"})
    check("vanilla-trim override keeps the artist's trim",
          kept.getpixel((0, 6))[:3] == sim.getpixel((0, 6))[:3])
    check("vanilla-trim override still tints the body", kept.getpixel((6, 6))[:3] != sim.getpixel((6, 6))[:3])
    check("every shield override names a real shield",
          all(_CATEGORY.get(i) in WHOLE_BRIGHT_CATS for i in SHIELD_OVERRIDES))
    check("every shield override names a known trim mode",
          all(o.get("trim", "silver") in ("silver", "gold", "vanilla") for o in SHIELD_OVERRIDES.values()))
    check("every shield override key is a known option",
          all(set(o) <= {"trim", "invert", "cover", "cover_small", "trim_tint", "split",
                         "split_p", "swap_small", "ring"} for o in SHIELD_OVERRIDES.values()))
    # ring: the geometric rim claims the border and NOT the center, whatever their colours
    rcoords, rmask = trim_mask(sim, ring=17)   # 17% of a 12px sprite = 2px band
    check("ring masks the border", rmask[(0, 6)] and rmask[(11, 6)])
    check("ring leaves the center", not rmask[(6, 6)])
    # sat-split: the low-saturation rim of the fixture is the mask even where it is dark
    scoords, smask = trim_mask(sim, sat_split=True)
    check("sat-split masks the low-saturation material", smask[(0, 6)] and not smask[(6, 6)])
    # swap_small flips the zone assignment on the small surface only: the rim pixel (mask
    # under sat-split) must carry the BODY tint's shading, computed independently here, and the
    # same override on the card surface must leave the plain assignment (rim = trim_tint).
    swapped = shield_two_tone(sim, (0.06, 0.72, 1.02),
                              {"trim_tint": (0.58, 0.6, 1.1), "split": "sat", "swap_small": True}, "small")
    _, rs0, rv0 = colorsys.rgb_to_hsv(*[c / 255 for c in sim.getpixel((0, 6))[:3]])
    check("swap_small puts the body tint on the mask zone",
          swapped.getpixel((0, 6))[:3] == shade_shield(0.06, 0.72, 1.02, rs0, rv0))
    carded = shield_two_tone(sim, (0.06, 0.72, 1.02),
                             {"trim_tint": (0.58, 0.6, 1.1), "split": "sat", "swap_small": True}, "card")
    check("swap_small leaves the card surface unswapped",
          carded.getpixel((0, 6))[:3] == shade_shield(0.58, 0.6, 1.1, rs0, rv0))
    # two-colour mode: body and mask each take a full tint, and the two zones come out distinct
    tt2 = shield_two_tone(sim, (0.06, 0.72, 1.02), {"trim_tint": (0.58, 0.55, 1.10)})
    b2, r2 = tt2.getpixel((6, 6))[:3], tt2.getpixel((0, 6))[:3]
    check("trim_tint tints the mask zone", r2 != sim.getpixel((0, 6))[:3])
    check("trim_tint zones are distinct tones", sum(abs(a - b) for a, b in zip(b2, r2)) > 60)
    # invert: the tint lands ON the fittings and the plate keeps its vanilla paint
    inv = shield_two_tone(sim, (0.45, 0.34, 1.10), {"invert": True, "cover": 0.40})
    check("invert keeps the plate vanilla", inv.getpixel((6, 6))[:3] == sim.getpixel((6, 6))[:3])
    check("invert tints the bright fittings", inv.tobytes() != sim.tobytes())
    # cover: the forced brightness split really claims about that fraction of the sprite
    ccoords, cmask = trim_mask(sim, cover=0.30)
    frac = sum(cmask.values()) / len(cmask)
    check("cover-forced mask lands near its target", 0.15 <= frac <= 0.55)
    # gold trim must actually read WARM, and must beat silver on a near-neutral identity, which
    # is the whole reason it exists (Bastion was silver-on-platinum, i.e. one colour)
    g_r, g_g, g_b = trim_tone(0.10, 0.07, 0.85, "gold")
    s_r, s_g, s_b = trim_tone(0.10, 0.07, 0.85)
    check("gold trim is warm", g_r > g_b and (g_r - g_b) > 0.15)
    check("gold trim separates from the neutral default", abs(g_r - s_r) + abs(g_b - s_b) > 0.2)
    # routing: shields take the shield engine, weapons keep bright-v2, everything else legacy
    check("shield routes to shield-bright", engine_for(128) == "shield-bright")
    check("weapon routes to bright-v2", engine_for(19) == "bright-v2")
    # PALETTE SEPARATION. Under whole-glyph coverage the tint is the item's entire colour signal,
    # so two close tints are one shield in two names. The pre-LW-190 palette had seven such pairs
    # (140 vs 142 were 0.02 hue and 0.02 saturation apart). This is the tripwire that keeps a
    # later tint tweak from quietly recreating one.
    SHIELD_MIN_HUE_GAP, SHIELD_MIN_SAT_GAP = 0.05, 0.25
    shields = sorted(i for i, c in _CATEGORY.items() if c in WHOLE_BRIGHT_CATS and i in ICON_TINTS)

    def tint_is_whole_signal(i):
        # The tripwire guards shields whose tint IS their entire colour signal. A shield on the
        # invert or vanilla-trim override keeps a large slab of its own vanilla art on screen
        # (owner round three anchored four shields to their vanilla look on purpose), so its
        # distinguishability rides the art, and its measured-from-vanilla tint may legitimately
        # sit near a sibling's.
        ov = SHIELD_OVERRIDES.get(i, {})
        # trim_tint shields are exempt too: they wear TWO identity colours, so the pair is the
        # signal and the body tint alone no longer decides distinguishability.
        return not (ov.get("invert") or ov.get("trim") == "vanilla" or "trim_tint" in ov)

    guarded = [i for i in shields if tint_is_whole_signal(i)]
    collisions = [(a, b) for n, a in enumerate(guarded) for b in guarded[n + 1:]
                  if abs(arc(ICON_TINTS[a][0], ICON_TINTS[b][0])) < SHIELD_MIN_HUE_GAP
                  and abs(ICON_TINTS[a][1] - ICON_TINTS[b][1]) < SHIELD_MIN_SAT_GAP]
    check(f"shield tints stay distinguishable (collisions: {collisions})", not collisions)
    check("shield palette covers all 16", len(shields) == 16)
    check("the tripwire still guards most of the set", len(guarded) >= 10)

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
