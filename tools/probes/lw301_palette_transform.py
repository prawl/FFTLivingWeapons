#!/usr/bin/env python
"""
LW-301: turn a weapon's ICON colour into its BATTLE SPRITE palette.

This is the honing loop the owner asked for: get the transform near-perfect here, in a medium
where one iteration is a second, and only then codify it into the C# runtime where one iteration
is a rebuild, a deploy, a battle load and a swing.

WHAT IT MUST NOT DO. The Phase 0 probes flattened a palette to one flat colour to prove the
mechanism, and the result looked like painted plastic: a weapon is 16 shades describing a lit
metal form, and flattening throws all 16 away. So the transform ROTATES the vanilla palette
toward the target and KEEPS its internal structure. Directly borrowed from the sibling colour
mod's Preserve mode (ColorMod/ThemeEditor/RelativeShadeGenerator.cs) and from this repo's own
icon work, where the same mistake was the root of the "highlights look nonsensical" verdict.

THE RULE, per RAMP (rewritten 2026-08-21 after measuring every weapon, see below):
    1. read the icon's MATERIALS: hue, saturation and what share of the icon each covers
    2. split the vanilla palette into RAMPS, hue grouped runs of shades, one per substance
    3. merge ramps that cover the same PLACE on the drawing, which recovers the weapon's PARTS
    4. give the biggest part the icon's own colour, then spend the rest by share
    5. per slot: hue = its part's material, saturation = REBASED on it, value = untouched
Value is never rewritten, only carried, which is what preserves the light to dark ordering that
makes a blade read as lit metal.

WHY IT WAS REWRITTEN. The first version split each palette into two BRIGHTNESS zones and handed
the icon's two dominant colours to them. Reviewed eight weapons at a time it looked plausible; the
owner's verdict was "they look right except for the bow". Measured across all 118 palette mapped
weapons (tools/probes/lw303_grade.py, which compares the icon's hue against the hue actually
delivered over the pixels the weapon's own tile inks) it was not close: MEDIAN ERROR 76 DEGREES,
with only 17 weapons within 20 degrees and 48 more than 90 degrees out, orange icons arriving blue.
The natural experiment sitting inside that run is what settled the diagnosis: the six weapons whose
icons happened to offer a single material skipped the zone code entirely and every one of them
landed within 3 degrees. The zoning was the whole fault, not the colour maths. The rewritten rule
measures 5 degrees median, 103 of 118 within 20, and no weapon worse than 60.

THEN THE OWNER LOOKED AGAIN and said to note the two toned areas, which is step 3 above and the
reason the score went from 110 within 20 down to 103. Grouping by hue alone splits a blade from
its own shading, so weapons came back striped across one surface instead of split between blade
and grip. Merging ramps by WHERE they sit fixes what he saw, and costs seven weapons at the 20
degree line while improving the worst case. That trade is deliberate: the score was only ever a
floor, and a weapon that is the right colour in the wrong places still looks wrong.

WHAT THE SCORE DOES NOT COVER, from the adversarial review of this rewrite. The score reads HUE
only, so it is blind to the flatten failure this transform exists to prevent: a deliberately
flattened palette scores slightly BETTER on hue than the shipped code. lw303_grade.py therefore
also asserts, per slot, that brightness is carried over untouched, and reports how the edges move.
Read both halves or the number is worth nothing.

Slot 0 is transparent and bit 15 is a per-slot flag; both are preserved untouched. Getting either
wrong turns the sprite into a coloured rectangle instead of a weapon.

NOTE ON SPRITE IDENTITY: which drawing each weapon KIND uses is now known, from the owner's
identification of the numbered tile chart (LW-303, lw301_sprite_labels.json), not from the graphic
byte that [weapon-graphic-byte-not-sprite] retracted. Colour still does not depend on it: colour is
chosen per PALETTE, so any tile using that palette previews the transform faithfully. The identity
buys the right SILHOUETTE beside each icon, which is what makes a review sheet honest.

USAGE:
  python lw301_palette_transform.py codes <itemId>
  python lw301_palette_transform.py preview <itemId> <spriteIndex> [<itemId> <spriteIndex> ...]
"""
import colorsys
import json
import math
import pathlib
import struct
import sys
import tempfile

HERE = pathlib.Path(__file__).resolve().parent
ROOT = HERE.parents[1]
sys.path.insert(0, str(HERE))
import lw251_wep_spr_forge as forge

PAL_SLOTS = 16

# Transform constants. Every one was settled by measuring all 118 palette mapped weapons against
# their own icons with tools/probes/lw303_grade.py, not by eye.
HUE_BUCKETS = 36            # 10 degree buckets before merging, finer than the material window
MATERIAL_MERGE_DEG = 40.0   # buckets this close describe one substance, so merge their mass
MIN_MATERIAL_SHARE = 0.12   # under this share of the icon's chroma it is a speck, not a material
MAX_MATERIALS = 3
RAMP_MERGE_DEG = 40.0       # palette slots this close in hue belong to one ramp
NEUTRAL_SATURATION = 0.15   # below this a slot is grey steel, and all such slots form ONE ramp
OUTLINE_VALUE = 0.15        # below this a slot is the drawing's outline and is never recoloured
SAT_FLOOR = 0.55            # the least of a material's chroma any slot in its ramp may carry
PART_MERGE_FRACTION = 0.15  # ramps whose pixels sit this close together are one part of the weapon

