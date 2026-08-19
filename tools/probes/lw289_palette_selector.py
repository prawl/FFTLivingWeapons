#!/usr/bin/env python
"""LW-289 step 1: WHAT picks which of the 16 palettes a battle weapon renders with?

The mechanism is settled (LIVE_LEDGER [wep-spr-palette-block], PROVEN 2026-08-19): battle weapon
colour comes from the 512-byte palette block at the head of FFTPack file 71,
`unit/battle_wep_spr.bin`, and a mod-shipped copy of that file repaints it. What is NOT settled
is the SELECTOR: nothing yet says which of the sixteen palettes any given weapon draws from, and
without that map there is no way to bake icon colours per weapon.

WHY NOT JUST RUN THE CENSUS. The plan of record was "paint all sixteen palettes a different loud
colour, then swing one weapon per type and per tier and read the map off the screen". That is
forty-plus swings of owner time. This probe spends four swings instead, because there is a
candidate selector sitting in plain sight and a 2x2 kills it or crowns it in one battle.

THE CANDIDATE. The modloader's vanilla ItemData template gives every item a <Palette> byte (0-15)
and a <SpriteID> byte (0-70, which weapon graphic). Both are real, patchable fields on the
loader's Item model (fftivc.utility.modloader.Interfaces/Tables/Models/Item.cs, PropertyMap,
written back into ITEM_COMMON_DATA). Over the 130 weapon rows the palette byte takes values 0-8
plus 11, 13, 15, and forty SpriteIDs are shared by two or more items carrying DIFFERENT palette
bytes. That is exactly the classic FFT "one graphic, several palettes, several items" design.

THE CASE AGAINST IT, which is why this needs a live test and not an assertion. Measured on the
vanilla sheet: palettes 1 and 2 hold only five non-zero colours each, at slots 11-15, with slots
1-10 fully transparent, and of the 229 blobs on the sheet of 20px or more they can fully render
exactly 49, every one of which sits at y >= 211 in the swing-arc and sparkle rows. No weapon tile
anywhere on the sheet is renderable by palette 1 or 2. Yet 33 weapons claim palette 1 or 2. Under
a straight-index reading those 33 would be half-invisible ghosts, which they are not. No constant
offset remap rescues it either: the used values cover 0-8 contiguously, so every shift in -3..+3
lands at least one weapon on palette 1 or 2. So the byte is either inert in this remaster, or
remapped by a table nobody has found.

THE 2x2. Four swords, one battle. Two share a graphic and differ in palette byte; two share a
palette byte and differ in graphic. Whichever field is the selector, exactly one pair goes
different and the other pair goes same, so the answer is legible even if some third factor is
also in play.

    item  mod name    vanilla name    SpriteID  Palette byte
      19  Vagabond    Broadsword           12             0
      26  Flamberge   Sleep Blade          12             4     <- same graphic as 19
      21  Riposte     Iron Sword           14             2
      22  Claymore    Mythril Sword        15             2     <- same palette byte as 21

  PALETTE BYTE IS THE SELECTOR : 19 != 26  AND  21 == 22
  GRAPHIC IS THE SELECTOR      : 19 == 26  AND  21 != 22
  ALL FOUR THE SAME COLOUR     : one palette serves every sword; the map is coarse and the
                                 feature's grain collapses to "per sheet", not "per weapon"
  ANY OTHER PATTERN            : neither field alone; report the pattern, do not force a story

WHY THE COLOUR TABLE IS GENERATED AND NOT HAND-LISTED. The launch that proved the mechanism used
a hand-written list in which palette 10 was MAGENTA rgb(248,0,248) and palette 15 was ORCHID
rgb(248,80,248). Both are hue 300.0 EXACTLY. The measurement "hue median 300.0, spread 1.7
degrees" therefore proved the mechanism but could not name the palette, and "palette 15" went
into the record on an instrument that could not have told 15 from 10. This probe generates
sixteen hues evenly spaced 22.5 degrees apart at full saturation and value, and its selftest
asserts every code is its own nearest neighbour in hue. Full saturation everywhere on purpose:
the same live measurement showed daylight rendering preserves HUE essentially exactly (forged
300.0, measured 300.0, spread 1.7), while brightness is exactly what the engine's lighting moves,
so encoding the label in value the way the current census table does is encoding it in the one
channel the game is free to change.

NO GREY AND NO WHITE, ever, in a labelling palette: a flat grey blade at sprite scale reads as an
ordinary steel sword, so a working probe gets reported as "no change". This is a live suspect for
the 2026-06-01 false negative on this very file.

AND JUDGE IN DAYLIGHT. At night the engine rotates hue about 135 degrees, so the same bow core
reads hue 60 by day and 195 after dark. A night screenshot cannot name a palette. Weapons only
render during an attack animation, so every reading needs an actual swing.

USAGE:
  python lw289_palette_selector.py --plan               # the owner-facing script, no files touched
  python lw289_palette_selector.py --selftest           # pure checks, no game, no files
  python lw289_palette_selector.py <work_dir>           # extract + md5-gate + forge + previews
  python lw289_palette_selector.py <work_dir> --deploy  # ...and install into the live mods folder
  python lw289_palette_selector.py <work_dir> --deploy --hot   # ...even while the game is running
  python lw289_palette_selector.py --checklog           # did the game read OUR file last launch?
  python lw289_palette_selector.py --measure shot.png   # name the palettes in an owner screenshot
  python lw289_palette_selector.py --bytetest           # round 2: patch <Palette> + the controls
  python lw289_palette_selector.py --undo-bytes         # put the deployed table back

--hot exists because the loader re-reads the file from disk on EVERY request
(FFTPackFileOverrideStrategy.OnRequestRead does File.OpenRead per call, no cache) and the game
requests file 71 once per battle load. So a hot deploy between two battles in one session is
itself the test for "restart or battle load", which is still formally untested.

Undo: delete <mods>/prawl.fft.livingweapons/FFTIVC/data/enhanced/fftpack/. The next BuildLinked
wipes it too, so deploy probe files AFTER any BuildLinked run, never before.
"""
import colorsys
import os
import re
import shutil
import struct
import subprocess
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
# Single-source the extract + md5 gate + geometry constants from the round-12 forge rather than
# copying them, so a pac change breaks one file instead of two.
from lw251_wep_spr_forge import (  # noqa: E402
    FFTPACK_ID, LOGS, NAME, PAL_BYTES, SHEET_W, TOTAL_BYTES, load_vanilla, newest_log,
)

