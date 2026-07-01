"""Find every function that loads a reaction-ability id as an immediate.

The reaction dispatch at 0x14030B584 (located 2026-07-01) maps each set bit of a
unit's reaction bitfield (combat +0x94..+0x97) to a HARDCODED reaction-ability id
immediate (0x1a6..0x1c4), e.g. `mov eax, 0x1ba` = Counter. That function turned out
to snapshot the unit to a stack copy and DISCARD its stamped scratch action, returning
only a bool -- i.e. it looks like a PREDICATE ("will this unit react?"), not the live
executor. If a *second* function references the same immediate cluster, that twin is a
candidate for the real reaction executor (the one whose stamp actually casts).

This scans FFT_enhanced.exe for `mov r32, imm32` (opcodes B8..BF, optional REX.B) where
imm32 is in the reaction-id range, then clusters hits by proximity (a real dispatch
loads many of them close together). Prints one line per cluster: [span] count ids.

Usage:  python reaction_id_sites.py [lo_hex hi_hex]   (default 0x1a6 0x1c4)
Env:    FFT_EXE overrides the exe path (same default as ic_disasm.py).
"""
import os
import struct
import sys

EXE = os.environ.get(
    "FFT_EXE",
    r"C:\Program Files (x86)\Steam\steamapps\common"
    r"\FINAL FANTASY TACTICS - The Ivalice Chronicles\FFT_enhanced.exe",
)
PREF = 0x140000000


def sections():
    with open(EXE, "rb") as f:
        d = f.read()
    pe = struct.unpack_from("<I", d, 0x3C)[0]
    nsec = struct.unpack_from("<H", d, pe + 6)[0]
    opt = struct.unpack_from("<H", d, pe + 0x14)[0]
    sec0 = pe + 0x18 + opt
    out = []
    for i in range(nsec):
        s = sec0 + i * 40
        name = d[s:s + 8].rstrip(b"\x00").decode("latin1")
        va = struct.unpack_from("<I", d, s + 12)[0]
        rsize = struct.unpack_from("<I", d, s + 16)[0]
        raw = struct.unpack_from("<I", d, s + 20)[0]
        flags = struct.unpack_from("<I", d, s + 36)[0]
        out.append((name, PREF + va, raw, rsize, flags))
    return d, out


def main():
    lo = int(sys.argv[1], 16) if len(sys.argv) > 2 else 0x1A6
    hi = int(sys.argv[2], 16) if len(sys.argv) > 2 else 0x1C4
    d, secs = sections()
    hits = []  # (va, id, opcode-desc)
    for name, base_va, raw, rsize, flags in secs:
        if not (flags & (0x20 | 0x20000000)) or not rsize:
            continue
        blob = d[raw:raw + rsize]
        n = len(blob)
        for i in range(n - 5):
            b = blob[i]
            # mov eax..edi, imm32 = B8..BF ; with REX.B (41) it's r8d..r15d
            rex_b = False
            j = i
            if b == 0x41 and i + 6 < n and 0xB8 <= blob[i + 1] <= 0xBF:
                rex_b = True
                j = i + 1
                b = blob[j]
            if 0xB8 <= b <= 0xBF:
                imm = struct.unpack_from("<I", blob, j + 1)[0]
                if lo <= imm <= hi:
                    reg = b - 0xB8 + (8 if rex_b else 0)
                    hits.append((base_va + i, imm, reg))
    hits.sort()
    print(f"{len(hits)} mov-imm sites with id in [0x{lo:X},0x{hi:X}]")
    # cluster: new cluster when gap > 0x300 bytes
    clusters = []
    cur = []
    for h in hits:
        if cur and h[0] - cur[-1][0] > 0x300:
            clusters.append(cur)
            cur = []
        cur.append(h)
    if cur:
        clusters.append(cur)
    min_distinct = int(os.environ.get("MIN_DISTINCT", "6"))
    big = [c for c in clusters if len({h[1] for h in c}) >= min_distinct]
    print(f"{len(clusters)} clusters (gap>0x300); "
          f"{len(big)} with >={min_distinct} distinct ids:\n")
    for c in big:
        ids = sorted({h[1] for h in c})
        idstr = " ".join(f"0x{x:X}" for x in ids)
        print(f"  [0x{c[0][0]:X}..0x{c[-1][0]:X}] {len(c):3d} sites, "
              f"{len(ids)} distinct ids: {idstr}")


if __name__ == "__main__":
    main()
