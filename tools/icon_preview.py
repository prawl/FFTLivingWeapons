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
  python tools/icon_preview.py compare [ids...] [--rev R]   # working-tree engine vs the one
                                                            # committed at R (default HEAD): what
                                                            # an engine change did to approved art
  python tools/icon_preview.py compare --expect ids... [--rev R]
                                                            # the same sweep as a GATE: name the
                                                            # ids this pass may move and anything
                                                            # else moving exits nonzero. Takes no
                                                            # positional ids: a gate that judged
                                                            # only what you scoped it to is not a
                                                            # gate
preview with no ids does every tinted item, each routed through recolor_icons.route() -- the
SAME per-category split production uses (LW-189 bright-v2 for weapons, LW-190 two-tone for
shields, legacy whole-tint for the rest), so the split can never drift from production.
flags.json in the out dir ({id: note}) annotates gallery rows amber.
"""
import base64
import copy
import importlib.util
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
# Imported as a MODULE, not `from lib.items import load_items`: load_engine below swaps the
# package's own attribute so a second copy of the engine reads the item data of ITS revision,
# and a from-import here would hold a reference the swap cannot reach.
import lib.items as lib_items
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


def load_engine(rev, out):
    """Import a SECOND copy of the recolor engine, as of a git revision, alongside the live one.

    An engine change silently invalidates the identity of every family already approved under
    the old one (docs/DEV_TEST_RECIPES.md says so in as many words), and until this verb existed
    the only way to see what a change did to approved art was to eyeball a gallery. Loading the
    committed engine as its own module makes that a measurement instead: same vanilla input,
    two engines, one diff.

    The copy lands in the out dir and NOT beside the live module: same-directory would put two
    files named recolor_icons.py on one path and the second import would win. tools/ stays on
    sys.path so the copy's own `from lib...` imports resolve to the same shared package.

    THE ITEM DATA HAS TO COME FROM THE SAME REVISION AS THE ENGINE, and getting that wrong is
    what made this verb lie twice. The module merges data/items.json at IMPORT time and reads it
    from DISK, so left alone the "old" engine comes up wearing TODAY'S colours. The first fix
    (2026-08-14) wrote the committed tints back afterwards, which repaired an EDITED row and
    stayed blind to an ADDED one: an id whose committed row carries no iconTint was never written
    back, so a tint newly added to items.json for one of the 29 approved items whose colour lives
    in the ICON_TINTS table sat on BOTH sides of the comparison. A second audit demonstrated it
    on sword id 25, where compare reported a serene 0 MOVED for a 96% repaint.
    So the loader itself is swapped for the duration of the import instead. `from lib.items
    import load_items` binds the attribute at import time, which is why patching the package
    before exec_module reaches the copy; and doing it here rather than after the fact restores
    everything else items.json feeds the engine (iconSource, category, name) to the same
    revision, not just the tints."""
    src = out / f"_engine_{rev.replace('/', '_')}.py"
    src.write_bytes(subprocess.run(["git", "show", f"{rev}:tools/recolor_icons.py"],
                                   cwd=str(ROOT), capture_output=True, check=True).stdout)
    old_items = json.loads(subprocess.run(["git", "show", f"{rev}:data/items.json"],
                                          cwd=str(ROOT), capture_output=True,
                                          check=True).stdout.decode("utf-8"))
    spec = importlib.util.spec_from_file_location(f"recolor_icons_{abs(hash(rev)):x}", src)
    mod = importlib.util.module_from_spec(spec)
    live_loader = lib_items.load_items
    lib_items.load_items = lambda *a, **k: copy.deepcopy(old_items)
    try:
        spec.loader.exec_module(mod)
    finally:
        lib_items.load_items = live_loader
    return mod


def band_census(van, old, new):
    """Per-alpha-band diff between two renders of the same sprite, plus how much of the identity
    colour survives. Solid means alpha >= ri.HALO_HI: the band the halo ramp leaves at FULL
    tint, so a nonzero count there is an engine change reaching art the owner already signed
    off, not a halo fix."""
    vp, op, np_ = van.load(), old.load(), new.load()
    solid = solid_moved = band = band_moved = 0
    kept_old = kept_new = 0.0
    for y in range(van.height):
        for x in range(van.width):
            r, g, b, a = vp[x, y]
            if a < 8:
                continue
            same = op[x, y] == np_[x, y]
            if a >= ri.HALO_HI:
                solid += 1
                solid_moved += not same
            else:
                band += 1
                band_moved += not same
            # alpha-weighted distance from the artist's own colour: what the tint is "worth"
            kept_old += a * sum(abs(c - v) for c, v in zip(op[x, y][:3], (r, g, b)))
            kept_new += a * sum(abs(c - v) for c, v in zip(np_[x, y][:3], (r, g, b)))
    return {"solid": solid, "solid_moved": solid_moved, "band": band, "band_moved": band_moved,
            "kept": (kept_new / kept_old) if kept_old else 1.0}


def cmd_compare(rev, only, out, expect=None):
    """Render every tinted item through the committed engine and the working-tree engine."""
    out.mkdir(parents=True, exist_ok=True)
    old_ri = load_engine(rev, out)
    rows = []
    for iid, tint in sorted(ri.ICON_TINTS.items()):
        if only and iid not in only:
            continue
        src_id = ri.SRC.get(iid, iid)
        for sub, pfx, surface in SURFACES:
            van = decode_vanilla(sub, pfx, src_id, out)
            if van is None:
                continue
            old_tint = old_ri.ICON_TINTS.get(iid, tint)
            old = old_ri.route(van.copy(), iid, old_tint, surface)
            new = ri.route(van.copy(), iid, tint, surface)
            row = {"id": iid, "surface": surface, "engine": ri.engine_for(iid),
                   "was": old_ri.engine_for(iid), "tint_moved": old_tint != tint,
                   **band_census(van, old, new)}
            rows.append(row)
            for tag, im in (("0van", van), ("1old", old), ("2new", new)):
                im.save(out / f"c{iid:03d}_{surface}_{tag}.png")
        print(f"id{iid:>3} {ri.engine_for(iid)}", end="\r")
    (out / "compare.json").write_text(json.dumps(rows, indent=1), encoding="utf-8")
    engines = sorted({r["engine"] for r in rows})
    print(f"\n{len(rows)} surfaces compared against {rev}\n")
    print(f"{'engine':<15} {'items':>5} {'solid px':>9} {'MOVED':>7} {'band px':>8} {'moved':>7}"
          f" {'colour kept':>12}")
    for eng in engines:
        g = [r for r in rows if r["engine"] == eng]
        keep = sorted(r["kept"] for r in g)
        print(f"{eng:<15} {len(g):>5} {sum(r['solid'] for r in g):>9}"
              f" {sum(r['solid_moved'] for r in g):>7} {sum(r['band'] for r in g):>8}"
              f" {sum(r['band_moved'] for r in g):>7}"
              f" {keep[len(keep) // 2] * 100:>10.0f}% ")
    thin = sorted((r for r in rows if r["kept"] < 0.60), key=lambda r: r["kept"])
    if thin:
        print(f"\n{len(thin)} surfaces keep under 60% of their identity colour:")
        for r in thin[:40]:
            print(f"  id{r['id']:>3} {r['surface']:<6} {r['engine']:<14} {r['kept'] * 100:>5.1f}%")
    # --expect turns this verb from a diagnostic into a GATE for the claim every icon-pass commit
    # makes: "no already-approved art moves". Name the ids the pass is allowed to move and any
    # movement outside them fails. Without it the verb stays a report, because during a pass the
    # family being worked moves on purpose and there is nothing to assert.
    if expect is not None:
        strays = sorted({r["id"] for r in rows
                         if r["id"] not in expect and (r["solid_moved"] or r["band_moved"])})
        if strays:
            print(f"\nFAIL: {len(strays)} item(s) moved that --expect did not allow: {strays}")
            for iid in strays[:12]:
                for r in (x for x in rows if x["id"] == iid):
                    print(f"  id{iid:>3} {r['surface']:<6} solid_moved={r['solid_moved']:>5}"
                          f" band_moved={r['band_moved']:>5} tint_moved={r['tint_moved']}")
            return 1
        print(f"\nOK: nothing moved outside the {len(expect)} item(s) named by --expect.")
    return 0


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
    elif verb == "compare":
        rev = rest[rest.index("--rev") + 1] if "--rev" in rest else "HEAD"
        expect = None
        if "--expect" in rest:
            i = rest.index("--expect")
            expect = {int(a) for a in rest[i + 1:] if a.isdigit()}
            rest = rest[:i]
        only = {int(a) for a in rest if a.isdigit()}
        # A GATE CANNOT BE SCOPED. Positional ids narrow which items get rendered, and --expect
        # judges only what was rendered, so the two together produce the most dangerous output
        # this file can print: `compare 51 --expect 51` examines one item of 234 and signs off in
        # the same words as a full sweep. Found by audit 2026-08-14 and demonstrated with an
        # engine edit that moved an approved sword while the scoped run stayed green. The
        # diagnostic verb keeps its scoping; the gate refuses it outright rather than widening
        # silently, so a scoped invocation cannot be mistaken for a pass.
        if expect is not None and only:
            print("compare --expect is a GATE and may not be scoped: it can only report on the "
                  f"items it rendered, and you named {sorted(only)}. Drop the positional ids to "
                  "sweep every tinted item, or drop --expect to use the diagnostic instead.")
            return 2
        return cmd_compare(rev, only, out, expect)
    else:
        print(__doc__)
        return 2
    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))
