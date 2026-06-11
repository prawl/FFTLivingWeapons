"""Find the live command-id list and read the entry AFTER Steal (0E) -- that's Reequip's
command id now that it's equipped. Scan for 08 00 0E 00 (Aim, Steal) and dump 24 bytes of
following context. Dedup identical contexts. Read-only."""
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
SIG = bytes([0x08, 0x00, 0x0E, 0x00])

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
seen = {}
try:
    addr = 0
    mbi = MBI()
    while addr < 0x7FFFFFFF0000:
        if not k32.VirtualQueryEx(h, ctypes.c_void_p(addr), ctypes.byref(mbi), ctypes.sizeof(mbi)):
            break
        base = mbi.BaseAddress or 0
        size = mbi.RegionSize
        if (mbi.State == MEM_COMMIT and not (mbi.Protect & PAGE_GUARD)
                and (mbi.Protect & 0xFF) in READABLE and size < 0x20000000):
            data = rd_raw(h, base, size)
            if data:
                i = data.find(SIG)
                while i != -1:
                    ctx = bytes(data[i:i + 24])
                    seen.setdefault(ctx, []).append(base + i)
                    i = data.find(SIG, i + 1)
        addr = base + size
    print(f"{len(seen)} distinct contexts after 08 00 0E 00:")
    for ctx, addrs in sorted(seen.items(), key=lambda kv: -len(kv[1])):
        u16 = [ctx[j] | (ctx[j + 1] << 8) for j in range(0, 24, 2)]
        print(f"  x{len(addrs):3} @ {addrs[0]:#x}: u16 {u16}")
        print(f"         hex " + " ".join(f"{v:02X}" for v in ctx))
finally:
    k32.CloseHandle(h)
