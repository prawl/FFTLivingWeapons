#!/usr/bin/env python
"""READ-ONLY-ish (brief SuspendThread pulses): sample a live fft_enhanced thread's RIP + stack
to find per-frame functions running on it. Born 2026-07-02 hunting a game-LOGIC-thread hook
portal for the on-command callout (natural orchestrator calls run on the logic thread; the
banner-update hook is the render thread, and fires from it never draw).

Usage: python thread_sample.py <tid> [samples=300] [interval_ms=7]

Output: histogram of in-module RIPs (top 20) + in-module return-address-looking qwords from
the top 0x200 bytes of stack (top 30). Wait/leaf RIPs land outside the module (ntdll) -- the
stack refs are usually the better portal candidates. Suspend pulses are microseconds; CE
debugging does far worse to this process.
"""
import ctypes as C
from ctypes import wintypes as W
import struct
import sys
from collections import Counter

k32 = C.WinDLL("kernel32", use_last_error=True)
psapi = C.WinDLL("psapi", use_last_error=True)

MOD_LO, MOD_HI = 0x140000000, 0x150000000


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
    tid = int(sys.argv[1])
    samples = int(sys.argv[2]) if len(sys.argv) > 2 else 300
    interval = (int(sys.argv[3]) if len(sys.argv) > 3 else 7) / 1000.0

    pid = find_pid("fft_enhanced")
    if not pid:
        print("fft_enhanced.exe not running"); sys.exit(1)
    hp = k32.OpenProcess(0x0410, False, pid)
    ht = k32.OpenThread(0x004A, False, tid)   # SUSPEND_RESUME | GET_CONTEXT | QUERY_INFORMATION
    if not ht:
        print(f"OpenThread({tid}) failed -- stale tid? (err {C.get_last_error()})"); sys.exit(1)
    print(f"pid {pid}, sampling tid {tid}: {samples} samples @ {interval * 1000:.0f}ms")

    # x64 CONTEXT: 0x4D0 bytes, 16-aligned; ContextFlags at +0x30, Rsp +0x98, Rip +0xF8.
    raw = C.create_string_buffer(0x4D0 + 16)
    base = C.addressof(raw)
    ctx = base + (16 - base % 16) % 16
    CONTEXT_CONTROL = 0x100001

    import time
    rips, refs = Counter(), Counter()
    ok_samples = 0
    for _ in range(samples):
        C.memset(ctx, 0, 0x4D0)
        C.cast(ctx + 0x30, C.POINTER(W.DWORD))[0] = CONTEXT_CONTROL
        if k32.SuspendThread(ht) == 0xFFFFFFFF:
            time.sleep(interval); continue
        got = k32.GetThreadContext(ht, C.c_void_p(ctx))
        rip = C.cast(ctx + 0xF8, C.POINTER(C.c_uint64))[0] if got else 0
        rsp = C.cast(ctx + 0x98, C.POINTER(C.c_uint64))[0] if got else 0
        stack = None
        if rsp:
            buf = C.create_string_buffer(0x200); n = C.c_size_t()
            if k32.ReadProcessMemory(hp, C.c_void_p(rsp), buf, 0x200, C.byref(n)) and n.value == 0x200:
                stack = buf.raw
        k32.ResumeThread(ht)
        if got:
            ok_samples += 1
            rips[rip] += 1
            if stack:
                for off in range(0, 0x200, 8):
                    q = struct.unpack_from("<Q", stack, off)[0]
                    if MOD_LO <= q < MOD_HI:
                        refs[q] += 1
        time.sleep(interval)

    print(f"\n{ok_samples} good samples. RIP histogram (in-module first):")
    mod = [(a, n) for a, n in rips.most_common() if MOD_LO <= a < MOD_HI]
    other = [(a, n) for a, n in rips.most_common() if not MOD_LO <= a < MOD_HI]
    for a, n in mod[:20]:
        print(f"  {a:#x}  x{n}")
    print(f"  (out-of-module RIPs: {sum(n for _, n in other)} samples across {len(other)} addrs, top: "
          + ", ".join(f"{a:#x} x{n}" for a, n in other[:3]) + ")")
    print("\nstack refs in-module (return-addr candidates, top 30):")
    for a, n in refs.most_common(30):
        print(f"  {a:#x}  x{n}")


if __name__ == "__main__":
    main()
