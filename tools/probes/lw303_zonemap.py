#!/usr/bin/env python
"""
LW-303: show WHERE each of an icon's colours lands on the weapon, not just which colours it gets.

WHY THIS EXISTS. The colour score (lw303_grade.py) reads one number off a whole weapon, so a sprite
can score beautifully and still look wrong, and it did: the owner looked at a passing set and said
to note the two toned areas. The score could not see it because the weapon was the right COLOURS in
the wrong PLACES, striped across a blade rather than split between blade and grip.

This is the instrument that made that visible. Each row is the menu icon, the recoloured sprite,
and a flat map of the same tile with one loud colour per material: red is the material a person
would name the weapon, blue the second, green the third. Read the map, not the sprite, when asking
whether the split follows the weapon's own parts. It is the same trick that cracked the sprite
identification in the first place, which is that a flat false colour answers a question about
structure that a realistic picture cannot.

WHAT A GOOD MAP LOOKS LIKE: two or three solid regions that a person would name (blade, grip,
pommel). WHAT A BAD ONE LOOKS LIKE: stripes running along one surface, or specks scattered over the
whole weapon, both of which mean the material split is following the artist's shading instead of
the object.

USAGE:
  python lw303_zonemap.py                       # a spread of one weapon per category
  python lw303_zonemap.py Knife                 # a whole category, for a review round
  python lw303_zonemap.py Claymore "Yoichi Bow" # named weapons
  python lw303_zonemap.py --all                 # every weapon, several pages
"""
import json
import pathlib
import sys
import tempfile

HERE = pathlib.Path(__file__).resolve().parent
ROOT = HERE.parents[1]
sys.path.insert(0, str(HERE))
sys.path.insert(0, str(ROOT / "tools"))

import lw301_palette_transform as T
from lib.paths import FF16

MARK = [(235, 60, 60), (60, 120, 255), (60, 205, 90)]     # material 0, 1, 2
ZOOM = 5
CELL_W, CELL_H, COLS = 430, 150, 3


def render(weapons, out_path):
    from PIL import Image, ImageDraw
    tmp = tempfile.mkdtemp(prefix="lw303zone_")
    raw = T.sheet_raw()
    grid = T.sheet_index_grid()
    pmap = {w["id"]: w for w in json.loads((HERE / "lw289_weapon_palette_map.json").read_text(encoding="utf-8"))}

    def paint(codes, box, ox, oy, d):
        for yy in range(box["h"]):
            for xx in range(box["w"]):
                v = grid[box["y"] + yy][box["x"] + xx]
                if not v:
                    continue
                r, g, b = T.bgr555_to_rgb(codes[v] & 0x7FFF)
                d.rectangle([ox + xx * ZOOM, oy + yy * ZOOM, ox + xx * ZOOM + ZOOM - 1, oy + yy * ZOOM + ZOOM - 1],
                            fill=(int(r * 255), int(g * 255), int(b * 255)))

    def zones(van, mats, box, icon_hue, ox, oy, d):
        which = {}
        for part, mat in T.assign_ramps(T.weapon_parts(van, box), mats, icon_hue):
            k = next(i for i, m in enumerate(mats) if m is mat)
            for x in part["slots"]:
                which[x["i"]] = k
        for yy in range(box["h"]):
            for xx in range(box["w"]):
                v = grid[box["y"] + yy][box["x"] + xx]
                if not v:
                    continue
                d.rectangle([ox + xx * ZOOM, oy + yy * ZOOM, ox + xx * ZOOM + ZOOM - 1, oy + yy * ZOOM + ZOOM - 1],
                            fill=MARK[which.get(v, 0) % len(MARK)])

    height = CELL_H * ((len(weapons) + COLS - 1) // COLS) + 8
    im = Image.new("RGB", (CELL_W * COLS, height), (240, 240, 242))
    d = ImageDraw.Draw(im)
    for n, it in enumerate(weapons):
        tile, _ = T.sprite_for_category(it.get("category", ""))
        if tile is None:
            continue
        box = T.sprite_boxes()[tile]
        van = T.palette_of(raw, pmap[it["id"]]["weaponPalette"])
        mats = T.icon_materials(it["id"], FF16, tmp)
        got = T.recolour_for_item(it, van, FF16, tmp, box)
        if not mats:
            continue
        ox, oy = (n % COLS) * CELL_W + 6, (n // COLS) * CELL_H + 6
        d.rectangle([ox - 4, oy - 3, ox + CELL_W - 12, oy + CELL_H - 12], outline=(215, 215, 218))
        d.text((ox, oy), it["name"], fill=(15, 15, 15))
        d.text((ox, oy + 13), " ".join(f'{round(m["h"]*360)}deg/{round(m["share"]*100)}%' for m in mats)
               + f'   {got["mode"]}', fill=(110, 110, 115))
        icon = T.icon_image(it["id"], FF16, tmp)
        if icon is not None:
            small = icon.resize((80, 80), Image.NEAREST)
            im.paste(small, (ox, oy + 30), small)
        paint(got["codes"], box, ox + 92, oy + 34, d)
        zones(van, mats, box, got["hue"], ox + 92 + 185, oy + 34, d)
    im.save(out_path)
    print(f"{len(weapons)} weapons -> {out_path}")


def main():
    args = [a for a in sys.argv[1:] if not a.startswith("--")]
    items = json.loads((ROOT / "data" / "items.json").read_text(encoding="utf-8"))["items"]
    pmap = {w["id"] for w in json.loads((HERE / "lw289_weapon_palette_map.json").read_text(encoding="utf-8"))}
    pool = [it for it in items if it["id"] in pmap and it.get("iconTint")]
    if args:
        want = {a.lower() for a in args}
        # a category name selects the whole family, which is the unit the owner reviews in
        cats = {c.lower(): c for c in {it.get("category", "") for it in pool}}
        chosen, named = [], set()
        for a in list(want):
            if a in cats:
                chosen += [it for it in pool if it.get("category") == cats[a]]
                named.add(a)
        rest = want - named
        chosen += [it for it in pool if it["name"].lower() in rest]
        missing = rest - {it["name"].lower() for it in chosen}
        if missing:
            sys.exit(f"not a weapon or category here: {', '.join(sorted(missing))}")
        chosen.sort(key=lambda it: (it.get("category", ""), it["id"]))
        render(chosen, HERE / "lw303_zones.png")
    elif "--all" in sys.argv:
        pool.sort(key=lambda it: (it.get("category", ""), it["id"]))
        for i in range(0, len(pool), 12):
            render(pool[i:i + 12], HERE / f"lw303_zones_{i // 12 + 1}.png")
    else:
        seen, spread = set(), []
        for it in sorted(pool, key=lambda it: (it.get("category", ""), it["id"])):
            if it.get("category") not in seen:
                seen.add(it.get("category"))
                spread.append(it)
        render(spread[:12], HERE / "lw303_zones.png")


if __name__ == "__main__":
    main()
