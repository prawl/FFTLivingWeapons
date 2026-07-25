#!/usr/bin/env python
"""Offline re-anchor scanner: diff two FFT:IC exes ON DISK to pre-solve image-static anchors.

WHY THIS EXISTS (2026-07-24, the 1.5.2 re-anchor). docs/PATCH_REANCHOR.md Phase B assumes a live
co-op session with the owner at the controls for every anchor. That is true for RUNTIME state
(roster rows, combat bands, battle flags, UI mirrors): those live in zero-initialized image space
and hold nothing until the game runs, so a file can never answer them. But a large share of the
mod's pinned addresses are FILE-BAKED STATIC DATA or CODE (the JobCommand ability table, the
ability-action table and its decoy mirror, the inflict-status table, the SetTextString hook), and
those are byte-identical in the exe on disk. This tool re-finds those by content signature against
the previous build's own bytes, offline, before anyone launches anything.

METHOD. For each pinned address: locate it in the OLD exe (VA -> RVA -> file offset via the section
table), lift a window of its bytes, then search the NEW exe for that window. A unique hit is a
re-find; multiple hits mean the signature is not discriminating; zero hits mean the content itself
changed (the anchor needs a real live re-find). Uninitialized (BSS-style) addresses are reported as
such rather than guessed at: the section table says the address is inside a section whose raw data
does not cover it, which is exactly the "runtime only" class.

The section-table diff is printed too: it gives Phase B a per-region starting delta instead of a
blank page. Treat those deltas as HINTS, never as answers -- docs/PATCH_REANCHOR.md's standing rule
is that deltas are a non-monotonic gradient and must never be interpolated across regions.

Usage:
    python tools/probes/exe_reanchor_scan.py <old_exe> <new_exe>
    python tools/probes/exe_reanchor_scan.py            # uses the default backup paths below
"""
import os
import struct
import sys

DEFAULT_OLD = r"C:\Users\ptyRa\FFT_IC_backup_1.5.1\FFT_enhanced.exe"
DEFAULT_NEW = (r"C:\Program Files (x86)\Steam\steamapps\common"
               r"\FINAL FANTASY TACTICS - The Ivalice Chronicles\FFT_enhanced.exe")
IMAGE_BASE = 0x140000000

# (name, address, window_bytes, note). Window sizes are chosen to be long enough to be unique in a
# 360MB image but short enough to survive a neighbouring-record edit. Addresses are the CURRENT
# pins in LivingWeapon/*.cs at the time of the 1.5.2 re-anchor.
ANCHORS = [
    ("Barrage.AbilityBase",      0x14067E213, 200, "JobCommand table; rec8/rec9 signature is the guard landmark"),
    ("Offsets.LiveActionTable",  0x14078B2DC, 256, "ability action table, 368 rows x 20 (Provoke repoint target)"),
    ("ProvokePolicy.DecoyActionTable", 0x14078961C, 256, "byte-identical decoy mirror of the action table"),
    ("Offsets.InflictTable",     0x14080FBA0, 192, "inflict-status table, 128 rows x 6"),
    ("PromptSwapHook.FnSetTextString", 0x1403F1098, 64, "CODE: the true text setter (LW-89)"),
    ("BodyDoubleSpike.FnNodeBuild",    0x14026EBEC, 64, "CODE, LWDEV spike only"),
    ("BodyDoubleSpike.FnEnroll",       0x140274F30, 64, "CODE, LWDEV spike only"),
    ("BodyDoubleSpike.FnObjPopulate",  0x140284A80, 64, "CODE, LWDEV spike only"),
    # Runtime-state pins: expected to report NOT-IN-RAW (they prove the tool's own negative case).
    ("Offsets.RosterBase",       0x1411A7D10, 64, "runtime roster; expect uninitialized"),
    ("Offsets.CombatAnchor",     0x141855CE0, 64, "runtime combat band; expect uninitialized"),
    ("Offsets.BattleMode",       0x1409069A0, 64, "runtime state flag; expect uninitialized"),
    ("Offsets.PauseFlag",        0x140C6B1C8, 64, "runtime UI flag; expect uninitialized"),
    ("Offsets.SubmenuFlag",      0x140D4080C, 64, "runtime UI flag; expect uninitialized"),
]


def sections(data):
    """Return [(name, va, vsize, raw_ptr, raw_size)] plus the PE header fields we report on."""
    e_lfanew = struct.unpack_from("<I", data, 0x3C)[0]
    n_sec = struct.unpack_from("<H", data, e_lfanew + 6)[0]
    opt_size = struct.unpack_from("<H", data, e_lfanew + 20)[0]
    opt = e_lfanew + 24
    tds = struct.unpack_from("<I", data, e_lfanew + 8)[0]
    soi = struct.unpack_from("<I", data, opt + 56)[0]
    tbl = opt + opt_size
    out = []
    for i in range(n_sec):
        off = tbl + i * 40
        name = data[off:off + 8].rstrip(b"\x00").decode("ascii", "replace")
        vsize, va, raw_size, raw_ptr = struct.unpack_from("<IIII", data, off + 8)
        out.append((name, va, vsize, raw_ptr, raw_size))
    return out, tds, soi


