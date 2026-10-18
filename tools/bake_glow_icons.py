#!/usr/bin/env python
"""
Bake glow-rim icon variants for every living weapon (LW-295 Cycle B, stage B1: the BAKE half).
The set is every growing weapon in 1..121 plus the extended-inventory rows.

A weapon that has grown glows on its equip icon too (the sprite half is Cycle A). This is the
OFFLINE half: for every weapon id (tools/lib/categories.py WEAPON_CATS via items.json, ids 1-121 plus
the extended rows, never a non-weapon), for both icon surfaces (card 100x100 / small 48x48), bake three
glow-rim intensity variants keyed by kill tier, using the parked rim machinery in
tools/recolor_icons.py (SHIP_GLOW_RIM stays False; the shipped base bake is untouched by a
single byte). Output lands in LivingWeapon/glow_icons/ alongside one manifest.json, which the
runtime half (LivingWeapon/Display/IconGlow.cs, built in parallel) reads to splice a variant
into the game's modded.pac at the player's current tier.

Recipe per (item_id, surface), mirroring recolor_icons.process()'s own encode recipe
(recolor_icons.py, process()) WITHOUT calling process() itself (it writes the shipped mod
tree; this writes glow_icons/ instead):
  1. render the shipped RIMLESS body via the production route: ri.ramp_render(item_id, tint,
     surface, glow=False). An extended row has no ramp treatment, so its body is its own shipped
     plain icon decoded from the mod tree. Pixel-identical to the shipped (glow-off) bake -- same code path,
     same vanilla decode (VANILLA tex -> FF16Tools tex-conv -> DDS -> Pillow).
  2. for each tier 1..3, apply the rim as an ADDED layer at the tier's scaled intensity by
     mirroring ramp_render's own post-body glow tail (its `if not glow: return body` branch
     onward): look up RAMP_RIMS.get(str(item_id)) and call ri.ramp_glow(...) with rim_scale
     set to that tier's TIER_SCALES entry. This is bit-for-bit equivalent to calling
     ri.ramp_render(item_id, tint, surface, glow=True, rim_scale=scale) fresh for each tier
     (`body` here IS the exact object ramp_render(glow=False) returns before its own glow
     tail runs) -- the tail is duplicated on purpose, NOT refactored into recolor_icons.py,
     because that file gains ONLY the rim_scale kwarg (LW-295 spec). Decoding vanilla once per
     (id, surface) instead of once per tier is also why this bakes in ~968 FF16Tools shell-outs
     rather than ~1450: 121 ids x 2 surfaces = 242 decodes (inside step 1) + 121 x 2 x 3 = 726
     encodes (step 3), 242 + 726 = 968.
  3. encode the tier image the same way process() does: Pillow PNG -> FF16Tools img-conv
     --no-chunk-compression -> .tex, written to glow_icons/ (never mod/FFTIVC/).

Tier scales are knobs (owner tunes at the live pass): {1: 0.6, 2: 1.0, 3: 1.3}. rim_scale=1.0
(tier 2) is an EXACT identity on ramp_glow's resolved alphas (see its doc comment), so tier 2's
pixels equaled the parked rim's output until the owner tuning passes of 2026-08-26 (lane colors, OUTER_TRIM, GLOW_TRIM) reshaped every tier.

Manifest schema (schemaVersion 1, EXACT field names -- the runtime half implements to this same
schema, so a rename here is a cross-agent break, not a local refactor):
  {
    "schemaVersion": 1,
    "tierScales": {"1": 0.6, "2": 1.0, "3": 1.3},
    "icons": [
      {"id": 12, "surface": "card",
       "baseRel": "data/enhanced/ui/ffto/icon/equip_item/texture/ei_012_uitx.tex",
       "length": 51296, "baseSha1": "<40 hex>",
       "variants": {"1": "ei_012_uitx_t1.tex", "2": "...", "3": "..."},
       "variantSha1s": {"1": "<40 hex>", "2": "...", "3": "..."}}
    ]
  }
baseRel is relative to the mod's FFTIVC root (runtime resolves modDir/FFTIVC/<baseRel>).
variants/variantSha1s are keyed by tier as a STRING ("1"/"2"/"3"), filenames are within
glow_icons/ (flat, no subfolder). length is the surface's FIXED tex size (card 51296 = 0xC860,
small 12384 = 0x3060) -- both the base and every variant must equal it, since the runtime
splices variant bytes over the base at a fixed offset. variantSha1s exist for this file's own
verify gate; the runtime may ignore them. inputsSha1 is the fingerprint of what went INTO the
bake (the plain icon bytes, the weapon's rim recipe, and for a ramp id its body inputs), so a
recipe edit or a hand edited manifest goes red at verify until the id is rebaked.

Subcommands:
  python tools/bake_glow_icons.py bake [--from N] [--to N] [--force]
      Bakes weapon ids in [--from, --to] (default the full set, 1..121 plus the extended rows). IDEMPOTENT/RESUMABLE:
      an (id, surface) whose three tier files already exist AND whose manifest inputsSha1
      still matches the plain icon and rim recipe is skipped (no re-decode, no re-encode);
      a stale fingerprint rebakes the id on its own, --force rebakes regardless. Safe to run in id-range slices across several invocations --
      each shells out to FF16Tools up to (2 decodes + 6 encodes) x range-size times, and a
      single call can take minutes; slice with --from/--to to stay under a shell timeout.
      Updates manifest.json after every invocation (upserts entries for the ids just touched,
      preserves entries from earlier slices).
  python tools/bake_glow_icons.py verify
      THE GATE. Exit nonzero on any failure: every WEAPON_CATS id x 2 surfaces x 3 tiers
      present; every variant file EXACTLY the surface's fixed byte length; manifest matches
      files (including sha1s); every inputsSha1 equals the fingerprint of the plain icon
      plus the rim recipe as the code reads them now; no non-weapon id anywhere in the
      manifest; schemaVersion 1; plus the rim_scale=1.0 identity pin (see selftest).
  python tools/bake_glow_icons.py selftest
      Just the rim_scale=1.0 identity pin, runnable on its own: ri.ramp_render(id, tint,
      "card", glow=True) must be byte-identical to the same call with rim_scale=1.0 passed
      explicitly, for a sample id with no rims.json row (1) and one with an explicit row
      (115), so both resolution paths are pinned. Needs the same local game files
      recolor_icons.py's own selftest needs (VANILLA tex tree + FF16Tools CLI); skips loudly
      if absent (matches recolor_icons.py's own _ramp_game_files_available() convention).
"""
import argparse
import hashlib
import json
import shutil
import subprocess
import sys
import tempfile
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
from lib.categories import WEAPON_CATS
from lib.items import load_items
from lib.lane_glow import lane_glow
from lib.paths import ROOT, FF16
import recolor_icons as ri
from PIL import Image

