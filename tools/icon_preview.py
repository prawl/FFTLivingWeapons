#!/usr/bin/env python
"""
Icon-recolor PREVIEW pipeline (LW-189 process, promoted from the session scratchpad so the
next equipment pass reuses it instead of rebuilding it).

The recolor ENGINE lives in tools/recolor_icons.py and is imported here, never copied: what
the preview gallery shows is BY CONSTRUCTION what the production bake will ship. The LW-189
weapon pass proved that identity 242/242 pixel-exact; this import structure makes the proof
structural for every future pass.

The process this serves (full recipe: docs/DEV_TEST_RECIPES.md, "Icon recolor process"):
  preview -> contact sheets -> visual QA sweep -> owner gallery -> owner flags -> per-item
  overrides in recolor_icons.py -> re-preview -> owner sign-off -> production bake
  (recolor_icons.py) -> verify (pixel identity, this file) -> commit -> deploy.

Verbs (all outputs go under --out, default %TEMP%\\icon_preview; never a repo dir):
  python tools/icon_preview.py preview [ids...] [--out D]   # decode vanilla + recolor, PNGs only
  python tools/icon_preview.py sheets [--out D]             # contact sheets for the QA sweep
  python tools/icon_preview.py gallery [--out D]            # the owner's before/after HTML
  python tools/icon_preview.py verify [--out D]             # preview PNGs vs the production bake's
                                                            # working/icons PNGs, pixel identity
preview with no ids does every tinted item, each routed through recolor_icons.route() -- the
SAME per-category split production uses (LW-189 bright-v2 for weapons, LW-190 two-tone for
shields, legacy whole-tint for the rest), so the split can never drift from production.
flags.json in the out dir ({id: note}) annotates gallery rows amber.
"""
import base64
import io
import json
import os
import pathlib
import shutil
import subprocess
import sys

from PIL import Image, ImageChops, ImageDraw

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
from lib.paths import ROOT, FF16
import recolor_icons as ri

DEFAULT_OUT = pathlib.Path(os.environ.get("TEMP", ".")) / "icon_preview"
PROD_WORK = ROOT / "working" / "icons"
SURFACES = [("equip_item", "ei", "card"), ("equip_item_s", "ei_s", "small")]


def out_dir(argv):
    if "--out" in argv:
        return pathlib.Path(argv[argv.index("--out") + 1])
    return DEFAULT_OUT


def decode_vanilla(sub, pfx, icon_id, out):
    src = ri.VANILLA / sub / "texture" / f"{pfx}_{icon_id:03d}_uitx.tex"
    if not src.exists():
        return None
    work = out / f"_{pfx}_{icon_id:03d}.tex"
    shutil.copy(src, work)
    subprocess.run([str(FF16), "tex-conv", "-i", str(work)], capture_output=True)
    dds = work.with_suffix(".dds")
    im = Image.open(dds).convert("RGBA")
    work.unlink(missing_ok=True)
    dds.unlink(missing_ok=True)
    return im


def cmd_preview(only, out):
    out.mkdir(parents=True, exist_ok=True)
    manifest = []
    for iid, tint in sorted(ri.ICON_TINTS.items()):
        if only and iid not in only:
            continue
        cat = ri._CATEGORY.get(iid)
        src_id = ri.SRC.get(iid, iid)
        row = {"id": iid, "category": cat, "src": src_id, "tint": list(tint),
               "engine": ri.engine_for(iid)}
        ok = True
        for sub, pfx, surface in SURFACES:
            vanilla = decode_vanilla(sub, pfx, src_id, out)
            if vanilla is None:
                print(f"id{iid}: MISSING vanilla {pfx} (src {src_id})")
                ok = False
                continue
            vanilla.save(out / f"i{iid:03d}_{surface}_0v.png")
            ri.route(vanilla, iid, tint, surface).save(out / f"i{iid:03d}_{surface}_1new.png")
        if ok:
            manifest.append(row)
            print(f"id{iid:>3} {cat:<12} [{row['engine']}]")
    (out / "manifest.json").write_text(json.dumps(manifest, indent=1), encoding="utf-8")
    print(f"{len(manifest)} items -> {out}")


def load_manifest(out):
    return json.loads((out / "manifest.json").read_text(encoding="utf-8"))