MODS_ENV = "RELOADEDIIMODS"
DEPLOY_SUB = os.path.join("prawl.fft.livingweapons", "FFTIVC", "data", "enhanced",
                          "fftpack", "unit")
# The loader copies the full request size (0x15000 = 86016) out of an ArrayPool rental it never
# clears, while reading only min(size, fileLength) bytes into it. A short file therefore leaves
# pool garbage in the tail and a long one gets read past vanilla. Ship exactly vanilla length.
EXACT_BYTES = TOTAL_BYTES

# The 2x2. Names are the LivingWeapons renames; vanilla names in the comments so the table can be
# checked against the modloader's own ItemData template dump, which uses vanilla names.
AB_PLAN = [
    # (item id, mod name, vanilla name, spriteId, paletteByte, role)
    (19, "Vagabond",  "Broadsword",    12, 0, "graphic pair A / palette 0"),
    (26, "Flamberge", "Sleep Blade",   12, 4, "graphic pair A / palette 4"),
    (21, "Riposte",   "Iron Sword",    14, 2, "palette pair B / graphic 14"),
    (22, "Claymore",  "Mythril Sword", 15, 2, "palette pair B / graphic 15"),
]


def codes16():
    """Sixteen labelling colours: hue = index * 22.5 degrees, full saturation, full value, as
    BGR555. Generated, never hand-listed, because a hand-list is how palette 10 and palette 15
    ended up sharing hue 300.0 in the round-12 instrument."""
    out = []
    for i in range(16):
        r, g, b = colorsys.hsv_to_rgb(i * 22.5 / 360.0, 1.0, 1.0)
        r5, g5, b5 = (max(0, min(31, round(c * 31))) for c in (r, g, b))
        code = r5 | (g5 << 5) | (b5 << 10)
        out.append((code or 1, i * 22.5))   # never 0: slot value 0 means transparent
    return out