# NAMED PARTS, and the palette slots that draw them. Measured, not guessed: every frame of each
# category was dumped slot by slot and the same slots draw the same part in all of them. Bows use
# slots 2, 3 and 4 for the string across all EIGHT of their frames, crossbows across all TWELVE,
# and knives and knight swords draw the blade with 1, 2, 3, 4, 15 and the grip with 5, 6, 7 across
# every frame of both. Reproduce the dump with lw303_zonemap.py plus the tile listing in
# lw301_sprite_boxes.json if these ever need re-checking after a game patch.
#
# WHY NAMED PARTS EXIST AT ALL. The owner asked for these four families to be two toned, with bow
# strings white and a grip that differs from its blade. The icons cannot supply that: measured
# along their own long axis, the two ends of a weapon icon sit 0 to 10 degrees apart for almost
# every one of them (Defender 10, Excalibur 9, Ravager 9, every bow within 5). The icons are single
# colour objects, so a second tone has to be a deliberate rule about the WEAPON rather than
# something read out of the picture.
#
# THE BOW HANDLE IS A THIRD TONE AND IT IS NOT INVENTED, it is the vanilla artist's own. Owner call
# 2026-08-21 looking at Skirmisher: "use the blue from the vanilla handle so it is tri-toned. Blue
# handle, purple sides, and white string." Slots 5, 6 and 7 draw the grip wrap, and the handle role
# simply LEAVES THEM ALONE, so whatever the artist painted there survives the recolour. Worth
# knowing before promising blue everywhere: that wrap is only blue in four of the thirteen weapon
# palettes (0, 3, 13 and 15). On the rest it is brown, olive, green or magenta, so this rule
# guarantees a third TONE, not a blue one. Of the seven bows that are recoloured at all,
# Skirmisher, Skypiercer and Stormarc get blue; the other four get their own earth tones.
# Also worth knowing: only four of the eight bow frames use these slots at all. The other four
# (tiles 76, 77, 78, 79) are a second bow drawing with no separate grip, so they stay two toned no
# matter what is done here.
PART_ROLES = {
    "Bow": {"string": {2, 3, 4}, "handle": {5, 6, 7}},
    "Crossbow": {"string": {2, 3, 4}},
    "Knife": {"grip": {5, 6, 7}},
    "KnightSword": {"grip": {5, 6, 7}},
}

# Weapons whose battle sprite is left exactly as the vanilla artist drew it. Owner call
# 2026-08-21 while reviewing the bows: these two keep their vanilla colours and are not
# recoloured at all.
KEEP_VANILLA = {90, 91}     # Yoichi Bow, Perseus Bow
STRING_SATURATION = 0.06    # a bowstring is white, not tinted
STRING_VALUE_FLOOR = 0.70   # vanilla strings run dark; lift the ramp so white reads as white
GRIP_CONTRAST_DEG = 40.0    # closer than this to the blade and the grip is not a second tone
LEATHER_HUE = 28 / 360.0    # a warm grip for a cool blade
STEEL_HUE = 215 / 360.0     # a cool grip for a warm blade

# Which sprite each weapon CATEGORY draws in battle, read from the owner's identification of the
# numbered tile chart (lw301_sprite_labels.json, LW-303, 2026-08-21). This used to be a hand-written
# dict that went stale the moment the owner corrected a call, so it now READS the labels file: the
# labels file is the record, this module is a consumer of it.
#
# The two vocabularies differ in one place only. Our item categories come from data/items.json;
# the labels use what the drawing looks like. "Instrument" items are harps, and the owner labelled
# those tiles Harp. Everything else matches by name.
CATEGORY_LABEL_ALIAS = {"Instrument": "Harp"}

# Categories with NO confirmed tile, and why. Cloth (the three veils) is the honest hole: the sheet
# carries a nine-tile pile the owner labelled Scroll, which is far more art than any one category
# needs, and cloth is a plausible tenant of it. Plausible is not identified. Naming it here beats
# drawing a wrong silhouette next to a veil, which is exactly the mistake that earned the earlier
# retraction on this sheet.
CATEGORY_NO_SPRITE = {
    "Cloth": "no tile identified; the 9-tile Scroll pile is a candidate but was never confirmed",
}

# A tile is a poor stand-in for its category when it is drawn edge-on: a pole seen end-on inks
# MORE pixels than the same pole seen across, while showing nothing recognisable. Anything longer
# than 3:1 is treated as an edge-on frame and only used if the category has no broader one.
MAX_REPRESENTATIVE_ASPECT = 3.0

_LABELS = None
_BOXES = None
_GRID = None
_RAW = None
_INK = {}


def sprite_labels():
    """{tile index -> owner label record} from lw301_sprite_labels.json."""
    global _LABELS
    if _LABELS is None:
        raw = json.loads((HERE / "lw301_sprite_labels.json").read_text(encoding="utf-8"))["labels"]
        _LABELS = {int(k): v for k, v in raw.items()}
    return _LABELS


def sprite_boxes():
    """{tile index -> {x, y, w, h}} from lw301_sprite_boxes.json."""
    global _BOXES
    if _BOXES is None:
        _BOXES = {b["i"]: b for b in json.loads((HERE / "lw301_sprite_boxes.json").read_text(encoding="utf-8"))}
    return _BOXES


def sheet_raw():
    global _RAW
    if _RAW is None:
        _RAW = load_sheet()
    return _RAW


