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
    roster() if len(sys.argv) > 1 and sys.argv[1] == "roster" else main()