OUT_DIR = ROOT / "LivingWeapon" / "glow_icons"
MANIFEST_PATH = OUT_DIR / "manifest.json"
WORK = ri.WORK  # reuse recolor_icons' own working/icons scratch dir; never a repo-local scratch

SCHEMA_VERSION = 1
# Knobs (owner tunes at the live pass, per LW-295 plan F1): tier 0 (no glow) is not baked here,
# the runtime keeps the base .tex spliced in for tier 0 (P6: the pac rebuilds from loose files
# every launch, so base art is the guaranteed launch state).
# Tier 3 softened 1.5 -> 1.3 (owner Atlas feedback 2026-08-26: '+3 the glow is too
# over powering, tone it down by like 10 or 15%'; 1.3 = -13%, and the inner ring
# drops off full 255 opacity, 170*1.3=221).
TIER_SCALES = {1: 0.6, 2: 1.0, 3: 1.3}
# The rim was too faint at true size on the list icon, so the outer band runs full width.
OUTER_TRIM = 1.0
# Strength multiplier that makes a +1 ring readable on the list icon.
GLOW_TRIM = 1.3
# Green and cyan cannot stand out on the cream panel, so their rim is darker than their text color.
# Yellow stays as is because a darker yellow merges with the icon outline and stops reading as a glow.
# The hp inner ring is lifted because the capped orange read brown on gold blades.
LANE_RIM_OVERRIDES = {
    "hp": {"deep": (235, 93, 0)},
    "speed": {"deep": (0, 133, 45), "bright": (0, 166, 56)},
    "pa+ma+brave": {"deep": (0, 133, 120), "bright": (0, 166, 149)},
}
# (pac subfolder, filename prefix, surface tag, fixed tex byte length). Same four surfaces
# process() loops over; sizes are the shipped tex sizes (0xC860 / 0x3060), verified against a
# real deployed file at the start of this arc.
SURFACES = [
    ("equip_item", "ei", "card", 51296),
    ("equip_item_s", "ei_s", "small", 12384),
]


