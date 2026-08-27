#!/usr/bin/env python
"""LW-346 read-only probe: dump the two nex rows the swing-side weapon resolver reads.

Plain language: when a unit swings, the game asks "what kind of thing is in this hand?" by
reading one row of a data table for the item id, then one row of a second table for the
category that row names. This probe replays that read from outside the game for a vanilla
id and for the new id 261, so we can see whether the new weapon answers the same way a
Chaos Blade does. Writes nothing.

Chain (1.5.2, disassembled live 2026-08-26 23:45, tools/probes/lw346_live_disasm.py):
  0x1401ED910(unit)         reads combat +0x20 / +0x24 (main / off hand), range-checks the id
                            ((id-1)<=0xFC || (id-0x100)<=5 after the marker widening), then
  0x1401EDC50(0, id)        table #45 = *(0x143CDA2E8) -> row[id] -> field +0x24 = key K
                            table #38 = *(0x143CDA6F0) -> row[K]  -> field +0x18 = result
  0x1403D3AE8(tbl, idx)     type-1 flagged container: idx in [tbl+0x44, tbl+0x48] inclusive,
                            entry = tbl+0x10 base + (idx - lo) * 16
  0x1403D2FD8(entry)        kind = entry[0] & 0x7FFF..; p = entry[8]; row = p + p[4|8|0x10]
Usage: python tools/probes/lw346_swing_model_tables.py [id ...]   (default: 37 67 257 261)
"""
import struct
import sys

sys.path.insert(0, __file__.rsplit("\\", 1)[0] if "\\" in __file__ else ".")
from lw346_live_disasm import find_pid, rd, k32  # noqa: E402

G_TABLE45 = 0x143CDA2E8
G_TABLE38 = 0x143CDA6F0


def q(h, a):
    b = rd(h, a, 8)
    return struct.unpack("<Q", b)[0] if b else None


def d(h, a):
    b = rd(h, a, 4)
    return struct.unpack("<I", b)[0] if b else None


def entry_for(h, tbl, idx):
    typ = q(h, tbl + 0x38)
    flag = rd(h, tbl + 0x41, 1)
    lo, hi, count = d(h, tbl + 0x44), d(h, tbl + 0x48), q(h, tbl + 0x28)
    if typ != 1 or flag != b"\x01":
        return None, "unsupported container shape type=%s flag=%s" % (typ, flag)
    if idx < lo or idx > hi:
        return None, "idx %d outside [%d, %d]" % (idx, lo, hi)
    rel = idx - lo
    if rel >= count:
        return None, "rel %d >= count %d" % (rel, count)
    return q(h, tbl + 0x10) + rel * 16, "ok"


def resolve(h, entry):
    kind = q(h, entry) & 0x7FFFFFFFFFFFFFFF
    if kind == 0:
        return None, "kind 0 (null row)"
    p = q(h, entry + 8)
    off_at = {1: 4, 2: 8}.get(kind, 0x10)
    off = struct.unpack("<i", rd(h, p + off_at, 4))[0]
    return p + off, "kind %d p=0x%X off=%d" % (kind, p, off)


def dump(h, a, n=0x40):
    b = rd(h, a, n)
    return " ".join("%02X" % x for x in b) if b else "<unreadable>"


def main():
    ids = [int(x, 0) for x in sys.argv[1:]] or [37, 67, 257, 261]
    h = k32.OpenProcess(0x0010 | 0x0400, False, find_pid("fft_enhanced.exe"))
    t45, t38 = q(h, G_TABLE45), q(h, G_TABLE38)
    for name, t in (("table45", t45), ("table38", t38)):
        print("%s @0x%X: base=0x%X count=%d type=%d range=[%d,%d] idx=%d" % (
            name, t, q(h, t + 0x10), q(h, t + 0x28), q(h, t + 0x38), d(h, t + 0x44), d(h, t + 0x48), d(h, t + 8)))
    for i in ids:
        e, why = entry_for(h, t45, i)
        print("\n== id %d ==" % i)
        if e is None:
            print("  table45:", why); continue
        row, how = resolve(h, e)
        print("  table45 entry 0x%X [%s] -> %s" % (e, dump(h, e, 16), how))
        if row is None:
            continue
        print("  row 0x%X: %s" % (row, dump(h, row, 0x40)))
        key = d(h, row + 0x24)
        print("  +0x24 key = %d" % key)
        e2, why2 = entry_for(h, t38, key)
        if e2 is None:
            print("  table38:", why2); continue
        row2, how2 = resolve(h, e2)
        print("  table38 entry 0x%X -> %s" % (e2, how2))
        if row2 is not None:
            print("  row2 0x%X: %s" % (row2, dump(h, row2, 0x30)))
            print("  +0x18 result = %d" % d(h, row2 + 0x18))
    return 0


if __name__ == "__main__":
    sys.exit(main())
