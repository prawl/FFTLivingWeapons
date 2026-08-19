"""LW-247 Phase 0 census: can the TRACKED ramp engine reproduce the live install?

For every one of the 300 install textures that differ from the repo mod tree, answer:
  FRESH  the tracked engine (tools/probes/ramp/ramp_engine.py) re-renders the banked
         ground-truth PNG pixel-for-pixel from vanilla + tables under that round's config
  RECON  the flashy2 reconstruction identity holds: body (target restored to vanilla
         outside the smoothed vanilla silhouette) + glow with the rim alphas/colour read
         out of the target itself == target
  NONE   neither works (a genuine wall; must be vendored or re-derived by hand)

Ground truth per family (hash-proven equal to the install 2026-08-18):
  weapons minus bags + helms -> session_bank bake_work PNGs
  shields                    -> shield_flashy complement_soft_bake PNGs
  bags                       -> bag_round crisp_bake PNGs

Also checks: img-conv determinism on a sample (banked PNG -> tex must equal banked tex,
twice), and vanilla_cache integrity on a sample (fresh tex-conv of the pristine Pac
Files tex must equal the cached DDS). Read-only on the install and the banks.

Usage: python tools/probes/lw247_repro_census.py  (writes census JSON + summary to stdout;
JSON lands next to this script as lw247_census_result.json)
"""
import importlib.util
import json
import os
import shutil
import subprocess
import sys
import tempfile

from PIL import Image

REPO = r"C:\Users\ptyRa\Dev\FFTLivingWeapons"
RAMP = os.path.join(REPO, "tools", "probes", "ramp")
BANK = r"C:\Users\ptyRa\Downloads\fft_ref"
BAKE = os.path.join(BANK, "session_bank_2026-08-16", "bake_work")
SOFT = os.path.join(BANK, "shield_flashy_2026-08-16", "complement_soft_bake")
CRISP = os.path.join(BANK, "bag_round_2026-08-16", "crisp_bake")
PACS = r"C:\Users\ptyRa\OneDrive\Desktop\Pac Files\0008\ui\ffto\icon"
OUT_JSON = os.path.join(os.path.dirname(os.path.abspath(__file__)),
                        "lw247_census_result.json")

SCRATCH = os.path.join(tempfile.gettempdir(), "lw247_census")
os.makedirs(SCRATCH, exist_ok=True)

spec = importlib.util.spec_from_file_location(
    "ramp_engine", os.path.join(RAMP, "ramp_engine.py"))
rp = importlib.util.module_from_spec(spec)
sys.modules["ramp_engine"] = rp
spec.loader.exec_module(rp)
rp.SCRATCH = SCRATCH  # keep load_shipped()'s tex-conv litter out of the tracked folder

assign = json.load(open(os.path.join(RAMP, "weapon_assignments.json")))
treat = json.load(open(os.path.join(RAMP, "weapon_treatments.json")))
reserved = set(json.load(open(os.path.join(RAMP, "reserved_weapons.json"))))
items = json.load(open(os.path.join(REPO, "data", "items.json")))["items"]
SRC = {it["id"]: it["iconSource"] for it in items if it.get("iconSource")}

BAGS = [115, 116, 117, 118]
WEAPON_IDS = sorted(int(k) for k in assign if int(k) not in BAGS)


def stem_for(i, surface):
    return f"ei_{i:03d}_uitx" if surface == "card" else f"ei_s_{i:03d}_uitx"


def pixels(im):
    return list(im.convert("RGBA").getdata())


def truth_image(i, surface):
    if i in rp.SHIELDS:
        d = SOFT
    elif i in BAGS:
        d = CRISP
    else:
        d = BAKE
    return Image.open(os.path.join(d, stem_for(i, surface) + ".png")).convert("RGBA"), d


def smoothed_mask(im):
    """Same contour rule as glow(): alpha >= 160 body, one majority smooth."""
    w, h = im.size
    px = im.load()
    body = {(x, y) for y in range(h) for x in range(w) if px[x, y][3] >= 160}
    out = set()
    for y in range(h):
        for x in range(w):
            n = sum((x + dx, y + dy) in body for dy in (-1, 0, 1) for dx in (-1, 0, 1)
                    if (dx, dy) != (0, 0))
            if ((x, y) in body and n >= 3) or ((x, y) not in body and n >= 5):
                out.add((x, y))
    return out


