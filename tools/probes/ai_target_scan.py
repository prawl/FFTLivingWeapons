#!/usr/bin/env python
"""
AI-Data struct scanner: find the IC engine's AI working struct by heap fingerprint.

Ground truth is the PSX disassembly (FFHacktics "AI ability use control routine"), which IC
is expected to port faithfully. The struct holds THREE consecutive per-unit byte arrays of
0x15 (21) entries each:

    +0x0C78  isTargetable   (1 byte per unit, 0/1)
    +0x0C8D  working copy   (byte-equal to isTargetable at ability-init; 0x0C8D-0x0C78 = 0x15)
    +0x0CA2  isTarget       (0x0CA2-0x0C8D = 0x15 -- three back-to-back arrays)

Struct-relative header/bonus fields (scoring only, never hard-required -- IC may have moved
or widened them):

    +0x00    skillset (u8)          +0x1D  last skillset (u8)
    +0x02    ability id (u16)       +0x1E  last ability id (u16)
    +0x06    item id (u8)
    +0x0CB4  AI-flags copy (u32)
    +0x0CC4  per-unit pointer table (PSX: 21 x 4 bytes; IC x64 may widen to 8 or drop it --
             8-byte values pointing into the 0x141850000-0x141880000 combat region are a
             confidence bonus, not a requirement)
    +0x0E2D  targeted unit id (u8, < 0x15)
    +0x0E2E  acting unit id (u8, < 0x15)

PRIMARY signature = the three 0x15-spaced arrays: all bytes in {0,1}, arrays 1 and 2
byte-EQUAL, >= 3 nonzero entries (a live battle has 3+ units). Everything else is a score
bonus. A secondary pass tries strides 0x18 and 0x1C in case IC widened the unit roster
(reported separately; for those strides the +0x0C78 base offset is still the PSX guess, so
header bonuses may not line up).

Scans committed RW regions: private (heap) plus image .data/.bss -- the known combat
statics live at 0x1418xxxxx inside the module, so image RW must be included.

USAGE (game running; scan/watch during a live battle, ideally an ENEMY turn):
    python ai_target_scan.py scan                 # find candidates, best first (all 3 strides)
    python ai_target_scan.py scan --stride 0x18   # single-stride pass only
    python ai_target_scan.py watch <base>         # 100ms loop, print arrays + ids on CHANGE
    python ai_target_scan.py mask <base> <keepIdx>  # TAUNT EXPERIMENT (writes, reversible):
        # every 50ms zero every isTargetable entry != keepIdx that reads 1 (originals saved);
        # Ctrl+C restores every byte it zeroed. If the enemy AI converges on unit keepIdx,
        # taunt is PROVEN at the decision layer.
    # watch/mask also accept --stride 0x18 / 0x1C for the widened-roster fallback.
"""
import ctypes as C
from ctypes import wintypes as W
import os
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from ct_probe import PROC, PV, PV_W, find_pid, k32, rd, wr

OFF_ARR = 0x0C78         # isTargetable (array 1); arrays 2/3 follow at +stride, +2*stride
OFF_SKILLSET = 0x00
OFF_ABILITY = 0x02
OFF_ITEM = 0x06
OFF_LAST_SK = 0x1D
OFF_LAST_AB = 0x1E
OFF_FLAGS = 0x0CB4
OFF_PTRS = 0x0CC4
OFF_TARGETED = 0x0E2D
OFF_ACTING = 0x0E2E
HDR_SPAN = 0x0E40        # covers everything above

COMBAT_LO, COMBAT_HI = 0x141850000, 0x141880000   # authoritative combat region (ptr bonus)

CHUNK = 0x200000
MAX_PER_REGION = 200     # dense periodic junk guard -- stop a region after this many hits
TOP_N = 25


def u16(b, o): return b[o] | (b[o + 1] << 8)
def u32(b, o): return int.from_bytes(b[o:o + 4], "little")
def u64(b, o): return int.from_bytes(b[o:o + 8], "little")


# ---------------------------------------------------------------- region walk
class MBI(C.Structure):
    _fields_ = [("BaseAddress", C.c_void_p), ("AllocationBase", C.c_void_p),
                ("AllocationProtect", W.DWORD), ("PartitionId", W.WORD),
                ("RegionSize", C.c_size_t), ("State", W.DWORD),
                ("Protect", W.DWORD), ("Type", W.DWORD)]


