#!/usr/bin/env python
"""LW-289 Tier 1: bake the battle weapon sheet's palettes from the design tints. GENERATED ARTIFACT.

WHAT THIS PRODUCES. `mod/FFTIVC/data/enhanced/fftpack/unit/battle_wep_spr.bin`, a copy of FFTPack
file 71 whose weapon palettes carry this mod's own colours instead of vanilla's, so a weapon a
player swings agrees with the card they equipped it from. NEVER hand edit the output; edit
`data/items.json` and re-run.

WHAT IT HONESTLY PROMISES, WHICH IS LESS THAN "EVERY WEAPON MATCHES ITS CARD". The game gives
thirteen palettes to a hundred and twenty seven weapons and WHICH palette a weapon uses cannot be
changed by any channel we can reach (ledger [weapon-palette-assignment-walled]: four levers tested
live, all dead; disk exhausted across 14.35 GB in the sibling session). So the grouping is the
game's, not ours, and the promise is per palette GROUP. Some weapons will land far from their card:
the groups are colour incoherent, and the best single hue for a group leaves its worst member about
110 degrees away on average. The tool measures and reports that per group rather than hiding it.

WHY IT IS SAFE TO DO AT ALL. Weapons draw from palettes 3-15 and effects from 0-2, with ZERO
overlap across all 127 weapons (BATTLE.BIN X and Y nibbles, cross-checked against an independent
pixel-level finding). So repainting the weapon palettes can never retint a slash arc. This tool
refuses to touch palettes 0, 1 and 2, and gates that it did not.

HOW IT PAINTS. Not a flat tint, and not the one-ramp luminance sort the old probe painter used.
Every full palette is four short hue ramps plus a shared specular: {1,2,3,4} body, {5,6,7} and
{8,9,10} and {11,12,13,14} accents, {15} highlight. Sorting all fifteen slots by luminance shuffles
colour ACROSS those independent ramps and collapses the artist's multi-material look. Instead:

  1. Take the group's design hue as the circular mean of the `iconTint` hues of every weapon that
     draws from that palette.
  2. Anchor on the BODY zone: compute the vanilla circular-mean hue of slots 1-4.
  3. Rotate EVERY non-transparent slot in the palette by the single delta that moves the anchor
     onto the group hue.

Because one delta is applied to the whole palette, every relative hue relationship the artist
chose survives exactly: a steel body with a brass accent stays a body with a contrasting accent,
rotated as a unit. Value is preserved bit for bit, so shading and contrast are untouched, and
saturation is nudged toward the group's mean only as far as SAT_PULL allows.

THE FILE IS THREE (palette, page) PAIRS. palA at 0x0000 serves page 1 (rows 0-255, weapons); palB
at 0x8200 is byte-identical to palA in vanilla and serves page 2 (rows 256-511, arcs, which use
slots 11-15 only); palC at 0x10400 is a separate additive glow bank for page 3. This tool paints
palettes 3-15 in BOTH palA and palB, which is correct under either reading of which bank feeds
which page, because no effect ever uses a palette in 3-15. palC is never touched. Total stays
exactly 85504 bytes, which matters: the loader copies a full 0x15000 request out of an uncleared
ArrayPool rental, so a short file leaks pool garbage into the tail.

USAGE:
  python tools/bake_weapon_sheet.py --selftest    # pure checks, no game install needed
  python tools/bake_weapon_sheet.py --report      # who shares which palette, and how incoherent
  python tools/bake_weapon_sheet.py               # bake into the mod tree + write previews
  python tools/bake_weapon_sheet.py --preview-only
"""
import colorsys
import json
import math
import os
import struct
import sys
from collections import defaultdict

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from lib.paths import ITEMS, ROOT  # noqa: E402

sys.path.insert(0, str(ROOT / "tools" / "probes"))
from lw289_battle_bin_palette_map import extract as extract_battle_bin  # noqa: E402
from lw289_battle_bin_palette_map import (  # noqa: E402
    FIRST_ITEM, LAST_ITEM, gate as battle_bin_gate, record_offset,
)
from lw251_wep_spr_forge import load_vanilla  # noqa: E402

