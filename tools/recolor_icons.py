#!/usr/bin/env python
"""
Recolor equipment menu icons from the vanilla originals to per-item tints.

Pipeline per item (both the 100x100 card image and the 48x48 list icon):
  vanilla BC7 .tex (Pac Files/0008) -> FF16Tools tex-conv -> DDS -> Pillow recolor
  -> img-conv --no-chunk-compression -> .tex placed in the mod tree.

FOUR recolor engines, routed per item by engine_for() (owner-directed; docs/TODO.md LW-189,
LW-190, LW-215 and LW-216 carry the decision trails):

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

  HELMETS (HELM_OVERRIDES) get the LW-215 two-tone helm treatment: helm_recolor's body tint
  under a contrast S-curve and a sheen, with the recipe's second colour blended across ONE
  feathered organic mask (cover = the bright fittings, shade = the recesses).

  HATS (ZONE_OVERRIDES) get the LW-216 THREE-ZONE treatment: zone_recolor lays N feathered zones
  over the body in list order, because a hat is cloth plus a brim or lining plus a plume or a
  painted emblem, and two zones cannot say that. It also carries two fixes the older engines do
  not have, since re-baking their already-approved art is a separate owner call: the halo ramp
  (LW-230) and the smooth-field contrast (LW-231).

  EVERYTHING ELSE (armor, accessories, hair adornments) keeps the ORIGINAL whole-icon tint: not
  yet reviewed under the new rules, so their shipped look must not change until an owner pass
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
from collections import deque
from pathlib import Path

from PIL import Image, ImageFilter

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
    # LW-202, 2026-08-14. The pre-pass palette is kept in the trailing comments because it is
    # the diagnosis: three of the six sat at saturation 0.15 or below and two more below 0.55,
    # so the family rendered as brown and grey sticks whatever engine ran over it. Every one is
    # now a saturated identity colour carried on the limb and frame, with a bright metal on the
    # stock (see ZONE_OVERRIDES). Hues are spread so the six stay nameable in one list.
    77: (0.075, 0.85, 1.12),  # Scoutbolt       honey amber wood      (was 0.08/0.40/0.85)
    78: (0.985, 0.92, 1.02),  # Knightslayer    blood crimson (Death) (was 0.99/0.55/0.55)
    79: (0.565, 0.82, 1.05),  # Arbalest        blued steel           (was 0.60/0.12/0.85)
    80: (0.265, 0.92, 1.05),  # Venombolt       venom green (Poison)  (was 0.25/0.70/0.85)
    81: (0.815, 0.88, 1.02),  # Snarebolt       plum (Immobilize)     (was 0.09/0.55/0.88)
    82: (0.695, 0.88, 0.98),  # Siegebolt       deep indigo (capstone)(was 0.60/0.15/0.68)
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


# --- LW-215 helmet engine -------------------------------------------------------------------
# Eleven of the thirteen helmets carry an owner-picked two-tone recipe (four review rounds,
# 2026-08-13; the two unpicked ids stay on the legacy tint until their letters land). Four
# picks are plain shield-engine recipes and route to shield_two_tone above. The other seven
# ride this engine: the identity tint on the body, a second tone across a FEATHERED ORGANIC
# mask (a brightness- or darkness-keyed zone that follows the art; the owner banned straight
# geometric mask edges from the 48px icon after round two), with per-recipe knobs for contrast
# expansion, metal sheen, dark-zone accent floors, and the gleam preserve.

HELM_SOLID = 160    # alpha floor for every mask/rank decision: the smoky semi-transparent halo
                    # around every sprite is darker than any real pixel, so ranking over all
                    # painted pixels spends the whole mask budget on invisible halo (measured:
                    # 80-94% of a darkness mask landed there on the 48px icons)


def images_equal(a, b):
    """Honest pixel identity. ImageChops.difference(...).getbbox() is NOT this: Pillow 10 made
    getbbox() alpha-only by default, so two pictures differing only in colour compare equal
    under the old idiom (proven against the shipped red Genji Helm vs its untinted vanilla)."""
    return a.size == b.size and a.tobytes() == b.tobytes()


def _helm_raw_mask(im, mode, pct):
    """Binary first pass. cover = brightest pct% of the SOLID sprite (fittings, wings, combs);
    shade = darkest pct% (recesses, plumes, visor voids). Both follow the art, never geometry."""
    px = im.load()
    coords = [(x, y) for y in range(im.height) for x in range(im.width) if px[x, y][3] >= 8]
    vals = {}
    for c in coords:
        if px[c][3] >= HELM_SOLID:
            r, g, b, _ = px[c]
            vals[c] = colorsys.rgb_to_hsv(r / 255, g / 255, b / 255)[2]
    if not vals:
        return coords, {c: False for c in coords}
    if mode == "shade":
        cut = _pct(list(vals.values()), pct)
        return coords, {c: (c in vals and vals[c] <= cut) for c in coords}
    cut = _pct(list(vals.values()), 100 - pct)
    return coords, {c: (c in vals and vals[c] > cut) for c in coords}


def _helm_despeckle(mask, min_blob):
    """Drop connected components smaller than min_blob, in BOTH polarities. This is the
    jaggedness the owner rejected in round one: BC7 sprite art misclassifies scattered single
    pixels under any per-pixel key, and they read as speckle across a clean plate."""
    for want in (True, False):
        seen = set()
        for start, val in mask.items():
            if val is not want or start in seen:
                continue
            blob, q = [], deque([start])
            seen.add(start)
            while q:
                c = q.popleft()
                blob.append(c)
                for dx, dy in ((1, 0), (-1, 0), (0, 1), (0, -1)):
                    n = (c[0] + dx, c[1] + dy)
                    if n in mask and n not in seen and mask[n] is want:
                        seen.add(n)
                        q.append(n)
            if len(blob) < min_blob:
                for c in blob:
                    mask[c] = not want
    return mask


def helm_mask(im, mode, pct, feather, min_blob, smooth=2):
    """Binary key -> majority smooth -> despeckle -> Gaussian feather into a 0..1 weight.

    Every spatial knob is authored against the 100px card and SCALED to the sprite: applied
    absolutely, the 48px icon got proportionally double the blur and a despeckle floor bigger
    than its real features, and the mask ate the icon's legitimate colour zones. Feather scales
    with width, the blob floor with width squared, and the icon drops one smoothing pass (the
    LW-190 trim hunt already measured two passes eating 1px features on 48px art)."""
    scale = im.width / 100.0
    coords, raw = _helm_raw_mask(im, mode, pct)
    passes = 0 if smooth <= 0 else (smooth if scale >= 0.72 else max(1, smooth - 1))
    raw = smooth_mask(raw, im.width, im.height, passes=passes)
    raw = _helm_despeckle({c: bool(v) for c, v in raw.items()}, max(2, round(min_blob * scale * scale)))
    f = feather * scale
    if f <= 0:
        return coords, {c: (1.0 if raw[c] else 0.0) for c in coords}
    buf = Image.new("L", im.size, 0)
    bp = buf.load()
    for c, v in raw.items():
        if v:
            bp[c] = 255
    buf = buf.filter(ImageFilter.GaussianBlur(f))
    bp = buf.load()
    return coords, {c: bp[c] / 255.0 for c in coords}


def _helm_scurve(v, median, amount):
    """Expand tonal separation around the sprite's OWN median brightness (amount 0 = off).
    The flat two-colour look the owner rejected came from the shoulder compressing the
    artist's centre-to-rim gradient; this puts the third tone back."""
    if amount <= 0:
        return v
    t = v - median
    return max(0.0, min(1.0, median + math.copysign(abs(t) ** (1.0 / (1.0 + amount)), t)
                        * (1.0 + 0.25 * amount)))


