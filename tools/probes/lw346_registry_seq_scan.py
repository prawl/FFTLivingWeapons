#!/usr/bin/env python
"""LW-346 registry hunt, data-first round: find every id-ORDERED structure in live memory.

WHY. The boot-built item registry (the list/equip source that still excludes id261 after
every known cap/catalog patch) has never been LOCATED -- every prior round patched code
bounds and data tables around it. Whatever its layout, if its records are keyed or ordered
by item id it must contain the extended-id run 256..260 (u16 0x100..0x104) somewhere at a
fixed record stride: the five "+" items are ordinary registry citizens (they render in the
bag). This probe fingerprints exactly that run across ALL committed RW memory (heap AND
image statics -- the count array itself is an image static, the registry may be too), then
extends each hit backward/forward to measure the full ascending extent. The dream hit is a
run whose extent is exactly ids 0..260: that IS the registry (or its index), and appending
a 261st record becomes a live-pokeable experiment for the first time.

The 256..260 fingerprint also dodges known noise: LivingWeapon's own meta structures hold
only vanilla weapon ids (<256), and the id-range boundary table (0x1406804E0) holds
duplicate 258s so it cannot match a strictly ascending run.

READ-ONLY. Requires the game running (any screen; the registry is built at boot).
USAGE:
    python lw346_registry_seq_scan.py            # full scan, report grouped candidates
    python lw346_registry_seq_scan.py --min-run 3  # looser: require only 256..258
"""
import ctypes as C
import os
import sys
import time

import numpy as np

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from ct_probe import PROC, PV, find_pid, k32, rd

from ctypes import wintypes as W

# Record strides to test, in bytes (record size between consecutive ids). 2 = packed u16
# array; larger = id field inside a wider record. 0x0C = the catalog record size, worth
# having in the set explicitly.
STRIDES = sorted({2, 3, 4, 6, 8, 10, 12, 14, 16, 20, 24, 28, 32, 36, 40, 44, 48,
                  56, 64, 72, 80, 96, 112, 128})
MAXS = max(STRIDES)
CHUNK = 0x400000                     # 4MB reads
OVERLAP = MAXS * 8                   # never lose a cross-boundary run
RUN_IDS = [0x100, 0x101, 0x102, 0x103, 0x104]   # ids 256..260


class MBI(C.Structure):
    _fields_ = [("BaseAddress", C.c_void_p), ("AllocationBase", C.c_void_p),
                ("AllocationProtect", W.DWORD), ("PartitionId", W.WORD),
                ("RegionSize", C.c_size_t), ("State", W.DWORD),
                ("Protect", W.DWORD), ("Type", W.DWORD)]


def walk_regions(h):
    """Committed, non-guard, readable RW regions: MEM_PRIVATE + MEM_IMAGE."""
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
                and mbi.Type in (0x20000, 0x1000000)
                and size < 0x40000000):
            regions.append((base, size, mbi.Type))
        addr = base + size
    return regions


def u16_at(data, off):
    return data[off] | (data[off + 1] << 8)


def extend_run(h, addr_of_256, stride):
    """From the address holding u16 256, walk the ascending id run both ways.

    Reads live memory directly (small reads) so runs crossing chunk borders measure true.
    Returns (first_id, last_id, base_addr_of_first).
    """
    lo_id, hi_id = 0x100, 0x104
    a = addr_of_256
    while lo_id > 0:
        b = rd(h, a - stride, 2)
        if b is None or (b[0] | (b[1] << 8)) != lo_id - 1:
            break
        lo_id -= 1
        a -= stride
    base = a
    a = addr_of_256 + 4 * stride
    while hi_id < 0x2000:
        b = rd(h, a + stride, 2)
        if b is None or (b[0] | (b[1] << 8)) != hi_id + 1:
            break
        hi_id += 1
        a += stride
    return lo_id, hi_id, base


def scan_chunk_for_run(data, min_run):
    """Yield (offset, stride) where u16(offset)=256 and the run continues min_run steps."""
    a8 = np.frombuffer(data, dtype=np.uint8)
    if len(a8) < 2:
        return
    u = a8[:-1].astype(np.uint16) | (a8[1:].astype(np.uint16) << 8)
    hits = np.where(u == 0x100)[0]
    n = len(data)
    for off in hits:
        off = int(off)
        for s in STRIDES:
            if off + (min_run - 1) * s + 1 >= n:
                continue
            ok = True
            for k in range(1, min_run):
                if u16_at(data, off + k * s) != 0x100 + k:
                    ok = False
                    break
            if ok:
                yield off, s


def main():
    min_run = len(RUN_IDS)
    if "--min-run" in sys.argv:
        min_run = int(sys.argv[sys.argv.index("--min-run") + 1])

    pid = find_pid(PROC)
    if not pid:
        print(f"{PROC} not running"); return 1
    h = k32.OpenProcess(PV, False, pid)
    if not h:
        print("OpenProcess failed"); return 1

    regions = walk_regions(h)
    total = sum(sz for _, sz, _ in regions)
    print(f"pid {pid}: {len(regions)} RW regions, {total / (1 << 30):.2f} GB to scan, "
          f"strides {STRIDES[0]}..{MAXS}, run >= {min_run}")

    # candidates keyed by (base_of_run, stride) after live extension -> dedup
    seen = {}
    t0 = time.time()
    done = 0
    for base, size, rtype in regions:
        pos = 0
        while pos < size:
            want = min(CHUNK, size - pos)
            data = rd(h, base + pos, want)
            if data is not None:
                for off, s in scan_chunk_for_run(data, min_run):
                    hit_addr = base + pos + off
                    lo, hi, run_base = extend_run(h, hit_addr, s)
                    key = (run_base, s)
                    if key not in seen:
                        seen[key] = (lo, hi, rtype)
            step = want - OVERLAP if want == CHUNK else want
            pos += max(step, 1)
        done += size
        if done and (done // (1 << 30)) != ((done - size) // (1 << 30)):
            print(f"  ... {done / (1 << 30):.1f} GB, {time.time() - t0:.0f}s, "
                  f"{len(seen)} runs")

    print(f"scan done in {time.time() - t0:.0f}s; {len(seen)} distinct runs\n")

    def tag(rtype):
        return "IMAGE" if rtype == 0x1000000 else "heap "

    # The registry-shaped hits first: runs that START at or near id 0 and END at 260/261.
    ranked = sorted(seen.items(),
                    key=lambda kv: (kv[1][0] != 0, abs(kv[1][1] - 0x104), kv[0][1]))
    print("=== runs covering 256..260, best (full 0..260 extent) first ===")
    for (run_base, s), (lo, hi, rtype) in ranked[:40]:
        n = hi - lo + 1
        star = " <-- FULL id table" if lo == 0 and hi in (0x104, 0x105) else ""
        print(f"  {tag(rtype)} base 0x{run_base:X} stride {s:>3}  ids {lo}..{hi} "
              f"({n} entries){star}")
    if len(ranked) > 40:
        print(f"  ... plus {len(ranked) - 40} more (looser extents)")
    k32.CloseHandle(h)
    return 0


if __name__ == "__main__":
    sys.exit(main())
