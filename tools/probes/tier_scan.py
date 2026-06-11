"""Scan FFT_enhanced memory for the Aim/Jump tier-id tables the command executor consults.

Patterns: u16 LE sequences [406..413] (Aim +1..+20) and [394..405] (Jump tiers), plus their
u32 variants. The JobCommand record stores these as BYTES+extend bits, so a u16/u32 hit is a
SEPARATE structure -- candidate for the executor's whitelist. Read-only.
"""
import ctypes
import ctypes.wintypes as wt
import os
import struct
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
PAGE_NOACCESS = 0x01
READABLE = {0x02, 0x04, 0x08, 0x20, 0x40, 0x80}

PATTERNS = {
    "aim_u16": struct.pack("<8H", *range(406, 414)),
    "jump_u16": struct.pack("<12H", *range(394, 406)),
    "aim_u32": struct.pack("<8I", *range(406, 414)),
    "jump_u32": struct.pack("<12I", *range(394, 406)),
}

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
        if (mbi.State == MEM_COMMIT and not (mbi.Protect & (PAGE_GUARD | PAGE_NOACCESS))
                and (mbi.Protect & 0xFF) in READABLE and size < 0x40000000):
            data = rd_raw(h, base, size)
            if data:
                for name, pat in PATTERNS.items():
                    i = data.find(pat)
                    while i != -1:
                        hits.append((name, base + i))
                        i = data.find(pat, i + 1)
        addr = base + size
    for name, a in hits:
        print(f"{name} @ {a:#014x}")
        ctx = rd_raw(h, a - 16, 16 + 48) or b""
        print("   ctx:", " ".join(f"{v:02X}" for v in ctx))
    if not hits:
        print("no hits -- the tier set likely lives in code (immediates), not a data table")
finally:
    k32.CloseHandle(h)
