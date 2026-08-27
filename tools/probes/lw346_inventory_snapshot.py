#!/usr/bin/env python
"""LW-346 READ-ONLY snapshot of the party inventory list while the menu is open (1.5.2).

Plain language: with the Items screen open in the running game, print what the list
actually holds: the word buffer (is id 261 in it?), the menu object's sort setting, the
list widget's raw fields (the row count lives in there), and the on-screen row table
(found by scanning writable memory for the buffer's first ids at the 0x18 row stride).
Answers "did the Moonblade survive the list build, and do the row count and the buffer
agree" without a debugger. Writes nothing.

Pointer chain (from 0x14036B221 / 0x14036B1F2 in the list-build consumer):
  [0x143CD9DA8] -> ui root; +0x10 -> ; +0x140 -> menu; +0x70 -> list panel (NULL when the
  Items screen is closed); panel+0x58 = list widget; panel+0xB18/+0xB1C = sort modes.
Usage: python tools/probes/lw346_inventory_snapshot.py [--rows]   (--rows = scan for the row table)
"""
import ctypes as C
import ctypes.wintypes as W
import struct
import sys

import numpy as np

sys.path.insert(0, __file__.rsplit("\\", 1)[0] if "\\" in __file__ else ".")
from lw346_live_disasm import find_pid, rd, k32, PV  # noqa: E402

WORD_BUF = 0x141811470
UI_ROOT = 0x143CD9DA8


class MBI(C.Structure):
    _fields_ = [("BaseAddress", C.c_void_p), ("AllocationBase", C.c_void_p), ("AllocationProtect", W.DWORD),
                ("PartitionId", W.WORD), ("RegionSize", C.c_size_t), ("State", W.DWORD), ("Protect", W.DWORD),
                ("Type", W.DWORD)]


def q(h, a):
    b = rd(h, a, 8)
    return struct.unpack("<Q", b)[0] if b else None


def scan_rows(h, ids, limit_gb=4):
    """Find every writable private region holding ids[0..3] at stride 0x18 (u16 at +0)."""
    hits = []
    addr = 0x10000
    scanned = 0
    mbi = MBI()
    k32.VirtualQueryEx.argtypes = [W.HANDLE, C.c_void_p, C.POINTER(MBI), C.c_size_t]
    while addr < 0x7FFFFFFFFFFF and scanned < limit_gb << 30:
        if not k32.VirtualQueryEx(h, C.c_void_p(addr), C.byref(mbi), C.sizeof(mbi)):
            break
        base, size = mbi.BaseAddress or 0, mbi.RegionSize
        if mbi.State == 0x1000 and mbi.Type == 0x20000 and mbi.Protect in (0x04, 0x08) and size <= 512 << 20:
            blob = rd(h, base, size)
            if blob:
                scanned += size
                arr = np.frombuffer(blob, dtype=np.uint8)
                m = (arr[:-1] == ids[0] & 0xFF) & (arr[1:] == ids[0] >> 8)
                for p in np.flatnonzero(m):
                    p = int(p)
                    ok = all(p + 0x18 * k + 1 < size and struct.unpack_from("<H", blob, p + 0x18 * k)[0] == ids[k]
                             for k in range(1, 4))
                    if ok:
                        hits.append(base + p)
        addr = base + size
    return hits, scanned


def main():
    h = k32.OpenProcess(PV, False, find_pid("fft_enhanced.exe"))
    raw = rd(h, WORD_BUF, 600)
    w = list(struct.unpack("<300H", raw))
    terms = [i for i, x in enumerate(w) if x == 0xFFFF]
    n = terms[0] if terms else 300
    ids = [x & 0x3FF for x in w[:n]]
    print("word buffer: %d entries, FFFF at %s, 261 present: %s, ext ids: %s" % (n, terms[:4], 0x105 in ids, [i for i in ids if i >= 256]))
    print("  order: %s" % ids)
    root = q(h, UI_ROOT); a = q(h, root + 0x10) if root else None; menu = q(h, a + 0x140) if a else None
    panel = q(h, menu + 0x70) if menu else None
    print("ui chain: root 0x%X -> 0x%X -> menu 0x%X -> list panel 0x%X" % (root or 0, a or 0, menu or 0, panel or 0))
    if not panel:
        print("  list panel is NULL: the Items screen is not open; open it and re-run.")
    else:
        b = rd(h, panel + 0xB00, 0x30)
        print("  panel +0xB00..: %s" % " ".join("%02X" % x for x in b))
        print("  sort modes: +0xB18=%d +0xB1C=%d +0xB20=%d" % struct.unpack_from("<iii", b, 0x18))
        wd = rd(h, panel + 0x58, 0x120)
        dw = struct.unpack("<%dI" % (len(wd) // 4), wd)
        print("  widget (panel+0x58) dwords holding a plausible count (100..150): %s" %
              ["+0x%X=%d" % (i * 4, v) for i, v in enumerate(dw) if 100 <= v <= 150])
        for i in range(0, 0x120, 32):
            print("    +%03X: %s" % (i, " ".join("%02X" % x for x in wd[i:i + 32])))
    if "--rows" in sys.argv and n >= 4:
        hits, scanned = scan_rows(h, ids[:4])
        print("row-table scan over %.1f GB of writable private memory: %d hit(s)" % (scanned / 2**30, len(hits)))
        for base in hits:
            rows = []
            for k in range(0, 160):
                wb = rd(h, base + 0x18 * k, 2)
                if not wb:
                    break
                rows.append(struct.unpack("<H", wb)[0])
            # rows past the real count are stale; print up to the first mismatch vs the buffer + 3 extra
            k = 0
            while k < len(rows) and k < n and (rows[k] & 0x3FF) == ids[k]:
                k += 1
            print("  0x%X: %d rows match the buffer in order; next rows: %s" % (base, k, [hex(r) for r in rows[k:k + 4]]))
    k32.CloseHandle(h)


if __name__ == "__main__":
    main()
