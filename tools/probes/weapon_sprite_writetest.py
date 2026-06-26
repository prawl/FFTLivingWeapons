#!/usr/bin/env python
"""
Weapon battle-sprite WRITE-TEST (WRITES MEMORY -- not a read-only probe). Prove the lever:
does writing the 2-byte weapon battle-sprite table actually change a weapon's swing graphic?

We proved ItemData byte 0x01 SpriteID does NOT drive the render. weapon_sprite_probe.py located the
real candidate -- the FFHacktics "weapon battle sprite and palette IDs" table (2 bytes/weapon, byte0
palette, byte1 Graphic ID) at 0x140785CF2, indexed by item id. Warbrand (id 67) carries gfx=3 (an
ODD index, invalid in the Sword section the engine now uses) -> the busted swing. This tool writes a
VALID Sword graphic (default gfx 6) into Warbrand's entry so we can SEE whether the swing fixes.

The table sits in the EXE image; the page may be read-only const data, so we VirtualProtectEx to
RW before writing (and report if that fails -- a failure is itself the finding: the table is
const and a runtime fix needs a different seam).

This is a TEST. It writes to the live game. The value persists until you reboot the game (EXE memory),
or use --restore. It targets exactly ONE 2-byte entry. Read-only siblings: weapon_sprite_probe.py.

USAGE (game booted; to SEE the swing, be in a battle with the weapon equipped and attack):
  python weapon_sprite_writetest.py                       # Warbrand id67 gfx -> 6 (persist)
  python weapon_sprite_writetest.py --id 67 --gfx 6       # explicit
  python weapon_sprite_writetest.py --id 67 --gfx 6 --pal 0xE0   # also set palette
  python weapon_sprite_writetest.py --id 67 --hold 180    # re-assert for 180s then auto-restore
  python weapon_sprite_writetest.py --id 67 --restore 3   # put the original gfx (3) back
  python weapon_sprite_writetest.py --base 0xADDR ...     # if the table moved (re-probe first)
"""
import ctypes as C
from ctypes import wintypes as W
import sys
import time

PROC = "FFT_enhanced"
WS_BASE_DEFAULT = 0x140785CF2   # weapon_sprite_probe.py located this; re-probe if the build changed
WS_REC = 2
WS_PAL = 0x00
WS_GFX = 0x01

k32 = C.WinDLL("kernel32", use_last_error=True)
psapi = C.WinDLL("psapi", use_last_error=True)
PROCESS_VM = 0x0010 | 0x0020 | 0x0008 | 0x0400  # VM_READ | VM_WRITE | VM_OPERATION | QUERY_INFO
PAGE_RW = 0x40  # PAGE_EXECUTE_READWRITE


class _PMC(C.Structure):
    _fields_ = [("cb", W.DWORD), ("PageFaultCount", W.DWORD),
                ("PeakWorkingSetSize", C.c_size_t), ("WorkingSetSize", C.c_size_t),
                ("QuotaPeakPagedPoolUsage", C.c_size_t), ("QuotaPagedPoolUsage", C.c_size_t),
                ("QuotaPeakNonPagedPoolUsage", C.c_size_t), ("QuotaNonPagedPoolUsage", C.c_size_t),
                ("PagefileUsage", C.c_size_t), ("PeakPagefileUsage", C.c_size_t)]


def _ws(pid):
    h = k32.OpenProcess(PROCESS_VM, False, pid)
    if not h:
        return 0
    p = _PMC(); p.cb = C.sizeof(p)
    ok = psapi.GetProcessMemoryInfo(h, C.byref(p), p.cb)
    k32.CloseHandle(h)
    return p.WorkingSetSize if ok else 0


def find_pid(name):
    class PE32(C.Structure):
        _fields_ = [("dwSize", W.DWORD), ("cntUsage", W.DWORD), ("th32ProcessID", W.DWORD),
                    ("th32DefaultHeapID", C.POINTER(C.c_ulong)), ("th32ModuleID", W.DWORD),
                    ("cntThreads", W.DWORD), ("th32ParentProcessID", W.DWORD),
                    ("pcPriClassBase", C.c_long), ("dwFlags", W.DWORD), ("szExeFile", C.c_char * 260)]
    snap = k32.CreateToolhelp32Snapshot(0x2, 0)
    e = PE32(); e.dwSize = C.sizeof(e); m = []
    if k32.Process32First(snap, C.byref(e)):
        while True:
            if name.lower() in e.szExeFile.decode(errors="ignore").lower():
                m.append(e.th32ProcessID)
            if not k32.Process32Next(snap, C.byref(e)):
                break
    k32.CloseHandle(snap)
    return max(m, key=_ws) if m else None


