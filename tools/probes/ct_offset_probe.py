#!/usr/bin/env python
"""
CT-offset disambiguator (FFT:IC 1.5). Read-only RPM -- cannot crash the game.

WHY: Maim (+ Plague's augment) count a VICTIM enemy's turns off band-entry +0x09 (Offsets.ACtTurn);
CharmLock counts off band-entry +0x25 (Offsets.ACtSlam, == the static-array layout's CT@+0x25). The
in-tree docs contradict each other about which byte actually carries an ENEMY's charging CT on 1.5.
Maim never unlatched after 3+ victim turns -> the +0x09 read is the prime suspect.

WHAT: for every valid band-entry unit, sample BOTH candidate CT bytes (+0x09 and +0x25) at ~20Hz
across player AND enemy turns, plus the global battleMode (0x1409069A0). Detect a "turn taken" on each
offset using the shipped rule (CtTurns.IsTurn: last >= 90 then cur < 70) and record the battleMode seen
at the transition. Prints transitions live and a per-unit summary at the end.

READS: 90->below-70 transitions on +0x09 vs +0x25 tell us which byte ticks for enemies; the modes
recorded at each transition tell us whether Maim's onField gate (mode 2/3/4 only) would ever see it.

USAGE (game running, in a live battle, with the maimed enemies still alive):
  python ct_offset_probe.py [seconds=120] [hz=20]
Take 2-3 rounds and LET THE MAIMED ENEMIES ACT (600 max HP and 409 max HP in the current battle).
"""
import ctypes as C
from ctypes import wintypes as W
import sys
import time

PROC = "FFT_enhanced"

# --- 1.5 re-anchored addresses (Offsets.cs) ---
COMBAT_ANCHOR = 0x141855CE0   # Offsets.CombatAnchor (1.5)
STRIDE        = 0x200         # Offsets.CombatStride
BAND_ENTRY    = 0x1C          # Offsets.BandEntry
BAND_READ_BASE = COMBAT_ANCHOR + BAND_ENTRY - 24 * STRIDE   # Offsets.BandReadBase (n=-24)
BAND_SLOTS    = 49            # Offsets.BandSlots
BATTLE_MODE   = 0x1409069A0   # Offsets.BattleMode (u8)

# band-entry-relative offsets (Offsets.A*)
A_LVL, A_BRAVE, A_FAITH, A_HP, A_MAXHP, A_GX, A_GY = 0x0D, 0x0E, 0x10, 0x14, 0x16, 0x33, 0x34
CT_09 = 0x09   # Offsets.ACtTurn  (Maim / Plague read here)
CT_25 = 0x25   # Offsets.ACtSlam  (CharmLock reads here; == static-array CT@+0x25)

TURN_HI, TURN_LO = 90, 70   # CtTurns.TurnHi / TurnLo

k32 = C.WinDLL("kernel32", use_last_error=True)
PV = 0x0010 | 0x0400   # VM_READ | QUERY_INFORMATION  (no write -- read-only probe)


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


def u16(b, o): return b[o] | (b[o + 1] << 8)


class Track:
    __slots__ = ("last09", "last25", "t09", "t25", "modes09", "modes25", "seen_hi09", "seen_hi25")
    def __init__(self):
        self.last09 = self.last25 = -1
        self.t09 = self.t25 = 0
        self.modes09 = set(); self.modes25 = set()
        self.seen_hi09 = self.seen_hi25 = False


def mode_u8(h):
    b = rd(h, BATTLE_MODE, 1)
    return b[0] if b else -1


