#!/usr/bin/env python
"""LW-251 round 9: forge the entry-156 CLUT-bank file override + entry-161 positive control.

PREMISE (falsifiable): shipping the real container's entry 156 (the HD weapon CLUT bank,
per lw251_hd_clut_scan.py's header), forged to FLAT per-row colours, as a modloader g2d
override (FFTIVC/data/enhanced/system/ffto/g2d/tex_156.bin) changes battle weapon colours
after a game restart.

WHY RETRY (corrected 2026-08-19 after adversarial review; an earlier draft blamed the
wrong container, which is FALSE, entries 154-162 are byte-identical between the loose
Dec-2025 leftover and the modded.pac copy except 158): round 2's negative was REAL BUT
UNCONTROLLED. Its launch mapped ONLY the palette entries (154/155/156/157/159/162), with
NO tex_161 positive control in the same frame, and the modloader's "mapping G2D file"
log lines are registration-side, not serve-side, with a known precedent of detours being
dead for a whole launch (memory: denuvo-hook-launch-fragility). So a dead serve path that
launch would read identically to "156 not consumed". This round re-runs the test WITH the
proven tex_161 control in frame. EXPECTED outcome is reading 2 (confirming round 2);
reading 1 would overturn it.

DESIGN: three-way discrimination in one launch, read on an Archer's bow (proven ON
tex_161; the crossbow is NOT on the sheet and proves nothing):
  bow FLAT single-colour       -> entry-156 file override IS consumed: true-hue lever
    (shading gone entirely)       found, portable to the ColorCustomizer slider. Flat is
                                  the tell an index scramble cannot fake: any derangement
                                  of a vanilla ramp still shows multiple shades.
  bow scrambled-but-shaded     -> control fired, 156 NOT consumed via the tex-file
    (round 1's deep blue look)    channel (that precise claim, nothing stronger). Next
                                  probe = ship the FULL g2d.dat with a genuinely forged
                                  entry 156: the 2026-08-18 round-5 full-container test
                                  was VACUOUS (the shipped file was byte-identical
                                  vanilla), and that launch's own Reloaded log shows the
                                  modloader DID hook and serve a mod g2d.dat, so the
                                  bulk-load route exists and is untested.
  bow fully vanilla            -> serve path dead this launch; do NOT conclude anything,
                                  check the log (below), relaunch and retry.
INSTRUMENTED READ: after the launch, grep the newest Reloaded-II launcher log for the
"mapping G2D file" lines for 156 and 161. Lines present + vanilla bow = dead serve path
this launch (registration fired, serving did not). Lines absent = deploy bug.

FORGE RULES (both preserve exact decompressed size):
  tex_156: per 16-colour palette row, colour 0 and any 0x0000 slot stay untouched
    (0 = transparent); every other colour keeps its top bit (unknown semantics, possibly
    the PSX STP/alpha bit) and gets ONE FLAT saturated colour shared by the whole row,
    hue spread across the bank by row so different weapons wearing different palettes go
    visibly DIFFERENT flat colours.
  tex_161: nibble derangement, 0 -> 0, n -> (n % 15) + 1 (round 1's proven control class:
    every visible pixel's index shifts, transparency preserved).

USAGE:
  python lw251_g2d_clut_forge.py <work_dir>            # extract + verify + forge + previews
  python lw251_g2d_clut_forge.py <work_dir> --deploy   # ...then copy both bins into the
                                                       #    livingweapons install (refuses
                                                       #    if the game is running)
  python lw251_g2d_clut_forge.py --selftest            # pure checks, no game files touched
Undo: delete <mods>/prawl.fft.livingweapons/FFTIVC/data/enhanced/system/ffto/g2d/
(the next BuildLinked deploy also wipes it; the folder is not in the mod's manifest, so
deploy the bins AFTER any BuildLinked run, never before).
"""
import os
import shutil
import struct
import subprocess
import sys
import zlib

MODDED_PAC = (r"c:\program files (x86)\steam\steamapps\common"
              r"\FINAL FANTASY TACTICS - The Ivalice Chronicles"
              r"\data\enhanced\modded.pac")
FF16TOOLS = r"C:\Users\ptyRa\Downloads\FF16Tools.CLI-1.13.2-win-x64\win-x64\FF16Tools.CLI.exe"
G2D_INNER = "system/ffto/g2d.dat"
MODS_ENV = "RELOADEDIIMODS"
DEPLOY_SUB = os.path.join("prawl.fft.livingweapons", "FFTIVC", "data", "enhanced",
                          "system", "ffto", "g2d")
ENTRY_CLUT, ENTRY_SHEET = 156, 161
SHEET_BYTES = 512 * 512 // 2  # 4bpp


def extract_g2d(work_dir):
    """Unpack the g2d container out of modded.pac via FF16Tools (-g fft; base pacs are
    encrypted, and modded.pac's offsets move every icon deploy, so never hardcode them)."""
    out = os.path.join(work_dir, "pac_unpack")
    subprocess.run([FF16TOOLS, "unpack", "-i", MODDED_PAC, "-f", G2D_INNER,
                    "-o", out, "-g", "fft"], check=True, capture_output=True)
    path = os.path.join(out, *G2D_INNER.split("/"))
    if not os.path.isfile(path):
        sys.exit(f"unpack produced no {path}")
    return path


