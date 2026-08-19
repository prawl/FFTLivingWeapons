#!/usr/bin/env python
"""LW-251 round 12: does the CLASSIC weapon sprite palette drive battle weapon colour?

WHY THIS TARGET, AND WHY IT IS NOT THE g2d DEAD END. The 2026-08-19 retraction
(LIVE_LEDGER [g2d-clut-bank-override]) established from the loader's own log that g2d entry
156 has NEVER been read by the game, and that the g2d equipment sheet (entry 161) is served
exactly once per launch at the menu, which fits menu art. The same logs name what the game
DOES read at every battle: FFTPack file 71 -> unit/battle_wep_spr.bin, over ninety reads
across eighteen launches, one per battle load, alongside file 63/64 (battle_wep1/2_shp.bin,
frame geometry) and 65/66 (the _seq animation files). That is the classic 2D weapon sprite
pipeline the 2026-06-01 scoping identified (docs/research/WEAPON_VISUALS_SCOPING.md) and the
HD layer upscales it.

THE PRIOR NEGATIVE, AND WHY IT DESERVES ONE PROPER RE-RUN. That scoping's Ask B tested this
exact file: it magenta-filled palettes 0 and 1 and saw no change, concluding "the HD weapon
render does NOT index this 2D bin palette". Two holes, and the doc names the first itself
("re-confirm the chocobo bin still recolors via the same override path, to rule out a
deploy/channel fault"), a control it never ran:
  1. SERVING WAS NEVER PROVEN. Exactly the flaw that produced tonight's false PROVEN.
  2. THE TEST WAS NARROW. Only 2 of 16 palettes were touched, so any weapon whose palette
     index is 2..15 would render vanilla and read as a negative. This forge flattens ALL
     SIXTEEN, so no weapon can escape it.

FILE FORMAT (classic FFT WEP.SPR, verified against the live file 2026-08-19, CORRECTED
2026-08-19 later the same day): 85504 bytes = THREE (palette, page) PAIRS, not one palette
block followed by one image.

    palA  @0x00000   512 B                    page1 @0x00200  32768 B  rows   0-255  WEAPONS
    palB  @0x08200   512 B (== palA vanilla)  page2 @0x08400  32768 B  rows 256-511  arcs
    palC  @0x10400   512 B (a different bank) page3 @0x10600  18432 B  rows 512-655  impacts

Each palette block is 16 palettes x 16 BGR555 u16, colour 0 transparent; pixels are 4bpp, low
nibble first, 256 px wide. WEAPONS DRAW FROM palA/page1, proven by this probe's own deployment:
flattening palA alone turned every weapon in game flat. Page 2 inks only slots 11-15.

THE EARLIER TEXT HERE SAID "a 512-byte palette block followed by 84992 bytes of 4bpp pixels,
256 px wide x 664 rows" AND THAT IS WRONG. It is recorded rather than deleted because the
sibling ColorCustomizer session read this docstring and built a published feature proposal on
it before the error was caught (docs/TODO.md LW-294). Reading the pixel block as one 256x664
image splices palB and palC in as 4-row junk bands at y 256-259 and y 516-519 and mislocates
every row above 255. Decode page 1 alone for a weapon preview.

TWO THINGS THAT BITE WHOEVER WRITES THIS FILE NEXT. The writable pixel extents are
0x00200..0x081FF, 0x08400..0x103FF and 0x10600..0x14DFF, with two 512-byte palette holes
between them, so a naive "rewrite the pixels after the header" pass writes straight through
palB and palC. And the file must be EXACTLY 85504 bytes: the loader copies the full 0x15000
request out of an UNCLEARED ArrayPool rental, so a short file leaks pool garbage into the tail
instead of failing loudly. Authority for all of the above is the [wep-spr-palette-block] row in
docs/LIVE_LEDGER.md, CORRECTION 1.

THE EXPERIMENT (single variable, machine-verified liveness, no in-game control needed):
  SHIP     the palette block flattened, one distinct vivid colour per palette; the PIXEL
           block byte-identical vanilla. So the only thing that can change on screen is
           colour, and it can only have come from this palette block.
  LIVENESS is read from the loader log, not from the screen. The loader prints
           "[FFTPack] Accessing file 71 -> unit/battle_wep_spr.bin" when it serves the GAME's
           copy and "Accessing MODDED file 71" when it serves OURS (that wording is observed
           live for dozens of ColorCustomizer unit sprites). Run --checklog after the launch.
READINGS, pre-registered:
  log says "modded file 71" + weapons FLAT   -> battle weapon colour is ours. The palette
                                                index a weapon uses is then readable straight
                                                off the screen (16 distinct colours).
  log says "modded file 71" + weapons NORMAL -> the 2026-06-01 negative is CONFIRMED, this
                                                time with serving proven: the HD renderer does
                                                not index this palette block. Stop testing it;
                                                the next question is whether it reads the
                                                PIXEL block at all (--derange).
  log says plain "file 71"                   -> our override was not served; deploy/channel
                                                fault, conclude NOTHING about colour, fix and
                                                re-run. This is the branch the 2026-06 test
                                                could not distinguish and we now can.

USAGE:
  python lw251_wep_spr_forge.py <work_dir>              # extract + verify + forge + preview
  python lw251_wep_spr_forge.py <work_dir> --derange    # ...also scramble the PIXEL block
                                                        #    (the follow-up question, only run
                                                        #    it when the palette read is in)
  python lw251_wep_spr_forge.py <work_dir> --deploy     # ...and install into livingweapons
  python lw251_wep_spr_forge.py --checklog              # was file 71 served MODDED last launch?
  python lw251_wep_spr_forge.py --selftest              # pure checks, touches nothing
Undo: delete <mods>/prawl.fft.livingweapons/FFTIVC/data/enhanced/fftpack/ (the next
BuildLinked wipes it too, so deploy AFTER any BuildLinked run, never before).
"""
import hashlib
import os
import shutil
import struct
import subprocess
import sys

