#!/usr/bin/env python
"""Print the crash registers and faulting address from a Windows minidump (read-only).

Plain language: when the game crashes, Windows writes a .dmp file; this reads the exception
record and the x64 register context out of it (no debugger), so the crash site and the
bad pointer are one command away. Also derives the item id when the bad pointer lies in a
12-byte-per-item catalog (main 0x14080EA90 or the rig's relocated extended buffer).
Usage: python tools/probes/lw346_minidump_context.py <dump.dmp> [rig_ext_base_hex]
"""
import struct
import sys

CATALOG_MAIN = 0x14080EA90
REGS = ["Rax", "Rcx", "Rdx", "Rbx", "Rsp", "Rbp", "Rsi", "Rdi", "R8", "R9", "R10", "R11", "R12", "R13", "R14", "R15", "Rip"]


def main():
    path = sys.argv[1]
    ext = int(sys.argv[2], 16) if len(sys.argv) > 2 else None
    d = open(path, "rb").read()
    sig, ver, nstreams, stream_rva = struct.unpack_from("<4sIII", d, 0)
    assert sig == b"MDMP", "not a minidump"
    for i in range(nstreams):
        stype, size, rva = struct.unpack_from("<III", d, stream_rva + 12 * i)
        if stype != 6:  # ExceptionStream
            continue
        tid, _, code, flags, rec, addr, nparams = struct.unpack_from("<IIIIQQI", d, rva)
        params = struct.unpack_from("<15Q", d, rva + 0x20 + 8)
        ctx_size, ctx_rva = struct.unpack_from("<II", d, rva + 0x20 + 8 + 15 * 8 + 8 + 4)
        # MINIDUMP_EXCEPTION_STREAM: ThreadId u32, alignment u32, MINIDUMP_EXCEPTION (0x98), ThreadContext (u32 size, u32 rva)
        ctx_size, ctx_rva = struct.unpack_from("<II", d, rva + 8 + 0x98)
        regs = struct.unpack_from("<17Q", d, ctx_rva + 0x78)
        print("thread %d  code 0x%08X  at 0x%X  access %s address 0x%X" % (tid, code, addr, "write" if params[0] == 1 else "read", params[1]))
        for n, v in zip(REGS, regs):
            print("  %-3s 0x%016X" % (n, v))
        bad = params[1]
        for label, base in (("main catalog", CATALOG_MAIN), ("rig ext catalog", ext)):
            if base and 0 <= bad - base < 12 * 1024:
                print("  -> %s: id %d (0x%X), record offset +%d" % (label, (bad - base) // 12, (bad - base) // 12, (bad - base) % 12))
        return 0
    print("no exception stream"); return 1


if __name__ == "__main__":
    sys.exit(main())
