#!/usr/bin/env python
"""
LW-303: score the battle-sprite transform against the promise it makes.

THE PROMISE is one sentence: a weapon's battle art should look like its menu icon. That is
checkable without opinion and without the game running, so it should not be settled by looking at
eight weapons and forming an impression. It was, twice, and both times the impression was wrong.

THE SCORE, per weapon: take the hue the shipped 48px icon actually renders (chroma weighted over
its opaque pixels) and the hue the sprite delivers over its MAIN PART, and report the angle between
them. Weighting both ends by chroma is the point: a palette slot that colours two pixels must not
count as much as one that colours forty, which is exactly the mistake the first transform made.

WHY THE MAIN PART AND NOT THE WHOLE WEAPON. It was the whole weapon until the owner asked for these
sprites to be two toned, with white bowstrings and a grip that differs from its blade. Scored whole,
that request reads as error: a deliberately contrasting grip drags the average off the icon, and
Hushblade jumped from 6 degrees to 76 by doing exactly what was asked. So fidelity is now judged on
the part that carries the weapon's identity, and the second tone is reported separately below as a
count of how many colours a viewer would name. Both numbers are needed; either alone can be gamed.

WHY IT EXISTS. Reviewed by eye, eight at a time, the first transform read as "right except for the
bow". Run over all 118 palette mapped weapons it measured a MEDIAN ERROR OF 76 DEGREES, with only
17 within 20 degrees and 48 more than 90 degrees out. Orange icons were arriving blue. The
rewritten ramp aware transform measures 5 degrees median, 110 of 118 within 20, worst case 64.

THE HUE SCORE ALONE IS NOT ENOUGH, and this file used to pretend otherwise. An adversarial review
on 2026-08-21 broke the transform on purpose five ways and ran this grader on each. Two of the five
scored BETTER than the shipped code:

    mutation                                    median   within 20
    shipped                                        5       110/118
    every material hue turned 90 degrees           90        0/118     caught
    ignore the icon, paint everything magenta      88        8/118     caught
    every ramp takes the dominant material          6        98/118    caught, mildly
    FLATTEN every slot to one brightness            4       106/118    MISSED
    REVERSE each ramp's light to dark order         4       106/118    MISSED

The two it missed are the "painted plastic" failure this whole transform exists to prevent. So the
hue score is necessary and not sufficient, and STRUCTURE below is now measured beside it rather
than asserted in a docstring: brightness is checked per slot against vanilla, and the tiles' edge
slots are reported. Two further honest limits, from the same review:
  * For the 55 weapons whose icon yields ONE material, a near zero score is close to an identity:
    every painted slot takes that one hue, and the target is a mean of the same icon pixels the
    material came from. The metric's real discriminating power is in the multi material half, and
    that is exactly where the old transform failed, so the comparison still means something.
  * For a multi material icon the target hue is a circular mean of colours that may appear nowhere
    in the icon. Kiyomori aims at 143 degrees, between its blue and its green. Error therefore
    rises with material count by construction (1 material 3.2 median, 2 materials 8.7, 3 at 14.4).

A GOOD SCORE IS STILL NOT A GOOD LOOK. Nothing here sees mud or a muddy pairing. Run it, then look
at the sheet, then let the owner look at the game.

USAGE:
  python lw303_grade.py               # summary, structure checks, and every weapon over 25 degrees
  python lw303_grade.py --all         # every weapon, worst first
  python lw303_grade.py --baseline    # score the SUPERSEDED transform too, for the before/after
"""
import colorsys
import json
import math
import pathlib
import statistics
import sys
import tempfile

HERE = pathlib.Path(__file__).resolve().parent
ROOT = HERE.parents[1]
sys.path.insert(0, str(HERE))
sys.path.insert(0, str(ROOT / "tools"))

import lw301_palette_transform as T
from lib.paths import FF16

FLAG_DEGREES = 25


def luma(r, g, b):
    """Rec.709 perceived brightness. HSV value is NOT this: yellow and blue at equal value are
    nothing like equally bright, so an edge slot can keep its value exactly and still change how
    dark it reads."""
    return 0.2126 * r + 0.7152 * g + 0.0722 * b


