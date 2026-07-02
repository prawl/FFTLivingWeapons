#!/usr/bin/env python
"""
Pointer-reference scanner: find every RW memory site holding a qword pointer to a given
combat frame (array 0x141853CE0, stride 0x200) -- the road to the AI's real decision object.

Rationale (frame_diff/stamp_swap, 2026-07-02): the per-frame incoming-action stamps are
DERIVED forecast, re-emitted per phase and swept when tampered with; the authoritative
AI decision (who to hit) lives in an engine-side object that must reference its target --
in a C++ engine, almost certainly by frame pointer.

USAGE (game running, IN a live battle):
    python -u ptr_scan.py scan 18          # all RW sites holding &frame[18], saved to refs_18.txt
    python -u ptr_scan.py watch 18         # rescan every 2s; print sites that APPEAR/VANISH --
                                           # run across an enemy's pending action: a site that
                                           # appears at decision time and vanishes after impact
                                           # is the decision object's target field
"""
import ctypes as C
from ctypes import wintypes as W
import os
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from ct_probe import PROC, PV, find_pid, k32, rd

BASE = 0x141853CE0
STRIDE = 0x200
CHUNK = 0x400000


class MBI(C.Structure):
    _fields_ = [("BaseAddress", C.c_void_p), ("AllocationBase", C.c_void_p),
                ("AllocationProtect", W.DWORD), ("PartitionId", W.WORD),
                ("RegionSize", C.c_size_t), ("State", W.DWORD),
                ("Protect", W.DWORD), ("Type", W.DWORD)]


def walk_regions(h):
    regions = []
    addr = 0
    mbi = MBI()
    while addr < 0x7FFFFFFF0000:
        if not k32.VirtualQueryEx(h, C.c_void_p(addr), C.byref(mbi), C.sizeof(mbi)):
            break
        base = mbi.BaseAddress or 0
        size = mbi.RegionSize
        if size == 0:
            break
        if (mbi.State == 0x1000 and not (mbi.Protect & 0x100)
                and (mbi.Protect & 0xFF) in (0x04, 0x08, 0x40, 0x80)
                and mbi.Type in (0x20000, 0x1000000) and size < 0x20000000):
            regions.append((base, size))
        addr = base + size
    return regions


def find_refs(h, target):
    """Absolute addresses of every occurrence of the qword little-endian pointer."""
    pat = target.to_bytes(8, "little")
    hits = []
    for base, size in walk_regions(h):
        off = 0
        while off < size:
            n = min(CHUNK + 8, size - off)
            data = rd(h, base + off, n)
            if data:
                p = data.find(pat)
                while p != -1:
                    hits.append(base + off + p)
                    p = data.find(pat, p + 1)
            off += CHUNK
    return hits


def main():
    if len(sys.argv) < 3:
        print(__doc__)
        return
    mode, slot = sys.argv[1], int(sys.argv[2], 0)
    target = BASE + slot * STRIDE
    pid = find_pid(PROC)
    if not pid:
        sys.exit(f"{PROC} not running")
    h = k32.OpenProcess(PV, False, pid)
    try:
        if mode == "scan":
            t0 = time.time()
            hits = find_refs(h, target)
            out = os.path.join(os.path.dirname(os.path.abspath(__file__)), f"refs_{slot}.txt")
            with open(out, "w") as f:
                f.write("\n".join(f"0x{a:012X}" for a in hits))
            print(f"{len(hits)} refs to &frame[{slot}] (0x{target:X}) in {time.time() - t0:.1f}s -> {out}")
            for a in hits[:40]:
                print(f"  0x{a:012X}")
            if len(hits) > 40:
                print(f"  ... {len(hits) - 40} more (see file)")
        elif mode == "watch":
            print(f"rescanning refs to &frame[{slot}] every ~2s; printing APPEAR/VANISH. Ctrl+C stops.")
            known = set(find_refs(h, target))
            print(f"baseline: {len(known)} refs")
            while True:
                cur = set(find_refs(h, target))
                t = time.strftime("%H:%M:%S")
                for a in sorted(cur - known):
                    print(f"{t} APPEAR 0x{a:012X}")
                for a in sorted(known - cur):
                    print(f"{t} VANISH 0x{a:012X}")
                known = cur
        else:
            print(__doc__)
    except KeyboardInterrupt:
        pass
    finally:
        k32.CloseHandle(h)


if __name__ == "__main__":
    main()
