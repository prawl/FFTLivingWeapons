#!/usr/bin/env python
"""LW-346 READ-ONLY live disassembly of a code range in the running game (capstone, x64).

Plain language: print the game's instructions at an address as they sit in memory right
now, so the copy-protected code (blind on disk) can be read without a debugger.
Writes nothing.
Usage: python tools/probes/lw346_live_disasm.py 0xSTART (+LEN | 0xEND) [--ptr]
  --ptr : 0xSTART holds an 8-byte pointer; disassemble at the pointed-to address instead.
"""
import ctypes as C
import ctypes.wintypes as W
import struct
import sys

from capstone import Cs, CS_ARCH_X86, CS_MODE_64

PROC = "fft_enhanced.exe"
k32 = C.WinDLL("kernel32", use_last_error=True)
PV = 0x0410


class PE32(C.Structure):
    _fields_ = [("dwSize", W.DWORD), ("cntUsage", W.DWORD), ("th32ProcessID", W.DWORD),
                ("th32DefaultHeapID", C.POINTER(C.c_ulong)), ("th32ModuleID", W.DWORD),
                ("cntThreads", W.DWORD), ("th32ParentProcessID", W.DWORD),
                ("pcPriClassBase", C.c_long), ("dwFlags", W.DWORD),
                ("szExeFile", C.c_char * 260)]


def find_pid(name):
    snap = k32.CreateToolhelp32Snapshot(2, 0)
    e = PE32(); e.dwSize = C.sizeof(PE32)
    pid = None
    if k32.Process32First(snap, C.byref(e)):
        while True:
            if name.lower() in e.szExeFile.decode(errors="ignore").lower():
                pid = e.th32ProcessID; break
            if not k32.Process32Next(snap, C.byref(e)): break
    k32.CloseHandle(snap)
    return pid


def rd(h, a, n):
    buf = (C.c_ubyte * n)(); g = C.c_size_t(0)
    if k32.ReadProcessMemory(h, C.c_void_p(a), buf, n, C.byref(g)) and g.value == n:
        return bytes(buf)
    # fall back to the readable prefix
    for m in range(n - 1, 0, -1):
        if k32.ReadProcessMemory(h, C.c_void_p(a), buf, m, C.byref(g)) and g.value == m:
            return bytes(buf)[:m]
    return None


def main():
    a = [x for x in sys.argv[1:] if not x.startswith("--")]
    start = int(a[0], 16)
    ln = int(a[1][1:], 16) if a[1].startswith("+") else int(a[1], 16) - start
    pid = find_pid(PROC)
    h = k32.OpenProcess(PV, False, pid)
    if "--ptr" in sys.argv:
        p = rd(h, start, 8)
        start = struct.unpack("<Q", p)[0]
        print("pointer -> 0x%X" % start)
    blob = rd(h, start, ln)
    if not blob:
        print("unreadable 0x%X" % start); return 2
    if len(blob) < ln:
        print("(readable prefix only: 0x%X bytes)" % len(blob))
    md = Cs(CS_ARCH_X86, CS_MODE_64)
    for ins in md.disasm(blob, start):
        print("%X  %-24s %s %s" % (ins.address, " ".join("%02X" % b for b in ins.bytes), ins.mnemonic, ins.op_str))
    return 0


if __name__ == "__main__":
    sys.exit(main())
