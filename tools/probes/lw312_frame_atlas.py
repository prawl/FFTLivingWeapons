#!/usr/bin/env python
"""LW-312: render EVERY frame of battle_wep1/2_shp.bin as labeled contact sheets.

WHY. The four ex-flails draw garbage chunks when swinging, and both cheap fixes died live
with serve proof (zero_frames edits at type 9 and graphic 11; loader log said modded file
63/64 while the garbage persisted). The remaster's frame selection does not follow the
community PSX model, so the next instrument is offline: identify WHICH frames the garbage
chunks actually are (a golden plume and a pale sliver, tools/probes/lw251_warbrand_probe2.png),
and reverse the real selection arithmetic from their indices. This script renders the full
frame set so those chunks can be found by eye; no game time needed.

FORMAT (from TacticsTemplateG's Shp.gd, cross-checked against ffhacktics SHP notes; the same
reference lw251_wep_shape_probe.py's header parser was checked against):
  section1 (WEP): u32 swim pointer + 32 u16 zero frames = 0x44 bytes
  section2: u32 frame pointers, terminated by the first zero pointer after index 0
  frames  : at section1+section2+2 + pointer; 1 byte (subframes-1 in bits 0-2, rotation in
            bits 3-7) + 1 byte (transparency) + 4 bytes per subframe:
            s8 dx, s8 dy, u16 packed (bits 0-4 tile x, 5-9 tile y, 10-13 size index,
            14 flip x, 15 flip y), tiles are 8 px, sizes from the battle.bin table below.
  pixels  : battle_wep_spr.bin page1 @0x200 (256 px wide, 4bpp low-nibble-first, rows 0-255),
            palA @0x0 (16 palettes x 16 BGR555, colour 0 transparent). wep2 frames are tried
            against page1 first and page2 @0x8400 second; the sheet that renders coherent art
            names the page (recorded in the output filename).

USAGE:
  python lw312_frame_atlas.py render [outdir]   # writes lw312_wep1_pN.png sheets + a json index
"""
import json
import pathlib
import struct
import sys
import tempfile

HERE = pathlib.Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))
import lw251_wep_spr_forge as fg

# Expected pristine sizes: the two SHPs match the PSX originals byte for byte
# (lw251_wep_shape_probe.py's own FILES table) and the spr is the proven 85504-byte
# three-(palette,page) layout ([wep-spr-palette-block]).
EXPECT = {"battle_wep1_shp.bin": 5218, "battle_wep2_shp.bin": 5436, "battle_wep_spr.bin": 85504}


def extract(name, wd):
    import subprocess
    inner = f"fftpack/unit/{name}"
    out = pathlib.Path(wd, "unpack")
    subprocess.run([fg.FF16TOOLS, "unpack", "-i", fg.SOURCE_PAC, "-f", inner, "-o", str(out),
                    "-g", "fft"], capture_output=True)
    p = pathlib.Path(out, *inner.split("/"))
    raw = p.read_bytes()
    if len(raw) != EXPECT[name]:
        sys.exit(f"{name} is {len(raw)} bytes, expected {EXPECT[name]}; format changed, STOP")
    return raw

# battle.bin's 16 subframe rect sizes (w, h), banked from the previous session's decode
# (shp_sizes.json, matching RomReader.battle_bin_data.shp_subframe_sizes).
SIZES = [(8, 8), (16, 8), (16, 16), (16, 24), (24, 8), (24, 16), (24, 24), (32, 8),
         (32, 16), (32, 24), (32, 32), (32, 40), (40, 16), (40, 32), (48, 48), (56, 56)]

SECTION1 = 0x44
PAGES = {1: 0x200, 2: 0x8400}
SHEET_W = 256


