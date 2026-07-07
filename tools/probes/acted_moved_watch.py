#!/usr/bin/env python
r"""
SPIKE (read-only): learn how to detect a FULL WAIT (no act AND no move) for the Mushin signature.

v2: watches a BLOCK around acted. The old two-byte read used moved = acted+0x10 (0x140782A9C),
an UNVERIFIED 1.5 guess that read in lockstep with acted and never showed the "16 on a fresh
unmoved turn" the FFTHandsFree BattleTracker notes -- i.e. the real 1.5 move byte is elsewhere.
This scans 0x140782A80..0x140782AC0 (acted @+0x0C) so we can SEE which neighbor byte carries the
move/wait state and learn its live values.

  acted = 0x140782A8C  (== LivingWeapon Offsets.Acted, 1.5 CONFIRMED): rising edge = an action completed

PROTOCOL (you act; I read): on ONE unit, take three turns with a CLEAR ~10s gap between them so the
events separate in the log; note the rough order:
  (A) full WAIT  -- no move, no act (just end the turn)
  (B) MOVE only  -- move, then wait (no attack)
  (C) ATTACK     -- act (move or not)
On any change I print the timestamp, acted, the full block hex, and which offsets changed. We diff
the three turns to find the "did nothing" signature.

Usage:  python tools\probes\acted_moved_watch.py [secs=180] [hz=50]
Read-only; largest-working-set pid (2-proc trap). No writes.
"""
import ctypes as C
from ctypes import wintypes as W
import sys, time, threading

PROC = "fft_enhanced"
ACTED = 0x140782A8C          # 1.5 CONFIRMED (Offsets.Acted): rising edge = an action completed
BASE  = 0x140782A80          # scan a block AROUND acted to FIND the real 1.5 move/wait byte
LEN   = 0x40                 # 0x140782A80..0x140782AC0
ACTED_OFF = ACTED - BASE     # acted's index within the block (0x0C)

k32 = C.WinDLL("kernel32", use_last_error=True)
psapi = C.WinDLL("psapi", use_last_error=True)


class _PMC(C.Structure):
    _fields_ = [("cb", W.DWORD), ("pf", W.DWORD)] + [("a%d" % i, C.c_size_t) for i in range(8)]


def find_pid(name):
    arr = (W.DWORD * 4096)(); need = W.DWORD()
    psapi.EnumProcesses(arr, C.sizeof(arr), C.byref(need))
    best, bw = None, -1
    for i in range(need.value // C.sizeof(W.DWORD)):
        h = k32.OpenProcess(0x0410, False, arr[i])
        if not h:
            continue
        buf = C.create_unicode_buffer(260)
        if psapi.GetModuleBaseNameW(h, None, buf, 260) and buf.value.lower() == name.lower() + ".exe":
            p = _PMC(); p.cb = C.sizeof(p)
            psapi.GetProcessMemoryInfo(h, C.byref(p), p.cb)
            if p.a2 > bw:
                best, bw = arr[i], p.a2
        k32.CloseHandle(h)
    return best


def main():
    secs = int(sys.argv[1]) if len(sys.argv) > 1 else 180
    hz = int(sys.argv[2]) if len(sys.argv) > 2 else 50
    pid = find_pid(PROC)
    if not pid:
        print(f"{PROC}.exe not running"); sys.exit(1)
    h = k32.OpenProcess(0x0410, False, pid)   # QUERY_INFO | VM_READ
    print(f"pid {pid}; watching {LEN} bytes @0x{BASE:X}..0x{BASE+LEN:X} (acted @+0x{ACTED_OFF:02X}) for {secs}s\n")
    print("One unit, three turns with a gap. Press ENTER right as you do each one to drop a labeled")
    print("MARK (type wait / move / attack then ENTER to name it). Let it reach 'done.' or Ctrl+C.\n")

    def rd_block(addr, n):
        b = C.create_string_buffer(n); got = C.c_size_t()
        ok = k32.ReadProcessMemory(h, C.c_void_p(addr), b, n, C.byref(got))
        return bytearray(b.raw[:got.value]) if ok and got.value == n else None

    t0 = time.time()

    def mark_listener():
        n = 0
        for line in sys.stdin:                         # one iteration per ENTER
            n += 1
            label = line.strip() or f"MARK {n}"
            print(f"  [{time.time()-t0:6.2f}s] ===== {label.upper()} =====")
    threading.Thread(target=mark_listener, daemon=True).start()

    last = None
    while time.time() - t0 < secs:
        cur = rd_block(BASE, LEN)
        if cur is not None and cur != last:
            t = time.time() - t0
            changed = "" if last is None else "  changed: " + ", ".join(
                f"+0x{i:02X} {last[i]:02X}->{cur[i]:02X}" for i in range(LEN) if last[i] != cur[i])
            hexdump = " ".join(f"{cur[i]:02X}" for i in range(LEN))
            print(f"  [{t:6.2f}s] acted=0x{cur[ACTED_OFF]:02X} | {hexdump}{changed}")
            last = cur
        time.sleep(1.0 / hz)
    print("\ndone.")


if __name__ == "__main__":
    main()