def walk_regions(h):
    """Committed, non-guard, RW regions: MEM_PRIVATE (heap) + MEM_IMAGE (.data/.bss)."""
    regions = []
    addr = 0
    mbi = MBI()
    while addr < 0x7FFFFFFF0000:
        if not k32.VirtualQueryEx(h, C.c_void_p(addr), C.byref(mbi), C.sizeof(mbi)):
            break
        base = mbi.BaseAddress or 0
        size = mbi.RegionSize
        if size == 0:
            break
        if (mbi.State == 0x1000 and not (mbi.Protect & 0x100)
                and (mbi.Protect & 0xFF) in (0x04, 0x08, 0x40, 0x80)
                and mbi.Type in (0x20000, 0x1000000)
                and size < 0x20000000):
            regions.append((base, size))
        addr = base + size
    return regions


# ------------------------------------------------------------------ signature
def scan_chunk(data, s):
    """Relative offsets of array-1 starts matching the primary signature with stride s.

    Cheap short-circuit: hop between 0x01 bytes (bytes.find is C-speed); a 1 at q in
    array 1 forces data[q+s] == 1 (arrays 1/2 byte-equal) and data[q+2s] in {0,1}
    (array 3 entry). Only offsets surviving that 3-point filter get the full window check.
    """
    out = []
    n = len(data)
    lim = n - 3 * s
    tested_hi = -1                       # each start is fully checked at most once
    ok = {0, 1}
    pos = data.find(b"\x01")
    while pos != -1:
        if pos + 2 * s < n and data[pos + s] == 1 and data[pos + 2 * s] <= 1:
            lo = max(0, pos - s + 1, tested_hi + 1)
            hi = min(pos, lim)
            for i in range(lo, hi + 1):
                a1 = data[i:i + s]
                if a1 != data[i + s:i + 2 * s]:
                    continue
                if not set(a1) <= ok:
                    continue
                ones = a1.count(1)
                # >= 3 nonzero (live battle); at least one zero kills constant-1 runs
                # (a real 21-slot roster is never 100% targetable)
                if ones < 3 or ones > s - 1:
                    continue
                if not set(data[i + 2 * s:i + 3 * s]) <= ok:
                    continue
                out.append(i)
                if len(out) >= MAX_PER_REGION:
                    return _collapse(out)
            tested_hi = hi
        pos = data.find(b"\x01", pos + 1)
    return _collapse(out)


def _collapse(hits):
    """Keep only the LAST hit of each run of consecutive offsets. Zeros preceding a real
    struct extend the arr1==arr2 periodicity backwards, so every start in the zero-run
    also matches -- the true base is the run's last element (scoring re-ranks regardless)."""
    return [x for j, x in enumerate(hits) if j + 1 == len(hits) or hits[j + 1] != x + 1]


def find_candidates(h, regions, strides):
    """{stride: [absolute array-1 addresses]} -- one read pass, all strides per chunk."""
    found = {s: [] for s in strides}
    seen = {s: set() for s in strides}
    span = 3 * max(strides)
    for base, size in regions:
        off = 0
        while off < size:
            n = min(CHUNK + span, size - off)
            data = rd(h, base + off, n)
            if data and b"\x01" in data:
                for s in strides:
                    for rel in scan_chunk(data, s):
                        a = base + off + rel
                        if a not in seen[s]:
                            seen[s].add(a)
                            found[s].append(a)
            off += CHUNK
    return found


