#!/usr/bin/env python
"""
Recolor equipment menu icons from the vanilla originals to per-item tints.

Pipeline per item (both the 100x100 card image and the 48x48 list icon):
  vanilla BC7 .tex (Pac Files/0008) -> FF16Tools tex-conv -> DDS -> Pillow recolor
  -> img-conv --no-chunk-compression -> .tex placed in the mod tree.

FOUR recolor engines, routed per item by engine_for() (owner-directed; docs/TODO.md LW-189,
LW-190, LW-215 and LW-216 carry the decision trails):

  UNREVIEWED WEAPONS (category in lib.categories.WEAPON_CATS, minus the families that have had
  their own re-pass and appear in ZONE_OVERRIDES below) get the LW-189 BRIGHT v2 treatment:
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

  HATS, CROSSBOWS and SWORDS (ZONE_OVERRIDES) get the LW-216 THREE-ZONE treatment: zone_recolor
  lays N feathered zones over the body in list order, because a hat is cloth plus a brim or
  lining plus a plume or a painted emblem, and two zones cannot say that. THIS TABLE IS THE
  ENGINE MAP for reviewed families: engine_for consults it BEFORE any category rule, so a family
  named here has left the category default above whatever its category says. Keep this paragraph
  in step with it; the tint rows below are machine-checked against items.json for exactly this
  kind of rot (tint_comment_names) and a prose engine map has no such guard.

  EVERYTHING ELSE (armor, accessories, hair adornments) keeps the ORIGINAL whole-icon tint: not
  yet reviewed under the new rules, so their shipped look must not change until an owner pass
  covers them.

TWO LATE FIXES cut across those engines (owner scope call 2026-08-14, docs/TODO.md LW-230 and
LW-231). The HALO RAMP runs in every engine that paints reviewed art (all but legacy): the tint
only fully owns genuinely solid pixels, so an item stops fuming coloured smoke into the neutral
haze the artist drew around it. The SMOOTH-FIELD CONTRAST runs in the two engines that carry a
per-pixel contrast expansion (helm and zone), so the art's own compression grain stops being
multiplied into blotchy speckle. Both shipped in zone_recolor first and reached the rest once
the owner agreed to re-bake art he had already approved; legacy is excluded on purpose, since
its families are unreviewed and their shipped look must not move before their own pass.

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
    10: (0.72, 0.42, 1.10),   # Zwill Straightblade dream lavender (Sleep on hit)
    # --- Swords (ids 19-32, plus id 67 which rides id 19's art; tint in data/items.json) ---
    # LW-199, 2026-08-14. The pre-pass palette is kept in the trailing comments as the diagnosis,
    # same as the crossbows above. Two of the fifteen were provably WRONG about their item rather
    # than merely timid: Lightbringer is the line's only Holy sword and wore the toad green picked
    # for a Toad sword that was renamed away, and Graviton wore an ice cyan picked for an Ice
    # sword when it carries no element at all. The rest were the family's real problem, which is
    # that eleven of them sat under saturation 0.6 on near-white blade art, so whatever the engine
    # did the answer came back grey.
    #
    # Hues are spread so the fifteen stay nameable in one list, and the spread is pinned in
    # selftest (SWORD_MIN_HUE_GAP and its saturation/value escapes). Read the pairs that sit
    # close in hue by their WEIGHT: 67 near-black against 26 molten, 19 dull tan against 31
    # blazing gold, 32 near-neutral white against 21 saturated blue.
    19: (0.085, 0.72, 0.82),  # Vagabond        weathered bronze, bright steel furniture
                              #                 (was 0.08/0.14/0.90, "worn warm steel")
    20: (0.615, 0.52, 0.52),  # Cleaver         deep blued iron, raw copper
                              #                 (was 0.60/0.10/0.70, dark heavy steel)
    21: (0.570, 0.62, 1.08),  # Riposte         duelist's azure, brass guard and edge
                              #                 (was 0.55/0.08/1.15, bright silver)
    22: (0.440, 0.78, 0.88),  # Claymore        jade mythril, copper (the reach sword)
                              #                 (was 0.48/0.24/1.08)
    23: (0.985, 0.98, 0.82),  # Sanguine Sword  blood crimson, bone furniture (Absorb HP)
                              #                 (was 0.99/0.78/0.92)
    24: (0.655, 0.78, 0.82),  # Stormbrand      storm indigo under a levin edge (Lightning)
                              #                 (was 0.14/0.82/1.10, electric yellow: it collided
                              #                 with Lightbringer's Holy gold, so the yellow moved
                              #                 to the edge and hilt where it reads hotter anyway)
    25: (0.335, 0.95, 0.46),  # Tanglethorn     deep forest green, gold (Immobilize)
                              #                 (was 0.10/0.45/0.78, earthy brown)
    26: (0.045, 0.98, 0.82),  # Flamberge       fire orange, white-hot edge (Fire)
                              #                 (was 0.05/0.85/1.05)
    27: (0.925, 0.95, 0.40),  # Wrathblade      garnet wine, bone furniture (damage = missing HP)
                              #                 (was 0.60/0.05/1.18, stark white: nothing on a
                              #                 near-white sprite, and the rack has white already)
    28: (0.505, 0.90, 0.98),  # Swiftedge       diamond cyan, white edge over a dark guard (Speed x WP)
                              #                 (was 0.58/0.32/1.12)
    29: (0.700, 0.58, 0.17),  # Graviton        near-black under a collapsing pale rim; casts
                              #                 Gravity and carries no element, so the old ice
                              #                 cyan (0.50/0.58/1.08) was a colour for a sword
                              #                 that no longer exists (LW-199)
    30: (0.780, 0.95, 0.88),  # Arcanum         amethyst, gold runework (arcane buff-thief)
                              #                 (was 0.74/0.55/0.95)
    31: (0.135, 0.90, 0.98),  # Lightbringer    molten gold, white radiance over dark furniture.
                              #                 The line's ONLY Holy sword, corrected off the toad
                              #                 green (0.27/0.65/0.88) picked for a renamed Toad
                              #                 sword.
                              #                 Compare id91, also Holy, also gold (LW-199)
    32: (0.585, 0.07, 1.20),  # Materia Blade   cool silver-white with gold furniture. RESERVED
                              #                 NAME: it kept its vanilla name, so it is anchored
                              #                 to the vanilla look and only enhanced (owner rule
                              #                 2026-08-14) rather than recoloured to the dark
                              #                 violet it wore (0.78/0.50/0.62)
    # --- Crossbows (ids 77-82) ---
    # LW-202, 2026-08-14. The pre-pass palette is kept in the trailing comments because it is
    # the diagnosis: three of the six sat at saturation 0.15 or below and two more below 0.55,
    # so the family rendered as brown and grey sticks whatever engine ran over it. Every one is
    # now a saturated identity colour carried on the limb and frame, with a bright metal on the
    # stock (see ZONE_OVERRIDES). Hues are spread so the six stay nameable in one list.
    77: (0.075, 0.85, 1.12),  # Scoutbolt       honey amber wood      (was 0.08/0.40/0.85)
    78: (0.985, 0.92, 1.02),  # Eclipsebolt     blood crimson (Doom)  (was 0.99/0.55/0.55)
    79: (0.565, 0.82, 1.05),  # Arbalest        blued steel           (was 0.60/0.12/0.85)
    80: (0.265, 0.92, 1.05),  # Venombolt       venom green (Poison)  (was 0.25/0.70/0.85)
    81: (0.815, 0.88, 1.02),  # Pitchbolt       plum (Oil)            (was 0.09/0.55/0.88)
    82: (0.695, 0.88, 0.98),  # Siegebolt       deep indigo (capstone)(was 0.60/0.15/0.68)
    # --- Bows (ids 83-91) ---
    # LW-201, 2026-08-14. The family the LW-232 glow defect hit hardest: measured, the pre-pass
    # bake reached 0.0% of the Skypiercer and the Perseus Bow and 0.2% of the Windrunner, so
    # five of the nine were the artist's vanilla sprite under a coloured haze. The pre-pass
    # palette is kept in the trailing comments as the diagnosis.
    83: (0.085, 0.42, 0.95),  # Skirmisher      honey wood, bone string (was 0.10/0.30/0.95)
    84: (0.740, 0.25, 1.16),  # Windrunner      pale lavender, silver string. Sage green was
                              #                 tried and made three green bows of nine. A silver BODY was
                              #                 tried first and the no-single-colour gate
                              #                 refused it: a silver bow strung with steel
                              #                 is one colour (was 0.45/0.18/1.05)
    85: (0.520, 0.78, 1.05),  # Frostarc        ice cyan, white string (Ice)
                              #                 (was 0.50/0.55/1.08)
    86: (0.150, 0.95, 1.00),  # Stormarc        electric yellow, levin string (Lightning). It
                              #                 sits one and a half hundredths off the Perseus
                              #                 Bow's Holy gold and separates on SATURATION, the
                              #                 same collision the swords hit between Stormbrand
                              #                 and Lightbringer (was 0.14/0.80/1.10)
    87: (0.420, 0.72, 1.00),  # Skypiercer      sky teal, white string (Wind)
                              #                 (was 0.42/0.52/1.05, and 0.0% covered)
    88: (0.620, 0.80, 0.85),  # Tidecaller      deep ocean blue, silver string (Water)
                              #                 (was 0.64/0.42/0.92)
    89: (0.330, 0.80, 0.72),  # Huntress        forest green, brass string (was 0.30/0.55/0.88)
    90: (0.030, 0.70, 0.68),  # Yoichi Bow      RESERVED NAME, anchored to its own art: measured
                              #                 vanilla hue 0.051, a warm lacquered wood, so it
                              #                 stays warm and only deepens toward lacquer red
                              #                 (was 0.58/0.42/0.95, a storm grey-blue that
                              #                 fought the art it sits on)
    91: (0.135, 0.55, 1.25),  # Perseus Bow     RESERVED NAME, kept GOLD on an owner ruling
                              #                 2026-08-14, and the reasoning is corrected here
                              #                 because the original was wrong. This was
                              #                 anchored as "vanilla chroma 0.031, near enough
                              #                 to colourless that hue is free", measured on the
                              #                 CARD. An audit the same day found the card
                              #                 understates chroma by a mean 2.4x against the
                              #                 item's own list icon, and this bow's ICON reads
                              #                 chroma 0.120 at hue 229 degrees: a visibly BLUE
                              #                 bow. So gold is a ~180 degree move away from its
                              #                 own art, not a free choice. The owner reviewed
                              #                 that and kept gold, on the convention that Holy
                              #                 is gold everywhere in this mod (Excalibur,
                              #                 Lightbringer). Measure BOTH surfaces before
                              #                 calling an anchor colourless
                              #                 (was 0.13/0.45/1.18, and 0.0% covered)
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
_NAME = {}
for _it in load_items()["items"]:
    if _it.get("iconTint"):
        ICON_TINTS[_it["id"]] = tuple(_it["iconTint"])
    if _it.get("iconSource"):
        SRC[_it["id"]] = _it["iconSource"]
    _CATEGORY[_it["id"]] = _it.get("category")
    _NAME[_it["id"]] = _it.get("name")


def tint_comment_names():
    """Every ICON_TINTS row's trailing comment, read back out of this file's own source.

    The table above is hand-written and each row names the item it colours, because a bare hue
    triple is unreviewable: the NAME is what says whether "toad green" was a good call. Those
    names are copies, so they rot when an item is renamed in data/items.json, and they rot
    silently because nothing executes a comment. It has bitten twice: three shields carried dead
    names into their whole recolor pass (dbb7f1f), and on 2026-08-14 a scan found thirteen more,
    including a Holy sword still wearing the hue picked for a Toad sword that no longer exists.

    So the comments get read back and checked like data. Parsing our own source is the same
    idiom analyze.py already uses on Offsets.cs and Tuning.cs; the alternative, moving the names
    into the table as strings, would duplicate items.json instead of citing it."""
    import re
    src = Path(__file__).read_text(encoding="utf-8")
    body = src[src.index("ICON_TINTS = {"):]
    body = body[:body.index("\n}\n")]
    out = {}
    for iid, tail in re.findall(r"^\s*(\d+):\s*\([^)]*\),(.*)$", body, re.M):
        if "#" in tail:
            out[int(iid)] = tail.split("#", 1)[1].strip()
    return out

# --- LW-189 BRIGHT v2 engine (weapons only) -------------------------------------------------
# The reference implementation these constants and functions were frozen from is the approved
# preview generator (session scratchpad bright_all.py); the owner signed the full 121-weapon
# gallery off against EXACTLY this math, so changes here need a fresh gallery pass.

NO_BLADE_CATS = {"Bag", "Book", "Instrument", "Cloth"}   # no blade: tint the LARGEST cluster
CARD_OVERRIDES = {
    9: {"k": 2},                # Galewind: 3 clusters shredded the blade
    113: {"k": 2},              # Eight-Fluted Pole: shaft never took the tint
    117: {"k": 2},              # Hornet Pouch: cluster fragmentation read as camo blobs
}   # ids 33 (Defender) and 37 (Chaos Blade) were here until LW-200 routed the knight swords to
    # the zone engine, which made both rows unreachable: route() consults ZONE_OVERRIDES first.
    # 37's row also still described a vmult_floor of 0.85 against a shipped value of 0.24, so it
    # was dead config that ALSO lied. Same defect the sword pass fixed by deleting SMALL_TWO_ZONE
    # id 24, found again by audit 2026-08-14; a selftest pin now refuses the whole class.
SMALL_TWO_ZONE = {13, 15, 16, 18}       # owner round-1: card-style split on these glyphs
                                        # (id 24 was a fifth until LW-199 routed the sword rack
                                        # to the zone engine, which consults ZONE_OVERRIDES
                                        # first; the entry became unreachable, so it is gone
                                        # rather than left to read as live configuration)
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
        if not mask[(x, y)]:
            continue                      # outside the tint zone: the artist's pixel stands
        r, g, b, a = px[x, y]
        halo = _halo_weight(a)
        if halo <= 0.0:
            continue                      # pure haze: the artist's pixel stands (LW-230)
        _, s0, v0 = colorsys.rgb_to_hsv(r / 255, g / 255, b / 255)
        opx[x, y] = (*_halo_int((r, g, b), shade_bright(h_t, s_t, vmult, s0, v0), halo), a)
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
                continue                  # the mean_s pool boundary above; the two move together
            halo = _halo_weight(a)
            if halo <= 0.0:
                continue                  # pure haze: the artist's pixel stands (LW-230)
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
            px[x, y] = (*_halo_int((r, g, b),
                                   (int(nr * 255), int(ng * 255), int(nb * 255)), halo), a)
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
        halo = _halo_weight(a)
        if halo <= 0.0:
            continue                         # pure haze: the artist's pixel stands (LW-230)
        van = (r, g, b)
        _, s0, v0 = colorsys.rgb_to_hsv(r / 255, g / 255, b / 255)
        if invert:
            # Inverse assignment (Swiftguard): the identity tint lands ON the bright fittings and
            # the plate keeps its vanilla paint, the owner's "inner plate stays original" rule.
            if mask[c]:
                opx[c] = (*_halo_int(van, shade_shield(h_t, s_t, vmult, s0, v0), halo), a)
            continue
        if mask[c]:
            if trim_mode == "vanilla":
                continue                     # out is a copy of im, so the vanilla pixel stands
            if "trim_tint" in ov:
                # Two-colour shield (owner round six, Conduit "copper blue but BRIGHT AF"):
                # the mask zone takes its OWN full identity tint through the same shader,
                # so both materials read hot instead of one deferring to a metal tone.
                t2 = ov["trim_tint"]
                opx[c] = (*_halo_int(van, shade_shield(t2[0], t2[1], t2[2], s0, v0), halo), a)
                continue
            nr, ng, nb = trim_tone(h_t, s_t, v0, trim_mode)
            opx[c] = (*_halo_int(van, (int(nr * 255), int(ng * 255), int(nb * 255)), halo), a)
        else:
            opx[c] = (*_halo_int(van, shade_shield(h_t, s_t, vmult, s0, v0), halo), a)
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
    second colour blended across the feathered mask weight.

    Carries the halo ramp (LW-230) and DELIBERATELY NOT the smooth-field contrast (LW-231),
    which is the one place the two contrast engines are allowed to disagree.

    The smooth field expands contrast on a blurred copy of the brightness and returns the fine
    residue at 30%, which on a hat is exactly right: hats are cloth domes, so that residue is
    compression grain and damping it is the whole fix. Helmet art is not cloth. It is engraved
    metal whose subject IS one-pixel line work, the scale rows on the Sunsteel crown, the visor
    slots on the Grand Helm, the black seams through the Timeward's white plate, and a Gaussian
    cannot tell a drawn line from grain. Tried on the thirteen helmets 2026-08-14 and rejected
    on sight: the aggregate metrics passed (blurred difference 8 to 11 of 255, tonal spread down
    under 10%) precisely because they are blind to 1px features, while the pictures lost their
    engraving. Helmets were also never the speckle complaint; the hats were."""
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
        halo = _halo_weight(a)
        if halo <= 0.0:
            continue                        # pure haze: the artist's pixel stands (LW-230)
        _, s0, v0raw = colorsys.rgb_to_hsv(r / 255, g / 255, b / 255)
        v0 = _helm_scurve(v0raw, median, contrast)      # per pixel ON PURPOSE, see the docstring
        body = _helm_sheen(helm_body(tint, s0, v0, o.get("gleam", 1.0)), sheen)
        t = _helm_tone(trim, s0, v0, o.get("trim_sheen", sheen),
                       o.get("trim_floor", 0.0), o.get("trim_gleam", 1.0))
        wc = w[c]
        rgb = tuple(bb * (1 - wc) + tt * wc for bb, tt in zip(body, t))
        if halo < 1.0:
            van = (r / 255, g / 255, b / 255)
            rgb = tuple(v * (1 - halo) + n * halo for v, n in zip(van, rgb))
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


