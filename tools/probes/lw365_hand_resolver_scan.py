#!/usr/bin/env python
"""LW-365 READ-ONLY disk scan: the last id-261 assumptions on the unit-hands read path.

Plain language: a handful of game routines that look up what a unit is holding were built
with the old 261-item limit and treat one of this mod's new ids as "no weapon there", which
is why attacking an EMPTY tile with a new weapon swings fists (the routine falls back to the
empty left hand). This probe reads the game's program file on disk and answers three staged
questions BEFORE any code is written:

  Q1  Do the nine transcribed sites (six hand-resolver copies, three equip-slot switch
      guards) really carry a `mov r32,0xff` .. `lea reg,[base+6]` bound pair ending exactly
      at the transcribed address? CONVENTION (fixed 2026-08-31, v1.1): every address below
      IS the disp8 byte itself -- the same byte ExtendedSites.cs patches -- not some earlier
      reference point, so Q1 searches the 0x30 bytes ENDING at each address for a
      `mov r32,0xff` (b8+r ff 00 00 00, optionally REX-prefixed; the resolvers use
      `ba ff 00 00 00` = mov edx,0xff, the slot guards `41 ba ff 00 00 00` = mov r10d,0xff)
      followed by one of three lea encodings whose disp8 byte lands exactly on the address:
      `8d 4a 06` (lea ecx,[rdx+6], the plain hand-resolver form), `45 8d 41 06`
      (lea r8d,[r9+6], resolver copy 8's variant) or `41 8d 42 06` (lea eax,[r10+6], the
      three slot guards). Success = the pair found ending at the address; failure at any
      site = a transcription error, STOP.
  Q2  What do the three "slot guard" sites at 0x1403066A7 / 0x14030671D / 0x1403067AB and
      the u16 walker bound at 0x1402C8155 actually compare, and which data do they serve?
      (Raw hex context only; classification, not a verdict -- Q1 already proves the bound
      pair exists for the slot guards.)
  Q3  Sweep the whole code section for EVERY `lea r32,[r32+6]` within 0x40 bytes after a
      `mov r32,0xff`, minus the sites the mod already widens: is the nine-copy count
      exhaustive, or did the transcription undercount (the LW-371 lesson)? Each hit is
      reported at the lea's OPCODE (0x8d) byte address; its disp8 byte (the address that
      would actually get patched) is opcode + 2 regardless of an intervening REX prefix,
      because the REX byte sits BEFORE the opcode, not between the opcode and the
      modrm/disp8 pair. Two sites are WALLED, never patched: the u16 walker at 0x1402C8155
      (a stack-buffer bound, LW-371 lesson) and the false positive at 0x1402BD992
      (`44 8d 4e 06`; opcode byte 0x1402BD993, disp8 byte 0x1402BD995 -- rsi is 1 there, so
      the lea computes the equip-slot fill count 7, not a cap). Both are pinned by their
      disp8 byte so the sweep tags them instead of flagging them as untagged copies.

Writes nothing, touches no process. Exit code: 0 only if every Q1 site's pair is found AND
every Q3 hit is tagged (already widened by ExtendedSites, one of the nine LW-365 transcribed
sites, or one of the two walls); 1 otherwise. Usage:
python tools/probes/lw365_hand_resolver_scan.py
"""
import struct
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from lib.paths import STEAM_FFT

EXE = STEAM_FFT / "fft_enhanced.exe"
BASE = 0x140000000

RESOLVERS = [0x1402C4EB7, 0x1402DE6FF, 0x1402FE436, 0x14033B5BB, 0x140378640, 0x14039636B]
SLOT_GUARDS = [0x1403066A7, 0x14030671D, 0x1403067AB]
NINE = RESOLVERS + SLOT_GUARDS
U16_WALKER = 0x1402C8155

# The false positive's disp8 byte (see the docstring): 0x1402BD992 is `44 8d 4e 06`, opcode
# byte 0x1402BD993, disp8 byte 0x1402BD995. Walled alongside the u16 walker, never patched.
FALSE_POSITIVE_DISP = 0x1402BD995
WALLS = {U16_WALKER, FALSE_POSITIVE_DISP}