def weapon_ids():
    """The exact bake set: tools/lib/categories.py WEAPON_CATS via items.json. Hard assert (the
    owner rule this whole file exists to enforce): never a non-weapon id, and nothing outside
    1..121 except the extended rows."""
    rows = load_items()["items"]
    # LW-351: a glow rim is a KILL-TIER rim, so an item that opts out of the Living Weapon
    # system (noGrowth -- the restored vanilla axes and flails) can never earn one. It also has
    # no items.json iconTint and no `grows` lane, which is what bake_range and _glow_variant
    # read, so including it here would KeyError the bake rather than produce anything. Excluded
    # at the source instead, so verify()'s expected set and the bake set stay the same rule.
    ids = sorted(it["id"] for it in rows
                 if it.get("category") in WEAPON_CATS and not it.get("noGrowth"))
    # LW-346: an extended-inventory weapon (an `extended` block, ids 261+) is a real growing
    # weapon with its own baked icon pair, so it gets rims like the vanilla-range 121; anything
    # else outside 1..121 is still the stray the owner rule forbids.
    extended = {it["id"] for it in rows if it.get("extended")}
    stray = [i for i in ids if not (1 <= i <= 121) and i not in extended]
    assert not stray, f"WEAPON_CATS produced an id outside 1..121 that is not an extended-inventory row: {stray}"
    return ids


def sha1_bytes(data):
    return hashlib.sha1(data).hexdigest()


def base_tex_path(sub, pfx, item_id):
    return (ROOT / "mod" / "FFTIVC" / "data" / "enhanced" / "ui" / "ffto" / "icon"
            / sub / "texture" / f"{pfx}_{item_id:03d}_uitx.tex")


def base_rel(sub, pfx, item_id):
    return f"data/enhanced/ui/ffto/icon/{sub}/texture/{pfx}_{item_id:03d}_uitx.tex"


def variant_name(pfx, item_id, tier):
    return f"{pfx}_{item_id:03d}_uitx_t{tier}.tex"


LANE_GLOW = lane_glow()
_LANE_BY_ID = {it["id"]: it["grows"].lower() for it in load_items()["items"]
               if it.get("grows")}
_EXTENDED_IDS = frozenset(it["id"] for it in load_items()["items"] if it.get("extended"))
_NO_TINT = (0.0, 0.0, 1.0)  # ramp_glow unpacks a tint but the lane supplies the rim color here


def body_route(item_id):
    """Where a rimless body comes from. Ramp ids have a treatment row, extended ids have none
    and use their own shipped plain icon."""
    if item_id in ri.RAMP_IDS:
        return "ramp"
    if item_id in _EXTENDED_IDS:
        return "shipped"
    raise SystemExit(f"id {item_id} has no glow body route (not a ramp id, not an extended row)")


def tint_for(item_id):
    return ri.ICON_TINTS[item_id] if body_route(item_id) == "ramp" else _NO_TINT


def decode_shipped(sub, pfx, item_id):
    """The shipped plain icon as RGBA. Decodes in a temp dir so WORK and the base file are untouched."""
    src = base_tex_path(sub, pfx, item_id)
    stem = f"{pfx}_{item_id:03d}_uitx"
    with tempfile.TemporaryDirectory(prefix="lw_shipped_") as td:
        tex = Path(td) / f"{stem}.tex"
        shutil.copy(src, tex)
        subprocess.run([str(FF16), "tex-conv", "-i", str(tex)], capture_output=True)
        return Image.open(Path(td) / f"{stem}.dds").convert("RGBA").copy()


def body_for(item_id, sub, pfx, surface):
    if body_route(item_id) == "ramp":
        return ri.ramp_render(item_id, ri.ICON_TINTS[item_id], surface, glow=False)
    return decode_shipped(sub, pfx, item_id)