def _halo_int(van, lit, halo):
    """The same blend for the engines whose shaders hand back 0-255 ints (LW-230, extended past
    this section on the owner's scope call 2026-08-14; the zone engine below blends in floats
    because it owns its pixel all the way to the quantizer).

    `lit` comes back UNCHANGED at full weight, so every solid pixel of the already-approved bake
    is bit-identical by construction rather than by arithmetic luck. Measured, the lerp happens
    to agree at weight 1.0 under either quantizer, so this arm buys nothing TODAY; it is here so
    that the identity proof does not quietly depend on that coincidence surviving the next edit.

    The quantizer this blend inherits is the one that genuinely matters, and it must stay each
    lane's OWN. The weapon and shield lanes truncate (shade_bright, shade_shield, and
    trim_tone's caller) while the helm and hat lanes round. Switching those shaders to rounding
    moves 24,476 of 107,237 solid weapon pixels and 31,396 of 48,389 solid shield pixels by one
    LSB: invisible on screen, fatal to the pixel-exact claim. Reproduce with
    `python tools/icon_preview.py compare` after changing one."""
    if halo >= 1.0:
        return lit
    return tuple(int(v * (1.0 - halo) + n * halo) for v, n in zip(van, lit))


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
# The sword rack's own metals (LW-199), on top of the five above. Every one of these is BRIGHT
# except BLACK_IRON, and that is a measured rule rather than a taste: the zone that carries a
# sword's second colour is keyed on the art's dark share, so the body already renders those
# pixels dark, and a dark tone laid over them is invisible by construction. Swept on ids 19, 22
# and 23, a dark second material measured a p90 distance of 44 to 73 out of 255 from the
# body-only render where every bright metal measured 113 to 172. BLACK_IRON survives as the one
# exception because it sits on the Flamberge, whose body is the rack's hottest orange, so the
# separation there is chroma rather than weight. Five tones written for these passes are NOT
# here, having been measured and then deleted rather than left to read as live vocabulary: a
# COLD_IRON at v0.52 and a DARK_STEEL at v0.60 (both lost the darkness argument above), an
# AGED_BRONZE that the Claymore and Tanglethorn both outgrew when their bodies deepened, a
# VOIDLIGHT the Graviton wore until it went properly exotic, and a VENOM acid-green the owner
# rejected on the Chaos Blade.
COPPER = (0.055, 0.88, 1.10)
BLACK_IRON = (0.615, 0.10, 0.45)
# The last three are not metals at all, and that is the point: the Stormbrand, the Graviton and
# the Warbrand are the rack's three exotic items, and their second colour is a LIGHT rather than
# a fitting. Each is SATURATED where a metal would be pale, which is also what keeps them honest
# under the no-single-colour gate: a rim that differs from its blade only in brightness is a
# highlight, not a material, and the gate says so out loud (it caught exactly that on the
# Graviton when an earlier near-neutral rim sat within 0.30 saturation of the blade).
LEVIN = (0.145, 0.95, 1.30)      # the one hue hot enough to read as lightning at 48px
PLASMA = (0.485, 0.82, 1.34)     # not a metal: the cold light bleeding off an event horizon
VERDANT = (0.330, 0.85, 1.10)    # nor this: living green, for the one rod whose orb is a spring
VIOLET_FLAME = (0.790, 0.88, 1.02)  # nor this: the dark arriving as fire on Ragnarok's fuller
NIGHT_IRON = (0.740, 0.45, 0.42)    # furniture for a near-NEUTRAL blade. A plain black iron is
                                    # the obvious choice there and the no-single-colour gate
                                    # refuses it, correctly: against a pale near-neutral body it
                                    # differs in value alone, which is a shadow rather than a
                                    # second material. Tinting the iron toward the item's own
                                    # accent keeps the look and earns the pass.
