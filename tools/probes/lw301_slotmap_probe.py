#!/usr/bin/env python
"""LW-301: identify WHICH sprite tile a weapon draws, by painting its 15 palette SLOTS apart.

WHY NOT A SILHOUETTE. Every attempt to name a weapon's sprite from its outline has failed here:
the sheet tiles are 10-25 px, the drawn sprite is rotated and scaled, and normalising the two to a
common size turns every tapered object into the same blob. Documented as a dead end twice.

THE IDEA. The sheet is 4 bits per pixel: each pixel stores a palette SLOT index 1..15. Flat-filling
a palette (what the rainbow probe does, to prove the mechanism) destroys that index and leaves only
a shape. Paint the fifteen slots FIFTEEN DIFFERENT COLOURS instead and the rendered weapon shows
its slot indices directly on screen. The multiset of slot indices is a fingerprint that survives
rotation, scale and partial occlusion, none of which an outline survives.

  paint  -> writes the same 15-colour ramp into every weapon palette (3..15), both banks
  id     -> reads a screenshot, recovers the slot histogram, ranks all 150 sheet tiles

The ramp is written to both 512-byte banks of the resident workspace at 0x140d35750
([resident-weapon-palette-buffer]). A battle LOAD refreshes that workspace from the loaded file
and reverts everything, so re-run `paint` after every load.

Slot 0 is transparent and bit 15 is a per-slot flag; both are preserved, or the sprite loses its
cutout and renders as a coloured rectangle.

USAGE:
  python lw301_slotmap_probe.py paint
  python lw301_slotmap_probe.py id <screenshot.png> [--top N]
"""
import json
import os
import pathlib
import struct
import sys
import tempfile

HERE = pathlib.Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))

# Fifteen slot colours, chosen for maximum mutual distance in 5-bit RGB and, deliberately, for
# distance from the *scene*: no near-blacks and no near-whites, because grass, stone and armour
# highlights supply plenty of both and an exact-match classifier would pick them up.
SLOT_RGB5 = {
    1:  (31, 0, 0),   2:  (31, 16, 0),  3:  (31, 31, 0),  4:  (16, 31, 0),  5:  (0, 31, 0),
    6:  (0, 31, 16),  7:  (0, 31, 31),  8:  (0, 16, 31),  9:  (0, 0, 31),   10: (16, 0, 31),
    11: (31, 0, 31),  12: (31, 0, 16),  13: (20, 10, 4),  14: (10, 20, 26), 15: (26, 22, 14),
}
BANKS = [0x140D35750, 0x140D35950]
WEAPON_PALETTES = range(3, 16)
PAL_BYTES, SHEET_W = 512, 256


def code(slot):
    r, g, b = SLOT_RGB5[slot]
    return r | (g << 5) | (b << 10)


def rgb8(slot):
    r, g, b = SLOT_RGB5[slot]
    return (r << 3, g << 3, b << 3)


def cmd_paint():
    import battle_cheats as bc
    bc._require_game()
    codes = [0] + [code(s) for s in range(1, 16)]
    ok = 0
    for pal in WEAPON_PALETTES:
        for base in BANKS:
            tgt = base + pal * 32
            raw = bc.rpm(tgt, 32)
            if not raw:
                print(f"  pal {pal} bank {base:#x} READ FAILED")
                continue
            cur = struct.unpack("<16H", raw)
            new = [c if c == 0 else ((c & 0x8000) | codes[i]) for i, c in enumerate(cur)]
            ok += bool(bc.wpm(tgt, struct.pack("<16H", *new)))
    print(f"painted {ok}/{len(list(WEAPON_PALETTES)) * len(BANKS)} palette/bank pairs")
    print("\nslot legend (what each index looks like on screen):")
    for s in range(1, 16):
        print(f"   slot {s:>2}  rgb{rgb8(s)}")
    print("\nEvery weapon palette now carries the SAME ramp, so the colour no longer names the")
    print("palette. Name the WEAPON in the shot instead; the slot pattern names the sprite.")


def load_page1():
    import lw251_wep_spr_forge as forge
    import numpy as np
    wd = tempfile.mkdtemp(prefix="lw301_slot_")
    raw = forge.load_vanilla(wd)
    raw = raw[0] if isinstance(raw, tuple) else raw
    pix = raw[PAL_BYTES:PAL_BYTES + 32768]
    page = np.zeros((256, SHEET_W), dtype=np.uint8)
    flat = np.frombuffer(pix, dtype=np.uint8)
    lo, hi = flat & 0xF, flat >> 4
    inter = np.empty(flat.size * 2, dtype=np.uint8)
    inter[0::2], inter[1::2] = lo, hi
    return inter.reshape(256, SHEET_W)