def rim_params(lane):
    """Returns deep, bright, outer_trim and glow_trim for a lane. An override color wins where
    present, otherwise the lane_glow color applies, and the trims are always global."""
    o = LANE_RIM_OVERRIDES.get(lane, {})
    return (o.get("deep", LANE_GLOW[lane]["deep"]), o.get("bright", LANE_GLOW[lane]["bright"]),
            OUTER_TRIM, GLOW_TRIM)


def rim_recipe(item_id, surface):
    """The data values a rim bake reads besides the plain icon bytes, in the shape the
    fingerprint hashes. A ramp id's body is rendered fresh, so its treatment row, tint, donor
    id, pop membership and vendored body png are inputs too. Code constants are not covered;
    the rebake pins stand for those."""
    lane = _LANE_BY_ID[item_id]
    deep, bright, outer_trim, glow_trim = rim_params(lane)
    rim = ri.RAMP_RIMS.get(str(item_id))
    route = body_route(item_id)
    recipe = {
        "lane": lane,
        "route": route,
        "deep": list(deep),
        "bright": list(bright),
        "outerTrim": outer_trim,
        "glowTrim": glow_trim,
        "innerA": rim["inner_a"] if rim else 170,
        "outerA": rim["outer_a"] if rim else 80,
        "thirdA": 0,
        "tierScales": {str(k): v for k, v in TIER_SCALES.items()},
    }
    if route == "ramp":
        stem = f"ei_{item_id:03d}_uitx" if surface == "card" else f"ei_s_{item_id:03d}_uitx"
        vendored = ri._RAMP_BODIES_DIR / f"{stem}.png"
        recipe["body"] = {
            "treatment": ri.RAMP_TREATMENTS[str(item_id)],
            "tint": list(ri.ICON_TINTS[item_id]),
            "srcId": ri.SRC.get(item_id, item_id),
            "pop": item_id in ri.RAMP_POP,
            "vendoredPngSha1": sha1_bytes(vendored.read_bytes()) if vendored.exists() else None,
        }
    return recipe


def inputs_sha1(item_id, surface, base_bytes):
    recipe = json.dumps(rim_recipe(item_id, surface), sort_keys=True, separators=(",", ":"))
    return sha1_bytes(base_bytes + recipe.encode("utf-8"))


def _bake_is_current(by_key, item_id, sub, pfx, surface, out_paths):
    """True when all three tier files exist, each still hashes to the manifest's variantSha1,
    and the manifest's fingerprint for this (id, surface) still matches the plain icon and
    rim recipe on disk. A swapped ring file therefore rebakes instead of being restamped."""
    if not all(p.exists() for p in out_paths.values()):
        return False
    entry = by_key.get((item_id, surface))
    base_path = base_tex_path(sub, pfx, item_id)
    if entry is None or not base_path.exists():
        return False
    for tier, path in out_paths.items():
        if entry.get("variantSha1s", {}).get(str(tier)) != sha1_bytes(path.read_bytes()):
            return False
    return entry.get("inputsSha1") == inputs_sha1(item_id, surface, base_path.read_bytes())


def _glow_variant(body, tint, item_id, rim_scale):
    """Mirrors ramp_render's own post-body glow tail (recolor_icons.py: inside ramp_render,
    everything from `if not glow: return body` onward) so a (id, surface) body can be decoded
    ONCE and reused for all three tiers.

    COLOR routing changed for the LW-319 C rung (owner-ruled 2026-08-25 on the knife
    sitting: lane hues at pop strength, pink over green): the rim wears the weapon's LANE
    pair from tools/lib/lane_glow.py -- deep inner ring, bright outer falloff -- instead of
    the LW-295 identity color. A rims.json row still contributes its per-weapon ALPHAS
    (rim geometry stays owner-tuned per weapon); only its rgb is superseded.
    A lane may override its colors through rim_params."""
    deep, bright, outer_trim, glow_trim = rim_params(_LANE_BY_ID[item_id])
    rim = ri.RAMP_RIMS.get(str(item_id))
    inner_a = rim["inner_a"] if rim else 170
    outer_a = rim["outer_a"] if rim else 80
    return ri.ramp_glow(body, tint, inner_a=inner_a,
                        outer_a=int(round(outer_a * outer_trim)), third_a=0,
                        rim_rgb=deep, outer_rgb=bright,
                        rim_scale=rim_scale * glow_trim)


