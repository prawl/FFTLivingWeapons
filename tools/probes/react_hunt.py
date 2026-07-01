#!/usr/bin/env python
"""Find switch/jump tables in FFT_enhanced.exe -- runs of consecutive code pointers whose
targets CLUSTER inside one function (span < max_span). The PSX reaction dispatch ("Perform
Reaction Abilities") is a ~25-case jump table; MSVC compiles that to such a table. This
surfaces every clustered jump table so we can spot the ~20-30 entry ones = reaction/ability
dispatchers. Reuses ic_disasm's on-disk PE parse. READ-ONLY, no game needed, no Denuvo risk.

Two encodings scanned:
  abs8  -- 8-byte absolute VA pointers into .edata
  rel4  -- 4-byte MSVC switch offsets, target = table_VA + entry (table-relative)

Usage:
  python react_hunt.py                 # min_run=12, max_span=0x8000, show 60
  python react_hunt.py 18 0x4000 80    # min_run, max_span, show N
"""
import sys
import numpy as np

sys.path.insert(0, r"c:\Users\ptyRa\Dev\FFTMultiplayer\tools\probes")
import ic_disasm as ic


def edata_range():
    for name, lo, vsize, raw, rsize, flags in ic.sections():
        if name == ".edata":
            return lo, lo + vsize
    raise RuntimeError("no .edata section")


def find_runs(mask, min_run):
    """(start_idx, length) for each maximal run of consecutive True of length >= min_run."""
    idx = np.flatnonzero(mask)
    if idx.size == 0:
        return []
    splits = np.flatnonzero(np.diff(idx) != 1)
    starts = np.concatenate(([0], splits + 1))
    ends = np.concatenate((splits, [idx.size - 1]))
    out = []
    for s, e in zip(starts, ends):
        length = int(e - s + 1)
        if length >= min_run:
            out.append((int(idx[s]), length))
    return out


def scan(min_run, max_span):
    lo_e, hi_e = edata_range()
    d = ic.data()
    cands = []
    for name, lo, vsize, raw, rsize, flags in ic.sections():
        if name not in (".rodata", ".edata"):
            continue
        blob = d[raw:raw + rsize]
        # --- abs8: 8-byte absolute pointers into .edata ---
        for align in range(8):
            trim = blob[align:]
            n = len(trim) // 8
            if n < min_run:
                continue
            vals = np.frombuffer(trim[:n * 8], dtype="<u8")
            mask = (vals >= lo_e) & (vals < hi_e)
            for start, length in find_runs(mask, min_run):
                tv = vals[start:start + length]
                span = int(tv.max() - tv.min())
                if span < max_span:
                    cands.append(("abs8", name, lo + align + start * 8, length,
                                  int(tv.min()), int(tv.max()), span))
        # --- rel4: MSVC table-relative 4-byte offsets, target = table_VA + entry ---
        for align in range(4):
            trim = blob[align:]
            n = len(trim) // 4
            if n < min_run:
                continue
            vals = np.frombuffer(trim[:n * 4], dtype="<u4").astype(np.int64)
            pos = lo + align + np.arange(n, dtype=np.int64) * 4
            tgt = pos + vals
            mask = (tgt >= lo_e) & (tgt < hi_e)
            for start, length in find_runs(mask, min_run):
                tv = tgt[start:start + length]
                span = int(tv.max() - tv.min())
                if span < max_span:
                    cands.append(("rel4", name, lo + align + start * 4, length,
                                  int(tv.min()), int(tv.max()), span))
    return cands


def score(min_run, max_span, show):
    """Rank candidate tables by reaction-handler fingerprints: byte stores of the
    PSX magic constants 0x81 (+1 stat), 0x83 (+3 stat/brave/faith), 0xFF (CT->100).
    A real reaction dispatcher's handlers are stuffed with these."""
    from capstone import CS_ARCH_X86, CS_MODE_64, Cs
    from capstone.x86 import X86_OP_IMM, X86_OP_MEM
    md = Cs(CS_ARCH_X86, CS_MODE_64)
    md.detail = True
    scored = []
    for kind, sec, tva, n, tlo, thi, span in scan(min_run, max_span):
        code = ic.read(tlo, thi - tlo + 48)
        if not code:
            continue
        c81 = c83 = cff = 0
        for ins in md.disasm(code, tlo):
            if ins.mnemonic == "mov" and len(ins.operands) == 2:
                dst, src = ins.operands
                if dst.type == X86_OP_MEM and dst.size == 1 and src.type == X86_OP_IMM:
                    v = src.imm & 0xFF
                    c81 += v == 0x81
                    c83 += v == 0x83
                    cff += v == 0xFF
        sc = 3 * c81 + 3 * c83 + cff
        if sc:
            scored.append((sc, c81, c83, cff, kind, sec, tva, n, tlo, thi))
    scored.sort(reverse=True)
    print(f"top reaction-fingerprint scores (0x81/0x83 x3 + 0xFF):\n")
    print(f"{'score':>5} {'x81':>4} {'x83':>4} {'xFF':>4} {'kind':5} "
          f"{'table_VA':>14} {'n':>3} {'handlers@':>13}")
    for sc, c81, c83, cff, kind, sec, tva, n, tlo, thi in scored[:show]:
        print(f"{sc:>5} {c81:>4} {c83:>4} {cff:>4} {kind:5} "
              f"{tva:#014x} {n:>3} {tlo:#013x}")


def _find_bytes(blob, needle):
    a = np.frombuffer(blob, dtype=np.uint8)
    n = len(needle)
    if a.size < n:
        return np.array([], dtype=np.int64)
    m = a[: a.size - n + 1] == needle[0]
    for k in range(1, n):
        m &= a[k : a.size - n + 1 + k] == needle[k]
    return np.flatnonzero(m)