def sheet_index_grid():
    """Page 1 of the sheet as a 256x256 grid of palette SLOT indices (4bpp, low nibble first)."""
    global _GRID
    if _GRID is None:
        pixels = sheet_raw()[512:512 + 32768]
        W = 256
        grid = [[0] * W for _ in range(W)]
        for i in range(len(pixels) * 2):
            byte = pixels[i // 2]
            grid[i // W][i % W] = (byte & 0x0F) if i % 2 == 0 else (byte >> 4)
        _GRID = grid
    return _GRID


def tile_ink(i):
    """How many pixels of a tile are actually drawn (slot 0 is transparent)."""
    if i not in _INK:
        b = sprite_boxes()[i]
        grid = sheet_index_grid()
        _INK[i] = sum(1 for yy in range(b["h"]) for xx in range(b["w"]) if grid[b["y"] + yy][b["x"] + xx])
    return _INK[i]


def tiles_for_category(category):
    """Every tile the owner labelled as this category's kind of weapon, best stand-in first.

    Ordered by the owner's confidence in 0.2-wide TIERS, then a broadside view over an edge-on
    one, then by how much art the tile actually inks. Two deliberate details: ink alone is the
    wrong sort key (an end-on pole is a nearly solid bar and outscores the diagonal swing a human
    can recognise), and tiering the confidence stops a 0.85-against-0.90 hair from settling the
    order before legibility gets a vote, which is what put a 21x3 ninja-blade sliver at the top.
    """
    label = CATEGORY_LABEL_ALIAS.get(category, category)
    boxes = sprite_boxes()
    out = []
    for i, rec in sprite_labels().items():
        if rec.get("name") != label or i not in boxes:
            continue
        b = boxes[i]
        long_side, short_side = max(b["w"], b["h"]), max(1, min(b["w"], b["h"]))
        out.append((-int(float(rec.get("conf", 0.0)) * 5),
                    long_side / short_side > MAX_REPRESENTATIVE_ASPECT,
                    -tile_ink(i), i))
    return [i for _, _, _, i in sorted(out)]


def sprite_for_category(category):
    """(tile index, None) for an identified category, or (None, reason) for one we cannot draw."""
    if category in CATEGORY_NO_SPRITE:
        return None, CATEGORY_NO_SPRITE[category]
    tiles = tiles_for_category(category)
    if not tiles:
        return None, "no tile in lw301_sprite_labels.json is labelled " + repr(category)
    return tiles[0], None


def bgr555_to_rgb(c):
    return ((c & 31) / 31.0, ((c >> 5) & 31) / 31.0, ((c >> 10) & 31) / 31.0)


def rgb_to_bgr555(r, g, b):
    q = lambda v: max(0, min(31, int(round(v * 31))))
    return q(r) | (q(g) << 5) | (q(b) << 10)


def load_sheet():
    wd = tempfile.mkdtemp(prefix="lw301_")
    raw = forge.load_vanilla(wd)
    return raw[0] if isinstance(raw, tuple) else raw


def palette_of(raw, index):
    return list(struct.unpack_from("<16H", raw, index * 32))


_ICON_CACHE = {}


def icon_image(item_id, ff16, tmp):
    """The shipped 48px icon as RGBA, decoded ONCE per item and cached.

    Both readers below want the same pixels, and tex-conv is a subprocess: without this, a full
    sheet pays two process launches per weapon for one file that never changes mid-run.
    """
    if item_id in _ICON_CACHE:
        return _ICON_CACHE[item_id]
    import os, subprocess
    from PIL import Image
    src = ROOT / "mod/FFTIVC/data/enhanced/ui/ffto/icon/equip_item_s/texture" / f"ei_s_{item_id:03d}_uitx.tex"
    im = None
    if src.exists():
        dst = os.path.join(tmp, src.name)
        open(dst, "wb").write(src.read_bytes())
        subprocess.run([str(ff16), "tex-conv", "-i", dst], capture_output=True)
        dds = dst[:-4] + ".dds"
        if os.path.exists(dds):
            im = Image.open(dds).convert("RGBA")
    _ICON_CACHE[item_id] = im
    return im


def rendered_icon_hue(item_id, ff16, tmp):
    """The hue the 48px icon ACTUALLY renders, chroma-weighted over opaque pixels.

    The authored iconTint is what the icon engine was ASKED for, not always what it produced: the
    engine can move a colour into a zone and leave the body a different hue. Measured 2026-08-21,
    most weapons agree within 20 degrees but Kiyomori's authored 330 renders as 143, because its
    venom colour went into the edge. "Match the icon" means matching what the player sees, so the
    target comes from the rendered pixels and the authored value is only a fallback.
    """
    import colorsys, math
    im = icon_image(item_id, ff16, tmp)
    if im is None:
        return None
    px = im.load()
    X = Y = 0.0
    n = 0
    for y in range(im.size[1]):
        for x in range(im.size[0]):
            r, g, b, a = px[x, y]
            if a < 250:
                continue
            h, sat, v = colorsys.rgb_to_hsv(r / 255, g / 255, b / 255)
            if sat < 0.12 or v < 0.12:
                continue
            X += math.cos(2 * math.pi * h) * sat
            Y += math.sin(2 * math.pi * h) * sat
            n += 1
    if n < 10:
        return None
    return (math.atan2(Y, X) / (2 * math.pi)) % 1.0


def hue_distance(a, b):
    """Shortest angle between two hues, in degrees (0..180). Hue is circular; subtraction is not."""
    d = abs(a - b) % 1.0
    return min(d, 1 - d) * 360


def icon_materials(item_id, ff16, tmp):
    """The icon's distinct MATERIALS: hue, saturation, and the share of the icon each covers.

    Why two or three and not one: a weapon is a blade plus its fittings, and the owner's directive
    (2026-08-21) is that the handle and hilt must not be the same colour as the blade.

    CORRECTED 2026-08-21 (second pass) after measuring all 118 weapons against their own icons. The
    first version bucketed hue at 20 degrees, ranked buckets by RAW PIXEL COUNT, and SKIPPED any
    bucket near one already taken. Three faults followed, each proven by measurement:
      1. A material split across bucket boundaries lost most of its mass. Kotetsu's gold reads as
         three adjacent buckets; ranked singly, a smaller blue bucket outranked all of it.
      2. Raw pixel count is the wrong weight in principle: it counts a washed out pixel and a
         vivid one the same, while "what colour is this" plainly does not. Chroma mass (the sum of
         saturation) is what rendered_icon_hue already used, so ranking by it makes the two halves
         of this file agree instead of quietly disagreeing about the same icon. CORRECTED after
         adversarial review 2026-08-21: an earlier draft of this docstring blamed Terrastaff's
         pixel counts, claiming its pale shaft outnumbered its blue head. Counted properly the
         blue wins about six to one at every saturation gate, so that sentence was a plausible
         story rather than a measurement, and it is struck rather than quietly deleted.
      3. Skipped buckets became materials anyway, which is what actually broke Terrastaff. Its
         blue is real and ranked first, but a ONE pixel orange speck, 171 degrees away and holding
         0.4 percent of the icon's chroma, was the next bucket far enough from blue to survive the
         separation test. It became the second material and was painted over a whole ramp.
    So: merge every bucket within MATERIAL_MERGE_DEG of the strongest into one material, weight by
    chroma mass, and drop anything under MIN_MATERIAL_SHARE of the icon. An icon with one material
    now returns one, and every weapon that already took that path scored within 3 degrees of its
    icon under the old code, which is the evidence this path was always the sound one.
    """
    import colorsys
    im = icon_image(item_id, ff16, tmp)
    if im is None:
        return None
    px = im.load()
    buckets = {}
    for y in range(im.size[1]):
        for x in range(im.size[0]):
            r, g, b, a = px[x, y]
            if a < 250:
                continue
            h, s, v = colorsys.rgb_to_hsv(r / 255, g / 255, b / 255)
            if v < 0.18 or s < 0.18:
                continue                                    # outline and shadow are not material
            acc = buckets.setdefault(int(h * HUE_BUCKETS) % HUE_BUCKETS, [0.0, 0.0, 0.0, 0.0])
            acc[0] += s                                     # chroma mass
            acc[1] += math.cos(2 * math.pi * h) * s
            acc[2] += math.sin(2 * math.pi * h) * s
            acc[3] += s * s                                 # mass weighted saturation
    if not buckets:
        return None
    pool = [((k + 0.5) / HUE_BUCKETS, m, X, Y, S2) for k, (m, X, Y, S2) in buckets.items()]
    total = sum(p[1] for p in pool)
    mats = []
    while pool and len(mats) < MAX_MATERIALS:
        pool.sort(key=lambda p: -p[1])
        seed = pool[0][0]
        near = [p for p in pool if hue_distance(p[0], seed) <= MATERIAL_MERGE_DEG]
        pool = [p for p in pool if hue_distance(p[0], seed) > MATERIAL_MERGE_DEG]
        mass = sum(p[1] for p in near)
        h = (math.atan2(sum(p[3] for p in near), sum(p[2] for p in near)) / (2 * math.pi)) % 1.0
        mats.append({"h": h, "s": (sum(p[4] for p in near) / mass) if mass else 0.0,
                     "share": (mass / total) if total else 0.0})
    mats = [m for m in mats if m["share"] >= MIN_MATERIAL_SHARE] or mats[:1]
    keep = sum(m["share"] for m in mats)
    for m in mats:
        m["share"] /= keep
    return mats


def slot_pixels(box):
    """Where in one tile each palette slot is drawn: {slot -> [(x, y), ...]}.

    Both of the corrections that made this transform work come out of this one measurement. How
    MANY pixels a slot covers says how much it matters; WHERE they sit says which part of the
    weapon it belongs to, and neither can be read off the palette alone.
    """
    grid = sheet_index_grid()
    at = {}
    for yy in range(box["h"]):
        for xx in range(box["w"]):
            v = grid[box["y"] + yy][box["x"] + xx]
            if v:
                at.setdefault(v, []).append((xx, yy))
    return at


def slot_usage(box):
    """How many pixels of one tile use each palette slot."""
    return {k: len(v) for k, v in slot_pixels(box).items()}


def palette_ramps(van, usage):
    """Split a vanilla palette into its RAMPS: hue grouped runs of shades, heaviest ramp first.

    A vanilla weapon palette is not two materials, it is four or more hue grouped ramps, each a
    light to dark run describing one substance (steel, wood, leather, gold). The previous transform
    split by BRIGHTNESS instead (bright and unsaturated meant "blade"), which cut straight across
    those ramps: Yoichi Bow's five "blade" slots held hues 120, 60, 300, 300 and 40, five unrelated
    substances treated as one material. Grouping by hue keeps a substance whole.

    Every low saturation slot forms ONE neutral ramp, since grey steel is a single substance
    however its residual hue happens to lean.

    ABOUT OUTLINE_VALUE, corrected after adversarial review 2026-08-21. This gate exists to skip
    true near black slots, and in the thirteen weapon palettes the game ships it NEVER FIRES: the
    darkest slot anywhere across them measures 0.161, above the 0.15 line. So the slots that
    actually draw each tile's edge are recoloured like any other, and that is deliberate rather
    than an oversight. A vanilla outline here is not black, it is a dark tinted shade of the
    substance beside it, so a blue sword wants a dark blue edge and not the vanilla brown one.
    What that costs is worth naming: brightness is held in HSV value, which is exact, but two
    colours at equal value are not equally bright to the eye, so an edge slot can shift up to
    about a third in perceived luma when its hue changes. The gate stays as a floor for palettes
    we have not seen, not as a description of what happens to these ones.
    """
    import colorsys
    lit = []
    for i, c in enumerate(van):
        if i == 0 or c == 0:
            continue
        h, s, v = colorsys.rgb_to_hsv(*bgr555_to_rgb(c & 0x7FFF))
        if v < OUTLINE_VALUE:
            continue
        lit.append({"i": i, "h": h, "s": s, "v": v, "w": usage.get(i, 0)})
    groups = []
    for slot in sorted([x for x in lit if x["s"] >= NEUTRAL_SATURATION], key=lambda x: -x["w"]):
        for g in groups:
            if hue_distance(slot["h"], g["h"]) <= RAMP_MERGE_DEG:
                g["slots"].append(slot)
                g["w"] += slot["w"]
                break
        else:
            groups.append({"h": slot["h"], "slots": [slot], "w": slot["w"]})
    neutral = [x for x in lit if x["s"] < NEUTRAL_SATURATION]
    if neutral:
        groups.append({"h": None, "slots": neutral, "w": sum(x["w"] for x in neutral)})
    return sorted(groups, key=lambda g: -g["w"])


def weapon_parts(van, box):
    """The weapon's PARTS: hue ramps merged again when they cover the same place on the drawing.

    A ramp is a substance as the PALETTE sees it. It is not a part of the weapon, and treating the
    two as the same thing is what the owner caught on sight: the sprites came back "two toned" in
    the wrong places, striped across a blade rather than split between blade and grip.

    Measured on the sword tile, which says it plainly. Its four ramps are a neutral run and a
    180 degree run whose pixels sit 2 percent of the tile apart, and a 240 degree run and a 120
    degree run sitting 14 percent apart, with 49 percent between the two groups. So the blade is
    TWO ramps and the hilt is TWO ramps: the artist shaded one object with two hue families, and
    handing those families different colours paints a stripe down the middle of the blade. Merging
    by position recovers what a person sees, blade and hilt, and then the colours land where the
    eye expects them.

    PART_MERGE_FRACTION is a share of the tile's diagonal, so it scales with the drawing rather
    than assuming every weapon is the same size. Ramps that ink nothing in this tile keep to
    themselves; they cannot be placed, and inventing a position for them would merge them at
    random.
    """
    at = slot_pixels(box)
    ramps = palette_ramps(van, {k: len(v) for k, v in at.items()})
    diagonal = math.hypot(box["w"], box["h"]) or 1.0
    centre = []
    for g in ramps:
        pts = [pt for x in g["slots"] for pt in at.get(x["i"], [])]
        centre.append((sum(pt[0] for pt in pts) / len(pts),
                       sum(pt[1] for pt in pts) / len(pts)) if pts else None)
    parent = list(range(len(ramps)))

    def root(a):
        while parent[a] != a:
            parent[a] = parent[parent[a]]
            a = parent[a]
        return a

    for i in range(len(ramps)):
        for j in range(i + 1, len(ramps)):
            if centre[i] is None or centre[j] is None:
                continue
            near = math.hypot(centre[i][0] - centre[j][0], centre[i][1] - centre[j][1]) / diagonal
            if near <= PART_MERGE_FRACTION:
                parent[root(i)] = root(j)
    merged = {}
    for i, g in enumerate(ramps):
        key = root(i) if centre[i] is not None else ("unplaced", i)
        part = merged.setdefault(key, {"slots": [], "w": 0})
        part["slots"] += g["slots"]
        part["w"] += g["w"]
    return sorted(merged.values(), key=lambda part: -part["w"])


def assign_ramps(ramps, mats, icon_hue=None):
    """Hand each ramp a material so the PAINTED PIXELS mirror the icon's own colour proportions.

    Spending each material a pixel budget equal to its share of the icon makes the sprite's colour
    balance match the icon's, and still leaves the fittings a different colour whenever the icon
    has a genuine second material.

    HOW MUCH THIS PART IS WORTH, measured by ablation after adversarial review 2026-08-21, because
    an earlier draft of this docstring took credit for the whole repair. Replacing this function
    with "every ramp takes the dominant material" costs only 1 degree of median error, 5 to 6, but
    it drops the weapons within 20 degrees from 110 to 98. So the bulk of the fix is upstream, in
    grouping the palette by HUE instead of brightness and in rebasing saturation; this rule buys
    the last twelve weapons and the two tone look the owner asked for. Note also what it does NOT
    change: budgets start unspent and the ramps arrive heaviest first, so the largest ramp still
    always takes the dominant material. The difference from the old rule is what happens to every
    ramp after that one, and that the ramps are now whole substances rather than brightness bands.

    THE LARGEST PART IS PINNED to the material nearest the icon's own overall colour, rather than
    to whichever material happens to hold the most chroma. Those two usually agree. When they do
    not, the difference is stark: Flamberge's icon splits 39 percent red against 40 percent blue,
    and on that one point of chroma the whole blade used to flip to blue while a person looking at
    the icon would call the weapon red. Pinning the biggest part to the colour a person would name
    took the worst weapon in the set from 118 degrees out to 60.
    """
    total = sum(g["w"] for g in ramps) or 1
    lead = 0 if icon_hue is None else min(range(len(mats)),
                                          key=lambda j: hue_distance(mats[j]["h"], icon_hue))
    budget = [m["share"] * total for m in mats]
    spent = [0.0] * len(mats)
    out = []
    for n, g in enumerate(ramps):
        k = lead if n == 0 else max(range(len(mats)), key=lambda j: budget[j] - spent[j])
        if n and budget[k] - spent[k] <= 0:
            k = lead                    # budgets all spent: the weapon's own colour takes the rest
        spent[k] += g["w"]
        out.append((g, mats[k]))
    return out


def contrast_hue(blade_hue, mats):
    """A grip colour that cannot be mistaken for the blade.

    First choice is the icon's own second material, because a real second colour in the picture
    beats anything invented. Most of these icons do not have one, so the fallback follows the
    game's own convention rather than a colour wheel: warm blades get a steel grip and cool blades
    get a leather one, which is how the vanilla artist drew them.
    """
    second = next((m for m in mats[1:] if hue_distance(m["h"], blade_hue) >= GRIP_CONTRAST_DEG), None)
    if second is not None:
        return second["h"], second["s"]
    warm = hue_distance(blade_hue, 40 / 360.0) < 70
    hue = STEEL_HUE if warm else LEATHER_HUE
    if hue_distance(hue, blade_hue) < GRIP_CONTRAST_DEG:
        hue = LEATHER_HUE if warm else STEEL_HUE
    return hue, 0.60


def apply_part_roles(codes, van, category, mats, blade_hue):
    """Force the two tones the owner asked for onto parts the icon cannot describe.

    Runs AFTER the normal painting, and touches only the slots named in PART_ROLES, so every other
    weapon and every other slot is untouched by it.

    The string is the ONE place this file rewrites brightness. A vanilla bowstring's three slots sit
    at values 0.32, 0.52 and 0.71, which is a dark grey rope: draining its colour alone leaves a
    grey string, not a white one. So the string's ramp is lifted into the top of the range with its
    ORDER preserved, brightest staying brightest. Everywhere else value is still carried untouched,
    and lw303_grade.py checks that separately so this exception cannot hide a flattened ramp.
    """
    import colorsys
    roles = PART_ROLES.get(category)
    if not roles:
        return codes
    out = list(codes)
    for role, slots in roles.items():
        live = [i for i in slots if i < len(van) and van[i] != 0]
        if not live:
            continue
        if role == "string":
            vals = sorted({colorsys.rgb_to_hsv(*bgr555_to_rgb(van[i] & 0x7FFF))[2] for i in live})
            for i in live:
                v = colorsys.rgb_to_hsv(*bgr555_to_rgb(van[i] & 0x7FFF))[2]
                rank = vals.index(v) / max(1, len(vals) - 1)
                lifted = STRING_VALUE_FLOOR + (1.0 - STRING_VALUE_FLOOR) * rank
                out[i] = (van[i] & 0x8000) | rgb_to_bgr555(
                    *colorsys.hsv_to_rgb(0.0, STRING_SATURATION, lifted))
        elif role == "handle":
            for i in live:
                out[i] = van[i]               # the vanilla grip colour is the third tone
        elif role == "grip":
            painted = colorsys.rgb_to_hsv(*bgr555_to_rgb(out[live[0]] & 0x7FFF))[0]
            if hue_distance(painted, blade_hue) >= GRIP_CONTRAST_DEG:
                continue                      # the grip already differs; leave the icon's answer
            hue, sat = contrast_hue(blade_hue, mats)
            top = max(colorsys.rgb_to_hsv(*bgr555_to_rgb(van[i] & 0x7FFF))[1] for i in live)
            for i in live:
                h, sv, v = colorsys.rgb_to_hsv(*bgr555_to_rgb(van[i] & 0x7FFF))
                rel = (sv / top) if top > 0.05 else 1.0
                out[i] = (van[i] & 0x8000) | rgb_to_bgr555(
                    *colorsys.hsv_to_rgb(hue, min(1.0, sat * (SAT_FLOOR + (1 - SAT_FLOOR) * rel)), v))
    return out


def paint_by_part(van, mats, box, icon_hue=None):
    """The transform: every ramp takes a material's hue and keeps its own light to dark shape.

    Saturation is REBASED on the material rather than multiplied by the vanilla slot's own.
    Multiplying was the third defect measured on 2026-08-21: a slot at saturation 0 stays 0 whatever
    it is multiplied by, so every weapon drawn in grey steel (books, guns, silver blades) was locked
    colourless no matter what its icon said. Glarebound Tome's blue icon produced a grey book whose
    measured saturation was 0.06, identical to vanilla. Rebasing lets the colour arrive, while the
    (SAT_FLOOR + rest * rel) term keeps each ramp's internal chroma order so a blade still shades.

    VALUE IS NEVER TOUCHED. That is what preserves the light to dark ordering that makes the sprite
    read as a lit object instead of the painted plastic the Phase 0 flatten produced.

    Without a tile there is no way to tell a weapon's parts apart, so this falls back to painting
    per RAMP with every slot counted equally. That is the old behaviour and it is worse; pass the
    box.
    """
    import colorsys
    out = list(van)
    groups = (weapon_parts(van, box) if box
              else palette_ramps(van, {i: 1 for i in range(1, PAL_SLOTS)}))
    for ramp, mat in assign_ramps(groups, mats, icon_hue):
        top = max([x["s"] for x in ramp["slots"]] + [0.0])
        for x in ramp["slots"]:
            rel = (x["s"] / top) if top > 0.05 else 1.0
            ns = min(1.0, mat["s"] * (SAT_FLOOR + (1.0 - SAT_FLOOR) * rel))
            out[x["i"]] = (van[x["i"]] & 0x8000) | rgb_to_bgr555(
                *colorsys.hsv_to_rgb(mat["h"], ns, x["v"]))
    return out


def item_tint_and_palette(item_id):
    items = {i["id"]: i for i in json.loads((ROOT / "data" / "items.json").read_text(encoding="utf-8"))["items"]}
    pmap = {w["id"]: w for w in json.loads((HERE / "lw289_weapon_palette_map.json").read_text(encoding="utf-8"))}
    it, pm = items.get(item_id), pmap.get(item_id)
    if not it or not pm:
        sys.exit(f"item {item_id} not found in items.json and/or the palette map")
    if not it.get("iconTint"):
        sys.exit(f"{it['name']} has no iconTint, so it has no colour to carry into battle")
    return it, tuple(it["iconTint"]), pm["weaponPalette"]


def cmd_codes(item_id):
    import tempfile
    sys.path.insert(0, str(ROOT / "tools"))
    from lib.paths import FF16
    tmp = tempfile.mkdtemp(prefix="lw301codes_")
    raw = sheet_raw()
    it, tint, pal = item_tint_and_palette(item_id)
    van = palette_of(raw, pal)
    spi, why = sprite_for_category(it.get("category", ""))
    box = sprite_boxes()[spi] if spi is not None else None
    got = recolour_for_item(it, van, FF16, tmp, box)
    print(f"{it['name']}  palette {pal}  tile {spi if spi is not None else why}")
    print(f"  authored hue {got['authored']*360:.0f}deg, icon renders {got['hue']*360:.0f}deg"
          + (f", drift {got['drift']:.0f}deg" if got["drift"] is not None else ""))
    for n, m in enumerate(icon_materials(item_id, FF16, tmp) or []):
        print(f"  material {n}: hue {m['h']*360:>3.0f}deg  sat {m['s']:.2f}  {m['share']*100:>3.0f}% of the icon")
    print(f"  {'slot':>4} {'vanilla':>8} {'new':>8}")
    for i, (a, b) in enumerate(zip(van, got["codes"])):
        print(f"  {i:>4}     {a:04x}     {b:04x}")


def cmd_preview(pairs):
    from PIL import Image, ImageDraw
    raw = load_sheet()
    pix = raw[512:512 + 32768]
    boxes = {b["i"]: b for b in json.loads((HERE / "lw301_sprite_boxes.json").read_text(encoding="utf-8"))}
    W = 256
    idx = [[0] * W for _ in range(W)]
    for i in range(len(pix) * 2):
        byte = pix[i // 2]
        idx[i // W][i % W] = (byte & 0x0F) if i % 2 == 0 else (byte >> 4)

    import tempfile
    sys.path.insert(0, str(ROOT / "tools"))
    from lib.paths import FF16
    tmp = tempfile.mkdtemp(prefix="lw301prev_")
    Z, rows = 6, []
    for item_id, sprite in pairs:
        it, tint, pal = item_tint_and_palette(item_id)
        van = palette_of(raw, pal)
        box = boxes[sprite]
        new = recolour_for_item(it, van, FF16, tmp, box)["codes"]
        panels = []
        for codes in (van, new):
            im = Image.new("RGBA", (box["w"] * Z, box["h"] * Z), (0, 0, 0, 0))
            d = ImageDraw.Draw(im)
            for yy in range(box["h"]):
                for xx in range(box["w"]):
                    v = idx[box["y"] + yy][box["x"] + xx]
                    if v:
                        r, g, b = bgr555_to_rgb(codes[v] & 0x7FFF)
                        d.rectangle([xx * Z, yy * Z, xx * Z + Z - 1, yy * Z + Z - 1],
                                    fill=(int(r * 255), int(g * 255), int(b * 255), 255))
            panels.append(im)
        rows.append((it["name"], tint, pal, panels))

    pad, lab = 24, 18
    Wt = max(p[0].width + p[1].width for _, _, _, p in rows) + pad * 3 + 150
    Ht = sum(max(p[0].height, p[1].height) + lab + pad for _, _, _, p in rows) + pad
    sheet = Image.new("RGBA", (Wt, Ht), (246, 244, 240, 255))
    d = ImageDraw.Draw(sheet)
    y = pad
    for name, tint, pal, (a, b) in rows:
        d.text((10, y), f"{name}  pal {pal}  hue {tint[0]*360:.0f}deg", fill=(30, 30, 30, 255))
        sheet.paste(a, (150, y + lab), a)
        sheet.paste(b, (150 + a.width + pad, y + lab), b)
        y += max(a.height, b.height) + lab + pad
    out = HERE / "lw301_preview.png"
    sheet.convert("RGB").save(out)
    print(f"vanilla | recoloured  ->  {out}")



def recolour_for_item(it, van, ff16, tmp, box=None):
    """One weapon's finished battle palette, plus everything a reviewer needs to judge it.

    The single place a weapon's colour is decided, so every review surface shows the colours the
    next surface will. Returns the 16 codes, how many materials the icon offered, the hue aimed at,
    and how far that sits from the authored iconTint.

    `box` is the weapon's own tile. It matters because the ramps are weighted by how much of THAT
    drawing each palette slot covers: the same palette recoloured for a bow and for a gun should
    not come out the same, since the two drawings lean on different slots. Without a box every slot
    counts equally, which is a fair fallback but a worse answer.
    """
    tint = list(it["iconTint"])
    authored = tint[0]
    rendered = rendered_icon_hue(it["id"], ff16, tmp)
    drift = None
    if rendered is not None:
        d = abs(rendered - authored) % 1.0
        drift = min(d, 1 - d) * 360
        tint[0] = rendered            # match what the icon RENDERS, not what was asked for
    mats = icon_materials(it["id"], ff16, tmp)
    if not mats:
        return {"codes": list(van), "mode": "no colour in icon", "hue": tint[0],
                "authored": authored, "drift": drift}
    if it["id"] in KEEP_VANILLA:
        return {"codes": list(van), "mode": "vanilla, owner call", "hue": tint[0],
                "authored": authored, "drift": drift}
    label = "1 material" if len(mats) == 1 else "%d materials" % len(mats)
    parts = len(weapon_parts(van, box)) if box else 0
    if parts:
        label += ", %d part%s" % (parts, "" if parts == 1 else "s")
    codes = paint_by_part(van, mats, box, tint[0])
    category = it.get("category", "")
    if category in PART_ROLES:
        codes = apply_part_roles(codes, van, category, mats, mats[0]["h"])
        label += ", " + "/".join(sorted(PART_ROLES[category]))
    return {"codes": codes, "mode": label, "hue": tint[0],
            "authored": authored, "drift": drift}


def cmd_sheet(limit=None, only_palette=None):
    """Regenerate the review sheet for EVERY tinted weapon, from current data.

    Deliberately repeatable and hand-data-free. The owner has said weapon colours WILL change
    again, so the review must be a command rather than an artifact: re-run it after editing
    data/items.json and every row is recomputed. Nothing here caches a colour.

    Each row is: the shipped 48px ICON (what the player sees in the list), the 16 transformed
    palette slots as swatches (the actual bytes that reach the game), and this weapon KIND's own
    battle drawing rendered in them beside the same drawing in vanilla. The tile comes from the
    owner's identification of the sprite chart (LW-303); a category with no identified tile draws
    nothing and says so, because a wrong silhouette invites a verdict on art the weapon never uses.
    """
    import os, subprocess, tempfile
    from PIL import Image, ImageDraw
    sys.path.insert(0, str(ROOT / "tools"))
    from lib.paths import FF16

    raw = sheet_raw()
    idx = sheet_index_grid()
    boxes = sprite_boxes()

    items = json.loads((ROOT / "data" / "items.json").read_text(encoding="utf-8"))["items"]
    pmap = {w["id"]: w for w in json.loads((HERE / "lw289_weapon_palette_map.json").read_text(encoding="utf-8"))}
    # Ninja blades used to be filtered out here ("owner: skip ninja blades for now"). Dropped
    # 2026-08-21 after adversarial review pointed out the consequence: Swiftfang is the single
    # worst scoring weapon in the corpus, and the sheet the owner actually looks at was the one
    # surface hiding it. A review surface that quietly omits the worst case is worse than none.
    rows = [it for it in items if it["id"] in pmap and it.get("iconTint")]
    if only_palette is not None:
        rows = [it for it in rows if pmap[it["id"]]["weaponPalette"] == only_palette]
    rows.sort(key=lambda it: (pmap[it["id"]]["weaponPalette"], it["id"]))
    if limit:
        rows = rows[:limit]

    tmp = tempfile.mkdtemp(prefix="lw301sheet_")
    def icon_of(item_id):
        return icon_image(item_id, FF16, tmp)

    def sprite(codes, bx, z=4):
        im = Image.new("RGBA", (bx["w"] * z, bx["h"] * z), (0, 0, 0, 0))
        d = ImageDraw.Draw(im)
        for yy in range(bx["h"]):
            for xx in range(bx["w"]):
                v = idx[bx["y"] + yy][bx["x"] + xx]
                if v:
                    r, g, b = bgr555_to_rgb(codes[v] & 0x7FFF)
                    d.rectangle([xx * z, yy * z, xx * z + z - 1, yy * z + z - 1],
                                fill=(int(r * 255), int(g * 255), int(b * 255), 255))
        return im

    RH, pad = 108, 10
    sheet = Image.new("RGBA", (980, RH * len(rows) + pad), (246, 244, 240, 255))
    d = ImageDraw.Draw(sheet)
    for n, it in enumerate(rows):
        pal = pmap[it["id"]]["weaponPalette"]
        van = palette_of(raw, pal)
        spi, why = sprite_for_category(it.get("category", ""))
        bx = boxes[spi] if spi is not None else None
        got = recolour_for_item(it, van, FF16, tmp, bx)
        new, mode, drift = got["codes"], got["mode"], got["drift"]
        tint = [got["hue"], it["iconTint"][1], it["iconTint"][2]]
        y = pad + n * RH
        d.text((8, y + 4), f'{it["name"]}', fill=(20, 20, 20, 255))
        lbl = f'pal {pal}  icon hue {tint[0]*360:.0f}deg'
        if drift is not None and drift > 25:
            lbl += f'  (authored {it["iconTint"][0]*360:.0f}, drift {drift:.0f})'
        d.text((8, y + 20), lbl, fill=(110, 105, 96, 255))
        d.text((8, y + 36), f'{it.get("category", "?")} / {mode}' + ("" if spi is not None else "  [no confirmed sprite]"),
               fill=(140, 136, 128, 255) if spi is not None else (150, 120, 100, 255))
        ic = icon_of(it["id"])
        if ic:
            sheet.paste(ic.resize((88, 88), Image.NEAREST), (170, y + 6), ic.resize((88, 88), Image.NEAREST))
        for k, c in enumerate(new):                       # the bytes that reach the game
            if c == 0:
                continue
            r, g, b = bgr555_to_rgb(c & 0x7FFF)
            d.rectangle([272 + k * 17, y + 30, 272 + k * 17 + 15, y + 62],
                        fill=(int(r * 255), int(g * 255), int(b * 255), 255), outline=(210, 206, 198, 255))
        if bx is not None:
            a = sprite(new, bx); b2 = sprite(van, bx)
            sheet.paste(a, (566, y + 6), a)
            sheet.paste(b2, (566 + a.width + 14, y + 6), b2)
        else:
            d.text((566, y + 40), why or "sprite shape not identified", fill=(170, 120, 90, 255))
    d.text((566, 2), "coloured", fill=(20, 20, 20, 255))
    d.text((700, 2), "vanilla", fill=(20, 20, 20, 255))
    out = HERE / "lw301_sheet.png"
    sheet.convert("RGB").save(out)
    print(f"{len(rows)} weapons -> {out}")


def main():
    mode = sys.argv[1] if len(sys.argv) > 1 else ""
    if mode == "codes" and len(sys.argv) >= 3:
        cmd_codes(int(sys.argv[2]))
    elif mode == "sheet":
        lim = int(sys.argv[2]) if len(sys.argv) > 2 else None
        cmd_sheet(lim)
    elif mode == "preview" and len(sys.argv) >= 4:
        rest = [int(x) for x in sys.argv[2:]]
        cmd_preview(list(zip(rest[0::2], rest[1::2])))
    else:
        print(__doc__)


if __name__ == "__main__":
    main()