# -------------------------------------------------------------------- scoring
def score_candidate(h, arr_addr, s):
    c = {"base": arr_addr - OFF_ARR, "arr": arr_addr, "score": 0, "why": [], "hdr": None}
    hdr = rd(h, c["base"], HDR_SPAN)
    if hdr:
        c["hdr"] = hdr
        arrs = hdr[OFF_ARR:OFF_ARR + 3 * s]
    else:
        c["why"].append("header span unreadable -- arrays-only score")
        arrs = rd(h, arr_addr, 3 * s) or b""
    c["arrs"] = arrs
    if len(arrs) < 3 * s:
        return c
    a1, a3 = arrs[:s], arrs[2 * s:3 * s]
    if all(not (a3[i] and not a1[i]) for i in range(s)):
        c["score"] += 2
        c["why"].append("arr3 subset of arr1 (+2)")
    if not hdr:
        return c
    ab, last_ab = u16(hdr, OFF_ABILITY), u16(hdr, OFF_LAST_AB)
    if 0 < ab < 0x400:
        c["score"] += 1
        c["why"].append(f"ability id 0x{ab:03X} sane (+1)")
    if 0 < last_ab < 0x400:
        c["score"] += 1
        c["why"].append(f"last ability id 0x{last_ab:03X} sane (+1)")
    if hdr[OFF_SKILLSET] <= 0xE0 and hdr[OFF_LAST_SK] <= 0xE0:
        c["score"] += 1
        c["why"].append("skillset bytes sane (+1)")
    if u32(hdr, OFF_FLAGS):
        c["score"] += 1
        c["why"].append(f"AI flags 0x{u32(hdr, OFF_FLAGS):08X} nonzero (+1)")
    nptr = sum(1 for k in range(s)
               if COMBAT_LO <= u64(hdr, OFF_PTRS + 8 * k) < COMBAT_HI)
    c["nptr"] = nptr
    if nptr >= 3:
        c["score"] += 3
        c["why"].append(f"ptr table: {nptr}/{s} x64 ptrs into combat region (+3)")
    tgt, act = hdr[OFF_TARGETED], hdr[OFF_ACTING]
    c["tgt"], c["act"] = tgt, act
    if tgt < s:
        c["score"] += 2
        c["why"].append(f"targeted id {tgt} < {s} (+2)")
    if act < s:
        c["score"] += 2
        c["why"].append(f"acting id {act} < {s} (+2)")
    return c


def arr_str(b):
    return "".join("01?"[min(v, 2)] for v in b)


def print_candidate(rank, c, s):
    print(f"#{rank:<2} score {c['score']:>2}  base 0x{c['base']:012X}  (arr1 @ 0x{c['arr']:012X})")
    arrs = c["arrs"]
    if len(arrs) >= 3 * s:
        print(f"    arr1 {arr_str(arrs[:s])}  arr2 {'==' if arrs[:s] == arrs[s:2 * s] else '!!'}"
              f"  arr3 {arr_str(arrs[2 * s:3 * s])}")
    hdr = c["hdr"]
    if hdr:
        print(f"    hdr: skillset=0x{hdr[OFF_SKILLSET]:02X} ability=0x{u16(hdr, OFF_ABILITY):04X}"
              f" item=0x{hdr[OFF_ITEM]:02X} lastSk=0x{hdr[OFF_LAST_SK]:02X}"
              f" lastAb=0x{u16(hdr, OFF_LAST_AB):04X}"
              f"  targeted={c.get('tgt', '?')} acting={c.get('act', '?')}")
    if c["why"]:
        print("    " + "; ".join(c["why"]))


def cmd_scan(h, strides):
    regions = walk_regions(h)
    total = sum(sz for _, sz in regions)
    print(f"{len(regions)} committed RW regions (private+image), {total / 2**20:.0f} MiB. "
          f"strides: {', '.join(hex(s) for s in strides)}")
    t0 = time.time()
    found = find_candidates(h, regions, strides)
    print(f"read+signature pass: {time.time() - t0:.1f}s\n")
    for s in strides:
        cands = found[s]
        tag = "PRIMARY (PSX 0x15)" if s == 0x15 else f"widened-roster fallback (0x{s:X})"
        print(f"=== stride 0x{s:02X} -- {tag}: {len(cands)} candidate(s) ===")
        if s != 0x15 and cands:
            print("    (base = arr1 - 0x0C78 is the PSX-layout guess; header bonuses may not apply)")
        scored = sorted((score_candidate(h, a, s) for a in cands), key=lambda c: -c["score"])
        for rank, c in enumerate(scored[:TOP_N], 1):
            print_candidate(rank, c, s)
        if len(scored) > TOP_N:
            print(f"    ... {len(scored) - TOP_N} more suppressed")
        print()


