"""Classify stack-scraped qwords as return addresses: is the preceding insn a CALL?

Companion to the ShowSpike F6 STACKSCRAPE (docs/research/CALLOUT_BANNER_JOURNEY.md): the scrape logs every
in-image qword found on the live stack, but only qwords sitting right after a call instruction
are real return addresses -- the rest are spilled registers, vtable/function pointers, or data.
For each candidate address this reads the bytes just before it via RPM and tries to decode a
single call instruction that ENDS exactly at the candidate (x64 call encodings are 2-7 bytes,
so each length is tried). Read-only, never writes, never executes.

Usage (game must be running):
  python tools\\probes\\retaddr_check.py 0x1408033c8 0x1402322b6 ...
  python tools\\probes\\retaddr_check.py @hits.txt        # one address per line

Output per address: RET-AFTER-CALL (call site + decoded target) or NOT-A-RETADDR.
"""
import pathlib
import sys

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
from treasure_flags import rpm, _require_game

try:
    from capstone import Cs, CS_ARCH_X86, CS_MODE_64
except ImportError:
    print("capstone not installed. Run:  python -m pip install capstone")
    sys.exit(1)

# x64 call encodings span 2 bytes (FF D0 call rax) to 7 (FF 94 24 .. call [rsp+disp32]).
CALL_LENGTHS = (5, 6, 7, 2, 3, 4)


def check(md, addr):
    for ln in CALL_LENGTHS:
        code = rpm(addr - ln, ln)
        if code is None:
            continue
        insns = list(md.disasm(code, addr - ln))
        # exactly one insn, it is a call, and it consumed all ln bytes (ends AT addr)
        if len(insns) == 1 and insns[0].mnemonic == "call" and insns[0].size == ln:
            i = insns[0]
            return f"RET-AFTER-CALL  site={i.address:#x}  call {i.op_str}"
    return "NOT-A-RETADDR (no call decodes ending here -- spilled reg / data / fn pointer?)"


def main():
    args = sys.argv[1:]
    if not args:
        print("usage: python retaddr_check.py <addr-hex> [...] | @file-of-addrs")
        sys.exit(2)
    if args[0].startswith("@"):
        args = pathlib.Path(args[0][1:]).read_text().split()
    _require_game()
    md = Cs(CS_ARCH_X86, CS_MODE_64)
    for a in args:
        addr = int(a, 0)
        print(f"{addr:#014x}  {check(md, addr)}")


if __name__ == "__main__":
    main()