# The three lea encodings the family uses, keyed by their disp8 byte as the LAST byte.
LEA_ENCODINGS = (b"\x8d\x4a\x06", b"\x45\x8d\x41\x06", b"\x41\x8d\x42\x06")

# disp8 +6 bounds the mod ALREADY widens (ExtendedSites.cs BootSites/PostLoadSites), so the
# Q3 sweep can subtract them. The post-load damage-path site lives at a runtime-relocated
# address (0x14F2EA40F) and never appears in the disk image's plain section.
ALREADY_WIDENED = {0x140284C0A, 0x140226FDF, 0x140286187, 0x140285EE7,
                   0x140285FB5, 0x1402860AE, 0x140396881}


def sections(data):
    pe = struct.unpack_from("<I", data, 0x3C)[0]
    nsec = struct.unpack_from("<H", data, pe + 6)[0]
    opt = struct.unpack_from("<H", data, pe + 20)[0]
    out = []
    for i in range(nsec):
        off = pe + 24 + opt + i * 40
        name = data[off:off + 8].rstrip(b"\0").decode(errors="replace")
        vsize, va, rsize, raw = struct.unpack_from("<IIII", data, off + 8)
        out.append((name, va, vsize, raw, rsize))
    return out


def va_to_file(secs, va):
    rva = va - BASE
    for name, sva, vsize, raw, rsize in secs:
        if sva <= rva < sva + max(vsize, rsize):
            off = rva - sva + raw
            return off if rva - sva < rsize else None
    return None


def hex_dump(data, secs, va, before=0x20, after=0x10, label=""):
    """Raw hex dump around va -- alignment-proof, unlike a disassembler that has to guess an
    instruction boundary. Marks the row(s) containing va itself."""
    off = va_to_file(secs, va)
    if off is None:
        print(f"  {va:#x}: NOT IN DISK IMAGE (runtime-only page?)")
        return
    start = max(0, off - before)
    buf = data[start:off + after]
    base_va = va - (off - start)
    print(f"-- {label} {va:#x} (raw bytes {base_va:#x}..{base_va + len(buf) - 1:#x}) --")
    width = 16
    for row in range(0, len(buf), width):
        row_bytes = buf[row:row + width]
        row_va = base_va + row
        hex_str = " ".join(f"{b:02x}" for b in row_bytes)
        marker = "  <== target" if row_va <= va < row_va + len(row_bytes) else ""
        print(f"  {row_va:#012x}: {hex_str}{marker}")


def find_mov_ff(w, before_idx):
    """Find the start offset (within w) of a `mov r32,0xff` (b8+r ff 00 00 00, optionally
    REX-prefixed) ending strictly before before_idx. Returns -1 if none."""
    j = w.rfind(b"\xff\x00\x00\x00", 0, before_idx)
    while j != -1:
        if j >= 1 and 0xB8 <= w[j - 1] <= 0xBF:
            start = j - 1
            if start >= 1 and w[start - 1] in (0x41, 0x44, 0x45):
                start -= 1
            return start
        j = w.rfind(b"\xff\x00\x00\x00", 0, j)
    return -1


def q1(data, secs):
    print("== Q1: the nine transcribed sites (six hand resolvers + three slot guards) ==")
    ok = True
    for va in NINE:
        off = va_to_file(secs, va)
        if off is None:
            print(f"  !! {va:#x}: NOT IN DISK IMAGE (runtime-only page?)")
            ok = False
            continue
        hex_dump(data, secs, va, before=0x30, after=0x04, label="site")
        window_len = 0x30
        window_start = max(0, off - window_len + 1)
        w = data[window_start:off + 1]   # the window ENDS at (and includes) the disp8 byte
        found = False
        for lea in LEA_ENCODINGS:
            idx = w.rfind(lea)
            if idx >= 0 and window_start + idx + len(lea) - 1 == off:
                mov_start = find_mov_ff(w, idx)
                if mov_start >= 0:
                    print(f"  OK {va:#x}: mov r32,0xff at {window_start + mov_start:#x}, "
                          f"lea ({lea.hex()}) at {window_start + idx:#x}; widen byte = {va:#x}")
                    found = True
                    break
        if not found:
            print(f"  !! {va:#x}: expected mov+lea pair ending here NOT found in the "
                  f"preceding {window_len:#x} bytes")
            ok = False
    print(f"Q1 verdict: {'PASS' if ok else 'FAIL: stop, re-derive the addresses'}\n")
    return ok


