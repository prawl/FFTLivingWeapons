"""LW-247 D4 emitter: replays the exact census configuration passes against the TRACKED probe
engine (tools/probes/ramp/ramp_engine.py, untouched here) and dumps the effective per-id
tables the PORTED production engine (tools/recolor_icons.py) consumes: data/icon_ramp/
treatments.json and data/icon_ramp/rims.json.

WHY REPLAY INSTEAD OF READING weapon_treatments.json DIRECTLY (B6 fix): that table's "punch"
field disagrees with the deployed census config for 40 ids (the deploy sets punch=True for
EVERY non-reserved weapon; the table's own "punch" is an earlier round's design-time flag) and
its "tint" field disagrees for 119 ids (post-hoc tweaks landed after the table was written).
tools/probes/lw247_repro_census.py and lw247_repro_census2.py are the last configuration
proven byte-exact against the live install (300/300 and 34/50, see their result jsons), so
this script mirrors their exact mutation ORDER rather than trusting any one static table.

DONOR CAPTURE (delta NEW-6): weapon_assignments.json's "donors" field is tint-choice
PROVENANCE (names of items that inspired a colour choice), not the runtime Mode-B donor a
render actually samples, and Mode-B membership itself lives in no committed table -- it falls
out of each item's own neutral-fraction test at render time. So this script monkeypatches the
probe engine's donor_ramp with an instrumented copy (identical candidate-scoring and ramp
math; the only addition is tagging each candidate with the vanilla icon id it came from) and
records, per (item, surface) that actually enters Mode B during the replay, the winning
donor's icon id. The ported engine (B7 fix) will sample that ONE pinned donor id through the
pipeline's own vanilla decode, never scanning %TEMP% at production time.

RIMS: rims.json's 16 shield + 4 bag rows are read straight out of the already-verified
tools/probes/lw247_census2_result.json extraction (GEN+RIM verdicts; card and small always
agree, so one row per id). The 21st row, helm 148 (Clarion), is not a census2 output -- 148's
glow rim was retuned AFTER the census bake shipped, so its row is the analytically computed
rim_color(0.055, 0.70) (cross-checked against the plan's reviewer-computed value at implement
time: rgb (242, 117, 56), alphas (170, 80, 0)).

Usage: python tools/probes/lw247_emit_tables.py
  writes data/icon_ramp/treatments.json, data/icon_ramp/rims.json, data/icon_ramp/README.md
"""
import importlib.util
import json
import os
import re
import sys
import tempfile

from PIL import Image

REPO = r"C:\Users\ptyRa\Dev\FFTLivingWeapons"
RAMP = os.path.join(REPO, "tools", "probes", "ramp")
OUT_DIR = os.path.join(REPO, "data", "icon_ramp")
CENSUS_DIR = os.path.join(REPO, "tools", "probes")

SCRATCH = os.path.join(tempfile.gettempdir(), "lw247_emit")
os.makedirs(SCRATCH, exist_ok=True)

spec = importlib.util.spec_from_file_location(
    "ramp_engine", os.path.join(RAMP, "ramp_engine.py"))
rp = importlib.util.module_from_spec(spec)
sys.modules["ramp_engine"] = rp
spec.loader.exec_module(rp)
rp.SCRATCH = SCRATCH  # keep load_shipped()'s tex-conv litter out of the tracked probe folder

assign = json.load(open(os.path.join(RAMP, "weapon_assignments.json")))
treat = json.load(open(os.path.join(RAMP, "weapon_treatments.json")))
reserved = set(json.load(open(os.path.join(RAMP, "reserved_weapons.json"))))
items = json.load(open(os.path.join(REPO, "data", "items.json")))["items"]
SRC = {it["id"]: it["iconSource"] for it in items if it.get("iconSource")}
NAME = {it["id"]: it.get("name") for it in items}

BAGS = [115, 116, 117, 118]
WEAPON_IDS = sorted(int(k) for k in assign)
NON_BAG_WEAPON_IDS = sorted(i for i in WEAPON_IDS if i not in BAGS)

# --- donor instrumentation ---------------------------------------------------------------
# Verbatim candidate-scoring and ramp math from ramp_engine.donor_ramp; the only addition is
# tagging each scanned candidate with the vanilla icon id it came from, and recording the
# winner against the (item, surface) that is currently rendering (set by the replay loops
# below via CTX, since donor_ramp itself receives only a target hue and a surface).
DONOR_LOG = {}          # (item_id, surface) -> donor icon id
CTX = {"item": None, "surface": None}
_cand_cache = {}