def encode_tex(img, out_path, tag):
    """PNG -> FF16Tools img-conv --no-chunk-compression -> .tex, the same encode recipe as
    recolor_icons.process() (never calls process() itself, which writes the shipped mod tree
    at MOD, not glow_icons/)."""
    WORK.mkdir(parents=True, exist_ok=True)
    png = WORK / f"_glow_{tag}.png"
    img.save(png)
    subprocess.run([str(FF16), "img-conv", "-i", str(png), "--no-chunk-compression"],
                   capture_output=True)
    tex = WORK / f"_glow_{tag}.tex"
    out_path.parent.mkdir(parents=True, exist_ok=True)
    shutil.move(str(tex), str(out_path))
    png.unlink(missing_ok=True)


def bake_range(ids, force=False):
    """Bake every (id, surface, tier) in `ids`. Idempotent: an (id, surface) whose three tier
    files already exist with a matching manifest fingerprint skips BOTH the decode and the
    three encodes unless force=True. A stale fingerprint rebakes all three tiers, so a recipe
    change can never be papered over by rewriting the manifest alone."""
    baked_files = 0
    baked_bytes = 0
    skipped_files = 0
    decodes = 0
    by_key = {(e["id"], e["surface"]): e for e in load_manifest().get("icons", [])}
    for item_id in ids:
        tint = tint_for(item_id)
        for sub, pfx, surface, size in SURFACES:
            out_paths = {t: OUT_DIR / variant_name(pfx, item_id, t) for t in TIER_SCALES}
            if not force and _bake_is_current(by_key, item_id, sub, pfx, surface, out_paths):
                skipped_files += len(out_paths)
                continue
            body = body_for(item_id, sub, pfx, surface)
            decodes += 1
            for tier, scale in TIER_SCALES.items():
                out_path = out_paths[tier]
                img = _glow_variant(body, tint, item_id, scale)
                encode_tex(img, out_path, f"{pfx}_{item_id:03d}_t{tier}")
                data_len = out_path.stat().st_size
                if data_len != size:
                    raise SystemExit(
                        f"BAKE SIZE MISMATCH {out_path.name}: {data_len} != {size} "
                        f"(the encode must exactly match the surface's fixed tex size)")
                baked_files += 1
                baked_bytes += data_len
        print(f"  id {item_id:03d} done", flush=True)
    update_manifest(ids)
    print(f"Baked {baked_files} new variant file(s), {baked_bytes} bytes, "
          f"{decodes} body decode(s); skipped {skipped_files} already-baked file(s).")
    return baked_files, baked_bytes


def manifest_entry(item_id, sub, pfx, surface, size):
    """None if this (id, surface) isn't fully baked yet (caller upserts only what exists, so a
    partial slice never corrupts the manifest's earlier entries)."""
    base_path = base_tex_path(sub, pfx, item_id)
    if not base_path.exists():
        return None
    base_bytes = base_path.read_bytes()
    if len(base_bytes) != size:
        raise SystemExit(f"BASE SIZE MISMATCH {base_path}: {len(base_bytes)} != {size}")
    variants, variant_sha1s = {}, {}
    for tier in sorted(TIER_SCALES):
        vpath = OUT_DIR / variant_name(pfx, item_id, tier)
        if not vpath.exists():
            return None
        vbytes = vpath.read_bytes()
        variants[str(tier)] = vpath.name
        variant_sha1s[str(tier)] = sha1_bytes(vbytes)
    return {
        "id": item_id,
        "surface": surface,
        "baseRel": base_rel(sub, pfx, item_id),
        "length": size,
        "baseSha1": sha1_bytes(base_bytes),
        "inputsSha1": inputs_sha1(item_id, surface, base_bytes),
        "variants": variants,
        "variantSha1s": variant_sha1s,
    }


def load_manifest():
    if MANIFEST_PATH.exists():
        return json.loads(MANIFEST_PATH.read_text(encoding="utf-8"))
    return {"schemaVersion": SCHEMA_VERSION,
            "tierScales": {str(k): v for k, v in TIER_SCALES.items()},
            "icons": []}


