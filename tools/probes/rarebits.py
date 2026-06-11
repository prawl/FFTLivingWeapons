#!/usr/bin/env python
"""Frequency-analyze a bandsurvey dump: for every (offset, bit), how many rows
have it set? Rare bits (<10%) are status candidates -- poison should sit on one
unit family only. Groups holders by fingerprint. Usage: rarebits.py <dump.txt>"""
import re
import sys
from collections import defaultdict

rows = []
pat = re.compile(r"^@([0-9A-F]{12}) (\S+)\s+(\d+)/(\d+)\s+((?:[0-9A-F]{2} ?)+)$")
lo = None
for line in open(sys.argv[1], encoding="utf-8", errors="replace"):
    m = re.search(r"bytes \+0x([0-9A-F]+)\.\.", line)
    if m:
        lo = int(m.group(1), 16)
    m = pat.match(line.strip())
    if m:
        addr, fp, hp, mhp = m.group(1), m.group(2), int(m.group(3)), int(m.group(4))
        data = bytes(int(x, 16) for x in m.group(5).split())
        rows.append((addr, fp, hp, mhp, data))

n = len(rows)
print(f"{n} rows parsed, region starts +0x{lo:02X}")
bitcount = defaultdict(list)            # (off, bit) -> [row indices]
for idx, (_, _, _, _, data) in enumerate(rows):
    for i, byte in enumerate(data):
        for bit in range(8):
            if byte & (1 << bit):
                bitcount[(lo + i, 1 << bit)].append(idx)

print(f"\nbits set in <10% of rows (offset, mask, count, holder fps):")
for (off, mask), holders in sorted(bitcount.items()):
    if not holders or len(holders) / n >= 0.10:
        continue
    fps = defaultdict(int)
    for idx in holders:
        fps[rows[idx][1]] += 1
    fpstr = "  ".join(f"{fp} x{c}" for fp, c in sorted(fps.items(), key=lambda kv: -kv[1])[:6])
    print(f"+0x{off:03X} mask 0x{mask:02X}  x{len(holders):<4} {fpstr}")