OUT_DIR = ROOT / "mod" / "FFTIVC" / "data" / "enhanced" / "fftpack" / "unit"
OUT_NAME = "battle_wep_spr.bin"
TOTAL_BYTES = 85504
# Three (palette, page) pairs. Offsets verified by byte arithmetic and hashes 2026-08-19.
BANKS = (0x0000, 0x8200, 0x10400)          # palA, palB, palC
PAL_BANK_BYTES = 512
PAINT_BANKS = (0x0000, 0x8200)             # palC is an additive glow bank; never repainted
EFFECT_PALETTES = (0, 1, 2)                # weapons never use these; refuse to touch them
BODY_ZONE = (1, 2, 3, 4)                   # the anchor: the main material of every weapon
SAT_PULL = 0.35                            # how far saturation moves toward the group mean


def _rgb(v):
    return ((v & 0x1F) / 31.0, ((v >> 5) & 0x1F) / 31.0, ((v >> 10) & 0x1F) / 31.0)


def _pack(r, g, b, hi_bit):
    q = lambda c: max(0, min(31, int(round(c * 31))))
    return hi_bit | q(r) | (q(g) << 5) | (q(b) << 10)


def _circmean(hues):
    """Mean of hues on the circle. A plain average of 350 and 10 degrees gives 180, which is the
    opposite colour; this gives 0."""
    x = sum(math.cos(h * 2 * math.pi) for h in hues)
    y = sum(math.sin(h * 2 * math.pi) for h in hues)
    if abs(x) < 1e-12 and abs(y) < 1e-12:
        return 0.0, 0.0
    return (math.atan2(y, x) / (2 * math.pi)) % 1.0, math.hypot(x, y) / len(hues)


def _hue_gap(a, b):
    d = abs(a - b) % 1.0
    return min(d, 1.0 - d)


def load_items():
    d = json.loads(open(ITEMS, encoding="utf-8").read())
    items = d["items"] if isinstance(d, dict) and "items" in d else d
    if isinstance(items, dict):
        items = list(items.values())
    return {it["id"]: it for it in items}


def weapon_palette_map(work_dir):
    raw = extract_battle_bin(work_dir)
    battle_bin_gate(raw)
    return {i: raw[record_offset(i)] >> 4 for i in range(FIRST_ITEM, LAST_ITEM + 1)}


def groups(work_dir):
    """palette -> {'items': [...], 'hue': circular mean of design tints, 'coherence': 0..1,
    'worst': the largest hue gap any member suffers, in degrees}."""
    items = load_items()
    xmap = weapon_palette_map(work_dir)
    by = defaultdict(list)
    for iid, pal in xmap.items():
        it = items.get(iid)
        if it and it.get("iconTint"):
            by[pal].append((iid, it["name"], tuple(it["iconTint"])))
    out = {}
    for pal, members in sorted(by.items()):
        hues = [t[0] for _, _, t in members]
        sats = [t[1] for _, _, t in members]
        h, coh = _circmean(hues)
        worst = max(_hue_gap(h, hh) for hh in hues) * 360.0
        out[pal] = {"items": members, "hue": h, "sat": sum(sats) / len(sats),
                    "coherence": coh, "worst": worst}
    return out


def paint_bank(raw, bank_off, plan):
    """Rotate whole palettes by one delta each. Value is preserved exactly; the transparent slot 0
    and any 0x0000 entry are untouched; bit 15 is carried through."""
    pal = list(struct.unpack_from(f"<{PAL_BANK_BYTES // 2}H", raw, bank_off))
    touched = 0
    for p, spec in plan.items():
        if p in EFFECT_PALETTES:
            raise AssertionError(f"refusing to paint effect palette {p}")
        live = [s for s in range(1, 16) if pal[p * 16 + s]]
        if not live:
            continue
        body = [s for s in BODY_ZONE if pal[p * 16 + s]] or live
        anchor, _ = _circmean([colorsys.rgb_to_hsv(*_rgb(pal[p * 16 + s]))[0] for s in body])
        delta = (spec["hue"] - anchor) % 1.0
        for s in live:
            k = p * 16 + s
            h, sat, val = colorsys.rgb_to_hsv(*_rgb(pal[k]))
            new_s = sat + (spec["sat"] - sat) * SAT_PULL if sat > 0.04 else sat
            r, g, b = colorsys.hsv_to_rgb((h + delta) % 1.0, max(0.0, min(1.0, new_s)), val)
            pal[k] = _pack(r, g, b, pal[k] & 0x8000)
            touched += 1
    return (raw[:bank_off]
            + struct.pack(f"<{PAL_BANK_BYTES // 2}H", *pal)
            + raw[bank_off + PAL_BANK_BYTES:]), touched