def _helm_sheen(rgb, strength, knee=0.72):
    """Specular pass, keyed on the OUTPUT tone's own brightness and gated on its chroma.
    Both halves are load-bearing: keyed on the vanilla pixel instead, a dark-tinted body on
    these mostly-bright sprites sheened everywhere and two near-black recipes rendered as
    white helmets; ungated, a hot brass faceplate washed to cream. Bare metal throws a hard
    white specular; lacquer and paint keep their colour in the highlight."""
    v = max(rgb)
    if strength <= 0 or v <= knee:
        return rgb
    chroma = 0.0 if v <= 0 else (v - min(rgb)) / v
    gate = 1.0 - min(1.0, chroma * 1.15)
    if gate <= 0.0:
        return rgb
    t = ((v - knee) / (1.0 - knee)) ** 1.4 * strength * gate
    return tuple(c + (1.0 - c) * min(0.60, t) for c in rgb)


def helm_body(tint, s0, v0, gleam=1.0):
    """shade_shield's exact math with the gleam preserve on a STRENGTH knob; at gleam=1.0 it
    is bit-identical to shade_shield (pinned in selftest, so shader drift fails loudly). The
    knob exists because the widened gleam branch protects light devices on dark shield plates,
    but on a near-white helmet sprite it fires on nearly every pixel and lifts a dark tint
    straight back toward white."""
    h_t, s_t, vmult = tint
    s_hot = min(0.97, s_t * 1.3 + 0.03)
    v = shoulder((v0 ** 0.88) * vmult)
    nh, ns = ramp_color(h_t, s_hot, v)
    rgb = colorsys.hsv_to_rgb(nh, ns, v)
    if gleam > 0 and s0 < SHIELD_GLEAM_S and v0 > SHIELD_GLEAM_V:
        lift = 0.60 * min(1.0, (v0 - SHIELD_GLEAM_V) / (1.0 - SHIELD_GLEAM_V) + 0.35) * gleam
        rgb = tuple(c + (1.0 - c) * lift for c in rgb)
    return rgb


def _helm_tone(spec, s0, v0, sheen, floor=0.0, gleam=1.0):
    """The mask zone's tone: a full [h,s,v] identity colour. floor guarantees brightness for
    an accent painted INTO a recess (unfloored, brass rivets and ember slits rendered
    near-black and three round-two helmets came back looking untinted). gleam routes through
    helm_body, not shade_shield, so a black comb can stay black and a gold crown gold on
    bright zones (trim_gleam low) while white and silver accents keep the full preserve."""
    v = floor + (1.0 - floor) * v0 if floor > 0 else v0
    return _helm_sheen(helm_body(spec, s0, v, gleam), sheen)


