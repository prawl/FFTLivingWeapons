#!/usr/bin/env python
"""READ-ONLY: RPM-dump live code regions from fft_enhanced.exe and disassemble with capstone.
Usage: python code_dump.py <hexaddr> <hexlen> [<hexaddr> <hexlen> ...]
Prints AT&T-free intel disasm with absolute addresses; marks known anchors."""
import ctypes as C
from ctypes import wintypes as W
import sys
import capstone

PROC = "fft_enhanced"
ANCHORS = {
    0x14028F7AC: "<== strlen walker (banner tag formatter, cmp byte [rdx+r8],0)",
    0x1405C9F00: "<== chunked reader (mov ecx,[rdx])",
    0x1405C9F02: "<== chunked reader (movzx r8d,[rdx+4])",
    0x1405C4FED: "<== June copier writer (mov [rax],r8)",
}

k32 = C.WinDLL("kernel32", use_last_error=True)
psapi = C.WinDLL("psapi", use_last_error=True)


class _PMC(C.Structure):
    _fields_ = [("cb", W.DWORD), ("pf", W.DWORD)] + [("a%d" % i, C.c_size_t) for i in range(8)]


def find_pid(name):
    arr = (W.DWORD * 4096)(); need = W.DWORD()
    psapi.EnumProcesses(arr, C.sizeof(arr), C.byref(need))
    best, bw = None, -1
    for i in range(need.value // C.sizeof(W.DWORD)):
        pid = arr[i]
        h = k32.OpenProcess(0x0410, False, pid)
        if not h:
            continue
        buf = C.create_unicode_buffer(260)
        if psapi.GetModuleBaseNameW(h, None, buf, 260) and buf.value.lower() == name.lower() + ".exe":
            p = _PMC(); p.cb = C.sizeof(p)
            psapi.GetProcessMemoryInfo(h, C.byref(p), p.cb)
            if p.a2 > bw:
                best, bw = pid, p.a2
        k32.CloseHandle(h)
    return best


def main():
    pid = find_pid(PROC)
    if not pid:
        print(f"{PROC}.exe not running"); sys.exit(1)
    h = k32.OpenProcess(0x0410, False, pid)
    print(f"pid {pid}")
    md = capstone.Cs(capstone.CS_ARCH_X86, capstone.CS_MODE_64)
    md.detail = False

    args = sys.argv[1:]
    for i in range(0, len(args), 2):
        base = int(args[i], 16)
        length = int(args[i + 1], 16)
        buf = C.create_string_buffer(length); got = C.c_size_t()
        ok = k32.ReadProcessMemory(h, C.c_void_p(base), buf, length, C.byref(got))
        if not ok or got.value != length:
            print(f"\n=== {base:#x} +{length:#x}: RPM FAILED (got {got.value}) ==="); continue
        print(f"\n=== dump {base:#x} .. {base + length:#x} ===")
        for ins in md.disasm(buf.raw, base):
            tag = ANCHORS.get(ins.address, "")
            print(f"{ins.address:#x}  {ins.bytes.hex():<24} {ins.mnemonic} {ins.op_str} {tag}")


if __name__ == "__main__":
    main()
