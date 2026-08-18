#!/usr/bin/env python
"""Measure the real wall-clock cost of one full VirtualQueryEx address-space walk against the
running game process -- the provenance instrument for PoolScan.SnapshotRefreshMs (LivingWeapon/
Display/PoolScan.cs), which exists specifically to stop paying this cost once a tick.

WHY THIS EXISTS
---------------
LW-261's retune round cited "a full VirtualQueryEx region walk measured ~45ms" as the reason
PoolScan.Step used to re-snapshot every call was roughly a third of a 92-to-102-second scan on
the owner's 2026-08-18 live tape. That number had no tracked instrument before this file (a repo
rule: probes live in tools/probes/, not just in a code comment) -- this is that instrument, kept
so the claim can be re-run and re-verified rather than taken on faith.

WHAT IT MEASURES
----------------
Opens FFT_enhanced.exe by name (tasklist, same pattern as combat_reaction.py's find_pid), then
walks the address space with VirtualQueryEx exactly like LivingWeapon/Memory/Mem.cs's own
Regions() does: MEM_COMMIT, MEM_PRIVATE, a writable protection bit set, no PAGE_GUARD/
PAGE_NOACCESS, from address 0 up to 0x7FFF_FFFF_0000. Counts every region VirtualQueryEx returns
(TOTAL) and every one that survives that filter (KEPT -- the same "committed, PRIVATE, writable"
set PoolScan actually scans), and times the whole walk. Runs three trials back to back and prints
per-trial elapsed ms plus the two counts, so a re-run can be compared against the recorded result
below without needing to re-read this file's prose.

2026-08-18 RESULT (the number PoolScan.SnapshotRefreshMs's own doc cites): 5519 regions
enumerated, 1362 kept, three trials 44.9 to 45.9ms cross-process.

USAGE (game running):
  python vq_walk_cost.py
Env: FFT_PID overrides the auto-picked pid.
"""
import ctypes as C
import os
import subprocess
import sys
import time

k32 = C.WinDLL("kernel32", use_last_error=True)
PROCESS_QUERY_INFORMATION = 0x0400
PROCESS_VM_READ = 0x0010

MEM_COMMIT = 0x1000
MEM_PRIVATE = 0x20000
PAGE_GUARD = 0x100
PAGE_NOACCESS = 0x01
WRITABLE = 0x04 | 0x08 | 0x40 | 0x80   # PAGE_READWRITE | PAGE_WRITECOPY | PAGE_EXECUTE_READWRITE | PAGE_EXECUTE_WRITECOPY

MAX_ADDR = 0x7FFF_FFFF_0000
TRIALS = 3


class MEMORY_BASIC_INFORMATION(C.Structure):
    # Mirrors LivingWeapon/Memory/Mem.cs's own private MEMORY_BASIC_INFORMATION exactly (64-bit
    # layout): BaseAddress/AllocationBase are pointer-sized, RegionSize is pointer-sized (nuint).
    _fields_ = [
        ("BaseAddress", C.c_void_p),
        ("AllocationBase", C.c_void_p),
        ("AllocationProtect", C.c_uint32),
        ("__pad0", C.c_uint32),
        ("RegionSize", C.c_size_t),
        ("State", C.c_uint32),
        ("Protect", C.c_uint32),
        ("Type", C.c_uint32),
        ("__pad1", C.c_uint32),
    ]


def find_pid():
    if os.environ.get("FFT_PID"):
        return int(os.environ["FFT_PID"])
    out = subprocess.check_output(
        ["tasklist", "/fi", "imagename eq FFT_enhanced.exe", "/fo", "csv", "/nh"],
        text=True, errors="ignore")
    best, bestmem = None, -1
    for line in out.splitlines():
        p = [x.strip('"') for x in line.split('","')]
        if len(p) >= 5 and p[0].lower().startswith("fft_enhanced"):
            mem = int(p[4].replace(",", "").replace("K", "").replace(" ", "") or 0)
            if mem > bestmem:
                best, bestmem = int(p[1]), mem
    if best is None:
        raise SystemExit("FFT_enhanced.exe not running")
    return best


def walk_once(h):
    addr = 0
    mbi = MEMORY_BASIC_INFORMATION()
    total = 0
    kept = 0
    while addr < MAX_ADDR:
        got = k32.VirtualQueryEx(h, C.c_void_p(addr), C.byref(mbi), C.sizeof(mbi))
        if got == 0:
            break
        total += 1
        base = mbi.BaseAddress or 0
        size = mbi.RegionSize
        if (mbi.State == MEM_COMMIT and mbi.Type == MEM_PRIVATE
                and (mbi.Protect & WRITABLE) != 0
                and (mbi.Protect & (PAGE_GUARD | PAGE_NOACCESS)) == 0):
            kept += 1
        nxt = base + size
        addr = nxt if nxt > addr else addr + 0x1000
    return total, kept


def main():
    pid = find_pid()
    h = k32.OpenProcess(PROCESS_QUERY_INFORMATION | PROCESS_VM_READ, False, pid)
    if not h:
        raise SystemExit(f"OpenProcess failed err={C.get_last_error()}")
    try:
        print(f"pid={pid}")
        for trial in range(1, TRIALS + 1):
            t0 = time.perf_counter()
            total, kept = walk_once(h)
            ms = (time.perf_counter() - t0) * 1000.0
            print(f"trial {trial}: {ms:.1f}ms, {total} regions enumerated, {kept} kept")
    finally:
        k32.CloseHandle(h)


if __name__ == "__main__":
    sys.exit(main())
