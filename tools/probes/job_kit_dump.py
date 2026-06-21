"""Dump each generic job's full native ability set from the live JobCommand table.

The kit-by-job model ("pick a JOB -> get its full native ability set") needs, per job, the
list of ability ids in that job's vanilla JobCommand record. This reads them straight out of the
loaded table so we don't have to hand-source them.

Record layout (KitStampPolicy, base 0x14067E213, 25 bytes/record, rec = job - 69):
    [ExtAb b0][ExtAb b1][ExtRSM] [AbilityId x16] [RSM x6]
    flag prefix (ExtAb...) starts at base + rec*25 - 3; the 16 ability bytes at base + rec*25.
    Effective id = abilityByte[i] + (256 if the slot's extend bit is set in ExtAb u16 else 0).
    Extend bit for slot i (MSB-first per byte): (0x80 >> (i % 8)) << (8 * (i / 8)).

Generic band = jobs 74..92. Special-executor jobs (75/77/87/89/90 = Items/Aim/Jump/Throw/Math)
are flagged: their records are readable but they swallow FOREIGN injected ids, so they make poor
TARGET kits to transplant (their own native set is still listed for reference).

Pure RPM (read-only). Run with the game in any state:
    python tools\\probes\\job_kit_dump.py

CAVEAT: if the Multiplayer Coordinator/KitStamp is ON, records of the drafted jobs are temporarily
overwritten -- read with the Coordinator OFF for a clean vanilla dump.
"""

import ctypes
import ctypes.wintypes as w
import sys

PROC = "fft_enhanced.exe"
ABILITY_BASE = 0x14067E213
REC_SIZE = 25
JOB_LO, JOB_HI = 74, 92
SPECIAL_EXECUTORS = {75, 77, 87, 89, 90}

# Job id -> name (generic band, PSX wheel order per ic-job-id-remap; labels for readability).
JOB_NAMES = {
    74: "Squire", 75: "Chemist", 76: "Knight", 77: "Archer", 78: "Monk",
    79: "WhiteMage", 80: "BlackMage", 81: "TimeMage", 82: "Summoner", 83: "Thief",
    84: "Orator", 85: "Mystic", 86: "Geomancer", 87: "Dragoon", 88: "Samurai",
    89: "Ninja", 90: "Arithmetician", 91: "Bard", 92: "Dancer",
}

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


def extend_bit(i):
    return (0x80 >> (i % 8)) << (8 * (i // 8))


def read_job_abilities(job):
    rec = job - 69
    flag = rpm(ABILITY_BASE + rec * REC_SIZE - 3, 3)   # ExtAb b0, b1, ExtRSM
    ab = rpm(ABILITY_BASE + rec * REC_SIZE, 16)        # 16 ability bytes
    if flag is None or ab is None:
        return rec, None
    ext = flag[0] | (flag[1] << 8)
    ids = []
    for i in range(16):
        if ab[i] == 0 and not (ext & extend_bit(i)):
            continue  # empty slot
        ids.append(ab[i] + (256 if (ext & extend_bit(i)) else 0))
    return rec, ids


def main():
    global _H
    _H = _open()
    if not _H:
        print(f"{PROC} not running")
        sys.exit(1)
    print(f"JobCommand dump @ 0x{ABILITY_BASE:X} (rec = job-69)\n")
    hdr = f"{'job':>4} {'rec':>3}  {'name':<14} abilities (effective ids)"
    print(hdr)
    print("-" * 72)
    table = {}
    for job in range(JOB_LO, JOB_HI + 1):
        rec, ids = read_job_abilities(job)
        name = JOB_NAMES.get(job, "?")
        tag = " [special-executor]" if job in SPECIAL_EXECUTORS else ""
        if ids is None:
            print(f"{job:>4} {rec:>3}  {name:<14} <unreadable>{tag}")
            continue
        table[job] = ids
        print(f"{job:>4} {rec:>3}  {name:<14} {ids}{tag}")
    print()
    print("// machine-readable (job -> ability ids):")
    print("{")
    for job, ids in table.items():
        print(f"  {job}: {ids},   // {JOB_NAMES.get(job, '?')}")
    print("}")


if __name__ == "__main__":
    main()