def main():
    secs = float(sys.argv[1]) if len(sys.argv) > 1 else 120
    hz   = float(sys.argv[2]) if len(sys.argv) > 2 else 20
    pid = find_pid(PROC)
    if not pid:
        sys.exit(f"{PROC} not running")
    h = k32.OpenProcess(PV, False, pid)
    if not h:
        sys.exit(f"OpenProcess failed err={C.get_last_error()}")
    tracks = {}   # key (mhp,lvl,br,fa) -> Track
    t0 = time.time()
    dt = 1.0 / hz
    last_status = -1
    print(f"watching band +0x09 vs +0x25 (+ battleMode) for {secs:.0f}s @ {hz:.0f}Hz.")
    print("LET THE MAIMED ENEMIES ACT (600 max HP, 409 max HP). Transitions print as they happen.\n")
    try:
        while time.time() - t0 < secs:
            mode = mode_u8(h)
            for s in range(BAND_SLOTS):
                b = rd(h, BAND_READ_BASE + s * STRIDE, 0x40)
                if b is None:
                    continue
                lvl, br, fa = b[A_LVL], b[A_BRAVE], b[A_FAITH]
                mhp, hp = u16(b, A_MAXHP), u16(b, A_HP)
                if not (1 <= lvl <= 99 and 1 <= br <= 100 and 1 <= fa <= 100 and 1 <= mhp < 2000):
                    continue
                gx, gy = b[A_GX], b[A_GY]
                if gx > 30 or gy > 30:
                    continue
                key = (mhp, lvl, br, fa)
                tr = tracks.get(key)
                if tr is None:
                    tr = tracks[key] = Track()
                c09, c25 = b[CT_09], b[CT_25]
                # +0x09 transition
                if tr.last09 >= TURN_HI and c09 < TURN_LO:
                    tr.t09 += 1; tr.modes09.add(mode)
                    print(f"  t={time.time()-t0:6.1f}s  TURN via +0x09  mhp={mhp} lv={lvl} hp={hp}  "
                          f"{tr.last09}->{c09}  mode={mode}  (#{tr.t09})")
                if c09 >= TURN_HI: tr.seen_hi09 = True
                tr.last09 = c09
                # +0x25 transition
                if tr.last25 >= TURN_HI and c25 < TURN_LO:
                    tr.t25 += 1; tr.modes25.add(mode)
                    print(f"  t={time.time()-t0:6.1f}s  TURN via +0x25  mhp={mhp} lv={lvl} hp={hp}  "
                          f"{tr.last25}->{c25}  mode={mode}  (#{tr.t25})")
                if c25 >= TURN_HI: tr.seen_hi25 = True
                tr.last25 = c25
            # periodic status for the maimed targets
            st = int(time.time() - t0)
            if st != last_status and st % 5 == 0:
                last_status = st
                bits = []
                for key, tr in sorted(tracks.items()):
                    mhp = key[0]
                    if mhp in (600, 409):
                        bits.append(f"mhp{mhp}: +09={tr.last09:>3}(hi={int(tr.seen_hi09)},t{tr.t09}) "
                                    f"+25={tr.last25:>3}(hi={int(tr.seen_hi25)},t{tr.t25})")
                print(f"t={st:>3}s mode={mode}  " + "  |  ".join(bits) if bits else f"t={st:>3}s mode={mode}  (targets not visible yet)")
            time.sleep(dt)
    except KeyboardInterrupt:
        print("(interrupted)")
    finally:
        k32.CloseHandle(h)

    print("\n==== SUMMARY (per unit) ====")
    print("offset +0x09 = ACtTurn (Maim/Plague);  offset +0x25 = ACtSlam (CharmLock / static-array CT)")
    for key, tr in sorted(tracks.items(), key=lambda kv: -kv[0][0]):
        mhp, lvl, br, fa = key
        tag = "  <-- MAIMED TARGET" if mhp in (600, 409) else ""
        print(f"  mhp={mhp:>4} lv={lvl:>2} br={br} fa={fa}: "
              f"+0x09 turns={tr.t09} sawHi={int(tr.seen_hi09)} modes={sorted(tr.modes09)} | "
              f"+0x25 turns={tr.t25} sawHi={int(tr.seen_hi25)} modes={sorted(tr.modes25)}{tag}")
    print("\nVERDICT KEY:")
    print("  the offset with turns>0 (and sawHi=1) is the one that carries the charging CT for enemies.")
    print("  if the winning offset's transition modes are all 1 (enemy turn), Maim's onField gate")
    print("  (mode 2/3/4 only) would NEVER sample it -- the gate is a second bug independent of offset.")


if __name__ == "__main__":
    main()
