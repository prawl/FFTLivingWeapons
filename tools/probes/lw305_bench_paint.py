#!/usr/bin/env python
"""LW-305: paint a Weapon Colour Bench export straight into the running game.

WHAT THIS IS FOR. The bench (the owner's HSL slider artifact) authors a weapon's sixteen palette
entries by hand and exports them. LW-251 promoted that export to data/weapon_colors.json (the
hand-authored source both this probe and the meta.json bake now read) -- a future bench re-export
drops there, not back into tools/probes/. This script is the shortest path from that file to a
weapon actually looking different on screen: no rebuild, no deploy, no table edit. Write the
palette, swing the weapon, look.

MECHANISM, already proven and unchanged ([resident-weapon-palette-buffer]):
  - the renderer reads a resident 512-byte workspace PER DRAW, so a write shows up immediately
  - there are TWO banks, 0x200 apart, and both must be written or the change flickers
  - a battle LOAD refreshes the workspace from the loaded file and reverts everything, so this is
    a LIVE prototype only; re-run after every load
  - entry 0 is the transparency slot and is NEVER written
  - bit 15 is a per-entry flag carried from whatever is currently in the slot; drop it and the
    sprite renders as a coloured rectangle instead of a weapon

COLOUR IS PER PALETTE, NOT PER WEAPON. There are 13 palettes for 127 weapons, so painting Materia
Blade's palette 8 repaints every other weapon sharing palette 8 at the same time. That is a known
wall ([lw289-palette-assignment-walled]), not a bug in this script. `roommates` lists who else is
on the palette so a review knows what it is looking at.

The bench exports 8-bit RGB already quantised to BGR555 (its own note says so). `check` proves
that offline: every channel must round-trip through 5 bits unchanged. If it ever stops being true
the bench changed, and the colour on screen would silently differ from the colour on the slider.

USAGE:
  python lw305_bench_paint.py check                 # offline: validate weapon_colors.json, no game needed
  python lw305_bench_paint.py codes "Materia Blade" # the 16 codes this weapon would write
  python lw305_bench_paint.py roommates 8           # every weapon sharing palette 8
  python lw305_bench_paint.py paint "Materia Blade" # LIVE: write it, needs a battle on screen
  python lw305_bench_paint.py slots 8               # LIVE: is the weapon on screen really on pal 8?
  python lw305_bench_paint.py keychart              # LIVE: one colour per palette; swing to read it off
  python lw305_bench_paint.py saw "Dagger" cyan     # record what a swing actually looked like
  python lw305_bench_paint.py tally                 # every observation, disagreements first
  python lw305_bench_paint.py render "Materia Blade"  # offline: what the bench SHOULD look like
  python lw305_bench_paint.py restore               # LIVE: re-read banks from... see note below

There is no `restore`: nothing on disk holds the pre-write bytes, and a battle load restores the
vanilla palette by itself. Reload the battle to undo.
"""
import json
import pathlib
import struct
import sys

HERE = pathlib.Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))

# LW-251: promoted to data/ as a hand-authored pipeline source (single source of truth --
# this probe and tools/gen_living_weapon_meta.py now read the same files).
EXPORT = HERE.parents[1] / "data" / "weapon_colors.json"
BANKS = [0x140D35750, 0x140D35950]
PAL_STRIDE = 32          # 16 entries x 2 bytes


def q5(v8):
    """8-bit channel -> 5-bit, using the bench's own rounding."""
    return max(0, min(31, int(round(v8 * 31 / 255))))


def e5(v5):
    """5-bit -> 8-bit the way the bench does it. FLOOR, not round: the bench emits 98 for 12,
    where rounding would emit 99. Getting this backwards makes every colour look off by one and
    reads as a corrupt export."""
    return (v5 * 255) // 31


def code_of(rgb):
    r, g, b = (q5(c) for c in rgb)
    return r | (g << 5) | (b << 10)


def load():
    with EXPORT.open(encoding="utf8") as fh:
        return json.load(fh)


