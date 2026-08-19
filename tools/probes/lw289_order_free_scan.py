#!/usr/bin/env python
"""LW-289 round 7 (READ-ONLY): find the palette table WITHOUT assuming item-id order or a stride.

WHY THIS EXISTS. Every search so far assumed the renderer keeps the weapon palette values as a
contiguous run in item-id order. Between the two of us that assumption has now been swept hard and
come up empty:

  Joe, static side : fft_enhanced.exe (362 MB), 6 encodings x both endiannesses x strides 1-64,
                     zero hits. All 192 nxd tables, same sweep, zero hits, and there is no item or
                     weapon nxd at all. His blind spot is .xdata: 349 MB at entropy 6.69, 96.6% of
                     the file, Denuvo-wrapped and therefore invisible to a plaintext scan.
  Frank, live side : a full-length scan of 4108 MB of committed memory in 6 forms found only the
                     PSX record block itself, in copies of battle_bin, and poking those does
                     nothing on screen.

Joe named the gap: **a table sorted by graphic, or by category, or by anything other than item id
defeats every search either of us has run.** This probe closes that gap.

THE IDEA. A multiset is order independent. The 127 weapon palette values have a distinctive
histogram, and any re-ordering of the table preserves it exactly:

    palette : 3  4  5  6  7  8  9 10 11 12 13 14 15
    count   : 8  9  6  7  6 10  5  9  7  5 18 20 17

So instead of hunting a byte sequence, hunt any 127-element window, at any stride, whose value
multiset equals that histogram. Sorting the table cannot hide it. Only changing the VALUES can.

WHY IT IS NOT SWAMPED BY FALSE POSITIVES. Every value lies in 3..15, so a window that matches must
first be 127 consecutive elements all inside a 13-value band. In generic data that alone is
vanishingly rare, which makes it a nearly free prefilter: mask, find runs, and only compute
histograms inside them. Everything else is skipped without ever building a histogram.

WHAT A HIT MEANS. A window whose multiset matches is a candidate table in unknown order. Reading
the actual order off it then tells us the sort key, and the sort key usually names the structure.
A near miss (a handful of elements different) is also reported, because the real table may carry
an extra row for item 0 or for the 128+ range, which shifts the counts slightly.

READ-ONLY: opens the process with QUERY_INFORMATION | VM_READ and never writes.

USAGE:
  python lw289_order_free_scan.py --selftest        # pure checks, no game
  python lw289_order_free_scan.py                   # strides 1,2,4,8,16 (the common struct sizes)
  python lw289_order_free_scan.py --strides 1-64    # everything, slower
  python lw289_order_free_scan.py --tol 6           # also report windows within N of the histogram
Hits land in lw289_order_free_hits.json beside this file.
"""
import ctypes as C
import json
import os
import sys
from collections import Counter

import numpy as np

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from lw289_battle_bin_palette_map import (  # noqa: E402
    FIRST_ITEM, LAST_ITEM, extract, gate, record_offset,
)
from lw289_palette_table_scan import (  # noqa: E402
    MBI, MEM_COMMIT, PAGE_GUARD, PAGE_NOACCESS, PROT, find_pid, k32, rpm,
)

HERE = os.path.dirname(os.path.abspath(__file__))
OUT = os.path.join(HERE, "lw289_order_free_hits.json")
PROCESS_QUERY_VM = 0x0400 | 0x0010
N = LAST_ITEM - FIRST_ITEM + 1          # 127 weapons
CHUNK = 32 << 20
SLICE = 1 << 20          # max elements histogrammed at once; caps peak RAM at about 64 MB


def target_hist(raw):
    xs = [raw[record_offset(i)] >> 4 for i in range(FIRST_ITEM, LAST_ITEM + 1)]
    return Counter(xs), xs


def hist_vec(counter):
    v = np.zeros(16, dtype=np.int32)
    for k, n in counter.items():
        v[k] = n
    return v


