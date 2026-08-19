#!/usr/bin/env python
"""LW-251: does the g2d file channel reach the battle-weapon PALETTE bank (entry 156)?

WHAT THIS SHIPS. Entries 161 and 158 are both 512x512 4bpp INDEXED equipment sheets
(161: bows, guns, harps, knives, cloths; 158: swords, shields, helmets). Their colours come
from a bank of 144 sixteen-colour BGR555 palettes in entry 156. The modloader serves a
mod-shipped raw decompressed system/ffto/g2d/tex_N.bin in place of container entry N, at
launch, cached per process. Replacing a SHEET is PROVEN (ledger
[g2d-equipment-sheet-override], owner flip 2026-08-18): it shuffles which of a weapon's own
16 colours each pixel uses. Replacing the PALETTE BANK would be TRUE HUE control, and is
what this probe tests.

THE ROUND 9/10 DESIGN ERROR, and the fix. Those rounds shipped a forged entry 156 AND a
deranged entry 161 together, so a wrong-looking bow had TWO possible authors and neither
reading was clean. Round 11 separates them:
  TEST     entry 156 forged to FLAT per-row colours; the bow's own sheet (161) stays
           VANILLA, so ANY colour change on the bow can only come from the palette bank.
  CONTROL  entry 158 (swords/shields/helmets) deranged, so a SWORD attack in the same
           battle proves the file channel is alive without touching the bow's sheet.
Read it in ONE daylight battle; weapons only render during an attack animation:
  sword scrambled + bow FLAT   -> palette bank consumed: true hue control. --decode names
                                  the bow's palette row from the screenshot.
  sword scrambled + bow normal -> CLEAN NEGATIVE: channel alive, entry 156 not consumed as
                                  a file. Next lever is the full-container ship (the
                                  2026-08-18 round-5 attempt was VACUOUS, it shipped a
                                  byte-identical vanilla container, and that launch's log
                                  shows the loader DOES hook and serve a mod g2d.dat).
  sword normal                 -> dead serve path this launch; conclude NOTHING, relaunch.

FORGE RULES (every output preserves its entry's exact decompressed size):
  tex_156 census mode: per palette row, slot 0 and any 0x0000 colour stay untouched
    (0 = transparent); every other colour keeps bit 15 and takes ONE flat coordinate-coded
    colour naming its row (see flat15). Flat is the tell no index scramble can fake.
  tex_156 --pin R[,R..]: forge ONLY those rows, each a vivid alphabet colour, everything
    else byte-identical vanilla. The surgical mode for a single-weapon recolour.
  tex_N --derange N: sheet indices shifted 0 -> 0, n -> (n % 15) + 1 (the proven control
    class; transparency preserved).

USAGE:
  python lw251_g2d_clut_forge.py <work_dir> [--pin R,R] [--derange N,N] [--deploy]
  python lw251_g2d_clut_forge.py --decode <screenshot.png>   # name the coded rows on screen
  python lw251_g2d_clut_forge.py --selftest                  # pure checks, touches nothing
Round 11 line:  <work_dir> --derange 158 --deploy
--deploy CLEARS the install's g2d folder first, so a previous round's sheet cannot linger.
Undo: delete <mods>/prawl.fft.livingweapons/FFTIVC/data/enhanced/system/ffto/g2d/ (the next
BuildLinked wipes it too, so deploy AFTER any BuildLinked run, never before).
"""
import colorsys
import os
import shutil
import struct
import subprocess
import sys
import zlib

GAME_DATA = (r"c:\program files (x86)\steam\steamapps\common"
             r"\FINAL FANTASY TACTICS - The Ivalice Chronicles\data\enhanced")
# 0007.pac is the BASE container and always holds system/ffto/g2d.dat. modded.pac is the
# modloader's per-launch merge output and holds g2d.dat only while some mod ships a whole
# container: the 2026-08-19 03:26 rebuild dropped it (the pac shrank by exactly 0xCF6120)
# once no mod supplied one, and an extract from it then fails outright. Verified 2026-08-19:
# the two copies' 2450 decompressed entries are IDENTICAL (they differ only in one 48-byte
# version-0 padding record at 0x0A50BD0), so the base pac is the stable, correct source.
SOURCE_PACS = (os.path.join(GAME_DATA, "0007.pac"), os.path.join(GAME_DATA, "modded.pac"))
FF16TOOLS = r"C:\Users\ptyRa\Downloads\FF16Tools.CLI-1.13.2-win-x64\win-x64\FF16Tools.CLI.exe"
G2D_INNER = "system/ffto/g2d.dat"
MODS_ENV = "RELOADEDIIMODS"
DEPLOY_SUB = os.path.join("prawl.fft.livingweapons", "FFTIVC", "data", "enhanced",
                          "system", "ffto", "g2d")