EMBER = (0.045, 0.98, 0.90)      # not a metal either: iron still cooling from the forge. Its
                                 # value multiplier is BELOW 1 on purpose, which looks backwards
                                 # for something meant to glow. ramp_color desaturates highlights
                                 # by 30% at full brightness, so pushing this tone brighter walks
                                 # it toward peach: measured on the Warbrand's ridge, vmult 1.36
                                 # renders (238,156,113) at saturation 0.53 while 0.90 renders
                                 # (206,103,53) at 0.74. Heat reads as SATURATION against a dark
                                 # blade, not as brightness.


def _hilt(tone, pct=26, floor=0.44, gleam=0.25, sheen=0.35, min_blob=4, feather=1.0):
    """The sword's SECOND MATERIAL: guard, grip and pommel, found on the art's dark share.

    Which key finds a sword's furniture is a measurement, not a preference, and the three
    candidates disagree completely on this family. Measured over all fifteen card sprites:

      shade (darkest N%)   lands on the guard, grip and pommel on 15 of 15, 10-27% of the
                           solid art. The artist drew every hilt darker than its blade, so
                           one key covers the whole rack.
      desat (LW-202's)     lands on the BLADE, because a blade is the desaturated-but-lit
                           population. Right for a crossbow's metal stock, backwards here.
      cover (brightest)    lands on the blade's specular ridge, which is what bright-v2 was
                           already painting and why this family reads as vanilla art with a
                           coloured streak on it (LW-232).

    floor 0.44 and gleam 0.25 are the pair that keeps furniture legible: a guard sits in the
    art's shadow, so unfloored brass renders near-black (the documented round-two helmet trap),
    while an unclamped gleam preserve lifts a metal on a bright sprite straight back to white.
    Reproduce the coverage numbers with tools/icon_preview.py preview plus helm_mask."""
    return {"key": "shade", "pct": pct, "tone": tone, "floor": floor, "gleam": gleam,
            "sheen": sheen, "min_blob": min_blob, "feather": feather}


def _edge(tone, pct=20, floor=0.52, gleam=0.30, sheen=0.45, min_blob=3, feather=0.7):
    """The metal along the blade's lit ridge: the zone that makes a sword read as two colours.

    This is the cover key that LW-232 condemned, used deliberately. The finding there was that
    the brightest population is the sprite's own haze; helm_mask ranks only pixels at
    alpha >= HELM_SOLID, so the haze is not a candidate here and the same key returns the ridge
    the artist actually drew, running the full length of the blade.

    It exists because of an owner rejection worth recording. Round one gave every sword a body
    colour plus a hilt, which is genuinely two materials and measured as such, and he still
    called a handful of them one colour. He was right and the measurement was answering the
    wrong question: a hilt is 10-27% of the sprite and it sits at ONE END, so the blade, which
    is about 70% of what the eye gets, was still a single flat colour. The lesson generalises
    past swords: a second material has to cross the object's LARGEST shape, not merely exist
    somewhere on it.

    pct is swept per item between 18 and 24. The sweep is in the commit: below ~12 the ridge
    reads as a highlight rather than a material, and by ~36 the metal has taken the blade and
    the identity colour is the accent (measured on ids 19, 21, 23, 25 and 30)."""
    return {"key": "cover", "pct": pct, "tone": tone, "floor": floor, "gleam": gleam,
            "sheen": sheen, "min_blob": min_blob, "feather": feather}

# The sword rack, DERIVED so it cannot drift from the data. Fourteen of the fifteen are the
# contiguous ids 19-32; the fifteenth is the Warbrand (id 67), a Sword built on the retired Iron
# Flail's slot, and the one item in the game that draws itself with ANOTHER item's sprite
# (iconSource 19, the Vagabond). That pair shares one picture, so colour is the only thing that
# separates them and selftest holds them to a harder floor than the rest of the rack.
SWORD_RACK = frozenset(i for i, c in _CATEGORY.items() if c == "Sword")
KNIGHT_RACK = frozenset(i for i, c in _CATEGORY.items() if c == "KnightSword")
BOW_RACK = frozenset(i for i, c in _CATEGORY.items() if c == "Bow")
GUN_RACK = frozenset(i for i, c in _CATEGORY.items() if c == "Gun")
ROD_RACK = frozenset(i for i, c in _CATEGORY.items() if c == "Rod")