def scan_buffer(buf, tgt_vec, lo, hi, strides, tol, base_va, out):
    """buf is a uint8 array. For each stride and phase, prefilter to runs of N consecutive
    in-band elements, then roll a histogram inside those runs only."""
    for s in strides:
        for phase in range(s):
            arr = buf[phase::s]
            if arr.size < N:
                continue
            valid = (arr >= lo) & (arr <= hi)
            if not valid.any():
                continue
            # run-length encode the valid mask, keep runs long enough to hold a window
            d = np.diff(np.concatenate(([0], valid.view(np.int8), [0])))
            starts = np.flatnonzero(d == 1)
            ends = np.flatnonzero(d == -1)
            for a, b in zip(starts, ends):
                if b - a < N:
                    continue
                # SLICE LONG RUNS. The histogram step costs 64 bytes per element (16 int32 bins),
                # so an unbounded run eats memory without limit. This bit for real: a control run
                # whose value band happened to include 0 made every zero-filled page one giant
                # in-band run and drove three scan processes to about 10 GB each before they were
                # killed. The real target's band is 3..15, which excludes zero pages and is why
                # the production scan never hit it, but a tool must not depend on its input being
                # lucky. SLICE caps the allocation at SLICE*64 bytes regardless of run length.
                for lo_i in range(a, b - N + 1, SLICE - N + 1):
                    hi_i = min(b, lo_i + SLICE)
                    seg = arr[lo_i:hi_i].astype(np.int64)
                    if seg.size < N:
                        break
                    onehot = np.zeros((seg.size, 16), dtype=np.int32)
                    onehot[np.arange(seg.size), seg] = 1
                    cs = np.cumsum(onehot, axis=0)
                    win = cs[N - 1:] - np.concatenate(([np.zeros(16, dtype=np.int32)], cs[:-N]))
                    diff = np.abs(win - tgt_vec).sum(axis=1)
                    for idx in np.flatnonzero(diff <= tol):
                        va = base_va + (lo_i + idx) * s + phase
                        out.append({"addr": int(va), "stride": int(s), "phase": int(phase),
                                    "diff": int(diff[idx]),
                                    "values": arr[lo_i + idx: lo_i + idx + N].tolist()})


def scan(strides, tol):
    pid = find_pid()
    if not pid:
        sys.exit("fft_enhanced.exe is not running")
    h = k32.OpenProcess(PROCESS_QUERY_VM, False, pid)
    if not h:
        sys.exit(f"OpenProcess failed {C.get_last_error()}")
    raw = extract(os.path.join(os.environ.get("TEMP", "."), "lw289"))
    gate(raw)
    tgt, xs = target_hist(raw)
    lo, hi = min(xs), max(xs)
    tgt_vec = hist_vec(tgt)
    print(f"pid {pid}; target histogram {dict(sorted(tgt.items()))}")
    print(f"values lie in {lo}..{hi}; window {N}; strides {strides}; tolerance {tol}", flush=True)

    out, scanned, regions = [], 0, 0
    addr = 0
    maxstride = max(strides)
    while addr < 0x7FFFFFFFFFFF:
        mbi = MBI()
        if not k32.VirtualQueryEx(h, C.c_void_p(addr), C.byref(mbi), C.sizeof(mbi)):
            break
        base = mbi.BaseAddress or 0
        nxt = base + mbi.RegionSize
        if (mbi.State == MEM_COMMIT and mbi.Protect
                and not (mbi.Protect & (PAGE_NOACCESS | PAGE_GUARD))):
            regions += 1
            pos = base
            overlap = N * maxstride
            while pos < nxt:
                chunk = rpm(h, pos, min(CHUNK, nxt - pos))
                if chunk is None:
                    break
                scanned += len(chunk)
                buf = np.frombuffer(chunk, dtype=np.uint8)
                scan_buffer(buf, tgt_vec, lo, hi, strides, tol, pos, out)
                if len(chunk) < CHUNK:
                    break
                pos += len(chunk) - overlap     # overlap so a window never straddles a boundary
        addr = nxt if nxt > addr else addr + 0x1000
    k32.CloseHandle(h)
    return out, scanned, regions, xs