def recon_check(i, surface):
    """Reconstruction identity: body from target + rim read out of target == target."""
    src_id = SRC.get(i, i)
    van = rp.load_vanilla(src_id, surface)
    target, _ = truth_image(i, surface)
    if van.size != target.size:
        return False, "size-mismatch"
    mask = smoothed_mask(van)
    w, h = van.size
    body = target.copy()
    bp, vp, tp = body.load(), van.load(), target.load()
    # distance-to-body for every outside pixel, then read the rim bands off the target
    band = {}
    for y in range(h):
        for x in range(w):
            if (x, y) in mask:
                continue
            d = min((max(abs(dx), abs(dy))
                     for dx in range(-3, 4) for dy in range(-3, 4)
                     if (x + dx, y + dy) in mask), default=99)
            bp[x, y] = vp[x, y]
            if d in (1, 2, 3):
                band.setdefault(d, set()).add(tp[x, y])
    def band_alpha(d):
        vals = band.get(d, set())
        alphas = {p[3] for p in vals}
        rgbs = {p[:3] for p in vals}
        return alphas, rgbs
    a1, rgb1 = band_alpha(1)
    if len(a1) != 1 or len(rgb1) != 1:
        return False, f"inner band not uniform (alphas={sorted(a1)}, rgbs={len(rgb1)})"
    inner_a, rim_rgb = a1.pop(), rgb1.pop()
    a2, rgb2 = band_alpha(2)
    if len(a2) != 1 or (rgb2 and rgb2 != {rim_rgb}):
        return False, f"outer band not uniform (alphas={sorted(a2)})"
    outer_a = a2.pop()
    a3, rgb3 = band_alpha(3)
    third_a = 0
    if rgb3 == {rim_rgb} and len(a3) == 1:
        third_a = a3.pop()  # third band present (flashy profile)
    gl = rp.glow(body, rp.TINTS.get(i, (0.0, 0.0, 1.0)), inner_a=inner_a,
                 outer_a=outer_a, third_a=third_a, rim_rgb=rim_rgb)
    if pixels(gl) == pixels(target):
        return True, f"rim {rim_rgb} a=({inner_a},{outer_a},{third_a})"
    return False, "glow-over-reconstruction differs"


results = {}


def record(i, surface, fresh_ok, note_fresh):
    key = stem_for(i, surface)
    entry = {"fresh": bool(fresh_ok), "fresh_note": note_fresh}
    if not fresh_ok:
        ok, note = recon_check(i, surface)
        entry["recon"] = ok
        entry["recon_note"] = note
    results[key] = entry
    verdict = "FRESH" if fresh_ok else ("RECON" if entry.get("recon") else "NONE")
    print(f"{key}: {verdict}" + ("" if fresh_ok else f"  [{entry.get('recon_note', '')}]"),
          flush=True)


# --- pass 1: shields + helms under the deploy_shields config (module defaults) --------
print("== pass 1: shields + helms, deploy_shields config, tracked engine ==", flush=True)
for i in rp.SHIELDS + rp.HELMS:
    for surface in ("small", "card"):
        if i in rp.KEEP_SHIPPED:
            base = rp.load_shipped(i, surface)
        else:
            van = rp.load_vanilla(i, surface)
            base = rp.prototype(van, rp.TINTS[i], surface, item_id=i)
            if i in rp.POP:
                base = rp.pop_filter(base)
        gl = rp.glow(base, rp.TINTS[i])
        target, _ = truth_image(i, surface)
        record(i, surface, pixels(gl) == pixels(target), "deploy_shields config")

# --- pass 2: weapons under the wow_render config --------------------------------------
print("== pass 2: weapons (minus bags), wow_render config, tracked engine ==", flush=True)
rp.RESERVED_POP = reserved
for i_str, a in assign.items():
    i = int(i_str)
    rp.TINTS[i] = tuple(a["tint"])
    if i in reserved:
        continue
    t = treat.get(i_str, {})
    rp.PUNCH = rp.PUNCH | {i}
    if t.get("rotate"):
        rp.ROTATE_ALL = rp.ROTATE_ALL | {i}
    if t.get("forceb"):
        rp.FORCE_MODE_B = rp.FORCE_MODE_B | {i}
