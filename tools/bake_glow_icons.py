#!/usr/bin/env python
"""
Bake glow-rim icon variants for every living weapon (LW-295 Cycle B, stage B1: the BAKE half).

A weapon that has grown glows on its equip icon too (the sprite half is Cycle A). This is the
OFFLINE half: for every weapon id (tools/lib/categories.py WEAPON_CATS via items.json, exactly
ids 1-121, never a non-weapon), for both icon surfaces (card 100x100 / small 48x48), bake three
glow-rim intensity variants keyed by kill tier, using the parked rim machinery in
tools/recolor_icons.py (SHIP_GLOW_RIM stays False; the shipped base bake is untouched by a
single byte). Output lands in LivingWeapon/glow_icons/ alongside one manifest.json, which the
runtime half (LivingWeapon/Display/IconGlow.cs, built in parallel) reads to splice a variant
into the game's modded.pac at the player's current tier.

Recipe per (item_id, surface), mirroring recolor_icons.process()'s own encode recipe
(recolor_icons.py, process()) WITHOUT calling process() itself (it writes the shipped mod
tree; this writes glow_icons/ instead):
  1. render the shipped RIMLESS body via the production route: ri.ramp_render(item_id, tint,
     surface, glow=False). Pixel-identical to the shipped (glow-off) bake -- same code path,
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
    "tierScales": {"1": 0.6, "2": 1.0, "3": 1.5},
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
verify gate; the runtime may ignore them.

Subcommands:
  python tools/bake_glow_icons.py bake [--from N] [--to N] [--force]
      Bakes weapon ids in [--from, --to] (default the full 1..121 set). IDEMPOTENT/RESUMABLE:
      an (id, surface) whose three tier files already exist is skipped (no re-decode, no
      re-encode) unless --force. Safe to run in id-range slices across several invocations --
      each shells out to FF16Tools up to (2 decodes + 6 encodes) x range-size times, and a
      single call can take minutes; slice with --from/--to to stay under a shell timeout.
      Updates manifest.json after every invocation (upserts entries for the ids just touched,
      preserves entries from earlier slices).
  python tools/bake_glow_icons.py verify
      THE GATE. Exit nonzero on any failure: every WEAPON_CATS id x 2 surfaces x 3 tiers
      present; every variant file EXACTLY the surface's fixed byte length; manifest matches
      files (including sha1s); no non-weapon id anywhere in the manifest; schemaVersion 1;
      plus the rim_scale=1.0 identity pin (see selftest).
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
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
from lib.categories import WEAPON_CATS
from lib.items import load_items
from lib.lane_glow import lane_glow
from lib.paths import ROOT, FF16
import recolor_icons as ri

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
# Rim WIDTH trim (owner Atlas feedback 2026-08-26 late: "make the width of the glow
# smaller"): the falloff band keeps its color at half strength and the 3px third band
# (a rims.json per-weapon widener) is dropped everywhere, so the rim reads as a crisp
# ring with a whisper of falloff instead of a 2-3px halo. Inner band untouched.
OUTER_TRIM = 0.5
# Global intensity trim (owner 2026-08-26, second tuning pass: "make the glow a
# little less intense instead of lowering the width"): every tier's resolved rim
# scale is multiplied by this, so the whole ladder dims 15% while tier ratios and
# rim width stay exactly as shipped. A width pass-2 (OUTER_TRIM 0.25) was started
# and reverted for this in the same breath.
GLOW_TRIM = 0.7  # 0.85 -> 0.7 (owner 2026-08-26: the 15% pass measured real, 653
                 # differing rim pixels, but read as identical by eye; +3 ring now
                 # ~60% opacity instead of 74%)
# (pac subfolder, filename prefix, surface tag, fixed tex byte length). Same four surfaces
# process() loops over; sizes are the shipped tex sizes (0xC860 / 0x3060), verified against a
# real deployed file at the start of this arc.
SURFACES = [
    ("equip_item", "ei", "card", 51296),
    ("equip_item_s", "ei_s", "small", 12384),
]


def weapon_ids():
    """The exact bake set: tools/lib/categories.py WEAPON_CATS via items.json. Hard assert (the
    owner rule this whole file exists to enforce): never a non-weapon id, never anything outside
    1..121."""
    ids = sorted(it["id"] for it in load_items()["items"] if it.get("category") in WEAPON_CATS)
    stray = [i for i in ids if not (1 <= i <= 121)]
    assert not stray, f"WEAPON_CATS produced an id outside 1..121: {stray}"
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


def _glow_variant(body, tint, item_id, rim_scale):
    """Mirrors ramp_render's own post-body glow tail (recolor_icons.py: inside ramp_render,
    everything from `if not glow: return body` onward) so a (id, surface) body can be decoded
    ONCE and reused for all three tiers.

    COLOR routing changed for the LW-319 C rung (owner-ruled 2026-08-25 on the knife
    sitting: lane hues at pop strength, pink over green): the rim wears the weapon's LANE
    pair from tools/lib/lane_glow.py -- deep inner ring, bright outer falloff -- instead of
    the LW-295 identity color. A rims.json row still contributes its per-weapon ALPHAS
    (rim geometry stays owner-tuned per weapon); only its rgb is superseded."""
    deep = LANE_GLOW[_LANE_BY_ID[item_id]]["deep"]
    bright = LANE_GLOW[_LANE_BY_ID[item_id]]["bright"]
    rim = ri.RAMP_RIMS.get(str(item_id))
    inner_a = rim["inner_a"] if rim else 170
    outer_a = rim["outer_a"] if rim else 80
    return ri.ramp_glow(body, tint, inner_a=inner_a,
                        outer_a=int(round(outer_a * OUTER_TRIM)), third_a=0,
                        rim_rgb=deep, outer_rgb=bright,
                        rim_scale=rim_scale * GLOW_TRIM)


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
    files already exist skips BOTH the decode and the three encodes unless force=True."""
    baked_files = 0
    baked_bytes = 0
    skipped_files = 0
    decodes = 0
    for item_id in ids:
        tint = ri.ICON_TINTS[item_id]
        for sub, pfx, surface, size in SURFACES:
            out_paths = {t: OUT_DIR / variant_name(pfx, item_id, t) for t in TIER_SCALES}
            if not force and all(p.exists() for p in out_paths.values()):
                skipped_files += len(out_paths)
                continue
            body = ri.ramp_render(item_id, tint, surface, glow=False)
            decodes += 1
            for tier, scale in TIER_SCALES.items():
                out_path = out_paths[tier]
                if out_path.exists() and not force:
                    skipped_files += 1
                    continue
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
    left exactly as they were (read-modify-write over the file, keyed by (id, surface))."""
    manifest = load_manifest()
    by_key = {(e["id"], e["surface"]): e for e in manifest.get("icons", [])}
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


def selftest():
    failures = _pin_rim_scale_identity()
    if failures:
        print("SELFTEST FAIL:")
        for f in failures:
            print(f"  - {f}")
        return 1
    print("SELFTEST PASS: rim_scale=1.0 identity pin (ids 1, 115)")
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
    p_bake.add_argument("--to", dest="to", type=int, default=121)
    p_bake.add_argument("--force", action="store_true",
                        help="re-bake even if the output file already exists")

    sub.add_parser("verify")
    sub.add_parser("selftest")

    args = parser.parse_args()

    if args.cmd == "bake":
        if not ri._ramp_game_files_available():
            print("BAKE ABORTED: VANILLA tex tree or FF16Tools CLI not found on this box.")
            return 1
        ids = [i for i in weapon_ids() if args.frm <= i <= args.to]
        if not ids:
            print(f"No weapon ids in range [{args.frm}, {args.to}]")
            return 1
        print(f"Baking {len(ids)} weapon id(s) in [{args.frm}, {args.to}]"
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
