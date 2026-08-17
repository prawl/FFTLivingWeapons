"""Render all weapons with current assignments/treatments/reserved sets; strips + bake."""
import json, os, subprocess
from PIL import Image
import ramp_prototype as rp

WORK = "bake_work"
assign = json.load(open("weapon_assignments.json"))
treat = json.load(open("weapon_treatments.json"))
reserved = set(json.load(open("reserved_weapons.json")))
items = json.load(open(os.path.join(rp.REPO, "data", "items.json")))["items"]
SRC = {it["id"]: it.get("iconSource") for it in items if it.get("iconSource")}

rp.RESERVED_POP = reserved
for i_str, a in assign.items():
    i = int(i_str)
    rp.TINTS[i] = tuple(a["tint"])
    t = treat.get(i_str, {})
    if i in reserved:
        continue
    rp.PUNCH = rp.PUNCH | {i}  # wow default: every non-reserved weapon paints at punch strength
    if t.get("rotate"): rp.ROTATE_ALL = rp.ROTATE_ALL | {i}
    if t.get("forceb"): rp.FORCE_MODE_B = rp.FORCE_MODE_B | {i}

by_cat = {}
for i_str, a in assign.items():
    by_cat.setdefault(a["cat"], []).append(int(i_str))

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
        w, h = rows["van"][0].size
        pad, label_h = 2, 14
        W = len(ids) * (w + pad) + pad
        H = 4 * (h + pad) + pad + label_h
        sheet = Image.new("RGBA", (W, H), (255, 255, 255, 255))
        for r, key in enumerate(("van", "ship", "pro", "glow")):
            for c, im in enumerate(rows[key]):
                sheet.paste(im, (pad + c * (w + pad), label_h + pad + r * (h + pad)), im)
        sheet.save(f"proto_strip_w_{cat}_{surface}.png")
    manifest["sections"].append({
        "cat": cat, "ids": ids, "names": [assign[str(i)]["name"] for i in ids],
        "small": f"proto_strip_w_{cat}_small.png", "card": f"proto_strip_w_{cat}_card.png"})
    print(f"{cat} done", flush=True)
json.dump(manifest, open("weapon_manifest.json", "w"), indent=1)
print("WOW RENDER DONE")
