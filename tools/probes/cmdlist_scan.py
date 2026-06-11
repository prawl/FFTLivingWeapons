"""Find the battle unit's materialized COMMAND LIST.

Ramza in battle: Archer primary (rec 8) + Steal secondary (rec 14). Locate every memory copy
of him via the lvl/brave/faith byte fingerprint (99/97/75 = 63 61 4B), then search a +-0x300
window for byte 0x08 followed within a few bytes by 0x0E (and the u16-spaced variant).
Read-only. Prints candidates with window-relative offsets + context for eyeballing.
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
FP = bytes([0x63, 0x61, 0x4B])   # lvl 99, brave 97, faith 75
WIN = 0x300

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
fp_hits = []
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
                i = data.find(FP)
                while i != -1:
                    fp_hits.append(base + i)
                    i = data.find(FP, i + 1)
        addr = base + size
    print(f"{len(fp_hits)} fingerprint hits")
    for fp_addr in fp_hits:
        win = rd_raw(h, fp_addr - WIN, WIN * 2)
        if not win:
            continue
        found = []
        for j in range(len(win) - 8):
            if win[j] == 0x08:
                for k in range(1, 7):
                    if win[j + k] == 0x0E:
                        found.append((j, k))
                        break
        for j, k in found:
            off = j - WIN   # relative to the fingerprint (lvl byte)
            ctx = win[max(0, j - 8): j + 16]
            print(f"fp@{fp_addr:#x} cmd-candidate at fp{off:+#x} (gap {k}): "
                  + " ".join(f"{v:02X}" for v in ctx))
finally:
    k32.CloseHandle(h)