def rd(h, a, n):
    b = (C.c_ubyte * n)(); g = C.c_size_t(0)
    if k32.ReadProcessMemory(h, C.c_void_p(a), b, n, C.byref(g)) and g.value == n:
        return bytes(b)
    return None


def wr(h, a, data):
    """Write bytes, VirtualProtect-ing to RW first (const .rdata pages otherwise reject)."""
    old = W.DWORD(0)
    vp = k32.VirtualProtectEx(h, C.c_void_p(a), len(data), PAGE_RW, C.byref(old))
    buf = (C.c_ubyte * len(data))(*data)
    n = C.c_size_t(0)
    ok = k32.WriteProcessMemory(h, C.c_void_p(a), buf, len(data), C.byref(n))
    if vp:
        tmp = W.DWORD(0)
        k32.VirtualProtectEx(h, C.c_void_p(a), len(data), old, C.byref(tmp))
    return bool(ok and n.value == len(data)), bool(vp)


def main():
    argv = sys.argv[1:]

    def opt(name, default=None):
        return argv[argv.index(name) + 1] if name in argv else default

    iid = int(opt("--id", "67"))
    base = int(opt("--base", hex(WS_BASE_DEFAULT)), 16)
    gfx = opt("--gfx", "6")
    pal = opt("--pal")
    restore = opt("--restore")
    hold = opt("--hold")

    pid = find_pid(PROC)
    if not pid:
        print(f"{PROC} not running."); return
    h = k32.OpenProcess(PROCESS_VM, False, pid)
    if not h:
        print(f"OpenProcess failed (err {C.get_last_error()})."); return
    print(f"pid {pid} (largest working set)")

    entry = base + iid * WS_REC
    cur = rd(h, entry, WS_REC)
    if not cur:
        print(f"read fail @ 0x{entry:X} -- re-probe the table base with weapon_sprite_probe.py."); return
    print(f"id {iid} entry @ 0x{entry:X}: pal=0x{cur[WS_PAL]:02X} gfx={cur[WS_GFX]} (current)")

    if restore is not None:
        ok, vp = wr(h, entry + WS_GFX, [int(restore)])
        print(f"RESTORE gfx -> {int(restore)}: {'OK' if ok else 'WRITE FAILED'} (VirtualProtect {'ok' if vp else 'FAILED'})")
        k32.CloseHandle(h); return

    new_gfx = int(gfx)
    ok, vp = wr(h, entry + WS_GFX, [new_gfx])
    print(f"WRITE gfx {cur[WS_GFX]} -> {new_gfx}: {'OK' if ok else 'WRITE FAILED'} (VirtualProtect {'ok' if vp else 'FAILED'})")
    if pal is not None:
        okp, _ = wr(h, entry + WS_PAL, [int(pal, 16) if pal.lower().startswith('0x') else int(pal)])
        print(f"WRITE pal -> {pal}: {'OK' if okp else 'WRITE FAILED'}")
    if not ok:
        print("Write rejected -> the table is likely const/protected beyond VirtualProtect, or the page moved. "
              "That itself is the finding: a runtime fix would need a different seam (per-unit resolved sprite).")
        k32.CloseHandle(h); return

    chk = rd(h, entry, WS_REC)
    print(f"readback: pal=0x{chk[WS_PAL]:02X} gfx={chk[WS_GFX]}  (engine may re-read it on the next swing/battle)")

    if hold is not None:
        secs = int(hold)
        print(f"holding gfx={new_gfx} for {secs}s (re-asserting every 200ms). Attack with the weapon to observe...")
        end = secs * 5
        for _ in range(end):
            wr(h, entry + WS_GFX, [new_gfx])
            time.sleep(0.2)
        wr(h, entry + WS_GFX, [cur[WS_GFX]])
        print(f"hold over -> restored gfx to {cur[WS_GFX]}.")
    else:
        print(f"persisted (reboot reverts). To undo now: --restore {cur[WS_GFX]}")
    k32.CloseHandle(h)


if __name__ == "__main__":
    main()
