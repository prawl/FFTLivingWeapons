#!/usr/bin/env python
"""
LW-303: put every weapon's menu ICON beside the BATTLE SPRITE it will actually wear, in one page.

WHY THIS EXISTS. The whole promise of the colour arc is that a weapon looks in battle like it
looks in the menu. Nobody had ever seen the two side by side: the icons live in one pipeline
(48px .tex renders) and the battle art in another (16-colour palettes on a 4bpp sheet), and the
review sheet could only show colour because which drawing each weapon used was unknown. It is
known now (LW-303, tools/probes/lw301_sprite_labels.json), so the comparison is finally possible.

WHAT IT SHOWS, per weapon: the shipped icon, the same weapon's battle drawing recoloured by the
transform, that drawing in vanilla for reference, and the 16 palette codes that reach the game.
Everything is computed from data/items.json and the pristine sheet at run time; nothing is cached
and no colour is typed in, so re-running after a tuning edit reprints the truth rather than a
snapshot. THE GAME DOES NOT NEED TO BE RUNNING.

THE ONE THING THE PAGE CANNOT SHOW, stated on the page itself: 127 weapons share 13 palettes, so
two weapons on the same palette cannot both be wearing their own colour at the same instant. The
palette-pressure panel measures exactly how far apart the tenants of each palette want to be.

USAGE:
  python lw303_icon_vs_sprite_page.py [out.html]
"""
import base64
import io
import json
import pathlib
import sys

HERE = pathlib.Path(__file__).resolve().parent
ROOT = HERE.parents[1]
sys.path.insert(0, str(HERE))
sys.path.insert(0, str(ROOT / "tools"))

import lw301_palette_transform as T
from lib.paths import FF16

SPRITE_BOX = (36, 24)      # every tile padded into one canvas so the grid's rows line up


def png_data_uri(im):
    buf = io.BytesIO()
    im.save(buf, format="PNG", optimize=True)
    return "data:image/png;base64," + base64.b64encode(buf.getvalue()).decode("ascii")


def render_tile(box, codes, grid):
    """One tile at 1:1, centred in a fixed canvas; the page scales it with pixelated rendering.

    Emitting 1x and scaling in CSS keeps the whole page small enough to stay self-contained, and
    nearest-neighbour upscaling in the browser is exactly what the game's own HD layer does to
    this art anyway.
    """
    from PIL import Image
    im = Image.new("RGBA", SPRITE_BOX, (0, 0, 0, 0))
    px = im.load()
    ox = (SPRITE_BOX[0] - box["w"]) // 2
    oy = (SPRITE_BOX[1] - box["h"]) // 2
    for yy in range(box["h"]):
        for xx in range(box["w"]):
            v = grid[box["y"] + yy][box["x"] + xx]
            if not v:
                continue
            tx, ty = ox + xx, oy + yy
            if 0 <= tx < SPRITE_BOX[0] and 0 <= ty < SPRITE_BOX[1]:
                r, g, b = T.bgr555_to_rgb(codes[v] & 0x7FFF)
                px[tx, ty] = (int(r * 255), int(g * 255), int(b * 255), 255)
    return im


def hexcode(c):
    r, g, b = T.bgr555_to_rgb(c & 0x7FFF)
    return "#%02x%02x%02x" % (int(r * 255), int(g * 255), int(b * 255))


CLASH_DEGREES = 20      # the owner's threshold: past this, two weapons visibly want different colours


def hue_conflict(degrees):
    """How much the weapons sharing one palette disagree about colour.

    The widest gap alone turned out to be useless as a signal: every palette in the mod holds at
    least one pair over 100 degrees apart, so a "widest gap" column paints every row red and says
    nothing. What separates a crowded palette from a quiet one is the SHARE of its pairs that
    clash, which is also the number the parry finding was stated in.
    """
    pairs = 0
    clashing = 0
    worst = 0.0
    for i, a in enumerate(degrees):
        for b in degrees[i + 1:]:
            d = abs(a - b) % 360
            d = min(d, 360 - d)
            pairs += 1
            worst = max(worst, d)
            if d > CLASH_DEGREES:
                clashing += 1
    return {"pairs": pairs, "clashing": clashing, "worst": round(worst),
            "share": round(100 * clashing / pairs) if pairs else 0}


def collect():
    import tempfile
    tmp = tempfile.mkdtemp(prefix="lw303_")
    raw = T.sheet_raw()
    grid = T.sheet_index_grid()
    boxes = T.sprite_boxes()

    items = json.loads((ROOT / "data" / "items.json").read_text(encoding="utf-8"))["items"]
    pmap = {w["id"]: w for w in json.loads((HERE / "lw289_weapon_palette_map.json").read_text(encoding="utf-8"))}
    rows = [it for it in items if it["id"] in pmap and it.get("iconTint")]
    rows.sort(key=lambda it: (it.get("category", ""), it.get("tier", 0), it["id"]))

    out = []
    for it in rows:
        pal = pmap[it["id"]]["weaponPalette"]
        van = T.palette_of(raw, pal)
        tile, why = T.sprite_for_category(it.get("category", ""))
        got = T.recolour_for_item(it, van, FF16, tmp, boxes[tile] if tile is not None else None)
        icon = T.icon_image(it["id"], FF16, tmp)
        rec = {
            "id": it["id"],
            "name": it["name"],
            "cat": it.get("category", "?"),
            "tier": it.get("tier"),
            "pal": pal,
            "tile": tile,
            "why": why,
            "mode": got["mode"],
            "hue": round(got["hue"] * 360),
            "authored": round(got["authored"] * 360),
            "drift": None if got["drift"] is None else round(got["drift"]),
            "icon": png_data_uri(icon) if icon is not None else None,
            "sw": [hexcode(c) if c else None for c in got["codes"]],
        }
        if tile is not None:
            b = boxes[tile]
            rec["spr"] = png_data_uri(render_tile(b, got["codes"], grid))
            rec["van"] = png_data_uri(render_tile(b, van, grid))
            rec["dim"] = f'{b["w"]}x{b["h"]}'
        out.append(rec)
        print(f'  {it["name"]:<22} pal {pal:>2}  tile {str(tile):>4}  {got["mode"]}', file=sys.stderr)
    return out


def palette_pressure(recs):
    groups = {}
    for r in recs:
        groups.setdefault(r["pal"], []).append(r)
    out = []
    for pal, members in sorted(groups.items()):
        hues = [m["hue"] for m in members]
        row = {"pal": pal, "n": len(members), "hues": sorted(hues),
               "names": [m["name"] for m in members]}
        row.update(hue_conflict(hues))
        out.append(row)
    return sorted(out, key=lambda g: (-g["share"], -g["n"]))


def build(out_path):
    recs = collect()
    payload = {
        "weapons": recs,
        "pressure": palette_pressure(recs),
        "boxW": SPRITE_BOX[0],
        "boxH": SPRITE_BOX[1],
    }
    html = (HERE / "lw303_icon_vs_sprite_page.html.tpl").read_text(encoding="utf-8")
    html = html.replace("/*PAYLOAD*/null", json.dumps(payload, separators=(",", ":")))
    out_path.write_text(html, encoding="utf-8")
    kb = out_path.stat().st_size / 1024
    print(f"{len(recs)} weapons -> {out_path}  ({kb:.0f} KB)")


if __name__ == "__main__":
    dest = pathlib.Path(sys.argv[1]) if len(sys.argv) > 1 else HERE / "lw303_icon_vs_sprite.html"
    build(dest)
