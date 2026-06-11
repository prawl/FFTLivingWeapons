#!/usr/bin/env python
"""Filter a bandsurvey dump to plausible REAL units (sane lvl/br/fa, real mhp),
group by fingerprint, and show each family's distinct status-region patterns
(+0x40..+0x60). The poisoned unit's family carries a bit the others lack.
Usage: realunits.py <dump.txt>"""
import re
import sys
from collections import defaultdict

pat = re.compile(r"^@([0-9A-F]{12}) (\d+)/(\d+)/(\d+)/(\d+)\s+(\d+)/(\d+)\s+((?:[0-9A-F]{2} ?)+)$")
fams = defaultdict(list)
for line in open(sys.argv[1], encoding="utf-8", errors="replace"):
    m = pat.match(line.strip())
    if not m:
        continue
    addr = int(m.group(1), 16)
    mhp, lvl, br, fa = (int(m.group(i)) for i in range(2, 6))
    hp = int(m.group(6))
    data = bytes(int(x, 16) for x in m.group(8).split())
    if not (150 <= mhp <= 1500 and 50 <= lvl <= 99 and 35 <= br <= 100
            and 35 <= fa <= 100 and hp > 0):
        continue
    fams[(mhp, lvl, br, fa)].append((addr, hp, data))

print(f"{len(fams)} real-unit families:\n")
for fp, rows in sorted(fams.items()):
    print(f"fp={fp[0]}/{fp[1]}/{fp[2]}/{fp[3]}  ({len(rows)} copies)")
    seen = set()
    for addr, hp, data in rows:
        region = data[:0x20]                      # +0x40..+0x5F
        key = (hp, region)
        if key in seen:
            continue
        seen.add(key)
        hx = " ".join(f"{b:02X}" for b in region)
        nz = ", ".join(f"+0x{0x40 + i:02X}={b:02X}" for i, b in enumerate(region) if b)
        print(f"  @{addr:012X} hp={hp:>4}  {hx}")
        print(f"      nonzero: {nz if nz else '(none)'}")
    print()