GAME = (r"c:\program files (x86)\steam\steamapps\common"
        r"\FINAL FANTASY TACTICS - The Ivalice Chronicles")
# ALWAYS extract the source fresh from 0002.pac. Loose copies on this box are NOT pristine:
# data/enhanced/0002/0002/fftpack/unit/battle_wep_spr.bin (md5 6439436f...) carries a flat RED
# palette 0 with palettes 1-8 zeroed, a leftover flat-fill test artifact, and forging from it
# would have silently produced a nonsense experiment. The pac copy (md5 cf6ad45e...) has the
# real ramps: 0/13 steel, 3/9/10/12 wood, 4/7 blue, 5 purple, 6 copper, 8 gold, 11/14/15
# green-grey, with palettes 1 and 2 short 5-colour rows.
SOURCE_PAC = os.path.join(GAME, "data", "enhanced", "0002.pac")
INNER = "fftpack/unit/battle_wep_spr.bin"
VANILLA_MD5 = "cf6ad45e04fef2b1795dfff5b8e54c21"
FF16TOOLS = (r"C:\Users\ptyRa\Downloads\FF16Tools.CLI-1.13.2-win-x64"
             r"\win-x64\FF16Tools.CLI.exe")
MODS_ENV = "RELOADEDIIMODS"
DEPLOY_SUB = os.path.join("prawl.fft.livingweapons", "FFTIVC", "data", "enhanced",
                          "fftpack", "unit")
NAME = "battle_wep_spr.bin"
FFTPACK_ID = 71               # fftpack.txt line 89: 71|unit\battle_wep_spr.bin
PAL_BYTES = 16 * 16 * 2       # 16 palettes x 16 BGR555
TOTAL_BYTES = 85504
SHEET_W = 256
LOGS = os.path.join(os.environ.get("APPDATA", ""), "Reloaded-Mod-Loader-II", "Logs")
REPO_ICONS = os.path.join(os.path.dirname(os.path.dirname(os.path.dirname(
    os.path.abspath(__file__)))), "mod", "FFTIVC", "data", "enhanced", "ui", "ffto",
    "icon", "equip_item", "texture")

