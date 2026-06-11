"""Global scan for command-list shapes: u16 pair [8][14] adjacent (08 00 0E 00) and the
byte pair strictly adjacent (08 0E). Prints contexts for the u16 hits (low noise) and a
count + first few for the byte pair. Read-only."""
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
u16_hits, byte_hits = [], []
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
                i = data.find(b"\x08\x00\x0e\x00")
                while i != -1:
                    u16_hits.append(base + i)
                    i = data.find(b"\x08\x00\x0e\x00", i + 1)
                i = data.find(b"\x08\x0e")
                while i != -1:
                    byte_hits.append(base + i)
                    i = data.find(b"\x08\x0e", i + 1)
        addr = base + size
    print(f"u16 pair hits: {len(u16_hits)}; byte pair hits: {len(byte_hits)}")
    for a in u16_hits[:20]:
        ctx = rd_raw(h, a - 16, 48) or b""
        print(f"u16 @ {a:#x}: " + " ".join(f"{v:02X}" for v in ctx))
    for a in byte_hits[:10]:
        ctx = rd_raw(h, a - 12, 36) or b""
        print(f"byte @ {a:#x}: " + " ".join(f"{v:02X}" for v in ctx))
finally:
    k32.CloseHandle(h)
