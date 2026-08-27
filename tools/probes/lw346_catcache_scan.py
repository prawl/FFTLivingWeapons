#!/usr/bin/env python
"""LW-346 registry hunt: find the boot-resolved per-id CATEGORY cache in live memory.

WHY. On 1.5 the item category system is id-range DATA tables consumed at boot; the live
category getter answers from a boot-resolved cache via an encrypted path (June-26 session 2:
live range-table pokes do nothing, only a function-return hook works). That cache is the
best "registry" suspect: the pre-1.5 display win was precisely a category-getter hook
(id261 -> clone weapon category -> appears in the Weapons tab), so the 1.5 list-build is
presumed to consult this cache per id and drop ids whose entry is missing/terminal.

FINGERPRINT. A per-id array of 261 entries whose value is constant inside each id range
and changes at each range boundary. Using the range table's own indices the expected tail
for ids 256..260 is [7, 7, 10, 11, 12] (ranges 8/9 are empty duplicates at 258) preceded
by sixteen 6s (ids 240..255); the dataType variant is [5, 5, 7, 7, 8] preceded by 4s.
Both anchors are searched at several record strides; every anchor hit is then verified
against the FULL 261-entry expected sequence and reported with its match count. A hit
matching all 261 is the cache; poke entry 261 live (+ display caps) and reopen the menu.

If nothing matches exactly, the cache stores remapped enum values: re-run the June-4 CE
find-what-accesses instead (this probe's structural mode is not built until needed).

READ-ONLY. Requires the game running with a save loaded (the cache is boot/load-built).
USAGE: python lw346_catcache_scan.py
"""
import ctypes as C
import os
import sys
import time

import numpy as np

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from ct_probe import PROC, PV, find_pid, k32, rd
from lw346_registry_seq_scan import walk_regions

# id-range bounds, dumped live 2026-08-26 (lw346_idrange_tables_dump.py, 1.5.2)
BOUNDS = [0, 122, 128, 144, 172, 208, 240, 256, 258, 258, 258, 259, 260, 261]
CAT_TO_DT = [0, 0, 1, 2, 2, 3, 4, 5, 5, 6, 7, 7, 8, 9]

STRIDES = [1, 2, 4, 8, 12, 16, 24, 32]
CHUNK = 0x400000
OVERLAP = max(STRIDES) * 8


def range_of(item_id):
    for i in range(len(BOUNDS) - 1, -1, -1):
        if item_id >= BOUNDS[i]:
            return i
    return -1


SEQ_CAT = bytes(range_of(i) for i in range(261))
SEQ_DT = bytes(CAT_TO_DT[range_of(i)] for i in range(261))
VARIANTS = [("cat", SEQ_CAT), ("dt", SEQ_DT)]


def anchor_positions(data, seq, s):
    """Vectorized: positions of entry 256 where entries 256..260 match seq's tail."""
    a = np.frombuffer(data, dtype=np.uint8)
    n = len(a) - 4 * s
    if n <= 0:
        return []
    m = (a[:n] == seq[256])
    for k in range(1, 5):
        m &= (a[k * s:n + k * s] == seq[256 + k])
    return np.where(m)[0]


def verify_full(h, addr256, s, seq):
    """Count how many of the 261 entries match, reading live around the anchor."""
    base = addr256 - 256 * s
    buf = rd(h, base, 261 * s)
    if buf is None:
        return 0, base
    good = sum(1 for i in range(261) if buf[i * s] == seq[i])
    return good, base


def main():
    pid = find_pid(PROC)
    if not pid:
        print(f"{PROC} not running"); return 1
    h = k32.OpenProcess(PV, False, pid)
    regions = walk_regions(h)
    total = sum(sz for _, sz, _ in regions)
    print(f"pid {pid}: {len(regions)} regions, {total / (1 << 30):.2f} GB, "
          f"strides {STRIDES}")

    results = {}
    t0 = time.time()
    for base, size, rtype in regions:
        pos = 0
        while pos < size:
            want = min(CHUNK, size - pos)
            data = rd(h, base + pos, want)
            if data is not None:
                for name, seq in VARIANTS:
                    for s in STRIDES:
                        for off in anchor_positions(data, seq, s):
                            addr256 = base + pos + int(off)
                            good, cache_base = verify_full(h, addr256, s, seq)
                            if good >= 200:      # near-full match only
                                key = (cache_base, s, name)
                                results[key] = (good, rtype)
            step = want - OVERLAP if want == CHUNK else want
            pos += max(step, 1)

    print(f"done in {time.time() - t0:.0f}s; {len(results)} near-full matches")
    for (cache_base, s, name), (good, rtype) in sorted(
            results.items(), key=lambda kv: -kv[1][0]):
        tag = "IMAGE" if rtype == 0x1000000 else "heap "
        star = " <-- FULL 261 MATCH" if good == 261 else ""
        print(f"  {tag} base 0x{cache_base:X} stride {s:>2} variant {name}: "
              f"{good}/261 entries match{star}")
    if not results:
        print("  none: the cache stores remapped values or wider records; "
              "fall back to CE find-what-accesses (June-4 method).")
    k32.CloseHandle(h)
    return 0


if __name__ == "__main__":
    sys.exit(main())
