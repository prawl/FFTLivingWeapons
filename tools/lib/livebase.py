"""Resolve the live image base of the running game, so a probe can read ASLR-relocated pins.

LW-389 (opened 2026-10-15): the Denuvo-free 1.5.2 build loads at a random base, while
tools/lib/offsets.py returns PREFERRED-BASE absolutes (0x140000000 + RVA), exactly as
Offsets.cs documents them. Every external probe therefore needs the same one-line fix: find
fft_enhanced.exe, read its module base, and rebase each pin by (live base - 0x140000000).
This module is that one shared place. It is Win32-only (ctypes over psapi/kernel32) and
read-only: it opens the process with query + read rights and never writes.

Usage:
    from lib.livebase import open_game, rebase
    pid, handle, base = open_game()          # raises SystemExit if the game is not running
    slot0_live = rebase(SLOT0_PREFERRED, base)
"""
import ctypes
import ctypes.wintypes as w

PREFERRED_BASE = 0x140000000
EXE_NAME = "fft_enhanced.exe"

PROCESS_VM_READ = 0x0010
PROCESS_QUERY_INFORMATION = 0x0400
LIST_MODULES_ALL = 0x03

_k32 = ctypes.windll.kernel32
_psapi = ctypes.windll.psapi

# Explicit 64-bit signatures: without these, ctypes passes HANDLE/HMODULE values as c_int and a
# 64-bit module handle overflows ("int too long to convert", first run 2026-10-16).
_k32.OpenProcess.argtypes = [w.DWORD, w.BOOL, w.DWORD]
_k32.OpenProcess.restype = w.HANDLE
_k32.CloseHandle.argtypes = [w.HANDLE]
_psapi.EnumProcesses.argtypes = [ctypes.c_void_p, w.DWORD, ctypes.POINTER(w.DWORD)]
_psapi.EnumProcessModulesEx.argtypes = [w.HANDLE, ctypes.c_void_p, w.DWORD, ctypes.POINTER(w.DWORD), w.DWORD]
_psapi.GetModuleBaseNameW.argtypes = [w.HANDLE, w.HMODULE, w.LPWSTR, w.DWORD]
_psapi.GetModuleBaseNameW.restype = w.DWORD


class _ModuleInfo(ctypes.Structure):
    _fields_ = [("lpBaseOfDll", ctypes.c_void_p),
                ("SizeOfImage", w.DWORD),
                ("EntryPoint", ctypes.c_void_p)]


_psapi.GetModuleInformation.argtypes = [w.HANDLE, w.HMODULE, ctypes.POINTER(_ModuleInfo), w.DWORD]


def _find_pid(name):
    arr = (w.DWORD * 4096)()
    needed = w.DWORD()
    _psapi.EnumProcesses(ctypes.byref(arr), ctypes.sizeof(arr), ctypes.byref(needed))
    for i in range(needed.value // ctypes.sizeof(w.DWORD)):
        h = _k32.OpenProcess(PROCESS_QUERY_INFORMATION | PROCESS_VM_READ, False, arr[i])
        if not h:
            continue
        buf = ctypes.create_unicode_buffer(260)
        if _psapi.GetModuleBaseNameW(h, None, buf, 260) and buf.value.lower() == name.lower():
            return arr[i], h
        _k32.CloseHandle(h)
    return None, None


def module_base(handle, name=EXE_NAME):
    """The load address of the main exe module in the process behind `handle`, or None.
    Enumerates every module (LIST_MODULES_ALL) and matches on base name, so a renamed
    launcher wrapper cannot hand back the wrong image."""
    count = 512
    mods = (w.HMODULE * count)()
    needed = w.DWORD()
    if not _psapi.EnumProcessModulesEx(handle, mods, ctypes.sizeof(mods), ctypes.byref(needed), LIST_MODULES_ALL):
        return None
    for i in range(min(count, needed.value // ctypes.sizeof(w.HMODULE))):
        buf = ctypes.create_unicode_buffer(260)
        if not _psapi.GetModuleBaseNameW(handle, mods[i], buf, 260):
            continue
        if buf.value.lower() != name.lower():
            continue
        info = _ModuleInfo()
        if _psapi.GetModuleInformation(handle, mods[i], ctypes.byref(info), ctypes.sizeof(info)):
            return info.lpBaseOfDll
    return None


def open_game(name=EXE_NAME):
    """(pid, read-only handle, live image base). Exits with a plain message when the game is
    not running or its base cannot be read, so a probe never falls through to preferred-base
    addresses on a relocated image."""
    pid, h = _find_pid(name)
    if not h:
        raise SystemExit("game not running")
    base = module_base(h, name)
    if base is None:
        raise SystemExit(f"could not read the module base of {name} (pid {pid})")
    return pid, h, base


def rebase(preferred_addr, base):
    """Map a preferred-base absolute (as Offsets.cs writes them) onto the live image."""
    return preferred_addr - PREFERRED_BASE + base
