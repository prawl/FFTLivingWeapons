#!/usr/bin/env python
"""
Charm-break probe (FFT:IC 1.5). Read-only RPM -- cannot crash the game.

WHY: CharmLock is supposed to make a landed Charm UNBREAKABLE for N of the foe's turns by re-stamping
the authoritative band copy every tick. On 1.5 the charm BREAKS WHEN THE ENEMY IS HIT (doesn't stay
sticky). The charm STATUS byte is confirmed right (band +0x49 bit 0x20 -- the engine sets it, our scan
reads it). The suspect is the ALLEGIANCE half: CharmLock holds band +0x54, but the proven team byte is
band +0x38 (== combat +0x54; battle_cheats A_ALLEG). If the engine reverts allegiance on the hit and we
re-stamp the wrong byte, the unit snaps back to the enemy.

WHAT THIS ANSWERS: across the three phases -- (1) enemy normal, (2) freshly charmed (fights for you),
(3) after the breaking hit -- which band byte(s) actually encode "fights for the player", and which the
engine CLEARS on the hit. The intersection is the byte CharmLock must hold. Watches, per live band unit:
  +0x14 HP (the hit shows as an HP drop)        +0x49 charm/doom/reflect status  (bit 0x20 = Charm)
  +0x38 team/allegiance (battle_cheats A_ALLEG) +0x54 what CharmLock currently holds as "allegiance"
  +0x45 dead/undead  +0x47 reraise/float  +0x48 poison/regen  +0x4A status timer
Prints a line whenever any watched byte changes for a unit (keyed by its mhp/lvl/brave/faith), and dumps
the +0x30..+0x58 window whenever a unit's Charm bit toggles -- so the full charmed-vs-broken diff is visible.

USAGE (game running, live battle, a +3 Galewind wielder + a target enemy):
  python tools\\probes\\charm_probe.py [seconds=180] [hz=20]
Then in-game: land a Charm on an enemy (watch the charm-bit dump), let it sit a beat, then HIT that same
charmed enemy. Read the change lines around the HP drop -- that names the byte the engine reverts.
"""
import ctypes as C
from ctypes import wintypes as W
import sys
import time

PROC = "FFT_enhanced"

# --- band (authoritative live structs; matches battle_cheats.py / Offsets.cs) ---
COMBAT_ANCHOR = 0x141855CE0
BAND_ENTRY    = 0x1C
COMBAT_STRIDE = 0x200
BAND_SLOTS    = 49
BAND_READ_BASE = COMBAT_ANCHOR + BAND_ENTRY - 24 * COMBAT_STRIDE
A_LEVEL, A_BRAVE, A_FAITH, A_HP, A_MAXHP = 0x0D, 0x0E, 0x10, 0x14, 0x16

# Watched control/status bytes (band-relative). name -> offset.
WATCH = {
    "hp@14":    0x14,   # u16 -- the breaking hit is an HP drop here
    "team@38":  0x38,   # u8  battle_cheats A_ALLEG (== combat+0x54): the proven team/allegiance byte
    "dead@45":  0x45,   # u8  Dead 0x20 / Undead 0x10
    "rerais@47":0x47,   # u8  Reraise 0x20 / Float 0x40 / Invisible 0x10
    "pois@48":  0x48,   # u8  Poison 0x80 / Regen 0x40 / Protect/Shell/Haste
    "charm@49": 0x49,   # u8  Charm 0x20 (the confirmed status bit) / Doom 0x01 / Reflect 0x02
    "ctim@4A":  0x4A,   # u8  status countdown timer
    "alleg@54": 0x54,   # u8  what CharmLock currently re-stamps as "allegiance" (suspect)
}
WIN_LO, WIN_LEN = 0x30, 0x28   # neighborhood dumped on a charm-bit toggle
CHARM_BIT = 0x20

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


def band_entry(s):
    return BAND_READ_BASE + s * COMBAT_STRIDE