def find(doc, key):
    key = str(key).strip().lower()
    hits = [w for w in doc["weapons"] if str(w["id"]) == key or w["name"].lower() == key]
    if not hits:
        hits = [w for w in doc["weapons"] if key in w["name"].lower()]
    if not hits:
        sys.exit(f"no weapon matches {key!r}")
    if len(hits) > 1:
        sys.exit("ambiguous: " + ", ".join(f"{w['id']} {w['name']}" for w in hits[:8]))
    return hits[0]


# LW-251: promoted alongside EXPORT (see above) -- the same data/ file the meta.json bake reads.
OVERRIDES = HERE.parents[1] / "data" / "weapon_palette_overrides.json"


def true_palette(w):
    """The palette a weapon ACTUALLY draws from.

    The bench and the map both carry a palette number decoded from BATTLE.BIN, and it is right
    for every weapon tested but one. Where a live swing has contradicted it the correction lives
    in data/weapon_palette_overrides.json, with its evidence, because the map itself is generated
    and regenerating it would silently discard a hand fix.
    """
    if OVERRIDES.exists():
        ov = json.loads(OVERRIDES.read_text(encoding="utf8"))["overrides"].get(str(w["id"]))
        if ov:
            return ov["trulyUses"], ov
    return w["pal"], None


def codes_for(w):
    """Entries 1..15 as (index, code, rgb) triples. Entry 0 is deliberately absent."""
    out = []
    for i in range(1, 16):
        ent = w["colours"][str(i)]
        out.append((i, code_of(ent["rgb"]), tuple(ent["rgb"]), bool(ent.get("semiTransparent"))))
    return out


def cmd_check():
    doc = load()
    bad = 0
    for w in doc["weapons"]:
        for i in range(1, 16):
            ent = w["colours"].get(str(i))
            if ent is None:
                print(f"  MISSING entry {i} on {w['id']} {w['name']}")
                bad += 1
                continue
            rgb = ent["rgb"]
            rt = [e5(q5(c)) for c in rgb]
            if rt != list(rgb):
                print(f"  NOT 5-BIT  {w['id']:>3} {w['name']:<20} entry {i:>2} {rgb} -> {rt}")
                bad += 1
            if ent["hex"].lower() != "#%02x%02x%02x" % tuple(rgb):
                print(f"  HEX/RGB DISAGREE {w['id']} {w['name']} entry {i}")
                bad += 1
    pals = sorted({w["pal"] for w in doc["weapons"]})
    print(f"{len(doc['weapons'])} weapons, palettes {pals}")
    print("weapon_colors.json is BGR555-clean" if not bad else f"{bad} PROBLEMS")
    return 1 if bad else 0


def cmd_codes(key):
    doc = load()
    w = find(doc, key)
    print(f"{w['id']} {w['name']}  cat={w['cat']}  palette={w['pal']}  tile={w['tile']}")
    print(f"painted by its own drawing: {w['paintedIndices']}")
    for i, c, rgb, semi in codes_for(w):
        mark = " *own" if i in w["paintedIndices"] else ""
        semi = " semiTransparent" if semi else ""
        print(f"  {i:>2}  {c:#06x}  rgb{rgb}{semi}{mark}")


def cmd_roommates(pal):
    doc = load()
    pal = int(pal)
    share = [w for w in doc["weapons"] if w["pal"] == pal]
    print(f"palette {pal} is worn by {len(share)} weapons:")
    for w in share:
        print(f"  {w['id']:>3}  {w['name']:<22} {w['cat']}")


# Fifteen mutually distant colours, reused from lw301_slotmap_probe. Painting these into ONE
# palette answers a question a screenshot cannot: whether the weapon on screen is actually wearing
# the palette we think it is. A weapon that does not change is not on that palette, however
# plausible the colours it is already showing look.
SLOT_RGB5 = {
    1:  (31, 0, 0),   2:  (31, 16, 0),  3:  (31, 31, 0),  4:  (16, 31, 0),  5:  (0, 31, 0),
    6:  (0, 31, 16),  7:  (0, 31, 31),  8:  (0, 16, 31),  9:  (0, 0, 31),   10: (16, 0, 31),
    11: (31, 0, 31),  12: (31, 0, 16),  13: (20, 10, 4),  14: (10, 20, 26), 15: (26, 22, 14),
}