def verify(vanilla, baked, plan):
    """Everything this bake promises not to do, checked rather than asserted in prose."""
    assert len(baked) == TOTAL_BYTES, f"output is {len(baked)} B, must be exactly {TOTAL_BYTES}"
    # pixel pages untouched
    for off, ln in ((0x0200, 32768), (0x8400, 32768), (0x10600, 18432)):
        assert baked[off:off + ln] == vanilla[off:off + ln], f"pixel page at 0x{off:X} was modified"
    # palC untouched
    assert baked[0x10400:0x10600] == vanilla[0x10400:0x10600], "palC (glow bank) was modified"
    for bank in PAINT_BANKS:
        van = struct.unpack_from(f"<{PAL_BANK_BYTES // 2}H", vanilla, bank)
        got = struct.unpack_from(f"<{PAL_BANK_BYTES // 2}H", baked, bank)
        for p in range(16):
            for s in range(16):
                k = p * 16 + s
                if p in EFFECT_PALETTES or p not in plan:
                    assert got[k] == van[k], f"bank 0x{bank:X} palette {p} slot {s} changed"
                    continue
                if s == 0 or van[k] == 0:
                    assert got[k] == van[k], f"transparent slot {p}:{s} was painted"
                    continue
                assert (got[k] & 0x8000) == (van[k] & 0x8000), f"bit 15 lost at {p}:{s}"
            if p in EFFECT_PALETTES or p not in plan:
                continue
            # luminance ORDER must survive, or the artist's shading is destroyed
            live = [s for s in range(1, 16) if van[p * 16 + s]]
            vv = lambda arr, s: colorsys.rgb_to_hsv(*_rgb(arr[p * 16 + s]))[2]
            assert sorted(live, key=lambda s: vv(van, s)) == sorted(live, key=lambda s: vv(got, s)), \
                f"palette {p} luminance ordering changed; shading would be destroyed"
    return True