def delivered(codes, box, only=None):
    """The hue and mean saturation the tile actually shows, weighted by inked pixels.

    `only` restricts the reading to one set of palette slots, which is how the main part of a
    two toned weapon gets scored without its contrasting grip dragging the average.
    """
    grid = T.sheet_index_grid()
    X = Y = 0.0
    sat = 0.0
    n = 0
    for yy in range(box["h"]):
        for xx in range(box["w"]):
            v = grid[box["y"] + yy][box["x"] + xx]
            if not v or (only is not None and v not in only):
                continue
            h, s, val = colorsys.rgb_to_hsv(*T.bgr555_to_rgb(codes[v] & 0x7FFF))
            if val < 0.12:
                continue                       # outline pixels carry no readable colour
            X += math.cos(2 * math.pi * h) * s
            Y += math.sin(2 * math.pi * h) * s
            sat += s
            n += 1
    if not n:
        return None, 0.0
    return (math.atan2(Y, X) / (2 * math.pi)) % 1.0, sat / n


def tone_count(codes, box):
    """How many colours a viewer would actually name on this weapon.

    A tone counts when it holds at least a tenth of the drawn pixels and sits at least 40 degrees
    from every tone already counted. Near grey pixels are pooled as one tone, because a white
    bowstring and a steel grip are one answer to "what colour", not two.
    """
    grid = T.sheet_index_grid()
    seen = []
    total = 0
    for yy in range(box["h"]):
        for xx in range(box["w"]):
            v = grid[box["y"] + yy][box["x"] + xx]
            if not v:
                continue
            h, sat, val = colorsys.rgb_to_hsv(*T.bgr555_to_rgb(codes[v] & 0x7FFF))
            if val < 0.12:
                continue
            total += 1
            key = None if sat < 0.15 else h
            for entry in seen:
                if entry[0] is None and key is None:
                    entry[1] += 1
                    break
                if entry[0] is not None and key is not None and T.hue_distance(entry[0], key) < 40:
                    entry[1] += 1
                    break
            else:
                seen.append([key, 1])
    return sum(1 for _, n in seen if total and n / total >= 0.10)


def edge_slots(box):
    """Which palette slots draw the tile's outline: inked pixels that touch transparency.

    Found by adjacency rather than by darkness, because the transform's own near black gate turned
    out never to fire on any shipped palette, so "the dark slot is the outline" was never checked
    against the art. These are the slots whose recolouring a viewer reads as the drawing's edge.
    """
    grid = T.sheet_index_grid()
    out = set()
    for yy in range(box["h"]):
        for xx in range(box["w"]):
            v = grid[box["y"] + yy][box["x"] + xx]
            if not v:
                continue
            for dy, dx in ((1, 0), (-1, 0), (0, 1), (0, -1)):
                ny, nx = yy + dy, xx + dx
                if not (0 <= ny < box["h"] and 0 <= nx < box["w"]):
                    out.add(v)
                    break
                if not grid[box["y"] + ny][box["x"] + nx]:
                    out.add(v)
                    break
    return out


def structure(van, new, box, category=""):
    """Did the recolour keep the drawing's light to dark shape, and what did it do to the edges?

    This is the half the hue score cannot see. Value must match vanilla EXACTLY on every slot the
    transform writes; anything else means a ramp was flattened, rescaled or reordered.

    ONE EXCEPTION IS ALLOWED AND IS CHECKED SEPARATELY. A named part rule may rewrite brightness on
    the slots it owns, which today is bow and crossbow strings: the owner asked for white strings
    and vanilla draws them dark grey, so draining the colour alone cannot produce white. Those
    slots are excluded from the exact check and get their own weaker one, that their light to dark
    ORDER survives. Excluding them without checking anything would be exactly the hole this whole
    section exists to close, since a part rule could then flatten a ramp unnoticed.
    """
    role_slots = set()
    for slots in T.PART_ROLES.get(category, {}).values():
        role_slots |= slots
    worst_value = 0.0
    worst_edge = 0.0
    edges = edge_slots(box)
    ordering = []
    for i, (a, b) in enumerate(zip(van, new)):
        if i == 0 or a == 0:
            continue
        ar, ag, ab = T.bgr555_to_rgb(a & 0x7FFF)
        br, bg, bb = T.bgr555_to_rgb(b & 0x7FFF)
        av = colorsys.rgb_to_hsv(ar, ag, ab)[2]
        bv = colorsys.rgb_to_hsv(br, bg, bb)[2]
        if i in role_slots:
            ordering.append((av, bv))
        else:
            worst_value = max(worst_value, abs(av - bv))
        if i in edges:
            worst_edge = max(worst_edge, abs(luma(ar, ag, ab) - luma(br, bg, bb)))
    kept_order = all((x[0] - y[0]) * (x[1] - y[1]) >= 0
                     for n, x in enumerate(ordering) for y in ordering[n + 1:])
    return worst_value, worst_edge, len(edges), len(ordering), kept_order


