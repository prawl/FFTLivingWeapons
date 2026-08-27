#!/usr/bin/env python
"""LW-346 READ-ONLY live cross-reference scan of the game's plain code section (1.5.2).

Plain language: given a few addresses (a function, a global buffer), find every place in the
running game's code that calls or refers to them, and show a few instructions around each hit.
This is how "which screen builds the party inventory into the global word buffer, and which
clean-up pass runs on it afterwards" gets answered without a debugger.

Three reference shapes are swept (numpy-vectorised over the whole executable section):
  E8 rel32 calls          -> target == p+5+rel32
  rip-relative disp32     -> target == p+4(+0|+1|+4 trailing imm)+disp32 (lea/mov/cmp...)
  imm64 absolute          -> the 8 little-endian bytes of the address (mov r64, imm64)
The Denuvo-processed code is scanned as it sits in memory (readable via RPM), so plain-code
references are complete; references from inside the encrypted/virtualised regions are not
recoverable this way (they show up as "no hits", which is a fact, not a proof of absence).

Writes nothing. Usage: python tools/probes/lw346_xref_scan.py 0xADDR [0xADDR ...] [--ctx N]
"""
import ctypes as C
import ctypes.wintypes as W
import struct
import sys

import numpy as np
from capstone import Cs, CS_ARCH_X86, CS_MODE_64

PROC = "fft_enhanced.exe"
BASE = 0x140000000
k32 = C.WinDLL("kernel32", use_last_error=True)
PV = 0x0410  # PROCESS_VM_READ | PROCESS_QUERY_INFORMATION


class PE32(C.Structure):
    _fields_ = [("dwSize", W.DWORD), ("cntUsage", W.DWORD), ("th32ProcessID", W.DWORD),
                ("th32DefaultHeapID", C.POINTER(C.c_ulong)), ("th32ModuleID", W.DWORD),
                ("cntThreads", W.DWORD), ("th32ParentProcessID", W.DWORD),
                ("pcPriClassBase", C.c_long), ("dwFlags", W.DWORD),
                ("szExeFile", C.c_char * 260)]


def find_pid(name):
    snap = k32.CreateToolhelp32Snapshot(2, 0)
    e = PE32(); e.dwSize = C.sizeof(PE32)
    pid = None
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


def rd_big(h, a, n, chunk=1 << 20):
    out = bytearray()
    for off in range(0, n, chunk):
        b = rd(h, a + off, min(chunk, n - off))
        if b is None:
            b = bytes(min(chunk, n - off))  # unreadable page range -> zeros (no false hits)
        out += b
    return bytes(out)


def sections(h):
    hdr = rd(h, BASE, 0x600)
    pe = struct.unpack_from("<I", hdr, 0x3C)[0]
    nsec = struct.unpack_from("<H", hdr, pe + 6)[0]
    opt = struct.unpack_from("<H", hdr, pe + 20)[0]
    out = []
    for i in range(nsec):
        s = pe + 24 + opt + i * 40
        name = hdr[s:s + 8].rstrip(b"\0").decode(errors="ignore")
        vsize, va = struct.unpack_from("<II", hdr, s + 8)
        chars = struct.unpack_from("<I", hdr, s + 36)[0]
        out.append((name, BASE + va, vsize, chars))
    return out


def hx(b):
    return " ".join("%02X" % x for x in b)


md = Cs(CS_ARCH_X86, CS_MODE_64)


def show_ctx(h, hit, before=0x14, after=0x12):
    blob = rd(h, hit - before, before + after)
    if not blob:
        return
    # resync: try successive start offsets until the disassembly lands exactly on the hit
    for off in range(0, before + 1):
        ins = list(md.disasm(blob[off:], hit - before + off))
        if any(i.address == hit for i in ins):
            for i in ins:
                if i.address < hit - 8 and i.address + i.size <= hit - 8:
                    continue
                if i.address > hit + after - 2:
                    break
                print("      %X  %-20s %s %s%s" % (i.address, hx(i.bytes), i.mnemonic, i.op_str,
                                                 "   <== here" if i.address == hit else ""))
            return


def main():
    args = [a for a in sys.argv[1:] if not a.startswith("--")]
    targets = [int(a, 16) for a in args]
    if not targets:
        print(__doc__); return 2
    pid = find_pid(PROC)
    if not pid:
        print("game not running"); return 2
    h = k32.OpenProcess(PV, False, pid)
    execs = [s for s in sections(h) if s[3] & 0x20000000]
    print("pid %d; executable sections: %s" % (pid, ", ".join("%s@%X+%X" % (n, a, sz) for n, a, sz, _ in execs)))
    for name, sa, sz, _ in execs:
        blob = rd_big(h, sa, sz)
        arr = np.frombuffer(blob, dtype=np.uint8)
        n = len(arr)
        # rel32 / disp32 at every byte offset p: value = s32(p)
        s32 = np.zeros(n, dtype=np.int64)
        for k in range(4):
            s32[: n - 3] += arr[k: n - 3 + k].astype(np.int64) << (8 * k)
        s32 = np.where(s32 >= 1 << 31, s32 - (1 << 32), s32)
        pos = np.arange(n, dtype=np.int64)
        for t in targets:
            print("\n=== target 0x%X in %s ===" % (t, name))
            hits = []
            # calls: E8 at p, rel at p+1 -> target = sa+p+5+rel
            ce = np.flatnonzero(arr[: n - 4] == 0xE8)
            m = (sa + ce + 5 + s32[ce + 1]) == t
            for p in ce[m]:
                hits.append((int(sa + p), "call"))
            # rip-relative: instruction ends at p+4 (+0, +1, +4 for a trailing immediate)
            for tail, tag in ((0, "rip"), (1, "rip+imm8"), (4, "rip+imm32")):
                m = (sa + pos + 4 + tail + s32) == t
                for p in np.flatnonzero(m):
                    p = int(p)
                    if tail == 0 and arr[p - 1] == 0xE8 and (p - 1) in ce and False:
                        pass
                    hits.append((sa + p, tag))
            # imm64 absolute
            pat = np.frombuffer(struct.pack("<Q", t), dtype=np.uint8)
            m = np.ones(n - 7, dtype=bool)
            for k in range(8):
                m &= arr[k: n - 7 + k] == pat[k]
            for p in np.flatnonzero(m):
                hits.append((int(sa + p), "imm64"))
            hits.sort()
            seen = set()
            print("  %d hit(s)" % len(hits))
            for a, tag in hits:
                if (a, tag) in seen:
                    continue
                seen.add((a, tag))
                site = a - 1 if tag == "call" else a
                print("  - 0x%X  (%s)" % (site, tag))
                if "--ctx" in sys.argv or len(hits) <= 40:
                    show_ctx(h, site)
    k32.CloseHandle(h)
    return 0


if __name__ == "__main__":
    sys.exit(main())
