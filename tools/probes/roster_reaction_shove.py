#!/usr/bin/env python
"""
Roster-REACTION shove probe -- test the FFHacktics claim "shove ANY ability into a
unit's reaction slot and it casts on the reaction's trigger rule."

In IC the LIVE/in-battle reaction system is a 4-byte BITFIELD (combat +0x94, RSM base
166) over the ~25 fixed reactions -- it has NO id field, so an arbitrary action ability
cannot be represented there. The ONE place a raw reaction ability *id* lives is the
ROSTER slot: base 0x1411A18D0, stride 0x258, reaction = 1 byte id @ +0x08 + 1 byte
equipped-flag @ +0x09. That byte feeds the battle BUILD (sticky: read once at battle
entry). So the only way to "shove an ability into the reaction slot" is here.

This probe is a SPIKE, NOT A SHIP. It snapshots the target slot's reaction id+flag,
writes a chosen id, HOLDS it (the slot self-normalizes -> re-assert each tick), and
restores on exit. The VERDICT IS PATRICK'S EYES: (1) hard-refresh the unit's ability
screen -- does the injected ability DISPLAY in the Reaction slot? (2) enter a battle and
get the unit hit -- does anything FIRE? Prior datum (Cherry Blossom, id 8): displayed
only, never fired -- architecture predicts the same here.

ACTION IDS TO TRY (Mettle, single-byte): Focus=0x41 (65), Rush=0x55 (85).

USAGE (game running):
  python roster_reaction_shove.py dump
        # list every non-empty roster slot: nameId/lvl/br/fa + reaction id(+name)+flag.
  python roster_reaction_shove.py shove <slot> <id> [seconds=240]
        # snapshot + write id into slot's reaction byte, set equipped-flag, HOLD.
        # e.g. shove 0 65   (Focus into Ramza). Prints the exact restore command.
  python roster_reaction_shove.py restore <slot> <orig_id> <orig_flag>
        # manual one-shot restore (if a hard-killed shove skipped its finally-restore).

TIMING: roster grants apply at the NEXT battle build and are then sticky. Be at the
party/formation menu (NOT mid-battle) when you shove; then enter a fresh battle.
"""
import ctypes as C
from ctypes import wintypes as W
import sys
import time

PROC = "fft_enhanced.exe"
PV = 0x0400 | 0x0010              # QUERY_INFORMATION | VM_READ
PV_W = PV | 0x0020                # + VM_WRITE
k32 = C.WinDLL("kernel32", use_last_error=True)
psapi = C.WinDLL("psapi", use_last_error=True)

# BASE re-located 2026-07-01: the old 0x1411A18D0 went stale (read all-zero on the
# current process); the live roster/unit array is at 0x1411A7D10 (FFTHandsFree's
# scanned "Unit data array"; +0x14 main-hand matches dual-gun). Record layout intact
# (reaction id @+0x08 confirmed: Ramza slot 0 read 0xBD/Mana Shield + brave/faith 97/75).
BASE, STRIDE, SLOTS = 0x1411A7D10, 0x258, 20
REACT, RFLAG, LVL, BR, FA, NAME, RHAND = 0x08, 0x09, 0x1D, 0x1E, 0x1F, 0x230, 0x14

# reaction id -> name (RSM space 167-197 == low byte of global id). Source ABILITY_IDS.md.
REACT_NAMES = {
    167: "Magick Surge", 168: "Speed Surge", 169: "Vanish", 170: "Vigilance",
    171: "Dragonheart", 172: "Regenerate", 174: "Faith Surge", 175: "Crit: Recover HP",
    176: "Crit: Recover MP", 177: "Crit: Quick", 178: "Bonecrusher", 179: "Magick Counter",
    180: "Counter Tackle", 181: "Nature's Wrath", 182: "Absorb MP", 183: "Gil Snapper",
    185: "Auto-Potion", 186: "Counter", 188: "Cup of Life", 189: "Mana Shield",
    190: "Soulbind", 191: "Parry", 192: "Earplugs", 193: "Reflexes", 194: "Sticky Fingers",
    195: "Shirahadori", 196: "Archer's Bane", 197: "First Strike",
}
ACTION_NAMES = {65: "Focus (Mettle)", 85: "Rush (Mettle)"}


def name_of(rid):
    if rid in REACT_NAMES:
        return REACT_NAMES[rid]
    if rid in ACTION_NAMES:
        return ACTION_NAMES[rid] + " <-- NON-REACTION action id"
    return f"id{rid} (not a known reaction)"


class PMC(C.Structure):
    _fields_ = [("cb", W.DWORD), ("PageFaultCount", W.DWORD),
                ("PeakWorkingSetSize", C.c_size_t), ("WorkingSetSize", C.c_size_t),
                ("QuotaPeakPagedPoolUsage", C.c_size_t), ("QuotaPagedPoolUsage", C.c_size_t),
                ("QuotaPeakNonPagedPoolUsage", C.c_size_t), ("QuotaNonPagedPoolUsage", C.c_size_t),
                ("PagefileUsage", C.c_size_t), ("PeakPagefileUsage", C.c_size_t)]


