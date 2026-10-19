#!/usr/bin/env python
r"""Hardware read/write watch on up to four addresses in the running game, from outside.

UNPROVEN as of 2026-10-19. Attaches and detaches clean (the game kept running through six runs) and
the debug registers read back set, but no run has produced a hit yet; every target tried that night
turned out to be a byte the game was not touching. Prove it on a byte the game writes every frame
before trusting a zero.

Attaches as a debugger, arms DR0..DR3 on every thread, collects the instruction addresses that
touch the watched bytes for N seconds, then disarms and detaches. The game keeps running when this
tool exits (DebugSetProcessKillOnExit false). Nothing in the game is written except the debug
registers, which are restored to zero on detach.

  python tools\probes\hwbp_watch.py 0x1437415F6:2:rw 0x1437415E0:2:rw --seconds 15

Addresses are preferred base and are rebased onto the live image. Width 1, 2, 4 or 8. Kind rw
(read or write) or w (write only). Output is one line per distinct RIP with a hit count, in
preferred base form, plus the first few register snapshots.
"""
import argparse
import ctypes as C
import ctypes.wintypes as W
import os
import sys
import time

sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), ".."))
from lib.livebase import open_game, rebase, PREFERRED_BASE  # noqa: E402

k32 = C.WinDLL("kernel32", use_last_error=True)
THREAD_ALL = 0x1FFFFF
CONTEXT_DEBUG_REGISTERS = 0x00100010
CONTEXT_FULL = 0x0010000B
EXCEPTION_DEBUG_EVENT = 1
CREATE_THREAD_DEBUG_EVENT = 2
EXIT_THREAD_DEBUG_EVENT = 4
EXCEPTION_SINGLE_STEP = 0x80000004
DBG_CONTINUE = 0x00010002
DBG_EXCEPTION_NOT_HANDLED = 0x80010001
TH32CS_SNAPTHREAD = 0x4


class M128A(C.Structure):
    _fields_ = [("Low", C.c_ulonglong), ("High", C.c_longlong)]


class CONTEXT(C.Structure):
    _pack_ = 16
    _fields_ = [("P1Home", C.c_ulonglong), ("P2Home", C.c_ulonglong), ("P3Home", C.c_ulonglong),
                ("P4Home", C.c_ulonglong), ("P5Home", C.c_ulonglong), ("P6Home", C.c_ulonglong),
                ("ContextFlags", W.DWORD), ("MxCsr", W.DWORD),
                ("SegCs", W.WORD), ("SegDs", W.WORD), ("SegEs", W.WORD), ("SegFs", W.WORD),
                ("SegGs", W.WORD), ("SegSs", W.WORD), ("EFlags", W.DWORD),
                ("Dr0", C.c_ulonglong), ("Dr1", C.c_ulonglong), ("Dr2", C.c_ulonglong),
                ("Dr3", C.c_ulonglong), ("Dr6", C.c_ulonglong), ("Dr7", C.c_ulonglong),
                ("Rax", C.c_ulonglong), ("Rcx", C.c_ulonglong), ("Rdx", C.c_ulonglong),
                ("Rbx", C.c_ulonglong), ("Rsp", C.c_ulonglong), ("Rbp", C.c_ulonglong),
                ("Rsi", C.c_ulonglong), ("Rdi", C.c_ulonglong), ("R8", C.c_ulonglong),
                ("R9", C.c_ulonglong), ("R10", C.c_ulonglong), ("R11", C.c_ulonglong),
                ("R12", C.c_ulonglong), ("R13", C.c_ulonglong), ("R14", C.c_ulonglong),
                ("R15", C.c_ulonglong), ("Rip", C.c_ulonglong),
                ("FltSave", C.c_byte * 512), ("VectorRegister", M128A * 26),
                ("VectorControl", C.c_ulonglong), ("DebugControl", C.c_ulonglong),
                ("LastBranchToRip", C.c_ulonglong), ("LastBranchFromRip", C.c_ulonglong),
                ("LastExceptionToRip", C.c_ulonglong), ("LastExceptionFromRip", C.c_ulonglong)]