def report(hits, scanned, regions, xs):
    print(f"scanned {scanned / (1 << 20):.0f} MB across {regions} committed regions")
    if not hits:
        print("\nNO WINDOW anywhere in memory has the right value multiset, at any tested stride.")
        print("That is a strong negative: re-ordering cannot hide a multiset, so the renderer does")
        print("not hold these 127 values as bytes in ANY order at these strides. The values are")
        print("either transformed (widened, offset, or packed with other fields inside a byte) or")
        print("never materialised as a table at all and are looked up per draw.")
        return
    exact = [x for x in hits if x["diff"] == 0]
    near = [x for x in hits if x["diff"] > 0]
    print(f"\nEXACT multiset matches: {len(exact)}")
    for x in sorted(exact, key=lambda z: z["addr"])[:40]:
        same_order = x["values"] == xs
        print(f"   0x{x['addr']:012X}  stride {x['stride']:2}  "
              f"{'SAME ORDER as item id' if same_order else 'DIFFERENT ORDER, sort key unknown'}")
    if near:
        print(f"\nnear misses (multiset off by <= tolerance): {len(near)}")
        for x in sorted(near, key=lambda z: (z['diff'], z['addr']))[:20]:
            print(f"   0x{x['addr']:012X}  stride {x['stride']:2}  off by {x['diff']}")


def selftest():
    raw = bytearray(0x40000)
    xs = [(3 + (i * 7) % 13) for i in range(N)]
    for k, i in enumerate(range(FIRST_ITEM, LAST_ITEM + 1)):
        raw[record_offset(i)] = xs[k] << 4
        raw[record_offset(i) + 1] = k % 251
    tgt, got = target_hist(bytes(raw))
    assert got == xs, "target extraction disagrees with the fixture"
    vec = hist_vec(tgt)
    assert vec.sum() == N, "histogram does not sum to the weapon count"

    # a buffer holding the values SHUFFLED must still be found: that is the whole point
    rng = np.random.default_rng(7)
    shuffled = list(xs)
    rng.shuffle(shuffled)
    assert shuffled != xs, "fixture shuffle was a no-op"
    buf = np.zeros(4096, dtype=np.uint8)
    buf[:] = 200                                    # out of band filler
    buf[1000:1000 + N] = shuffled
    out = []
    scan_buffer(buf, vec, min(xs), max(xs), [1], 0, 0, out)
    assert any(o["addr"] == 1000 for o in out), "an out-of-order copy was NOT found; probe is blind"
    assert all(o["values"] != xs for o in out if o["addr"] == 1000), "fixture was not shuffled"

    # strided placement must be found too
    buf2 = np.full(8192, 200, dtype=np.uint8)
    for k, v in enumerate(xs):
        buf2[500 + k * 4] = v
    out2 = []
    scan_buffer(buf2, vec, min(xs), max(xs), [4], 0, 0, out2)
    assert any(o["addr"] == 500 and o["stride"] == 4 for o in out2), "strided copy not found"

    # and a buffer with the right VALUES but the wrong COUNTS must NOT match at tolerance 0
    bad = list(xs)
    bad[0] = bad[1]
    buf3 = np.full(4096, 200, dtype=np.uint8)
    buf3[100:100 + N] = bad
    out3 = []
    scan_buffer(buf3, vec, min(xs), max(xs), [1], 0, 0, out3)
    assert not out3, "a wrong-histogram window matched at tolerance 0; the test is vacuous"
    # A run far longer than one slice must still be searched end to end, and must not blow up.
    big = np.full(SLICE * 2 + 5000, 7, dtype=np.uint8)      # one enormous in-band run
    plant = SLICE + 1234
    big[plant:plant + N] = shuffled
    out4 = []
    scan_buffer(big, vec, min(xs), max(xs), [1], 0, 0, out4)
    assert any(o["addr"] == plant for o in out4),         "a window past the first slice was missed; the slicing loop drops data"
    print("selftest OK")
    print("  finds shuffled copies, strided copies, and rejects a 2-element-off histogram")
    print(f"  finds a window at offset {plant} inside a {big.size}-element run (slice cap {SLICE})")


def parse_strides(argv):
    if "--strides" not in argv:
        return [1, 2, 4, 8, 16]
    spec = argv[argv.index("--strides") + 1]
    if "-" in spec:
        a, b = spec.split("-")
        return list(range(int(a), int(b) + 1))
    return [int(x) for x in spec.split(",")]


def main():
    if "--selftest" in sys.argv:
        selftest()
        return
    selftest()
    tol = int(sys.argv[sys.argv.index("--tol") + 1]) if "--tol" in sys.argv else 0
    hits, scanned, regions, xs = scan(parse_strides(sys.argv), tol)
    report(hits, scanned, regions, xs)
    json.dump(hits, open(OUT, "w", encoding="utf-8"), indent=1)
    print(f"\nwrote {OUT}")


if __name__ == "__main__":
    main()
