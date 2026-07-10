r"""LW-58 Canary 3 offline forensics: diff the two render-node hex dumps in livingweapon.log.

The BodyDoubleSpike (F5, dev build) logs full 0x548-byte dumps of the cribbed sibling's render
node and Frank's freshly built one as file-only DBG lines:

    ... body-double: dump[sibling] node 0x140D31xxx (1352 bytes):
    ... body-double: dump[sibling] +0x000: 00 01 02 ... (16 bytes, last row 8)
    ... body-double: dump[frank]   +0x000: ...

This tool parses BOTH dumps back out of the log and prints every offset range where they differ,
so the next canary knows exactly which per-instance fields the skipped node+0x270 sub-block (the
suspected VRAM/palette slot math) leaves unset. Read-only, no game required.

Input can be livingweapon.log OR the rotation-proof forensics file the spike persists to the
update-safe save dir (Reloaded\User\Mods\prawl.fft.livingweapons\bodydouble_forensics_*.txt);
both carry the same dump[...] line format. The combat[...] snapshot lines in the forensics file
are deliberately ignored here (spawn_probe banddiff is their reader).

USAGE
    python tools\probes\node_dump_diff.py <path-to-log-or-forensics-file>
        # prints a summary line per differing byte range: offset, width, sibling bytes, frank bytes
        # annotates the offsets the spike already writes (0x11, 0x150, 0x230, 0x238) and the
        # skipped 0x270 sub-block span, so NEW differences stand out
    python tools\probes\node_dump_diff.py --selftest
        # offline: parser + differ logic on synthetic lines

If the log holds several dump pairs (multiple F5 sessions), the LAST complete pair wins.
"""

import re
import sys

NODE_SIZE = 0x548

# Offsets stage-2 already writes (expected to differ or match by design), for annotation only.
KNOWN = {
    0x11: "node+0x11 (si, stage-2 writes it)",
    0x150: "node+0x150 (combat ptr link, stage-2 writes it)",
    0x230: "node+0x230 (anim object A, stage-2 writes it)",
    0x238: "node+0x238 (anim object B, stage-2 writes it)",
}
SKIPPED_LO, SKIPPED_HI = 0x270, 0x2A8   # the skipped scene-bind sub-block's plausible field span

_LINE = re.compile(r"body-double: dump\[(?P<tag>\w+)\] \+0x(?P<off>[0-9A-Fa-f]{3}): (?P<hex>[0-9A-Fa-f ]+)")


def parse_dumps(lines):
    """Return {tag: bytearray} for the LAST complete dump of each tag in the given lines."""
    bufs, done = {}, {}
    for line in lines:
        m = _LINE.search(line)
        if not m:
            continue
        tag = m.group("tag")
        off = int(m.group("off"), 16)
        data = bytes.fromhex(m.group("hex").replace(" ", ""))
        if off == 0:
            bufs[tag] = bytearray()   # a fresh dump restarts this tag
        buf = bufs.get(tag)
        if buf is None or off != len(buf):
            bufs.pop(tag, None)       # torn or out-of-order dump, drop it
            continue
        buf.extend(data)
        if len(buf) == NODE_SIZE:
            done[tag] = bytes(buf)
            bufs.pop(tag)
    return done


def diff_ranges(a, b):
    """Yield (start, end_exclusive) byte ranges where a and b differ (contiguous runs)."""
    start = None
    for i in range(min(len(a), len(b))):
        if a[i] != b[i]:
            if start is None:
                start = i
        elif start is not None:
            yield (start, i)
            start = None
    if start is not None:
        yield (start, min(len(a), len(b)))


def annotate(start, end):
    notes = [note for off, note in KNOWN.items() if start <= off < end]
    if SKIPPED_LO <= start < SKIPPED_HI or SKIPPED_LO < end <= SKIPPED_HI:
        notes.append("inside the SKIPPED node+0x270 sub-block span (the prime suspect)")
    return "; ".join(notes)


def report(dumps, out=print):
    if "sibling" not in dumps or "frank" not in dumps:
        out(f"need BOTH dumps; found: {sorted(dumps) or 'none'} (run F5 in battle 435 first)")
        return 1
    sib, frank = dumps["sibling"], dumps["frank"]
    ranges = list(diff_ranges(sib, frank))
    out(f"nodes differ in {len(ranges)} range(s), {sum(e - s for s, e in ranges)} byte(s) total:")
    for s, e in ranges:
        note = annotate(s, e)
        out(f"  +0x{s:03X}..+0x{e - 1:03X} ({e - s:3d}B)  sibling {sib[s:e].hex(' ')}  frank {frank[s:e].hex(' ')}"
            + (f"   <- {note}" if note else ""))
    return 0


def _selftest():
    def mkline(tag, off, data):
        h = " ".join(f"{x:02X}" for x in data)
        return f"12:00:00.000 DBG body-double: dump[{tag}] +0x{off:03X}: {h}"

    ok = True

    def check(name, cond):
        nonlocal ok
        print(f"  {name}  {'OK' if cond else 'FAIL'}")
        ok = ok and cond

    a = bytes(range(256)) * 5 + bytes(72)            # 1352 bytes
    b = bytearray(a)
    b[0x11] = 0xEE                                   # a known stage-2 offset
    b[0x271] = 0xAA                                  # inside the skipped block
    b[0x272] = 0xBB
    lines = []
    for off in range(0, NODE_SIZE, 16):
        lines.append(mkline("sibling", off, a[off:off + 16]))
        lines.append(mkline("frank", off, bytes(b)[off:off + 16]))
    dumps = parse_dumps(lines)
    check("parses both full dumps", set(dumps) == {"sibling", "frank"})
    check("round-trips sibling bytes", dumps.get("sibling") == a)
    ranges = list(diff_ranges(dumps["sibling"], dumps["frank"]))
    check("finds the two diff ranges", ranges == [(0x11, 0x12), (0x271, 0x273)])
    check("annotates the known offset", "stage-2" in annotate(0x11, 0x12))
    check("annotates the skipped block", "0x270" in annotate(0x271, 0x273))
    check("last row is 8 bytes", len(a[0x540:]) == 8)

    torn = lines[: len(lines) - 1]                   # frank's dump missing its last row
    dumps2 = parse_dumps(torn)
    check("torn dump is not reported complete", "frank" not in dumps2 and "sibling" in dumps2)

    lines3 = lines + [mkline("frank", 0, b"\x99" * 16)]   # a restarted dump that never completes
    dumps3 = parse_dumps(lines3)
    check("restart keeps the last COMPLETE dump", dumps3.get("frank") == bytes(b))

    combat = [f"12:00:00.000 DBG body-double: combat[frank] +0x{o:03X}: " + " ".join("AB" for _ in range(16))
              for o in range(0, 0x200, 16)]
    dumps4 = parse_dumps(lines + combat)                  # forensics-file shape: nodes + combat snapshots
    check("combat[...] snapshot lines are ignored", set(dumps4) == {"sibling", "frank"})
    check("combat lines do not corrupt the node dumps", dumps4.get("frank") == bytes(b))

    print("selftest", "PASSED" if ok else "FAILED")
    return 0 if ok else 1


def main(argv):
    if len(argv) == 2 and argv[1] == "--selftest":
        return _selftest()
    if len(argv) != 2:
        print(__doc__)
        return 2
    with open(argv[1], encoding="utf-8", errors="replace") as f:
        dumps = parse_dumps(f)
    return report(dumps)


if __name__ == "__main__":
    sys.exit(main(sys.argv))
