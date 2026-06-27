#!/usr/bin/env python
"""Watch the delayed-action status bits (Charging 0x08 / Jump 0x04 / Performing 0x01) on every
live band unit and stream a timestamped line on each TRANSITION, plus HP->0 deaths.

WHY: KillTracker.Delayed.cs tracks ADelayedActionMask = 0x0C (Jump 0x04 | Charging 0x08) at
band-relative +0x45. Jump 0x04 is PROVEN LIVE (2026-06-26); Charging 0x08 is marked
live-UNVERIFIED. This probe confirms that a CHARGED summon/spell sets 0x08 at cast and clears it
at landing -- the premise the cross-turn untracked-summoner attribution fix rests on. Correlate the
0x08 1->0 edge with the victim's HP->0 (the summon landing AND the kill maturing the SAME instant,
cross-turn from the caster's acted period -- that is the leak signature seen live: summoner casts,
31s later the kill lands during enemy turns, the next player latch -- Chaos Blade -- absorbs it).

USAGE (game running, in a live battle):
  python charging_probe.py [seconds=120] [hz=20]
  -> start it, then on a SUMMONER/charged caster's turn cast a charged spell. Watch for:
       slot S fp(lvlL,brB,faF) ... Charging 0->1     (at cast)
       slot S fp(lvlL,brB,faF) ... Charging 1->0     (at landing)
       slot V ... HP n->0 (DIED)                     (same instant as the landing = the kill)

Read-only (RPM only, never writes). Cribs find_pid/rd from the proven probes; find_pid targets the
LARGEST working set so a duplicate FFT_enhanced instance can't silently feed a dead process.
"""
import ctypes as C
from ctypes import wintypes as W
import sys, time

PROC = "FFT_enhanced"

# Band: Offsets.BandReadBase = CombatAnchor + BandEntry - 24*CombatStride (n=-24 anchor).
ANCHOR = 0x14184F890   # Offsets.CombatAnchor
STRIDE = 0x200         # Offsets.CombatStride
ENTRY  = 0x1C          # Offsets.BandEntry
BASE   = ANCHOR + ENTRY - 24 * STRIDE
SLOTS  = 49

# band-relative offsets (== static-array A* layout; status byte = PSX current-status byte 0)
O_LVL, O_BRAVE, O_FAITH = 0x0D, 0x0E, 0x10
O_HP, O_MAXHP = 0x14, 0x16
O_GX, O_GY = 0x33, 0x34
O_STATUS = 0x45        # +0x45..0x49 status bitfield; byte +0x45 holds the delayed-action bits

# bits within +0x45 (from FFTHandsFree StatusDecoder map, mirrored in status_probe.py)
BIT_CHARGING = 0x08
BIT_JUMP     = 0x04
BIT_PERFORM  = 0x01
WATCH_BITS = [(BIT_CHARGING, "Charging"), (BIT_JUMP, "Jump"), (BIT_PERFORM, "Performing")]

k32 = C.WinDLL("kernel32", use_last_error=True)
psapi = C.WinDLL("psapi", use_last_error=True)
PV = 0x0010 | 0x0008 | 0x0400          # VM_READ | VM_OPERATION | QUERY_INFORMATION


class _PMC(C.Structure):
    _fields_ = [("cb", W.DWORD), ("PageFaultCount", W.DWORD),
                ("PeakWorkingSetSize", C.c_size_t), ("WorkingSetSize", C.c_size_t),
                ("QuotaPeakPagedPoolUsage", C.c_size_t), ("QuotaPagedPoolUsage", C.c_size_t),
                ("QuotaPeakNonPagedPoolUsage", C.c_size_t), ("QuotaNonPagedPoolUsage", C.c_size_t),
                ("PagefileUsage", C.c_size_t), ("PeakPagefileUsage", C.c_size_t)]


def _working_set(pid):
    h = k32.OpenProcess(0x0400 | 0x0010, False, pid)
    if not h:
        return 0
    pmc = _PMC(); pmc.cb = C.sizeof(pmc)
    ok = psapi.GetProcessMemoryInfo(h, C.byref(pmc), pmc.cb)
    k32.CloseHandle(h)
    return pmc.WorkingSetSize if ok else 0