def field(disp, want_size, show):
    """Find [reg+disp] memory accesses of operand size want_size (0=any) in .edata.
    The IC reaction bitfield is a 4-byte field at combat+0x94 -- read by the dispatch,
    written by cripple/grant. This is an IC-native fingerprint (no PSX assumptions)."""
    import struct as _st

    from capstone import CS_ARCH_X86, CS_MODE_64, Cs
    from capstone.x86 import X86_OP_MEM
    md = Cs(CS_ARCH_X86, CS_MODE_64)
    md.detail = True
    d = ic.data()
    needle = _st.pack("<i", disp)
    seen = set()
    hits = []
    for name, lo, vsize, raw, rsize, flags in ic.sections():
        if name != ".edata":
            continue
        blob = d[raw : raw + rsize]
        for i in _find_bytes(blob, needle).tolist():
            for start in range(max(0, i - 15), i):
                ins = next(md.disasm(blob[start : i + 12], lo + start), None)
                if ins is None or start + ins.size <= i:
                    continue
                for op in ins.operands:
                    if op.type == X86_OP_MEM and op.mem.disp == disp and op.mem.base != 0:
                        if want_size and op.size != want_size:
                            continue
                        va = lo + start
                        if va not in seen:
                            seen.add(va)
                            hits.append((va, op.size, ins.mnemonic, ins.op_str))
                        break
                else:
                    continue
                break
    hits.sort()
    print(f"{len(hits)} [reg+{disp:#x}] accesses"
          f"{f' of size {want_size}' if want_size else ''} in .edata\n")
    for va, sz, mn, ops in hits[:show]:
        print(f"  {va:#014x}  (sz{sz})  {mn} {ops}")
    return hits


# Proven-live IC combat-struct offsets (see memories: brave/faith current, HP/MP,
# extra-turn CT, poison/doom status). A reaction dispatcher's handlers touch an
# unusually BROAD set of these -- that breadth is the fingerprint, no PSX assumptions.
KNOWN_OFFS = {0x2B: "brave", 0x2D: "faith", 0x30: "hp", 0x32: "maxhp",
              0x34: "mp", 0x36: "maxmp", 0x41: "ct",
              0x45: "st0", 0x46: "st1", 0x47: "st2", 0x48: "st3", 0x49: "st4"}


def breadth(min_run, max_span, show):
    from capstone import CS_ARCH_X86, CS_MODE_64, Cs
    from capstone.x86 import X86_OP_MEM
    md = Cs(CS_ARCH_X86, CS_MODE_64)
    md.detail = True
    scored = []
    for kind, sec, tva, n, tlo, thi, span in scan(min_run, max_span):
        code = ic.read(tlo, thi - tlo + 64)
        if not code:
            continue
        offs = set()
        for ins in md.disasm(code, tlo):
            for op in ins.operands:
                if op.type == X86_OP_MEM and op.mem.base != 0 and op.mem.disp in KNOWN_OFFS:
                    offs.add(op.mem.disp)
        if offs:
            scored.append((len(offs), sorted(offs), kind, tva, n, tlo))
    scored.sort(key=lambda x: -x[0])
    print(f"candidates by DISTINCT known-offset touches (max {len(KNOWN_OFFS)}):\n")
    for cnt, offs, kind, tva, n, tlo in scored[:show]:
        names = ",".join(KNOWN_OFFS[o] for o in offs)
        print(f"  breadth={cnt:2d}  {kind} tbl {tva:#014x} n={n:3d} "
              f"handlers@{tlo:#013x}  [{names}]")


def main():
    if len(sys.argv) > 1 and sys.argv[1] == "breadth":
        mr = int(sys.argv[2]) if len(sys.argv) > 2 else 8
        ms = int(sys.argv[3], 0) if len(sys.argv) > 3 else 0x8000
        sh = int(sys.argv[4]) if len(sys.argv) > 4 else 30
        breadth(mr, ms, sh)
        return
    if len(sys.argv) > 1 and sys.argv[1] == "field":
        disp = int(sys.argv[2], 0)
        want = int(sys.argv[3]) if len(sys.argv) > 3 else 4
        sh = int(sys.argv[4]) if len(sys.argv) > 4 else 60
        field(disp, want, sh)
        return
    if len(sys.argv) > 1 and sys.argv[1] == "score":
        mr = int(sys.argv[2]) if len(sys.argv) > 2 else 12
        ms = int(sys.argv[3], 0) if len(sys.argv) > 3 else 0x8000
        sh = int(sys.argv[4]) if len(sys.argv) > 4 else 30
        score(mr, ms, sh)
        return
    min_run = int(sys.argv[1]) if len(sys.argv) > 1 else 12
    max_span = int(sys.argv[2], 0) if len(sys.argv) > 2 else 0x8000
    show = int(sys.argv[3]) if len(sys.argv) > 3 else 60
    cands = scan(min_run, max_span)
    # closest to a 25-case table first, then tightest cluster
    cands.sort(key=lambda c: (abs(c[3] - 25), c[6]))
    print(f"{len(cands)} clustered jump-table candidates "
          f"(min_run={min_run}, max_span={hex(max_span)})\n")
    print(f"{'kind':5} {'sec':8} {'table_VA':>14} {'n':>4} "
          f"{'tgt_lo':>13} {'tgt_hi':>13} {'span':>8}")
    for kind, sec, tva, n, tlo, thi, span in cands[:show]:
        print(f"{kind:5} {sec:8} {tva:#014x} {n:>4} "
              f"{tlo:#013x} {thi:#013x} {span:#8x}")


if __name__ == "__main__":
    main()
