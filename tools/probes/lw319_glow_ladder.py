#!/usr/bin/env python
"""LW-319: render the Glow Ladder gallery panels in the MEASURED lane hues.

OFFLINE, reads the vanilla tex tree via recolor_icons' own decode route, writes PNGs
into tools/probes/ only; the mod tree and glow_icons/ are untouched.

WHY. The glow arc's owner gate: before any bake, the owner confirms the sampled card
text colors (tools/probes/lw319_text_rgb_map.json, the measured sitting) read well as
icon glow rims. This renders, per lane, one poster weapon's card icon as the authored
body plus tiers 1..3, with the rim painted in the lane's MEASURED RGB verbatim in
place of the LW-295 identity color. Everything else mirrors the shipped machinery
exactly: body via ri.ramp_render(id, tint, "card", glow=False), rim via ri.ramp_glow
honoring any data/icon_ramp/rims.json alphas, tier intensities via
bake_glow_icons.TIER_SCALES, so the gallery shows the pixels the LW-319 bake would
produce and nothing softer.

CONTRAST ADVISORY. ramp_rim_color proves an identity rim against the menu ground
(RAMP_PANEL, dE >= 30 at inner alpha) before it may exist; a verbatim measured RGB
skips that proof, which is the point (exact match), but any lane that would FAIL the
same bar is flagged in lw319_ladder_manifest.json so the owner rules on it seeing it.
"""
import json
import pathlib
import sys

HERE = pathlib.Path(__file__).resolve().parent
sys.path.insert(0, str(HERE.parent))
import recolor_icons as ri              # noqa: E402
from bake_glow_icons import TIER_SCALES  # noqa: E402

MEASURED = json.loads((HERE / "lw319_text_rgb_map.json").read_text(encoding="utf8"))
LANE_KEY = {"speed": "Speed", "pa": "PA", "ma": "MA", "hp": "HP", "wp": "WP",
            "wp+faith": "WP+Faith", "pa+ma": "PA+MA", "pa+ma+brave": "PA+MA+Brave"}
POSTERS = {"speed": "Quicksilver", "pa": "Claymore", "ma": "Spark Rod", "hp": "Defender",
           "wp": "Outrider Pistol", "wp+faith": "Blaze Gun", "pa+ma": "Greenwood Pole",
           "pa+ma+brave": "Kiyomori"}


def lane_rim(body, tint, item_id, rgb, scale):
    """bake_glow_icons._glow_variant with the lane RGB in place of the identity color."""
    rim = ri.RAMP_RIMS.get(str(item_id))
    if rim:
        return ri.ramp_glow(body, tint, inner_a=rim["inner_a"], outer_a=rim["outer_a"],
                            third_a=rim["third_a"], rim_rgb=rgb, rim_scale=scale)
    return ri.ramp_glow(body, tint, rim_rgb=rgb, rim_scale=scale)


def vibrant(rgb):
    """The measured hue at glow strength (owner direction 2026-08-25 late: the verbatim
    card-text values are post-blend parchment pastels and read underwhelming as rims, so
    the glow keeps the HUE and turns saturation and brightness up). Floors chosen so every
    lane lands clearly saturated and bright; the A/B ladder is the owner's judgment call."""
    import colorsys
    h, s, v = colorsys.rgb_to_hsv(*(c / 255 for c in rgb))
    s2, v2 = min(1.0, max(s * 1.3, 0.75)), min(1.0, max(v * 1.1, 0.85))
    return tuple(int(round(c * 255)) for c in colorsys.hsv_to_rgb(h, s2, v2))


def pop_deep(rgb):
    """C rung inner band: the lane hue at full chroma, value held DEEP. The inventory
    ground is white (RAMP_PANEL), so a rim pops by being darker and fully saturated,
    not lighter; a pale core would dissolve into the panel."""
    import colorsys
    h, s, v = colorsys.rgb_to_hsv(*(c / 255 for c in rgb))
    return tuple(int(round(c * 255)) for c in colorsys.hsv_to_rgb(h, 1.0, min(max(v * 1.05, 0.60), 0.80)))


def pop_bright(rgb):
    """C rung outer band: the same hue at full chroma and full value; the shipped outer
    alpha (80, tier-scaled) fades it into the white panel as the glow falloff."""
    import colorsys
    h, s, v = colorsys.rgb_to_hsv(*(c / 255 for c in rgb))
    return tuple(int(round(c * 255)) for c in colorsys.hsv_to_rgb(h, 1.0, 1.0))