def cmd_slots(pal):
    """Paint one palette's 15 slots 15 different colours: an identity test, not a colour test."""
    import battle_cheats as bc
    bc._require_game()
    pal = int(pal)
    for base in BANKS:
        tgt = base + pal * PAL_STRIDE
        raw = bc.rpm(tgt, PAL_STRIDE)
        if not raw:
            print(f"  bank {base:#x} READ FAILED")
            continue
        cur = list(struct.unpack("<16H", raw))
        new = list(cur)
        for i, (r, g, b) in SLOT_RGB5.items():
            new[i] = (cur[i] & 0x8000) | (r | (g << 5) | (b << 10))
        print(f"  bank {base:#x} {'written' if bc.wpm(tgt, struct.pack('<16H', *new)) else 'FAILED'}")
    print(f"palette {pal} is now a rainbow. If the weapon on screen did NOT change, it is not on")
    print("this palette. Slot legend:")
    for i, (r, g, b) in SLOT_RGB5.items():
        print(f"   slot {i:>2}  rgb{(r << 3, g << 3, b << 3)}")


def cmd_render(key, out="lw305_render.png"):
    """Render this weapon's own tile twice: under the VANILLA palette and under the bench's.

    This is the offline half of the loop and it answers the question a screenshot cannot ask on
    its own: what SHOULD the paint have looked like. If the vanilla and bench panels are near
    identical, "no change on screen" is not evidence the write failed.
    """
    from PIL import Image
    import lw301_palette_transform as tf
    doc = load()
    w = find(doc, key)
    raw = tf.load_sheet()
    pix = raw[512:512 + 32768]
    W = 256
    idx = [[0] * W for _ in range(W)]
    for i in range(len(pix) * 2):
        byte = pix[i // 2]
        idx[i // W][i % W] = (byte & 0x0F) if i % 2 == 0 else (byte >> 4)
    boxes = {b["i"]: b for b in json.loads((HERE / "lw301_sprite_boxes.json").read_text(encoding="utf8"))}
    box = boxes[w["tile"]]
    bench = [0] + [c for _i, c, _rgb, _s in codes_for(w)]
    Z = 8
    panels = []
    for pal, codes in (("vanilla", tf.palette_of(raw, w["pal"])), ("bench", bench)):
        im = Image.new("RGB", (box["w"] * Z, box["h"] * Z), (40, 44, 50))
        for yy in range(box["h"]):
            for xx in range(box["w"]):
                v = idx[box["y"] + yy][box["x"] + xx]
                if not v:
                    continue
                # tf.bgr555_to_rgb returns 0..1 floats; expand with the bench's own floor rule
                # so this render and the slider agree on what a code looks like.
                code = codes[v] & 0x7FFF
                r, g, b = (e5(code & 31), e5((code >> 5) & 31), e5((code >> 10) & 31))
                for dy in range(Z):
                    for dx in range(Z):
                        im.putpixel((xx * Z + dx, yy * Z + dy), (r, g, b))
        panels.append((pal, im))
    gap = 24
    sheet = Image.new("RGB", (sum(p.width for _n, p in panels) + gap, panels[0][1].height), (40, 44, 50))
    x = 0
    for _n, im in panels:
        sheet.paste(im, (x, 0))
        x += im.width + gap
    sheet.save(out)
    print(f"{w['name']}  tile {w['tile']}  palette {w['pal']}  ->  {out}  (left vanilla, right bench)")


# Sixteen flat colours, one per palette: sixteen evenly spaced HUES, never a light/dark pair.
# The first version of this chart used dark red / dark green / dark blue for palettes 12-14 and
# that made three palettes unreadable: the renderer tints and smooths, so a screenshot can only
# be read brightness-invariantly, and brightness is the ONLY thing separating red from dark red. This is
# the cheap way to audit the weapon->palette map: paint the chart once, then every weapon that gets
# swung reports its own palette by what colour it comes out. Materia Blade is why this exists: its
# map row says palette 8, the screen says palette 0, and nothing short of a swing can tell.
PALETTE_KEY = {
    0: ("red", (31, 0, 0)),
    1: ("vermilion", (31, 12, 0)),
    2: ("orange", (31, 23, 0)),
    3: ("amber", (27, 31, 0)),
    4: ("yellow-green", (16, 31, 0)),
    5: ("chartreuse", (4, 31, 0)),
    6: ("green", (0, 31, 8)),
    7: ("emerald", (0, 31, 19)),
    8: ("cyan", (0, 31, 31)),
    9: ("sky", (0, 19, 31)),
    10: ("azure", (0, 8, 31)),
    11: ("indigo", (4, 0, 31)),
    12: ("violet", (16, 0, 31)),
    13: ("purple", (27, 0, 31)),
    14: ("magenta", (31, 0, 23)),
    15: ("rose", (31, 0, 12)),
}


OBSERVED = HERE / "lw305_observed_palettes.json"
COLOUR_TO_PAL = None  # built on first use from PALETTE_KEY


def cmd_saw(key, colour):
    """Record what colour a weapon actually swung, and diff it against the map on the spot.

    The map is lw289_weapon_palette_map.json, decoded offline from BATTLE.BIN's two nibbles. This
    file is the LIVE counter-evidence: one row per weapon somebody actually swung under the key
    chart. Where the two disagree the screen wins, because the screen is the thing players see.
    """
    global COLOUR_TO_PAL
    COLOUR_TO_PAL = {name: pal for pal, (name, _rgb) in PALETTE_KEY.items()}
    colour = colour.strip().lower()
    if colour.isdigit():
        # A bare palette number is accepted so observations taken under an EARLIER key chart can
        # still be banked: the colour names change when the chart is redesigned, the palette
        # numbers never do.
        seen = int(colour)
        colour = PALETTE_KEY[seen][0]
    elif colour in COLOUR_TO_PAL:
        seen = COLOUR_TO_PAL[colour]
    else:
        sys.exit("colour must be a palette number or one of: " + ", ".join(COLOUR_TO_PAL))
    doc = load()
    w = find(doc, key)
    rows = json.loads(OBSERVED.read_text(encoding="utf8")) if OBSERVED.exists() else {}
    mapped = {r["id"]: r for r in json.loads(
        (HERE / "lw289_weapon_palette_map.json").read_text(encoding="utf8"))}.get(w["id"], {})
    rows[str(w["id"])] = {
        "name": w["name"], "cat": w["cat"], "observedPalette": seen, "observedColour": colour,
        "mapWeaponPalette": mapped.get("weaponPalette"),
        "mapEffectPalette": mapped.get("effectPalette"),
        "graphic": mapped.get("graphic"),
    }
    OBSERVED.write_text(json.dumps(rows, indent=1, sort_keys=True), encoding="utf8")
    agree = seen == mapped.get("weaponPalette")
    print(f"{w['name']:<22} swung {colour:<10} = palette {seen:<2} "
          f"map says {mapped.get('weaponPalette')}  {'AGREES' if agree else 'DISAGREES'}")
    ok = sum(1 for r in rows.values() if r["observedPalette"] == r["mapWeaponPalette"])
    print(f"  running tally: {ok}/{len(rows)} agree with the map")


def cmd_tally():
    """Every weapon observed so far, disagreements first."""
    if not OBSERVED.exists():
        sys.exit("nothing recorded yet")
    rows = json.loads(OBSERVED.read_text(encoding="utf8"))
    bad = [r for r in rows.values() if r["observedPalette"] != r["mapWeaponPalette"]]
    good = [r for r in rows.values() if r["observedPalette"] == r["mapWeaponPalette"]]
    for label, group in (("DISAGREES", bad), ("agrees", good)):
        for r in sorted(group, key=lambda x: x["name"]):
            print(f"  {label:<9} {r['name']:<22} {r['cat']:<12} saw {r['observedPalette']:>2}  "
                  f"map X={r['mapWeaponPalette']} Y={r['mapEffectPalette']} graphic={r['graphic']}")
    print("")
    print(f"{len(good)}/{len(rows)} agree, {len(bad)} disagree")


def cmd_keychart():
    """One flat colour per palette, all 16. Swing a weapon and its colour names its palette."""
    import battle_cheats as bc
    bc._require_game()
    for pal, (_name, (r, g, b)) in PALETTE_KEY.items():
        c = r | (g << 5) | (b << 10)
        for base in BANKS:
            tgt = base + pal * PAL_STRIDE
            raw = bc.rpm(tgt, PAL_STRIDE)
            if not raw:
                continue
            cur = list(struct.unpack("<16H", raw))
            bc.wpm(tgt, struct.pack("<16H", *([cur[0]] + [(cur[i] & 0x8000) | c for i in range(1, 16)])))
    print("palette key painted. Swing a weapon; the colour it comes out IS its palette.")
    for pal, (name, _rgb) in PALETTE_KEY.items():
        print(f"   palette {pal:>2}  {name}")


def cmd_flat(pal, r5, g5, b5):
    """Flat-fill one palette. Deliberately destroys the shading: this is an IDENTITY test, where
    the only question is which palette a weapon answers to, and a flat colour is unmistakable."""
    import battle_cheats as bc
    bc._require_game()
    pal = int(pal)
    c = int(r5) | (int(g5) << 5) | (int(b5) << 10)
    for base in BANKS:
        tgt = base + pal * PAL_STRIDE
        raw = bc.rpm(tgt, PAL_STRIDE)
        if not raw:
            continue
        cur = list(struct.unpack("<16H", raw))
        new = [cur[0]] + [(cur[i] & 0x8000) | c for i in range(1, 16)]
        bc.wpm(tgt, struct.pack("<16H", *new))
    print(f"palette {pal} flattened to rgb5({r5},{g5},{b5})")


def cmd_paint(key):
    import battle_cheats as bc
    bc._require_game()
    doc = load()
    w = find(doc, key)
    entries = codes_for(w)
    pal, ov = true_palette(w)
    if ov:
        print(f"OVERRIDE: the map says palette {ov['mapSays']} but a live swing says {pal}")
    print(f"painting {w['name']} into palette {pal}, both banks")
    wrote = 0
    for base in BANKS:
        tgt = base + pal * PAL_STRIDE
        raw = bc.rpm(tgt, PAL_STRIDE)
        if not raw:
            print(f"  bank {base:#x} READ FAILED")
            continue
        cur = list(struct.unpack("<16H", raw))
        new = list(cur)
        for i, c, _rgb, _semi in entries:
            new[i] = (cur[i] & 0x8000) | c      # carry the flag bit, never invent it
        if bc.wpm(tgt, struct.pack("<16H", *new)):
            wrote += 1
            back = bc.rpm(tgt, PAL_STRIDE)
            same = back == struct.pack("<16H", *new)
            print(f"  bank {base:#x} written, read-back {'matches' if same else 'DIFFERS'}")
        else:
            print(f"  bank {base:#x} WRITE FAILED")
    print(f"{wrote}/{len(BANKS)} banks")
    share = [x["name"] for x in doc["weapons"]
             if true_palette(x)[0] == pal and x["id"] != w["id"]]
    if share:
        print(f"also repainted (same palette): {', '.join(share)}")
    print("a battle load reverts this; re-run after every load")


if __name__ == "__main__":
    mode = sys.argv[1] if len(sys.argv) > 1 else ""
    if mode == "check":
        sys.exit(cmd_check())
    elif mode == "codes" and len(sys.argv) > 2:
        cmd_codes(sys.argv[2])
    elif mode == "roommates" and len(sys.argv) > 2:
        cmd_roommates(sys.argv[2])
    elif mode == "saw" and len(sys.argv) > 3:
        cmd_saw(sys.argv[2], sys.argv[3])
    elif mode == "tally":
        cmd_tally()
    elif mode == "keychart":
        cmd_keychart()
    elif mode == "render" and len(sys.argv) > 2:
        cmd_render(*sys.argv[2:4])
    elif mode == "flat" and len(sys.argv) > 5:
        cmd_flat(*sys.argv[2:6])
    elif mode == "slots" and len(sys.argv) > 2:
        cmd_slots(sys.argv[2])
    elif mode == "paint" and len(sys.argv) > 2:
        cmd_paint(sys.argv[2])
    else:
        print(__doc__)