def cmd_id(path, top=10):
    import numpy as np
    from PIL import Image
    from scipy import ndimage
    a = np.asarray(Image.open(path).convert("RGB")).astype(float)
    # The engine tints sprites with the scene lighting (a night map darkens and blues everything),
    # so an exact RGB match finds almost nothing. Compare CHROMATICITY instead -- r/(r+g+b) and
    # g/(r+g+b) -- which is invariant to any uniform brightness scale, and segment on saturation,
    # because the fifteen ramp colours are far more saturated than grass, stone or armour.
    tot = a.sum(2) + 1e-6
    chrom = np.dstack([a[:, :, 0] / tot, a[:, :, 1] / tot])
    mx, mn = a.max(2), a.min(2)
    sat = (mx - mn) / (mx + 1e-6)
    refs = {s: np.array(rgb8(s), dtype=float) for s in range(1, 16)}
    rc = {s: np.array([v[0] / v.sum(), v[1] / v.sum()]) for s, v in refs.items()
          if max(v) - min(v) > 40}          # drop the three muted slots: no reliable chromaticity
    keys = sorted(rc)
    d = np.dstack([np.abs(chrom - rc[s]).sum(2) for s in keys])
    near = d.min(2)
    lab_idx = np.array(keys)[d.argmin(2)]
    # Slots 13-15 are muted by design and a tinted night scene sits right on their chromaticity,
    # so they are excluded from the query as well as from the tiles.
    cand = (sat > 0.60) & (mx > 70) & (near < 0.075)
    lab, n = ndimage.label(cand, structure=np.ones((3, 3)))
    if n == 0:
        print("no painted pixels found -- is the ramp applied, and is the weapon on screen?")
        return
    # THE discriminator: a weapon carries many palette slots, a field of grass carries one. Rank
    # components by how many DISTINCT slots they contain, not by area, or the scenery always wins.
    best, keep = None, None
    for k in range(1, n + 1):
        comp = lab == k
        if comp.sum() < 25:
            continue
        distinct = len(set(lab_idx[comp].tolist()))
        score = (distinct, int(comp.sum()))
        if best is None or score > best:
            best, keep = score, comp
    if keep is None:
        print("no component large enough")
        return
    print(f"picked a {int(keep.sum())} px component spanning {best[0]} distinct slots")
    hist = np.array([float(((lab_idx == s) & keep).sum()) for s in range(1, 16)])
    print(f"weapon blob {int(keep.sum())} px; slot counts:")
    for s in range(1, 16):
        if hist[s - 1]:
            print(f"   slot {s:>2}: {int(hist[s-1]):>5}")
    q = hist / max(1.0, hist.sum())

    page = load_page1()
    boxes = json.loads((HERE / "lw301_sprite_boxes.json").read_text(encoding="utf-8"))
    rows = []
    for b in boxes:
        t = page[b["y"]:b["y"] + b["h"], b["x"]:b["x"] + b["w"]]
        h = np.array([float((t == s).sum()) for s in range(1, 16)])
        # score on the SAME slots the shot can see; the muted three are invisible to chromaticity
        h = h * np.array([1.0 if s in rc else 0.0 for s in range(1, 16)])
        if h.sum() < 6:
            continue
        p_ = h / h.sum()
        rows.append((float(np.sqrt(q * p_).sum()), b["i"], set(np.nonzero(h)[0] + 1)))
    rows.sort(reverse=True)
    qslots = set(np.nonzero(hist)[0] + 1)
    print("")
    print("slots seen:", sorted(qslots))
    try:
        names = {int(k): v["name"] for k, v in
                 json.loads((HERE / "lw301_sprite_labels.json").read_text())["labels"].items()}
    except Exception:
        names = {}
    print(f"{'tile':>6} {'score':>7}  {'label':<12} slots")
    for s, i, sl in rows[:top]:
        miss = qslots - sl
        print(f"{i:>6} {s:>7.4f}  {names.get(i,'?'):<12} {sorted(sl)}"
              + (f"   MISSING {sorted(miss)}" if miss else ""))


def main():
    mode = sys.argv[1] if len(sys.argv) > 1 else ""
    if mode == "paint":
        cmd_paint()
    elif mode == "id" and len(sys.argv) >= 3:
        top = int(sys.argv[sys.argv.index("--top") + 1]) if "--top" in sys.argv else 10
        cmd_id(sys.argv[2], top)
    else:
        print(__doc__)


main()
