"""LW-247 D4: extracts the 16 vendored body PNGs data/icon_ramp/bodies/ needs -- the (id,
surface) pairs whose body the ported ramp engine cannot re-render from vanilla + committed
tables (census2 BODY-VEND verdicts, tools/probes/lw247_census2_result.json): 6 shield cards
(130/132/134/136/138/140) and 7 helm files (145 card, 149 card, 150/151/152/154 card+small).

EXTRACTION RULE, proven by the reconstruction identity in lw247_repro_census.py's
recon_check / lw247_repro_census2.py's extract_rim: body + glow(rim read out of the target)
== target. So the body layer is target's OWN pixels inside the smoothed vanilla silhouette
(the same contour rule ramp_engine.glow() uses to place its rim: alpha >= 160 body, one
majority smooth) and VANILLA's pixels everywhere outside it, including the far haze glow()
never repaints. This script does NOT compute the rim (that is data/icon_ramp/rims.json,
already verified against the census2 extraction); it only isolates the body layer, so that
compositing rims.json's rim over one of these bodies reproduces the shipped texture
pixel-exact.

Ground truth per family (hash-proven equal to the install 2026-08-18, see
lw247_repro_census.py's docstring): shields -> shield_flashy_2026-08-16/complement_soft_bake
PNGs, helms -> session_bank_2026-08-16/bake_work PNGs.

Usage: python tools/probes/lw247_extract_bodies.py
  writes data/icon_ramp/bodies/*.png + data/icon_ramp/bodies/README.md
"""
import importlib.util
import json
import os
import re
import sys

from PIL import Image

REPO = r"C:\Users\ptyRa\Dev\FFTLivingWeapons"
RAMP = os.path.join(REPO, "tools", "probes", "ramp")
BANK = r"C:\Users\ptyRa\Downloads\fft_ref"
BAKE = os.path.join(BANK, "session_bank_2026-08-16", "bake_work")
SOFT = os.path.join(BANK, "shield_flashy_2026-08-16", "complement_soft_bake")
OUT_DIR = os.path.join(REPO, "data", "icon_ramp", "bodies")
CENSUS2 = os.path.join(REPO, "tools", "probes", "lw247_census2_result.json")

spec = importlib.util.spec_from_file_location(
    "ramp_engine", os.path.join(RAMP, "ramp_engine.py"))
rp = importlib.util.module_from_spec(spec)
sys.modules["ramp_engine"] = rp
spec.loader.exec_module(rp)

SHIELDS = set(rp.SHIELDS)


def parse_stem(stem):
    m = re.match(r"^ei_(s_)?(\d+)_uitx$", stem)
    return int(m.group(2)), ("small" if m.group(1) else "card")


def smoothed_mask(im):
    """Same contour rule as ramp_engine.glow(): alpha >= 160 body, one majority smooth."""
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


census2 = json.load(open(CENSUS2))
vendored = sorted(k for k, v in census2.items() if not v.get("gen_body"))

os.makedirs(OUT_DIR, exist_ok=True)
table_rows = []
for key in vendored:
    iid, surface = parse_stem(key)
    family = "shield" if iid in SHIELDS else "helm"
    bank_dir = SOFT if family == "shield" else BAKE
    bank_name = ("shield_flashy_2026-08-16/complement_soft_bake" if family == "shield"
                 else "session_bank_2026-08-16/bake_work")
    van = rp.load_vanilla(iid, surface)
    target = Image.open(os.path.join(bank_dir, key + ".png")).convert("RGBA")
    if van.size != target.size:
        raise SystemExit(f"{key}: size mismatch van={van.size} target={target.size}")
    mask = smoothed_mask(van)
    body = target.copy()
    bp, vp = body.load(), van.load()
    w, h = van.size
    for y in range(h):
        for x in range(w):
            if (x, y) in mask:
                continue
            bp[x, y] = vp[x, y]
    out_name = f"{key}.png"
    body.save(os.path.join(OUT_DIR, out_name))
    print(f"{key}: extracted -> {out_name}  ({family}, {bank_name})")
    table_rows.append(f"| {out_name} | {iid} | {surface} | {family} | {bank_name} | BODY-VEND |")

readme = f"""# Vendored ramp body PNGs (LW-247 D4)

STATUS: CONTRACT. Sixteen (id, surface) bodies the ported ramp engine cannot re-render from
vanilla + the committed tables (census2 BODY-VEND verdicts,
tools/probes/lw247_census2_result.json). Extracted by tools/probes/lw247_extract_bodies.py:
each file is the target texture restored to vanilla OUTSIDE the smoothed vanilla silhouette
(the same contour rule tools/probes/ramp/ramp_engine.py's glow() uses to place its rim), so
recompositing data/icon_ramp/rims.json's rim over one of these bodies reproduces the shipped
texture pixel-exact -- the reconstruction identity proven in tools/probes/lw247_repro_census.py
and lw247_repro_census2.py.

Regenerate with `python tools/probes/lw247_extract_bodies.py`; do not hand-edit these PNGs.

Environment pin: Python {sys.version.split()[0]}, Pillow {__import__("PIL").__version__},
FF16Tools.CLI 1.13.2 (directory-name version; not invoked by this script, which only reads
already-decoded vanilla_cache DDS and banked PNGs).

| file | id | surface | family | banked source round | census2 verdict |
|---|---|---|---|---|---|
{chr(10).join(table_rows)}

{len(vendored)} files extracted.
"""
open(os.path.join(OUT_DIR, "README.md"), "w", encoding="utf-8").write(readme)
print(f"\n{len(vendored)} vendored bodies -> {OUT_DIR}")
