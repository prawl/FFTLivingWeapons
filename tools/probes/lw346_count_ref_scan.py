#!/usr/bin/env python
"""LW-346 registry hunt: every live .code reference to the bag count array (0x1411A7C00).

WHY. The party inventory list = bag items (count array) + equipped items, so the
list-build MUST read the count array; the June (pre-1.5) session mapped 15 reader
functions the same way (scan_count_refs.ps1) and proved none carries a fixed id-cap:
the id261 drop happens in a registry/validity consult NEXT TO the count read. Those 15
addresses are pre-1.5-stale. This probe re-derives the reader set for the CURRENT build:
find every code site whose RIP-relative disp32 or imm64 resolves to the count base, then
capstone-confirm each. The list-build readers found here are where the registry consult
lives; disassembling around them (disasm.py) is the next step, with CE find-what-accesses
as the live confirm of which fire during a menu open.

RIP-relative math: disp32 at code offset o (with t trailing imm bytes after the disp)
satisfies disp32 = TARGET - (0x140000000 + o + 4 + t), i.e. u32[o] + o == const(t).
That is one vectorized numpy compare per t in {0,1,2,4} -- no full-code disassembly.

READ-ONLY. Requires the game running.
USAGE: python lw346_count_ref_scan.py [target_hex=0x1411A7C00]
"""
import ctypes as C
import os
import sys

import numpy as np

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from ct_probe import PROC, PV, find_pid, k32, rd
from lw346_registry_cap_scan import code_section

from capstone import Cs, CS_ARCH_X86, CS_MODE_64
from capstone.x86 import X86_OP_IMM, X86_OP_MEM, X86_REG_RIP

BASE = 0x140000000
CHUNK = 0x400000
OVERLAP = 16


def resolves_to(ins, target):
    """True if any operand (rip-relative mem or imm64) resolves to target."""
    for op in ins.operands:
        if op.type == X86_OP_MEM and op.mem.base == X86_REG_RIP:
            if ins.address + ins.size + op.mem.disp == target:
                return True
        if op.type == X86_OP_IMM and op.imm == target:
            return True
    return False


def confirm(h, site_guess, target):
    """Disassemble a window around the disp32 hit; return the matching instruction."""
    lo = site_guess - 12
    buf = rd(h, lo, 32)
    if buf is None:
        return None
    md = Cs(CS_ARCH_X86, CS_MODE_64)
    md.detail = True
    for start in range(0, 12):
        for ins in md.disasm(bytes(buf[start:]), lo + start):
            end = ins.address + ins.size
            if ins.address <= site_guess < end and resolves_to(ins, target):
                return ins
            if ins.address > site_guess:
                break
    return None


def main():
    target = int(sys.argv[1], 16) if len(sys.argv) > 1 else 0x1411A7C00

    pid = find_pid(PROC)
    if not pid:
        print(f"{PROC} not running"); return 1
    h = k32.OpenProcess(PV, False, pid)
    sec_va, sec_len = code_section(h)
    print(f"pid {pid}: .code at 0x{sec_va:X} len 0x{sec_len:X}, target 0x{target:X}")

    hits = []
    pos = 0
    while pos < sec_len:
        want = min(CHUNK, sec_len - pos)
        data = rd(h, sec_va + pos, want)
        if data is not None:
            a8 = np.frombuffer(data, dtype=np.uint8)
            n = len(a8) - 3
            u = (a8[:n].astype(np.uint64) | (a8[1:n + 1].astype(np.uint64) << 8)
                 | (a8[2:n + 2].astype(np.uint64) << 16)
                 | (a8[3:n + 3].astype(np.uint64) << 24))
            offs = np.arange(n, dtype=np.uint64) + np.uint64(sec_va + pos)
            for t in (0, 1, 2, 4):
                want_disp = (np.uint64(target) - offs - np.uint64(4 + t)) & np.uint64(0xFFFFFFFF)
                for o in np.where(u == want_disp)[0]:
                    hits.append(sec_va + pos + int(o))
            # imm64 absolute reference
            pat = target.to_bytes(8, "little")
            f = data.find(pat)
            while f != -1:
                hits.append(sec_va + pos + f)
                f = data.find(pat, f + 1)
        step = want - OVERLAP if want == CHUNK else want
        pos += max(step, 1)

    hits = sorted(set(hits))
    print(f"{len(hits)} raw disp/imm sites; capstone-confirming...")
    confirmed = []
    for site in hits:
        ins = confirm(h, site, target)
        if ins is not None:
            confirmed.append(ins)
            print(f"  0x{ins.address:X}: {ins.mnemonic} {ins.op_str}")
    print(f"\n{len(confirmed)} confirmed instruction(s) referencing 0x{target:X}")
    k32.CloseHandle(h)
    return 0


if __name__ == "__main__":
    sys.exit(main())