def two_tone_rim(body_img, item_id, deep, bright, scale):
    """C rung ('pop'): ramp_glow's exact contour and band geometry, but the inner band
    wears the DEEP color and the outer (and any rims.json third) the BRIGHT one.
    This is a PROPOSED transform, not the shipped bake: if the owner rules C, the
    same two-color loop moves into bake_glow_icons/ramp_glow in its own commit."""
    rim = ri.RAMP_RIMS.get(str(item_id)) or {}
    inner_a = max(0, min(255, int(round(rim.get("inner_a", 170) * scale))))
    outer_a = max(0, min(255, int(round(rim.get("outer_a", 80) * scale))))
    third_a = max(0, min(255, int(round(rim.get("third_a", 0) * scale))))
    w, h = body_img.size
    out = body_img.copy(); po = out.load(); px = body_img.load()
    body = {(x, y) for y in range(h) for x in range(w) if px[x, y][3] >= 160}
    smoothed = set()
    for y in range(h):
        for x in range(w):
            n = sum((x + dx, y + dy) in body for dy in (-1, 0, 1) for dx in (-1, 0, 1)
                    if (dx, dy) != (0, 0))
            if ((x, y) in body and n >= 3) or ((x, y) not in body and n >= 5):
                smoothed.add((x, y))
    r_ = 3 if third_a else 2
    for y in range(h):
        for x in range(w):
            if (x, y) in smoothed:
                continue
            d = min((max(abs(dx), abs(dy)) for dx in range(-r_, r_ + 1) for dy in range(-r_, r_ + 1)
                     if (x + dx, y + dy) in smoothed), default=99)
            if d == 1:
                po[x, y] = deep + (inner_a,)
            elif d == 2:
                po[x, y] = bright + (outer_a,)
            elif d == 3 and third_a:
                po[x, y] = bright + (third_a,)
    return out


def knives():
    """The LW-319 C-rung family sitting (owner call 2026-08-25 late: icons only, one
    family judged before the recipe rolls forward): all 11 Speed knives on BOTH icon
    surfaces, body + tiers 1..3 in A (verbatim measured), B (vibrant), C (two-tone pop).
    Outputs are DERIVED and stay untracked; this probe + the measured map regenerate them."""
    items = json.load(open(HERE.parent.parent / "data" / "items.json"))
    rows = items["items"] if isinstance(items, dict) and "items" in items else items
    meta = json.load(open(HERE.parent.parent / "LivingWeapon" / "meta.json"))
    fam = [r for r in rows if isinstance(r, dict) and r.get("category") == "Knife"]
    rgb = tuple(MEASURED["Speed"]["rgb"])
    vib, deep, bright = vibrant(rgb), pop_deep(rgb), pop_bright(rgb)
    manifest = {"lane": "speed", "measured": MEASURED["Speed"]["hex"],
                "vibrant": "#{:02X}{:02X}{:02X}".format(*vib),
                "pop_deep": "#{:02X}{:02X}{:02X}".format(*deep),
                "pop_bright": "#{:02X}{:02X}{:02X}".format(*bright), "knives": []}
    for r in sorted(fam, key=lambda r: r["id"]):
        item_id = r["id"]
        entry = meta.get(str(item_id))
        assert entry and entry["lane"] == "speed", f"knife {item_id} is not Speed lane"
        tint = ri.ICON_TINTS[item_id]
        for surface, sfx in (("card", ""), ("small", "s")):
            body = ri.ramp_render(item_id, tint, surface, glow=False)
            body.save(HERE / f"lw319_knife{sfx}_id{item_id:03d}_t0.png")
            for tier, scale in sorted(TIER_SCALES.items()):
                lane_rim(body, tint, item_id, rgb, scale).save(
                    HERE / f"lw319_knife{sfx}_id{item_id:03d}_a{tier}.png")
                lane_rim(body, tint, item_id, vib, scale).save(
                    HERE / f"lw319_knife{sfx}_id{item_id:03d}_b{tier}.png")
                two_tone_rim(body, item_id, deep, bright, scale).save(
                    HERE / f"lw319_knife{sfx}_id{item_id:03d}_c{tier}.png")
        manifest["knives"].append({"id": item_id, "name": entry["name"]})
        print(f"  id {item_id:3d} {entry['name']}")
    (HERE / "lw319_knife_manifest.json").write_text(
        json.dumps(manifest, indent=1) + "\n", encoding="utf8")
    print(f"A {MEASURED['Speed']['hex']} dE {round(panel_dE(rgb),1)} | "
          f"B {manifest['vibrant']} dE {round(panel_dE(vib),1)} | "
          f"C deep {manifest['pop_deep']} dE {round(panel_dE(deep, 255),1)} "
          f"bright {manifest['pop_bright']} dE {round(panel_dE(bright, 120),1)}")