def find_pid(name):
    # Largest working set = the real rendered game (a duplicate ~630MB instance silently ate
    # every probe read for ~10 turns once -- see dualgun_probe.py / dual-gun memory).
    class PE32(C.Structure):
        _fields_ = [("dwSize", W.DWORD), ("cntUsage", W.DWORD), ("th32ProcessID", W.DWORD),
                    ("th32DefaultHeapID", C.POINTER(C.c_ulong)), ("th32ModuleID", W.DWORD),
                    ("cntThreads", W.DWORD), ("th32ParentProcessID", W.DWORD),
                    ("pcPriClassBase", C.c_long), ("dwFlags", W.DWORD), ("szExeFile", C.c_char * 260)]
    snap = k32.CreateToolhelp32Snapshot(0x2, 0)
    e = PE32(); e.dwSize = C.sizeof(e); matches = []
    if k32.Process32First(snap, C.byref(e)):
        while True:
            if name.lower() in e.szExeFile.decode(errors="ignore").lower():
                matches.append(e.th32ProcessID)
            if not k32.Process32Next(snap, C.byref(e)):
                break
    k32.CloseHandle(snap)
    return max(matches, key=_working_set) if matches else None


def rd(h, a, n):
    b = (C.c_ubyte * n)(); g = C.c_size_t(0)
    if k32.ReadProcessMemory(h, C.c_void_p(a), b, n, C.byref(g)) and g.value == n:
        return bytes(b)
    return None


def valid(b):
    if b is None:
        return False
    mhp = b[O_MAXHP] | (b[O_MAXHP + 1] << 8)
    lvl, br, fa = b[O_LVL], b[O_BRAVE], b[O_FAITH]
    return 1 <= mhp < 2000 and 1 <= lvl <= 99 and 1 <= br <= 100 and 1 <= fa <= 100


def main():
    try:
        sys.stdout.reconfigure(line_buffering=True)   # flush each line so a redirected run streams live
    except Exception:
        pass
    secs = float(sys.argv[1]) if len(sys.argv) > 1 else 120
    hz = float(sys.argv[2]) if len(sys.argv) > 2 else 20
    pid = find_pid(PROC)
    if not pid:
        sys.exit(f"{PROC} not running")
    h = k32.OpenProcess(PV, False, pid)
    if not h:
        sys.exit(f"OpenProcess failed err={C.get_last_error()}")
    print(f"pid={pid} band@{BASE:012X} watching {SLOTS} slots for {secs:.0f}s @ {hz:.0f}Hz")
    print("Cast a CHARGED summon/spell now. Watching Charging(0x08)/Jump(0x04)/Performing(0x01) "
          "edges + deaths...\n")

    prev_status = [None] * SLOTS
    prev_hp = [None] * SLOTS
    dt = 1.0 / hz
    t0 = time.time()
    end = t0 + secs
    try:
        while time.time() < end:
            now = time.time() - t0
            for s in range(SLOTS):
                b = rd(h, BASE + s * STRIDE, 0x60)
                if not valid(b):
                    prev_status[s] = None
                    prev_hp[s] = None
                    continue
                st = b[O_STATUS]
                hp = b[O_HP] | (b[O_HP + 1] << 8)
                mhp = b[O_MAXHP] | (b[O_MAXHP + 1] << 8)
                lvl, br, fa = b[O_LVL], b[O_BRAVE], b[O_FAITH]
                gx, gy = b[O_GX], b[O_GY]
                fp = f"fp(lvl{lvl},br{br},fa{fa})"
                pos = f"({gx},{gy})"

                ps = prev_status[s]
                if ps is not None and st != ps:
                    for mask, name in WATCH_BITS:
                        if (st & mask) != (ps & mask):
                            edge = "0->1" if (st & mask) else "1->0"
                            print(f"[t={now:6.2f}s] slot {s:2} {fp} hp={hp}/{mhp} pos={pos} "
                                  f"+45={st:02X}: {name} {edge}")
                prev_status[s] = st

                ph = prev_hp[s]
                if ph is not None and ph > 0 and hp == 0:
                    print(f"[t={now:6.2f}s] slot {s:2} {fp} pos={pos} HP {ph}->0 (DIED)")
                prev_hp[s] = hp
            time.sleep(dt)
    except KeyboardInterrupt:
        print("\n(stopped)")
    finally:
        k32.CloseHandle(h)
    print("\ndone.")


if __name__ == "__main__":
    main()