# ----------------------------------------------------------------------------------------------
# The SUPERSEDED transform, transcribed here so the before/after stays checkable.
#
# It was deleted from lw301_palette_transform.py on 2026-08-21 because a tool must hold exactly one
# answer to "what colour is this weapon". Deleting it outright, though, left the claim "76 degrees
# before, 5 after" impossible for anyone to re-run, which the adversarial review of that same day
# called out as an unfalsifiable before and after. So the old path lives on HERE, in the measuring
# instrument where it can never paint a shipped pixel, and only under --baseline. It is a faithful
# transcription: brightness zoning at value 0.42 and saturation 0.45, 20 degree hue buckets ranked
# by raw pixel count with a two bucket separation test, at most two materials, saturation applied
# by MULTIPLYING the vanilla slot's own, and the single material fallback rotating the whole
# palette to one hue. Running it reproduces the 76 degree median and 17 of 118 measured live from
# the real code before it was removed, which is the check that it was transcribed correctly.
# ----------------------------------------------------------------------------------------------

def legacy_materials(item_id, ff16, tmp):
    im = T.icon_image(item_id, ff16, tmp)
    if im is None:
        return None
    px = im.load()
    buckets = {}
    for y in range(im.size[1]):
        for x in range(im.size[0]):
            r, g, b, a = px[x, y]
            if a < 250:
                continue
            h, sat, v = colorsys.rgb_to_hsv(r / 255, g / 255, b / 255)
            if v < 0.18 or sat < 0.18:
                continue
            acc = buckets.setdefault(int(h * 18) % 18, [0, 0.0, 0.0, 0.0])
            acc[0] += 1
            acc[1] += r / 255
            acc[2] += g / 255
            acc[3] += b / 255
    if not buckets:
        return None
    out = []
    for key, (n, r, g, b) in sorted(buckets.items(), key=lambda kv: -kv[1][0]):
        if all(min(abs(key - k2), 18 - abs(key - k2)) > 2 for k2, _ in out):
            out.append((key, (r / n, g / n, b / n)))
        if len(out) == 2:
            break
    return [c for _, c in out]


def legacy_two_zone(van, mats):
    prim = mats[0]
    sec = mats[1] if len(mats) > 1 else mats[0]
    ph, ps, _ = colorsys.rgb_to_hsv(*prim)
    sh, ss, _ = colorsys.rgb_to_hsv(*sec)
    out = []
    for c in van:
        if c == 0:
            out.append(0)
            continue
        h, s, v = colorsys.rgb_to_hsv(*T.bgr555_to_rgb(c & 0x7FFF))
        blade = (v >= 0.42 and s <= 0.45)
        th, ts = (ph, ps) if blade else (sh, ss)
        ns = min(1.0, s * (ts / 0.5 if ts else 1.0))
        out.append((c & 0x8000) | T.rgb_to_bgr555(*colorsys.hsv_to_rgb(th, ns, v)))
    return out