def _all_pids():
    arr = (W.DWORD * 4096)()
    need = W.DWORD()
    psapi.EnumProcesses(C.byref(arr), C.sizeof(arr), C.byref(need))
    pids = []
    for i in range(need.value // C.sizeof(W.DWORD)):
        h = k32.OpenProcess(PV, False, arr[i])
        if not h:
            continue
        buf = C.create_unicode_buffer(260)
        if psapi.GetModuleBaseNameW(h, None, buf, 260) and buf.value.lower() == PROC.lower():
            pids.append(arr[i])
        k32.CloseHandle(h)
    return pids


def _ws(pid):
    h = k32.OpenProcess(PV, False, pid)
    if not h:
        return 0
    try:
        pmc = PMC()
        pmc.cb = C.sizeof(pmc)
        return pmc.WorkingSetSize if psapi.GetProcessMemoryInfo(h, C.byref(pmc), pmc.cb) else 0
    finally:
        k32.CloseHandle(h)


def find_pid():
    pids = _all_pids()
    if not pids:
        return None
    if len(pids) > 1:
        best = max(pids, key=_ws)
        print(f"  {len(pids)} {PROC} procs {pids}; using largest working set -> {best}")
        return best
    return pids[0]


def rd(h, addr, n):
    buf = C.create_string_buffer(n)
    got = C.c_size_t()
    if k32.ReadProcessMemory(h, C.c_void_p(addr), buf, n, C.byref(got)) and got.value == n:
        return buf.raw
    return None


def wr(h, addr, data):
    got = C.c_size_t()
    return bool(k32.WriteProcessMemory(h, C.c_void_p(addr), data, len(data), C.byref(got))) and got.value == len(data)


def slot_base(slot):
    return BASE + slot * STRIDE


def cmd_dump(h):
    print(f"roster @0x{BASE:X} stride 0x{STRIDE:X}; reaction id @+0x{REACT:02X}, flag @+0x{RFLAG:02X}\n")
    print(f"{'slot':>4} {'nameId':>6} {'lvl':>3} {'br':>3} {'fa':>3}  reaction")
    for s in range(SLOTS):
        d = rd(h, slot_base(s), 0x238)
        if d is None:
            continue
        lvl = d[LVL]
        if lvl == 0:
            continue
        nm = int.from_bytes(d[NAME:NAME + 2], "little")
        rid, rflag = d[REACT], d[RFLAG]
        tag = name_of(rid) if rflag else "(none equipped)"
        print(f"{s:>4} {nm:>6} {lvl:>3} {d[BR]:>3} {d[FA]:>3}  +0x08={rid:>3} flag={rflag}  {tag}")


def cmd_shove(h, slot, new_id, seconds):
    base = slot_base(slot)
    d = rd(h, base, 0x238)
    if d is None or d[LVL] == 0:
        print(f"slot {slot} is empty/unreadable; `dump` to pick a live slot.")
        return
    orig_id, orig_flag = d[REACT], d[RFLAG]
    nm = int.from_bytes(d[NAME:NAME + 2], "little")
    print(f"slot {slot}: nameId={nm} lvl={d[LVL]} br={d[BR]} fa={d[FA]}")
    print(f"  ORIG reaction: +0x08={orig_id} ({name_of(orig_id) if orig_flag else 'none'}) flag={orig_flag}")
    print(f"  SHOVING id {new_id} ({name_of(new_id)}) + flag=1, holding {seconds:.0f}s.\n")
    print(f"  >>> 1. HARD-REFRESH the unit's ability/equip screen (fully exit + re-enter)")
    print(f"  >>>    -- does '{name_of(new_id)}' DISPLAY in the Reaction slot?")
    print(f"  >>> 2. Enter a FRESH battle, get this unit hit by a physical attack")
    print(f"  >>>    -- does ANYTHING fire?")
    print(f"  RESTORE (if hard-killed): "
          f"python roster_reaction_shove.py restore {slot} {orig_id} {orig_flag}\n")
    reasserts = 0
    t0 = time.time()
    last = -1
    try:
        while time.time() - t0 < seconds:
            cur = rd(h, base + REACT, 2)
            if cur is not None and (cur[0] != new_id or cur[1] != 1):
                wr(h, base + REACT, bytes([new_id, 1]))
                reasserts += 1
            s = int(time.time() - t0)
            if s != last:
                last = s
                if s % 5 == 0:
                    print(f"  t={s:>3}s  engine-reasserts={reasserts}")
            time.sleep(0.03)
    except KeyboardInterrupt:
        print("(interrupted)")
    finally:
        wr(h, base + REACT, bytes([orig_id, orig_flag]))
        print(f"\nrestored slot {slot} reaction -> id {orig_id} flag {orig_flag}.")
    print(f"done. engine re-asserted {reasserts}x "
          f"(0 = ours to hold; many = a normalize source fights us). VERDICT = your eyes.")


def cmd_restore(h, slot, orig_id, orig_flag):
    base = slot_base(slot)
    if wr(h, base + REACT, bytes([orig_id & 0xFF, orig_flag & 0xFF])):
        print(f"restored slot {slot} reaction -> id {orig_id} flag {orig_flag}.")
    else:
        print("write failed.")


def main():
    a = sys.argv
    mode = a[1] if len(a) > 1 else ""
    if mode not in ("dump", "shove", "restore"):
        print(__doc__)
        return
    pid = find_pid()
    if not pid:
        print(f"{PROC} not running")
        return
    h = k32.OpenProcess(PV_W if mode != "dump" else PV, False, pid)
    if not h:
        print(f"OpenProcess failed err={C.get_last_error()}")
        return
    try:
        if mode == "dump":
            cmd_dump(h)
        elif mode == "shove":
            slot = int(a[2])
            new_id = int(a[3], 0)
            secs = float(a[4]) if len(a) > 4 else 240
            cmd_shove(h, slot, new_id, secs)
        else:
            cmd_restore(h, int(a[2]), int(a[3], 0), int(a[4], 0))
    finally:
        k32.CloseHandle(h)


if __name__ == "__main__":
    main()