def helm_recolor(im, tint, opts, surface="card"):
    """Owner-picked two-tone: body tint under contrast expansion and sheen, the recipe's
    second colour blended across the feathered mask weight."""
    o = dict(opts or {})
    px = im.load()
    coords, w = helm_mask(im, o.get("mode", "cover"), o.get("pct", 22),
                          o.get("feather", 1.0), o.get("min_blob", 4))
    solid_v = []
    for c in coords:
        if px[c][3] >= HELM_SOLID:
            r, g, b, _ = px[c]
            solid_v.append(colorsys.rgb_to_hsv(r / 255, g / 255, b / 255)[2])
    median = sorted(solid_v)[len(solid_v) // 2] if solid_v else 0.5
    contrast, sheen = o.get("contrast", 0.0), o.get("sheen", 0.0)
    trim = tuple(o["trim"])
    out = im.copy()
    opx = out.load()
    for c in coords:
        r, g, b, a = px[c]
        _, s0, v0raw = colorsys.rgb_to_hsv(r / 255, g / 255, b / 255)
        v0 = _helm_scurve(v0raw, median, contrast)
        body = _helm_sheen(helm_body(tint, s0, v0, o.get("gleam", 1.0)), sheen)
        t = _helm_tone(trim, s0, v0, o.get("trim_sheen", sheen),
                       o.get("trim_floor", 0.0), o.get("trim_gleam", 1.0))
        wc = w[c]
        rgb = tuple(bb * (1 - wc) + tt * wc for bb, tt in zip(body, t))
        opx[c] = (*[max(0, min(255, int(x * 255 + 0.5))) for x in rgb], a)
    return out


# Owner picks, one entry per settled helmet (rounds and letters in the commit that added each).
# style "shield" = the recipe is a plain shield-engine override and bakes via shield_two_tone;
# style "helm" = it bakes via helm_recolor above. Body tints live in data/items.json iconTint
# (single source), same split as the shields' SHIELD_OVERRIDES.
HELM_OVERRIDES = {
    147: {"style": "shield", "trim_tint": (0.13, 0.86, 1.0), "split": "sat", "split_p": 44},
    153: {"style": "shield", "trim_tint": (0.12, 0.72, 1.16), "cover_small": 0.24},
    154: {"style": "shield", "trim": "vanilla", "cover_small": 0.18},
    155: {"style": "shield", "trim_tint": (0.11, 0.60, 1.02), "split": "sat", "split_p": 50},
    144: {"style": "helm", "mode": "cover", "pct": 22, "trim": (0.115, 0.85, 1.10),
          "feather": 1.2, "min_blob": 5, "contrast": 0.45, "sheen": 0.45},
    146: {"style": "helm", "mode": "cover", "pct": 20, "trim": (0.118, 0.80, 1.10),
          "trim_floor": 0.35, "trim_gleam": 0.3, "feather": 1.2, "min_blob": 5,
          "contrast": 0.5, "sheen": 0.45},
    148: {"style": "helm", "mode": "cover", "pct": 20, "trim": (0.10, 0.10, 1.18),
          "feather": 1.2, "min_blob": 5, "contrast": 0.5, "sheen": 0.5},
    149: {"style": "helm", "mode": "shade", "pct": 24, "trim": (0.995, 0.85, 0.95),
          "trim_floor": 0.5, "feather": 1.2, "min_blob": 5, "contrast": 0.5, "sheen": 0.45},
    150: {"style": "helm", "mode": "cover", "pct": 20, "trim": (0.58, 0.05, 1.20),
          "trim_sheen": 0.75, "gleam": 0.3, "feather": 1.2, "min_blob": 5,
          "contrast": 0.5, "sheen": 0.35},
    152: {"style": "helm", "mode": "shade", "pct": 22, "trim": (0.045, 0.75, 1.10),
          "trim_floor": 0.5, "feather": 1.1, "min_blob": 5, "contrast": 0.45, "sheen": 0.4},
    156: {"style": "helm", "mode": "cover", "pct": 28, "trim": (0.58, 0.03, 1.30),
          "trim_sheen": 0.8, "gleam": 0.15, "feather": 1.2, "min_blob": 5,
          "contrast": 0.45, "sheen": 0.4},
    # The last two, 2026-08-14. These close the helmet family: they were the only ones left on
    # the legacy one-hue stamp, and both of their old tints collided with a sibling anyway (145
    # sat 0.005 from the Clarion Helm's amber, 151 exactly on the Wardsteel Helm's teal).
    # 145 Mendsteel, Regen and Poison immunity: the one green helmet, with the gold landing on
    # the inset panel the artist painted across the brow, so the emblem stays an emblem.
    145: {"style": "helm", "mode": "cover", "pct": 22, "trim": (0.125, 0.90, 1.15),
          "trim_floor": 0.45, "trim_gleam": 0.30, "feather": 1.2, "min_blob": 5,
          "contrast": 0.50, "sheen": 0.45},
    # 151 Timeward, Stop immunity: the head slot's one bright metal, which is the honest answer
    # to a shelf with no hue left on it. The accent is on SHADE and not on cover: this helm's
    # bright pixels are scattered highlights along a horn, so a cover mask returns confetti,
    # while the recesses take brass cleanly and the whole thing reads as chrono-metal. An ice
    # blue version was tried and dropped, since four helmets are already blue.
    151: {"style": "helm", "mode": "shade", "pct": 26, "trim": (0.105, 0.92, 1.15),
          "trim_floor": 0.45, "trim_gleam": 0.30, "gleam": 0.30, "feather": 1.2, "min_blob": 6,
          "contrast": 0.55, "sheen": 0.45},
}


# --- LW-216 THREE-ZONE engine (hats) --------------------------------------------------------
# A helmet is one plate plus its fittings, so two zones say everything there is to say about it.
# A hat is not: it is cloth, plus a brim or an underside, plus a plume or a painted emblem, and
# the owner asked for all three ("is it possible to put three colors into it?", 2026-08-13). So
# this engine runs the same organic masks helm_recolor does, but N of them, composited over the
# body in list order. A later zone wins where two overlap, which is why an emblem is listed last:
# a white star inside a bright crest has to survive the crest, not average with it.

def _desat_raw(im, sat_p, val_p):
    """Binary SATURATION key: the desaturated-but-lit population of the solid sprite.

    The third mask key, and the one the two-zone engine had no way to express. The Arcanist
    Cap's star is white paint on pink felt, and a brightness key cannot see it because the lit
    felt beside it is exactly as bright; keying on saturation finds it in one pass."""
    px = im.load()
    coords = [(x, y) for y in range(im.height) for x in range(im.width) if px[x, y][3] >= 8]
    ss, vv = {}, {}
    for c in coords:
        if px[c][3] >= HELM_SOLID:
            r, g, b, _ = px[c]
            _, s0, v0 = colorsys.rgb_to_hsv(r / 255, g / 255, b / 255)
            ss[c], vv[c] = s0, v0
    if not ss:
        return coords, {c: False for c in coords}
    s_cut = _pct(list(ss.values()), sat_p)
    v_cut = _pct(list(vv.values()), val_p)
    return coords, {c: (c in ss and ss[c] <= s_cut and vv[c] >= v_cut) for c in coords}


def zone_weight(im, spec):
    """One zone spec -> per-pixel 0..1 weight. cover/shade delegate to helm_mask so those two
    cannot drift from the helmet engine. desat runs the same shape of chain (smooth, despeckle,
    feather, every spatial knob scaled to sprite width) but ONE SMOOTHING PASS FEWER than
    helm_mask at each size: 1 on the card and 0 on the icon, against helm_mask's 2 and 1.

    That difference is deliberate and it is why the chain is repeated here instead of shared. A
    saturation key is looking for small painted emblems, not for a plate's worth of fittings,
    and majority smoothing eats them: on the Arcanist Cap's star, helm_mask's pass counts return
    51px on the card and 26px on the icon against the 56 and 30 this keeps. If helm_mask's
    smoothing default ever changes, this branch will NOT follow, by design."""
    key = spec.get("key", "cover")
    feather = spec.get("feather", 1.2)
    min_blob = spec.get("min_blob", 5)
    if key in ("cover", "shade"):
        return helm_mask(im, key, spec.get("pct", 22), feather, min_blob)[1]
    scale = im.width / 100.0
    coords, raw = _desat_raw(im, spec.get("sat_p", 22), spec.get("val_p", 45))
    raw = smooth_mask(raw, im.width, im.height, passes=1 if scale >= 0.72 else 0)
    raw = _helm_despeckle({c: bool(v) for c, v in raw.items()},
                          max(2, round(min_blob * scale * scale)))
    f = feather * scale
    if f <= 0:
        return {c: (1.0 if raw[c] else 0.0) for c in coords}
    buf = Image.new("L", im.size, 0)
    bp = buf.load()
    for c, v in raw.items():
        if v:
            bp[c] = 255
    bp = buf.filter(ImageFilter.GaussianBlur(f)).load()
    return {c: bp[c] / 255.0 for c in coords}


HALO_LO, HALO_HI = 48, 224


def _halo_weight(a):
    """How much of the recolour a pixel may take, by its own alpha (LW-230).

    Every one of these sprites is drawn sitting in a soft semi-transparent haze, and the artist
    drew that haze NEUTRAL. The older engines paint every pixel down to alpha 8, so the haze
    takes the identity tint and the item looks like it is fuming; the owner rejected exactly
    that on the hat previews (2026-08-14).

    The first attempt ramped 64 -> 160, reusing HELM_SOLID because that is the threshold the
    mask code treats as real art, and the owner still saw smoke. Correctly: measured per alpha
    band, the 160-191 pixels were still taking the full tint (+0.54 saturation) and that band IS
    the visible outer glow. HELM_SOLID answers a different question ("is there enough signal
    here to classify?") than this one ("is this pixel solid enough to own the identity
    colour?"). 48 -> 224 is the ramp that holds, and its length is also what keeps a hard ring
    from appearing at the cutoff."""
    if a >= HALO_HI:
        return 1.0
    if a <= HALO_LO:
        return 0.0
    return (a - HALO_LO) / float(HALO_HI - HALO_LO)


DETAIL_GAIN = 0.30     # how much of the sprite's fine grain survives the contrast expansion


def _value_fields(im, radius):
    """Split the sprite's brightness into a SMOOTH field and a fine-grain residue (LW-231).

    The contrast S-curve is what makes a recolour read as a solid object instead of a flat
    stamp, but applied per pixel it also multiplies the art's own compression grain, and under a
    saturated tint that grain reads as blotchy coloured speckle across the surface. That speckle
    is what the owner kept calling smoke across three rounds: it is ON the hat, not around it,
    which is why two rounds of halo work never touched it.

    So the expansion runs on the BLURRED brightness (the artist's real shading: the domes, the
    folds, the Lumen Crown's ridge) and the residue is added back at reduced gain. Large
    features separate exactly as before; pixel noise stops being amplified."""
    px = im.load()
    raw = {}
    val = Image.new("L", im.size, 0)          # Pillow only blurs integer modes, not "F"
    vp = val.load()
    for y in range(im.height):
        for x in range(im.width):
            r, g, b, a = px[x, y]
            v = colorsys.rgb_to_hsv(r / 255, g / 255, b / 255)[2] if a >= 8 else 0.0
            raw[(x, y)] = v
            vp[x, y] = int(v * 255 + 0.5)
    sp = val.filter(ImageFilter.GaussianBlur(radius)).load()
    return raw, {c: sp[c] / 255.0 for c in raw}


def zone_recolor(im, tint, opts, surface="card"):
    """Body tint, then each zone blended over it by its own feathered weight, in list order."""
    o = dict(opts)
    zones = o["zones"]      # required: a recipe with no zones is a typo, not a plain tint
    sheen, contrast, gleam = o.get("sheen", 0.20), o.get("contrast", 0.50), o.get("gleam", 0.80)
    # No recipe overrides "detail"; it exists so the selftest can render the same fixture with
    # the fine-grain residue turned off and measure what the residue is worth, without having
    # to mutate the engine to find out.
    detail = o.get("detail", DETAIL_GAIN)
    px = im.load()
    coords = [(x, y) for y in range(im.height) for x in range(im.width) if px[x, y][3] >= 8]
    weights = [(z, zone_weight(im, z)) for z in zones]
    solid_v = []
    for c in coords:
        if px[c][3] >= HELM_SOLID:
            r, g, b, _ = px[c]
            solid_v.append(colorsys.rgb_to_hsv(r / 255, g / 255, b / 255)[2])
    median = sorted(solid_v)[len(solid_v) // 2] if solid_v else 0.5
    # Blur radius: 0.9px on the 100px card, scaled to width like every other spatial knob, but
    # FLOORED at 0.6 rather than following the scale down to 0.432 on the 48px icon. Sub-pixel
    # Gaussians barely separate the artist's shading from the compression grain, which is the
    # one thing this field exists to do, so the icon deliberately takes proportionally more
    # blur. Measured on the 48px sprites of ids 157/160/165 (horizontal field swing, raw vs
    # smoothed): the floor damps grain by 17-19% where pure scaling damps 10-12%, so the icon
    # keeps roughly half again as much of the fix. Reproduce with _value_fields at both radii.
    raw_v, smooth_v = _value_fields(im, max(0.6, 0.9 * im.width / 100.0))
    out = im.copy()
    opx = out.load()
    for c in coords:
        r, g, b, a = px[c]
        halo = _halo_weight(a)
        if halo <= 0.0:
            continue                      # pure haze: the artist's own pixel stands
        _, s0, _ = colorsys.rgb_to_hsv(r / 255, g / 255, b / 255)
        base = smooth_v[c]
        v0 = max(0.0, min(1.0, _helm_scurve(base, median, contrast)
                          + (raw_v[c] - base) * detail))
        rgb = _helm_sheen(helm_body(tint, s0, v0, gleam), sheen)
        for z, w in weights:
            wc = w.get(c, 0.0)
            if wc <= 0.002:
                continue
            zc = _helm_tone(z["tone"], s0, v0, z.get("sheen", sheen),
                            z.get("floor", 0.45), z.get("gleam", 0.30))
            rgb = tuple(bb * (1 - wc) + tt * wc for bb, tt in zip(rgb, zc))
        if halo < 1.0:
            van = (r / 255, g / 255, b / 255)
            rgb = tuple(v * (1 - halo) + n * halo for v, n in zip(van, rgb))
        opx[c] = (*[max(0, min(255, int(x * 255 + 0.5))) for x in rgb], a)
    return out


# The head slot's own colour vocabulary, settled over four owner review rounds. Body tints live
# in data/items.json iconTint (single source); these are the zones laid over them.
#
# Round three's rule, the one the owner called fire on the Arcanist Cap and which all four of the
# last hats are built on: body and lining far apart on the hue wheel, both saturated hard, and
# the third colour a NEUTRAL white rather than a tinted off-white. Read it as a direction, not as
# a formula the table obeys to three decimals: the four are 0.475 / 0.475 / 0.495 / 0.475 apart
# in hue at saturations of 0.88 to 0.93, while the Arcanist itself, the item the rule is named
# after, sits only 0.335 apart because its third colour is a white STAR rather than a crest and
# the picture could carry a closer pair. WHITE below is saturation 0.05, not zero, and renders
# around RGB 252/249/238; a true zero reads slightly cold against warm sprite art.
#
# Two knobs read as arbitrary and are not. The lining percentage is per item because it is a
# percentile of the sprite's OWN darkness, so the same number means a different thing on a
# different shape: on a pointed or brimmed hat the darkest quarter really is the brim, so 22 to
# 26 is right, but on a HOOD it is the shaded side of a smooth dome and a quarter comes back as
# camouflage across the cloth, so the three hooded shapes were swept 10 to 26 and settled at 14.
# And a crest much above 10 percent stops reading as a crest and starts reading as glare, a wet
# white patch across the crown.
WHITE = (0.130, 0.05, 1.35)


def _lining(pct, tone, floor=0.48, gleam=0.30, sheen=None, min_blob=5, feather=1.2):
    """The second colour, on the art's own dark share: an underside, a brim, deep folds. Half
    these hats have no second material at all, so an invented fitting would be trim the artist
    never drew; the dark zone reads as a lining instead, which is real.

    sheen None means inherit the recipe's own, which is not the same as passing the default: a
    lining sits in shadow, so it takes the body's restrained specular rather than the crest's."""
    z = {"key": "shade", "pct": pct, "tone": tone, "floor": floor, "gleam": gleam,
         "min_blob": min_blob, "feather": feather}
    if sheen is not None:
        z["sheen"] = sheen
    return z


def _crest(tone, pct=10, floor=0.55, gleam=0.35, sheen=0.30, min_blob=4, feather=0.7):
    """The third colour, on the lit crown."""
    return {"key": "cover", "pct": pct, "tone": tone, "floor": floor, "gleam": gleam,
            "sheen": sheen, "min_blob": min_blob, "feather": feather}


def _accent(pct, tone, floor=0.45, gleam=0.30, sheen=0.30, min_blob=5, feather=1.2):
    """The second colour where the artist DID draw one: a plume, a faceplate, a set stone."""
    return {"key": "cover", "pct": pct, "tone": tone, "floor": floor, "gleam": gleam,
            "sheen": sheen, "min_blob": min_blob, "feather": feather}


def _material(tone, sat_p=30, val_p=20, floor=0.45, gleam=0.35, sheen=0.35, min_blob=4,
              feather=0.8):
    """The second MATERIAL, separated by saturation rather than by brightness.

    Brightness keys answer "which part is lit"; on a solid object that happens to coincide with
    "which part is a fitting", and on line art it does not coincide with anything. A crossbow is
    the case that needs this: a thin stock, a curved limb, a string, a stirrup, and almost no
    broad surface, so a brightness crest claims 0.4 to 1.7 percent of the sprite and is simply
    invisible. What a crossbow does have is two MATERIALS, warm wood against grey metal, and
    saturation separates those in one pass: measured 23 to 26 percent of the solid sprite on all
    six, landing on the stock and leaving the limb and frame to the identity colour."""
    return {"key": "desat", "sat_p": sat_p, "val_p": val_p, "tone": tone, "floor": floor,
            "gleam": gleam, "sheen": sheen, "min_blob": min_blob, "feather": feather}


# The Arcanist Cap's star, found by saturation because white paint on pink felt is invisible to a
# brightness key. sat_p 12 with val_p 72 is the window that catches the paint and nothing else:
# looser settings claimed the cone's whole shadowed side and handed back a pink smudge. Claims
# 56px on the card and 30px on the icon.
STAR = {"key": "desat", "sat_p": 12, "val_p": 72, "min_blob": 2, "feather": 0.5,
        "tone": WHITE, "floor": 0.75, "gleam": 1.0}

STEEL = (0.575, 0.14, 1.28)
BONE = (0.105, 0.14, 1.30)
BRASS = (0.115, 0.90, 1.20)
GOLD = (0.125, 0.92, 1.24)
SILVER = (0.585, 0.10, 1.30)

ZONE_OVERRIDES = {
    # --- Crossbows (LW-202, 2026-08-14) ---------------------------------------------------
    # The owner's word for the family was dull, and it was true twice over. The tints were
    # timid, three of the six at saturation 0.15 or below, so the render came back looking like
    # the vanilla sprite with a wash over it. And the engine was wrong for the art: bright-v2
    # splits a picture into two clusters, which is right for a sword (blade against hilt) and
    # meaningless on line art with no second cluster in it.
    #
    # So every crossbow now runs one hot identity colour over the limb, frame and string, with a
    # bright METAL on the stock found by saturation. That is the owner's own taste rule from the
    # helmet rounds, a rich saturated body plus one bright metallic accent and two instantly
    # nameable materials, applied to a family that never had a second material before.
    77: {"zones": [_material(STEEL)]},                     # Scoutbolt   honey wood, steel stock
    78: {"zones": [_material(BONE)]},                      # Knightslayer blood and bone (Death)
    79: {"zones": [_material(BRASS)]},                     # Arbalest    blued steel and brass
    80: {"zones": [_material(GOLD)]},                      # Venombolt   venom green, gilt stock
    81: {"zones": [_material(SILVER)]},                    # Snarebolt   plum, cold silver
    82: {"zones": [_material(GOLD, sat_p=36)]},            # Siegebolt   the capstone, gold-heavy
    # --- Hats (LW-216) ----------------------------------------------------------------------
    # Round four, 2026-08-14: the four the owner never lettered, decided on the rule above.
    157: {"zones": [_lining(22, (0.690, 0.92, 1.05)), _crest(WHITE)]},           # Roughspun Cap
    159: {"zones": [_lining(14, (0.455, 0.88, 1.10)), _crest(WHITE)]},           # Adept's Hood
    160: {"zones": [_lining(14, (0.555, 0.90, 1.08)), _crest(WHITE)]},           # Martial Band
    165: {"zones": [_lining(14, (0.300, 0.92, 1.15)), _crest(WHITE)],            # Assassin's Cowl
          "gleam": 0.40},   # a cowl that reads bright is not a cowl, and the gleam preserve
                            # lifts this body straight back toward lilac if left at full
    # Round three: settled on the owner's letter E, "FIIIIIRE".
    161: {"zones": [_lining(26, (0.900, 0.92, 1.20)), STAR], "gleam": 0.28},     # Arcanist Cap
    # Rounds one and two: locked on the owner's save list, converted from the two-zone recipes
    # they were approved as. Same colours, same masks, now through the three-zone engine so the
    # whole family gets the halo ramp and the smooth-field contrast.
    158: {"zones": [_accent(20, (0.125, 0.88, 1.28), floor=0.50)],               # Wardplume
          "contrast": 0.55},
    162: {"zones": [_accent(22, (0.115, 0.85, 1.28), floor=0.50)],               # Zephyr Beret
          "contrast": 0.55},
    # 163's vanilla is near-white linen, so the gleam preserve fires on nearly every pixel and
    # lifts the tint straight back toward white (the documented helm_body trap). gleam 0.15 is
    # what lets a hot colour land on pale art at all.
    163: {"zones": [_accent(18, (0.130, 0.92, 1.28), floor=0.52, gleam=0.5,      # Twisted Headband
                            min_blob=4, feather=1.1)],
          "contrast": 0.60, "gleam": 0.15},
    164: {"zones": [_accent(24, (0.520, 0.80, 1.28), floor=0.50)],               # Hierophant Miter
          "contrast": 0.55},
    166: {"zones": [_accent(32, (0.130, 0.92, 1.30), floor=0.58, min_blob=4,     # Mana Coronet
                            feather=1.1)],
          "contrast": 0.55},
    # Round two, redone on the owner's notes. 167: the ridge is a dark seam the artist drew along
    # the crest and round one's gold clipped the whole top of the sprite to near-white, erasing
    # it; the value comes down, the gleam comes down, and the contrast goes up until the seam
    # separates from the cloth either side of it.
    167: {"zones": [_lining(26, (0.545, 0.88, 1.12), floor=0.42, sheen=0.30,     # Lumen Crown
                            feather=1.1)],
          "contrast": 0.78, "gleam": 0.30},
    # 168 was too close to the Hierophant's violet, so it separates by VALUE instead of hue: a
    # near-black cap with an ember plume, the only dark item on the shelf. trim gleam matters as
    # much as the colour here; left high the preserve returns the pale plume to white, which is
    # how round two lost the ember twice.
    168: {"zones": [_accent(24, (0.055, 0.95, 1.25), floor=0.55, gleam=0.15)],   # Nightrunner Cap
          "contrast": 0.65, "gleam": 0.15},
}


def recolor(im, hue, sat, val_mult):
    """LEGACY whole-icon tint: armor, accessories, and the hair adornments whose own re-pass has
    not run yet (their look must not change until it does); everything else predates LW-189
    review and keeps its shipped look the same way."""
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
    """Per-ITEM opt-in beats per-category default, which is why ZONE_OVERRIDES is consulted
    first: the zone engine is not tied to a family, and the re-pass reaches items whose category
    already has an engine. Crossbows are the case that forced the order. They are weapons, so
    the category rule sends them to bright-v2, but bright-v2 splits a picture into two clusters
    and a crossbow is line art with no second cluster in it, so it came back looking like the
    vanilla sprite with a wash over it."""
    if item_id in ZONE_OVERRIDES:
        return "three-zone"
    cat = _CATEGORY.get(item_id)
    if cat in WEAPON_CATS:
        return "bright-v2"
    if cat in WHOLE_BRIGHT_CATS:
        return "shield-bright"
    if cat == "Helmet" and item_id in HELM_OVERRIDES:
        return "helm-two-tone"
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
    if engine == "helm-two-tone":
        ov = dict(HELM_OVERRIDES[item_id])
        style = ov.pop("style")
        if style == "shield":
            return shield_two_tone(im, tint, ov, surface)
        return helm_recolor(im, tint, ov, surface)
    if engine == "three-zone":
        return zone_recolor(im, tint, ZONE_OVERRIDES[item_id], surface)
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
    # --- LW-215 helmet engine ---------------------------------------------------------------
    # Honest image identity: the old getbbox idiom compared ONLY alpha under Pillow 10+, so a
    # red and a blue image passed as identical. This pin keeps the comparison colour-aware.
    red = Image.new("RGBA", (4, 4), (255, 0, 0, 255))
    blue = Image.new("RGBA", (4, 4), (0, 0, 255, 255))
    ghost = red.copy()
    ghost.putpixel((0, 0), (255, 0, 0, 128))
    check("images_equal same is true", images_equal(red, red.copy()))
    check("images_equal sees colour-only difference", not images_equal(red, blue))
    check("images_equal sees alpha-only difference", not images_equal(red, ghost))
    # masks rank over SOLID pixels only: a sprite ringed by a dark semi-transparent halo must
    # never spend its shade budget on the halo (measured 80-94% loss on the 48px icons)
    him = Image.new("RGBA", (20, 20), (0, 0, 0, 0))
    for y in range(20):
        for x in range(20):
            edge = x in (0, 1, 18, 19) or y in (0, 1, 18, 19)
            if edge:
                him.putpixel((x, y), (10, 10, 10, 100))          # dark halo, NOT solid
            else:
                # dark recess in a plate whose brightness VARIES (a two-value plate defeats
                # percentile cuts: the cut can only land on the majority value)
                shade = 40 if (4 <= x <= 7 and 4 <= y <= 7) else 150 + (x + y) * 2
                him.putpixel((x, y), (min(shade, 235),) * 3 + (255,))
    hc, hm = _helm_raw_mask(him, "shade", 6)
    hpx = him.load()
    check("shade mask never lands on the halo", not any(hm[c] for c in hc if hpx[c][3] < HELM_SOLID))
    check("shade mask finds the real recess", hm[(5, 5)] and not hm[(14, 14)])
    # despeckle: an isolated wrong pixel dies in both polarities; a real blob survives
    spk = {(x, y): False for x in range(8) for y in range(8)}
    spk[(3, 3)] = True
    check("despeckle kills an isolated speck", not _helm_despeckle(dict(spk), 3)[(3, 3)])
    blob = {(x, y): (2 <= x <= 5 and 2 <= y <= 5) for x in range(8) for y in range(8)}
    check("despeckle keeps a real blob", _helm_despeckle(dict(blob), 3)[(3, 3)])
    # sheen: keyed on the OUTPUT tone (a dark body must not sheen), gated on chroma (saturated
    # gold keeps its colour while neutral silver takes the lift)
    check("sheen leaves a dark tone alone", _helm_sheen((0.3, 0.3, 0.3), 0.6) == (0.3, 0.3, 0.3))
    lift_n = _helm_sheen((0.9, 0.9, 0.9), 0.6)[0] - 0.9
    lift_g = _helm_sheen((0.9, 0.6, 0.1), 0.6)[0] - 0.9
    check("sheen lifts a bright neutral", lift_n > 0.01)
    check("sheen chroma gate protects saturated gold", lift_g < lift_n * 0.2)
    # gleam knob: at 1.0 helm_body IS shade_shield (bit-identical, so shader drift fails here);
    # at 0 a gleam-qualified pixel comes out darker (the lift that whitewashed dark bodies)
    drift = []
    for s0 in (0.05, 0.2, 0.35, 0.6):
        for v0 in (0.1, 0.5, 0.76, 0.9, 1.0):
            for tt in ((0.6, 0.1, 0.45), (0.02, 0.7, 0.8), (0.13, 0.85, 1.1)):
                a1 = tuple(int(c * 255) for c in helm_body(tt, s0, v0, gleam=1.0))
                if a1 != shade_shield(tt[0], tt[1], tt[2], s0, v0):
                    drift.append((tt, s0, v0))
    check(f"helm_body(gleam=1) is bit-identical to shade_shield (drift: {drift})", not drift)
    check("gleam 0 kills the whitewash on a dark body",
          max(helm_body((0.6, 0.1, 0.35), 0.1, 0.9, gleam=0.0))
          < max(helm_body((0.6, 0.1, 0.35), 0.1, 0.9, gleam=1.0)))
    # trim_floor: an accent painted into a recess must be able to read as a light source
    check("trim_floor lifts a dark-zone accent",
          max(_helm_tone((0.115, 0.85, 1.1), 0.4, 0.1, 0.0, floor=0.5))
          > max(_helm_tone((0.115, 0.85, 1.1), 0.4, 0.1, 0.0, floor=0.0)))
    # spatial knobs scale with sprite width: the same 2px feature a 100px card despeckles as
    # noise (floor 5) must SURVIVE on a 48px icon (floor scales to 2), or icons lose real zones
    def two_px_sprite(w):
        s = Image.new("RGBA", (w, w), (0, 0, 0, 0))
        for y in range(w):
            for x in range(w):
                s.putpixel((x, y), (60, 60, 60, 255))
        s.putpixel((w // 2, w // 2), (250, 250, 250, 255))
        s.putpixel((w // 2 + 1, w // 2), (250, 250, 250, 255))
        return s
    _, w100 = helm_mask(two_px_sprite(100), "cover", 2, feather=0.0, min_blob=5, smooth=0)
    _, w48 = helm_mask(two_px_sprite(48), "cover", 2, feather=0.0, min_blob=5, smooth=0)
    check("card-size despeckle eats a 2px speck", not any(v > 0 for v in w100.values()))
    check("icon-size blob floor scales down and keeps it", any(v > 0 for v in w48.values()))
    # scurve: off at 0, output clamped, monotone around the median
    check("scurve identity at amount 0", _helm_scurve(0.37, 0.5, 0.0) == 0.37)
    check("scurve expands away from the median",
          _helm_scurve(0.8, 0.5, 0.5) > 0.8 and _helm_scurve(0.2, 0.5, 0.5) < 0.2)
    # helm_recolor end to end on a synthetic sprite: body tinted, mask zone a distinct tone,
    # transparency untouched
    hr = helm_recolor(him, (0.6, 0.7, 0.9), {"mode": "shade", "pct": 6, "trim": (0.1, 0.8, 1.1),
                                             "trim_floor": 0.5, "feather": 0.5, "min_blob": 3})
    check("helm body takes the tint", hr.getpixel((14, 14)) != him.getpixel((14, 14)))
    check("helm mask zone is a distinct tone",
          sum(abs(a - b) for a, b in zip(hr.getpixel((5, 5))[:3], hr.getpixel((14, 14))[:3])) > 60)
    check("helm transparency untouched", hr.getpixel((0, 5))[3] == 100)
    # the picked-recipe table: every entry names a real helmet, a known style, known keys
    check("every helm override names a real helmet",
          all(_CATEGORY.get(i) == "Helmet" for i in HELM_OVERRIDES))
    check("every helm override names a known style",
          all(o.get("style") in ("shield", "helm") for o in HELM_OVERRIDES.values()))
    _SHIELD_KEYS = {"trim", "invert", "cover", "cover_small", "trim_tint", "split", "split_p",
                    "swap_small", "ring"}
    _HELM_KEYS = {"mode", "pct", "trim", "trim_floor", "trim_gleam", "trim_sheen",
                  "feather", "min_blob", "contrast", "sheen", "gleam"}
    check("shield-style helm recipes use only shield keys",
          all(set(o) - {"style"} <= _SHIELD_KEYS
              for o in HELM_OVERRIDES.values() if o["style"] == "shield"))
    check("helm-style recipes use only helm keys",
          all(set(o) - {"style"} <= _HELM_KEYS
              for o in HELM_OVERRIDES.values() if o["style"] == "helm"))
    check("helm-style masks are organic modes only",
          all(o.get("mode") in ("cover", "shade")
              for o in HELM_OVERRIDES.values() if o["style"] == "helm"))

    # routing: shields take the shield engine, weapons keep bright-v2, picked helmets the helm
    # engine, unpicked helmets (and everything unreviewed) legacy
    # --- LW-216 three-zone hat engine -------------------------------------------------------
    # The desat key exists because a brightness key CANNOT find painted-on paint. This fixture
    # is the Arcanist Cap's problem in miniature: a pale emblem sitting on a field that is
    # exactly as bright as the emblem and differs only in saturation. Both halves are the pin:
    # the saturation key finds it AND the brightness key does not, or the third key is dead
    # weight and someone will delete it in a tidy-up.
    dim = Image.new("RGBA", (16, 16), (0, 0, 0, 0))
    for y in range(16):
        for x in range(16):
            lit = 200 + x                      # gradient: flat fields defeat percentile cuts
            if 6 <= x <= 9 and 6 <= y <= 9:
                dim.putpixel((x, y), (lit, lit - 2, lit - 1, 255))       # emblem: near-neutral
            else:
                dim.putpixel((x, y), (lit, 40 + y, 90 + y, 255))         # felt: same value, hot
    _, dmask = _desat_raw(dim, 12, 40)
    # (14, 1) is felt at the emblem's OWN brightness. Testing against a dark felt pixel instead
    # would pass with the saturation half of the key deleted, which is the whole point of it.
    check("desat key finds the low-saturation emblem",
          dmask[(7, 7)] and not dmask[(14, 1)] and not dmask[(1, 1)])
    _, bmask = _helm_raw_mask(dim, "cover", 12)
    check("a brightness key cannot find that emblem (so the desat key is load-bearing)",
          not bmask[(7, 7)])
    # Halo ramp (LW-230): full tint only on genuinely opaque pixels, none on the artist's haze,
    # and a long ramp between so no hard ring appears at the cutoff.
    check("halo ramp is off below the floor", _halo_weight(HALO_LO) == 0.0 and _halo_weight(8) == 0.0)
    check("halo ramp is full above the ceiling", _halo_weight(HALO_HI) == 1.0 and _halo_weight(255) == 1.0)
    check("halo ramp is partial in between", 0.0 < _halo_weight(160) < 1.0)
    check("halo ramp is monotone",
          all(_halo_weight(a) <= _halo_weight(a + 1) for a in range(0, 255)))
    check("the ramp clears the mask threshold it used to reuse", HALO_HI > HELM_SOLID)
    # ...and end to end: a haze pixel keeps the artist's own colour while a solid one takes the
    # tint. Without this the identity colour becomes a cloud of coloured smoke around the item.
    # The fixture carries all THREE alpha populations, and the middle one is the point: a pixel
    # inside the 48-223 ramp is where the blend actually runs. A fixture with only haze and
    # solid pixels leaves the blend itself untested, because haze short-circuits before it and
    # solid multiplies by 1.0 through it, so the whole of LW-230 can be deleted under a green
    # gate. (Measured when it was: 765 pixels off on the Roughspun card, 817 on the
    # Nightrunner, max delta 201 of 255. That is the look the owner rejected twice.)
    haze = Image.new("RGBA", (14, 14), (0, 0, 0, 0))
    for y in range(14):
        for x in range(14):
            if x in (0, 13) or y in (0, 13):
                haze.putpixel((x, y), (130, 130, 130, 30))          # haze: below the ramp
            elif x in (1, 12) or y in (1, 12):
                haze.putpixel((x, y), (130, 130, 130, 140))         # INSIDE the ramp
            else:
                haze.putpixel((x, y), (120 + x, 110 + y, 100, 255))  # solid: above the ramp
    hz = zone_recolor(haze, (0.33, 0.9, 1.1), {"zones": [_lining(20, (0.9, 0.9, 1.2))]})
    check("halo pixels keep the artist's neutral haze", hz.getpixel((0, 7)) == haze.getpixel((0, 7)))
    check("solid pixels still take the identity colour", hz.getpixel((7, 7)) != haze.getpixel((7, 7)))
    ramp_px, ramp_van = hz.getpixel((1, 7))[:3], haze.getpixel((1, 7))[:3]
    full = zone_recolor(Image.new("RGBA", (14, 14), (130, 130, 130, 255)), (0.33, 0.9, 1.1),
                       {"zones": []}).getpixel((7, 7))[:3]
    check("a ramp-band pixel lands strictly between the artist's colour and the full tint",
          ramp_px != ramp_van and ramp_px != full
          and all(min(v, n) - 1 <= p <= max(v, n) + 1
                  for p, v, n in zip(ramp_px, ramp_van, full)))
    # ...and the band has to be the one that was measured as visible glow. At the first
    # attempt's ceiling of 160 the 160-191 pixels still took the full tint and the owner still
    # saw smoke, so a regression to any ceiling at or below HELM_SOLID must fail here.
    check("the ramp ceiling clears the whole visible glow band", HALO_HI >= 224)
    check("alpha is never touched by the recolour",
          all(hz.getpixel(c)[3] == haze.getpixel(c)[3] for c in ((0, 7), (1, 7), (7, 7))))
    # Smooth-field contrast (LW-231): the S-curve runs on the BLURRED brightness, so the art's
    # own compression grain stops being multiplied into coloured speckle. Measured as
    # neighbour-to-neighbour swing across a grainy field, against the per-pixel engine on the
    # SAME input, tint and contrast.
    grain = Image.new("RGBA", (24, 24), (0, 0, 0, 0))
    for y in range(24):
        for x in range(24):
            noise = 26 if (x * 7 + y * 3) % 5 == 0 else 0     # 1px speckle, no structure
            base = 90 + x * 4 + noise
            grain.putpixel((x, y), (base, base - 6, base - 12, 255))

    def swing(im):
        """Neighbour-to-neighbour swing along the x axis. The fixture's structural gradient also
        runs along x (measured: 2208 of the 7092 a grainy walk reports, i.e. under a third), so
        this is grain PLUS a constant pedestal, not grain alone. That is fine for comparing two
        engines on the same fixture and wrong for reading any single number as noise."""
        p = im.load()
        return sum(abs(p[x, y][0] - p[x + 1, y][0]) for y in range(24) for x in range(23))
    raw_f, smooth_f = _value_fields(grain, max(0.6, 0.9 * 24 / 100.0))
    field_swing = lambda f: sum(abs(f[(x, y)] - f[(x + 1, y)]) for y in range(24) for x in range(23))
    check("the smoothed field carries much less grain than the raw one",
          field_swing(smooth_f) < field_swing(raw_f) * 0.75)
    # The comparison that matters is against the shipped per-pixel engine on the SAME fixture
    # with EVERY other knob matched: sheen off, gleam full, an empty mask. Matching them is the
    # point. Compared loosely the two engines differ for a dozen reasons and the check passes
    # whatever the curve does; matched, the only surviving difference is which brightness field
    # the S-curve expanded, and bypassing the smooth field makes these two exactly equal.
    matched = {"contrast": 0.7, "sheen": 0.0, "gleam": 1.0}
    smoothed = zone_recolor(grain, (0.6, 0.8, 1.0), dict(matched, zones=[]))
    perpixel = helm_recolor(grain, (0.6, 0.8, 1.0),
                            dict(matched, mode="cover", pct=0, trim=(0.6, 0.8, 1.0)))
    check("smooth-field contrast amplifies less grain than the per-pixel curve",
          swing(smoothed) < swing(perpixel) * 0.9)
    # The other half of the fix: the grain is DAMPED, not deleted. Measured against the same
    # render with the residue term removed, which is a flat blurred stamp; "swing > 0" is not
    # this test, because the fixture's own gradient satisfies that with the residue gone.
    no_detail = zone_recolor(grain, (0.6, 0.8, 1.0), dict(matched, zones=[], detail=0.0))
    check("the fine grain is damped, not thrown away",
          0.0 < DETAIL_GAIN < 1.0 and swing(smoothed) > swing(no_detail) * 1.05)
    # Three zones, composited in list order, and the LAST one wins where two overlap. That is
    # what lets a white star sit inside a bright crest instead of averaging with it.
    tri = Image.new("RGBA", (20, 20), (0, 0, 0, 0))
    for y in range(20):
        for x in range(20):
            v = 60 + y * 8 + x                    # dark at the top, bright at the bottom
            tri.putpixel((x, y), (min(v, 250), min(v, 250) - 20, min(v, 250) - 40, 255))
    zoned = {"zones": [_lining(20, (0.9, 0.92, 1.2)), _crest((0.6, 0.9, 1.2), pct=20)]}
    three = zone_recolor(tri, (0.33, 0.9, 1.05), zoned)
    plain = zone_recolor(tri, (0.33, 0.9, 1.05), {"zones": []})
    body_px, lin_px, crest_px = three.getpixel((10, 10)), three.getpixel((10, 0)), three.getpixel((10, 19))
    # Measured AGAINST THE SAME RENDER WITH NO ZONES, not against each other. The three sample
    # pixels sit at three different brightnesses on the fixture's gradient, so a body tint alone
    # already puts 215-562 between them and an each-other comparison passes with zones deleted.
    check("each zone actually repaints its own region",
          all(sum(abs(a - b) for a, b in zip(three.getpixel(c)[:3], plain.getpixel(c)[:3])) > 30
              for c in ((10, 0), (10, 19)))
          and three.getpixel((10, 10)) == plain.getpixel((10, 10)))
    check("three zones come out as three distinct tones",
          sum(abs(a - b) for a, b in zip(body_px[:3], lin_px[:3])) > 40
          and sum(abs(a - b) for a, b in zip(body_px[:3], crest_px[:3])) > 40
          and sum(abs(a - b) for a, b in zip(lin_px[:3], crest_px[:3])) > 40)
    # zone_weight's desat branch, through the ROUTING rather than by calling _desat_raw: the
    # pins above prove the key works, this proves the engine reaches it. Rerouted to a
    # brightness mask, the emblem stops being repainted and this goes red.
    star_spec = {"key": "desat", "sat_p": 12, "val_p": 40, "min_blob": 2, "feather": 0.0,
                 "tone": WHITE, "floor": 0.75, "gleam": 1.0}
    starred = zone_recolor(dim, (0.9, 0.9, 1.0), {"zones": [star_spec]})
    bare = zone_recolor(dim, (0.9, 0.9, 1.0), {"zones": []})
    check("a desat zone routes through zone_weight and repaints the emblem only",
          sum(abs(a - b) for a, b in zip(starred.getpixel((7, 7))[:3], bare.getpixel((7, 7))[:3])) > 30
          and starred.getpixel((14, 1)) == bare.getpixel((14, 1))
          and starred.getpixel((1, 1)) == bare.getpixel((1, 1)))
    # Order, pinned by WHICH zone wins rather than by the two orderings differing: two zones on
    # the identical unfeathered mask, so the second must overwrite the first completely. Merely
    # asserting the two orderings differ passes just as happily when the list is composited
    # backwards, and backwards is exactly the bug this guards (a star listed last would end up
    # underneath the crest it is painted on).
    blue, white = _crest((0.6, 0.9, 1.2), pct=20, feather=0.0), _crest(WHITE, pct=20, feather=0.0)
    both = zone_recolor(tri, (0.33, 0.9, 1.05), {"zones": [blue, white]})
    only_white = zone_recolor(tri, (0.33, 0.9, 1.05), {"zones": [white]})
    only_blue = zone_recolor(tri, (0.33, 0.9, 1.05), {"zones": [blue]})
    check("a later zone wins where two overlap",
          both.getpixel((10, 19)) == only_white.getpixel((10, 19))
          and both.getpixel((10, 19)) != only_blue.getpixel((10, 19)))
    # Transparency: a sprite with THREE alpha levels must come back with all three intact. The
    # earlier version of this pin read an opaque fixture pixel and a haze pixel the engine never
    # writes, so destroying every written pixel's alpha passed it.
    check("hat transparency untouched",
          all(hz.getpixel(c)[3] == haze.getpixel(c)[3]
              for c in ((0, 0), (0, 7), (1, 7), (7, 7), (13, 13))))
    # the picked-recipe table. It spans families now, so the pin is that every id belongs to a
    # family that has actually been through a review pass, not that they are all hats: the zone
    # engine takes items per item, and engine_for consults this table BEFORE any category rule,
    # so a stray id here silently overrides its family's engine.
    _ZONED_CATS = {"Hat", "Crossbow"}
    check("every zone override names an item from a reviewed family",
          all(_CATEGORY.get(i) in _ZONED_CATS for i in ZONE_OVERRIDES))
    check("every zone override key is a known option",
          all(set(o) <= {"zones", "gleam", "contrast", "sheen", "detail"}
              for o in ZONE_OVERRIDES.values()))
    # Keys are checked PER MASK KEY, not as one pooled set. Pooled, a desat zone typo'd with
    # "pct" instead of "sat_p" passes and then silently runs on the default window.
    _COMMON = {"key", "tone", "floor", "gleam", "sheen", "min_blob", "feather"}
    _BY_KEY = {"cover": _COMMON | {"pct"}, "shade": _COMMON | {"pct"},
               "desat": _COMMON | {"sat_p", "val_p"}}
    zones_all = [z for o in ZONE_OVERRIDES.values() for z in o["zones"]]
    check("every zone names a known mask key",
          all(z.get("key") in _BY_KEY for z in zones_all))
    check("every zone key is a known option for ITS mask key",
          all(set(z) <= _BY_KEY[z["key"]] for z in zones_all if z.get("key") in _BY_KEY))
    check("every zone override carries at least one zone",
          all(o["zones"] for o in ZONE_OVERRIDES.values()))
    check("every zone override has a body tint to lay its zones over",
          all(i in ICON_TINTS for i in ZONE_OVERRIDES))

    # routing: shields take the shield engine, unreviewed weapons keep bright-v2, picked helmets
    # the helm engine, anything in the zone table the three-zone engine, everything else legacy
    check("shield routes to shield-bright", engine_for(128) == "shield-bright")
    check("weapon routes to bright-v2", engine_for(19) == "bright-v2")
    check("picked helmet routes to helm-two-tone", engine_for(156) == "helm-two-tone")
    check("the last two helmets joined the helm engine",
          engine_for(145) == "helm-two-tone" and engine_for(151) == "helm-two-tone")
    check("picked hat routes to three-zone", engine_for(157) == "three-zone")
    # The crossbows are the pin that the per-item table BEATS the per-category rule: they are
    # weapons, so a category-first engine_for would send them to bright-v2 and quietly ignore
    # their recipes.
    check("a picked crossbow beats its category's engine", engine_for(77) == "three-zone")
    check("all twelve hats and all six crossbows are picked",
          sorted(ZONE_OVERRIDES)
          == [i for i, c in sorted(_CATEGORY.items()) if c in ("Hat", "Crossbow")])
    # hair adornments share the slot but ship under their own row (LW-217), so they must NOT
    # have quietly ridden along on this pass
    check("hair adornments stay legacy until their own pass",
          all(engine_for(i) == "legacy" for i, c in _CATEGORY.items() if c == "HairAdornment"))
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