# ---------------------------------------------------------------- watch / mask
def cmd_watch(h, base, s):
    print(f"watching base 0x{base:012X} stride 0x{s:02X} @ 100ms -- printing on CHANGE. "
          f"run an enemy turn; Ctrl+C to stop.")
    last = None
    try:
        while True:
            arrs = rd(h, base + OFF_ARR, 3 * s)
            ids = rd(h, base + OFF_TARGETED, 2)
            snap = (arrs, ids)
            if snap != last:
                last = snap
                t = time.strftime("%H:%M:%S")
                if arrs is None:
                    print(f"{t}  arrays UNREADABLE (struct freed / battle over?)")
                else:
                    tgt = ids[0] if ids else "?"
                    act = ids[1] if ids else "?"
                    print(f"{t}  arr1 {arr_str(arrs[:s])}  arr2 {arr_str(arrs[s:2 * s])}"
                          f"  arr3 {arr_str(arrs[2 * s:3 * s])}  targeted={tgt} acting={act}")
            time.sleep(0.1)
    except KeyboardInterrupt:
        print("stopped.")


def cmd_mask(h, base, keep, s):
    """TAUNT EXPERIMENT: force the AI's isTargetable list down to unit <keep>.
    Saves every byte before zeroing it; Ctrl+C restores (guarded: only bytes that
    still read 0, i.e. our write, are put back)."""
    if not 0 <= keep < s:
        print(f"keepIdx must be 0..{s - 1}")
        return
    arr = rd(h, base + OFF_ARR, s)
    if not arr or not set(arr) <= {0, 1} or arr.count(1) < 1:
        print(f"base 0x{base:012X} does not look like a live isTargetable array "
              f"(bytes: {arr.hex() if arr else 'unreadable'}) -- refusing to write")
        return
    print(f"masking isTargetable @ 0x{base + OFF_ARR:012X}, keeping unit {keep} "
          f"(reads {arr[keep]}). re-asserting every 50ms; Ctrl+C restores.")
    saved = {}
    try:
        while True:
            arr = rd(h, base + OFF_ARR, s)
            if arr is None:
                time.sleep(0.05)
                continue
            for i in range(s):
                if i == keep or arr[i] != 1:
                    continue
                if i not in saved:
                    saved[i] = arr[i]
                    print(f"  masked unit {i} (isTargetable 1 -> 0)")
                wr(h, base + OFF_ARR + i, b"\x00")
            time.sleep(0.05)
    except KeyboardInterrupt:
        print(f"restoring {len(saved)} masked byte(s)...")
        for i, orig in sorted(saved.items()):
            a = base + OFF_ARR + i
            cur = rd(h, a, 1)
            if cur is None:
                print(f"  unit {i}: unreadable -- skipped")
            elif cur[0] == 0:
                wr(h, a, bytes([orig]))
                back = rd(h, a, 1)
                print(f"  unit {i}: restored -> {back[0] if back else '?'}")
            else:
                print(f"  unit {i}: reads {cur[0]} (engine rewrote it) -- left as-is")
        print("done.")


def main():
    argv = sys.argv[1:]
    stride, explicit = 0x15, False
    if "--stride" in argv:
        i = argv.index("--stride")
        try:
            stride = int(argv[i + 1], 0)
            explicit = True
        except (IndexError, ValueError):
            print("--stride needs a value (e.g. --stride 0x18)")
            return
        del argv[i:i + 2]
    mode = argv[0] if argv else ""
    if mode not in ("scan", "watch", "mask"):
        print(__doc__)
        return
    pid = find_pid(PROC)
    if not pid:
        print(f"{PROC} not running")
        return
    h = k32.OpenProcess(PV_W if mode == "mask" else PV, False, pid)
    if not h:
        print(f"OpenProcess failed err={C.get_last_error()}")
        return
    try:
        if mode == "scan":
            cmd_scan(h, [stride] if explicit else [0x15, 0x18, 0x1C])
        elif mode == "watch":
            cmd_watch(h, int(argv[1], 0), stride)
        else:
            cmd_mask(h, int(argv[1], 0), int(argv[2], 0), stride)
    finally:
        k32.CloseHandle(h)


if __name__ == "__main__":
    main()