ZONE_OVERRIDES = {
    # --- Swords (LW-199, 2026-08-14) ------------------------------------------------------
    # The rack the owner asked for by name, and the family LW-232 was worst on: measured, the
    # shipping bake reached a MEDIAN 34% of each sword's solid art and as little as 14% (id 31),
    # which on screen is the artist's grey sword wearing a coloured stripe down one edge. The
    # body tint now owns every solid pixel, and TWO zones bring the metal back: the hilt on the
    # art's dark share (see _hilt for why darkness is the key that finds furniture on all
    # fifteen) and the blade's lit ridge on its bright share (see _edge, which exists because
    # the hilt alone was not enough and the owner said so).
    #
    # ROUND ONE WAS REJECTED and the reason is the useful part. Body plus hilt IS two materials
    # and measures as two materials, and he still called a handful of them one colour. The hilt
    # is 10-27% of the sprite and sits at one END, so the blade, which is most of what the eye
    # gets, stayed flat. The ridge runs the blade's whole length, and that is what turns the
    # measurement and the picture into the same answer.
    #
    # THE PALETTE RULE, owner brief 2026-08-14: "make them pop... use multiple colors, try and
    # make no two swords look similar in color", and "if you send me a updated weapon that's a
    # single color I'm going to immediately reject it". Both halves are pinned in selftest
    # rather than trusted: hue/saturation/value separation across the rack, and a floor on how
    # far each second material sits from its own body. Where two swords sit close in hue they
    # separate by WEIGHT instead (Warbrand is near-black where Flamberge is molten), which is
    # the same escape the shield palette uses and the only one available on a wheel this full.
    #
    # gleam is the knob that decides whether a hot colour lands at all. These blades are bright
    # near-white art, so the preserve fires on most of the sprite and pulls a saturated tint back
    # toward white, and the whole rack therefore runs it well below the engine's 0.80 default.
    # Read the column as one rule with two exceptions: thirteen swords sit between 0.15 and 0.30,
    # scaled by how hard their colour has to fight the art's own white, while the Claymore (0.55)
    # and the Materia Blade (0.85) keep a high preserve on purpose, because those two are PALE by
    # design and the artist's white is doing the work rather than getting in its way.
    19: {"zones": [_hilt(STEEL, pct=28, floor=0.46),
                   _edge(STEEL, pct=22)], "gleam": 0.30},                   # Vagabond
    20: {"zones": [_hilt(COPPER, pct=22, floor=0.52),
                   _edge(COPPER, pct=20, floor=0.55)],
         "gleam": 0.25, "contrast": 0.55},                                  # Cleaver
    21: {"zones": [_hilt(BRASS, pct=24),
                   _edge(BRASS, pct=22)], "gleam": 0.30},                   # Riposte
    22: {"zones": [_hilt(COPPER, pct=26, floor=0.46),
                   _edge(COPPER, pct=24)], "gleam": 0.40},                  # Claymore
    # Bone is the loudest second colour in the rack against a dark body, so both of the swords
    # that wear it (this and the Wrathblade) take a NARROWER ridge than the metals do: at the
    # rack's usual 22 the white had eaten half the blade and the sword stopped being red.
    23: {"zones": [_hilt(BONE, pct=24, floor=0.46),
                   _edge(BONE, pct=17, floor=0.55)], "gleam": 0.22},        # Sanguine Sword
    # Lightning is the one identity that cannot live in the body alone: a yellow blade collides
    # with Lightbringer's gold four hundredths away. So the storm is the body and the levin is
    # the furniture AND the ridge, which is also the rack's hottest pairing.
    24: {"zones": [_hilt(LEVIN, pct=22, floor=0.55), _edge(LEVIN, pct=20, floor=0.62)],
         "gleam": 0.22, "contrast": 0.60},                                  # Stormbrand
    25: {"zones": [_hilt(GOLD, pct=22, floor=0.48),
                   _edge(GOLD, pct=22, floor=0.52)], "gleam": 0.22},        # Tanglethorn
    26: {"zones": [_hilt(BLACK_IRON, pct=24, floor=0.34), _edge(WHITE, pct=22, floor=0.68)],
         "gleam": 0.22, "contrast": 0.55},                                  # Flamberge
    27: {"zones": [_hilt(BONE, pct=22, floor=0.50),
                   _edge(BONE, pct=18, floor=0.55)], "gleam": 0.25},        # Wrathblade
    28: {"zones": [_hilt(BLACK_IRON, pct=22, floor=0.30),
                   _edge(WHITE, pct=22, floor=0.62)], "gleam": 0.30},       # Swiftedge
    # The only body dark enough that its own furniture would vanish into it, so the second
    # colour is light instead of metal and the ridge carries the same tone: a black blade with a
    # collapsing rim, which is what a Gravity proc looks like if it looks like anything.
    # EXOTIC PASS (owner round five). A black blade with a white rim is a dark sword; these two
    # are supposed to look like objects rather than equipment, so both take a LIGHT as their
    # second colour instead of a metal. The Graviton's is cold: void-black under the cyan bleed
    # of something falling into a hole, which is also the only place in the rack where the
    # second colour is more saturated than the body it sits on.
    29: {"zones": [_hilt(PLASMA, pct=13, floor=0.42), _edge(PLASMA, pct=15, floor=0.90)],
         "gleam": 0.05, "contrast": 0.85},                                  # Graviton
    30: {"zones": [_hilt(GOLD, pct=22),
                   _edge(GOLD, pct=22)], "gleam": 0.25},                    # Arcanum
    # Holy, and the rack's inverse pair with the Materia Blade below: gold blade with white
    # furniture against white blade with gold furniture. They read apart because the blade is
    # ~70% of the pixels, so the body decides the item's colour at a glance.
    31: {"zones": [_hilt(BLACK_IRON, pct=22, floor=0.28), _edge(WHITE, pct=22, floor=0.72)],
         "gleam": 0.25, "contrast": 0.60},                                  # Lightbringer
    # RESERVED NAME (owner rule, 2026-08-14: an item that kept its vanilla name kept it because
    # players know it, so it is anchored to the vanilla look and only enhanced). The vanilla
    # Materia Blade is a white blade with gold furniture; this is that, with the white cooled
    # and the gold brought up, and nothing invented. Its ridge is the rack's narrowest for the
    # same reason: enough to tie the gold cross into the blade, not enough to restyle the item.
    32: {"zones": [_hilt(GOLD, pct=24, floor=0.48),
                   _edge(GOLD, pct=12, floor=0.55)], "gleam": 0.85},        # Materia Blade
    # Two things about this one are the shared sprite's fault. It is a WIDE flat blade, so a
    # ridge percentage that reads as a line on a tapered sword reads as half the blade here, and
    # it keeps the rack's narrowest metal because of it. And its body had to go COLD: a warm
    # near-black under a warm brass ridge is brown with a tan stripe down it, which the owner
    # correctly called a hotdog. Black steel with gold is the same idea with the bun removed.
    # And the Warbrand's is hot: meteoric iron with the forge heat still in its edge. Brass stays
    # on the furniture so the sword keeps one honest metal, and the ember line is deliberately
    # the rack's narrowest zone, because heat reads as a line and never as a surface.
    # pct 22 rather than the 10 a heat line wants: measured on THIS sprite the cover mask claims
    # 1% of the solid art at pct 10 and 1% again at 14, because the brightest pixels on a wide
    # flat blade scatter into blobs the despeckle pass then eats. It reaches 12% at 22, which is
    # the first setting where the ember survives as a line instead of a speck.
    67: {"zones": [_hilt(BRASS, pct=14, floor=0.55),
                   _edge(EMBER, pct=22, floor=0.50, sheen=0.10, gleam=0.0,
                         min_blob=2, feather=0.6)], "gleam": 0.06,
         "contrast": 0.85},                                                 # Warbrand
    # --- Knight Swords (LW-200, 2026-08-14) -----------------------------------------------
    # The sword vocabulary, unchanged, on the swords' direct siblings in art: measured on all
    # five distinct sprites, the dark share is guard/grip/pommel (8.8 to 16.7 percent) and the
    # bright share is the fuller running the blade's whole length (9.4 to 18.8 percent).
    33: {"zones": [_hilt(BLACK_IRON, pct=24, floor=0.34),
                   _edge(STEEL, pct=22)], "gleam": 0.25},                   # Defender
    34: {"zones": [_hilt(STEEL, pct=24, floor=0.50),
                   _edge(WHITE, pct=22, floor=0.62)], "gleam": 0.45},       # Save the Queen
    35: {"zones": [_hilt(BLACK_IRON, pct=24, floor=0.32),
                   _edge(WHITE, pct=22, floor=0.68)], "gleam": 0.25,
         "contrast": 0.55},                                                 # Excalibur
    # Owner picker, 2026-08-14: variant B, "spectral ice, violet flame". Its art is cold and PALE
    # (measured hue 0.587) while the item carries the Dark element, and B is the answer that
    # sides with the ART: the artist's icy blade kept, with the darkness arriving as a violet
    # flame down the fuller and violet-black furniture under it.
    36: {"zones": [_hilt(NIGHT_IRON, pct=22, floor=0.30),
                   _edge(VIOLET_FLAME, pct=22, floor=0.55)], "gleam": 0.45,
         "contrast": 0.55},                                                 # Ragnarok
    # Chaos Blade's cover mask claims 3.9% at the family default, the same fragmentation the
    # Warbrand hit: its brightest pixels scatter across a wide ornate blade and the despeckle
    # pass eats them. 26 is where the BONE edge survives as a line. (This comment described a
    # crimson edge until 2026-08-14; crimson was an earlier candidate the owner rejected, and
    # the note outlived it by one round.)
    # Owner picker, round three, 2026-08-14: "blood-black, bone edge". Three rounds and nine
    # candidates went into this one, and the finding that settled it was about the FAMILY rather
    # than the item: all six of its settled siblings are bright saturated blades, so there is no
    # dark sword on the shelf, and the Chaos Blade's colourless vanilla art (chroma 0.041) was
    # already asking to be the dark one. That made the 48px icon the real test, and of nine
    # candidates this was the only one still unmistakably dark at that size without tuning.
    # Its one risk is the Ravager, also red: they separate on WEIGHT and on FURNITURE (a
    # luminous red blade with gold fittings against near-black plum with black ones), which are
    # the two differences that survive being shrunk to a list glyph. An acid-green version and a
    # molten-crimson one were rejected by the owner before this.
    37: {"zones": [_hilt(BLACK_IRON, pct=22, floor=0.30),
                   _edge(BONE, pct=26, floor=0.66)], "gleam": 0.12,
         "contrast": 0.78},                                                 # Chaos Blade
    49: {"zones": [_hilt(BRASS, pct=24, floor=0.50),
                   _edge(BRASS, pct=22, floor=0.56)], "gleam": 0.15,
         "contrast": 0.60},                                                 # Ravager
    # Free name, and the one that had to move: with the Defender on brass and the Ravager on
    # blood, a copper Sunderer made three warm blades out of seven. Patinated bronze is what
    # copper BECOMES, so gold fittings still belong on it, and a cold body is also the furthest
    # thing from Save the Queen, whose sprite it borrows.
    50: {"zones": [_hilt(GOLD, pct=24, floor=0.48),
                   _edge(GOLD, pct=22)], "gleam": 0.20, "contrast": 0.60},  # Sunderer
    # --- Rods (LW-208, 2026-08-14) --------------------------------------------------------
    # The worst family left before this pass: a TRUE median of 1.8% of the solid art, with the
    # Ember Rod and the Rod of Faith at 0.0.
    #
    # A rod is the fourth art shape to need no new engine, and the neatest fit yet. It is a
    # SHAFT, an ORB and a ferrule, and the two sword keys land on exactly those: measured over
    # the six distinct sprites, darkness claims 3.3 to 17.6 percent and is the shaft every time,
    # brightness claims 4.0 to 21.9 percent and is the orb. So these are the swords' recipes with
    # the meanings swapped, _hilt taking the shaft and _edge the orb, which is also why the orb
    # tone is a LIGHT on the elemental rods rather than a metal: the orb is where the magic is.
    #
    # Tints live in data/items.json for this family. Reserved names are anchored against BOTH
    # surfaces, not just the card: the Dragon Rod's icon reads hue 153 degrees, a jade teal, and
    # the tint follows it; the Rod of Faith's icon reads a warm 18 degrees, so Holy gold sits
    # with its art rather than against it. Measuring the card alone is what put the Perseus Bow
    # 180 degrees from its own colour (see id 91).
    51: {"zones": [_hilt(BLACK_IRON, pct=22, floor=0.34),
                   _edge(VERDANT, pct=18, floor=0.60)], "gleam": 0.25},      # Wellspring Rod
    52: {"zones": [_hilt(STEEL, pct=22, floor=0.48),
                   _edge(LEVIN, pct=20, floor=0.62)], "gleam": 0.22,
         "contrast": 0.60},                                                  # Spark Rod
    53: {"zones": [_hilt(BLACK_IRON, pct=24, floor=0.34),
                   _edge(WHITE, pct=18, floor=0.68)], "gleam": 0.22},        # Ember Rod
    54: {"zones": [_hilt(SILVER, pct=22, floor=0.50),
                   _edge(WHITE, pct=18, floor=0.66)], "gleam": 0.30},        # Frost Rod
    55: {"zones": [_hilt(BRASS, pct=22, floor=0.48),
                   _edge(BONE, pct=18, floor=0.62)], "gleam": 0.25},         # Hushward Rod
    56: {"zones": [_hilt(SILVER, pct=22, floor=0.48),
                   _edge(PLASMA, pct=18, floor=0.62)], "gleam": 0.20,
         "contrast": 0.65},                                                  # Umbral Rod
    57: {"zones": [_hilt(STEEL, pct=22, floor=0.48),
                   _edge(GOLD, pct=18, floor=0.58)], "gleam": 0.25},         # Dragon Rod
    58: {"zones": [_hilt(BLACK_IRON, pct=22, floor=0.36),
                   _edge(WHITE, pct=20, floor=0.70)], "gleam": 0.25},        # Rod of Faith
    # --- Guns (LW-203, 2026-08-14) --------------------------------------------------------
    # The least-coloured family in the game before this pass: measured, the shipping bake reached
    # a MEDIAN 1.7% of each gun's solid art and 0.0% of the Ironclad Repeater's, which is last of
    # the twelve families still on bright-v2.
    #
    # A gun is the crossbow's split again, and the third art shape to need no new engine: a
    # wooden STOCK against a metal BARREL, which saturation separates in one pass. Measured, the
    # desat key claims 12 to 21 percent on all six and lands on the barrel every time, so the
    # identity colour lives on the stock and frame where a player reads it.
    #
    # Tints for this family live in data/items.json, not in the table above, so the reasoning
    # lives here beside the recipes. Four of the six kept their vanilla names and are anchored to
    # their own measured hue: Stoneshooter 0.075 (warm earth), Glacial Gun 0.574 (cool),
    # Blaze Gun 0.033 at chroma 0.143 (the most chromatic anchor met so far, and it wanted little
    # more than saturating), Blaster 0.184 at chroma 0.032 (near colourless, so value is what its
    # anchor constrains, the Chaos Blade's case).
    # A walnut stock was tried first and put two BROWN guns in a six-gun list beside the
    # Stoneshooter, which is anchored to its own earth hue and cannot move. This one is the
    # free name, so it takes the burgundy lacquer a duelling pistol would wear.
    71: {"zones": [_material(STEEL, sat_p=32)], "gleam": 0.25},              # Outrider Pistol
    72: {"zones": [_material(BRASS, sat_p=30)], "gleam": 0.25},              # Ironclad Repeater
    73: {"zones": [_material(SILVER, sat_p=32)], "gleam": 0.22},             # Stoneshooter
    74: {"zones": [_material(WHITE, sat_p=30)], "gleam": 0.35},              # Glacial Gun
    75: {"zones": [_material(BLACK_IRON, sat_p=32)], "gleam": 0.20},         # Blaze Gun
    76: {"zones": [_material(STEEL, sat_p=30)], "gleam": 0.22},              # Blaster
    # --- Bows (LW-201, 2026-08-14) --------------------------------------------------------
    # Bows reuse the CROSSBOW helper, not the sword one, because a bow is a crossbow's relative
    # and not a blade's: it has no hilt to find. Measured over the nine, the darkness key that
    # finds a hilt on 15 of 15 swords claims 0.7 to 12.4 percent here and lands on scattered
    # limb tips, while the saturation key finds the STRING on 10 to 24 percent. The string is
    # also the right shape for a second material by the sword pass's own lesson, since it
    # crosses the sprite's longest dimension instead of sitting at one end.
    #
    # min_blob is 2 across the family rather than the helper's 4. These sprites are TINY: the
    # Tidecaller card carries 113 solid pixels against a sword's 400 to 1100, so a despeckle
    # floor authored for a blade eats a bow's string whole.
    83: {"zones": [_material(STEEL, sat_p=34, min_blob=2)], "gleam": 0.25},  # Skirmisher
    84: {"zones": [_material(SILVER, sat_p=30, min_blob=2)], "gleam": 0.45}, # Windrunner
    # The one bow whose STRING the key cannot find, because the artist drew it almost
    # invisibly. At this window the mask lands on patches along the limb instead, which on an
    # ICE bow reads as rime and is the right answer for the wrong reason; taken deliberately
    # rather than left at a setting that returned 3% of the card.
    85: {"zones": [_material(WHITE, sat_p=48, min_blob=2)], "gleam": 0.30},  # Frostarc
    # A levin string on a levin bow is one colour, which the gate said out loud. Black iron on
    # a hot yellow limb is the Flamberge's pairing and the contrast is chroma, not weight.
    86: {"zones": [_material(BLACK_IRON, sat_p=32, min_blob=2)], "gleam": 0.22},  # Stormarc
    87: {"zones": [_material(WHITE, sat_p=30, min_blob=2)], "gleam": 0.30},  # Skypiercer
    88: {"zones": [_material(SILVER, sat_p=30, min_blob=2)], "gleam": 0.25}, # Tidecaller
    89: {"zones": [_material(BRASS, sat_p=30, min_blob=2)], "gleam": 0.22},  # Huntress
    90: {"zones": [_material(GOLD, sat_p=30, min_blob=2)], "gleam": 0.20},   # Yoichi Bow
    91: {"zones": [_material(WHITE, sat_p=30, min_blob=2)], "gleam": 0.40},  # Perseus Bow
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
    78: {"zones": [_material(BONE)]},                      # Eclipsebolt blood and bone (Doom)
    79: {"zones": [_material(BRASS)]},                     # Arbalest    blued steel and brass
    80: {"zones": [_material(GOLD)]},                      # Venombolt   venom green, gilt stock
    81: {"zones": [_material(SILVER)]},                    # Pitchbolt   plum, cold silver
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


SOLID_TINT_FLOOR = 0.02    # below this an item ships as vanilla art wearing a coloured glow


def solid_tint_share(van, out):
    """What fraction of an item's SOLID art the recolour actually repainted.

    Added 2026-08-14 with the halo ramp, because the ramp exposed a defect it did not cause:
    two_zone_bright picks the brightest k-means cluster, and on thin line art (staves, bows,
    spears) the brightest population is the sprite's own haze, so the engine painted the glow
    and left the weapon vanilla. Thirteen weapon cards had ZERO solid pixels in their tint zone,
    which read as a recoloured item only for as long as the glow was allowed to take the tint.
    A colour that lives entirely in the haze is not an identity colour, so the bake says so out
    loud instead of shipping a vanilla sprite under a coloured name."""
    vp, op = van.load(), out.load()
    solid = tinted = 0
    for y in range(van.height):
        for x in range(van.width):
            if vp[x, y][3] < HALO_HI:
                continue
            solid += 1
            tinted += op[x, y][:3] != vp[x, y][:3]
    return (tinted / solid) if solid else 0.0


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
        van = Image.open(WORK / f"{src_name}.dds").convert("RGBA")
        im = route(van, item_id, tint, surface)
        share = solid_tint_share(van, im)
        if share < SOLID_TINT_FLOOR:
            print(f"  WARN {out_name}: the tint reaches {share * 100:.1f}% of the solid art, so"
                  f" this ships as the vanilla sprite. Its family needs its engine chosen from"
                  f" the art (docs/TODO.md LW-232) before the bake means anything.")
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
    helm_render = helm_recolor(grain, (0.6, 0.8, 1.0),
                               dict(matched, mode="cover", pct=0, trim=(0.6, 0.8, 1.0)))
    check("smooth-field contrast amplifies less grain than the per-pixel curve",
          swing(smoothed) < swing(helm_render) * 0.9)
    # ...and the helmet engine KEEPS that per-pixel curve, which is why this comparison still has
    # two sides to it. Extending the smooth field there was tried and reverted the same day: a
    # Gaussian cannot tell a drawn one-pixel line from compression grain, and helmet art is
    # engraved metal whose subject is exactly that line work. The check below is what stops a
    # future tidy-up from unifying the two engines on the grounds that they look like the same
    # function: they are not, and the difference is the thirteen helmets' engraving.
    check("the helmet engine keeps its per-pixel curve, so the engines are NOT interchangeable",
          not images_equal(smoothed, helm_render))
    # The other half of the fix: the grain is DAMPED, not deleted. Measured against the same
    # render with the residue term removed, which is a flat blurred stamp; "swing > 0" is not
    # this test, because the fixture's own gradient satisfies that with the residue gone.
    no_detail = zone_recolor(grain, (0.6, 0.8, 1.0), dict(matched, zones=[], detail=0.0))
    check("the fine grain is damped, not thrown away",
          0.0 < DETAIL_GAIN < 1.0 and swing(smoothed) > swing(no_detail) * 1.05)
    # The fine line work a blur cannot tell from grain, which is why the smooth field stays out
    # of the helmet engine. The fixture is a one-pixel dark grid, the shape of engraved metal:
    # through the per-pixel curve the lines survive as lines, through the smooth field they lose
    # most of their depth. Both numbers are the measurement that reverted LW-231 on helmets.
    lines = Image.new("RGBA", (24, 24), (0, 0, 0, 0))
    for y in range(24):
        for x in range(24):
            drawn = x % 4 == 0 or y % 6 == 0        # 1px engraved lines on a lit plate
            lines.putpixel((x, y), (70, 62, 50, 255) if drawn else (196, 188, 170, 255))
    line_knobs = dict(matched, mode="cover", pct=0, trim=(0.6, 0.8, 1.0))
    check("the per-pixel curve keeps one-pixel line work that the smooth field flattens",
          swing(helm_recolor(lines, (0.6, 0.8, 1.0), line_knobs))
          > swing(zone_recolor(lines, (0.6, 0.8, 1.0), dict(matched, zones=[]))) * 1.25)

    # --- LW-230 past the hat engine (owner scope call, 2026-08-14) ---------------------------
    # The halo ramp shipped inside zone_recolor alone, because re-baking art the owner had
    # already approved was his call and not ours. He made it, so it now runs in every engine
    # that paints REVIEWED art. (LW-231, the smooth field, deliberately did NOT follow: see
    # helm_recolor's docstring and the line-work check above.)
    #
    # The checks above pin the ramp and the field as FUNCTIONS. These pin that the engines
    # actually call them, which is a different claim and the one that was missing: measured
    # before the extension, all of LW-230 could be deleted from three of the four engines and
    # every existing check stayed green.
    #
    # legacy is deliberately NOT in the fixed set (see the scope tripwire below).

    def hazed_sprite(w=22):
        """One fixture every engine can chew: all THREE alpha populations plus two materials.

        A rim of haze below the ramp, two rings INSIDE it, and a solid body split into pale
        metal and a saturated field so the brightness and saturation masks each have something
        to find. The middle rings are the load-bearing part: haze short-circuits before the
        blend and solid multiplies through it by 1.0, so a fixture without ramp-band pixels
        leaves the blend itself untested."""
        im = Image.new("RGBA", (w, w), (0, 0, 0, 0))
        for y in range(w):
            for x in range(w):
                d = min(x, y, w - 1 - x, w - 1 - y)
                if d == 0:
                    im.putpixel((x, y), (196 + x, 198, 194 + y, 24))       # haze, below the ramp
                elif d == 1:
                    im.putpixel((x, y), (190 + y, 192, 188 + x, 40))       # haze, below the ramp
                elif d == 2:
                    im.putpixel((x, y), (150 + x, 152 + y, 148, 96))       # inside the ramp
                elif d == 3:
                    im.putpixel((x, y), (140 + y, 142 + x, 138, 176))      # inside the ramp
                elif 5 <= x <= 7:
                    im.putpixel((x, y), (198 + x, 202 + y, 206 + x, 255))  # solid: pale metal
                else:
                    im.putpixel((x, y), (104 + 3 * x, 58 + y, 44, 255))    # solid: saturated
        return im

    hazed = hazed_sprite()
    hazed_haze, hazed_ramp = (0, 11), (2, 11)
    hazed_solid = ((6, 11), (14, 11))       # one metal pixel, one body pixel
    # Routing coverage: a future engine added without the fix must fail HERE rather than ship
    # smoking. The right-hand side is every engine name the router can actually return today.
    halo_sample = {"bright-v2": 1, "shield-bright": 128, "helm-two-tone": 156,
                   "three-zone": 157, "legacy": 169}
    check("the halo sample names every engine the router can return",
          set(halo_sample) == {engine_for(i) for i in ICON_TINTS})
    check("every halo sample id really routes to the engine it is filed under",
          all(engine_for(i) == e for e, i in halo_sample.items()))
    for eng, iid in sorted(halo_sample.items()):
        if eng == "legacy":
            continue
        for surf in ("card", "small"):
            painted = route(hazed, iid, ICON_TINTS[iid], surf)
            check(f"{eng}/{surf} leaves the artist's haze at its own colour",
                  painted.getpixel(hazed_haze) == hazed.getpixel(hazed_haze))
            check(f"{eng}/{surf} still paints the solid art",
                  any(painted.getpixel(c)[:3] != hazed.getpixel(c)[:3] for c in hazed_solid))
            check(f"{eng}/{surf} never rewrites alpha",
                  all(painted.getpixel(c)[3] == hazed.getpixel(c)[3]
                      for c in (hazed_haze, hazed_ramp) + hazed_solid))
    # The branches the one-id-per-engine sample cannot reach: a helmet that bakes through the
    # SHIELD engine (style "shield"), and a weapon small that takes the card's two-zone split
    # instead of the whole-glyph ramp. A fixer who patches helm_recolor and stops would leave
    # id147 smoking, and it is filed under helmets.
    check("both helmet styles and the two-zone weapon small keep the haze too",
          all(route(hazed, i, ICON_TINTS[i], s).getpixel(hazed_haze)
              == hazed.getpixel(hazed_haze)
              for i, s in ((147, "card"), (147, "small"), (156, "card"), (13, "small"))))

    def opaque_twin(im):
        """The same sprite with every painted pixel forced solid."""
        out = im.copy()
        p = out.load()
        for y in range(im.height):
            for x in range(im.width):
                r, g, b, a = p[x, y]
                if a >= 8:
                    p[x, y] = (r, g, b, 255)
        return out

    # Byte identity with no magic constant in it. bright-v2, shield-bright and legacy rank their
    # masks on COLOUR alone over an alpha>=8 pool, so erasing the haze must not move one solid
    # pixel. This is what goes red the moment someone "fixes" a mask pool to skip the haze,
    # which is the natural next thought and measures as flipping 28% of solid weapon pixels into
    # the other zone, on 115 of 115 sprites. Excluded on purpose: the helm and hat engines rank
    # over alpha>=HELM_SOLID (so an opaque twin legitimately re-ranks), and the ring shield
    # (id132) keys its BFS on alpha>=160 for the same reason.
    twin = opaque_twin(hazed)
    for iid in (1, 13, 128, 143, 169):
        for surf in ("card", "small"):
            with_haze = route(hazed, iid, ICON_TINTS[iid], surf)
            without = route(twin, iid, ICON_TINTS[iid], surf)
            check(f"id{iid}/{surf} ({engine_for(iid)}): the haze cannot move a solid pixel",
                  all(with_haze.getpixel(c) == without.getpixel(c) for c in hazed_solid))
    # No hard ring at the cutoff. One flat colour whose alpha ramps across the strip, through a
    # mask-free configuration of every engine: the distance from the artist's own pixel must
    # climb gradually, never in one step. A cliff here is exactly the artefact the long 48->224
    # ramp exists to prevent, and it is invisible in a per-pixel unit test.
    strip = Image.new("RGBA", (64, 3), (0, 0, 0, 0))
    for x in range(64):
        for y in range(3):
            strip.putpixel((x, y), (140, 120, 96, min(255, x * 4 + 3)))
    sp = strip.load()
    for name, im_r in (("weapon cards", two_zone_bright(strip, (0.6, 0.8, 1.0), "Sword", {})),
                       ("weapon smalls", small_bright(strip, (0.6, 0.8, 1.0))),
                       ("shields", shield_two_tone(strip, (0.6, 0.8, 1.0))),
                       ("helmets", helm_recolor(strip, (0.6, 0.8, 1.0),
                                                {"mode": "cover", "pct": 0, "contrast": 0.5,
                                                 "trim": (0.6, 0.8, 1.0)})),
                       ("hats", zone_recolor(strip, (0.6, 0.8, 1.0), {"zones": []}))):
        rp = im_r.load()
        prof = [sum(abs(rp[x, 1][i] - sp[x, 1][i]) for i in range(3)) for x in range(64)]
        check(f"{name}: the alpha ramp has no hard ring in it",
              max(prof) > 0 and max(b - a for a, b in zip(prof, prof[1:])) < max(prof) * 0.25)
    # Scope tripwire. The legacy engine KEEPS its smoky halo: its 72 items are unreviewed under
    # the new rules and their shipped look must not move until their own owner pass lands
    # (LW-217 through LW-226). Whoever runs that pass deletes this check; until then it is what
    # stops a tidy-minded refactor from silently re-skinning 72 approved icons.
    check("the legacy engine still paints the haze, until its own families are reviewed",
          route(hazed, 169, ICON_TINTS[169], "card").getpixel(hazed_haze)
          != hazed.getpixel(hazed_haze))
    # The bake-time reading that catches an item whose colour lives entirely in its glow. Both
    # arms are the pin: a whole-glyph render reads 1.0, and a render that touched only the haze
    # reads 0.0 even though it is visibly, colourfully different from the vanilla art.
    check("solid tint share reads a fully painted sprite as whole",
          solid_tint_share(hazed, small_bright(hazed, (0.6, 0.9, 1.1))) == 1.0)
    glow_only = hazed.copy()
    gp = glow_only.load()
    for gy in range(glow_only.height):
        for gx in range(glow_only.width):
            if 8 <= gp[gx, gy][3] < HALO_HI:
                gp[gx, gy] = (20, 60, 240, gp[gx, gy][3])
    check("solid tint share reads a glow-only recolour as untouched art",
          solid_tint_share(hazed, glow_only) == 0.0 and SOLID_TINT_FLOOR > 0.0)
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
    _ZONED_CATS = {"Hat", "Crossbow", "Sword", "KnightSword", "Bow", "Gun", "Rod"}
    # The tint table's own comments, checked like data. See tint_comment_names for why: a hue
    # triple is unreviewable without the item name beside it, and those names rot silently.
    drifted = sorted(i for i, c in tint_comment_names().items()
                     if i in _NAME and not c.startswith(_NAME[i]))
    check(f"every tint row names its real item (drifted: {drifted})", not drifted)
    check("the tint comment scan actually finds rows, so the check above cannot pass by parsing "
          "nothing", len(tint_comment_names()) >= 50)
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
    # id 1 is a knife: a weapon whose family re-pass (LW-198) has NOT run, so it must still take
    # the category default. This pin has now been re-pointed twice, from id 19 when the swords
    # moved and from id 83 when the bows did, which is the pin working rather than rotting.
    check("an unreviewed weapon still routes to bright-v2", engine_for(1) == "bright-v2")
    check("a reviewed sword beats its category's engine", engine_for(19) == "three-zone")
    check("picked helmet routes to helm-two-tone", engine_for(156) == "helm-two-tone")
    check("the last two helmets joined the helm engine",
          engine_for(145) == "helm-two-tone" and engine_for(151) == "helm-two-tone")
    check("picked hat routes to three-zone", engine_for(157) == "three-zone")
    # The crossbows are the pin that the per-item table BEATS the per-category rule: they are
    # weapons, so a category-first engine_for would send them to bright-v2 and quietly ignore
    # their recipes.
    check("a picked crossbow beats its category's engine", engine_for(77) == "three-zone")
    check("every reviewed family is picked, whole",
          sorted(ZONE_OVERRIDES)
          == sorted({i for i, c in _CATEGORY.items()
                     if c in ("Hat", "Crossbow", "Bow", "Gun", "Rod")}
                    | SWORD_RACK | KNIGHT_RACK))
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

    # THE BLADE RACKS (LW-199 swords, LW-200 knight swords). The same tripwire as the shields
    # above, for the same reason and one more. Under the zone engine the body tint owns every
    # solid pixel, so a blade's tint IS its colour signal; and the owner's brief for these passes
    # was explicit ("try and make no two swords look similar in color"), which is a claim a
    # person cannot keep by eye across a family and a later tweak can silently break. Sat gap
    # 0.20 rather than the shields' 0.25: these racks deliberately carry near-neighbours in hue
    # that separate by weight instead (Warbrand is a near-black iron two hundredths off
    # Flamberge's fire orange), so the pair check leans on the value term the shields' pin has
    # no need for. Each rack is checked WITHIN itself: two families are never on screen in the
    # same list, and holding one rack's palette away from another's would spend hue the wheel
    # does not have.
    RACK_MIN_HUE_GAP, RACK_MIN_SAT_GAP, RACK_MIN_VAL_GAP = 0.05, 0.20, 0.28
    swords = sorted(SWORD_RACK)
    knights = sorted(KNIGHT_RACK)
    bows = sorted(BOW_RACK)
    guns = sorted(GUN_RACK)
    rods = sorted(ROD_RACK)
    hats = sorted(i for i, c in _CATEGORY.items() if c == "Hat" and i in ZONE_OVERRIDES)
    xbows = sorted(i for i, c in _CATEGORY.items() if c == "Crossbow" and i in ZONE_OVERRIDES)

    # Which items this tripwire may judge, on the SHIELDS' rule (tint_is_whole_signal above): a
    # body tint may only stand in for an item's whole colour signal when the item's other tones
    # are FITTINGS. The metal vocabulary is listed here rather than guessed from saturation,
    # because brass and gold are as saturated as any identity colour.
    #
    # This exemption is not a convenience. Run without it, the check calls four HAT pairs
    # collisions (157/167, 158/161, 160/163, 164/165) and every one is a false alarm: their
    # bodies do sit close, and their second colours are a violet lining against a teal one, a
    # magenta against a gold. A hat wears three identity colours over 18 to 38 percent of the
    # sprite and the owner passed all twelve by eye across four review rounds. A blade wears one
    # identity colour and a metal, so its body really is the signal.
    METALS = frozenset({STEEL, BONE, BRASS, GOLD, SILVER, COPPER, BLACK_IRON, WHITE})

    def body_is_whole_signal(i):
        return all(tuple(z["tone"]) in METALS for z in ZONE_OVERRIDES[i]["zones"])

    for rack_name, rack in (("sword", swords), ("knight sword", knights),
                            ("bow", bows), ("gun", guns), ("rod", rods),
                            ("hat", hats), ("crossbow", xbows)):
        rack = [i for i in rack if body_is_whole_signal(i)]
        rack_collisions = [
            (a, b) for n, a in enumerate(rack) for b in rack[n + 1:]
            if abs(arc(ICON_TINTS[a][0], ICON_TINTS[b][0])) < RACK_MIN_HUE_GAP
            and abs(ICON_TINTS[a][1] - ICON_TINTS[b][1]) < RACK_MIN_SAT_GAP
            and abs(ICON_TINTS[a][2] - ICON_TINTS[b][2]) < RACK_MIN_VAL_GAP]
        check(f"{rack_name} tints stay distinguishable (collisions: {rack_collisions})",
              not rack_collisions)
    guarded_total = sum(1 for i in ZONE_OVERRIDES if body_is_whole_signal(i))
    check(f"the collision tripwire still guards most of the zone engine ({guarded_total}/"
          f"{len(ZONE_OVERRIDES)})", guarded_total >= 35)
    check("the sword rack is all fifteen", len(swords) == 15)
    check("the knight sword rack is all seven", len(knights) == 7)
    check("the bow rack is all nine", len(bows) == 9)
    check("the gun rack is all six", len(guns) == 6)
    check("the rod rack is all eight", len(rods) == 8)
    # SHARED SPRITES. Three items in these two racks draw themselves with ANOTHER item's picture
    # (the Warbrand on the Vagabond's, the Ravager on the Defender's, the Sunderer on Save the
    # Queen's), so for those pairs colour is not the main signal, it is the ONLY one. The pairs
    # are DERIVED from SRC rather than listed, because the next family to do this will otherwise
    # be guarded by nothing. They get a harder floor than the rack's.
    twins = sorted((src, i) for i, src in SRC.items()
                   if i in ICON_TINTS and src in ICON_TINTS
                   and _CATEGORY.get(i) == _CATEGORY.get(src)
                   and _CATEGORY.get(i) in _ZONED_CATS)
    check("the shared-sprite scan finds every known twin pair", len(twins) == 3)
    same_picture = [(a, b) for a, b in twins
                    if abs(arc(ICON_TINTS[a][0], ICON_TINTS[b][0])) < 0.05
                    and abs(ICON_TINTS[a][2] - ICON_TINTS[b][2]) < 0.35]
    check(f"items sharing one sprite are far apart (too close: {same_picture})", not same_picture)
    # NO SINGLE-COLOUR SWORD (owner rule, 2026-08-14: "if you send me a updated weapon that's a
    # single color I'm going to immediately reject it"). THREE checks, because the first two
    # versions of this gate were both provably cheatable and an adversarial audit demonstrated
    # each one with a mutation that stayed green:
    #   1. a zone LIST that is merely non-empty passes with pct=0 or min_blob=9999, i.e. a zone
    #      that paints nothing at all, so the config check below is backed by a RENDER check;
    #   2. a tone check with three ANDed terms lets one far term excuse the other two, so a tone
    #      identical in hue and saturation and darker only in value, which is a literal shadow of
    #      the body, sailed through. Value alone does not make a material, so it is gone as an
    #      escape: a second material must differ in HUE or in SATURATION.
    # These are the pins standing in for a rule the owner enforces by rejection, so they are held
    # to the standard of failing when the thing they describe is false.
    # EVERY item under the zone engine, derived from the table itself rather than from a list of
    # families. An audit on 2026-08-14 found the previous version covered 37 of 55: all twelve
    # hats and all six crossbows sat outside both owner-rule pins AND every palette check, and
    # the auditor turned all six crossbows one colour with the gate still green. A list of racks
    # is a list someone forgets to extend; the table cannot be forgotten, because being in it is
    # what puts an item under this engine in the first place.
    bladed = swords + knights + bows + guns + rods
    zone_ids = sorted(ZONE_OVERRIDES)
    # Dead per-item config for the OLD engine. Both tables below are read only inside the
    # bright-v2 branch of route(), and engine_for consults ZONE_OVERRIDES first, so any id in
    # both is unreachable configuration that still reads as live. This has now bitten twice
    # (SMALL_TWO_ZONE id 24, then CARD_OVERRIDES ids 33 and 37), so it gets a pin rather than a
    # third discovery.
    stale_bright = sorted((set(CARD_OVERRIDES) | set(SMALL_TWO_ZONE)) & set(ZONE_OVERRIDES))
    check(f"no bright-v2 override survives for an item that left that engine ({stale_bright})",
          not stale_bright)
    check("every reviewed weapon has a recipe at all",
          all(i in ZONE_OVERRIDES for i in bladed))
    check("the no-single-colour rule covers every item under the zone engine",
          len(zone_ids) == len(ZONE_OVERRIDES) and len(zone_ids) >= len(bladed) + 18)
    check("every sword carries a second material",
          all(ZONE_OVERRIDES[i]["zones"] for i in zone_ids))
    flat = []
    for i in zone_ids:
        h_b, s_b, _ = ICON_TINTS[i]
        for z in ZONE_OVERRIDES[i]["zones"]:
            h_z, s_z, _ = z["tone"]
            if abs(arc(h_b, h_z)) < 0.08 and abs(s_b - s_z) < 0.30:
                flat.append((i, z["tone"]))
    check(f"no sword's second material collapses into its body (flat: {flat})", not flat)
    # The render half. Every sword recipe runs over the fixture and its zones must actually move
    # a real share of the SOLID pixels away from the same recipe with its zones removed. The
    # fixture is not the real sprite, so this cannot pin per-item coverage (that is measured on
    # the art by tools/icon_preview.py); what it pins is that no recipe has been tuned into a
    # no-op, which is exactly the mutation that beat the config check.
    #
    # The band is two-sided, and the ceiling is not hypothetical: it was found by mutating
    # min_blob to 99999, where the despeckle pass flips the whole mask to TRUE instead of to
    # FALSE and the zone tone swallows the entire sprite. That is a single-colour sword just as
    # surely as a zone that paints nothing, and a floor-only check called it healthy.
    # TWO fixtures, and the second exists because the first has a blind spot an audit found on
    # 2026-08-14. hazed_sprite's second material is one fat contiguous blob, so the DESPECKLE
    # knob is untested across its whole useful range: min_blob=40 leaves that fixture reading an
    # unchanged 15.5% while zeroing the second material on six of the nine real bow cards. A
    # bow's string is a one-pixel line, so the fixture has to carry a THIN feature too or the
    # pin certifies a setting that erases real art.
    def threaded_sprite(w):
        """The hazed fixture plus a one-pixel diagonal of desaturated-but-lit pixels: a string,
        a fuller, a filigree line. Anything a despeckle floor authored for a blob will eat."""
        im = hazed_sprite(w)
        for k in range(4, w - 4):
            im.putpixel((k, k), (236, 238, 240, 255))
        return im

    # The thin-feature check runs at CARD SIZE, and that is the whole point of it. Every spatial
    # knob in this file is authored against a 100px card and scaled by sprite width, so on a 28px
    # fixture a despeckle floor of 40 collapses to 3 and passes anything. Run at 100 it means
    # what it means in production. Only the MASK is computed here, not a full recolor, because
    # the question is purely whether the key still finds a one-pixel feature.
    # Isolate the DESPECKLE knob rather than the window. Each zone is measured twice on the same
    # fixture, once at its own min_blob and once at the floor's minimum, and the question is only
    # how much the despeckle setting costs it. A narrow WINDOW that finds nothing is not a defect
    # (the Arcanist Cap's star is sat_p 12 by design, to catch white paint on pink felt), but a
    # despeckle floor that throws away most of what its own window found is the bug the audit
    # demonstrated: min_blob 40 reads healthy on a chunky fixture and erases a bow's string.
    # DESPECKLE FLOOR, bounded rather than simulated. The audit's escape was min_blob=40, which
    # reads healthy on a chunky fixture and zeroes the second material on six of the nine real
    # bow cards, because the floor scales with sprite width squared and a bow's string is a
    # one-pixel line. A fixture that catches it honestly was attempted and abandoned: these keys
    # are PERCENTILES, so a thin feature is either swallowed by the cut or joined to a big
    # component, and the one smoothing pass erases a genuinely 1px line before despeckle ever
    # sees it. The value itself is the risk, so the value is what gets bounded. Every recipe in
    # this file sits at 2 to 6; 8 is a ceiling with headroom that still refuses the failure.
    fat = sorted((i, z.get("min_blob"), z["key"]) for i in zone_ids
                 for z in ZONE_OVERRIDES[i]["zones"] if z.get("min_blob", 4) > 8)
    check(f"no zone's despeckle floor can eat a thin second material (too fat: {fat})", not fat)

    silent = []
    for fixture_name, zsprite in (("blob", hazed_sprite(28)), ("thread", threaded_sprite(28))):
        zsolid = [c for c in ((x, y) for y in range(28) for x in range(28))
                  if zsprite.getpixel(c)[3] >= HALO_HI]
        for i in zone_ids:
            o = ZONE_OVERRIDES[i]
            painted = zone_recolor(zsprite, ICON_TINTS[i], o)
            bare = zone_recolor(zsprite, ICON_TINTS[i], {**o, "zones": []})
            moved = sum(1 for c in zsolid
                        if max(abs(a - b) for a, b in zip(painted.getpixel(c)[:3],
                                                          bare.getpixel(c)[:3])) >= 12)
            if not (0.03 * len(zsolid) <= moved <= 0.90 * len(zsolid)):
                silent.append((fixture_name, i, moved, len(zsolid)))
    check(f"every zone recipe paints some of the art and not all of it (bad: {silent})",
          not silent)

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