ENTRY_CLUT = 156
SHEET_BYTES = 512 * 512 // 2  # 4bpp
CLUT_BYTES = 144 * 32         # 144 palettes x 16 BGR555 colours

PIN_ALPHABET = [  # max-distinct vivid BGR555, reused cyclically; position disambiguates
    (0x001F, "RED"), (0x03FF, "YELLOW"), (0x03E0, "GREEN"),
    (0x7FE0, "CYAN"), (0x7C00, "BLUE"), (0x7C1F, "MAGENTA"),
]


def extract_g2d(work_dir):
    """Unpack the g2d container via FF16Tools (-g fft; the pacs are encrypted and their
    offsets move on every deploy, so never hardcode them). Tries SOURCE_PACS in order."""
    out = os.path.join(work_dir, "pac_unpack")
    path = os.path.join(out, *G2D_INNER.split("/"))
    for pac in SOURCE_PACS:
        if os.path.isfile(path):
            os.remove(path)
        subprocess.run([FF16TOOLS, "unpack", "-i", pac, "-f", G2D_INNER,
                        "-o", out, "-g", "fft"], capture_output=True)
        if os.path.isfile(path):
            print(f"g2d source: {os.path.basename(pac)}")
            return path
    sys.exit(f"no pac in {SOURCE_PACS} yielded {G2D_INNER}")


def parse_entries(data):
    """YOX container -> {index: (true_offset, payload_len)}. Auto-detects the +16*i table
    drift (the loose Dec-2025 leftover has it, the modded.pac copy does not) by requiring
    every entry's payload to open with the per-entry YOX magic."""
    magic, _, tbl, n = struct.unpack_from("<4sIII", data, 0)
    if magic != b"YOX\x00":
        sys.exit("not a YOX container")
    for drift in (0, 16):
        entries = {}
        for i in range(n):
            off, plen = struct.unpack_from("<II", data, tbl + i * 16)[:2]
            true = off - drift * i
            if plen and data[true:true + 4] != b"YOX\x00":
                entries = None
                break
            entries[i] = (true, plen)
        if entries is not None:
            return entries, n, drift
    sys.exit("neither drift hypothesis lands every entry on a YOX header")


def decompress_entry(data, off, plen):
    m, ver, dec_size, _ = struct.unpack_from("<4sIII", data, off)
    assert m == b"YOX\x00" and ver == 2, (m, ver)
    raw = zlib.decompress(data[off + 16:off + plen])
    assert len(raw) == dec_size, (len(raw), dec_size)
    return raw


