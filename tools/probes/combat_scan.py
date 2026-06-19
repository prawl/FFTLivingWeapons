"""Scan the live battle-unit array and tag player vs enemy.

Reads BattleUnitsBase + i*0x200 for i in 0..27 (the constructed combat structs,
NOT the pre-battle source). Decodes the 1.5 combat-struct fields and flags the
agency bit (+0x05 bit 0x08: SET=human/player-driven, CLEAR=AI/enemy) so we can
see, live, which slots are enemies -- the repaint targets.

Pure RPM (cross-process, read-only, crash-safe). Run while standing in a battle:
    python tools\\probes\\combat_scan.py

Anchors (1.5, from Offsets.cs / Puppeteer.Policy.cs / Dicene Mod.cs):
    BattleUnitsBase 0x141853CE0, stride 0x200
    job      +0x03   agency  +0x05 (bit 0x08)
    weapon   +0x20   level   +0x29   brave +0x2A   faith +0x2C
    PA +0x3E  MA +0x3F  Speed +0x40
"""

import ctypes
import ctypes.wintypes as w
import sys

PROC = "fft_enhanced.exe"
BASE = 0x141853CE0
STRIDE = 0x200
N = 28
LEN = 0x60

k32 = ctypes.WinDLL("kernel32", use_last_error=True)
psapi = ctypes.WinDLL("psapi", use_last_error=True)
_H = None


def _open():
    arr = (w.DWORD * 4096)()
    need = w.DWORD()
    if not psapi.EnumProcesses(arr, ctypes.sizeof(arr), ctypes.byref(need)):
        return None
    for i in range(need.value // ctypes.sizeof(w.DWORD)):
        h = k32.OpenProcess(0x0410, False, arr[i])  # QUERY_INFO | VM_READ
        if not h:
            continue
        buf = ctypes.create_unicode_buffer(260)
        if psapi.GetModuleBaseNameW(h, None, buf, 260) and buf.value.lower() == PROC.lower():
            return h
        k32.CloseHandle(h)
    return None


def rpm(addr, n):
    if not _H:
        return None
    buf = ctypes.create_string_buffer(n)
    got = ctypes.c_size_t()
    ok = k32.ReadProcessMemory(_H, ctypes.c_void_p(addr), buf, n, ctypes.byref(got))
    return buf.raw if ok and got.value == n else None


def main():
    global _H
    _H = _open()
    if not _H:
        print(f"{PROC} not running")
        sys.exit(1)
    print(f"scanning {N} slots @ 0x{BASE:X} stride 0x{STRIDE:X}\n")
    hdr = f"{'i':>2} {'addr':>11} {'agency':>7} {'job':>4} {'lvl':>3} {'br':>3} {'fa':>3} {'wpn':>4} {'PA':>3} {'MA':>3} {'SPD':>3}  flags"
    print(hdr)
    print("-" * len(hdr))
    enemies, players = [], []
    for i in range(N):
        a = BASE + i * STRIDE
        d = rpm(a, LEN)
        if d is None:
            continue
        job, agency = d[0x03], d[0x05]
        lvl, br, fa = d[0x29], d[0x2A], d[0x2C]
        wpn = d[0x20]
        pa, ma, spd = d[0x3E], d[0x3F], d[0x40]
        sane = (1 <= lvl <= 99) and (br <= 100) and (fa <= 100) and job != 0
        if not sane:
            continue
        human = bool(agency & 0x08)
        tag = "HUMAN" if human else "AI"
        (players if human else enemies).append(a)
        print(f"{i:>2} 0x{a:09X} {tag:>7} 0x{job:02X} {lvl:>3} {br:>3} {fa:>3} "
              f"0x{wpn:02X} {pa:>3} {ma:>3} {spd:>3}  agency=0x{agency:02X}")
    print(f"\nplayers(HUMAN bit set): {len(players)}   enemies(AI): {len(enemies)}")
    if enemies:
        print("enemy struct addrs:", " ".join(f"0x{a:X}" for a in enemies))


if __name__ == "__main__":
    main()
