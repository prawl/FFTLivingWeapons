"""LW-247 Phase 0 premise P6 (KEEP_SHIPPED circularity check), promoted from the session
scratchpad and extended to cover the Padded Coif's colour map alongside the four KEEP_SHIPPED
helms.

Question: for helms 144 (Padded Coif, colour map only), 147 (Storm Barbut), 148 (Clarion
Helm), 155 (Genji Helm) and 156 (Grand Helm), is the mod tree tex the ramp deploy's route()
branch will read (B1 fix: never a mod-tree read; render live instead) reproducible
GENERATIVELY, purely from the CURRENT repo engine plus its committed tint/recipe tables?

Chain per file:
  fresh = tools/recolor_icons.py's CURRENT route(vanilla_cache dds) pixels
  cachepng = working/icons/<stem>.png (the production bake's own cached PNG intermediate)
  fresh == cachepng  ->  the current engine reproduces what the last bake produced
  img-conv(cachepng) bytes == mod tree .tex bytes  ->  and that PNG really is the shipped tex

If both hold for all ten (id, surface) pairs, the ramp engine's route() branch for these five
ids can call helm_recolor / shield_two_tone directly (their existing HELM_OVERRIDES rows) and
never touch the mod tree as an input, which is the B1 fix LW-247 requires.

Usage: python tools/probes/lw247_keepship_check.py  (prints ten True/True lines; result saved
by the caller alongside this script as lw247_keepship_result.txt)
"""
import json
import os
import shutil
import subprocess
import sys
import tempfile

from PIL import Image

REPO = r"C:\Users\ptyRa\Dev\FFTLivingWeapons"
sys.path.insert(0, os.path.join(REPO, "tools"))
import recolor_icons as ri

WORKICONS = os.path.join(REPO, "working", "icons")
MOD = os.path.join(REPO, "mod", "FFTIVC", "data", "enhanced", "ui", "ffto", "icon")
CACHE = os.path.join(os.environ["TEMP"], "vanilla_cache")
SCR = os.path.join(tempfile.gettempdir(), "lw247_keepship")
os.makedirs(SCR, exist_ok=True)

items = json.load(open(os.path.join(REPO, "data", "items.json")))["items"]
tints = {it["id"]: tuple(it["iconTint"]) for it in items if it.get("iconTint")}
merged = dict(ri.ICON_TINTS)
merged.update(tints)

KEEPSHIP_CHECK_IDS = (144, 147, 148, 155, 156)

if __name__ == "__main__":
    all_ok = True
    for i in KEEPSHIP_CHECK_IDS:
        for sub, pfx, surface in [("equip_item", "ei", "card"),
                                  ("equip_item_s", "ei_s", "small")]:
            stem = f"{pfx}_{i:03d}_uitx"
            van = Image.open(os.path.join(CACHE, stem + ".dds")).convert("RGBA")
            fresh = ri.route(van, i, merged[i], surface)
            cachepng = Image.open(os.path.join(WORKICONS, stem + ".png")).convert("RGBA")
            px_ok = list(fresh.convert("RGBA").getdata()) == list(cachepng.getdata())
            p = os.path.join(SCR, stem + ".png")
            shutil.copy(os.path.join(WORKICONS, stem + ".png"), p)
            subprocess.run([str(ri.FF16), "img-conv", "-i", p, "--no-chunk-compression"],
                           capture_output=True)
            tex_new = open(os.path.join(SCR, stem + ".tex"), "rb").read()
            tex_mod = open(os.path.join(MOD, sub, "texture", stem + ".tex"), "rb").read()
            tex_ok = tex_new == tex_mod
            all_ok = all_ok and px_ok and tex_ok
            print(f"{stem}: fresh==cachePNG {px_ok}  imgconv(cachePNG)==modtex {tex_ok}",
                  flush=True)
    print(f"\n{'ALL PASSED' if all_ok else 'SOME FAILED'}")
    raise SystemExit(0 if all_ok else 1)
