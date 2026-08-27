#!/usr/bin/env python
"""LW-346 live poke (outside-process WPM): widen the attack path's copy-protected id caps.

Plain language: in battle the game resolves "what is in this unit's hand" through several
copies of one check that keeps the item only if its id is below 261; two of those copies live
in the copy-protected code region, which is not decrypted when the boot-arm marker runs, so
they cannot be marker patches. Run this AFTER the save is loaded (world map or later). Each
write is verified against its expected old byte first; --restore puts the old bytes back.
Sites (1.5.2, read live 2026-08-26 23:15 via CE find-what-accesses on the combat weapon word):
  0x14F2EA40D  lea ecx,[rdx+6]        imm byte 0x14F2EA40F  06 -> 07  (hand resolver before the weapon-stat call)
  0x14F45D312  xor r15d,0x5E          imm byte 0x14F45D315  5E -> 5F  (r15 = 0x58^0x5E = 6 feeds the 0x105 cap
                                                                       that gates the damage staging globals)
Usage: python tools/probes/lw346_battle_caps_poke.py --apply | --restore | --peek
"""
import sys

sys.path.insert(0, __file__.rsplit("\\", 1)[0] if "\\" in __file__ else ".")
from lw346_live_disasm import find_pid, rd, k32  # noqa: E402
from lw346_order_table_poke import wr, PRW  # noqa: E402

SITES = [
    ("copy-protected hand resolver lea ecx,[rdx+6] @0x14F2EA40D", 0x14F2EA40F, b"\x06", b"\x07"),
    ("copy-protected damage-staging cap input xor r15d,0x5E @0x14F45D312", 0x14F45D315, b"\x5E", b"\x5F"),
]


def main():
    mode = sys.argv[1] if len(sys.argv) > 1 else "--peek"
    h = k32.OpenProcess(PRW, False, find_pid("fft_enhanced.exe"))
    hx = lambda b: b.hex().upper() if b else "<unreadable>"
    for label, a, old, new in SITES:
        cur = rd(h, a, 1)
        if mode == "--peek":
            print("0x%X: %s (vanilla %s / poked %s)  %s" % (a, hx(cur), hx(old), hx(new), label)); continue
        src, dst = (old, new) if mode == "--apply" else (new, old)
        if cur != src:
            print("SKIP 0x%X: reads %s, expected %s (%s)" % (a, hx(cur), hx(src), label)); continue
        ok = wr(h, a, dst)
        print("%s 0x%X -> %s : %s  (%s)" % ("wrote" if ok else "WRITE FAILED", a, hx(dst), "verified" if rd(h, a, 1) == dst else "READBACK " + hx(rd(h, a, 1)), label))
    return 0


if __name__ == "__main__":
    sys.exit(main())
