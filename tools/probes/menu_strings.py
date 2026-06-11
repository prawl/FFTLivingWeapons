"""Find the LIVE battle command-menu widget by its label strings while the menu is open.
Anchors on "Steal" (UTF-8 and UTF-16LE), then requires "Aim" within +-0x200.
Prints structure context around each qualifying hit. Read-only."""
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
hits = []
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
                for pat, enc in ((b"Steal", "u8"), ("Steal".encode("utf-16-le"), "u16")):
                    aim = b"Aim" if enc == "u8" else "Aim".encode("utf-16-le")
                    i = data.find(pat)
                    while i != -1:
                        lo = max(0, i - 0x200)
                        if aim in data[lo: i + 0x200]:
                            hits.append((enc, base + i))
                        i = data.find(pat, i + 1)
        addr = base + size
    print(f"{len(hits)} qualifying 'Steal'+'Aim' hits")
    for enc, a in hits[:14]:
        ctx = rd_raw(h, a - 48, 112) or b""
        txt = "".join(chr(v) if 32 <= v < 127 else "." for v in ctx)
        print(f"{enc} @ {a:#x}")
        print("   " + " ".join(f"{v:02X}" for v in ctx[:56]))
        print("   " + txt)
finally:
    k32.CloseHandle(h)
