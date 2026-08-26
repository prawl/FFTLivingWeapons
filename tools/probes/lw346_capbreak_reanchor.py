#!/usr/bin/env python
"""LW-346 offline byte check: did the 261 cap-break rig's gates survive to 1.5.2?

The FFTHandsFree capbreak-equip rig pins five patch sites plus two catalog data bases,
ALL found against the pre-1.5 binary (2026-06-26 sessions, CE live disasm). The game
recompiled twice since (1.5.1, 1.5.2). Per docs/PATCH_REANCHOR.md, before any live
probe: verify the old addresses against the NEW exe on disk. This tool does that
offline, using the exe_reanchor_scan.py method (VA -> RVA -> file offset via the PE
section table; lift a window from the OLD exe; exact-compare at the same VA in the
NEW exe; on mismatch, signature-search the new image, falling back to a search with
disp32/rel32 bytes wildcarded since relative operands move independently of code).

Oracle bytes come from the rig's own doc comments (FFTHandsFree/Hooks/
CapBreakEquipHook.cs + ExtendedCatalogRelocator.cs) and are ASSERTED against the old
exe first: if the oracle does not match the pre-1.5 bytes, the documentation is wrong
and the comparison would be meaningless.

Usage: python tools/probes/lw346_capbreak_reanchor.py [old_exe] [new_exe]
"""
import re
import struct
import sys

OLD_DEFAULT = r"C:\Users\ptyRa\FFT_IC_backup_pre1.5\FFT_enhanced.exe"
NEW_DEFAULT = (r"C:\Program Files (x86)\Steam\steamapps\common"
               r"\FINAL FANTASY TACTICS - The Ivalice Chronicles\FFT_enhanced.exe")
IMAGE_BASE = 0x140000000

# Expected NEW-exe PE identity (1.5.2, from the port ledger / LaunchGuard).
NEW_PE_KEY = (0x6A5EA53C, 0x18D78000)

# (name, window_start_va, window_len, oracle_checks, mask_vas, note)
#   oracle_checks: [(va, expected_bytes, label)] asserted against the OLD exe.
#   mask_vas: [(va, len)] wildcarded in the fallback search (disp32/rel32 operands).
ANCHORS = [
    ("equip-clamp", 0x140284C60, 64,
     [(0x140284C80, bytes([0x8D, 0x4A, 0x06]), "lea ecx,[rdx+6]")],
     [],
     "gate 1: clamp disp8 @0x140284C82, patch 06->07"),
    ("count-cap", 0x140284860, 64,
     [(0x140284875, bytes([0x41, 0xB8, 0x03, 0x01, 0x00, 0x00]), "mov r8d,0x103")],
     [],
     "gate 4: imm byte @0x140284878, patch 01->02"),
    ("catalog-accessor-cluster", 0x1402B8CB0, 88,
     [(0x1402B8CDA, bytes([0x48, 0x8D, 0x04, 0x85]), "lea rax,[rax*4+disp32]"),
      (0x1402B8CDE, bytes([0x10, 0xF9, 0x67, 0x00]), "disp32 = 0x0067F910"),
      (0x1402B8CE8, bytes([0xE9]), "weapon-stat enc thunk jmp")],
     [(0x1402B8CDE, 4), (0x1402B8CE9, 4)],
     "gates 2+3: catalog disp32 @0x1402B8CDE + weapon thunk @0x1402B8CE8"),
    ("validity-thunk", 0x1402B8F20, 48,
     [(0x1402B8F30, bytes([0xE9]), "validity enc thunk jmp")],
     [(0x1402B8F31, 4)],
     "gate 5: validity thunk @0x1402B8F30, doc target 0x1501484E0"),
    ("ext-catalog-data", 0x14067F910, 60, [], [],
     "DLC catalog records ids 256-260 (12B each), relocation source data"),
    ("main-catalog-data", 0x14080EA90, 96, [], [],
     "main catalog ids 0-255 base; id37 clone source @+0x1BC"),
    ("count-array", 0x1411A7C00, 16, [], [],
     "runtime count[] base; EXPECT not-in-raw (negative case)"),
]

# Thunk sites whose E9 rel32 targets we decode and report old vs new.
THUNKS = [("weapon-stat", 0x1402B8CE8, 0x1500DF9F8), ("validity", 0x1402B8F30, 0x1501484E0)]


def pe_info(data):
    e_lfanew = struct.unpack_from("<I", data, 0x3C)[0]
    ts = struct.unpack_from("<I", data, e_lfanew + 8)[0]
    opt = e_lfanew + 24
    size_img = struct.unpack_from("<I", data, opt + 56)[0]
    nsec = struct.unpack_from("<H", data, e_lfanew + 6)[0]
    opt_size = struct.unpack_from("<H", data, e_lfanew + 20)[0]
    secs = []
    for i in range(nsec):
        s = opt + opt_size + i * 40
        name = data[s:s + 8].rstrip(b"\0").decode("ascii", "replace")
        vsize, va, rsize, rptr = struct.unpack_from("<IIII", data, s + 8)
        secs.append((name, va, vsize, rptr, rsize))
    return ts, size_img, secs


