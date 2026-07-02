#!/usr/bin/env python
"""
SPIKE (READ-ONLY): high-rate (~2-4ms) change-trace of the global Acted byte against band HP
writes -- does Acted transiently dip to 0 at the damage-application instant?

WHY: Kobu (and Maim/Plague/Ricochet/Puppeteer) gate their HP-drop detection on a RAW
`Acted == 1` read at the 33ms tick that observes the drop. KillTracker debounces Acted
dips (UnfreezeTicks=3) because "the byte transiently drifts to 0 after a confirmed
action" -- but the signature modules read it raw. If the dip lands ON the damage tick,
the HP delta is consumed by the baseline update while the gate is shut: the strike is
silently lost forever (the 2026-07-02 Kobu raise-detection failure; see handoff).

Each sample = ONE RPM of the whole 49-slot combat-frame region (atomic-ish snapshot)
plus small reads of Acted / TurnQueue / ActorPtr. Logs CHANGES only:
  - Acted transitions (with timestamps)
  - any band slot's HP change  -- the line includes the live Acted value THAT sample
    plus the last 8 (t, acted) samples, so dip-vs-drop alignment is direct
  - TurnQueue tuple changes (lvl/team/hp/mhp) -- evidences the latch-tick churn window
  - ActorPtr changes (seat number)
  - current-brave changes on any slot (Kobu's write target: a `kobu:` fire shows here)

RUN: python tools\\probes\\acted_watch.py [--out FILE] [--hours 2]
Exits when fft_enhanced.exe exits, or after --hours. Read-only; safe to leave running.
"""
import argparse
import ctypes as C
from ctypes import wintypes as W
import sys
import time
from collections import deque
from datetime import datetime

PROC = "fft_enhanced"

# Offsets.cs mirror
ACTED       = 0x140782A8C
BATTLE_MODE = 0x1409069A0
TURN_QUEUE  = 0x1407832A0   # +0 lvl u16, +2 team u16, +0xC hp u16, +0x10 mhp u16
ACTOR_PTR   = 0x14186AF68

COMBAT_ANCHOR   = 0x141855CE0
COMBAT_STRIDE   = 0x200
BAND_ENTRY      = 0x1C
FRAME_READ_BASE = COMBAT_ANCHOR - 24 * COMBAT_STRIDE   # frame of band slot 0
BAND_SLOTS      = 49
REGION_SIZE     = BAND_SLOTS * COMBAT_STRIDE

A_LEVEL, A_BRAVE, A_BRAVE_CUR, A_FAITH = 0x0D, 0x0E, 0x0F, 0x10
A_HP, A_MAXHP = 0x14, 0x16

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
    ap = argparse.ArgumentParser()
    ap.add_argument("--out", default=None, help="trace file (default stdout)")
    ap.add_argument("--hours", type=float, default=2.0, help="max runtime")
    args = ap.parse_args()

    out = open(args.out, "a", buffering=1) if args.out else sys.stdout

    def log(msg):
        out.write(f"{datetime.now().strftime('%H:%M:%S.%f')[:-3]} {msg}\n")

    pid = find_pid(PROC)
    if not pid:
        log(f"{PROC}.exe not running"); sys.exit(1)
    h = k32.OpenProcess(0x0410, False, pid)
    if not h:
        log(f"OpenProcess failed (err {C.get_last_error()})"); sys.exit(1)
    log(f"watching pid {pid} -- Acted vs band-HP change trace (read-only)")

    region_buf = C.create_string_buffer(REGION_SIZE)
    small_buf = C.create_string_buffer(32)
    got = C.c_size_t()

    def rpm_into(addr, buf, n):
        ok = k32.ReadProcessMemory(h, C.c_void_p(addr), buf, n, C.byref(got))
        return bool(ok) and got.value == n

    prev_hp = [None] * BAND_SLOTS
    prev_cbr = [None] * BAND_SLOTS
    prev_acted = None
    prev_tq = None
    prev_ptr = None
    acted_ring = deque(maxlen=8)   # (t, acted) recent samples for drop-line context

    t0 = time.perf_counter()
    deadline = t0 + args.hours * 3600.0
    exit_ok = W.DWORD()

    while time.perf_counter() < deadline:
        if not k32.GetExitCodeProcess(h, C.byref(exit_ok)) or exit_ok.value != 259:  # STILL_ACTIVE
            log("game exited -- stopping"); break

        t = time.perf_counter() - t0

        acted = None
        if rpm_into(ACTED, small_buf, 1):
            acted = small_buf.raw[0]
        acted_ring.append((round(t, 4), acted))
        if acted != prev_acted:
            log(f"t=+{t:9.4f}s ACTED {prev_acted}->{acted}")
            prev_acted = acted

        if rpm_into(TURN_QUEUE, small_buf, 0x12):
            raw = small_buf.raw
            tq = (int.from_bytes(raw[0:2], "little"), int.from_bytes(raw[2:4], "little"),
                  int.from_bytes(raw[0xC:0xE], "little"), int.from_bytes(raw[0x10:0x12], "little"))
            if tq != prev_tq:
                log(f"t=+{t:9.4f}s TQ lvl/team/hp/mhp {prev_tq} -> {tq} (acted={acted})")
                prev_tq = tq

        if rpm_into(ACTOR_PTR, small_buf, 8):
            ptr = int.from_bytes(small_buf.raw[:8], "little")
            if ptr != prev_ptr:
                seat = (ptr - FRAME_READ_BASE) / COMBAT_STRIDE if ptr else -1
                log(f"t=+{t:9.4f}s ACTORPTR {prev_ptr and hex(prev_ptr)} -> {hex(ptr)} (seat {seat}, acted={acted})")
                prev_ptr = ptr

        if rpm_into(FRAME_READ_BASE, region_buf, REGION_SIZE):
            blob = region_buf.raw
            for s in range(BAND_SLOTS):
                e = s * COMBAT_STRIDE + BAND_ENTRY
                lvl = blob[e + A_LEVEL]
                if not (1 <= lvl <= 99):
                    prev_hp[s] = None; prev_cbr[s] = None
                    continue
                mhp = int.from_bytes(blob[e + A_MAXHP:e + A_MAXHP + 2], "little")
                if not (1 <= mhp < 2000):
                    prev_hp[s] = None; prev_cbr[s] = None
                    continue
                hp = int.from_bytes(blob[e + A_HP:e + A_HP + 2], "little")
                cbr = blob[e + A_BRAVE_CUR]
                if prev_hp[s] is not None and hp != prev_hp[s]:
                    log(f"t=+{t:9.4f}s slot {s:>2} HP {prev_hp[s]}->{hp}/{mhp}  ACTED={acted}  "
                        f"ring={list(acted_ring)}")
                if prev_cbr[s] is not None and cbr != prev_cbr[s]:
                    log(f"t=+{t:9.4f}s slot {s:>2} curBrave {prev_cbr[s]}->{cbr}  ACTED={acted}")
                prev_hp[s] = hp
                prev_cbr[s] = cbr

        time.sleep(0.002)

    log("watch ended")


if __name__ == "__main__":
    main()
