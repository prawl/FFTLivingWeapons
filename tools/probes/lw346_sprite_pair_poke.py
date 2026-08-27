#!/usr/bin/env python
"""LW-346 live poke (outside-process WPM): rewrite one weapon's battle sprite/palette pair.

Plain language: the game keeps a tiny two-byte record per item that says which drawing in the
weapon art sheet a weapon uses on the field and which color set it gets. This probe changes
that record for ONE weapon id (default: swap it to the Chaos Blade's pair, a knight sword)
so the owner can watch whether the swing art follows. Every write checks the old bytes first;
--restore puts them back. The process forgets it all at exit anyway.

Where the record lives (1.5.2, read live 2026-08-27 00:30, tools/probes/lw346_live_disasm.py):
  pair table  0x140785CF0 + id*2   byte 0 = palette selector (nibbles), byte 1 = graphic index
  accessor    thunk 0x1402B8E60 -> 0x14FEC80C0(id): range index via 0x1402B8BCC, and for the
              weapon data type returns &table[id]; NULL when the id has no range (id 261+)
  readers     0x14026BC60 (plain sprite composer: graphic byte for the unit word at +0x150)
              0x14E92B95C (copy-protected CLUT loader: palette byte, high/low nibbles)
The June 2026 note that treated 0x140785CF2 as the table base was one item off, which explains
that session's "writes changed nothing" and its zero CE hits during a swing.
Known pairs: id 37 Chaos Blade 50 0C, id 67 Warbrand F0 03, id 19 / id 1 E0 00.
Usage: python tools/probes/lw346_sprite_pair_poke.py --peek ID [ID ...]
       python tools/probes/lw346_sprite_pair_poke.py --apply ID [--pair 500C] | --restore ID
"""
import sys

sys.path.insert(0, __file__.rsplit("\\", 1)[0] if "\\" in __file__ else ".")
from lw346_live_disasm import find_pid, rd, k32  # noqa: E402
from lw346_order_table_poke import wr, PRW  # noqa: E402

TABLE = 0x140785CF0
DONOR_PAIR = bytes.fromhex("500C")  # Chaos Blade: palette byte 0x50, graphic 0x0C (knight sword)
MAX_ID = 260  # the table has 261 entries; past it lives unrelated data, never write there


def main():
    args = [a for a in sys.argv[1:] if not a.startswith("--")]
    mode = next((a for a in sys.argv[1:] if a.startswith("--") and a != "--pair"), "--peek")
    pair = DONOR_PAIR
    if "--pair" in sys.argv:
        pair = bytes.fromhex(sys.argv[sys.argv.index("--pair") + 1])
    ids = [int(x, 0) for x in args if x not in ("500C",) and not (len(x) == 4 and "--pair" in sys.argv and x == sys.argv[sys.argv.index("--pair") + 1])]
    if not ids:
        print(__doc__); return 2
    h = k32.OpenProcess(PRW, False, find_pid("fft_enhanced.exe"))
    for i in ids:
        if i < 0 or i > MAX_ID:
            print("id %d refused (table holds ids 0..%d)" % (i, MAX_ID)); continue
        a = TABLE + i * 2
        cur = rd(h, a, 2)
        if mode == "--peek":
            print("id %3d @0x%X: %s (palette 0x%02X, graphic 0x%02X)" % (i, a, cur.hex().upper(), cur[0], cur[1])); continue
        if mode == "--apply":
            undo = "tools/probes/lw346_sprite_pair_undo_%d.txt" % i
            import os
            if not os.path.exists(undo):  # keep the FIRST (vanilla) bytes across repeated --apply calls
                open(undo, "w").write(cur.hex())
            ok = wr(h, a, pair)
            print("id %d: %s -> %s %s (undo bytes saved to %s)" % (i, cur.hex().upper(), pair.hex().upper(), "verified" if ok and rd(h, a, 2) == pair else "WRITE FAILED", undo))
        elif mode == "--restore":
            undo = "tools/probes/lw346_sprite_pair_undo_%d.txt" % i
            try:
                old = bytes.fromhex(open(undo).read().strip())
            except OSError:
                print("id %d: no undo file %s" % (i, undo)); continue
            ok = wr(h, a, old)
            print("id %d: %s -> %s %s" % (i, cur.hex().upper(), old.hex().upper(), "verified" if ok and rd(h, a, 2) == old else "WRITE FAILED"))
    return 0


if __name__ == "__main__":
    sys.exit(main())