def va2off(secs, va):
    rva = va - IMAGE_BASE
    for name, sva, vsize, rptr, rsize in secs:
        if sva <= rva < sva + vsize:
            delta = rva - sva
            if delta < rsize:
                return rptr + delta, name
            return None, name  # inside section, beyond raw data = uninitialized
    return None, "?"


def hexdump(b):
    return " ".join("%02X" % x for x in b)


def decode_jmp(data, off, va):
    if data[off] != 0xE9:
        return None
    rel = struct.unpack_from("<i", data, off + 1)[0]
    return va + 5 + rel


def masked_search(data, window, masks):
    pat = b""
    i = 0
    for mo, ml in sorted(masks):
        pat += re.escape(window[i:mo]) + b"." * ml
        i = mo + ml
    pat += re.escape(window[i:])
    return [m.start() for m in re.finditer(pat, data, re.DOTALL)][:8]


def main():
    old_path = sys.argv[1] if len(sys.argv) > 1 else OLD_DEFAULT
    new_path = sys.argv[2] if len(sys.argv) > 2 else NEW_DEFAULT
    old = open(old_path, "rb").read()
    new = open(new_path, "rb").read()
    ots, osz, osecs = pe_info(old)
    nts, nsz, nsecs = pe_info(new)
    print("OLD %s  ts=0x%08X size=0x%X" % (old_path, ots, osz))
    print("NEW %s  ts=0x%08X size=0x%X" % (new_path, nts, nsz))
    if (nts, nsz) != NEW_PE_KEY:
        print("!! NEW exe PE key != expected 1.5.2 key (0x%08X, 0x%X) -- wrong binary?" % NEW_PE_KEY)
    print()

    def new_off_to_va(off):
        for sname, sva, vsize, rptr, rsize in nsecs:
            if rptr <= off < rptr + rsize:
                return IMAGE_BASE + sva + (off - rptr)
        return None

    for name, wva, wlen, checks, masks, note in ANCHORS:
        print("== %s  (%s)" % (name, note))
        ooff, osec = va2off(osecs, wva)
        if ooff is None:
            print("   OLD: not in raw data (section %s) -- runtime-only, offline N/A" % osec)
            noff, nsec = va2off(nsecs, wva)
            print("   NEW: %s" % ("also not in raw (section %s), as expected" % nsec if noff is None
                                  else "IN RAW now?! off=0x%X" % noff))
            print()
            continue
        window = old[ooff:ooff + wlen]
        for cva, expect, label in checks:
            coff, _ = va2off(osecs, cva)
            got = old[coff:coff + len(expect)]
            ok = got == expect
            print("   oracle %-28s @0x%X: %s  (old bytes: %s)" %
                  (label, cva, "OK" if ok else "MISMATCH expected %s" % hexdump(expect), hexdump(got)))
        noff, nsec = va2off(nsecs, wva)
        if noff is None:
            print("   NEW: VA not in raw data (section %s) -- content moved or layout changed" % nsec)
        else:
            nwindow = new[noff:noff + wlen]
            if nwindow == window:
                print("   NEW @0x%X: IDENTICAL -- SURVIVED IN PLACE" % wva)
                print()
                continue
            ndiff = sum(1 for a, b in zip(window, nwindow) if a != b)
            first = next(i for i, (a, b) in enumerate(zip(window, nwindow)) if a != b)
            print("   NEW @0x%X: DIFFERS (%d/%d bytes, first at +0x%X = VA 0x%X)" %
                  (wva, ndiff, wlen, first, wva + first))
            print("     old: %s" % hexdump(window[max(0, first - 4):first + 12]))
            print("     new: %s" % hexdump(nwindow[max(0, first - 4):first + 12]))
        hits = [m.start() for m in re.finditer(re.escape(window), new)][:8]
        kind = "exact"
        if not hits and masks:
            rel = [(va2off(osecs, mva)[0] - ooff, ml) for mva, ml in masks]
            hits = masked_search(new, window, rel)
            kind = "masked"
        vas = [new_off_to_va(h) for h in hits]
        print("   %s search in NEW: %d hit(s): %s" %
              (kind, len(hits), ", ".join("0x%X" % v if v else "?" for v in vas) or "none"))
        print()

    print("== thunk targets (E9 rel32 decode, old vs new site)")
    for tname, tva, doc_target in THUNKS:
        ooff, _ = va2off(osecs, tva)
        noff, _ = va2off(nsecs, tva)
        ot = decode_jmp(old, ooff, tva) if ooff else None
        nt = decode_jmp(new, noff, tva) if noff else None
        print("   %-12s @0x%X: old->%s (doc 0x%X)  new->%s" %
              (tname, tva,
               "0x%X" % ot if ot else "not-E9", doc_target,
               "0x%X" % nt if nt else "not-E9"))

    print()
    print("== section table diff (deltas are HINTS, never interpolate -- PATCH_REANCHOR rule)")
    om = {s[0]: s for s in osecs}
    for sname, sva, vsize, rptr, rsize in nsecs:
        o = om.get(sname)
        if o:
            print("   %-10s va 0x%08X->0x%08X (%+d)  vsize 0x%X->0x%X" %
                  (sname, o[1], sva, sva - o[1], o[2], vsize))
        else:
            print("   %-10s NEW SECTION va 0x%08X" % (sname, sva))


if __name__ == "__main__":
    main()
