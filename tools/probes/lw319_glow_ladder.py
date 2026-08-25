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


def panel_dE(rgb, alpha=170):
    eff = tuple(round(rgb[k] * alpha / 255 + ri.RAMP_PANEL[k] * (1 - alpha / 255))
                for k in range(3))
    return ri._ramp_dE(eff, ri.RAMP_PANEL)


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
        for tier, scale in sorted(TIER_SCALES.items()):
            img = lane_rim(body, tint, item_id, rgb, scale)
            img.save(HERE / f"lw319_ladder_{safe}_t{tier}.png")
        de = round(panel_dE(rgb), 1)
        manifest[lane] = {
            "poster": poster, "id": item_id,
            "rgb": list(rgb), "hex": MEASURED[LANE_KEY[lane]]["hex"],
            "panel_dE": de, "contrast_ok": de >= 30.0,
        }
        flag = "" if de >= 30.0 else "  <-- BELOW the identity-rim contrast bar (30)"
        print(f"  [{lane}] {poster} (id {item_id}) rim {MEASURED[LANE_KEY[lane]]['hex']} "
              f"panel dE {de}{flag}")
    (HERE / "lw319_ladder_manifest.json").write_text(
        json.dumps(manifest, indent=1, sort_keys=True) + "\n", encoding="utf8")
    print(f"\npanels + manifest -> {HERE}/lw319_ladder_*")


if __name__ == "__main__":
    main()
