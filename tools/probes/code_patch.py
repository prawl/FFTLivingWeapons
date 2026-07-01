"""Safe cross-process code patcher for FFT_enhanced.exe (live RE probe tool).

Why this exists: FFTHandsFree's `write_byte` bridge verb calls
GameMemoryScanner.WriteByte, which is a RAW pointer write (`*(byte*)addr = val`)
with NO VirtualProtect. Pointed at a read-only .xcode code page it throws an
uncatchable access violation and CRASHES THE GAME (learned the hard way 2026-07-01).

This patches code the way a debugger does -- from ANOTHER process, so an AV cannot
occur in our address space, and the target only ever sees valid bytes:
    OpenProcess -> VirtualProtectEx(PAGE_EXECUTE_READWRITE) -> WriteProcessMemory
    -> VirtualProtectEx(restore old) -> ReadProcessMemory(verify)

The exe is image-base 0x140000000 with NO ASLR, so a VA == the live address.

Usage:
  python code_patch.py read  <va_hex> [len]           # ReadProcessMemory, hex dump (SAFE)
  python code_patch.py patch <va_hex> <bytes_hex>      # guarded write, then verify
      e.g. patch 14030BA10 B4                          # Counter id 0x1BA -> 0x1B4
  python code_patch.py patch 14030BA10 B4 --expect BA  # only write if current==expect
Env: FFT_PID overrides the auto-picked pid (largest-working-set FFT_enhanced.exe).
"""
import ctypes
import sys
from ctypes import wintypes

k32 = ctypes.WinDLL("kernel32", use_last_error=True)

PROCESS_VM_OPERATION = 0x0008
PROCESS_VM_READ = 0x0010
PROCESS_VM_WRITE = 0x0020
PROCESS_QUERY_INFORMATION = 0x0400
PAGE_EXECUTE_READWRITE = 0x40

k32.OpenProcess.restype = wintypes.HANDLE
k32.OpenProcess.argtypes = [wintypes.DWORD, wintypes.BOOL, wintypes.DWORD]
k32.CloseHandle.argtypes = [wintypes.HANDLE]
k32.ReadProcessMemory.argtypes = [wintypes.HANDLE, wintypes.LPCVOID, wintypes.LPVOID,
                                  ctypes.c_size_t, ctypes.POINTER(ctypes.c_size_t)]
k32.WriteProcessMemory.argtypes = [wintypes.HANDLE, wintypes.LPVOID, wintypes.LPCVOID,
                                   ctypes.c_size_t, ctypes.POINTER(ctypes.c_size_t)]
k32.VirtualProtectEx.argtypes = [wintypes.HANDLE, wintypes.LPVOID, ctypes.c_size_t,
                                 wintypes.DWORD, ctypes.POINTER(wintypes.DWORD)]


def find_pid():
    import os
    if os.environ.get("FFT_PID"):
        return int(os.environ["FFT_PID"])
    import subprocess
    # tasklist -> pick FFT_enhanced.exe with the largest memory (the real game
    # instance; matches the dual-gun "largest working set" convention).
    out = subprocess.check_output(
        ["tasklist", "/fi", "imagename eq FFT_enhanced.exe", "/fo", "csv", "/nh"],
        text=True, errors="ignore")
    best_pid, best_mem = None, -1
    for line in out.splitlines():
        parts = [p.strip('"') for p in line.split('","')]
        if len(parts) >= 5 and parts[0].lower().startswith("fft_enhanced"):
            pid = int(parts[1])
            mem = int(parts[4].replace(",", "").replace(" K", "").replace("K", "").strip() or 0)
            if mem > best_mem:
                best_pid, best_mem = pid, mem
    if best_pid is None:
        raise SystemExit("FFT_enhanced.exe not running")
    return best_pid


def open_proc(pid):
    access = (PROCESS_VM_OPERATION | PROCESS_VM_READ | PROCESS_VM_WRITE
              | PROCESS_QUERY_INFORMATION)
    h = k32.OpenProcess(access, False, pid)
    if not h:
        raise SystemExit(f"OpenProcess({pid}) failed err={ctypes.get_last_error()} "
                         f"(run elevated?)")
    return h


def rpm(h, va, n):
    buf = (ctypes.c_ubyte * n)()
    got = ctypes.c_size_t(0)
    ok = k32.ReadProcessMemory(h, ctypes.c_void_p(va), buf, n, ctypes.byref(got))
    if not ok:
        raise SystemExit(f"ReadProcessMemory @0x{va:X} failed err={ctypes.get_last_error()}")
    return bytes(buf[:got.value])


def wpm_guarded(h, va, data):
    n = len(data)
    old = wintypes.DWORD(0)
    if not k32.VirtualProtectEx(h, ctypes.c_void_p(va), n, PAGE_EXECUTE_READWRITE,
                                ctypes.byref(old)):
        raise SystemExit(f"VirtualProtectEx @0x{va:X} failed err={ctypes.get_last_error()}")
    try:
        buf = (ctypes.c_ubyte * n)(*data)
        wrote = ctypes.c_size_t(0)
        if not k32.WriteProcessMemory(h, ctypes.c_void_p(va), buf, n, ctypes.byref(wrote)):
            raise SystemExit(f"WriteProcessMemory @0x{va:X} failed err={ctypes.get_last_error()}")
    finally:
        tmp = wintypes.DWORD(0)
        k32.VirtualProtectEx(h, ctypes.c_void_p(va), n, old.value, ctypes.byref(tmp))
    # FlushInstructionCache so the CPU re-fetches the patched bytes
    k32.FlushInstructionCache(h, ctypes.c_void_p(va), n)


def hexs(b):
    return " ".join(f"{x:02X}" for x in b)


def main():
    a = sys.argv[1:]
    if not a or a[0] in ("-h", "--help"):
        print(__doc__)
        return
    pid = find_pid()
    h = open_proc(pid)
    try:
        if a[0] == "read":
            va = int(a[1], 16)
            n = int(a[2]) if len(a) > 2 else 5
            print(f"pid={pid} read 0x{va:X} [{n}] = {hexs(rpm(h, va, n))}")
        elif a[0] == "patch":
            va = int(a[1], 16)
            data = bytes(int(a[2][i:i + 2], 16) for i in range(0, len(a[2]), 2))
            expect = None
            if "--expect" in a:
                exs = a[a.index("--expect") + 1]
                expect = bytes(int(exs[i:i + 2], 16) for i in range(0, len(exs), 2))
            before = rpm(h, va, len(data))
            print(f"pid={pid} before 0x{va:X} = {hexs(before)}")
            if expect is not None and before != expect:
                raise SystemExit(f"ABORT: current {hexs(before)} != --expect {hexs(expect)}")
            wpm_guarded(h, va, data)
            after = rpm(h, va, len(data))
            print(f"        after  0x{va:X} = {hexs(after)}  "
                  f"({'OK' if after == data else 'MISMATCH!'})")
        else:
            print("unknown subcommand; see --help")
    finally:
        k32.CloseHandle(h)


if __name__ == "__main__":
    main()