CODES = codes16()


def _rgb8(code):
    return ((code & 31) * 8, ((code >> 5) & 31) * 8, ((code >> 10) & 31) * 8)


def _hue(rgb):
    h, s, v = colorsys.rgb_to_hsv(*(c / 255.0 for c in rgb))
    return h * 360.0, s, v


def _hue_gap(a, b):
    d = abs(a - b) % 360.0
    return min(d, 360.0 - d)


def forge(raw):
    """Flatten all sixteen palettes onto their own label colour. Slot 0 and any already-zero slot
    stay transparent, bit 15 is preserved, and the pixel block is not touched, so the ONLY thing
    that can change on screen is colour and it can only have come from this block."""
    pal = list(struct.unpack_from(f"<{PAL_BYTES // 2}H", raw, 0))
    changed = 0
    for p in range(16):
        for slot in range(1, 16):
            k = p * 16 + slot
            if pal[k] == 0:
                continue
            pal[k] = (pal[k] & 0x8000) | CODES[p][0]
            changed += 1
    return struct.pack(f"<{PAL_BYTES // 2}H", *pal) + raw[PAL_BYTES:], changed


def preview(work_dir, raw, tag):
    from PIL import Image
    pal = struct.unpack_from(f"<{PAL_BYTES // 2}H", raw, 0)
    pix = raw[PAL_BYTES:]
    h = len(pix) * 2 // SHEET_W
    im = Image.new("RGBA", (SHEET_W, h))
    px = im.load()
    for i in range(len(pix) * 2):
        b = pix[i // 2]
        n = (b & 0xF) if i % 2 == 0 else (b >> 4)
        x, y = i % SHEET_W, i // SHEET_W
        px[x, y] = (0, 0, 0, 0) if n == 0 else _rgb8(pal[n]) + (255,)
    path = os.path.join(work_dir, f"lw289_{tag}.png")
    im.save(path)
    return path


def plan():
    """The owner-facing script. Pre-registered readings, so no result can be re-interpreted after
    the fact into whatever we were hoping for."""
    print("LW-289 palette-selector 2x2. ONE battle, FOUR swings, DAYLIGHT map.\n")
    print("Setup: give four units one sword each and have each of them ATTACK once. Weapons only")
    print("render during an attack animation, so a unit that only moves tells us nothing.\n")
    for iid, mod_name, van_name, spr, pb, role in AB_PLAN:
        code, hue = CODES[pb]
        print(f"  item {iid:3}  {mod_name:10} (vanilla {van_name:14}) graphic {spr:2}  palette byte {pb:2}")
        print(f"             {role}")
        print(f"             IF the palette byte is the selector it renders "
              f"rgb{_rgb8(code)}, hue {hue:.1f}\n")
    print("Then send one daylight screenshot per swing, or one screenshot with all four visible.")
    print("Run --measure on each; the reading is numeric, not an eyeball.\n")
    print("IF ONLY TWO SWINGS ARE PRACTICAL, do 19 and 26. They share a graphic and their label")
    print("hues sit 90 degrees apart, so that one pair alone already separates 'the byte drives")
    print("it' from 'the graphic drives it'. Items 21 and 22 are the confirmation, not the test.\n")
    print("PRE-REGISTERED READINGS")
    print("  19 and 26 DIFFERENT, 21 and 22 SAME  -> the ItemData <Palette> byte IS the selector.")
    print("       The whole 130-weapon map then falls straight out of the vanilla table and no")
    print("       census is owed at all.")
    print("  19 and 26 SAME, 21 and 22 DIFFERENT  -> the GRAPHIC picks the palette. The byte is")
    print("       inert, and the map is per SpriteID, so the census is 71 graphics not 130 items.")
    print("  all four the SAME colour             -> one palette serves every sword. Grain is per")
    print("       sheet, and the honest feature promise shrinks a long way.")
    print("  anything else                        -> report the pattern verbatim. Do not force it")
    print("       into one of the three stories above.")
    print("\nBEFORE believing any of it, run --checklog. If the log does not say the game read OUR")
    print("file, the reading is void and means nothing about colour. That check is not optional:")
    print("skipping it is exactly what produced the retracted [g2d-clut-bank-override] row.")


# ---------------------------------------------------------------------------------------------
# ROUND 2: is the <Palette> byte WRITABLE and live?
#
# Round 1 (four swings, 2026-08-19, owner captures, measured not eyeballed) killed both simple
# hypotheses at once:
#     item 19 Vagabond   SpriteID 12  byte 0  -> renders palette 13
#     item 26 Flamberge  SpriteID 12  byte 4  -> renders palette 15
#     item 21 Riposte    SpriteID 14  byte 2  -> renders palette  3
#     item 22 Claymore   SpriteID 15  byte 2  -> renders palette 15
# Same graphic, different palettes: the GRAPHIC does not decide. Same byte, different palettes:
# the BYTE does not decide either, at least not as any pure function of itself. The assignment is
# per ITEM and it is not either of the two fields the classic table exposes.
#
# The sanity check that says these are real assignments and not noise: the palettes each weapon
# landed on suit it. Vanilla palette 13 is greys, blue-steel and gold, which is a Broadsword.
# Palette 15 is grey-greens, blues and olives, which is exactly mythril. Palette 3 is warm browns
# with a red ramp, which is an iron blade with a leather grip.
#
# WHY THIS ROUND MATTERS MORE THAN THE MAP. For the feature we do not actually need to know which
# palette an item currently uses IF we can assign one. Writing beats reading. So round 2 asks the
# only question left that changes the design: does setting <Palette> in a table override move the
# colour on screen?
#
# THE CONTROLS ARE THE POINT. A "nothing changed" result is worthless unless we know the override
# reached the game at all, which is the same trap that produced the retracted g2d row.
#   item 21 gets its SpriteID moved from 14 to 33 (the axe). SpriteID is independently known to be
#          live (a category override that crosses graphic families renders the weapon offset mid
#          swing unless the sprite is repointed, tools/generate.py). So Riposte MUST come out a
#          different SHAPE. If it does not, our table override never landed and the whole round is
#          void. Its colour this round is uninterpretable; ignore it.
#   item 22 is left completely alone. It must still render palette 15, rose pink (255,0,122). If it
#          moves, something other than our edit is in play and the round is void.
# ROUND 1 BASELINE, measured from owner captures 2026-08-19 (serve gate: 4 modded reads, 0 game
# reads; overbright factor 1.232 solved from the one unclamped channel, max channel error 7/255).
ROUND1 = {19: 13, 26: 15, 21: 3, 22: 15}

# ROUND 2 (same four swings, table override log-confirmed at cell level:
# "[ItemData] prawl.fft.livingweapons changed ID 19 (Palette, value: 8)" etc, 94 changes applied):
#   19  byte 0 -> 8   palette 13 -> 14   MOVED
#   26  byte 4 -> 0   palette 15 -> 15   unchanged
#   21  SpriteID 14 -> 33               shape UNCHANGED and palette unchanged
#   22  untouched                        palette 15, control holds
# Two findings, one of which retires a repo claim:
#   SpriteID IS INERT for the drawn battle weapon. The write reached game memory (logged) and the
#   axe graphic never appeared. The "SpriteID picks the drawn weapon graphic" comment in
#   tools/generate.py is a doc-comment claim with no ledger row behind it and this contradicts it.
#   THE PALETTE BYTE IS LIVE BUT ONLY BIT 3 DID ANYTHING. The one treatment that moved a colour
#   (0 -> 8) is the only one that flipped bit 3; 4 -> 0 left bit 3 at zero and moved nothing.
#   The vagabond shift is not an exposure artifact: mean grass green is 61.7 in round 1 versus
#   60.8 in round 2, so the overbright factor is the same and palette 13 (255,0,255) versus
#   palette 14 (255,0,228) is a real separation.
# WORKING HYPOTHESIS: palette = base(item) + (PaletteByte >> 3), base from an unknown per-item
# source. Round 3 tests it and is designed so all three live stories give different pictures.
TREATMENT_PALETTE = {19: 8, 21: 8, 22: 8, 26: 8}   # ROUND 3: every test item to byte 8
CONTROL_SPRITE = {}                                 # SpriteID control retired, it is inert
BAK_SUFFIX = ".lw289bak"


def round3_predictions():
    """What each live story predicts for round 3, so the reading is fixed before the pictures."""
    out = {}
    for iid, base in sorted(ROUND1.items()):
        out[iid] = {
            "base+bit3": (base + 1) % 16,   # wrap is a guess; a clamp would show 15 instead
            "byte-is-index": 8,
            "byte-inert": base,
        }
    return out


def _deployed_itemdata():
    mods = os.environ.get(MODS_ENV) or sys.exit(f"{MODS_ENV} not set")
    p = os.path.join(mods, "prawl.fft.livingweapons", "FFTIVC", "tables", "enhanced",
                     "ItemData.xml")
    if not os.path.isfile(p):
        sys.exit(f"no deployed ItemData.xml at {p}; run BuildLinked first")
    return p


def bytetest():
    """Patch the DEPLOYED ItemData.xml (a generated artifact's deployed copy, never the repo's)
    with the round-2 treatment and controls. Reversible with --undo-bytes, and the next
    BuildLinked wipes it anyway, which is why probe files go in AFTER a build and never before."""
    path = _deployed_itemdata()
    bak = path + BAK_SUFFIX
    if os.path.isfile(bak):
        sys.exit(f"{bak} already exists; run --undo-bytes first so we never stack two patches")
    raw = open(path, encoding="utf-8").read()
    for iid in list(TREATMENT_PALETTE) + list(CONTROL_SPRITE):
        if f"<Id>{iid}</Id>" in raw:
            sys.exit(f"item {iid} is already a row in the deployed table; this probe only knows "
                     f"how to ADD rows, and editing an existing one would silently change what "
                     f"the mod ships. Pick different items or extend the probe.")
    shutil.copy2(path, bak)
    rows = []
    for iid, pal in sorted(TREATMENT_PALETTE.items()):
        rows.append(f"    <Item>\n      <Id>{iid}</Id> <!-- LW-289 probe: palette treatment -->\n"
                    f"      <Palette>{pal}</Palette>\n    </Item>\n")
    for iid, spr in sorted(CONTROL_SPRITE.items()):
        rows.append(f"    <Item>\n      <Id>{iid}</Id> <!-- LW-289 probe: SHAPE control -->\n"
                    f"      <SpriteID>{spr}</SpriteID>\n    </Item>\n")
    marker = "  </Entries>"
    if marker not in raw:
        sys.exit("deployed ItemData.xml has no </Entries>; refusing to guess where rows go")
    open(path, "w", encoding="utf-8").write(raw.replace(marker, "".join(rows) + marker, 1))
    print(f"patched {path}")
    print(f"backup  {bak}   (restore with --undo-bytes)")
    print()
    print("ROUND 3 SCRIPT: relaunch, ONE daylight battle, all four swords swung IN THAT SAME")
    print("battle. Round 1 and round 2 each logged four separate file-71 reads, so they may have")
    print("been four separate battles; keeping all four swings inside one battle removes any")
    print("chance that the game allocates palettes per battle rather than per item.")
    print()
    print("Every test item now carries Palette byte 8, so the three live stories separate cleanly:")
    print()
    pred = round3_predictions()
    names = {19: "Vagabond", 26: "Flamberge", 21: "Riposte", 22: "Claymore"}
    print(f"  {'item':14} {'round1':>7} {'base+bit3':>22} {'byte-is-index':>22} {'byte-inert':>22}")
    for iid in (19, 21, 22, 26):
        p = pred[iid]
        f = lambda k: f"{p[k]:2} {tuple(min(255, round(v * 1.232)) for v in _rgb8(CODES[p[k]][0]))}"
        print(f"  {iid:2} {names[iid]:11} {ROUND1[iid]:>7} {f('base+bit3'):>22} "
              f"{f('byte-is-index'):>22} {f('byte-inert'):>22}")
    print()
    print("PRE-REGISTERED READINGS")
    print("  each weapon lands one palette ABOVE its round-1 palette -> the byte adds bit 3 and")
    print("       the working hypothesis holds. Items 22 and 26 sit at base 15, so they also tell")
    print("       us whether 15+1 WRAPS to palette 0 (red) or CLAMPS at 15 (rose pink), which is")
    print("       a free extra answer.")
    print("  all four turn the SAME colour, cyan -> the byte is a straight palette index after")
    print("       all and round 2 was misread. Best possible outcome: we can assign any palette")
    print("       to any item and the feature needs no census.")
    print("  nothing moves except item 19 staying at 14 -> the byte is not additive; something")
    print("       about item 19 alone is special and the hypothesis dies. Report and rethink.")
    print("  anything else -> report the pattern verbatim, do not force it into a story.")


def undo_bytes():
    path = _deployed_itemdata()
    bak = path + BAK_SUFFIX
    if not os.path.isfile(bak):
        sys.exit(f"no {bak}; nothing to undo")
    shutil.copy2(bak, path)
    os.remove(bak)
    print(f"restored {path} from backup and removed the backup")


def checklog():
    log = newest_log()
    if not log:
        sys.exit(f"no Reloaded logs under {LOGS}")
    print(f"log: {os.path.basename(log)}")
    modded = plain = 0
    times = []
    with open(log, encoding="utf-8", errors="ignore") as fh:
        for line in fh:
            if f"file {FFTPACK_ID} -> unit/{NAME}" not in line or "Accessing" not in line:
                continue
            stamp = re.match(r"\[(\d\d:\d\d:\d\d)\]", line)
            times.append(stamp.group(1) if stamp else "??:??:??")
            if "modded file" in line:
                modded += 1
            else:
                plain += 1
    print(f"  served from OUR override : {modded}")
    print(f"  served from the GAME copy: {plain}")
    print(f"  read at                  : {', '.join(times) if times else '(never)'}")
    if modded and not plain:
        print("LIVE: every read this launch came from our file. A vanilla-looking weapon is now a")
        print("REAL negative about the palette block, not a channel fault.")
    elif modded and plain:
        print("MIXED: some reads came from the game's own copy. Treat the visual as inconclusive")
        print("and work out which read fed the battle you photographed before concluding.")
    elif plain:
        print("NOT SERVED: the game read its own copy. Conclude NOTHING about colour. Check the")
        print("deploy path and that the mod is enabled.")
    else:
        print("NOT READ AT ALL: no battle was loaded this launch (file 71 is read per battle).")
    if len(times) > 1:
        print(f"  ({len(times)} reads in one launch confirms per-battle reading, not per-launch.)")


def measure(path):
    """Name the palettes present in an owner screenshot. Numeric, because the whole reason this
    probe exists is that an eyeball could not tell hue 300 from hue 300."""
    from PIL import Image
    im = Image.open(path).convert("RGB")
    w, h = im.size
    buckets = {i: [] for i in range(16)}
    for y in range(h):
        for x in range(w):
            hue, sat, val = _hue(im.getpixel((x, y)))
            # Only strongly saturated, reasonably bright pixels can be a label colour. Game art
            # and terrain sit well below this; the forged codes are all sat 1.0 val 1.0.
            if sat < 0.55 or val < 0.35:
                continue
            best = min(range(16), key=lambda i: _hue_gap(hue, CODES[i][1]))
            if _hue_gap(hue, CODES[best][1]) <= 11.25:   # half the 22.5 degree spacing
                buckets[best].append((x, y, hue))
    print(f"{os.path.basename(path)}  {w}x{h}")
    hits = 0
    for i in range(16):
        pts = buckets[i]
        if len(pts) < 25:            # below this, JPEG fringing and UI accents dominate
            continue
        hits += 1
        hues = sorted(p[2] for p in pts)
        xs = [p[0] for p in pts]
        ys = [p[1] for p in pts]
        lo, hi = hues[len(hues) // 10], hues[(len(hues) * 9) // 10]
        print(f"  palette {i:2} (forged hue {CODES[i][1]:5.1f}): {len(pts):6} px  "
              f"box ({min(xs)},{min(ys)})-({max(xs)},{max(ys)})  "
              f"hue median {hues[len(hues) // 2]:6.1f}  10-90 spread {hi - lo:5.1f}")
    if not hits:
        print("  NO label colour found. Either the shot has no swing frame in it, or the file was")
        print("  not served, or this is a night map and every hue is rotated. Check --checklog and")
        print("  the map's time of day before reading anything into it.")
    else:
        print("  A tight spread (a few degrees) is a flat forged palette. A spread of tens of")
        print("  degrees is ordinary shaded art that happens to sit near a label hue; do not")
        print("  count it as a hit.")


def selftest():
    assert len(CODES) == 16, "need one code per palette"
    assert len({c for c, _ in CODES}) == 16, "codes are not distinct"
    assert all(1 <= c <= 0x7FFF for c in (c for c, _ in CODES)), "a code is zero or sets bit 15"
    for i, (code, want) in enumerate(CODES):
        got, sat, val = _hue(_rgb8(code))
        assert _hue_gap(got, want) < 4.0, f"code {i} hue {got} strays from {want}"
        assert sat > 0.9, f"code {i} is not saturated ({sat}); grey reads as steel in game"
        assert val > 0.9, f"code {i} is dark ({val}); brightness is the channel lighting moves"
        near = min((j for j in range(16) if j != i),
                   key=lambda j: _hue_gap(_hue(_rgb8(CODES[j][0]))[0], got))
        assert _hue_gap(_hue(_rgb8(CODES[near][0]))[0], got) > 15.0, \
            f"codes {i} and {near} are within 15 degrees; that is the round-12 failure again"
    ids = [p[0] for p in AB_PLAN]
    assert len(set(ids)) == 4, "the 2x2 must be four distinct items"
    by_spr = {}
    by_pal = {}
    for iid, _, _, spr, pb, _ in AB_PLAN:
        by_spr.setdefault(spr, []).append(iid)
        by_pal.setdefault(pb, []).append(iid)
    assert any(len(v) == 2 for v in by_spr.values()), "no pair shares a graphic"
    assert any(len(v) == 2 for v in by_pal.values()), "no pair shares a palette byte"
    shared_spr = next(v for v in by_spr.values() if len(v) == 2)
    shared_pal = next(v for v in by_pal.values() if len(v) == 2)
    assert set(shared_spr) != set(shared_pal), "the two pairs must be different pairs"
    # The graphic pair must differ in palette byte and the palette pair must differ in graphic,
    # otherwise the 2x2 has a collapsed cell and one branch of the reading is unreachable.
    pal_of = {p[0]: p[4] for p in AB_PLAN}
    spr_of = {p[0]: p[3] for p in AB_PLAN}
    assert pal_of[shared_spr[0]] != pal_of[shared_spr[1]], "graphic pair shares its palette byte"
    assert spr_of[shared_pal[0]] != spr_of[shared_pal[1]], "palette pair shares its graphic"
    # And the two label colours the graphic pair would wear must be tellable apart on a screen.
    ha = CODES[pal_of[shared_spr[0]]][1]
    hb = CODES[pal_of[shared_spr[1]]][1]
    assert _hue_gap(ha, hb) > 60.0, \
        f"the deciding pair would render {ha} vs {hb} degrees apart; pick items further apart"

    fake = struct.pack("<256H", *([0, 0x8000, 0x1234, 0] + [0x7FFF] * 12) * 16) + bytes(64)
    out, changed = forge(fake)
    assert len(out) == len(fake), "forge changed the file length"
    assert out[PAL_BYTES:] == fake[PAL_BYTES:], "forge touched the pixel block"
    got = struct.unpack_from("<256H", out, 0)
    for p in range(16):
        assert got[p * 16] == 0 and got[p * 16 + 3] == 0, "a transparent slot was painted"
        assert got[p * 16 + 1] & 0x8000, "bit 15 not preserved"
        lows = {v & 0x7FFF for v in got[p * 16 + 1:p * 16 + 16] if v}
        assert lows == {CODES[p][0]}, f"palette {p} is not flat on its own code"
    assert changed == 16 * 14, f"expected 224 painted slots, got {changed}"
    print("selftest OK")
    print("  16 label hues, min separation "
          f"{min(_hue_gap(CODES[i][1], CODES[j][1]) for i in range(16) for j in range(16) if i != j):.1f} degrees")
    print(f"  deciding pair {shared_spr[0]} vs {shared_spr[1]} would render "
          f"{ha:.1f} vs {hb:.1f} degrees apart")


def deploy(work_dir, hot):
    running = "fft_enhanced.exe" in subprocess.run(
        ["tasklist", "/FI", "IMAGENAME eq fft_enhanced.exe"],
        capture_output=True, text=True).stdout
    if running and not hot:
        sys.exit("fft_enhanced.exe is RUNNING; close it and rerun, or pass --hot to swap live")
    mods = os.environ.get(MODS_ENV) or sys.exit(f"{MODS_ENV} not set")
    dst = os.path.join(mods, DEPLOY_SUB)
    os.makedirs(dst, exist_ok=True)
    shutil.copy2(os.path.join(work_dir, NAME), os.path.join(dst, NAME))
    print(f"deployed {NAME} -> {dst}")
    if running:
        print("HOT SWAP, game still running. The loader re-opens this path on every request")
        print("(FFTPackFileOverrideStrategy.OnRequestRead, File.OpenRead per call, no cache) and")
        print("the game requests file 71 once per battle load. So: finish or leave the current")
        print("battle, start ANOTHER one, and swing. If the new colours appear, that also settles")
        print("the open 'restart or battle load' question in favour of battle load.")
    else:
        print("Launch, load a DAYLIGHT battle, run the four swings from --plan, then --checklog.")


def main():
    if "--plan" in sys.argv:
        plan()
        return
    if "--selftest" in sys.argv:
        selftest()
        return
    if "--checklog" in sys.argv:
        checklog()
        return
    if "--bytetest" in sys.argv:
        bytetest()
        return
    if "--undo-bytes" in sys.argv:
        undo_bytes()
        return
    if "--measure" in sys.argv:
        measure(sys.argv[sys.argv.index("--measure") + 1])
        return
    selftest()
    if len(sys.argv) < 2 or sys.argv[1].startswith("--"):
        sys.exit(__doc__.split("USAGE:")[1])
    work_dir = sys.argv[1]
    os.makedirs(work_dir, exist_ok=True)
    raw = load_vanilla(work_dir)
    out, changed = forge(raw)
    assert len(out) == EXACT_BYTES, f"forged file is {len(out)}B, must be exactly {EXACT_BYTES}"
    print(f"vanilla {NAME}: {len(raw)} bytes, md5 gate passed")
    print(f"flattened all 16 palettes ({changed} colours); pixel block untouched: "
          f"{out[PAL_BYTES:] == raw[PAL_BYTES:]}")
    for i, (code, hue) in enumerate(CODES):
        print(f"  palette {i:2} -> rgb{_rgb8(code)} hue {hue:5.1f}")
    open(os.path.join(work_dir, NAME), "wb").write(out)
    print("previews:", preview(work_dir, raw, "vanilla"), preview(work_dir, out, "forged"))
    if "--deploy" not in sys.argv:
        print("dry run only; rerun with --deploy to install")
        return
    deploy(work_dir, "--hot" in sys.argv)


if __name__ == "__main__":
    main()
