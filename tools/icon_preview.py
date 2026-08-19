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
  python tools/icon_preview.py anchors [--out D]            # GATE: every item that kept its
                                                            # vanilla name still renders like its
                                                            # own art (or carries a ruling)
  python tools/icon_preview.py silhouettes [--out D]        # GATE: items the artist drew with
                                                            # one picture are far enough apart in
                                                            # colour, derived from the art
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
import colorsys
import copy
import hashlib
import importlib.util
import io
import json
import math
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
from lib.items import load_items
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
    back, so a tint newly added to items.json for one of the 45 approved items whose colour lives
    in the ICON_TINTS table sat on BOTH sides of the comparison. A second audit demonstrated it
    on sword id 25, where compare reported a serene 0 MOVED for a 96% repaint. (45, not the 29
    first written: 55 ids are table-only, 29 of them under the zone engine and the other 16 the
    whole shield family, which is just as approved and just as reviewed.)
    So the loader itself is swapped for the duration of the import instead. `from lib.items
    import load_items` binds the attribute at import time, which is why patching the package
    before exec_module reaches the copy; and doing it here rather than after the fact restores
    everything else items.json feeds the engine (iconSource, category, name) to the same
    revision, not just the tints.

    LW-247 S9: the same class of bug exists for data/icon_ramp/*.json -- the ramp engine's
    treatments.json/rims.json are also read from DISK at import time, so an "old" engine
    revision comes up reading TODAY'S ramp tables unless those are re-anchored too. Fixed for
    the two JSON tables (cheap, re-fetched via `git show` and the derived config sets rebuilt
    from them); the DOCUMENTED LIMITATION is data/icon_ramp/bodies/ (16 vendored PNGs): a full
    binary-tree checkout per revision was judged invasive for a diagnostic verb, so the bodies
    stay at their CURRENT committed content and a loud warning prints when they might matter
    (comparing against a revision before LW-247, or after a body PNG itself changed). A compare
    across the arc's own commits (2 vs 3, or 3 vs a later re-pass) is unaffected: the bodies
    are the same bytes on both sides of any revision pair that has them at all."""
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
    _reanchor_ramp_tables(mod, rev)
    return mod


def _reanchor_ramp_tables(mod, rev):
    """S9: re-fetch data/icon_ramp/{treatments,rims}.json from `rev` and rebuild the module's
    derived config sets from them, so an "old" engine's ramp branch judges Mode-B/reserved/
    punch membership the way IT shipped, not the way today's tables say. Absent at `rev`
    (anything before LW-247) -> loud warning, module keeps reading today's tables."""
    if not hasattr(mod, "RAMP_TREATMENTS"):
        return   # a pre-LW-247 revision has no ramp engine at all; nothing to re-anchor
    for name, attr in (("treatments.json", "RAMP_TREATMENTS"), ("rims.json", "RAMP_RIMS")):
        r = subprocess.run(["git", "show", f"{rev}:data/icon_ramp/{name}"],
                           cwd=str(ROOT), capture_output=True)
        if r.returncode != 0:
            print(f"WARNING: data/icon_ramp/{name} does not exist at {rev}; load_engine keeps "
                 f"reading TODAY'S copy for this revision, which may misrepresent it "
                 f"(tools/icon_preview.py load_engine, LW-247 S9 documented limitation).")
            continue
        setattr(mod, attr, json.loads(r.stdout.decode("utf-8")))
    print(f"NOTE: data/icon_ramp/bodies/ (vendored body PNGs) is NOT re-anchored per revision "
         f"(LW-247 S9 documented limitation) -- {rev}'s ramp render may differ from what it "
         f"actually shipped if a vendored body changed since.")
    (mod.RAMP_PUNCH, mod.RAMP_ROTATE_ALL, mod.RAMP_FORCE_MODE_B, mod.RAMP_MUTED,
     mod.RAMP_WHITE_SPEC, mod.RAMP_SOFT_SPEC, mod.RAMP_OUTLINE_BLACK, mod.RAMP_DEEP_DAMP,
     mod.RAMP_RESERVED_POP, mod.RAMP_VSCALE_OVERRIDE) = mod._ramp_weapon_config()
    mod.RAMP_IDS = frozenset(int(k) for k in mod.RAMP_TREATMENTS)


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
    """Render every tinted item through the committed engine and the working-tree engine.

    THE SWEEP POPULATION IS THE UNION of the two revisions' tint tables, and each side decodes
    the sprite ITS OWN revision names. Both were audit findings on 2026-08-14 and both let an
    approved item change with the gate printing OK:

      - iterating only the working tree's ICON_TINTS meant DELETING an items.json tint dropped
        that id out of the sweep entirely. It was never rendered, never diffed, and the closing
        sentence was identical to a full pass (demonstrated on an approved helmet). The same
        deletion also stops the item being re-baked forever, so it is a movement, not an absence.
      - decoding one sprite from the working tree's SRC and feeding it to BOTH engines meant a
        changed iconSource repainted an item with a completely different picture on both sides
        and the diff came out exactly zero (demonstrated by turning an approved rod into a harp).
    """
    out.mkdir(parents=True, exist_ok=True)
    old_ri = load_engine(rev, out)
    rows = []
    ids = sorted(set(ri.ICON_TINTS) | set(old_ri.ICON_TINTS))
    for iid in ids:
        if only and iid not in only:
            continue
        tint = ri.ICON_TINTS.get(iid)
        old_tint = old_ri.ICON_TINTS.get(iid, tint)
        new_src = ri.SRC.get(iid, iid)
        old_src = old_ri.SRC.get(iid, iid)
        for sub, pfx, surface in SURFACES:
            van_new = decode_vanilla(sub, pfx, new_src, out) if tint is not None else None
            # `van_new is not None` is load-bearing: a DROPPED id has no new render to reuse, so
            # without it the old sprite is never decoded, base comes back None and the row this
            # branch exists to report is skipped. It was, for one round.
            van_old = (van_new if (van_new is not None and old_src == new_src)
                       else decode_vanilla(sub, pfx, old_src, out))
            base = van_new if van_new is not None else van_old
            if base is None:
                continue
            row = {"id": iid, "surface": surface,
                   "engine": ri.engine_for(iid) if tint is not None else "DROPPED",
                   "was": old_ri.engine_for(iid), "tint_moved": old_tint != tint,
                   "src_moved": old_src != new_src}
            if tint is None:
                # The id lost its tint: it leaves the bake, so every solid pixel of the art it
                # used to paint is a movement, and the census cannot be run against a render
                # that no longer exists.
                px = base.load()
                solid = sum(1 for y in range(base.height) for x in range(base.width)
                            if px[x, y][3] >= ri.HALO_HI)
                row.update(solid=solid, solid_moved=solid, band=0, band_moved=0, kept=0.0)
                rows.append(row)
                continue
            old = old_ri.route(van_old.copy(), iid, old_tint, surface)
            new = ri.route(van_new.copy(), iid, tint, surface)
            row.update(band_census(van_new, old, new))
            if old_src != new_src:
                row["solid_moved"] = row["solid"]     # a different picture is a total repaint
            rows.append(row)
            for tag, im in (("0van", van_new), ("1old", old), ("2new", new)):
                im.save(out / f"c{iid:03d}_{surface}_{tag}.png")
        print(f"id{iid:>3} {ri.engine_for(iid) if tint is not None else 'DROPPED'}", end="\r")
    (out / "compare.json").write_text(json.dumps(rows, indent=1), encoding="utf-8")
    engines = sorted({r["engine"] for r in rows})
    swept = len({r["id"] for r in rows})
    print(f"\n{len(rows)} surfaces over {swept} items compared against {rev}\n")
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
                         if r["id"] not in expect
                         and (r["solid_moved"] or r["band_moved"] or r["src_moved"])})
        if strays:
            print(f"\nFAIL: {len(strays)} item(s) moved that --expect did not allow: {strays}")
            for iid in strays[:12]:
                for r in (x for x in rows if x["id"] == iid):
                    print(f"  id{iid:>3} {r['surface']:<6} solid_moved={r['solid_moved']:>5}"
                          f" band_moved={r['band_moved']:>5} tint_moved={r['tint_moved']}"
                          f" src_moved={r['src_moved']} engine={r['engine']}")
            return 1
        # The count is stated because it is the evidence: a commit that cites this gate cites the
        # sweep size with it, and a run that examined fewer items than it should have is then
        # visible in the sentence rather than only in the header two lines up.
        print(f"\nOK: nothing moved outside the {len(expect)} item(s) named by --expect, "
              f"across {swept} items and {len(rows)} surfaces.")
    return 0


ANCHOR_CHROMA = 0.120      # below this an item's own art has no colour to be anchored to
ANCHOR_TOLERANCE = 40      # degrees the RENDERED item may sit from its own art


def art_reading(im):
    """Chroma-weighted circular mean hue (degrees) and mean chroma over the solid art.

    Calibrated, not invented: this is the one metric that reproduces the figures already
    published in recolor_icons.py for the Perseus Bow (icon 229.0 deg at chroma 0.120, card
    0.031). Chroma is (max - min) / 255, the same quantity as saturation x value."""
    px = im.load()
    sx = sy = c = 0.0
    n = 0
    for y in range(im.height):
        for x in range(im.width):
            r, g, b, a = px[x, y]
            if a < ri.HALO_HI:
                continue
            ch = (max(r, g, b) - min(r, g, b)) / 255
            h = colorsys.rgb_to_hsv(r / 255, g / 255, b / 255)[0]
            sx += ch * math.cos(2 * math.pi * h)
            sy += ch * math.sin(2 * math.pi * h)
            c += ch
            n += 1
    if not n:
        return 0.0, 0.0
    return ((math.atan2(sy, sx) / (2 * math.pi)) % 1.0) * 360, c / n


def _hue_gap(a, b):
    d = abs(a - b) % 360
    return min(d, 360 - d)


def cmd_anchors(out):
    """THE RESERVED-NAME GATE: an item that kept its vanilla name must still look like itself.

    The rule is the owner's and it has been cited in six passes; until now nothing checked it,
    and a verify round showed the exact wrong colour one pass had just corrected would sail
    through every gate. It cannot live in the recolor selftest, which runs in CI on a machine
    with neither the game files nor the texture tool, so it lives here beside the other art
    gates.

    What it compares is the RENDERED icon against the vanilla icon, not the tint against the
    vanilla, because a recipe can keep an item's colour by moving it into a zone: the Staff of
    the Magi's body tint is a near-black steel while its gold head is a zone, and the item still
    reads as the gold-headed staff its art is. Comparing tints would call that a violation.

    Scope: reviewed engines only. A family still on bright-v2 or legacy has not had its pass, so
    its colours are the ones being replaced, not defended. Chroma floor: art below
    ANCHOR_CHROMA has no colour to be anchored to (the near-neutral case the Ivory Pole
    established). Rulings live in recolor_icons.ANCHOR_RULINGS with a reason each."""
    out.mkdir(parents=True, exist_ok=True)
    items = {it["id"]: it for it in load_items()["items"]}
    # LW-247 (2026-08-18): shield-bright and helm-two-tone are DORMANT in the router now (every
    # shield/helm id is a ramp id, so RAMP_IDS wins before either category rule is reached);
    # "ramp" replaces them here so reserved-name shields, helms and every weapon family the ramp
    # arc covers stay under this gate instead of silently dropping out of it.
    reviewed = {"three-zone", "ramp"}
    rows, bad = [], []
    for iid in sorted(ri.ICON_TINTS):
        it = items.get(iid)
        if not it or not it.get("name"):
            continue
        if it["name"].lower() != it["vanillaName"].lower():
            continue
        if ri.engine_for(iid) not in reviewed:
            continue
        van = decode_vanilla("equip_item_s", "ei_s", ri.SRC.get(iid, iid), out)
        if van is None:
            continue
        art_hue, art_chroma = art_reading(van)
        made_hue, _ = art_reading(ri.route(van.copy(), iid, ri.ICON_TINTS[iid], "small"))
        gap = _hue_gap(art_hue, made_hue)
        ruling = ri.ANCHOR_RULINGS.get(iid)
        if art_chroma < ANCHOR_CHROMA:
            state = "free"
        elif gap <= ANCHOR_TOLERANCE:
            state = "anchored"
        elif ruling:
            state = "RULED"
        else:
            state = "OFF ITS ART"
            bad.append((iid, it["name"], gap))
        rows.append((iid, it["name"], ri.engine_for(iid), art_hue, art_chroma, made_hue, gap,
                     state))
    print(f"{len(rows)} reserved-name items under a reviewed engine\n")
    print(f"{'id':>4} {'name':<20} {'engine':<14} {'art hue':>8} {'chroma':>7} {'made':>7}"
          f" {'gap':>5}  state")
    for r in rows:
        print(f"{r[0]:>4} {r[1][:20]:<20} {r[2]:<14} {r[3]:>7.1f} {r[4]:>7.3f} {r[5]:>7.1f}"
              f" {r[6]:>5.0f}  {r[7]}")
    for iid, why in sorted(ri.ANCHOR_RULINGS.items()):
        print(f"\nruling id {iid}: {why}")
    if bad:
        print(f"\nFAIL: {len(bad)} reserved-name item(s) render more than {ANCHOR_TOLERANCE} "
              f"degrees from their own art with no ruling: "
              f"{[(i, n, round(g)) for i, n, g in bad]}")
        return 1
    print(f"\nOK: every reserved name is within {ANCHOR_TOLERANCE} degrees of its own art, "
          f"near-neutral, or ruled.")
    return 0


def cmd_silhouettes(out):
    """THE SHARED-PICTURE GATE, derived from the ART rather than from the iconSource field.

    recolor_icons' own twins pin reads SRC, which knows only about items built on another item's
    sprite: 7 pairs. The artist also drew plain glyphs that repeat, and at 48px those are just as
    indistinguishable, which an audit measured at 15 of 16 byte-identical outlines unguarded.
    This groups every tinted item by category and by the exact solid-alpha mask of its list icon,
    then holds each group to the same floor recolor_icons uses, imported rather than re-declared.

    LW-287 (2026-08-19) rewrote what "judged" means here, because the previous rule graded a layer
    that is no longer painted. It said a ramp id's signal was "its body tint escaped by a distinct
    rim, or its rim alone if reserved"; the shipped bake now paints no rim at all
    (recolor_icons.SHIP_GLOW_RIM), so both halves of that sentence were dead. The rule now:

      * both ids JUDGED (neither exempt)  -> compare body tints, recolor_icons' own signal.
      * either id EXEMPT                  -> the tint says nothing about an exempt id, since it
        ships as the artist's own art popped. These pairs are the ones where colour is the ONLY
        signal, so they are judged on the RENDERED PIXELS instead: the chroma-weighted hue of each
        icon as actually drawn, held to ART_HUE_FLOOR. This is the honest completion of the
        exemption rather than a hole left behind it.
      * either side too near neutral      -> named and skipped, on the same convention cmd_anchors
        already uses (near-neutral art has no colour to compare).

    Every pair lands in exactly one of those three buckets and the three are asserted to sum to
    the total considered, which is what stops this gate quietly judging fewer things over time."""
    out.mkdir(parents=True, exist_ok=True)
    groups = {}
    rendered = {}
    for iid in sorted(ri.ICON_TINTS):
        van = decode_vanilla("equip_item_s", "ei_s", ri.SRC.get(iid, iid), out)
        if van is None:
            continue
        px = van.load()
        mask = bytes(1 if px[x, y][3] >= ri.HALO_HI else 0
                     for y in range(van.height) for x in range(van.width))
        groups.setdefault((ri._CATEGORY.get(iid), hashlib.md5(mask).hexdigest()), []).append(iid)
        rendered[iid] = ri.route(van.copy(), iid, ri.ICON_TINTS[iid], "small")
    shared = sorted((c, v) for (c, h), v in groups.items() if len(v) > 1)

    ART_HUE_FLOOR = 15.0   # degrees between two rendered icons; measured tightest real pair is
                           # (34, 50) Save the Queen / Sunderer at 18.3 deg on 2026-08-19
    on_tint = on_art = near_neutral = considered = bad = 0
    print(f"{len(shared)} groups of items share one 48px silhouette\n")
    for cat, ids in shared:
        keep = [i for i in ids if ri.body_is_whole_signal(i) or i in ri.RAMP_IDS]
        note = "" if len(keep) == len(ids) else f"  (judging {keep} of {ids})"
        print(f"  {str(cat):<12} {ids}{note}")
        hg, sg = ri.ramp_rack_floors("shield" if cat == "Shield" else cat)
        for n, a in enumerate(keep):
            for b in keep[n + 1:]:
                considered += 1
                a_ramp, b_ramp = a in ri.RAMP_IDS, b in ri.RAMP_IDS
                exempt = (a_ramp and ri.ramp_separation_exempt(a)) or \
                         (b_ramp and ri.ramp_separation_exempt(b))
                if a_ramp and b_ramp and exempt:
                    ha, ca = art_reading(rendered[a])
                    hb, cb = art_reading(rendered[b])
                    if ca < ANCHOR_CHROMA or cb < ANCHOR_CHROMA:
                        near_neutral += 1
                        print(f"      skip {a} vs {b}: near-neutral art "
                              f"(chroma {ca:.3f} / {cb:.3f}), no colour to compare")
                        continue
                    on_art += 1
                    gap = abs(ri.arc(ha / 360.0, hb / 360.0)) * 360.0
                    if gap < ART_HUE_FLOOR:
                        bad += 1
                        print(f"      FAIL {a} vs {b}: rendered art only {gap:.1f} deg apart "
                              f"(floor {ART_HUE_FLOOR:.0f}); one of them is exempt from the tint "
                              f"rule, so this is the only thing holding them apart")
                    else:
                        print(f"      ok   {a} vs {b}: rendered art {gap:.1f} deg apart "
                              f"(exempt pair, judged on pixels)")
                    continue
                on_tint += 1
                if a_ramp and b_ramp:
                    sig_a, sig_b = ri.ramp_separation_signal(a), ri.ramp_separation_signal(b)
                    collide = bool(sig_a and sig_b
                                   and ri.ramp_separation_collides(sig_a, sig_b, hg, sg))
                    reason = "body tints are both inside the floor"
                else:
                    dh = abs(ri.arc(ri.ICON_TINTS[a][0], ri.ICON_TINTS[b][0]))
                    ds = abs(ri.ICON_TINTS[a][1] - ri.ICON_TINTS[b][1])
                    collide = dh < hg and ds < sg
                    reason = f"hue {dh:.3f} and saturation {ds:.3f} are both inside the floor"
                if collide:
                    bad += 1
                    print(f"      FAIL {a} vs {b}: {reason}")

    # THE ACCOUNTING, which is this gate's anti-vacuity device. Every pair considered must land
    # in exactly one bucket, and the buckets are printed. The predecessor counted `judged += 1`
    # BEFORE deciding anything, so it reported pairs CONSIDERED as pairs JUDGED and its floor of
    # 40 could be satisfied by pairs nothing actually looked at.
    print(f"\n{considered} pair(s) considered: {on_tint} judged on body tint, {on_art} judged "
          f"on rendered pixels (exempt pairs), {near_neutral} skipped as near-neutral")
    if on_tint + on_art + near_neutral != considered:
        print(f"\nFAIL: the buckets do not sum to the pairs considered "
              f"({on_tint} + {on_art} + {near_neutral} != {considered}); a pair fell through "
              f"this gate without being judged or explicitly excused.")
        return 1
    if on_tint + on_art < 40:
        print(f"\nFAIL: only {on_tint + on_art} pair(s) were actually JUDGED (floor 40); the "
              f"judged set shrank, which is exactly what this gate exists to catch.")
        return 1
    if bad:
        print(f"\nFAIL: {bad} pair(s) drawn with one picture are too close in colour.")
        return 1
    print(f"OK: {on_tint + on_art} judged pair(s) sharing a picture are far enough apart.")
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
    elif verb == "anchors":
        return cmd_anchors(out)
    elif verb == "silhouettes":
        return cmd_silhouettes(out)
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
