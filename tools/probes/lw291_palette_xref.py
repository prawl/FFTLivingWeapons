#!/usr/bin/env python
"""
LW-291 PROBE: find every instruction that references the resident weapon-palette working copy.

WHY THIS IS NOW POSSIBLE. [resident-weapon-palette-buffer] (2026-08-21) located the 16 weapon
palettes at four addresses, two of them INSIDE the game image: 0x140d35750 and 0x140d35950. The
image base is fixed at 0x140000000 with no ASLR, so those are stable image-relative addresses and
not per-launch heap noise. x64 reaches static data through RIP-relative displacements, so any code
that reads or writes the palette encodes `target - rip_after_instruction` as a 4-byte little-endian
field inside a code section. Scanning for that displacement finds the code sites directly.

That is a much better handle than what came before. The standing conclusion
([weapon-palette-assignment-walled]) was that palette ASSIGNMENT "is resolved once at startup into
a render-side structure nobody has found", and reopening it needed a hook on the weapon draw path.
Nobody had a concrete address to hang a search on. Now there is one, and code that touches the
palette is by definition on or adjacent to that path.

SECTION NAMES ARE MANGLED. This binary is Denuvo-protected and has NO .text. Executable code lives
in .code (about 6.4 MB) plus .xcode / .ecode / .xtext; the palette statics themselves land in
.xpdata, which is data despite the name. A probe that looks for .text finds nothing and reports a
false dead end, which is exactly what happened on the first run.

WHAT A HIT MEANS, and the honest limits:
  - A hit is a 4-byte field whose value, read as a RIP-relative displacement, resolves to the
    palette. It is NOT proof that it is an operand, nor that the instruction is the draw call, the
    upload, or the per-weapon selection. It is a place for a human to look.
  - This does not decode instructions, so it cannot know where an instruction ends. It tries a few
    plausible tail lengths, which trades false positives for not missing real sites.
  - Sites print image-relative (addr - 0x140000000) as well as absolute, because the relative form
    is what survives a game patch and belongs in a re-anchor note.
  - A clean MISS is informative too: a heap-sourced buffer is usually reached through a POINTER in
    a register or struct field, which leaves no displacement to find. The probe says so rather than
    implying the path does not exist.

READ-ONLY. No writes, no hooks installed.
"""
import collections
import pathlib
import struct
import sys

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import battle_cheats as bc

IMAGE_BASE = 0x140000000
PAL_STATIC = [0x140d35750, 0x140d35950]
CODE_SECTIONS = (".code", ".xcode", ".ecode", ".xtext")
TAILS = (0, 1, 2, 4)


def sections():
    hdr = bc.rpm(IMAGE_BASE, 0x1000)
    if not hdr or hdr[:2] != b"MZ":
        sys.exit("could not read the PE header at the image base")
    pe = struct.unpack_from("<I", hdr, 0x3C)[0]
    if hdr[pe:pe + 4] != b"PE\0\0":
        sys.exit("no PE signature")
    nsec = struct.unpack_from("<H", hdr, pe + 6)[0]
    opt = struct.unpack_from("<H", hdr, pe + 20)[0]
    base = pe + 24 + opt
    out = []
    for i in range(nsec):
        o = base + i * 40
        name = hdr[o:o + 8].rstrip(b"\0").decode("latin1")
        vsize, vaddr = struct.unpack_from("<II", hdr, o + 8)
        if name in CODE_SECTIONS and vsize:
            out.append((name, IMAGE_BASE + vaddr, vsize))
    if not out:
        sys.exit("no executable section found (section names may have changed)")
    return out


def read_all(base, size):
    parts, off = [], 0
    while off < size:
        n = min(0x400000, size - off)
        c = bc.rpm(base + off, n)
        parts.append(c if c else bytes(n))
        off += n
    return b"".join(parts)[:size]


def main():
    bc._require_game()
    secs = sections()
    print("executable sections:")
    for n, b, z in secs:
        print(f"  {n:8s} {b:#x} size {z:#x} ({z / 1e6:.1f} MB)")

    targets = {}
    for b in PAL_STATIC:
        targets[b] = f"block@{b:#x}"
        for p in range(16):
            targets[b + p * 32] = f"pal{p:>2}@{b:#x}"

    hits = collections.defaultdict(list)
    for name, base, size in secs:
        blob = read_all(base, size)
        print(f"\nscanning {name} ({len(blob):#x} bytes)...")
        for i in range(len(blob) - 4):
            disp = struct.unpack_from("<i", blob, i)[0]
            if disp == 0:
                continue
            for tail in TAILS:
                tgt = base + i + 4 + tail + disp
                if tgt in targets:
                    hits[targets[tgt]].append((base + i, tail, name))
                    break

    total = sum(len(v) for v in hits.values())
    if not total:
        print("\nNO references found in any code section.")
        print("That is a real result, not a failure: a buffer the game filled from a file is")
        print("normally reached through a POINTER held in a register or a struct field, which")
        print("leaves no displacement to scan for. Next cheapest step is to scan the DATA")
        print("sections for an 8-byte value equal to one of these addresses, which is how such")
        print("a pointer would be stored, and then find the code that reads THAT.")
        return
    print(f"\n{total} candidate reference(s) across {len(hits)} target(s):\n")
    for label, sites in sorted(hits.items()):
        print(f"  {label}: {len(sites)} site(s)")
        for addr, tail, sn in sites[:5]:
            ctx = bc.rpm(max(addr - 8, IMAGE_BASE), 20) or b""
            print(f"     operand@{addr:#x} [{sn}] img+{addr - IMAGE_BASE:#x} tail={tail}")
            print(f"        bytes around: {ctx.hex(' ')}")


main()
