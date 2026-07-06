"""LW-31 stage 3 premise checks (plan phase, 2026-07-06). Two questions, one probe:

1. jobs:  dump every valid band entry's job byte (+0x02) next to its level/hp/brave/faith,
   to live-verify the inherited ic-job-id-remap claim that HUMAN generic jobs read 74-92
   (0x4A-0x5C) in the band, before the Fist branch gates on it. Read-only.

2. alloc: prove a page can be planted within u32-forward range of the Attack record bases
   (the VirtualAlloc premise): for each given record base, VirtualAllocEx probing upward,
   report the granted address and its delta, then free it immediately. The game never sees
   the region; nothing of the game's is touched.

  python tools/probes/plant_alloc_probe.py jobs
  python tools/probes/plant_alloc_probe.py alloc <recordbase hexaddr ...>
"""
import ctypes
import ctypes.wintypes as wt
import sys

PROCESS_ALL = 0x0010 | 0x0020 | 0x0008 | 0x0400
MEM_COMMIT_RESERVE = 0x3000
MEM_RELEASE = 0x8000
PAGE_READWRITE = 0x04

BAND_READ_BASE = 0x141852CFC   # Offsets.BandReadBase (CombatAnchor 0x141855CE0 + 0x1C - 24*0x200)
COMBAT_STRIDE = 0x200
BAND_SLOTS = 49
A_JOB, A_LEVEL, A_BRAVE, A_FAITH, A_HP, A_MAXHP = 0x02, 0x0D, 0x0E, 0x10, 0x14, 0x16

k32 = ctypes.windll.kernel32
psapi = ctypes.windll.psapi
k32.VirtualAllocEx.restype = ctypes.c_void_p
k32.VirtualAllocEx.argtypes = [wt.HANDLE, ctypes.c_void_p, ctypes.c_size_t, wt.DWORD, wt.DWORD]


def find_pid(name=b"fft_enhanced.exe"):
    arr = (wt.DWORD * 4096)()
    needed = wt.DWORD()
    psapi.EnumProcesses(arr, ctypes.sizeof(arr), ctypes.byref(needed))
    for i in range(needed.value // ctypes.sizeof(wt.DWORD)):
        h = k32.OpenProcess(PROCESS_ALL, False, arr[i])
        if not h:
            continue
        buf = ctypes.create_string_buffer(260)
        if psapi.GetModuleBaseNameA(h, None, buf, 260) and buf.value.lower() == name:
            return arr[i], h
        k32.CloseHandle(h)
    return None, None


def rpm(h, addr, n):
    buf = ctypes.create_string_buffer(n)
    got = ctypes.c_size_t()
    ok = k32.ReadProcessMemory(h, ctypes.c_void_p(addr), buf, n, ctypes.byref(got))
    return buf.raw[:got.value] if ok else None


def cmd_jobs(h):
    print("slot  entry-addr    job   lvl brave faith    hp/maxhp")
    for s in range(BAND_SLOTS):
        addr = BAND_READ_BASE + s * COMBAT_STRIDE
        raw = rpm(h, addr, 0x18)
        if raw is None or len(raw) < 0x18:
            continue
        lvl = raw[A_LEVEL]
        if lvl < 1 or lvl > 99:
            continue
        br, fa = raw[A_BRAVE], raw[A_FAITH]
        hp = raw[A_HP] | (raw[A_HP + 1] << 8)
        maxhp = raw[A_MAXHP] | (raw[A_MAXHP + 1] << 8)
        if maxhp == 0 or maxhp >= 2000:
            continue
        job = raw[A_JOB]
        human = "HUMAN-RANGE" if 74 <= job <= 92 else "outside"
        print(f"  {s:2}  {addr:012X}  0x{job:02X}({job:3})  {lvl:3} {br:5} {fa:5}  {hp:4}/{maxhp:<4}  {human}")


def cmd_alloc(h, bases):
    for base in bases:
        granted = None
        hint = (base + 0x1000000 + 0xFFFF) & ~0xFFFF
        step = 0x1000000
        for _ in range(40):
            addr = k32.VirtualAllocEx(h, ctypes.c_void_p(hint), 0x1000, MEM_COMMIT_RESERVE, PAGE_READWRITE)
            if addr:
                granted = addr
                break
            hint += step
            step = min(step * 2, 0x40000000)
            if hint - base > 0xFFFF0000:
                break
        if granted is None:
            # last resort: let the OS choose anywhere, then range-check
            addr = k32.VirtualAllocEx(h, None, 0x1000, MEM_COMMIT_RESERVE, PAGE_READWRITE)
            granted = addr
        if granted:
            delta = granted - base
            ok = 0 < delta <= 0xFFFF0000
            print(f"base {base:012X}: granted {granted:012X} delta 0x{delta:X} in-u32-range={ok}")
            k32.VirtualFreeEx(h, ctypes.c_void_p(granted), 0, MEM_RELEASE)
        else:
            print(f"base {base:012X}: NO allocation granted (err {ctypes.get_last_error()})")


ROSTER_BASE = 0x1411A7D10   # Offsets.RosterBase
ROSTER_STRIDE = 0x258
R_SPRITE, R_JOB, R_LEVEL, R_RHAND, R_NAMEID = 0x00, 0x02, 0x1D, 0x14, 0x230


def cmd_roster(h):
    """Verify the Fist/monster gate's roster fields: SpriteSet(+0x00), Job(+0x02),
    identity(+0x230), next to level and right hand (docs/research/SPRITE_SWAP.md)."""
    print("slot  sprite  job          lvl  rhand   nameId")
    for s in range(24):
        b = ROSTER_BASE + s * ROSTER_STRIDE
        raw = rpm(h, b, 0x238)
        if raw is None or len(raw) < 0x238:
            continue
        lvl = raw[R_LEVEL]
        if lvl < 1 or lvl > 99:
            continue
        sprite, job = raw[R_SPRITE], raw[R_JOB]
        rhand = raw[R_RHAND] | (raw[R_RHAND + 1] << 8)
        nameid = raw[R_NAMEID] | (raw[R_NAMEID + 1] << 8)
        kind = "MONSTER" if sprite == 0x82 else "human"
        print(f"  {s:2}  0x{sprite:02X}   0x{job:02X}({job:3})  {lvl:3}  0x{rhand:04X}  0x{nameid:04X}  {kind}")


def main():
    pid, h = find_pid()
    if not h:
        print("game not running")
        sys.exit(1)
    print(f"pid {pid}")
    mode = sys.argv[1] if len(sys.argv) > 1 else "jobs"
    try:
        if mode == "jobs":
            cmd_jobs(h)
        elif mode == "roster":
            cmd_roster(h)
        elif mode == "alloc":
            cmd_alloc(h, [int(a, 16) for a in sys.argv[2:]])
        else:
            print(__doc__)
    finally:
        k32.CloseHandle(h)


if __name__ == "__main__":
    main()