class EXCEPTION_RECORD(C.Structure):
    _fields_ = [("ExceptionCode", W.DWORD), ("ExceptionFlags", W.DWORD),
                ("ExceptionRecord", C.c_void_p), ("ExceptionAddress", C.c_void_p),
                ("NumberParameters", W.DWORD), ("ExceptionInformation", C.c_ulonglong * 15)]


class EXCEPTION_DEBUG_INFO(C.Structure):
    _fields_ = [("ExceptionRecord", EXCEPTION_RECORD), ("dwFirstChance", W.DWORD)]


class DEBUG_EVENT(C.Structure):
    _fields_ = [("dwDebugEventCode", W.DWORD), ("dwProcessId", W.DWORD), ("dwThreadId", W.DWORD),
                ("pad", W.DWORD), ("u", C.c_ulonglong * 20)]


class THREADENTRY32(C.Structure):
    _fields_ = [("dwSize", W.DWORD), ("cntUsage", W.DWORD), ("th32ThreadID", W.DWORD),
                ("th32OwnerProcessID", W.DWORD), ("tpBasePri", C.c_long), ("tpDeltaPri", C.c_long),
                ("dwFlags", W.DWORD)]


k32.OpenThread.restype = W.HANDLE
k32.OpenThread.argtypes = [W.DWORD, W.BOOL, W.DWORD]
k32.GetThreadContext.argtypes = [W.HANDLE, C.POINTER(CONTEXT)]
k32.SetThreadContext.argtypes = [W.HANDLE, C.POINTER(CONTEXT)]
k32.SuspendThread.argtypes = [W.HANDLE]
k32.ResumeThread.argtypes = [W.HANDLE]
k32.CreateToolhelp32Snapshot.restype = W.HANDLE
k32.WaitForDebugEvent.argtypes = [C.POINTER(DEBUG_EVENT), W.DWORD]
k32.ContinueDebugEvent.argtypes = [W.DWORD, W.DWORD, W.DWORD]


def new_ctx():
    """GetThreadContext needs a 16 byte aligned CONTEXT; ctypes gives no alignment promise."""
    buf = C.create_string_buffer(C.sizeof(CONTEXT) + 16)
    addr = (C.addressof(buf) + 15) & ~15
    ctx = CONTEXT.from_address(addr)
    ctx._keep = buf
    return ctx


def threads_of(pid):
    snap = k32.CreateToolhelp32Snapshot(TH32CS_SNAPTHREAD, 0)
    te = THREADENTRY32()
    te.dwSize = C.sizeof(te)
    out = []
    if k32.Thread32First(snap, C.byref(te)):
        while True:
            if te.th32OwnerProcessID == pid:
                out.append(te.th32ThreadID)
            if not k32.Thread32Next(snap, C.byref(te)):
                break
    k32.CloseHandle(snap)
    return out


def dr7_for(watches):
    """watches: list of (addr, width, kind). LEN bits 00=1 01=2 11=4 10=8; RW bits 01=write 11=rw."""
    dr7 = 0
    lens = {1: 0b00, 2: 0b01, 4: 0b11, 8: 0b10}
    for i, (_, width, kind) in enumerate(watches):
        dr7 |= 1 << (i * 2)
        rw = 0b01 if kind == "w" else 0b11
        dr7 |= (rw | (lens[width] << 2)) << (16 + i * 4)
    return dr7