def parse_frames(data):
    ptrs = []
    off = SECTION1
    while off + 4 <= len(data):
        p = struct.unpack_from("<I", data, off)[0]
        if ptrs and p == 0:
            break
        ptrs.append(p)
        off += 4
    base = SECTION1 + (len(ptrs) + 1) * 4 + 2   # +1 for the zero terminator, +2 per Shp.gd
    frames = []
    for p in ptrs:
        fo = base + p
        if fo + 2 > len(data):
            frames.append(None)
            continue
        n = 1 + (data[fo] & 0x07)
        subs = []
        ok = True
        for i in range(n):
            so = fo + 2 + i * 4
            if so + 4 > len(data):
                ok = False
                break
            dx = struct.unpack_from("<b", data, so)[0]
            dy = struct.unpack_from("<b", data, so + 1)[0]
            b = struct.unpack_from("<H", data, so + 2)[0]
            subs.append((dx, dy, (b & 0x1F) * 8, ((b >> 5) & 0x1F) * 8,
                         SIZES[(b >> 10) & 0x0F], bool(b & 0x4000), bool(b & 0x8000)))
        frames.append(subs if ok else None)
    return frames


def load_sheet(spr, page):
    """page pixels as a row-major list of 4-bit indices, 256 wide."""
    start = PAGES[page]
    rows = 256 if page == 1 else 256
    px = []
    for y in range(rows):
        row = []
        for xb in range(SHEET_W // 2):
            b = spr[start + y * (SHEET_W // 2) + xb]
            row.append(b & 0x0F)
            row.append(b >> 4)
        px.append(row)
    return px


def load_palette(spr, which=0):
    out = []
    for i in range(16):
        v = struct.unpack_from("<H", spr, which * 32 + i * 2)[0]
        out.append(((v & 0x1F) << 3, ((v >> 5) & 0x1F) << 3, ((v >> 10) & 0x1F) << 3))
    return out


def render(outdir):
    from PIL import Image, ImageDraw
    out = pathlib.Path(outdir)
    out.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory() as wd:
        spr = extract("battle_wep_spr.bin", wd)
        shp1 = extract("battle_wep1_shp.bin", wd)
        shp2 = extract("battle_wep2_shp.bin", wd)

    pal = load_palette(spr, 0)
    index = {}
    for name, blob, page in (("wep1", shp1, 1), ("wep2", shp2, 1), ("wep2page2", shp2, 2)):
        sheet = load_sheet(spr, page)
        frames = parse_frames(blob)
        index[name] = len(frames)
        COLS, CELL, SCALE, PER = 10, 68, 2, 100
        for chunk in range(0, len(frames), PER):
            batch = frames[chunk:chunk + PER]
            rows = (len(batch) + COLS - 1) // COLS
            im = Image.new("RGB", (COLS * CELL * SCALE, rows * (CELL * SCALE + 12)), (24, 24, 28))
            dr = ImageDraw.Draw(im)
            for k, subs in enumerate(batch):
                cx = (k % COLS) * CELL * SCALE
                cy = (k // COLS) * (CELL * SCALE + 12)
                dr.text((cx + 2, cy), f"{chunk + k}", fill=(160, 160, 160))
                if not subs:
                    continue
                cell = Image.new("RGB", (CELL, CELL), (24, 24, 28))
                for dx, dy, sx, sy, (w, h), fx, fy in reversed(subs):
                    for yy in range(h):
                        for xx in range(w):
                            gx, gy = sx + xx, sy + yy
                            if gy >= len(sheet) or gx >= SHEET_W:
                                continue
                            c = sheet[gy][gx]
                            if c == 0:
                                continue
                            px = CELL // 2 + dx + (w - 1 - xx if fx else xx)
                            py = CELL // 2 + dy + (h - 1 - yy if fy else yy)
                            if 0 <= px < CELL and 0 <= py < CELL:
                                cell.putpixel((px, py), pal[c])
                im.paste(cell.resize((CELL * SCALE, CELL * SCALE), Image.NEAREST), (cx, cy + 12))
            im.save(out / f"lw312_{name}_p{chunk // PER}.png")
    (out / "lw312_frame_counts.json").write_text(json.dumps(index, indent=1))
    print(json.dumps(index))
    print(f"sheets -> {out}")


if __name__ == "__main__":
    render(sys.argv[2] if len(sys.argv) > 2 else (sys.argv[1] if len(sys.argv) > 1 else "."))
