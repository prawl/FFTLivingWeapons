"""Bag v2 rounds: muted interior, flashy glow (owner verdict 2026-08-16 evening).

Step 0 regression: with every new knob empty and the reconstructed h3 config applied,
ramp_v2 must reproduce the deployed bake bit-for-bit (proves the knob edits are pure
additions). Then three muted candidates render for the review row.
"""
import json, os, sys
from PIL import Image

BANK = r"C:\Users\ptyRa\Downloads\fft_ref\session_bank_2026-08-16"
HERE = os.path.dirname(os.path.abspath(__file__))
OUT = os.path.join(HERE, "bag_v2")
os.makedirs(OUT, exist_ok=True)
sys.path.insert(0, HERE)
import ramp_v2 as rp

assign = json.load(open(os.path.join(BANK, "weapon_assignments.json")))
reserved = set(json.load(open(os.path.join(BANK, "reserved_weapons.json"))))
BAGS = [115, 116, 117, 118]
ORIG_TINT = {i: tuple(assign[str(i)]["tint"]) for i in BAGS}


def stem_for(i, surface):
    return f"ei_{i:03d}_uitx" if surface == "card" else f"ei_s_{i:03d}_uitx"


def reset_bags():
    rp.RESERVED_POP = reserved - {116}
    rp.PUNCH = rp.PUNCH - set(BAGS)
    rp.ROTATE_ALL = rp.ROTATE_ALL - set(BAGS)
    rp.WHITE_SPEC = set()
    rp.OUTLINE_BLACK = set()
    rp.SOFT_SPEC = set()
    rp.MUTED = set()
    rp.DEEP_DAMP = set()
    rp.VSCALE_OVERRIDE = {}
    for i in BAGS:
        rp.TINTS[i] = ORIG_TINT[i]


# --- step 0: regression against the deployed bake -------------------------------------
reset_bags()
rp.PUNCH = rp.PUNCH | set(BAGS)
rp.ROTATE_ALL = rp.ROTATE_ALL | {115, 117, 118}
rp.WHITE_SPEC = set(BAGS)
rp.OUTLINE_BLACK = set(BAGS)
ok = True
for i in BAGS:
    for surface in ("small", "card"):
        van = rp.load_vanilla(i, surface)
        gl = rp.glow(rp.prototype(van, rp.TINTS[i], surface, item_id=i), rp.TINTS[i])
        truth = Image.open(os.path.join(BANK, "bake_work", f"{stem_for(i, surface)}.png")).convert("RGBA")
        same = list(gl.convert("RGBA").getdata()) == list(truth.getdata())
        ok = ok and same
        if not same:
            print("REGRESSION FAIL:", stem_for(i, surface))
print("REGRESSION:", "PASS (knob edits are pure additions)" if ok else "FAIL")
if not ok:
    sys.exit(1)

# --- candidates -----------------------------------------------------------------------
FLASH = dict(inner_a=235, outer_a=135, third_a=60, min_de=40.0)
RIM_SAT = {i: max(ORIG_TINT[i][1], 0.80) for i in BAGS}

VARIANTS = {
    # A: muted body, the artist's own chroma ceiling; highlights left to the engine
    "A": dict(sat=0.45, soft=False, deep=False),
    # B: A plus the soft cloth sheen on the lightest fifth
    "B": dict(sat=0.45, soft=True, deep=False),
    # C: deep mute -- lower chroma target, flat 0.8 damp, sheen kept
    "C": dict(sat=0.35, soft=True, deep=True),
}

for tag, cfg in VARIANTS.items():
    reset_bags()
    rp.ROTATE_ALL = rp.ROTATE_ALL | {115, 117, 118}
    rp.OUTLINE_BLACK = set(BAGS)
    rp.MUTED = set(BAGS)
    rp.VSCALE_OVERRIDE = {115: 1.18, 116: 1.15}
    if cfg["soft"]:
        rp.SOFT_SPEC = set(BAGS)
    if cfg["deep"]:
        rp.DEEP_DAMP = set(BAGS)
    for i in BAGS:
        h, s, vm = ORIG_TINT[i]
        rp.TINTS[i] = (h, cfg["sat"], vm)
    for i in BAGS:
        for surface in ("small", "card"):
            van = rp.load_vanilla(i, surface)
            body = rp.prototype(van, rp.TINTS[i], surface, item_id=i)
            gl = rp.glow(body, rp.TINTS[i], rim_sat=RIM_SAT[i], **FLASH)
            body.save(os.path.join(OUT, f"{tag}_body_{stem_for(i, surface)}.png"))
            gl.save(os.path.join(OUT, f"{tag}_glow_{stem_for(i, surface)}.png"))
    print(f"variant {tag} rendered", flush=True)

# vanilla + current-deployed reference copies for the page
for i in BAGS:
    for surface in ("small", "card"):
        rp.load_vanilla(i, surface).save(os.path.join(OUT, f"van_{stem_for(i, surface)}.png"))
        Image.open(os.path.join(BANK, "bake_work", f"{stem_for(i, surface)}.png")) \
            .save(os.path.join(OUT, f"cur_{stem_for(i, surface)}.png"))
print("BAG V2 RENDER DONE")
