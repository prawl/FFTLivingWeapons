"""LW-247 census pass 2: for every file census pass 1 marked RECON-only, split BODY
from RIM. Pass 1 rendered fresh bodies but compared them under the DEFAULT glow, so a
file whose body is fine and whose rim simply is not the default (complement shield rims,
crisp bag rims, helm tints retuned after the deploy) landed in RECON. Here each file is
re-tested as: fresh body under its round's config + the rim READ OUT OF THE TARGET.

Verdicts per file:
  GEN+RIM   fresh body + extracted rim == target  (fully generative given a rim table)
  BODY-VEND fresh body differs (true engine drift); reconstruction already proved the
            body is recoverable from the target, so the body must be vendored
Also re-diagnoses ei_116_uitx (card), pass 1's only NEITHER, with a pixel-level report.

Usage: python tools/probes/lw247_repro_census2.py
"""
import importlib.util
import json
import os
import sys
import tempfile

from PIL import Image

REPO = r"C:\Users\ptyRa\Dev\FFTLivingWeapons"
RAMP = os.path.join(REPO, "tools", "probes", "ramp")
BANK = r"C:\Users\ptyRa\Downloads\fft_ref"
BAKE = os.path.join(BANK, "session_bank_2026-08-16", "bake_work")
SOFT = os.path.join(BANK, "shield_flashy_2026-08-16", "complement_soft_bake")
CRISP = os.path.join(BANK, "bag_round_2026-08-16", "crisp_bake")
OUT_JSON = os.path.join(os.path.dirname(os.path.abspath(__file__)),
                        "lw247_census2_result.json")

SCRATCH = os.path.join(tempfile.gettempdir(), "lw247_census")
os.makedirs(SCRATCH, exist_ok=True)

spec = importlib.util.spec_from_file_location(
    "ramp_engine", os.path.join(RAMP, "ramp_engine.py"))
rp = importlib.util.module_from_spec(spec)
sys.modules["ramp_engine"] = rp
spec.loader.exec_module(rp)
rp.SCRATCH = SCRATCH

assign = json.load(open(os.path.join(RAMP, "weapon_assignments.json")))
reserved = set(json.load(open(os.path.join(RAMP, "reserved_weapons.json"))))

BAGS = [115, 116, 117, 118]
DRIFT_HELM = {145: ["card"], 149: ["card"], 150: ["card", "small"],
              151: ["card", "small"], 152: ["card", "small"], 154: ["card", "small"]}


def stem_for(i, surface):
    return f"ei_{i:03d}_uitx" if surface == "card" else f"ei_s_{i:03d}_uitx"


def pixels(im):
    return list(im.convert("RGBA").getdata())


def truth_image(i, surface):
    d = SOFT if i in rp.SHIELDS else (CRISP if i in BAGS else BAKE)
    return Image.open(os.path.join(d, stem_for(i, surface) + ".png")).convert("RGBA")


def smoothed_mask(im):
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


def extract_rim(i, surface):
    """Read (rim_rgb, inner_a, outer_a, third_a) out of the target's glow bands."""
    van = rp.load_vanilla(i, surface)
    target = truth_image(i, surface)
    mask = smoothed_mask(van)
    tp = target.load()
    w, h = van.size
    band = {}
    for y in range(h):
        for x in range(w):
            if (x, y) in mask:
                continue
            d = min((max(abs(dx), abs(dy))
                     for dx in range(-3, 4) for dy in range(-3, 4)
                     if (x + dx, y + dy) in mask), default=99)
            if d in (1, 2, 3):
                band.setdefault(d, set()).add(tp[x, y])
    rgb = {p[:3] for p in band[1]}.pop()
    inner_a = {p[3] for p in band[1]}.pop()
    outer_a = {p[3] for p in band[2]}.pop()
    third = band.get(3, set())
    third_a = 0
    if {p[:3] for p in third} == {rgb} and len({p[3] for p in third}) == 1:
        third_a = {p[3] for p in third}.pop()
    return rgb, inner_a, outer_a, third_a