def preview(vanilla, baked, out_dir):
    from PIL import Image
    pix = vanilla[0x0200:0x0200 + 32768]          # page 1 only: the weapon art
    w, h = 256, 256
    tiles = []
    for tag, src in (("vanilla", vanilla), ("baked", baked)):
        pal = struct.unpack_from(f"<{PAL_BANK_BYTES // 2}H", src, 0x0000)
        im = Image.new("RGB", (w, h), (24, 24, 28))
        px = im.load()
        for i in range(w * h):
            b = pix[i // 2]
            n = (b & 0xF) if i % 2 == 0 else (b >> 4)
            if n == 0:
                continue
            # render every tile under palette 13, the single busiest weapon palette, purely so the
            # two previews are comparable; per-palette truth needs the map, which the report prints
            v = pal[13 * 16 + n]
            px[i % w, i // w] = tuple(int(c * 255) for c in _rgb(v))
        tiles.append((tag, im))
    strip = Image.new("RGB", (w * 2 + 12, h), (12, 12, 14))
    for i, (_, im) in enumerate(tiles):
        strip.paste(im, (i * (w + 12), 0))
    p = os.path.join(out_dir, "lw289_bake_preview.png")
    strip.resize((strip.width * 3, strip.height * 3), Image.NEAREST).save(p)
    return p


def report(g):
    print(f"{'pal':>4} {'n':>3} {'hue':>6} {'coh':>5} {'worst':>6}  members")
    for pal, spec in sorted(g.items()):
        names = ", ".join(n for _, n, _ in spec["items"])
        print(f"{pal:>4} {len(spec['items']):>3} {spec['hue'] * 360:>6.1f} "
              f"{spec['coherence']:>5.2f} {spec['worst']:>6.1f}  {names[:96]}")
    worst = max(s["worst"] for s in g.values())
    mean = sum(s["worst"] for s in g.values()) / len(g)
    print(f"\n{len(g)} weapon palettes, {sum(len(s['items']) for s in g.values())} weapons")
    print(f"worst single member is {worst:.0f} degrees from its group hue; mean worst {mean:.0f}")
    print("Those numbers are the honest cost of a grouping we do not control "
          "(ledger [weapon-palette-assignment-walled]).")


def selftest():
    assert sum(1 for _ in BANKS) == 3
    assert 0x0200 + 32768 == 0x8200 and 0x8400 + 32768 == 0x10400 and 0x10600 + 18432 == TOTAL_BYTES
    h, coh = _circmean([350 / 360.0, 10 / 360.0])
    assert _hue_gap(h, 0.0) < 1e-9, f"circular mean of 350 and 10 gave {h * 360}, expected 0"
    assert coh > 0.98, "two near-identical hues should read as coherent"
    _, coh2 = _circmean([0.0, 0.5])
    assert coh2 < 0.01, "opposite hues should read as incoherent"
    for v in (0x0000, 0x7FFF, 0x8421, 0x1234):
        r, g, b = _rgb(v)
        assert _pack(r, g, b, v & 0x8000) == v, f"pack/unpack is not a round trip for {v:#06x}"
    # painting must preserve value exactly and leave transparent slots alone
    fake = bytearray(TOTAL_BYTES)
    for bank in PAINT_BANKS:
        for p in range(16):
            for s in range(16):
                fake[bank + (p * 16 + s) * 2: bank + (p * 16 + s) * 2 + 2] = struct.pack(
                    "<H", 0 if s in (0, 5) else (0x8000 if s == 1 else 0) | (s | (s << 5) | (s << 10)))
    plan = {p: {"hue": 0.25, "sat": 0.8} for p in range(3, 16)}
    out, touched = paint_bank(bytes(fake), 0x0000, plan)
    assert touched == 13 * 14, f"expected 182 painted slots, got {touched}"
    van = struct.unpack_from("<256H", bytes(fake), 0)
    got = struct.unpack_from("<256H", out, 0)
    for p in range(16):
        for s in range(16):
            k = p * 16 + s
            if p in EFFECT_PALETTES or s in (0, 5):
                assert got[k] == van[k], f"{p}:{s} should be untouched"
            else:
                assert (got[k] & 0x8000) == (van[k] & 0x8000), "bit 15 lost"
                assert abs(colorsys.rgb_to_hsv(*_rgb(got[k]))[2]
                           - colorsys.rgb_to_hsv(*_rgb(van[k]))[2]) < 0.04, \
                    f"value moved at {p}:{s}; shading must be preserved"
    # and the verifier must REJECT a bake that touches an effect palette
    bad = bytearray(out)
    bad[0x0000 + (1 * 16 + 3) * 2] ^= 0xFF
    try:
        verify(bytes(fake), bytes(bad), plan)
    except AssertionError:
        pass
    else:
        raise AssertionError("verify() accepted a bake that modified an effect palette")
    print("selftest OK")


def main():
    if "--selftest" in sys.argv:
        selftest()
        return
    selftest()
    work = os.path.join(os.environ.get("TEMP", "."), "lw289")
    g = groups(work)
    if "--report" in sys.argv:
        report(g)
        return
    vanilla = load_vanilla(work)
    plan = {p: {"hue": s["hue"], "sat": s["sat"]} for p, s in g.items()}
    baked = vanilla
    total = 0
    for bank in PAINT_BANKS:
        baked, n = paint_bank(baked, bank, plan)
        total += n
    verify(vanilla, baked, plan)
    report(g)
    print(f"\npainted {total} slots across {len(PAINT_BANKS)} palette banks; "
          f"pixel pages and palC untouched; {len(baked)} bytes")
    print("preview:", preview(vanilla, baked, os.environ.get("TEMP", ".")))
    if "--preview-only" in sys.argv:
        print("preview only; no file written")
        return
    os.makedirs(OUT_DIR, exist_ok=True)
    dst = os.path.join(OUT_DIR, OUT_NAME)
    open(dst, "wb").write(baked)
    print(f"wrote GENERATED artifact {dst} (never hand edit; edit data/items.json and re-run)")


if __name__ == "__main__":
    main()
