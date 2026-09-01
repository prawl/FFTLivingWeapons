"""Disk-side disassembler for fft_enhanced.exe (READ-ONLY, no process access).

Plain language: print the game's instructions at an address straight from the program file
on disk, so plain code can be read even when the game is not running. Copy-protected bodies
(addresses that disassemble to a single `jmp 0x14Fxxxxxxx`) live only in memory; follow those
with tools/probes/lw346_live_disasm.py instead. PE section headers map the address to a file
offset, so the .code section and the on-disk parts of .xdata both work.

    python tools/probes/lw_disk_disasm.py 0x14030D2D4          # 0x100 bytes
    python tools/probes/lw_disk_disasm.py 0x14030D2D4 0x280
"""
import struct, sys
from pathlib import Path
sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from lib.paths import STEAM_FFT
from capstone import Cs, CS_ARCH_X86, CS_MODE_64

EXE = STEAM_FFT / "fft_enhanced.exe"
BASE = 0x140000000

def sections(data):
    pe = struct.unpack_from("<I", data, 0x3C)[0]
    nsec = struct.unpack_from("<H", data, pe + 6)[0]
    opt = struct.unpack_from("<H", data, pe + 20)[0]
    out = []
    for i in range(nsec):
        off = pe + 24 + opt + i * 40
        name = data[off:off+8].rstrip(b"\0").decode(errors="replace")
        vsize, va, rsize, raw = struct.unpack_from("<IIII", data, off + 8)
        out.append((name, va, vsize, raw, rsize))
    return out

def va_to_file(secs, va):
    rva = va - BASE
    for name, sva, vsize, raw, rsize in secs:
        if sva <= rva < sva + max(vsize, rsize):
            off = rva - sva + raw
            return off if rva - sva < rsize else None
    return None

data = EXE.read_bytes()
secs = sections(data)
addr = int(sys.argv[1], 0)
n = int(sys.argv[2], 0) if len(sys.argv) > 2 else 0x100
off = va_to_file(secs, addr)
if off is None:
    print(f"{addr:#x} not in disk image"); sys.exit(1)
code = data[off:off+n]
md = Cs(CS_ARCH_X86, CS_MODE_64)
for insn in md.disasm(code, addr):
    print(f"{insn.address:#014x}  {insn.bytes.hex():<24} {insn.mnemonic} {insn.op_str}")
