"""Find the per-unit battle COMMAND ARRAY: a tight cluster holding pages {8 Aim, 14 Steal,
2 Evasive Stance} (the unit's live command set) WITHOUT the passive ids (441 Auto-Potion /
480 Reequip-era / 488 Move+3 etc.) that mark the known loadout blocks.

Scan: every u16-LE 0x0008; require u16 2 AND u16 14 within +-0x20 bytes; exclude windows
containing u16 441/465/488 (passives). Print deduped contexts. Read-only.
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
WIN = 0x20

def rd_raw(h, addr, size):
    buf = ctypes.create_string_buffer(size)
    n = ctypes.c_size_t()
    if not k32.ReadProcessMemory(h, ctypes.c_void_p(addr), buf, size, ctypes.byref(n)):
        return None
    return buf.raw[: n.value]

def u16s(b):
    return {b[i] | (b[i + 1] << 8) for i in range(0, len(b) - 1, 2)} | \
           {b[i] | (b[i + 1] << 8) for i in range(1, len(b) - 1, 2)}

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
                i = data.find(b"\x08\x00")
                while i != -1:
                    lo = max(0, i - WIN)
                    win = data[lo: i + WIN]
                    vals = u16s(win)
                    if 2 in vals and 14 in vals and not vals & {441, 465, 480, 488, 358}:
                        ctx = bytes(data[lo: lo + 2 * WIN])
                        seen.setdefault(ctx, []).append(base + i)
                    i = data.find(b"\x08\x00", i + 2)
        addr = base + size
    print(f"{len(seen)} distinct cluster shapes:")
    for ctx, addrs in sorted(seen.items(), key=lambda kv: -len(kv[1]))[:18]:
        print(f"  x{len(addrs):3} first@{addrs[0]:#x}:")
        print("     " + " ".join(f"{v:02X}" for v in ctx))
finally:
    k32.CloseHandle(h)