def _instrumented_donor_ramp(target_hue, surface):
    key = surface
    if key not in _cand_cache:
        cands = []
        pat = re.compile(r"^ei_s_(\d+)_uitx\.dds$" if surface == "small" else r"^ei_(\d+)_uitx\.dds$")
        for fn in os.listdir(rp.CACHE):
            m = pat.match(fn)
            if not m:
                continue
            iid = int(m.group(1))
            im = Image.open(os.path.join(rp.CACHE, fn)).convert("RGBA")
            a = rp.analyze(im)
            if len(a["chrom"]) < 40:
                continue
            cl = rp.hue_clusters(a["chrom"], a["hsv"], k_max=2)
            if not cl:
                continue
            mem, ch = cl[0]
            sats = [a["hsv"][p][1] for p in mem]
            if sum(sats) / len(sats) < 0.30:
                continue
            cands.append((ch, mem, a["hsv"], iid))
        _cand_cache[key] = cands
    cands = _cand_cache[key]
    best = min(cands, key=lambda t: rp.hdist(t[0], target_hue))
    mem, hsv, donor_id = best[1], best[2], best[3]
    if CTX["item"] is not None:
        DONOR_LOG[(CTX["item"], CTX["surface"])] = donor_id
    pts = sorted(mem, key=lambda p: hsv[p][2])
    steps = []
    n = len(pts)
    for i in range(5):
        seg = pts[int(i * n / 5):int((i + 1) * n / 5)]
        hs = rp.circ_mean([hsv[p][0] for p in seg], [max(0.05, hsv[p][1]) for p in seg])
        ss = sum(hsv[p][1] for p in seg) / len(seg)
        vs = sum(hsv[p][2] for p in seg) / len(seg)
        steps.append((hs, ss, vs))
    centre = rp.circ_mean([h for h, _, _ in steps], [max(0.05, s) for _, s, _ in steps])
    disciplined = []
    for hs, ss, vs in steps:
        off = ((hs - centre + 0.5) % 1.0) - 0.5
        off = max(-0.06, min(0.06, off))
        disciplined.append(((target_hue + off) % 1.0, ss, vs))
    return disciplined


rp.donor_ramp = _instrumented_donor_ramp

# --- pass 1 donor-capture replay: shields + helms, module-default config (mirrors
# lw247_repro_census.py pass 1's branching exactly, including its KEEP_SHIPPED skip) --------
# Mode B is a data-dependent branch of _prototype_inner (neutral_frac > 0.5), not a
# weapon-only concept: any ramp id can land there. The original census1 pass 1 only calls
# prototype() -- and so only donor_ramp -- for shield/helm ids NOT in KEEP_SHIPPED (those
# take load_shipped() instead, which the port never calls, so they never need a donor either).
print("== replaying pass 1 (shields + helms, donor capture only) ==", flush=True)
for i in rp.SHIELDS + rp.HELMS:
    if i in rp.KEEP_SHIPPED:
        continue
    for surface in ("small", "card"):
        CTX["item"], CTX["surface"] = i, surface
        van = rp.load_vanilla(i, surface)
        base = rp.prototype(van, rp.TINTS[i], surface, item_id=i)
        if i in rp.POP:
            base = rp.pop_filter(base)
    print(f"  id{i}", end="\r", flush=True)
print()

# --- pass 2 config mutation (mirrors lw247_repro_census.py pass 2 verbatim) ---------------
rp.RESERVED_POP = set(reserved)
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

# --- pass 2 render loop: trigger Mode A/B decisions + donor capture, non-bag weapons ------
print("== replaying pass 2 (non-bag weapons) ==", flush=True)
for i in NON_BAG_WEAPON_IDS:
    for surface in ("small", "card"):
        CTX["item"], CTX["surface"] = i, surface
        src_id = SRC.get(i, i)
        van = rp.load_vanilla(src_id, surface)
        rp.prototype(van, rp.TINTS[i], surface, item_id=i)
    print(f"  id{i}", end="\r", flush=True)
print()

# --- pass B config overwrite (mirrors lw247_repro_census2.py pass B verbatim) -------------
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

print("== replaying pass B (bags) ==", flush=True)
for i in BAGS:
    for surface in ("small", "card"):
        CTX["item"], CTX["surface"] = i, surface
        src_id = SRC.get(i, i)
        van = rp.load_vanilla(src_id, surface)
        rp.prototype(van, rp.TINTS[i], surface, item_id=i)
    print(f"  id{i}", end="\r", flush=True)
print()

# --- treatments.json: weapon rows ---------------------------------------------------------
def note_for_weapon(i):
    a = assign.get(str(i), {})
    name = a.get("name", NAME.get(i, "?"))
    donors = a.get("donors") or []
    tribunal = a.get("tribunal", "")
    bits = [f"{name}: tint chosen from {donors or 'no cited donor'}"]
    if tribunal:
        bits.append(f"({tribunal})")
    if i == 113:
        bits.append("NIT (LW-247 census, carried forward): kept its vanilla name but is "
                     "absent from reserved_weapons.json; harmless, its art chroma sits below "
                     "the anchors gate's floor.")
    return " ".join(bits)