def legacy_one_hue(van, tint):
    t_h, t_s, t_vm = tint
    hs, ss = [], []
    for c in van:
        if c == 0:
            continue
        h, s, v = colorsys.rgb_to_hsv(*T.bgr555_to_rgb(c))
        if v < 0.04:
            continue
        hs.append((h, s))
        ss.append(s)
    a_s = sorted(ss)[len(ss) // 2] if ss else 0.0
    ratio = (t_s / a_s) if a_s > 0.01 else 1.0
    out = []
    for c in van:
        if c == 0:
            out.append(0)
            continue
        h, s, v = colorsys.rgb_to_hsv(*T.bgr555_to_rgb(c & 0x7FFF))
        out.append((c & 0x8000) | T.rgb_to_bgr555(*colorsys.hsv_to_rgb(
            t_h % 1.0, max(0.0, min(1.0, s * ratio)), max(0.0, min(1.0, v * t_vm)))))
    return out


def legacy_codes(it, van, ff16, tmp, rendered):
    mats = legacy_materials(it["id"], ff16, tmp)
    if mats and len(mats) >= 2:
        return legacy_two_zone(van, mats)
    tint = list(it["iconTint"])
    if rendered is not None:
        tint[0] = rendered
    return legacy_one_hue(van, tuple(tint))


def grade(baseline=False):
    tmp = tempfile.mkdtemp(prefix="lw303grade_")
    raw = T.sheet_raw()
    items = json.loads((ROOT / "data" / "items.json").read_text(encoding="utf-8"))["items"]
    pmap = {w["id"]: w for w in json.loads((HERE / "lw289_weapon_palette_map.json").read_text(encoding="utf-8"))}
    out = []
    skipped = []
    for it in items:
        if it["id"] not in pmap or not it.get("iconTint"):
            continue
        tile, why = T.sprite_for_category(it.get("category", ""))
        if tile is None:
            skipped.append((it["name"], why))
            continue
        box = T.sprite_boxes()[tile]
        van = T.palette_of(raw, pmap[it["id"]]["weaponPalette"])
        got = T.recolour_for_item(it, van, FF16, tmp, box)
        want = got["hue"]
        main = {x["i"] for x in T.weapon_parts(van, box)[0]["slots"]}
        new_h, new_s = delivered(got["codes"], box, main)
        van_h, van_s = delivered(van, box, main)
        if new_h is None:
            continue
        worst_value, worst_edge, n_edges, n_role, kept_order = structure(
            van, got["codes"], box, it.get("category", ""))
        row = {
            "err": T.hue_distance(want, new_h),
            "name": it["name"], "cat": it.get("category", "?"),
            "want": round(want * 360), "got": round(new_h * 360), "van": round(van_h * 360),
            "sat": new_s, "vsat": van_s, "mode": got["mode"],
            "dvalue": worst_value, "dedge": worst_edge, "edges": n_edges,
            "role": n_role, "order": kept_order,
            "tones": tone_count(got["codes"], box),
        }
        if baseline:
            old = legacy_codes(it, van, FF16, tmp, want)
            old_h, _ = delivered(old, box)
            row["old"] = T.hue_distance(want, old_h) if old_h is not None else None
        out.append(row)
    return sorted(out, key=lambda r: -r["err"]), skipped


def spread(errs, label):
    print(f'\n{label}: median {statistics.median(errs):.0f}deg   mean {statistics.mean(errs):.0f}deg')
    for t in (10, 20, 30, 45, 90):
        print(f'  within {t:>3}deg: {sum(1 for e in errs if e <= t):>3} / {len(errs)}')


def main():
    show_all = "--all" in sys.argv
    baseline = "--baseline" in sys.argv
    rows, skipped = grade(baseline)
    errs = [r["err"] for r in rows]
    print(f'{"err":>4}  {"weapon":<22}{"category":<12}{"icon":>5}{"sprite":>7}{"vanilla":>8}'
          f'{"sat":>6}{"was":>6}  materials')
    for r in rows:
        if show_all or r["err"] > FLAG_DEGREES:
            print(f'{r["err"]:4.0f}  {r["name"]:<22}{r["cat"]:<12}{r["want"]:>5}{r["got"]:>7}'
                  f'{r["van"]:>8}{r["sat"]:>6.2f}{r["vsat"]:>6.2f}  {r["mode"]}')

    spread(errs, f'{len(rows)} weapons, MAIN PART hue against the icon')
    print(f'  median saturation {statistics.median([r["sat"] for r in rows]):.2f}'
          f' (vanilla {statistics.median([r["vsat"] for r in rows]):.2f})')

    worst_value = max(r["dvalue"] for r in rows)
    worst_edge = max(r["dedge"] for r in rows)
    hot = max(rows, key=lambda r: r["dedge"])
    print(f'\nSTRUCTURE, which the hue score cannot see')
    print(f'  brightness carried per slot: worst change {worst_value:.4f}'
          f'   {"PASS, the ramp is untouched" if worst_value < 1e-9 else "FAIL, a ramp was rescaled or flattened"}')
    print(f'  tile edge slots recoloured: yes, by design; worst perceived shift {worst_edge:.3f}'
          f' ({hot["name"]}, {hot["edges"]} edge slots)')
    two = [r for r in rows if r["tones"] >= 2]
    print(f'  weapons a viewer would call two toned: {len(two)} / {len(rows)}')
    for cat in sorted({r["cat"] for r in rows if r["cat"] in T.PART_ROLES}):
        got = [r for r in rows if r["cat"] == cat]
        print(f'    {cat:<12} {sum(1 for r in got if r["tones"] >= 2)} / {len(got)}')
    ruled = [r for r in rows if r["role"]]
    if ruled:
        bad_order = [r["name"] for r in ruled if not r["order"]]
        print(f'  named part rules rewrote brightness on {len(ruled)} weapons '
              f'({ruled[0]["role"]} slots each): '
              + ('PASS, every one kept its light to dark order' if not bad_order
                 else 'FAIL, order broken on ' + ', '.join(bad_order)))

    if baseline:
        old = [r["old"] for r in rows if r.get("old") is not None]
        spread(old, 'the SUPERSEDED transform, same weapons')
        better = sum(1 for r in rows if r.get("old") is not None and r["err"] < r["old"])
        print(f'  improved on {better} of {len(old)} weapons')

    if skipped:
        print(f'\nNOT GRADED, no identified tile: {", ".join(n for n, _ in skipped)}')
        print(f'  {skipped[0][1]}')


if __name__ == "__main__":
    main()