def q2(data, secs):
    print("== Q2: slot guards + u16 walker (classification evidence) ==")
    for va in SLOT_GUARDS:
        hex_dump(data, secs, va, before=0x28, after=0x38, label="slot-guard")
    hex_dump(data, secs, U16_WALKER, before=0x30, after=0x40, label="u16-walker")
    print()


def q3(data, secs):
    print("== Q3: exhaustive sweep, every lea disp8 +6 within 0x40 after a mov r32,0xff ==")
    known_off = va_to_file(secs, RESOLVERS[0])
    sec = next(s for s in secs if s[3] <= known_off < s[3] + s[4])
    name, sva, vsize, raw, rsize = sec
    blob = data[raw:raw + rsize]
    movs = []
    i = blob.find(b"\xff\x00\x00\x00")
    while i != -1:
        # mov r32, 0xff imm32 forms: b8+r ff000000 (5), or REX.41/44/45 b8+r (6)
        if i >= 1 and 0xB8 <= blob[i - 1] <= 0xBF:
            movs.append(i - 1 - (1 if i >= 2 and blob[i - 2] in (0x41, 0x44, 0x45) else 0))
        i = blob.find(b"\xff\x00\x00\x00", i + 1)
    hits = []
    for m in movs:
        w = blob[m:m + 0x40]
        j = 0
        while True:
            j = w.find(b"\x8d", j)
            if j < 0:
                break
            if j + 2 < len(w):
                modrm = w[j + 1]
                if (modrm >> 6) == 1 and w[j + 2] == 0x06 and (modrm & 7) != 4:  # disp8=6, no SIB
                    hits.append(m + j)
            j += 1
    found = set()
    for m_plus_j in hits:
        file_off = raw + m_plus_j
        va = next((BASE + s[1] + (file_off - s[3]) for s in secs
                   if s[3] <= file_off < s[3] + s[4]), None)
        if va:
            found.add(va)   # va is the lea's 0x8d opcode byte address

    untagged = []
    for va in sorted(found):
        disp_byte = va + 2   # the disp8 byte, regardless of an intervening REX prefix
        tags = []
        if disp_byte in ALREADY_WIDENED:
            tags.append("already widened by ExtendedSites")
        if disp_byte in RESOLVERS or disp_byte in SLOT_GUARDS:
            tags.append("one of the nine LW-365 transcribed sites")
        if disp_byte in WALLS:
            tags.append("walled, never patched (see ExtendedSites.cs)")
        if tags:
            print(f"  lea opcode {va:#x}, disp8 byte {disp_byte:#x} ({'; '.join(tags)})")
        else:
            print(f"  lea opcode {va:#x}, disp8 byte {disp_byte:#x} *** UNTAGGED ***")
            untagged.append(disp_byte)
    print(f"Q3: {len(found)} candidate lea-after-mov-0xff sites in {name}; "
          f"{len(found) - len(untagged)} tagged, {len(untagged)} untagged. Any UNTAGGED "
          f"line above is a copy the LW-365 table missed.\n")
    return untagged


def main():
    data = EXE.read_bytes()
    secs = sections(data)
    print(f"exe: {EXE} ({len(data):,} bytes)")
    for s in secs:
        print(f"  section {s[0]:<8} va={BASE + s[1]:#x} rawoff={s[3]:#x} rawsize={s[4]:#x}")
    print()
    q1_ok = q1(data, secs)
    q2(data, secs)
    untagged = q3(data, secs)
    if not q1_ok or untagged:
        print("RESULT: FAIL (see !! and *** UNTAGGED *** lines above)")
        sys.exit(1)
    print("RESULT: PASS")


if __name__ == "__main__":
    main()