def flat15(row, nrows):
    """One flat COORDINATE-CODED low-15 BGR555 colour for palette `row`. Flat (the same
    colour in every slot 1..15) is deliberate: a consumed forge renders items as shadeless
    colour blobs, which no index scramble of a vanilla ramp can imitate. The colour is a
    machine-decodable row code (hue = (row%36)*10 degrees, two value bands by (row//36)%2,
    two saturation bands by row//72; 144 distinct never-zero colours, min RGB pair distance
    8), so a screenshot of any item in battle names its palette row via --decode.
    Confusable near-neighbours share a hue family, so a mis-decode is off by a band, not by
    a random row."""
    h = (row % 36) * 10 / 360.0
    v = (1.0, 0.62)[(row // 36) % 2]
    s = (1.0, 0.5)[min(row, nrows - 1) // 72]
    r, g, b = colorsys.hsv_to_rgb(h, s, v)
    r5, g5, b5 = (max(1, int(c * 31)) for c in (r, g, b))
    return r5 | (g5 << 5) | (b5 << 10)     # low bits = red (scan probe's proven layout)


def code_rgb(row, nrows=144):
    v = flat15(row, nrows)
    return ((v & 0x1F) << 3, ((v >> 5) & 0x1F) << 3, ((v >> 10) & 0x1F) << 3)


def decode_image(path, nrows=144, tol=26, min_pixels=12):
    """Read a battle screenshot and report which coded rows appear. Nearest-code match
    within `tol` RGB distance per pixel; rows under `min_pixels` matched pixels are noise.
    Daylight battles decode far better than night. Returns [(row, pixels, (cx, cy))...].
    TRAP: natural scene colours (hair, grass, sky) land within tol of SOME code, so treat a
    hit as real only when its pixels sit on the weapon; the caller is expected to eyeball
    the region, not to trust the list blind."""
    from PIL import Image
    im = Image.open(path).convert("RGB")
    w, h = im.size
    codes = [code_rgb(r, nrows) for r in range(nrows)]
    hits = {}
    for y in range(h):
        for x in range(w):
            p = im.getpixel((x, y))
            best, bd = None, tol * tol
            for r, c in enumerate(codes):
                d = (p[0] - c[0]) ** 2 + (p[1] - c[1]) ** 2 + (p[2] - c[2]) ** 2
                if d < bd:
                    best, bd = r, d
            if best is not None:
                e = hits.setdefault(best, [0, 0, 0])
                e[0] += 1
                e[1] += x
                e[2] += y
    out = [(r, n, (sx // n, sy // n)) for r, (n, sx, sy) in hits.items() if n >= min_pixels]
    out.sort(key=lambda t: -t[1])
    return out


def forge_clut(raw, pinned=None):
    """pinned=None: code every row (census mode). pinned=iterable of row indices: forge ONLY
    those rows, each a vivid alphabet colour; every other row stays byte-identical vanilla,
    which is the surgical mode for a single-weapon recolour."""
    if len(raw) % 32:
        sys.exit(f"entry {ENTRY_CLUT} size {len(raw)} is not 16-colour rows")
    nrows = len(raw) // 32
    pal = list(struct.unpack(f"<{nrows * 16}H", raw))
    changed = 0
    rows = range(nrows) if pinned is None else sorted(set(pinned))
    for i, row in enumerate(rows):
        colour = flat15(row, nrows) if pinned is None else PIN_ALPHABET[i % 6][0]
        for slot in range(1, 16):
            k = row * 16 + slot
            if pal[k] == 0:
                continue
            pal[k] = (pal[k] & 0x8000) | colour
            changed += 1
    return struct.pack(f"<{nrows * 16}H", *pal), nrows, changed


def parse_list(spec):
    out = []
    for part in spec.split(","):
        if "-" in part:
            a, b = part.split("-")
            out.extend(range(int(a), int(b) + 1))
        else:
            out.append(int(part))
    return out


def _dn(n):
    return 0 if n == 0 else (n % 15) + 1


def derange_sheet(raw):
    lut = bytes((_dn(b & 0xF) | (_dn(b >> 4) << 4)) for b in range(256))
    return raw.translate(lut)


def preview(work_dir, clut_raw, tag):
    from PIL import Image
    nrows = len(clut_raw) // 32
    im = Image.new("RGBA", (16, nrows))
    px = im.load()
    for i in range(nrows * 16):
        v = struct.unpack_from("<H", clut_raw, i * 2)[0]
        px[i % 16, i // 16] = ((v & 0x1F) << 3, ((v >> 5) & 0x1F) << 3,
                               ((v >> 10) & 0x1F) << 3, 0 if v == 0 else 255)
    im.resize((16 * 12, nrows * 4), Image.NEAREST).save(
        os.path.join(work_dir, f"clut_{tag}.png"))


def game_running():
    out = subprocess.run(["tasklist", "/FI", "IMAGENAME eq fft_enhanced.exe"],
                         capture_output=True, text=True).stdout
    return "fft_enhanced.exe" in out


def selftest():
    assert sorted(_dn(n) for n in range(1, 16)) == list(range(1, 16)), "not a permutation"
    assert all(_dn(n) != n for n in range(1, 16)), "some index survives unshifted"
    assert _dn(0) == 0, "transparency broken"
    sheet = bytes(range(256)) * 4
    out = derange_sheet(sheet)
    assert len(out) == len(sheet) and out != sheet
    assert all((a & 0xF == 0) == (b & 0xF == 0) for a, b in zip(sheet, out))
    raw = struct.pack("<32H", *([0, 0x8000, 0x1234, 0x0000] + [0x7FFF] * 12) * 2)
    forged, nrows, changed = forge_clut(raw)
    assert len(forged) == len(raw) and nrows == 2 and changed == 28
    got = struct.unpack("<32H", forged)
    assert got[0] == 0 and got[3] == 0 and got[16] == 0 and got[19] == 0, "zeros must hold"
    assert got[1] & 0x8000 and not got[2] & 0x8000, "top bit not preserved"
    assert all(v & 0x7FFF for v in got if v), "a forged colour collapsed to invisible"
    for row in (0, 1):
        low15 = {v & 0x7FFF for v in got[row * 16:(row + 1) * 16] if v}
        assert len(low15) == 1, "a forged row is not flat"
    assert (got[1] & 0x7FFF) != (got[17] & 0x7FFF), "adjacent rows share a colour"
    pin, _, pin_changed = forge_clut(raw, [1])
    pin_u = struct.unpack("<32H", pin)
    assert pin_u[:16] == struct.unpack("<32H", raw)[:16], "pin mode touched an unpinned row"
    assert pin_changed == 14 and pin_u[17] & 0x7FFF == PIN_ALPHABET[0][0], "pin mode wrong"
    codes = [flat15(r, 144) for r in range(144)]
    assert len(set(codes)) == 144, "row codes are not distinct"
    assert all(1 <= c <= 0x7FFF for c in codes), "a row code is zero or has the top bit"
    rgbs = [code_rgb(r) for r in range(144)]
    for i, p in enumerate(rgbs):
        near = min(range(144), key=lambda j: sum((a - b) ** 2 for a, b in zip(rgbs[j], p)))
        assert near == i, "the code table does not self-decode"
    assert parse_list("158") == [158] and parse_list("1,3-5") == [1, 3, 4, 5]
    print("selftest OK")


def main():
    if "--selftest" in sys.argv:
        selftest()
        return
    if "--decode" in sys.argv:
        for row, n, (cx, cy) in decode_image(sys.argv[sys.argv.index("--decode") + 1]):
            print(f"row {row:3}: {n:5} px  around ({cx},{cy})")
        return
    selftest()
    work_dir = sys.argv[1]
    os.makedirs(work_dir, exist_ok=True)
    data = open(extract_g2d(work_dir), "rb").read()
    entries, n, drift = parse_entries(data)
    print(f"container: {len(data)} bytes, {n} entries, drift {drift}")
    clut = decompress_entry(data, *entries[ENTRY_CLUT])
    if len(clut) != CLUT_BYTES:
        sys.exit(f"entry {ENTRY_CLUT} is {len(clut)} bytes, expected {CLUT_BYTES}; STOP")
    pinned = parse_list(sys.argv[sys.argv.index("--pin") + 1]) if "--pin" in sys.argv else None
    if pinned:
        for i, row in enumerate(sorted(set(pinned))):
            print(f"  pin row {row:3} = {PIN_ALPHABET[i % 6][1]}")
    forged, nrows, changed = forge_clut(clut, pinned)
    ship = {ENTRY_CLUT: forged}
    print(f"TEST  tex_{ENTRY_CLUT}.bin: {changed} colours forged across {nrows} rows")

    # Controls ship whole sheets with their palette INDICES shifted. Round 11 uses 158
    # (swords/shields/helmets) so a sword attack proves the channel is alive in the same
    # battle, while the bow's own sheet (161) stays vanilla and any bow colour change can
    # only come from the palette bank.
    ctrl = parse_list(sys.argv[sys.argv.index("--derange") + 1]) if "--derange" in sys.argv else []
    for idx in ctrl:
        raw = decompress_entry(data, *entries[idx])
        if len(raw) != SHEET_BYTES:
            sys.exit(f"entry {idx} is {len(raw)} bytes, not a {SHEET_BYTES}-byte sheet; STOP")
        ship[idx] = derange_sheet(raw)
        print(f"CTRL  tex_{idx}.bin: sheet indices deranged")

    for idx, blob in ship.items():
        open(os.path.join(work_dir, f"tex_{idx}.bin"), "wb").write(blob)
    preview(work_dir, clut, "vanilla")
    preview(work_dir, forged, "forged")
    print(f"bins + previews in {work_dir}")
    if "--deploy" not in sys.argv:
        print("dry run only; rerun with --deploy to install")
        return
    if game_running():
        sys.exit("fft_enhanced.exe is RUNNING; close it, then rerun with --deploy")
    mods = os.environ.get(MODS_ENV) or sys.exit(f"{MODS_ENV} not set")
    dst = os.path.join(mods, DEPLOY_SUB)
    if os.path.isdir(dst):                 # never leave a previous round's sheet behind
        for stale in os.listdir(dst):
            os.remove(os.path.join(dst, stale))
            print(f"cleared stale {stale}")
    os.makedirs(dst, exist_ok=True)
    for idx in ship:
        name = f"tex_{idx}.bin"
        shutil.copy2(os.path.join(work_dir, name), os.path.join(dst, name))
        print(f"deployed {name} -> {dst}")
    print("restart, then in ONE daylight battle attack with a SWORD unit (control) and the "
          "BOW unit (test). Sword scrambled = channel alive. Bow flat = the palette bank is "
          "consumed (run --decode to name its row). Bow normal while the sword scrambled = "
          "palette bank NOT consumed, a clean negative.")


if __name__ == "__main__":
    main()