def _codes():
    """16 census colours: 8 hues 45 degrees apart, each at full and at 55% value. Generated
    rather than hand-listed for two reasons found the hard way. NO GREY AND NO WHITE: a flat
    grey or white blade at sprite scale reads as an unchanged STEEL weapon, so a working probe
    would be reported as 'no change' (this is a live suspect for the 2026-06-01 negative). And
    no near-duplicate pairs: an earlier hand-list had red beside orange-red and magenta beside
    orchid, fine for a yes/no read but unreliable for NAMING which palette a weapon uses,
    which is the census this instrument exists for."""
    import colorsys
    out = []
    for i in range(16):
        hue, val = (i % 8) * 45 / 360.0, (1.0, 0.55)[i // 8]
        r, g, b = colorsys.hsv_to_rgb(hue, 1.0, val)
        r5, g5, b5 = (max(1, int(c * 31)) for c in (r, g, b))
        name = ["RED", "ORANGE", "YELLOW", "GREEN", "CYAN", "AZURE", "VIOLET", "MAGENTA"][i % 8]
        out.append((r5 | (g5 << 5) | (b5 << 10), name if i < 8 else "DARK " + name))
    return out


# Every palette gets one, so a weapon renders as a flat blob whose colour NAMES the palette
# index it drew from. That is how the Flamberge was identified as palette 15.
PALETTE_CODES = _codes()


def load_vanilla(work_dir):
    """Extract the pristine sheet from 0002.pac and hash-check it, so a contaminated loose
    copy can never sneak into a forge."""
    out = os.path.join(work_dir, "pac_unpack")
    path = os.path.join(out, *INNER.split("/"))
    if os.path.isfile(path):
        os.remove(path)
    subprocess.run([FF16TOOLS, "unpack", "-i", SOURCE_PAC, "-f", INNER, "-o", out, "-g", "fft"],
                   capture_output=True)
    if not os.path.isfile(path):
        sys.exit(f"unpack produced no {path}")
    raw = open(path, "rb").read()
    got = hashlib.md5(raw).hexdigest()
    if got != VANILLA_MD5:
        sys.exit(f"source md5 {got} != expected {VANILLA_MD5}; the pac changed, STOP")
    if len(raw) != TOTAL_BYTES:
        sys.exit(f"{NAME} is {len(raw)} bytes, expected {TOTAL_BYTES}; format changed, STOP")
    pix = raw[PAL_BYTES:]
    if len(pix) * 2 % SHEET_W:
        sys.exit("pixel block does not divide into 256-wide rows; format changed, STOP")
    return raw


def forge_palettes(raw):
    """Flatten all 16 palettes: slot 0 and any 0x0000 stay untouched (transparent), every
    other colour keeps bit 15 and takes its palette's code colour. Pixels are NOT touched."""
    pal = list(struct.unpack_from(f"<{PAL_BYTES // 2}H", raw, 0))
    changed = 0
    for p in range(16):
        for slot in range(1, 16):
            k = p * 16 + slot
            if pal[k] == 0:
                continue
            pal[k] = (pal[k] & 0x8000) | PALETTE_CODES[p][0]
            changed += 1
    return struct.pack(f"<{PAL_BYTES // 2}H", *pal) + raw[PAL_BYTES:], changed


def icon_ramp(item_id, slots):
    """Build a `slots`-long dark-to-light BGR555 ramp from an item's SHIPPED menu icon, so a
    battle weapon can be painted in the colours players already see on its card. Samples the
    icon's opaque pixels by luminance percentile, which keeps the art's own hue at each
    tone (a red-rimmed steel blade stays red-rimmed steel) instead of inventing a flat tint."""
    from PIL import Image
    stem = f"ei_{item_id:03d}_uitx"
    work = os.path.join(os.environ["TEMP"], "lw251_icon")
    os.makedirs(work, exist_ok=True)
    src = os.path.join(REPO_ICONS, f"{stem}.tex")
    if not os.path.isfile(src):
        sys.exit(f"no shipped icon for item {item_id}: {src}")
    shutil.copy2(src, os.path.join(work, f"{stem}.tex"))
    subprocess.run([FF16TOOLS, "tex-conv", "-i", os.path.join(work, f"{stem}.tex")],
                   capture_output=True)
    im = Image.open(os.path.join(work, f"{stem}.dds")).convert("RGBA")
    px = [im.getpixel((x, y))[:3] for y in range(im.height) for x in range(im.width)
          if im.getpixel((x, y))[3] > 128]
    if len(px) < slots:
        sys.exit(f"icon for item {item_id} has too few opaque pixels ({len(px)})")
    px.sort(key=lambda c: 0.299 * c[0] + 0.587 * c[1] + 0.114 * c[2])
    ramp = []
    for i in range(slots):
        r, g, b = px[min(len(px) - 1, (i * len(px)) // slots + len(px) // (2 * slots))]
        ramp.append((r >> 3) | ((g >> 3) << 5) | ((b >> 3) << 10) or 1)  # never 0 (=transparent)
    return ramp


def paint_palette(raw, index, ramp, order_src):
    """Write `ramp` into one palette, preserving its transparent slots and its dark-to-light
    ORDER (slot k keeps its luminance rank), so the artist's shading survives the re-hue.
    order_src MUST be the VANILLA file: ranking by luminance only means something before the
    flattening pass, since after it every slot in a palette holds the same colour and the
    sort order is arbitrary."""
    pal = list(struct.unpack_from(f"<{PAL_BYTES // 2}H", raw, 0))
    van = struct.unpack_from(f"<{PAL_BYTES // 2}H", order_src, 0)
    slots = [s for s in range(1, 16) if pal[index * 16 + s]]
    order = sorted(slots, key=lambda s: _lum(van[index * 16 + s]))
    for rank, s in enumerate(order):
        pal[index * 16 + s] = (pal[index * 16 + s] & 0x8000) | ramp[rank]
    return struct.pack(f"<{PAL_BYTES // 2}H", *pal) + raw[PAL_BYTES:], len(order)


def _lum(v):
    return 0.299 * (v & 0x1F) + 0.587 * ((v >> 5) & 0x1F) + 0.114 * ((v >> 10) & 0x1F)


def _dn(n):
    return 0 if n == 0 else (n % 15) + 1


def derange_pixels(raw):
    """Shift every palette index (0 stays transparent). Answers the separate question of
    whether the renderer reads this file's PIXELS, independent of its palettes."""
    lut = bytes((_dn(b & 0xF) | (_dn(b >> 4) << 4)) for b in range(256))
    return raw[:PAL_BYTES] + raw[PAL_BYTES:].translate(lut)


def preview(work_dir, raw, tag):
    from PIL import Image
    pal = struct.unpack_from(f"<{PAL_BYTES // 2}H", raw, 0)
    pix = raw[PAL_BYTES:]
    h = len(pix) * 2 // SHEET_W
    im = Image.new("RGBA", (SHEET_W, h))
    px = im.load()
    for i in range(len(pix) * 2):
        b = pix[i // 2]
        n = (b & 0xF) if i % 2 == 0 else (b >> 4)
        x, y = i % SHEET_W, i // SHEET_W
        if n == 0:
            px[x, y] = (0, 0, 0, 0)
            continue
        v = pal[n]                      # palette 0; the sheet's rows pick their own live
        px[x, y] = ((v & 0x1F) << 3, ((v >> 5) & 0x1F) << 3, ((v >> 10) & 0x1F) << 3, 255)
    im.save(os.path.join(work_dir, f"wep_{tag}.png"))


def newest_log():
    if not os.path.isdir(LOGS):
        return None
    logs = [os.path.join(LOGS, f) for f in os.listdir(LOGS) if f.endswith(".txt")]
    return max(logs, key=os.path.getmtime) if logs else None


def checklog():
    """Read the pre-registered liveness answer out of the newest launch log."""
    log = newest_log()
    if not log:
        sys.exit(f"no Reloaded logs under {LOGS}")
    print(f"log: {os.path.basename(log)}")
    modded = plain = 0
    with open(log, encoding="utf-8", errors="ignore") as fh:
        for line in fh:
            if f"file {FFTPACK_ID} -> unit/{NAME}" not in line:
                continue
            if "modded file" in line:
                modded += 1
            else:
                plain += 1
    print(f"  served from OUR override : {modded}")
    print(f"  served from the GAME copy: {plain}")
    if modded:
        print("LIVE: the game read our file. A vanilla-looking weapon is now a REAL negative "
              "about the palette block, not a channel fault.")
    elif plain:
        print("NOT SERVED: the game read its own copy. Conclude NOTHING about colour; the "
              "override did not take. Check the deploy path and the mod's enabled state.")
    else:
        print("NOT READ AT ALL this launch: no battle was loaded (file 71 is read per battle).")


def selftest():
    assert sorted(_dn(n) for n in range(1, 16)) == list(range(1, 16)), "not a permutation"
    assert _dn(0) == 0, "transparency broken"
    assert len(PALETTE_CODES) == 16, "need one code per palette"
    codes = [c for c, _ in PALETTE_CODES]
    assert len(set(codes)) == 16, "palette codes are not distinct"
    assert all(1 <= c <= 0x7FFF for c in codes), "a code is zero or sets bit 15"
    rgb = [((c & 0x1F) << 3, ((c >> 5) & 0x1F) << 3, ((c >> 10) & 0x1F) << 3) for c in codes]
    for i, p in enumerate(rgb):
        near = min(range(16), key=lambda j: sum((a - b) ** 2 for a, b in zip(rgb[j], p)))
        assert near == i, "the code table does not self-decode"
    fake = struct.pack("<256H", *([0, 0x8000, 0x1234, 0] + [0x7FFF] * 12) * 16) + bytes(64)
    forged, changed = forge_palettes(fake)
    assert len(forged) == len(fake), "forge changed the file length"
    assert forged[PAL_BYTES:] == fake[PAL_BYTES:], "forge touched the pixel block"
    got = struct.unpack_from("<256H", forged, 0)
    for p in range(16):
        assert got[p * 16] == 0 and got[p * 16 + 3] == 0, "a transparent slot was painted"
        assert got[p * 16 + 1] & 0x8000, "bit 15 not preserved"
        lows = {v & 0x7FFF for v in got[p * 16 + 1:p * 16 + 16] if v}
        assert lows == {PALETTE_CODES[p][0]}, f"palette {p} is not flat on its own code"
    assert changed == 16 * 14
    dr = derange_pixels(fake)
    assert dr[:PAL_BYTES] == fake[:PAL_BYTES], "derange touched the palette block"
    assert len(dr) == len(fake)
    print("selftest OK")


def main():
    if "--selftest" in sys.argv:
        selftest()
        return
    if "--checklog" in sys.argv:
        checklog()
        return
    selftest()
    work_dir = sys.argv[1]
    os.makedirs(work_dir, exist_ok=True)
    raw = load_vanilla(work_dir)
    print(f"vanilla {NAME}: {len(raw)} bytes = {PAL_BYTES}B palettes + "
          f"{len(raw) - PAL_BYTES}B pixels ({SHEET_W}x{(len(raw) - PAL_BYTES) * 2 // SHEET_W})")
    out, changed = forge_palettes(raw)
    print(f"flattened all 16 palettes ({changed} colours); pixel block untouched: "
          f"{out[PAL_BYTES:] == raw[PAL_BYTES:]}")
    for i, (_, name) in enumerate(PALETTE_CODES):
        print(f"  palette {i:2} -> {name}")
    # --icon <palette>:<itemId> paints ONE palette in that item's shipped menu-icon colours
    # while the other fifteen keep their flat codes. Two answers from one launch: the target
    # weapon either wears its icon (that palette is its palette) or wears a flat code colour,
    # and THAT colour names the palette it really uses.
    if "--icon" in sys.argv:
        spec = sys.argv[sys.argv.index("--icon") + 1]
        idx, item = (int(v) for v in spec.split(":"))
        ramp = icon_ramp(item, 15)
        out, painted = paint_palette(out, idx, ramp, raw)
        print(f"painted palette {idx} with item {item}'s icon ramp ({painted} slots, "
              f"dark to light; the other palettes keep their flat code colours)")
        print("  ramp:", " ".join(f"({(v & 31) * 8},{((v >> 5) & 31) * 8},{((v >> 10) & 31) * 8})"
                                  for v in ramp))
    if "--derange" in sys.argv:
        out = derange_pixels(out)
        print("ALSO deranged the pixel block (two variables; only for the follow-up question)")
    open(os.path.join(work_dir, NAME), "wb").write(out)
    preview(work_dir, raw, "vanilla")
    preview(work_dir, out, "forged")
    print(f"bin + previews in {work_dir}")
    if "--deploy" not in sys.argv:
        print("dry run only; rerun with --deploy to install")
        return
    if "fft_enhanced.exe" in subprocess.run(
            ["tasklist", "/FI", "IMAGENAME eq fft_enhanced.exe"],
            capture_output=True, text=True).stdout:
        sys.exit("fft_enhanced.exe is RUNNING; close it, then rerun with --deploy")
    mods = os.environ.get(MODS_ENV) or sys.exit(f"{MODS_ENV} not set")
    dst = os.path.join(mods, DEPLOY_SUB)
    os.makedirs(dst, exist_ok=True)
    shutil.copy2(os.path.join(work_dir, NAME), os.path.join(dst, NAME))
    print(f"deployed {NAME} -> {dst}")
    print("restart, load ANY battle, attack with any weapon, then run --checklog. The log "
          "decides whether a vanilla-looking weapon means 'palette not read' or 'not served'.")


if __name__ == "__main__":
    main()
