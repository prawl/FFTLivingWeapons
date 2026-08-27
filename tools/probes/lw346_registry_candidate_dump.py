#!/usr/bin/env python
"""LW-346 registry hunt: characterize an id-ordered table found by lw346_registry_seq_scan.

WHY. The sequence scan surfaced heap tables spanning exactly ids 0..260 (the boot-built
registry's shape) and 0..261 (structures that ingested the added nxd row). This dumps a
candidate's records raw, annotates each with what we know about the id (its category from
the id-range tables, weapon/armor/etc.), and prints the bytes AROUND the table -- a header
holding 261, vector begin/end pointers, or slack space to append into are all load-bearing
facts for the injection experiment.

READ-ONLY.
USAGE:
    python lw346_registry_candidate_dump.py <base_hex> <stride> [n_entries=262] [id_off=0]
    # e.g. python lw346_registry_candidate_dump.py 0x15B6CF820 8
"""
import ctypes as C
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from ct_probe import PROC, PV, find_pid, k32, rd

# id-range -> category (dumped live 2026-08-26 by lw346_idrange_tables_dump.py; 1.5.2)
RANGE_BOUNDS = [0, 122, 128, 144, 172, 208, 240, 256, 258, 258, 258, 259, 260, 261]
CAT_NAMES = {0: "wpn", 1: "shield", 2: "helm", 3: "armor", 4: "acc", 5: "item",
             6: "r256", 7: "r258a", 8: "r258b", 9: "r258c", 10: "r259", 11: "r260",
             12: "r261", 13: "term"}


def cat_of(item_id):
    for i in range(len(RANGE_BOUNDS) - 1, -1, -1):
        if item_id >= RANGE_BOUNDS[i]:
            return i
    return -1


def main():
    base = int(sys.argv[1], 16)
    stride = int(sys.argv[2])
    n = int(sys.argv[3]) if len(sys.argv) > 3 else 262
    id_off = int(sys.argv[4]) if len(sys.argv) > 4 else 0

    pid = find_pid(PROC)
    if not pid:
        print(f"{PROC} not running"); return 1
    h = k32.OpenProcess(PV, False, pid)

    pre = rd(h, base - 0x40, 0x40)
    print(f"=== 0x40 bytes BEFORE 0x{base:X} ===")
    if pre:
        for i in range(0, 0x40, 16):
            row = pre[i:i + 16]
            print(f"  0x{base - 0x40 + i:X}: {row.hex(' ')}")
    else:
        print("  (unreadable)")

    print(f"\n=== records @ 0x{base:X} stride {stride} (id field at +{id_off}) ===")
    data = rd(h, base, stride * (n + 8))
    if data is None:
        print("  read failed"); return 1

    prev = None
    for i in range(n + 8):
        off = i * stride
        rec = data[off:off + stride]
        if len(rec) < stride:
            break
        rid = rec[id_off] | (rec[id_off + 1] << 8) if id_off + 1 < stride else rec[id_off]
        note = ""
        if i < n:
            c = cat_of(i)
            note = f" cat={CAT_NAMES.get(c, c)}"
        mark = " <-- PAST END" if i >= n else ""
        # collapse identical-shaped runs: print first 8, boundaries, and every 16th
        boundary = i in (0, 121, 122, 127, 128, 143, 144, 171, 172, 207, 208, 239, 240,
                         255, 256, 257, 258, 259, 260, 261)
        if i < 8 or boundary or i % 16 == 0 or i >= n:
            print(f"  [{i:3}] 0x{base + off:X}: {rec.hex(' ')}  id_fld={rid}{note}{mark}")
        prev = rec

    print(f"\n=== 0x40 bytes AFTER entry {n - 1} ===")
    tail_addr = base + n * stride
    tail = rd(h, tail_addr, 0x40)
    if tail:
        for i in range(0, 0x40, 16):
            row = tail[i:i + 16]
            print(f"  0x{tail_addr + i:X}: {row.hex(' ')}")
    k32.CloseHandle(h)
    return 0


if __name__ == "__main__":
    sys.exit(main())