def set_regs(tid, watches, dr7):
    h = k32.OpenThread(THREAD_ALL, False, tid)
    if not h:
        return False
    k32.SuspendThread(h)
    ctx = new_ctx()
    ctx.ContextFlags = CONTEXT_DEBUG_REGISTERS
    ok = k32.GetThreadContext(h, C.byref(ctx))
    if ok:
        for i, (a, _, _) in enumerate(watches):
            setattr(ctx, f"Dr{i}", a)
        ctx.Dr6 = 0
        ctx.Dr7 = dr7
        ok = k32.SetThreadContext(h, C.byref(ctx))
    k32.ResumeThread(h)
    k32.CloseHandle(h)
    return bool(ok)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("watch", nargs="+", help="ADDR:WIDTH:KIND, preferred base hex, kind rw or w")
    ap.add_argument("--seconds", type=float, default=15)
    ap.add_argument("--max", type=int, default=200, help="stop after this many hits")
    a = ap.parse_args()
    watches = []
    for w in a.watch[:4]:
        addr, width, kind = w.split(":")
        watches.append((int(addr, 16), int(width), kind))
    pid, hproc, base = open_game()
    live = [(rebase(ad, base), wd, kd) for ad, wd, kd in watches]
    dr7 = dr7_for(live)
    print(f"pid {pid} base {base:#x}; watching " + ", ".join(f"{ad:#x}({wd},{kd})" for ad, wd, kd in watches))

    k32.DebugSetProcessKillOnExit(False)
    if not k32.DebugActiveProcess(pid):
        sys.exit(f"DebugActiveProcess failed: {C.get_last_error()}")
    armed = 0
    for tid in threads_of(pid):
        armed += set_regs(tid, live, dr7)
    print(f"armed {armed} threads")

    hits = {}
    samples = []
    ev = DEBUG_EVENT()
    deadline = time.time() + a.seconds
    total = 0
    try:
        while time.time() < deadline and total < a.max:
            if not k32.WaitForDebugEvent(C.byref(ev), 200):
                continue
            status = DBG_CONTINUE
            code = ev.dwDebugEventCode
            if code == EXCEPTION_DEBUG_EVENT:
                info = C.cast(ev.u, C.POINTER(EXCEPTION_DEBUG_INFO)).contents
                rec = info.ExceptionRecord
                if rec.ExceptionCode == EXCEPTION_SINGLE_STEP:
                    h = k32.OpenThread(THREAD_ALL, False, ev.dwThreadId)
                    ctx = new_ctx()
                    ctx.ContextFlags = CONTEXT_FULL | CONTEXT_DEBUG_REGISTERS
                    k32.GetThreadContext(h, C.byref(ctx))
                    which = ctx.Dr6 & 0xF
                    rip = ctx.Rip - base + PREFERRED_BASE
                    key = (rip, which)
                    hits[key] = hits.get(key, 0) + 1
                    total += 1
                    if len(samples) < 12:
                        samples.append((rip, which, ctx.Rax, ctx.Rcx, ctx.Rdx, ctx.Rbx, ctx.Rsi, ctx.Rdi, ctx.R8, ctx.R9))
                    ctx.Dr6 = 0
                    ctx.ContextFlags = CONTEXT_DEBUG_REGISTERS
                    k32.SetThreadContext(h, C.byref(ctx))
                    k32.CloseHandle(h)
                else:
                    status = DBG_EXCEPTION_NOT_HANDLED
            elif code == CREATE_THREAD_DEBUG_EVENT:
                set_regs(ev.dwThreadId, live, dr7)
            k32.ContinueDebugEvent(ev.dwProcessId, ev.dwThreadId, status)
    finally:
        for tid in threads_of(pid):
            set_regs(tid, [(0, 1, "w")] * 4, 0)
        k32.DebugActiveProcessStop(pid)
    print(f"\n{total} hits, {len(hits)} distinct sites")
    for (rip, which), n in sorted(hits.items(), key=lambda kv: -kv[1]):
        print(f"  {rip:#x}  DR{which.bit_length() - 1 if which else '?'}  x{n}")
    for s in samples:
        rip, which, *regs = s
        print(f"  sample {rip:#x} dr{which} rax={regs[0]:#x} rcx={regs[1]:#x} rdx={regs[2]:#x} rbx={regs[3]:#x} rsi={regs[4]:#x} rdi={regs[5]:#x} r8={regs[6]:#x} r9={regs[7]:#x}")


if __name__ == "__main__":
    main()