def panel_dE(rgb, alpha=170):
    eff = tuple(round(rgb[k] * alpha / 255 + ri.RAMP_PANEL[k] * (1 - alpha / 255))
                for k in range(3))
    return ri._ramp_dE(eff, ri.RAMP_PANEL)


def roster():
    """Every living weapon's card icon at tier 3 in its lane's measured hue, for the
    full-roster half of the gallery (owner call 2026-08-25: judge the shades in the
    artifact, the in-game icon splice having failed to show). Outputs are DERIVED and
    stay untracked; the committed probe + lw319_text_rgb_map.json regenerate them."""
    meta = json.load(open(HERE.parent.parent / "LivingWeapon" / "meta.json"))
    for k, v in sorted(meta.items(), key=lambda kv: int(kv[0])):
        if not (isinstance(v, dict) and "lane" in v):
            continue
        item_id = int(k)
        rgb = tuple(MEASURED[LANE_KEY[v["lane"]]]["rgb"])
        for surface, sfx in (("card", ""), ("small", "s")):
            body = ri.ramp_render(item_id, ri.ICON_TINTS[item_id], surface, glow=False)
            img = lane_rim(body, ri.ICON_TINTS[item_id], item_id, rgb, TIER_SCALES[3])
            img.save(HERE / f"lw319_roster_t3{sfx}_id{item_id:03d}.png")
        print(f"  id {item_id:3d} {v['name']} ({v['lane']})")


def main():
    meta = json.load(open(HERE.parent.parent / "LivingWeapon" / "meta.json"))
    by_name = {v["name"]: (int(k), v) for k, v in meta.items() if isinstance(v, dict)}
    manifest = {}
    for lane, poster in sorted(POSTERS.items()):
        item_id, entry = by_name[poster]
        assert entry["lane"] == lane, f"{poster} is lane {entry['lane']}, expected {lane}"
        rgb = tuple(MEASURED[LANE_KEY[lane]]["rgb"])
        tint = ri.ICON_TINTS[item_id]
        body = ri.ramp_render(item_id, tint, "card", glow=False)
        safe = lane.replace("+", "_")
        body.save(HERE / f"lw319_ladder_{safe}_t0.png")
        vib = vibrant(rgb)
        for tier, scale in sorted(TIER_SCALES.items()):
            lane_rim(body, tint, item_id, rgb, scale).save(
                HERE / f"lw319_ladder_{safe}_t{tier}.png")
            lane_rim(body, tint, item_id, vib, scale).save(
                HERE / f"lw319_ladder_{safe}_v{tier}.png")
        de = round(panel_dE(rgb), 1)
        manifest[lane] = {
            "poster": poster, "id": item_id,
            "rgb": list(rgb), "hex": MEASURED[LANE_KEY[lane]]["hex"],
            "vibrant_rgb": list(vib),
            "vibrant_hex": "#{:02X}{:02X}{:02X}".format(*vib),
            "vibrant_panel_dE": round(panel_dE(vib), 1),
            "panel_dE": de, "contrast_ok": de >= 30.0,
        }
        flag = "" if de >= 30.0 else "  <-- BELOW the identity-rim contrast bar (30)"
        print(f"  [{lane}] {poster} (id {item_id}) rim {MEASURED[LANE_KEY[lane]]['hex']} "
              f"panel dE {de}{flag}")
    (HERE / "lw319_ladder_manifest.json").write_text(
        json.dumps(manifest, indent=1, sort_keys=True) + "\n", encoding="utf8")
    print(f"\npanels + manifest -> {HERE}/lw319_ladder_*")


if __name__ == "__main__":
    mode = sys.argv[1] if len(sys.argv) > 1 else ""
    knives() if mode == "knives" else roster() if mode == "roster" else main()