def update_manifest(ids):
    """Upserts manifest entries for `ids` only; entries for ids baked in an earlier slice are
    left exactly as they were (read-modify-write over the file, keyed by (id, surface)).

    LW-351 stage 2: it also DROPS entries for ids that are no longer in weapon_ids(), i.e. items
    that left the glow programme (the restored vanilla axes and flails are noGrowth and earn no
    kill-tier rim). verify() already refuses such stale entries; pruning them here means the
    generated manifest never needs a hand edit to pass its own gate. The variant files of a
    pruned id are the caller's to delete (they are listed in the printout)."""
    manifest = load_manifest()
    live = set(weapon_ids())
    stale = sorted({e["id"] for e in manifest.get("icons", []) if e["id"] not in live})
    if stale:
        print(f"  pruning manifest entries for ids outside the glow programme: {stale} "
              f"(delete their variant files under {OUT_DIR.name}/)")
    by_key = {(e["id"], e["surface"]): e for e in manifest.get("icons", []) if e["id"] in live}
    for item_id in ids:
        for sub, pfx, surface, size in SURFACES:
            entry = manifest_entry(item_id, sub, pfx, surface, size)
            if entry is not None:
                by_key[(item_id, surface)] = entry
    manifest["schemaVersion"] = SCHEMA_VERSION
    manifest["tierScales"] = {str(k): v for k, v in TIER_SCALES.items()}
    manifest["icons"] = [by_key[k] for k in sorted(by_key)]
    OUT_DIR.mkdir(parents=True, exist_ok=True)
    MANIFEST_PATH.write_text(json.dumps(manifest, indent=1) + "\n", encoding="utf-8")
    return manifest


def _pin_rim_scale_identity():
    """rim_scale=1.0 must be an EXACT identity (spec requirement). Exercises BOTH resolution
    paths inside ramp_glow: id 1 has no rims.json row (default rim_color(tint) path), id 115
    has an explicit row (rim_rgb passed straight through). Offline in the sense that it needs
    no network and no prior bake -- but like recolor_icons.py's own game-gated pins, it does
    need the local VANILLA tex tree + FF16Tools CLI to decode a real body, so it skips loudly
    (never fails) when those are absent, matching ri._ramp_game_files_available()'s contract."""
    failures = []
    if not ri._ramp_game_files_available():
        print("SELFTEST SKIP (game files absent): rim_scale=1.0 identity pin")
        return failures
    for sample_id, surface in ((1, "card"), (115, "card")):
        tint = ri.ICON_TINTS[sample_id]
        default_call = ri.ramp_render(sample_id, tint, surface, glow=True)
        explicit_call = ri.ramp_render(sample_id, tint, surface, glow=True, rim_scale=1.0)
        if default_call.tobytes() != explicit_call.tobytes():
            failures.append(
                f"id {sample_id} {surface}: rim_scale=1.0 is NOT byte-identical to the "
                f"default (no-kwarg) call -- identity pin FAILED")
    return failures


def _pin_rim_params():
    """Needs no game files. Three lanes override the rim colors and every other lane is
    exactly its lane_glow pair, all with the global trims."""
    failures = []
    overrides = {
        "hp": ((235, 93, 0), LANE_GLOW["hp"]["bright"]),
        "speed": ((0, 133, 45), (0, 166, 56)),
        "pa+ma+brave": ((0, 133, 120), (0, 166, 149)),
    }
    if (OUTER_TRIM, GLOW_TRIM) != (1.0, 1.3):
        failures.append(f"trims {(OUTER_TRIM, GLOW_TRIM)!r} != (1.0, 1.3)")
    for lane, (deep, bright) in overrides.items():
        want = (deep, bright, 1.0, 1.3)
        if rim_params(lane) != want:
            failures.append(f"rim_params({lane!r}) {rim_params(lane)!r} != {want!r}")
    for lane, pair in LANE_GLOW.items():
        if lane in overrides:
            continue
        want = (pair["deep"], pair["bright"], 1.0, 1.3)
        if rim_params(lane) != want:
            failures.append(f"rim_params({lane!r}) {rim_params(lane)!r} != {want!r}")
    return failures


