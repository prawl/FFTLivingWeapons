"""Dump the battle-state sentinels Engine reads every tick, from the live game.

LW-41: addresses come from LivingWeapon/Offsets.cs via tools/lib/offsets.py. The original
hardcoded the pre-1.5 copies and fed garbage sentinels (battleMode=0, slot9=0x1) into the
LW-40 live incident, nearly misdirecting the diagnosis.

Usage:
  python tools/probes/sentinel_probe.py             # game running: dump the sentinels
  python tools/probes/sentinel_probe.py --selftest  # no game: parser + Offsets.cs shape check
"""
import ctypes, ctypes.wintypes as w, struct, sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from lib import offsets

if "--selftest" in sys.argv:
    offsets.selftest()
    sys.exit(0)

SLOT0, SLOT9, BATTLE_MODE, EVENT_ID, PAUSE, SUBMENU = offsets.require(
    ["Slot0", "Slot9", "BattleMode", "EventId", "PauseFlag", "SubmenuFlag"])

PROCESS_VM_READ = 0x0010
PROCESS_QUERY_INFORMATION = 0x0400
k32 = ctypes.windll.kernel32
psapi = ctypes.windll.psapi

def find_pid(name):
    arr = (w.DWORD * 4096)()
    needed = w.DWORD()
    psapi.EnumProcesses(ctypes.byref(arr), ctypes.sizeof(arr), ctypes.byref(needed))
    for i in range(needed.value // ctypes.sizeof(w.DWORD)):
        h = k32.OpenProcess(PROCESS_QUERY_INFORMATION | PROCESS_VM_READ, False, arr[i])
        if not h: continue
        buf = ctypes.create_unicode_buffer(260)
        if psapi.GetModuleBaseNameW(h, None, buf, 260) and buf.value.lower() == name.lower():
            return arr[i], h
        k32.CloseHandle(h)
    return None, None

pid, h = find_pid("fft_enhanced.exe")
if not h:
    print("game not running"); sys.exit(1)

def rpm(addr, n):
    buf = ctypes.create_string_buffer(n)
    got = ctypes.c_size_t()
    if not k32.ReadProcessMemory(h, ctypes.c_void_p(addr), buf, n, ctypes.byref(got)) or got.value != n:
        return None
    return buf.raw

def u32(a):
    b = rpm(a, 4); return struct.unpack('<I', b)[0] if b else None
def u16(a):
    b = rpm(a, 2); return struct.unpack('<H', b)[0] if b else None
def u8(a):
    b = rpm(a, 1); return b[0] if b else None

def fmt(v, hexed=False):
    if v is None: return "unreadable"
    return f"{v:#x}" if hexed else str(v)

print(f"pid={pid} (addresses from Offsets.cs)")
print(f"slot0      = {fmt(u32(SLOT0), hexed=True)}   @ {SLOT0:#x}")
print(f"slot9      = {fmt(u32(SLOT9), hexed=True)}   @ {SLOT9:#x}")
print(f"battleMode = {fmt(u8(BATTLE_MODE))}   @ {BATTLE_MODE:#x}")
print(f"eventId    = {fmt(u16(EVENT_ID))}   @ {EVENT_ID:#x}")
print(f"pauseFlag  = {fmt(u8(PAUSE))}   @ {PAUSE:#x}")
print(f"submenu    = {fmt(u8(SUBMENU))}   @ {SUBMENU:#x}")
