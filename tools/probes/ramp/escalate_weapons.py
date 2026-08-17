"""Distinctness escalation (owner rule 2026-08-16: 'does it look similar to vanilla?
Yes -> change it'). Bar calibrated from owner-approved items: mean dE >= 20 on the
small icon. Ladder per failing weapon: punch -> rotate-all -> forced body donor ->
hue swap (bank rule relaxed; farthest per-set-unused colour). Then final render of
both surfaces, strips, manifest, and texture bake."""
import json, os, subprocess
from PIL import Image
import ramp_prototype as rp

SCRATCH = os.path.dirname(os.path.abspath(__file__))
WORK = os.path.join(SCRATCH, "bake_work")
THRESH = 20.0

assign = json.load(open(os.path.join(SCRATCH, "weapon_assignments.json")))
items = json.load(open(os.path.join(rp.REPO, "data", "items.json")))["items"]
SRC = {it["id"]: it.get("iconSource") for it in items if it.get("iconSource")}
for i_str, a in assign.items():
    rp.TINTS[int(i_str)] = tuple(a["tint"])

def dom_hue(i):
    a = rp.analyze(rp.load_vanilla(SRC.get(i, i), "small"))
    cl = rp.hue_clusters(a["chrom"], a["hsv"], k_max=2)
    return cl[0][1] if cl else None

def de_mean(i):
    van = rp.load_vanilla(SRC.get(i, i), "small")
    pro = rp.prototype(van, rp.TINTS[i], "small", item_id=i)
    a = rp.analyze(van)
    pv, pp = van.load(), pro.load()
    tot = n = 0
    for p in a["solid"]:
        tot += rp._dE(pv[p][:3], pp[p][:3]); n += 1
    return tot / max(1, n)

BANK = [0.106, 0.624, 0.045, 0.247, 0.147, 0.335, 0.584, 0.200, 0.850, 0.231,
        0.460, 0.405, 0.291, 0.708, 0.498, 0.975, 0.932, 0.650]

by_cat = {}
for i_str, a in assign.items():
    by_cat.setdefault(a["cat"], []).append(int(i_str))

def swap_hue(i):
    """Farthest colour from the vanilla dominant that no set-mate uses."""
    cat = assign[str(i)]["cat"]
    used = {round(rp.TINTS[j][0], 2) for j in by_cat[cat] if j != i}
    dh = dom_hue(i) or rp.TINTS[i][0]
    cands = [h for h in BANK if round(h, 2) not in used]
    if not cands:
        cands = [(dh + 0.5) % 1.0]
    best = max(cands, key=lambda h: rp.hdist(h, dh))
    old = rp.TINTS[i]
    rp.TINTS[i] = (best, max(old[1], 0.5), old[2])
    assign[str(i)]["tint"] = [round(best, 3), rp.TINTS[i][1], old[2]]
    assign[str(i)]["hue_swapped"] = True

treatments = {}
report = []
for i_str in sorted(assign, key=int):
    i = int(i_str)
    lvl = 0
    swapped = False
    dh = dom_hue(i)
    if dh is not None and rp.hdist(rp.TINTS[i][0], dh) < 0.06:
        swap_hue(i); swapped = True  # camouflage: swap FIRST, ladder still applies
    d = de_mean(i)
    steps = [("PUNCH",), ("ROTATE_ALL",), ("FORCE_MODE_B",)]
    for (attr,) in steps:
        if d >= THRESH:
            break
        setattr(rp, attr, getattr(rp, attr) | {i}); lvl += 1
        d2 = de_mean(i)
        if d2 <= d:  # a rung that regresses gets rolled back, best config wins
            setattr(rp, attr, getattr(rp, attr) - {i}); lvl -= 1
        else:
            d = d2
    if d < THRESH and not swapped:
        swap_hue(i); swapped = True; lvl += 1
        d2 = de_mean(i)
        if d2 > d:
            d = d2
    treatments[i] = {"punch": i in rp.PUNCH, "rotate": i in rp.ROTATE_ALL,
                     "forceb": i in rp.FORCE_MODE_B, "swapped": swapped,
                     "tint": list(rp.TINTS[i]), "de": round(d, 1)}
    report.append((d, i, assign[str(i)]["name"], lvl))
    print(f"{i} {assign[str(i)]['name']:<20} level {lvl} dE {d:.1f}", flush=True)

json.dump(treatments, open(os.path.join(SCRATCH, "weapon_treatments.json"), "w"), indent=1)
json.dump(assign, open(os.path.join(SCRATCH, "weapon_assignments.json"), "w"), indent=1)
still = [r for r in report if r[0] < THRESH]
print(f"\n{len(report)} weapons, {len(still)} still under bar after full ladder:")
for d, i, name, lvl in still:
    print(f"  {i} {name} dE {d:.1f}")

# ---- final render: strips + bake, both surfaces -------------------------------------
manifest = {"sections": []}
for cat, ids in sorted(by_cat.items()):
    ids.sort()
    for surface in ("small", "card"):
        rows = {"van": [], "ship": [], "pro": [], "glow": []}
        for i in ids:
            src_id = SRC.get(i, i)
            van = rp.load_vanilla(src_id, surface)
            try:
                ship = rp.load_shipped(i, surface)
            except Exception:
                ship = van
            pro = rp.prototype(van, rp.TINTS[i], surface, item_id=i)
            gl = rp.glow(pro, rp.TINTS[i])
            rows["van"].append(van); rows["ship"].append(ship)
            rows["pro"].append(pro); rows["glow"].append(gl)
            stem = f"ei_{i:03d}_uitx" if surface == "card" else f"ei_s_{i:03d}_uitx"
            png = os.path.join(WORK, f"{stem}.png")
            gl.save(png)
            subprocess.run([rp.FF16, "img-conv", "-i", png, "--no-chunk-compression"],
                           capture_output=True)
            assert os.path.exists(os.path.join(WORK, f"{stem}.tex")), stem
        w, h = rows["van"][0].size
        pad, label_h = 2, 14
        W = len(ids) * (w + pad) + pad
        H = 4 * (h + pad) + pad + label_h
        sheet = Image.new("RGBA", (W, H), (255, 255, 255, 255))
        for r, key in enumerate(("van", "ship", "pro", "glow")):
            for c, im in enumerate(rows[key]):
                sheet.paste(im, (pad + c * (w + pad), label_h + pad + r * (h + pad)), im)
        sheet.save(os.path.join(SCRATCH, f"proto_strip_w_{cat}_{surface}.png"))
    manifest["sections"].append({
        "cat": cat, "ids": ids, "names": [assign[str(i)]["name"] for i in ids],
        "small": f"proto_strip_w_{cat}_small.png", "card": f"proto_strip_w_{cat}_card.png"})
    print(f"final render {cat}: {len(ids)} done", flush=True)
json.dump(manifest, open(os.path.join(SCRATCH, "weapon_manifest.json"), "w"), indent=1)
print("ESCALATION + FINAL RENDER DONE")
