#!/usr/bin/env python
r"""
SPIKE (read-only): find a PER-UNIT "this unit moved / waited / acted on its OWN turn" byte for the
Mushin signature (Kiku-ichimonji). The global Acted flag (acted_moved_watch.py) only says "SOME unit
acted" and is blind to a passive turn; TurnTracker rides that same edge, so neither can see a unit
end its turn without acting. The engine tracks per-unit action state on the combat FRAME itself:
frame +0x1BB is a documented action-state byte (stamp_swap.py's "incoming-action marker"; the
UI_GREY_HOLD research's acted-state flag at CombatAnchor+0x1BB). This probe watches ONE chosen unit's
frame across three of its OWN turns to learn which byte(s) distinguish wait vs move vs attack.

We watch a FIXED combat slot (no ActorPtr following: the pointer is unreliable for player units and
parks on struck victims). Default slot 24 = CombatAnchor (Ramza in 1.5). Run the startup census, then
pass the slot of the unit you want to test as arg 1.

  FrameReadBase 0x141852CE0, stride 0x200 (Offsets.cs). frame(slot) = base + slot*0x200.
  In-frame ids: level +0x29, PA +0x3E, Speed +0x40. Watched window: frame +0x100..+0x200 (the
  action-state upper half; +0x1BB lives here). Low half is HP/CT/position/facing (noisy) -- excluded.

PROTOCOL (you act; I read): with the chosen unit, take three of ITS turns with a clear gap, pressing
ENTER as you do each (type wait / move / attack then ENTER to label it):
  (A) full WAIT  -- no move, no act        (B) MOVE only  -- move then wait        (C) ATTACK
On any change in the window I print the timestamp and which offsets flipped. We diff the three turns:
a byte that reads one value after WAIT, another after MOVE, another after ATTACK is Mushin's trigger
(and if WAIT and MOVE differ, the strict "stand still" version is back on the table).

Usage:  python tools\probes\wait_state_probe.py [slot=24] [secs=180] [hz=30]
Read-only; largest-working-set pid (2-proc trap). No writes.
"""
import ctypes as C
from ctypes import wintypes as W
import sys, time, threading

PROC = "fft_enhanced"
FRAME_BASE = 0x141852CE0     # Offsets.FrameReadBase (CombatAnchor 0x141855CE0 - 24*0x200)
STRIDE     = 0x200           # Offsets.CombatStride
WIN_OFF    = 0x100           # watch frame +0x100..+0x200 (action-state upper half; +0x1BB is here)
WIN_LEN    = 0x100
LEVEL_OFF, PA_OFF, SPEED_OFF = 0x29, 0x3E, 0x40

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
    slot = int(sys.argv[1]) if len(sys.argv) > 1 else 24
    secs = int(sys.argv[2]) if len(sys.argv) > 2 else 180
    hz   = int(sys.argv[3]) if len(sys.argv) > 3 else 30
    pid = find_pid(PROC)
    if not pid:
        print(f"{PROC}.exe not running"); sys.exit(1)
    h = k32.OpenProcess(0x0410, False, pid)   # QUERY_INFO | VM_READ

    def rd(addr, n):
        b = C.create_string_buffer(n); got = C.c_size_t()
        ok = k32.ReadProcessMemory(h, C.c_void_p(addr), b, n, C.byref(got))
        return bytearray(b.raw[:got.value]) if ok and got.value == n else None

    def u8(addr):
        b = rd(addr, 1)
        return b[0] if b else None

    # Census: every populated combat slot, so you can pick your test unit's slot.
    print(f"pid {pid}; combat-slot census (base 0x{FRAME_BASE:X}, stride 0x{STRIDE:X}):")
    for s in range(0, 32):
        f = FRAME_BASE + s * STRIDE
        lvl = u8(f + LEVEL_OFF)
        if lvl is not None and 1 <= lvl <= 99:
            print(f"  slot {s:2d}  frame 0x{f:X}  level {lvl:3d}  PA {u8(f + PA_OFF)}  Speed {u8(f + SPEED_OFF)}")

    watch = FRAME_BASE + slot * STRIDE
    base = watch + WIN_OFF
    lvl = u8(watch + LEVEL_OFF)
    print(f"\nwatching slot {slot} frame 0x{watch:X} (level {lvl}, PA {u8(watch + PA_OFF)}) window +0x{WIN_OFF:X}..+0x{WIN_OFF+WIN_LEN:X} for {secs}s")
    if lvl is None or not (1 <= lvl <= 99):
        print("  WARNING: that slot has no sane unit; pick a slot from the census above (arg 1).")
    print("Take three of THIS unit's turns with a gap; press ENTER as you do each")
    print("(type wait / move / attack then ENTER to name it). Let it reach 'done.' or Ctrl+C.\n")

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
        cur = rd(base, WIN_LEN)
        if cur is not None and cur != last:
            t = time.time() - t0
            if last is None:
                print(f"  [{t:6.2f}s] baseline captured")
            else:
                diffs = ", ".join(f"+0x{WIN_OFF+i:03X} {last[i]:02X}->{cur[i]:02X}"
                                  for i in range(WIN_LEN) if last[i] != cur[i])
                print(f"  [{t:6.2f}s] {diffs}")
            last = cur
        time.sleep(1.0 / hz)
    print("\ndone.")


if __name__ == "__main__":
    main()