def read_watch(h, e):
    out = {}
    for name, off in WATCH.items():
        out[name] = u16(h, e + off) if name.startswith("hp") else u8(h, e + off)
    return out


def fp_of(h, e):
    return (u16(h, e + A_MAXHP), u8(h, e + A_LEVEL), u8(h, e + A_BRAVE), u8(h, e + A_FAITH))


def valid(fp):
    mhp, lvl, br, fa = fp
    return 0 < mhp < 2000 and 1 <= lvl <= 99 and 1 <= br <= 100 and 1 <= fa <= 100


def dump_window(h, e, label):
    b = rd(h, e + WIN_LO, WIN_LEN)
    if not b:
        print(f"      {label}: <window unreadable>"); return
    cells = " ".join(f"{(WIN_LO+i):02x}:{v:02X}" for i, v in enumerate(b))
    print(f"      {label} [entry+{WIN_LO:#x}..]: {cells}")


def fmt(fp):
    return f"L{fp[1]}/{fp[0]}hp/br{fp[2]}fa{fp[3]}"


def fmt_watch(w):
    return " ".join(f"{k}={v}" for k, v in w.items())


def main():
    secs = float(sys.argv[1]) if len(sys.argv) > 1 else 180
    hz   = float(sys.argv[2]) if len(sys.argv) > 2 else 20
    pid = find_pid(PROC)
    if not pid:
        sys.exit(f"{PROC} not running")
    h = k32.OpenProcess(PV, False, pid)
    if not h:
        sys.exit(f"OpenProcess failed err={C.get_last_error()}")

    print(f"watching band control bytes for {secs:.0f}s @ {hz:.0f}Hz.  read-only.")
    print("IN-GAME: charm an enemy, let it sit, then HIT that charmed enemy. Watch the change lines.\n")

    t0 = time.time()
    dt = 1.0 / hz
    last = {}        # slot -> (fp, watch dict)
    charm_was = {}   # slot -> bool charmed last tick
    try:
        while time.time() - t0 < secs:
            t = time.time() - t0
            for s in range(BAND_SLOTS):
                e = band_entry(s)
                fp = fp_of(h, e)
                if not valid(fp):
                    last.pop(s, None); charm_was.pop(s, None); continue
                w = read_watch(h, e)
                prev = last.get(s)
                if prev is None or prev[0] != fp:
                    last[s] = (fp, w)
                    charm_was[s] = (w["charm@49"] & CHARM_BIT) != 0
                    continue
                pw = prev[1]
                changed = [k for k in WATCH if w[k] != pw[k]]
                if changed:
                    diffs = " ".join(f"{k}:{pw[k]}->{w[k]}" for k in changed)
                    print(f"t={t:6.1f}s s{s:02d} {fmt(fp)}  {diffs}")
                    last[s] = (fp, w)
                # charm-bit toggle -> dump the neighborhood for the full charmed/broken diff
                now_charm = (w["charm@49"] & CHARM_BIT) != 0
                if now_charm != charm_was.get(s, False):
                    print(f"t={t:6.1f}s s{s:02d} {fmt(fp)}  CHARM {'SET' if now_charm else 'CLEARED'}  ({fmt_watch(w)})")
                    dump_window(h, e, "on-toggle")
                    charm_was[s] = now_charm
            time.sleep(dt)
    except KeyboardInterrupt:
        print("(interrupted)")
    finally:
        k32.CloseHandle(h)

    print("\n==== READ ME ====")
    print("Find the CHARM SET line (you landed the charm) and note which bytes besides charm@49 changed")
    print("from the unit's normal state -- those encode 'fights for the player'. Then find the change")
    print("lines at the HP drop (the breaking hit): the byte that REVERTS there is what the hold must")
    print("keep. If team@38 flips on charm and reverts on the hit while alleg@54 never moves, the fix is")
    print("to hold +0x38, not +0x54.")


if __name__ == "__main__":
    main()
