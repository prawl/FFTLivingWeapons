"""Refine job-table candidates: require the FULL 20-job run (pages 5..24 for jobs 74..93)
and a NON-consecutive byte after it (job 94 = monster page, not 25). Print survivors with
context so the true table is identifiable. Read-only."""
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
survivors = []
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
                        if i + s * 21 >= len(data):
                            continue
                        if all(data[i + s * k] == 5 + k for k in range(20)):
                            nxt = data[i + s * 20]
                            if nxt != 25:
                                survivors.append((base + i, s, nxt))
        addr = base + size
    print(f"{len(survivors)} survivors (full 20-run, non-consecutive follower):")
    for a, s, nxt in survivors[:15]:
        print(f"  base {a:#x} stride {s:#x}  job94-follower={nxt}  Archer(77) page byte at {a + 3*s:#x}")
finally:
    k32.CloseHandle(h)
