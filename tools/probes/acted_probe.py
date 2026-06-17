#!/usr/bin/env python
"""
Acted-byte + turn-boundary probe (FFT:IC 1.5). Read-only RPM -- cannot crash the game.

WHY: a non-Huntress character's hit refreshed Maim and logged [w:89] (Ramza's Huntress) because
KillTracker's actor latch went STALE -- the second character's acted-period never re-latched, so
LastPlayerMainHand stayed at Ramza's weapon. KillTracker only re-latches the next actor after the
"acted-falling edge": Offsets.Acted reads non-1 for UnfreezeTicks(3) consecutive ticks. The live log
shows Acted bouncing 1/0/255, which can keep resetting that counter so the latch never unfreezes
between turns. Acted (0x140782A8C) was only REGION-PREDICTED in the 1.5 port (Offsets.cs:15 "VERIFY"),
not differentially confirmed -- so the address may be wrong OR the semantics changed.

WHAT THIS ANSWERS:
  1. Does Acted read a CLEAN 1-while-a-unit-acts / 0-otherwise, or does it flicker 1/0/255 garbage?
  2. Between two units' turns, does Acted ever stay non-1 for >=3 straight ticks (the unfreeze)?
     -> "max non-1 streak" in the summary. If it never reaches 3, the latch never unfreezes = the bug.
  3. Does the TURN-QUEUE active unit (lvl/hp/maxHp/team) change cleanly when the turn passes from
     Ramza to the second character? If yes, the active-unit CHANGE is a robust latch-reset signal
     to replace the flaky Acted edge.
Also dumps the 0x20-byte window around Acted at start + on each active-unit change, so if 0x140782A8C
is the wrong byte a cleaner "is-acting" flag nearby can be spotted.

USAGE (game running, in a live battle with Ramza + a 2nd character):
  python tools\\probes\\acted_probe.py [seconds=150] [hz=20]
Then in-game: act with RAMZA (the Huntress), end his turn; then act with the SECOND character
(attack the same enemy); let an enemy take a turn too. Watch the printed transitions.
"""
import ctypes as C
from ctypes import wintypes as W
import sys
import time

PROC = "FFT_enhanced"

# --- 1.5 addresses (Offsets.cs) ---
ACTED       = 0x140782A8C   # Offsets.Acted (u8) -- the byte under investigation
BATTLE_MODE = 0x1409069A0   # Offsets.BattleMode (u8): 2/3/4 = on-field, 1/5 = anim/targeting, 0 = map
TURN_QUEUE  = 0x1407832A0   # Offsets.TurnQueue (active unit header)
TQ_LEVEL, TQ_TEAM, TQ_HP, TQ_MAXHP = 0x00, 0x02, 0x0C, 0x10   # u16 each
ACTED_WIN_LO, ACTED_WIN_LEN = 0x140782A80, 0x20   # neighborhood dump around Acted

k32 = C.WinDLL("kernel32", use_last_error=True)
PV = 0x0010 | 0x0400   # VM_READ | QUERY_INFORMATION (read-only)


def find_pid(name):
    class PE32(C.Structure):
        _fields_ = [("dwSize", W.DWORD), ("cntUsage", W.DWORD), ("th32ProcessID", W.DWORD),
                    ("th32DefaultHeapID", C.POINTER(C.c_ulong)), ("th32ModuleID", W.DWORD),
                    ("cntThreads", W.DWORD), ("th32ParentProcessID", W.DWORD),
                    ("pcPriClassBase", C.c_long), ("dwFlags", W.DWORD), ("szExeFile", C.c_char * 260)]
    snap = k32.CreateToolhelp32Snapshot(0x2, 0)
    e = PE32(); e.dwSize = C.sizeof(e); pid = None
    if k32.Process32First(snap, C.byref(e)):
        while True:
            if name.lower() in e.szExeFile.decode(errors="ignore").lower():
                pid = e.th32ProcessID; break
            if not k32.Process32Next(snap, C.byref(e)): break
    k32.CloseHandle(snap)
    return pid


def rd(h, a, n):
    buf = (C.c_ubyte * n)(); g = C.c_size_t(0)
    if k32.ReadProcessMemory(h, C.c_void_p(a), buf, n, C.byref(g)) and g.value == n:
        return bytes(buf)
    return None


def u8(h, a):
    b = rd(h, a, 1); return b[0] if b else -1