results = {}


def test_file(i, surface, body):
    rgb, ia, oa, ta = extract_rim(i, surface)
    gl = rp.glow(body, rp.TINTS[i], inner_a=ia, outer_a=oa, third_a=ta, rim_rgb=rgb)
    target = truth_image(i, surface)
    ok = pixels(gl) == pixels(target)
    key = stem_for(i, surface)
    results[key] = {"gen_body": ok, "rim": list(rgb), "alphas": [ia, oa, ta]}
    print(f"{key}: {'GEN+RIM' if ok else 'BODY-VEND'}  rim={rgb} a=({ia},{oa},{ta})",
          flush=True)
    return gl, target


# --- pass A: shields + drifted helm files, deploy config (module defaults) ------------
print("== pass A: shields + drifted helms, fresh body + extracted rim ==", flush=True)
for i in rp.SHIELDS:
    for surface in ("small", "card"):
        van = rp.load_vanilla(i, surface)
        body = rp.prototype(van, rp.TINTS[i], surface, item_id=i)
        if i in rp.POP:
            body = rp.pop_filter(body)
        test_file(i, surface, body)
for i, surfaces in sorted(DRIFT_HELM.items()):
    for surface in surfaces:
        van = rp.load_vanilla(i, surface)
        body = rp.prototype(van, rp.TINTS[i], surface, item_id=i)
        test_file(i, surface, body)

# --- pass B: bags, variant C body config + extracted crisp rim ------------------------
print("== pass B: bags, deep-mute C bodies + extracted rims ==", flush=True)
ORIG_TINT = {i: tuple(assign[str(i)]["tint"]) for i in BAGS}
rp.RESERVED_POP = reserved - {116}
rp.PUNCH = rp.PUNCH - set(BAGS)
rp.ROTATE_ALL = (rp.ROTATE_ALL - set(BAGS)) | {115, 117, 118}
rp.WHITE_SPEC = set()
rp.OUTLINE_BLACK = set(BAGS)
rp.SOFT_SPEC = set(BAGS)
rp.MUTED = set(BAGS)
rp.DEEP_DAMP = set(BAGS)
rp.VSCALE_OVERRIDE = {115: 1.18, 116: 1.15}
for i in BAGS:
    h, s, vm = ORIG_TINT[i]
    rp.TINTS[i] = (h, 0.35, vm)
diag = None
for i in BAGS:
    for surface in ("small", "card"):
        van = rp.load_vanilla(i, surface)
        body = rp.prototype(van, rp.TINTS[i], surface, item_id=i)
        gl, target = test_file(i, surface, body)
        if i == 116 and surface == "card":
            diag = (gl, target, van)

# --- diagnosis: ei_116_uitx card ------------------------------------------------------
if diag and not results["ei_116_uitx"]["gen_body"]:
    gl, target, van = diag
    mask = smoothed_mask(van)
    gp, tp = gl.load(), target.load()
    diffs = []
    for y in range(van.height):
        for x in range(van.width):
            if gp[x, y] != tp[x, y]:
                diffs.append((x, y, gp[x, y], tp[x, y], (x, y) in mask))
    print(f"\nei_116_uitx card diagnosis: {len(diffs)} px differ "
          f"({sum(1 for d in diffs if d[4])} inside body mask)")
    for d in diffs[:12]:
        print("   ", d)
    results["ei_116_uitx"]["diag_px"] = len(diffs)
    results["ei_116_uitx"]["diag_inside_mask"] = sum(1 for d in diffs if d[4])

json.dump(results, open(OUT_JSON, "w"), indent=1)
gen = sum(1 for v in results.values() if v["gen_body"])
print(f"\n=== CENSUS2 SUMMARY ===\nfiles tested: {len(results)}  GEN+RIM: {gen}  "
      f"BODY-VEND: {len(results) - gen}")
print("json:", OUT_JSON)