weapon_rows = {}
for i in WEAPON_IDS:
    row = {
        "kind": "weapon",
        "tint": list(rp.TINTS[i]),
        "reserved": i in rp.RESERVED_POP,
        "punch": i in rp.PUNCH,
        "rotate": i in rp.ROTATE_ALL,
        "forceb": i in rp.FORCE_MODE_B,
        "muted": i in rp.MUTED,
        "white_spec": i in rp.WHITE_SPEC,
        "soft_spec": i in rp.SOFT_SPEC,
        "outline_black": i in rp.OUTLINE_BLACK,
        "deep_damp": i in rp.DEEP_DAMP,
        "vscale_override": rp.VSCALE_OVERRIDE.get(i),
        "note": note_for_weapon(i),
    }
    dk_card, dk_small = DONOR_LOG.get((i, "card")), DONOR_LOG.get((i, "small"))
    if dk_card is not None or dk_small is not None:
        row["donor"] = {"card": dk_card, "small": dk_small}
    weapon_rows[str(i)] = row

# --- treatments.json: shield + helm rows (fixed module-level config, documentation only) --
SHIELD_IDS = list(rp.SHIELDS)
HELM_IDS = list(rp.HELMS)
SHIELD_PUNCH_BASE = {130, 134, 136, 138, 140, 142}
SHIELD_POP = {134, 136, 138}
HELM_PUNCH_ADD = {147, 148, 149, 150, 151, 152, 154}
HELM_ROTATE_ALL = {145, 146, 149, 150, 151, 152, 154}
WARM_TRIM_VANILLA = {145}
KEEP_SHIPPED_HELMS = {147, 148, 155, 156}

for i in SHIELD_IDS:
    bits = []
    if i in SHIELD_PUNCH_BASE:
        bits.append("PUNCH base (module default)")
    if i in SHIELD_POP:
        bits.append("POP (module default)")
    tail = f" ({', '.join(bits)})" if bits else " (no punch/pop)"
    row = {
        "kind": "shield",
        "note": f"{NAME.get(i, '?')}: fixed module-level shield config" + tail,
    }
    dk_card, dk_small = DONOR_LOG.get((i, "card")), DONOR_LOG.get((i, "small"))
    if dk_card is not None or dk_small is not None:
        row["donor"] = {"card": dk_card, "small": dk_small}
    weapon_rows[str(i)] = row

for i in HELM_IDS:
    bits = []
    if i in HELM_PUNCH_ADD:
        bits.append("PUNCH addition")
    if i in HELM_ROTATE_ALL:
        bits.append("ROTATE_ALL")
    if i in WARM_TRIM_VANILLA:
        bits.append("WARM_TRIM_VANILLA")
    if i in KEEP_SHIPPED_HELMS:
        bits.append("KEEP_SHIPPED: old-engine body kept, rendered outside the ramp dispatch")
    if i == 144:
        bits.append("144 coif map: old-engine render, never a mod-tree read (B1 fix)")
    tail = f" ({', '.join(bits)})" if bits else " (pure ramp render)"
    row = {
        "kind": "helm",
        "note": f"{NAME.get(i, '?')}: fixed module-level helm config" + tail,
    }
    dk_card, dk_small = DONOR_LOG.get((i, "card")), DONOR_LOG.get((i, "small"))
    if dk_card is not None or dk_small is not None:
        row["donor"] = {"card": dk_card, "small": dk_small}
    weapon_rows[str(i)] = row

os.makedirs(OUT_DIR, exist_ok=True)
treatments_path = os.path.join(OUT_DIR, "treatments.json")
json.dump({k: weapon_rows[k] for k in sorted(weapon_rows, key=int)},
          open(treatments_path, "w", encoding="utf-8"), indent=1)
print(f"\ntreatments.json: {len(weapon_rows)} rows -> {treatments_path}")
mode_b_hits = sorted(DONOR_LOG)
print(f"  Mode-B donor hits captured: {len(mode_b_hits)} (item, surface) pairs")

# --- rims.json: 16 shield + 4 bag rows from the verified census2 extraction, + helm 148 ----
census2 = json.load(open(os.path.join(CENSUS_DIR, "lw247_census2_result.json")))
rim_rows = {}
for key, v in census2.items():
    if not v.get("gen_body"):
        continue  # BODY-VEND: no rim to extract here, its body is vendored wholesale
    m = re.match(r"^ei_(?:s_)?(\d+)_uitx$", key)
    iid = int(m.group(1))
    if iid in rim_rows:
        continue  # card and small agree (checked by hand at implement time); one row per id
    rgb = v["rim"]
    ia, oa, ta = v["alphas"]
    family = "shield" if iid in SHIELD_IDS else "bag"
    rim_rows[str(iid)] = {
        "rgb": rgb, "inner_a": ia, "outer_a": oa, "third_a": ta,
        "note": f"{NAME.get(iid, '?')}: {family} rim, extracted from census2 (GEN+RIM verdict)",
    }