def parse_entries(data):
    """YOX container -> {index: (true_offset, payload_len)}. Auto-detects the +16*i table
    drift (the loose Dec-2025 file has it, the modded.pac copy does not) by requiring every
    entry's payload to open with the per-entry YOX magic."""
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
    """One flat COORDINATE-CODED low-15 BGR555 colour for palette `row`. Flat (same colour
    in every slot 1..15) is deliberate: a consumed forge renders items as shadeless colour
    blobs, which no index scramble of a vanilla ramp can imitate. Round 10 upgrade: the
    colour is a machine-decodable row code (hue = (row%36)*10 degrees, two value bands by
    (row//36)%2, two saturation bands by row//72; 144 distinct never-zero colours, min RGB
    pair distance 8), so a screenshot of ANY item in battle names its palette row via
    --decode. Confusable near-neighbours share a hue family, so a mis-decode is off by a
    band, not off by a random row."""
    import colorsys
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
    Daylight battles decode far better than night (night haze shifted a green flat ~40
    degrees blue in the round-9 read). Returns [(row, pixel_count, (cx, cy))...]."""
    from PIL import Image
    import math
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
                e[0] += 1; e[1] += x; e[2] += y
    out = [(r, n, (sx // n, sy // n)) for r, (n, sx, sy) in hits.items() if n >= min_pixels]
    out.sort(key=lambda t: -t[1])
    return out


def forge_clut(raw):
    if len(raw) % 32:
        sys.exit(f"entry {ENTRY_CLUT} size {len(raw)} is not 16-colour rows")
    nrows = len(raw) // 32
    pal = list(struct.unpack(f"<{nrows * 16}H", raw))
    changed = 0
    for row in range(nrows):
        for slot in range(1, 16):
            k = row * 16 + slot
            if pal[k] == 0:
                continue
            pal[k] = (pal[k] & 0x8000) | flat15(row, nrows)
            changed += 1
    return struct.pack(f"<{nrows * 16}H", *pal), nrows, changed


def derange_sheet(raw):
    lut = bytes((_dn(b & 0xF) | (_dn(b >> 4) << 4)) for b in range(256))
    return raw.translate(lut)


def _dn(n):
    return 0 if n == 0 else (n % 15) + 1


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
    codes = [flat15(r, 144) for r in range(144)]
    assert len(set(codes)) == 144, "row codes are not distinct"
    assert all(1 <= c <= 0x7FFF for c in codes), "a row code is zero or has the top bit"
    rgbs = [code_rgb(r) for r in range(144)]
    for i, p in enumerate(rgbs):
        near = min(range(144), key=lambda j: sum((a - b) ** 2 for a, b in zip(rgbs[j], p)))
        assert near == i, "the code table does not self-decode"
    print("selftest OK")


def main():
    if "--selftest" in sys.argv:
        selftest()
        return
    if "--decode" in sys.argv:
        shot = sys.argv[sys.argv.index("--decode") + 1]
        for row, n, (cx, cy) in decode_image(shot):
            print(f"row {row:3}: {n:5} px  around ({cx},{cy})")
        return
    selftest()
    work_dir = sys.argv[1]
    os.makedirs(work_dir, exist_ok=True)
    data = open(extract_g2d(work_dir), "rb").read()
    entries, n, drift = parse_entries(data)
    print(f"container: {len(data)} bytes, {n} entries, drift {drift}")
    clut = decompress_entry(data, *entries[ENTRY_CLUT])
    sheet = decompress_entry(data, *entries[ENTRY_SHEET])
    print(f"entry {ENTRY_CLUT}: {len(clut)} bytes ({len(clut) // 32} palette rows), "
          f"entry {ENTRY_SHEET}: {len(sheet)} bytes (expect {SHEET_BYTES})")
    if len(sheet) != SHEET_BYTES:
        sys.exit("entry 161 is not the 512x512 4bpp sheet; container layout changed, STOP")
    forged, nrows, changed = forge_clut(clut)
    deranged = derange_sheet(sheet)
    for name, blob in ((f"tex_{ENTRY_CLUT}.bin", forged), (f"tex_{ENTRY_SHEET}.bin", deranged)):
        open(os.path.join(work_dir, name), "wb").write(blob)
    preview(work_dir, clut, "vanilla")
    preview(work_dir, forged, "forged")
    print(f"forged {changed} colours across {nrows} rows; bins + previews in {work_dir}")
    if "--deploy" not in sys.argv:
        print("dry run only; rerun with --deploy to install")
        return
    if game_running():
        sys.exit("fft_enhanced.exe is RUNNING; close it, then rerun with --deploy")
    mods = os.environ.get(MODS_ENV) or sys.exit(f"{MODS_ENV} not set")
    dst = os.path.join(mods, DEPLOY_SUB)
    os.makedirs(dst, exist_ok=True)
    for name in (f"tex_{ENTRY_CLUT}.bin", f"tex_{ENTRY_SHEET}.bin"):
        shutil.copy2(os.path.join(work_dir, name), os.path.join(dst, name))
        print(f"deployed {name} -> {dst}")
    print("restart the game, enter a battle with an Archer, read the bow "
          "(flat = 156 consumed; scrambled-but-shaded = 156 dead via this channel; "
          "vanilla = serve path dead, check the Reloaded log and retry)")


if __name__ == "__main__":
    main()