def _pin_body_routes():
    """Needs no game files. Every glow id has exactly one body route."""
    failures = []
    for i in weapon_ids():
        if body_route(i) not in ("ramp", "shipped"):
            failures.append(f"id {i} has no body route")
    for i in sorted(_EXTENDED_IDS):
        if body_route(i) != "shipped":
            failures.append(f"extended id {i} routes {body_route(i)!r}, want 'shipped'")
    for i in sorted(ri.RAMP_IDS):
        if body_route(i) != "ramp":
            failures.append(f"ramp id {i} routes {body_route(i)!r}, want 'ramp'")
    try:
        body_route(10 ** 6)
        failures.append("an id with no route must be refused")
    except SystemExit:
        pass
    return failures


def _pin_tint_dead_on_glow_path():
    """Needs no game files. The lane supplies the rim color, so the tint must not matter."""
    body = Image.new("RGBA", (24, 24), (0, 0, 0, 0))
    body.paste((200, 60, 40, 255), (8, 8, 16, 16))
    a = _glow_variant(body, (0.5, 0.9, 1.0), 33, 1.0)
    b = _glow_variant(body, _NO_TINT, 33, 1.0)
    return [] if a.tobytes() == b.tobytes() else ["the tint changed a glow variant"]


def _pin_rebake_matches_committed(ids):
    """A fresh rebake from each id's own art must equal the committed variants byte for byte.
    This is what catches a donor copy coming back under any name."""
    failures = []
    manifest = json.loads(MANIFEST_PATH.read_text(encoding="utf-8"))
    by_key = {(e["id"], e["surface"]): e for e in manifest["icons"]}
    for item_id in ids:
        if body_route(item_id) == "ramp" and not ri._ramp_game_files_available():
            print(f"SKIP (game files absent): rebake pin for id {item_id}")
            continue
        if not FF16.exists():
            print(f"SKIP (FF16Tools absent): rebake pin for id {item_id}")
            continue
        for sub, pfx, surface, _ in SURFACES:
            body = body_for(item_id, sub, pfx, surface)
            entry = by_key.get((item_id, surface))
            for tier, scale in TIER_SCALES.items():
                with tempfile.TemporaryDirectory(prefix="lw_pin_") as td:
                    out = Path(td) / "v.tex"
                    encode_tex(_glow_variant(body, tint_for(item_id), item_id, scale), out,
                               f"_pin_{pfx}_{item_id:03d}_t{tier}")
                    got = sha1_bytes(out.read_bytes())
                disk = sha1_bytes((OUT_DIR / variant_name(pfx, item_id, tier)).read_bytes())
                if got != disk:
                    failures.append(f"id {item_id} {surface} tier {tier}: committed file is not the rebake from its own art")
                if entry is None or entry["variantSha1s"][str(tier)] != got:
                    failures.append(f"id {item_id} {surface} tier {tier}: manifest sha1 is not the rebake")
    return failures


def selftest():
    failures = _pin_rim_params()
    failures += _pin_body_routes()
    failures += _pin_tint_dead_on_glow_path()
    failures += _pin_rebake_matches_committed([33, 262])
    failures += _pin_rim_scale_identity()
    if failures:
        print("SELFTEST FAIL:")
        for f in failures:
            print(f"  - {f}")
        return 1
    print("SELFTEST PASS: lane rim params, body routes, tint-dead pin, rebake pin (ids 33, 262), rim_scale=1.0 identity pin (ids 1, 115)")
    return 0


