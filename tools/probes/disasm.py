"""Read-only disassembler: dump + decode game code at an address via RPM (no writes, no calls).

For reverse-engineering a function's signature/behavior before any cold-call attempt -- e.g.
SetHP @ 0x14000E418C. Reads code bytes out of the running process (image base 0x140000000,
no ASLR) and disassembles them with capstone. 100% safe: it never writes and never executes.

Usage (game must be running):
  python tools\\probes\\disasm.py 0x14000E418C            # 64 bytes
  python tools\\probes\\disasm.py 0x14000E418C 0xC0       # 192 bytes
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


def main():
    if len(sys.argv) < 2:
        print("usage: python disasm.py <addr-hex> [nbytes=64]")
        sys.exit(2)
    addr = int(sys.argv[1], 0)
    n = int(sys.argv[2], 0) if len(sys.argv) > 2 else 64
    _require_game()
    code = rpm(addr, n)
    if code is None:
        print(f"could not read {n} bytes at {addr:#x} (game running? address valid/committed?)")
        sys.exit(1)

    md = Cs(CS_ARCH_X86, CS_MODE_64)
    md.detail = False
    print(f"=== disasm {n} bytes @ {addr:#x} ===")
    count = 0
    for insn in md.disasm(code, addr):
        print(f"  {insn.address:#014x}  {insn.bytes.hex():<22} {insn.mnemonic} {insn.op_str}")
        count += 1
    if count == 0:
        print("  (capstone decoded 0 instructions -- bytes may be encrypted/data, "
              "or the address is wrong)")
        print(f"  raw: {code.hex()}")


if __name__ == "__main__":
    main()
