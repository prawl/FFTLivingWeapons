"""Poke the support slot (4th u16) in the live ability-loadout lists [primary,secondary,
reaction,support,movement] that the battle menu reads. Lists found via the 08 00 0E 00
(Aim,Steal) prefix. Only touch copies in the GAME-DATA ranges (0x140000000-0x142000000),
never stack noise.

Usage: python support_poke.py <newSupportU16>   # e.g. 0 to clear Reequip, or restore 480
"""
import ctypes
import ctypes.wintypes as wt
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from ct_probe import PROC, PV_W, find_pid, k32, wr

class MBI(ctypes.Structure):
    _fields_ = [("BaseAddress", ctypes.c_void_p), ("AllocationBase", ctypes.c_void_p),
                ("AllocationProtect", wt.DWORD), ("PartitionId", wt.WORD),
                ("RegionSize", ctypes.c_size_t), ("State", wt.DWORD),
                ("Protect", wt.DWORD), ("Type", wt.DWORD)]

MEM_COMMIT = 0x1000
PAGE_GUARD = 0x100
READABLE = {0x02, 0x04, 0x08, 0x20, 0x40, 0x80}
# Aim(8) Steal(14) AutoPotion(441) Reequip(480) Move+1(488):
PREFIX = bytes([0x08, 0x00, 0x0E, 0x00, 0xB9, 0x01, 0xE0, 0x01, 0xE8, 0x01])
SUPPORT_OFF = 6   # 4th u16 = bytes [6:8] = the 480 (Reequip) slot
LO, HI = 0x140000000, 0x142000000

def rd_raw(h, addr, size):
    buf = ctypes.create_string_buffer(size)
    n = ctypes.c_size_t()
    if not k32.ReadProcessMemory(h, ctypes.c_void_p(addr), buf, size, ctypes.byref(n)):
        return None
    return buf.raw[: n.value]

newval = int(sys.argv[1]) if len(sys.argv) > 1 else 0
pid = find_pid(PROC)
if not pid:
    sys.exit(f"{PROC} not running")
h = k32.OpenProcess(PV_W, False, pid)
n = 0
try:
    addr = 0
    mbi = MBI()
    while addr < 0x7FFFFFFF0000:
        if not k32.VirtualQueryEx(h, ctypes.c_void_p(addr), ctypes.byref(mbi), ctypes.sizeof(mbi)):
            break
        base = mbi.BaseAddress or 0
        size = mbi.RegionSize
        if (LO <= base < HI and mbi.State == MEM_COMMIT and not (mbi.Protect & PAGE_GUARD)
                and (mbi.Protect & 0xFF) in READABLE and size < 0x20000000):
            data = rd_raw(h, base, size)
            if data:
                i = data.find(PREFIX)
                while i != -1:
                    a = base + i + SUPPORT_OFF
                    wr(h, a, bytes([newval & 0xFF, (newval >> 8) & 0xFF]))
                    n += 1
                    print(f"  wrote {newval} to {a:#x}")
                    i = data.find(PREFIX, i + 1)
        addr = base + size
    print(f"poked support slot -> {newval} in {n} game-data copies")
finally:
    k32.CloseHandle(h)