def u16(h, a):
    b = rd(h, a, 2); return (b[0] | (b[1] << 8)) if b else -1


def active_unit(h):
    return (u16(h, TURN_QUEUE + TQ_LEVEL), u16(h, TURN_QUEUE + TQ_TEAM),
            u16(h, TURN_QUEUE + TQ_HP), u16(h, TURN_QUEUE + TQ_MAXHP))


def fmt_unit(u):
    lvl, team, hp, mhp = u
    side = "player" if team == 0 else ("enemy" if team == 1 else f"team{team}")
    return f"lvl={lvl} hp={hp}/{mhp} [{side}]"


def dump_neighborhood(h, label):
    b = rd(h, ACTED_WIN_LO, ACTED_WIN_LEN)
    if not b:
        print(f"    {label}: <neighborhood unreadable>"); return
    off = ACTED - ACTED_WIN_LO
    cells = []
    for i, v in enumerate(b):
        mark = "*" if i == off else " "   # * marks the Acted byte itself
        cells.append(f"{mark}{v:02X}")
    print(f"    {label} [{ACTED_WIN_LO:#x}..]: " + " ".join(cells) + "   (* = Acted)")


def main():
    secs = float(sys.argv[1]) if len(sys.argv) > 1 else 150
    hz   = float(sys.argv[2]) if len(sys.argv) > 2 else 20
    pid = find_pid(PROC)
    if not pid:
        sys.exit(f"{PROC} not running")
    h = k32.OpenProcess(PV, False, pid)
    if not h:
        sys.exit(f"OpenProcess failed err={C.get_last_error()}")

    print(f"watching Acted ({ACTED:#x}) + turn-queue + battleMode for {secs:.0f}s @ {hz:.0f}Hz.")
    print("IN-GAME: act with RAMZA, end his turn; then act with the 2nd character; let an enemy act.\n")
    dump_neighborhood(h, "start")
    print()

    t0 = time.time()
    dt = 1.0 / hz
    last_acted = last_mode = None
    last_unit = None
    acted_counts = {}            # value -> times seen
    cur_nonone = 0               # consecutive non-1 acted reads
    max_nonone = 0               # longest such run (the unfreeze needs >= 3)
    unit_changes = 0
    try:
        while time.time() - t0 < secs:
            t = time.time() - t0
            acted = u8(h, ACTED)
            mode = u8(h, BATTLE_MODE)
            unit = active_unit(h)

            acted_counts[acted] = acted_counts.get(acted, 0) + 1
            if acted == 1:
                cur_nonone = 0
            else:
                cur_nonone += 1
                max_nonone = max(max_nonone, cur_nonone)

            if unit != last_unit:
                unit_changes += 1
                print(f"t={t:6.1f}s  ACTIVE UNIT -> {fmt_unit(unit)}   (acted={acted} mode={mode})")
                dump_neighborhood(h, "  on-change")
                last_unit = unit
            if acted != last_acted:
                print(f"t={t:6.1f}s  acted {last_acted} -> {acted}   (mode={mode}, active {fmt_unit(unit)}, run-of-non1={cur_nonone})")
                last_acted = acted
            if mode != last_mode:
                # mode transitions are frequent; keep them terse
                print(f"t={t:6.1f}s  battleMode -> {mode}")
                last_mode = mode
            time.sleep(dt)
    except KeyboardInterrupt:
        print("(interrupted)")
    finally:
        k32.CloseHandle(h)

    print("\n==== SUMMARY ====")
    print(f"Acted values seen (value: ticks): " + ", ".join(f"{k}:{v}" for k, v in sorted(acted_counts.items())))
    print(f"max consecutive non-1 Acted reads = {max_nonone}  (KillTracker unfreezes the actor latch at >= 3)")
    if max_nonone < 3:
        print("  -> Acted NEVER stayed non-1 for 3 straight ticks: the actor latch can't unfreeze")
        print("     between turns -> the next actor never re-latches -> STALE attribution. Root confirmed.")
    print(f"turn-queue active-unit changes = {unit_changes}  (clean per-turn changes => use this as the")
    print("     latch-reset signal instead of the Acted edge).")
    print("If the Acted byte (*) in the neighborhood dumps looks unrelated to who is acting, the")
    print("0x140782A8C anchor is likely wrong -- look for a byte that reads 1 only during an action.")


if __name__ == "__main__":
    main()