# helm 148 (Clarion): retuned AFTER the census bake, so its rim never appeared in census2.
# Value is the analytic rim_color(0.055, 0.70) at the default inner alpha 170 -- verified at
# implement time against the plan's reviewer-computed figure (rgb (242, 117, 56), alphas
# (170, 80, 0)).
_rgb148 = rp.rim_color(0.055, 0.70)
assert _rgb148 == (242, 117, 56), f"rim_color(0.055, 0.70) drifted: {_rgb148}"
rim_rows["148"] = {
    "rgb": list(_rgb148), "inner_a": 170, "outer_a": 80, "third_a": 0,
    "note": "Clarion Helm: KEEP_SHIPPED body kept at its current items.json tint (D3 exception); "
            "only the glow rim moves, to the retuned identity rim_color(0.055, 0.70).",
}

rims_path = os.path.join(OUT_DIR, "rims.json")
json.dump({k: rim_rows[k] for k in sorted(rim_rows, key=int)},
          open(rims_path, "w", encoding="utf-8"), indent=1)
print(f"rims.json: {len(rim_rows)} rows -> {rims_path}")

# --- README.md: schema + environment pin ---------------------------------------------------
readme = f"""# Ramp engine data tables (LW-247)

STATUS: CONTRACT. Committed inputs the ported ramp engine (tools/recolor_icons.py) reads at
build time. Generated by tools/probes/lw247_emit_tables.py (treatments.json, rims.json, this
file) and tools/probes/lw247_extract_bodies.py (bodies/, its own README.md). Regenerate by
re-running both scripts; do not hand-edit the generated files, the same rule
data/items.json's own header states for the modloader tables.

## treatments.json

One row per of the 150 ramp ids (121 weapons incl. bags, 16 shields, 13 helms), keyed by
item id as a string.

Weapon rows (`"kind": "weapon"`) carry the effective per-id engine configuration, replayed
from tools/probes/lw247_repro_census.py + lw247_repro_census2.py's exact mutation order
(not read from weapon_treatments.json directly -- see this script's docstring for why that
table disagrees with the deployed config on 40 ids): `tint` (the final [hue, sat, value_mult]
after the bag desaturation pass), `reserved`, `punch`, `rotate`, `forceb`, `muted`,
`white_spec`, `soft_spec`, `outline_black`, `deep_damp`, `vscale_override` (float or null),
an optional `donor` ({{"card": id, "small": id}}, present only for items that actually entered
Mode B on that surface during the replay), and a `note`.

Shield and helm rows (`"kind": "shield"` / `"kind": "helm"`) carry only a `note`: their engine
configuration is FIXED module-level constants in the ported engine (PUNCH base, POP,
ROTATE_ALL, WARM_TRIM_VANILLA, KEEP_SHIPPED, the TWIN groups), not something a census replay
produces, so the note documents which fixed sets the id belongs to rather than duplicating
config a code reader can already see.

## rims.json

21 rows: 16 shields (complement rim) + 4 bags (crisp rim) extracted verbatim from the already
census2-verified rim bands, plus helm 148 (Clarion), whose rim was retuned after the census
bake and is instead the analytic `rim_color(0.055, 0.70)`. Each row is
`{{"rgb": [r,g,b], "inner_a": int, "outer_a": int, "third_a": int, "note": str}}`. An id
ABSENT from this table (including the six drift-helms 145/149/150/151/152/154) uses the
engine's default `rim_color(tint)` path; the Phase 2 adversarial reviewer verified those six
default rims equal `rim_color(migrated tint)` by hand, so they need no row.

## bodies/

16 vendored body PNGs for the (id, surface) pairs the ported engine cannot re-render from
vanilla + these tables (census2 BODY-VEND verdicts). See bodies/README.md for the per-file
provenance table.

## Environment pin

Recorded at implement time (2026-08-18): Python {sys.version.split()[0]}, Pillow {__import__("PIL").__version__},
FF16Tools.CLI 1.13.2 (directory name; the binary's own `--version` self-reports 1.13.0 -- both
noted since they disagree and neither was investigated further, this arc being scoped to the
icon pipeline and not the tool's own versioning).
"""
open(os.path.join(OUT_DIR, "README.md"), "w", encoding="utf-8").write(readme)
print(f"README.md -> {os.path.join(OUT_DIR, 'README.md')}")