for i in WEAPON_IDS:
    for surface in ("small", "card"):
        src_id = SRC.get(i, i)
        van = rp.load_vanilla(src_id, surface)
        pro = rp.prototype(van, rp.TINTS[i], surface, item_id=i)
        gl = rp.glow(pro, rp.TINTS[i])
        target, _ = truth_image(i, surface)
        record(i, surface, pixels(gl) == pixels(target), "wow_render config")

# --- pass 3: bags, reconstruction only (crisp round knobs not re-derived here) --------
print("== pass 3: bags vs crisp_bake, reconstruction only ==", flush=True)
for i in BAGS:
    for surface in ("small", "card"):
        record(i, surface, False, "crisp config not attempted fresh")

# --- pass 4: img-conv determinism + vanilla cache integrity (samples) -----------------
print("== pass 4: toolchain determinism samples ==", flush=True)
det = {}
sample = [(1, "card"), (50, "small"), (99, "card"), (128, "card"), (144, "small"),
          (116, "card")]
for i, surface in sample:
    stem = stem_for(i, surface)
    truth, tdir = truth_image(i, surface)
    src_png = os.path.join(tdir, stem + ".png")
    truth_tex = open(os.path.join(tdir, stem + ".tex"), "rb").read()
    outs = []
    for run in (1, 2):
        wd = os.path.join(SCRATCH, f"det{run}")
        os.makedirs(wd, exist_ok=True)
        p = os.path.join(wd, stem + ".png")
        shutil.copy(src_png, p)
        subprocess.run([rp.FF16, "img-conv", "-i", p, "--no-chunk-compression"],
                       capture_output=True)
        outs.append(open(os.path.join(wd, stem + ".tex"), "rb").read())
    det[stem] = {"stable": outs[0] == outs[1], "matches_banked_tex": outs[0] == truth_tex}
    print(f"  img-conv {stem}: stable={det[stem]['stable']} "
          f"matches_banked={det[stem]['matches_banked_tex']}", flush=True)
cache_ok = {}
for i, surface in [(1, "card"), (128, "small"), (150, "card")]:
    stem = stem_for(i, surface)
    sub = "equip_item" if surface == "card" else "equip_item_s"
    wd = os.path.join(SCRATCH, "vc")
    os.makedirs(wd, exist_ok=True)
    t = os.path.join(wd, stem + ".tex")
    shutil.copy(os.path.join(PACS, sub, "texture", stem + ".tex"), t)
    subprocess.run([rp.FF16, "tex-conv", "-i", t], capture_output=True)
    fresh = open(os.path.join(wd, stem + ".dds"), "rb").read()
    cached = open(os.path.join(rp.CACHE, stem + ".dds"), "rb").read()
    cache_ok[stem] = fresh == cached
    print(f"  vanilla_cache {stem}: {'MATCH' if fresh == cached else 'MISMATCH'}",
          flush=True)

# --- summary --------------------------------------------------------------------------
fresh_n = sum(1 for v in results.values() if v["fresh"])
recon_n = sum(1 for v in results.values() if not v["fresh"] and v.get("recon"))
none_n = sum(1 for v in results.values() if not v["fresh"] and not v.get("recon"))
summary = {"total": len(results), "fresh": fresh_n, "recon_only": recon_n,
           "neither": none_n,
           "neither_list": sorted(k for k, v in results.items()
                                  if not v["fresh"] and not v.get("recon")),
           "imgconv": det, "vanilla_cache": cache_ok}
json.dump({"summary": summary, "files": results}, open(OUT_JSON, "w"), indent=1)
print("\n=== CENSUS SUMMARY ===")
print(f"total files: {len(results)}  FRESH: {fresh_n}  RECON-only: {recon_n}  "
      f"NEITHER: {none_n}")
if summary["neither_list"]:
    print("NEITHER:", ", ".join(summary["neither_list"]))
print("json:", OUT_JSON)