def cmd_sheets(out, per=8, cell=104, pad=6, label_h=14):
    manifest = load_manifest(out)
    for s in range(0, len(manifest), per):
        chunk = manifest[s:s + per]
        sheet = Image.new("RGBA", (pad + 4 * (cell + pad),
                                   pad + len(chunk) * (cell + label_h + pad)), (24, 28, 27, 255))
        drw = ImageDraw.Draw(sheet)
        for row, m in enumerate(chunk):
            y0 = pad + row * (cell + label_h + pad)
            imgs = [Image.open(out / f"i{m['id']:03d}_card_0v.png"),
                    Image.open(out / f"i{m['id']:03d}_card_1new.png"),
                    Image.open(out / f"i{m['id']:03d}_small_0v.png").resize((96, 96), Image.NEAREST),
                    Image.open(out / f"i{m['id']:03d}_small_1new.png").resize((96, 96), Image.NEAREST)]
            for col, img in enumerate(imgs):
                x0 = pad + col * (cell + pad)
                sheet.paste(img, (x0 + (cell - img.width) // 2, y0 + (cell - img.height) // 2), img)
            drw.text((pad, y0 + cell + 1),
                     f"id{m['id']} ({m['category']}, {m['engine']})  cols: cardV cardNEW smallV smallNEW",
                     fill=(220, 220, 210, 255))
        sheet.save(out / f"sheet_{s // per:02d}.png")
    print(f"{(len(manifest) + per - 1) // per} sheets -> {out}")


def _uri(path):
    im = Image.open(path)
    buf = io.BytesIO()
    im.save(buf, "PNG", optimize=True)
    return "data:image/png;base64," + base64.b64encode(buf.getvalue()).decode()


def cmd_gallery(out):
    manifest = load_manifest(out)
    flags = {}
    fp = out / "flags.json"
    if fp.exists():
        flags = {int(k): v for k, v in json.loads(fp.read_text(encoding="utf-8")).items()}
    rows = ""
    for m in manifest:
        iid = m["id"]
        cells = "".join(
            f'<figure><img src="{_uri(out / f"i{iid:03d}_{sf}_{v}.png")}" loading="lazy">'
            f"<figcaption>{cap}</figcaption></figure>"
            for sf, v, cap in [("card", "0v", "Card vanilla"), ("card", "1new", "Card new"),
                               ("small", "0v", "Icon vanilla"), ("small", "1new", "Icon new")])
        note = f'<p class="flag">Review note: {flags[iid]}</p>' if iid in flags else ""
        rows += (f'<section class="row{" is-flagged" if iid in flags else ""}" id="i{iid}">'
                 f'<header><h2>id {iid}</h2><span class="kind">{m["category"]} · {m["engine"]}</span>'
                 f'</header><div class="strip">{cells}</div>{note}</section>')
    html = ("<title>Icon Recolor Preview</title><style>"
            ":root{--g:#16201f;--p:#1d2a29;--i:#e8e4d8;--m:#9aa8a2;--l:#2c3b39;--w:#d9a441}"
            "@media (prefers-color-scheme: light){:root{--g:#f2f0e8;--p:#fff;--i:#25302e;--m:#68756f;--l:#dcd8ca;--w:#a3690a}}"
            ":root[data-theme='light']{--g:#f2f0e8;--p:#fff;--i:#25302e;--m:#68756f;--l:#dcd8ca;--w:#a3690a}"
            ":root[data-theme='dark']{--g:#16201f;--p:#1d2a29;--i:#e8e4d8;--m:#9aa8a2;--l:#2c3b39;--w:#d9a441}"
            "body{background:var(--g);color:var(--i);font:16px/1.5 Georgia,serif;margin:0;padding:2rem 1rem}"
            "main{max-width:760px;margin:0 auto}"
            ".row{background:var(--p);border:1px solid var(--l);border-radius:6px;padding:1rem 1.2rem;margin-bottom:1rem}"
            ".row.is-flagged{border-color:var(--w)}"
            ".row header{display:flex;gap:.7rem;align-items:baseline;margin-bottom:.7rem}"
            "h2{font-size:1rem;margin:0}.kind{font:12px ui-monospace,monospace;color:var(--m)}"
            ".strip{display:flex;gap:1rem;flex-wrap:wrap}figure{margin:0;display:flex;flex-direction:column;align-items:center;gap:.4rem}"
            "figure img{width:120px;height:120px;object-fit:contain;image-rendering:pixelated;"
            "border:1px solid var(--l);border-radius:4px;background:repeating-conic-gradient(rgba(128,128,128,.12) 0% 25%,transparent 0% 50%) 0 0/16px 16px}"
            "figcaption{font:11px ui-monospace,monospace;color:var(--m)}"
            ".flag{color:var(--w);margin:.7rem 0 0;font-size:.92rem}</style>"
            f"<main><h1>Icon recolor preview</h1><p>{len(manifest)} items, {len(flags)} flagged.</p>{rows}</main>")
    (out / "gallery.html").write_text(html, encoding="utf-8")
    print(f"gallery.html ({len(manifest)} items, {len(flags)} flags) -> {out}")


def cmd_verify(out):
    """Preview PNGs vs the production bake's intermediates: the LW-189 identity gate."""
    manifest = load_manifest(out)
    bad, missing = [], []
    for m in manifest:
        iid = m["id"]
        for sub, pfx, surface in SURFACES:
            prod = PROD_WORK / f"{pfx}_{iid:03d}_uitx.png"
            ref = out / f"i{iid:03d}_{surface}_1new.png"
            if not prod.exists():
                missing.append(prod.name)
                continue
            a = Image.open(prod).convert("RGBA")
            b = Image.open(ref).convert("RGBA")
            # ri.images_equal, NOT ImageChops.difference(...).getbbox(): Pillow 10 made
            # getbbox() alpha-only by default, so the old idiom passed two images that
            # differed only in colour (LW-227; regression pinned in the recolor selftest).
            if not ri.images_equal(a, b):
                bad.append(f"{prod.name} vs {ref.name}")
    print(f"{len(manifest) * 2} comparisons: {len(bad)} mismatched, {len(missing)} missing")
    for x in bad + missing:
        print(" ", x)
    return 1 if (bad or missing) else 0


def main(argv):
    if not argv:
        print(__doc__)
        return 2
    verb, rest = argv[0], argv[1:]
    out = out_dir(rest)
    if verb == "preview":
        only = {int(a) for a in rest if a.isdigit()}
        cmd_preview(only, out)
    elif verb == "sheets":
        cmd_sheets(out)
    elif verb == "gallery":
        cmd_gallery(out)
    elif verb == "verify":
        return cmd_verify(out)
    else:
        print(__doc__)
        return 2
    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))
