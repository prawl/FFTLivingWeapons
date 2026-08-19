"""LW-251 probe: extract and decode every entry of the game's HD 2D art container.

The container (g2d.dat, magic YOX) holds the battle-art textures the modloader serves as
system/ffto/g2d/tex_N.bin. Format, cracked 2026-08-18 against the live file:
  file header: "YOX\0", u32 0, u32 table_off, u32 entry_count
  entry table at table_off: entry_count x 16 bytes {u32 offset, u32 payload_len, 0, 0}
  THE TRAP: table offsets carry a +16*index drift (true_offset = offset - 16*index);
  verified 1426/1426 by the per-entry sub-header landing exactly there.
  entry payload: "YOX\0", u32 version(2), u32 decompressed_size, u32 0, then a zlib stream.
  decompressed pixels: u16 LE, 5-bit channels (same channel order FFTColorCustomizer's
  decode_tex_to_png.ps1 uses for the served tex_N.bin, proven on Ramza art), value 0 =
  transparent. Width is not stored; 256 fits the known character sheets (131072 = 256x256,
  118784 = 256x232); other sizes get a best-guess width.

Usage: python tools/probes/lw251_g2d_extract.py <out_dir> [--sheets]
Writes tex_N.png per decodable entry plus (with --sheets) labeled contact sheets. Read-only
on the container; output goes wherever <out_dir> says (use the session scratchpad).

FINDINGS (2026-08-18, the LW-251 hunt): the container mixes pixel formats and nothing in
the per-entry header says which is which (the game knows by index). Entries confirmed by
eye: tex_161 = the 512x512 FOUR-BIT INDEXED equipment sheet (bows, crossbows, guns,
shields, cloths, bags, harps, knives; the PSX WEP art at exactly 2x, 16-color CLUT
structure intact, index 0 = transparent, low nibble first); tex_153 = battle UI glyph
sheet (4bpp); tex_152 = chapter title cards (4bpp); tex_160 = crystals and treasure
chests (4bpp); tex_158 = arrow/projectile sheet (direct RGB555); tex_830+/1000+ =
character sheets (direct RGB555, the ones FFTColorCustomizer overrides). The 4608-byte
entries interleaved with the sheets (154-157, 159, 162) decode as 144 rows of 16 u16
colors and are the CLUT-bank suspects (156 is the dense one; structure not yet solved).
The RGB555 decode below renders 4bpp entries as garish doubled noise; that signature IS
how the indexed sheets were found. A mod override for an entry ships the RAW DECOMPRESSED
bytes as system/ffto/g2d/tex_N.bin (proven by FFTColorCustomizer's shipped bins matching
decompressed sizes exactly).
"""
import os
import struct
import sys
import zlib

from PIL import Image, ImageDraw

G2D = (r"c:\program files (x86)\steam\steamapps\common"
       r"\FINAL FANTASY TACTICS - The Ivalice Chronicles"
       r"\FFTIVC\data\enhanced\system\ffto\g2d.dat")

WIDTHS = (256, 512, 128, 64, 32, 16)


def decode_rgb555(raw):
    npx = len(raw) // 2
    w = next((c for c in WIDTHS if npx % c == 0 and 1 <= npx // c <= 4096), None)
    if w is None:
        return None
    h = npx // w
    im = Image.new("RGBA", (w, h))
    px = im.load()
    for i in range(npx):
        v = raw[2 * i] | (raw[2 * i + 1] << 8)
        r = (v & 0x1F) << 3
        g = ((v >> 5) & 0x1F) << 3
        b = ((v >> 10) & 0x1F) << 3
        px[i % w, i // w] = (r, g, b, 0 if v == 0 else 255)
    return im


def main():
    out_dir = sys.argv[1]
    os.makedirs(out_dir, exist_ok=True)
    d = open(G2D, "rb").read()
    magic, _, tbl, n = struct.unpack_from("<4sIII", d, 0)
    assert magic == b"YOX\x00", "not a YOX container"
    decoded, skipped = [], []
    for i in range(n):
        off, plen = struct.unpack_from("<II", d, tbl + i * 16)[:2]
        true = off - 16 * i
        if d[true:true + 4] != b"YOX\x00":
            skipped.append((i, "no header"))
            continue
        dsize = struct.unpack_from("<I", d, true + 8)[0]
        try:
            raw = zlib.decompress(d[true + 16: true + plen])
        except Exception as e:
            skipped.append((i, f"zlib {e}"))
            continue
        if len(raw) != dsize:
            skipped.append((i, f"size {len(raw)} != {dsize}"))
            continue
        im = decode_rgb555(raw)
        if im is None:
            skipped.append((i, f"odd pixel count {len(raw) // 2}"))
            continue
        im.save(os.path.join(out_dir, f"tex_{i}.png"))
        decoded.append(i)
    print(f"decoded {len(decoded)}/{n}; skipped {len(skipped)}")
    for i, why in skipped[:20]:
        print("  skip", i, why)

    if "--sheets" in sys.argv:
        thumb, cols, rows = 72, 12, 10
        per = cols * rows
        for s in range(0, len(decoded), per):
            ids = decoded[s:s + per]
            sheet = Image.new("RGBA", (cols * thumb, rows * (thumb + 12)), (30, 30, 30, 255))
            draw = ImageDraw.Draw(sheet)
            for k, i in enumerate(ids):
                im = Image.open(os.path.join(out_dir, f"tex_{i}.png"))
                im.thumbnail((thumb, thumb))
                x, y = (k % cols) * thumb, (k // cols) * (thumb + 12)
                sheet.paste(im, (x, y))
                draw.text((x + 2, y + thumb), str(i), fill=(255, 255, 0, 255))
            name = os.path.join(out_dir, f"sheet_{s:04d}.png")
            sheet.save(name)
        print("contact sheets written")


if __name__ == "__main__":
    main()