def verify():
    ids = weapon_ids()
    failures = []
    if not MANIFEST_PATH.exists():
        print(f"VERIFY FAIL: {MANIFEST_PATH} does not exist")
        return 1
    manifest = json.loads(MANIFEST_PATH.read_text(encoding="utf-8"))
    if manifest.get("schemaVersion") != SCHEMA_VERSION:
        failures.append(f"schemaVersion {manifest.get('schemaVersion')!r} != {SCHEMA_VERSION}")

    entries = manifest.get("icons", [])
    by_key = {(e["id"], e["surface"]): e for e in entries}
    expected_keys = {(i, surface) for i in ids for (_, _, surface, _) in SURFACES}
    manifest_keys = set(by_key.keys())

    missing = sorted(expected_keys - manifest_keys)
    if missing:
        failures.append(f"manifest missing {len(missing)} entr(y/ies), e.g. {missing[:5]}")
    extra = sorted(manifest_keys - expected_keys)
    if extra:
        failures.append(f"manifest has {len(extra)} entr(y/ies) outside the weapon set "
                         f"(non-weapon id or stale id), e.g. {extra[:5]}")

    for item_id, surface in sorted(expected_keys & manifest_keys):
        entry = by_key[(item_id, surface)]
        sub, pfx, size = next((s, p, sz) for (s, p, surf, sz) in SURFACES if surf == surface)
        base_path = ROOT / "mod" / "FFTIVC" / entry.get("baseRel", "")
        if not base_path.exists():
            failures.append(f"id {item_id} {surface}: base tex missing at {base_path}")
            continue
        base_bytes = base_path.read_bytes()
        if entry.get("length") != size or len(base_bytes) != size:
            failures.append(f"id {item_id} {surface}: length {entry.get('length')} "
                             f"(actual base {len(base_bytes)}) != expected {size}")
        if entry.get("baseSha1") != sha1_bytes(base_bytes):
            failures.append(f"id {item_id} {surface}: baseSha1 does not match the deployed base tex")
        if entry.get("inputsSha1") != inputs_sha1(item_id, surface, base_bytes):
            failures.append(f"id {item_id} {surface}: inputsSha1 is not the fingerprint of the plain icon "
                             f"plus this weapon's rim recipe (rebake the id with --force)")
        for tier in sorted(TIER_SCALES):
            tkey = str(tier)
            vname = entry.get("variants", {}).get(tkey)
            if not vname:
                failures.append(f"id {item_id} {surface} tier {tier}: no variant filename in manifest")
                continue
            vpath = OUT_DIR / vname
            if not vpath.exists():
                failures.append(f"id {item_id} {surface} tier {tier}: missing file {vpath}")
                continue
            vbytes = vpath.read_bytes()
            if len(vbytes) != size:
                failures.append(f"id {item_id} {surface} tier {tier}: {len(vbytes)} bytes != {size}")
            if entry.get("variantSha1s", {}).get(tkey) != sha1_bytes(vbytes):
                failures.append(f"id {item_id} {surface} tier {tier}: variantSha1 mismatch")

    failures.extend(_pin_rim_scale_identity())
    failures.extend(_pin_body_routes())
    failures.extend(_pin_tint_dead_on_glow_path())
    failures.extend(_pin_rebake_matches_committed([33] + sorted(_EXTENDED_IDS)))

    if failures:
        print(f"VERIFY FAIL ({len(failures)} issue(s)):")
        for f in failures:
            print(f"  - {f}")
        return 1

    total_variants = len(ids) * len(SURFACES) * len(TIER_SCALES)
    total_bytes = sum(
        (OUT_DIR / by_key[(i, surf)]["variants"][str(t)]).stat().st_size
        for i in ids for (_, _, surf, _) in SURFACES for t in TIER_SCALES
    )
    print(f"VERIFY PASS: {len(ids)} weapon ids, {len(SURFACES)} surfaces, "
          f"{total_variants} variant files, {total_bytes} bytes total.")
    return 0


def main():
    parser = argparse.ArgumentParser(description=__doc__,
                                     formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = parser.add_subparsers(dest="cmd", required=True)

    p_bake = sub.add_parser("bake")
    p_bake.add_argument("--from", dest="frm", type=int, default=1)
    p_bake.add_argument("--to", dest="to", type=int, default=None)
    p_bake.add_argument("--force", action="store_true",
                        help="re-bake even if the output file already exists")

    sub.add_parser("verify")
    sub.add_parser("selftest")

    args = parser.parse_args()

    if args.cmd == "bake":
        if not ri._ramp_game_files_available():
            print("BAKE ABORTED: VANILLA tex tree or FF16Tools CLI not found on this box.")
            return 1
        hi = args.to if args.to is not None else max(weapon_ids())
        ids = [i for i in weapon_ids() if args.frm <= i <= hi]
        if not ids:
            print(f"No weapon ids in range [{args.frm}, {hi}]")
            return 1
        print(f"Baking {len(ids)} weapon id(s) in [{args.frm}, {hi}]"
              + (" (--force)" if args.force else "") + " ...")
        bake_range(ids, force=args.force)
        return 0
    if args.cmd == "verify":
        return verify()
    if args.cmd == "selftest":
        return selftest()
    return 1


if __name__ == "__main__":
    sys.exit(main())
