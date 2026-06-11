"""Find the LIVE battle-menu command block by diffing menu-closed vs menu-open scans.

Signature: 08 00 0E 00 0E 00 D0 00 05 00 05 00 (rec 8 Aim, rec 14 Steal, x2?, 0xD0, 5, 5).
Usage:
  python menu_diff.py save     # scan + save address list (run with the menu CLOSED)
  python menu_diff.py diff     # rescan + print NEW addresses (run with the menu OPEN)
  python menu_diff.py poke <hexaddr> <byteoff> <u16val>   # write a u16 into a candidate
"""
import ctypes
import ctypes.wintypes as wt
import json
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from ct_probe import PROC, PV_W, find_pid, k32, rd, wr

class MBI(ctypes.Structure):
    _fields_ = [("BaseAddress", ctypes.c_void_p), ("AllocationBase", ctypes.c_void_p),
                ("AllocationProtect", wt.DWORD), ("PartitionId", wt.WORD),
                ("RegionSize", ctypes.c_size_t), ("State", wt.DWORD),
                ("Protect", wt.DWORD), ("Type", wt.DWORD)]

MEM_COMMIT = 0x1000
PAGE_GUARD = 0x100
READABLE = {0x02, 0x04, 0x08, 0x20, 0x40, 0x80}
SIG = bytes([0x08, 0x00, 0x0E, 0x00, 0x0E, 0x00, 0xD0, 0x00, 0x05, 0x00, 0x05, 0x00])
SNAP = os.path.join(os.path.dirname(os.path.abspath(__file__)), "menu_snap.json")

def rd_raw(h, addr, size):
    buf = ctypes.create_string_buffer(size)
    n = ctypes.c_size_t()
    if not k32.ReadProcessMemory(h, ctypes.c_void_p(addr), buf, size, ctypes.byref(n)):
        return None
    return buf.raw[: n.value]

def scan(h):
    hits = []
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
                    hits.append(base + i)
                    i = data.find(SIG, i + 1)
        addr = base + size
    return hits

pid = find_pid(PROC)
if not pid:
    sys.exit(f"{PROC} not running")
h = k32.OpenProcess(PV_W, False, pid)
try:
    mode = sys.argv[1] if len(sys.argv) > 1 else "save"
    if mode == "save":
        hits = scan(h)
        json.dump([f"{a:#x}" for a in hits], open(SNAP, "w"))
        print(f"baseline: {len(hits)} signature copies saved")
    elif mode == "diff":
        old = set(json.load(open(SNAP)))
        hits = scan(h)
        new = [a for a in hits if f"{a:#x}" not in old]
        print(f"now {len(hits)} copies; NEW since baseline: {len(new)}")
        for a in new:
            ctx = rd_raw(h, a - 32, 80) or b""
            print(f"  NEW @ {a:#x}: " + " ".join(f"{v:02X}" for v in ctx))
    else:
        a = int(sys.argv[2], 16)
        off = int(sys.argv[3])
        val = int(sys.argv[4])
        wr(h, a + off, bytes([val & 0xFF, (val >> 8) & 0xFF]))
        ctx = rd_raw(h, a, 16)
        print(f"poked {a:#x}+{off} = {val}; block now: " + " ".join(f"{v:02X}" for v in ctx))
finally:
    k32.CloseHandle(h)
