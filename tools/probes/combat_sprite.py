"""Write the COMBAT-struct SpriteSet byte (+0x00) on a live battle unit and watch.

The "live repaint is label-only" wall (LIVE_LEDGER / fftmp-live-repaint-shallow) was
established by writing +0x03 (JOB), then noting the body didn't follow. But the body is
driven by +0x00 (SpriteSet), a SEPARATE byte -- and job never drives the body anyway.
So a DIRECT +0x00 write on a live combat unit is UNTESTED. This probe tests it: does the
body re-skin live (maybe after the unit acts / the camera moves / a re-render), or is the
sprite scene-graph node truly welded at construction?

Combat units: BattleUnitsBase 0x141853CE0, stride 0x200. Fields (1.5):
  sprite +0x00 | job +0x03 | agency +0x05 (bit0x08=human) | lvl +0x29 | HPcur +0x30

Pure cross-process RPM/WPM (crash-safe; combat struct is NOT the save -- resets on battle
end / restart). Run while standing in a battle.

  python tools\\probes\\combat_sprite.py                 # scan slots (find enemy slot #)
  python tools\\probes\\combat_sprite.py set 9 0x1E      # slot 9 SpriteSet -> Agrias (one shot)
  python tools\\probes\\combat_sprite.py hold 9 0x1E 8   # write+hold for 8s (defeats per-frame revert)
"""

import ctypes
import ctypes.wintypes as w
import sys
import time

PROC = "fft_enhanced.exe"
BASE = 0x141853CE0
STRIDE = 0x200
N = 28
LEN = 0x60
OFF_SPRITE = 0x00
OFF_JOB = 0x03
OFF_AGENCY = 0x05
OFF_LEVEL = 0x29
OFF_HP = 0x30

k32 = ctypes.WinDLL("kernel32", use_last_error=True)
psapi = ctypes.WinDLL("psapi", use_last_error=True)
ACCESS = 0x0438  # QUERY_INFO | VM_OPERATION | VM_READ | VM_WRITE
_H = None


def _open():
    arr = (w.DWORD * 4096)()
    need = w.DWORD()
    if not psapi.EnumProcesses(arr, ctypes.sizeof(arr), ctypes.byref(need)):
        return None
    for i in range(need.value // ctypes.sizeof(w.DWORD)):
        h = k32.OpenProcess(ACCESS, False, arr[i])
        if not h:
            continue
        buf = ctypes.create_unicode_buffer(260)
        if psapi.GetModuleBaseNameW(h, None, buf, 260) and buf.value.lower() == PROC.lower():
            return h
        k32.CloseHandle(h)
    return None


def rpm(addr, n):
    buf = ctypes.create_string_buffer(n)
    got = ctypes.c_size_t()
    ok = k32.ReadProcessMemory(_H, ctypes.c_void_p(addr), buf, n, ctypes.byref(got))
    return buf.raw if ok and got.value == n else None


def wpm(addr, data):
    got = ctypes.c_size_t()
    ok = k32.WriteProcessMemory(_H, ctypes.c_void_p(addr), data, len(data), ctypes.byref(got))
    return bool(ok) and got.value == len(data)


def scan():
    print(f"combat units @ 0x{BASE:X} stride 0x{STRIDE:X}\n")
    hdr = f"{'slot':>4} {'addr':>11} {'who':>6} {'sprite':>6} {'job':>4} {'lvl':>3} {'hp':>5}"
    print(hdr)
    print("-" * len(hdr))
    for i in range(N):
        a = BASE + i * STRIDE
        d = rpm(a, LEN)
        if d is None:
            continue
        lvl, job, sprite, agency = d[OFF_LEVEL], d[OFF_JOB], d[OFF_SPRITE], d[OFF_AGENCY]
        hp = int.from_bytes(d[OFF_HP:OFF_HP + 2], "little")
        if not (1 <= lvl <= 99) or job == 0:
            continue
        who = "HUMAN" if (agency & 0x08) else "AI"
        print(f"{i:>4} 0x{a:09X} {who:>6} 0x{sprite:02X} 0x{job:02X} {lvl:>3} {hp:>5}")


def _addr(slot):
    return BASE + slot * STRIDE


def setsprite(slot, sprite):
    a = _addr(slot)
    d = rpm(a, LEN)
    if d is None:
        print(f"slot {slot} @ 0x{a:X} unreadable")
        return
    old = d[OFF_SPRITE]
    print(f"slot {slot} @ 0x{a:X}  SpriteSet 0x{old:02X} -> 0x{sprite:02X}  (job 0x{d[OFF_JOB]:02X})")
    print("  WRITTEN" if wpm(a + OFF_SPRITE, bytes([sprite])) else "  WRITE FAILED")
    print(f"  REVERT: python tools\\probes\\combat_sprite.py set {slot} 0x{old:02X}")
    print("  Now make the unit ACT / MOVE, or pan the camera, and watch the body.")


def hold(slot, sprite, secs):
    a = _addr(slot)
    d = rpm(a, LEN)
    if d is None:
        print(f"slot {slot} @ 0x{a:X} unreadable")
        return
    old = d[OFF_SPRITE]
    print(f"slot {slot}: holding SpriteSet 0x{old:02X} -> 0x{sprite:02X} for {secs}s "
          f"(act/move the unit now to force a re-render)")
    end = time.time() + secs
    n = 0
    while time.time() < end:
        wpm(a + OFF_SPRITE, bytes([sprite]))
        n += 1
    wpm(a + OFF_SPRITE, bytes([old]))  # restore
    print(f"  held {n} writes, restored to 0x{old:02X}")


def main():
    global _H
    _H = _open()
    if not _H:
        print(f"{PROC} not running")
        sys.exit(1)
    argv = sys.argv[1:]
    if argv and argv[0] == "set":
        setsprite(int(argv[1]), int(argv[2], 16))
    elif argv and argv[0] == "hold":
        hold(int(argv[1]), int(argv[2], 16), float(argv[3]) if len(argv) > 3 else 8.0)
    else:
        scan()


if __name__ == "__main__":
    main()