def va_to_file(secs, va):
    """VA -> file offset, or None when the address is not backed by raw file bytes."""
    rva = va - IMAGE_BASE
    for name, sva, vsize, raw_ptr, raw_size in secs:
        if sva <= rva < sva + max(vsize, raw_size):
            delta = rva - sva
            if delta >= raw_size:
                return None, name        # inside the section, past its raw data == uninitialized
            return raw_ptr + delta, name
    return None, None


def find_all(hay, needle, limit=4):
    hits, start = [], 0
    while len(hits) < limit:
        i = hay.find(needle, start)
        if i < 0:
            break
        hits.append(i)
        start = i + 1
    return hits


def file_to_va(secs, off):
    for name, sva, vsize, raw_ptr, raw_size in secs:
        if raw_size and raw_ptr <= off < raw_ptr + raw_size:
            return IMAGE_BASE + sva + (off - raw_ptr), name
    return None, None


def main():
    old_path = sys.argv[1] if len(sys.argv) > 2 else DEFAULT_OLD
    new_path = sys.argv[2] if len(sys.argv) > 2 else DEFAULT_NEW
    for p in (old_path, new_path):
        if not os.path.exists(p):
            print("MISSING: %s" % p)
            return 2
    old = open(old_path, "rb").read()
    new = open(new_path, "rb").read()
    osecs, otds, osoi = sections(old)
    nsecs, ntds, nsoi = sections(new)

    print("OLD %s\n    TimeDateStamp 0x%08X  SizeOfImage 0x%08X  size %d" % (old_path, otds, osoi, len(old)))
    print("NEW %s\n    TimeDateStamp 0x%08X  SizeOfImage 0x%08X  size %d" % (new_path, ntds, nsoi, len(new)))
    print()
    print("=== SECTION TABLE (VA deltas are HINTS for a live re-find, never answers) ===")
    print("%-10s %-34s %-34s %s" % ("section", "old VA span", "new VA span", "VA delta"))
    nmap = {s[0]: s for s in nsecs}
    for name, sva, vsize, _rp, _rs in osecs:
        n = nmap.get(name)
        if not n:
            print("%-10s 0x%09X..0x%09X  ** GONE **" % (name, IMAGE_BASE + sva, IMAGE_BASE + sva + vsize))
            continue
        d = n[1] - sva
        print("%-10s 0x%09X..0x%09X  0x%09X..0x%09X  %s0x%X"
              % (name, IMAGE_BASE + sva, IMAGE_BASE + sva + vsize,
                 IMAGE_BASE + n[1], IMAGE_BASE + n[1] + n[2], "-" if d < 0 else "+", abs(d)))
    for name, sva, vsize, _rp, _rs in nsecs:
        if name not in {s[0] for s in osecs}:
            print("%-10s ** NEW SECTION **                    0x%09X..0x%09X" % (name, IMAGE_BASE + sva, IMAGE_BASE + sva + vsize))
    print()
    print("=== ANCHOR CONTENT RE-FIND ===")
    for label, va, win, note in ANCHORS:
        off, sec = va_to_file(osecs, va)
        if off is None:
            kind = "not in any section" if sec is None else "uninitialized in '%s' (runtime state)" % sec
            print("  %-34s 0x%09X  SKIP: %s" % (label, va, kind))
            continue
        sig = old[off:off + win]
        if len(sig) < win or sig.count(sig[:1]) == len(sig):
            print("  %-34s 0x%09X  SKIP: window is degenerate (all one byte)" % (label, va))
            continue
        hits = find_all(new, sig)
        if not hits:
            print("  %-34s 0x%09X  ** CONTENT CHANGED ** (%d-byte window absent from the new exe) -- live re-find required"
                  % (label, va, win))
        elif len(hits) == 1:
            nva, nsec = file_to_va(nsecs, hits[0])
            d = nva - va
            flag = "UNCHANGED" if d == 0 else "MOVED %s0x%X" % ("-" if d < 0 else "+", abs(d))
            print("  %-34s 0x%09X  -> 0x%09X  [%s]  sec %s->%s" % (label, va, nva, flag, sec, nsec))
        else:
            vas = ", ".join("0x%09X" % file_to_va(nsecs, h)[0] for h in hits)
            print("  %-34s 0x%09X  AMBIGUOUS: %d+ hits (%s) -- widen the window or re-find live"
                  % (label, va, len(hits), vas))
        print("  %-34s    (%s)" % ("", note))
    return 0


if __name__ == "__main__":
    sys.exit(main())
