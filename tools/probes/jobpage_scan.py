"""Find the job -> command-page mapping table.

Generic jobs 74..93 map to JobCommand pages 5..24 (live-proven anchors). If the job table
stores the page id per job record at fixed stride, the page bytes form an ascending run
5,6,7,8,... spaced exactly one stride apart. Scan the static image/data ranges
(0x140000000-0x142000000) for chains of >=8 ascending bytes starting at 5 across candidate
strides. Read-only.
"""
import ctypes
import ctypes.wintypes as wt
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from ct_probe import PROC, PV_W, find_pid, k32

class MBI(ctypes.Structure):
    _fields_ = [("BaseAddress", ctypes.c_void_p), ("AllocationBase", ctypes.c_void_p),
                ("AllocationProtect", wt.DWORD), ("PartitionId", wt.WORD),
                ("RegionSize", ctypes.c_size_t), ("State", wt.DWORD),
                ("Protect", wt.DWORD), ("Type", wt.DWORD)]

MEM_COMMIT = 0x1000
PAGE_GUARD = 0x100
READABLE = {0x02, 0x04, 0x08, 0x20, 0x40, 0x80}
LO, HI = 0x140000000, 0x142000000
STRIDES = [1, 2, 3, 4, 6, 8, 10, 12, 16, 20, 24, 25, 28, 32, 40, 48, 56, 64, 80, 96,
           112, 128, 0x90, 0xA0, 0xC0, 0x100, 0x140, 0x180, 0x200, 0x258]
CHAIN = 10   # require pages 5..14 in a row

def rd_raw(h, addr, size):
    buf = ctypes.create_string_buffer(size)
    n = ctypes.c_size_t()
    if not k32.ReadProcessMemory(h, ctypes.c_void_p(addr), buf, size, ctypes.byref(n)):
        return None
    return buf.raw[: n.value]

pid = find_pid(PROC)
if not pid:
    sys.exit(f"{PROC} not running")
h = k32.OpenProcess(PV_W, False, pid)
hits = []
try:
    addr = LO
    mbi = MBI()
    while addr < HI:
        if not k32.VirtualQueryEx(h, ctypes.c_void_p(addr), ctypes.byref(mbi), ctypes.sizeof(mbi)):
            break
        base = mbi.BaseAddress or 0
        size = mbi.RegionSize
        if (mbi.State == MEM_COMMIT and not (mbi.Protect & PAGE_GUARD)
                and (mbi.Protect & 0xFF) in READABLE):
            data = rd_raw(h, base, size)
            if data:
                fives = [i for i, v in enumerate(data) if v == 5]
                for s in STRIDES:
                    for i in fives:
                        if i + s * CHAIN >= len(data):
                            continue
                        ok = all(data[i + s * k] == 5 + k for k in range(CHAIN))
                        if ok:
                            hits.append((base + i, s))
        addr = base + size
    print(f"{len(hits)} chain hits (pages 5..{4+CHAIN}, stride shown):")
    for a, s in hits[:25]:
        # the chain starts at job 74's record; job 77 (Archer) page byte = a + 3*s
        print(f"  base {a:#x} stride {s:#x}  -> Archer page byte at {a + 3*s:#x}")
finally:
    k32.CloseHandle(h)
