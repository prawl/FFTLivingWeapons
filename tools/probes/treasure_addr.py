"""Treasure Master PHASE 2: solve tile (x,y) -> per-tile mark-flag ADDRESS.

The mark mechanism is solved (bit 0x80 of a per-tile status byte; write+hold renders it).
What's open is ADDRESSING: the flag lives on the heap and rebases every battle, so the DLL
can't hardcode it. This probe derives the (x,y)->address LAYOUT and seeds the per-battle
base resolver. RPM/WPM only -- it cannot crash the game.

Run this in YOUR OWN terminal (it's interactive). Hover the tile in-game, alt-tab here on
each prompt -- the mark persists in memory regardless of window focus.

  python tools\\probes\\treasure_addr.py findflag
      Guided toggle-scan of the CURRENT cursor tile. Mark/unmark on each prompt; prints the
      flag address(es) (usually ~3 buffer copies) tagged real-data vs UI-render-arena, each
      stamped with the tile's (x,y). This is tile A.

  python tools\\probes\\treasure_addr.py windowdiff <centerAddr> <radiusBytes>
      With tile A's flag at <centerAddr>, hover a DIFFERENT known tile and run this. It snaps
      a window, you mark the new tile, it snaps again and reports the byte that gained 0x80 ->
      tile B's flag. delta = addrB - addrA over a known (dx,dy) gives the stride + row pitch.

  python tools\\probes\\treasure_addr.py region <addr>      # VirtualQueryEx: base/size/type/protect
  python tools\\probes\\treasure_addr.py ptrscan <addr>     # find 8-byte LE pointers == addr (chain seed)
  python tools\\probes\\treasure_addr.py read <addr> <n>    # hex dump
  python tools\\probes\\treasure_addr.py poke <addr> <hex>  # one-shot write
  python tools\\probes\\treasure_addr.py hold <addr> <hex> [s]   # write+hold (prove we can mark a tile)
"""
import ctypes
import ctypes.wintypes as w
import json
import os
import pathlib
import sys
import time

RESULTS = pathlib.Path(os.environ.get("TEMP", ".")) / "fft_treasure"
RESULTS.mkdir(exist_ok=True)

PROCESS_VM_READ = 0x0010
PROCESS_VM_WRITE = 0x0020
PROCESS_VM_OPERATION = 0x0008
PROCESS_QUERY_INFORMATION = 0x0400
MEM_COMMIT, MEM_IMAGE = 0x1000, 0x1000000
MEM_PRIVATE, MEM_MAPPED = 0x20000, 0x40000
PAGE_GUARD, PAGE_NOACCESS = 0x100, 0x01
WRITABLE = 0x04 | 0x08 | 0x40 | 0x80

IMAGE_BASE = 0x140000000
IMAGE_END = 0x143000000
MARK_BIT = 0x80
UI_ARENA = (0x140C63000, 0x140CC5000)   # the cursor/render region -- flags are NOT here

CURSOR_X, CURSOR_Y = 0x140C64A54, 0x140C6496C   # u8, validated vs the on-screen display

k32 = ctypes.windll.kernel32
psapi = ctypes.windll.psapi


class MBI(ctypes.Structure):
    _fields_ = [("BaseAddress", ctypes.c_void_p), ("AllocationBase", ctypes.c_void_p),
                ("AllocationProtect", w.DWORD), ("PartitionId", w.WORD),
                ("RegionSize", ctypes.c_size_t), ("State", w.DWORD),
                ("Protect", w.DWORD), ("Type", w.DWORD)]


def find_handle(name="fft_enhanced.exe"):
    arr = (w.DWORD * 4096)()
    needed = w.DWORD()
    psapi.EnumProcesses(ctypes.byref(arr), ctypes.sizeof(arr), ctypes.byref(needed))
    want = PROCESS_QUERY_INFORMATION | PROCESS_VM_READ | PROCESS_VM_WRITE | PROCESS_VM_OPERATION
    for i in range(needed.value // ctypes.sizeof(w.DWORD)):
        h = k32.OpenProcess(want, False, arr[i])
        if not h:
            continue
        buf = ctypes.create_unicode_buffer(260)
        if psapi.GetModuleBaseNameW(h, None, buf, 260) and buf.value.lower() == name.lower():
            return h
        k32.CloseHandle(h)
    return None


HANDLE = find_handle()
if not HANDLE:
    print("game not running (fft_enhanced.exe)")
    sys.exit(1)


def rpm(addr, n):
    buf = ctypes.create_string_buffer(n)
    got = ctypes.c_size_t()
    if not k32.ReadProcessMemory(HANDLE, ctypes.c_void_p(addr), buf, n, ctypes.byref(got)) or got.value != n:
        return None
    return buf.raw


def wpm(addr, data):
    n = ctypes.c_size_t()
    ok = k32.WriteProcessMemory(HANDLE, ctypes.c_void_p(addr), data, len(data), ctypes.byref(n))
    return bool(ok) and n.value == len(data)


def cursor():
    x, y = rpm(CURSOR_X, 1), rpm(CURSOR_Y, 1)
    return (x[0] if x else None, y[0] if y else None)


def query(addr):
    mbi = MBI()
    if not k32.VirtualQueryEx(HANDLE, ctypes.c_void_p(addr), ctypes.byref(mbi), ctypes.sizeof(mbi)):
        return None
    return mbi


def writable_regions():
    """(base, size, type) for committed, writable, non-guard regions in the module span."""
    out, addr, mbi = [], IMAGE_BASE, MBI()
    while addr < IMAGE_END:
        if not k32.VirtualQueryEx(HANDLE, ctypes.c_void_p(addr), ctypes.byref(mbi), ctypes.sizeof(mbi)):
            break
        base, size = mbi.BaseAddress or 0, mbi.RegionSize
        if (mbi.State == MEM_COMMIT and (mbi.Protect & WRITABLE)
                and not (mbi.Protect & (PAGE_GUARD | PAGE_NOACCESS))):
            out.append((base, size, mbi.Type))
        addr = base + size if base + size > addr else addr + 0x1000
    return out


def snapshot():
    # Skip the UI render arena: marking a tile re-renders its billboard, so that whole region
    # flips in lockstep and would drown the real per-tile flag in thousands of pixel bytes.
    return {base: rpm(base, size) for base, size, _ in writable_regions() if not in_ui(base)}


def in_ui(addr):
    return UI_ARENA[0] <= addr < UI_ARENA[1]


def prompt(msg):
    try:
        input(msg)
    except (EOFError, KeyboardInterrupt):
        print("\naborted.")
        sys.exit(0)


def togglefind(offs, ons):
    """Bytes whose 0x80 bit is CLEAR in every off snap and SET in every on snap."""
    hits = []
    bases = set(offs[0])
    for s in offs[1:] + ons:
        bases &= set(s)
    for base in sorted(bases):
        arrs_off = [s[base] for s in offs if s[base] is not None]
        arrs_on = [s[base] for s in ons if s[base] is not None]
        if len(arrs_off) != len(offs) or len(arrs_on) != len(ons):
            continue
        n = min(len(a) for a in arrs_off + arrs_on)
        o0, n0 = arrs_off[0], arrs_on[0]
        for ci in range(0, n, 4096):                      # C-speed skip of unchanged chunks
            if o0[ci:ci + 4096] == n0[ci:ci + 4096]:
                continue
            for i in range(ci, min(ci + 4096, n)):
                if (o0[i] ^ n0[i]) & MARK_BIT == 0:
                    continue
                if all((a[i] & MARK_BIT) == 0 for a in arrs_off) and all((a[i] & MARK_BIT) for a in arrs_on):
                    hits.append((base + i, o0[i], n0[i]))
    return hits


def clean_diff(base, snap):
    """Addresses where base has bit 0x80 CLEAR and snap == base|0x80 (a pure mark-bit set)."""
    out = []
    for b in base:
        ba, sa = base[b], snap.get(b)
        if ba is None or sa is None:
            continue
        n = min(len(ba), len(sa))
        for ci in range(0, n, 4096):
            if ba[ci:ci + 4096] == sa[ci:ci + 4096]:
                continue
            for i in range(ci, min(ci + 4096, n)):
                if (ba[i] & MARK_BIT) == 0 and sa[i] == (ba[i] | MARK_BIT):
                    out.append(b + i)
    return out


def fit_layout(points):
    """points: [(x,y,addr)] -> (xstride, ypitch, base, max_residual) via the first non-collinear
    triple, residuals checked across ALL points (0 == a clean linear array)."""
    x0, y0, a0 = points[0]
    for i in range(1, len(points)):
        for j in range(i + 1, len(points)):
            x1, y1, a1 = points[i]
            x2, y2, a2 = points[j]
            det = (x1 - x0) * (y2 - y0) - (x2 - x0) * (y1 - y0)
            if det == 0:
                continue
            d1, d2 = a1 - a0, a2 - a0
            xs = (d1 * (y2 - y0) - d2 * (y1 - y0)) / det
            yp = (d2 * (x1 - x0) - d1 * (x2 - x0)) / det
            base = a0 - xs * x0 - yp * y0
            maxres = max(abs(px * xs + py * yp + base - pa) for px, py, pa in points)
            return xs, yp, base, maxres
    return None


cmd = sys.argv[1] if len(sys.argv) > 1 else "help"

if cmd == "findflag":
    x, y = cursor()
    if x is None:
        print("cursor unreadable -- get a unit on the field and hover the tile first.")
        sys.exit(1)
    print(f"Finding the mark flag for the tile at (x={x}, y={y}).")
    print("On each prompt: alt-tab to the game, set the mark state, alt-tab back, Enter.\n")
    prompt("1/5  tile UNMARKED -> Enter ...")
    off0 = snapshot()
    prompt("2/5  MARK it (press 2), cursor still -> Enter ...")
    on0 = snapshot()
    prompt("3/5  UNMARK it -> Enter ...")
    off1 = snapshot()
    prompt("4/5  MARK it again -> Enter ...")
    on1 = snapshot()
    prompt("5/5  UNMARK it -> Enter ...")
    off2 = snapshot()
    print("\nscanning (UI render arena excluded)...")
    hits = togglefind([off0, off1, off2], [on0, on1])
    out = RESULTS / f"flag_{x}_{y}.json"
    out.write_text(json.dumps({"x": x, "y": y, "hits": [[a, o, n] for a, o, n in hits]}, indent=2))
    print(f"\ntile (x={x}, y={y}): {len(hits)} candidate flag byte(s)  ->  {out}")
    for addr, off, on in hits:
        print(f"  {addr:#x}   off={off:#04x} on={on:#04x}")
    if not hits:
        print("  (none -- did the cursor move during the cycle, or the mark not toggle cleanly?)")

elif cmd == "windowdiff":
    center, radius = int(sys.argv[2], 0), int(sys.argv[3], 0)
    lo = center - radius
    x, y = cursor()
    print(f"window [{lo:#x} .. {lo + 2 * radius:#x}] around {center:#x}. New tile at (x={x}, y={y}).")
    prompt("new tile UNMARKED -> Enter ...")
    before = rpm(lo, 2 * radius)
    prompt("MARK the new tile -> Enter ...")
    after = rpm(lo, 2 * radius)
    if not before or not after:
        print("window unreadable.")
        sys.exit(1)
    changed = [(lo + i, before[i], after[i]) for i in range(len(before)) if before[i] != after[i]]
    gained = [c for c in changed if (c[1] ^ c[2]) & MARK_BIT]
    print(f"\n{len(changed)} bytes changed; {len(gained)} flipped bit 0x80:")
    for addr, b, a in gained:
        print(f"  {addr:#x}   {b:#04x} -> {a:#04x}   delta_from_center={addr - center:+d}")
    if not gained:
        print("  (none -- the new tile's flag is outside this window; widen the radius)")

elif cmd == "region":
    addr = int(sys.argv[2], 0)
    mbi = query(addr)
    if not mbi:
        print("query failed")
    else:
        types = {MEM_IMAGE: "IMAGE", MEM_PRIVATE: "PRIVATE", MEM_MAPPED: "MAPPED"}
        print(f"  addr        {addr:#x}")
        print(f"  AllocBase   {mbi.AllocationBase or 0:#x}")
        print(f"  RegionBase  {mbi.BaseAddress or 0:#x}")
        print(f"  RegionSize  {mbi.RegionSize:#x} ({mbi.RegionSize / 1024:.0f} KB)")
        print(f"  Type        {types.get(mbi.Type, hex(mbi.Type))}")
        print(f"  Protect     {mbi.Protect:#x}")
        print(f"  offset-in-region {addr - (mbi.BaseAddress or 0):#x}")

elif cmd == "ptrscan":
    target = int(sys.argv[2], 0)
    needle = target.to_bytes(8, "little")
    hits = []
    for base, size, _ in writable_regions():
        buf = rpm(base, size)
        if not buf:
            continue
        i = buf.find(needle)
        while i != -1 and len(hits) < 200:
            if i % 8 == 0 or True:                # report all alignments; chain-walk decides
                hits.append(base + i)
            i = buf.find(needle, i + 1)
    print(f"{len(hits)} pointer(s) holding {target:#x}:")
    for a in hits:
        print(f"  {a:#x}")

elif cmd == "read":
    addr, n = int(sys.argv[2], 0), int(sys.argv[3], 0)
    data = rpm(addr, n)
    print(data.hex(" ") if data else "unreadable")

elif cmd == "poke":
    addr, data = int(sys.argv[2], 0), bytes.fromhex(sys.argv[3])
    print("OK" if wpm(addr, data) else "WRITE FAILED")

elif cmd in ("hold", "holdmany"):
    if cmd == "hold":
        targets = [(int(sys.argv[2], 0), bytes.fromhex(sys.argv[3]))]
        secs = float(sys.argv[4]) if len(sys.argv) > 4 else 8.0
    else:
        secs = float(sys.argv[2])
        targets = [(int(sys.argv[i], 0), bytes.fromhex(sys.argv[i + 1])) for i in range(3, len(sys.argv), 2)]
    saved = [(a, d, rpm(a, len(d))) for a, d in targets]
    for a, d, orig in saved:
        print(f"  hold {a:#x}: {orig.hex() if orig else '??'} -> {d.hex()}")
    t0, writes = time.time(), 0
    while time.time() - t0 < secs:
        for a, d in targets:
            wpm(a, d)
        writes += 1
    for a, _, orig in saved:
        if orig:
            wpm(a, orig)
    print(f"done: {writes} loops, originals restored.")

elif cmd == "layout":
    # Capture several tiles in ONE allocation, filtered to the cursor-STABLE store only
    # (survive proved it lives at ~0x142e_xxxx and does not relocate on cursor moves). That
    # filter excludes the volatile render buffers that contaminated multimark. One baseline,
    # then mark tiles cumulatively; a rebase of the stable store mid-run is detected and warned.
    # survive-in-a-loop against ONE baseline: per tile, mark (cursor still) then move; keep only
    # bytes that survive the move (the cursor-stable store). The move IS the filter that strips
    # the volatile render buffers -- a region filter alone can't (0x142e holds both). One baseline
    # keeps every tile in the same allocation, so the cross-tile deltas are clean.
    for f in RESULTS.glob("flag_*.json"):
        f.unlink()
    print("layout: per tile = MARK (cursor still) -> Enter, then MOVE cursor -> Enter.")
    prompt("baseline: NO tiles marked -> Enter ...")
    base = snapshot()
    known, captured = set(), []
    while True:
        try:
            line = input("hover+MARK next tile (cursor STILL), then Enter  |  'done': ").strip().lower()
        except (EOFError, KeyboardInterrupt):
            break
        if line == "done":
            break
        x, y = cursor()
        if x is None:
            print("  cursor unreadable -- hover a tile first.")
            continue
        a1 = set(clean_diff(base, snapshot()))
        try:
            input("    ...now MOVE the cursor a few tiles away, then Enter ...")
        except (EOFError, KeyboardInterrupt):
            break
        stable = a1 & set(clean_diff(base, snapshot()))   # survived the move = stable store
        lost = known - stable
        if lost:
            print(f"    *** {len(lost)} prior stable byte(s) moved (rebase) -- note it; re-run if many. ***")
        new = sorted(stable - known)
        known |= set(new)
        captured.append((x, y, new))
        (RESULTS / f"flag_{x}_{y}.json").write_text(
            json.dumps({"x": x, "y": y, "hits": [[a, 1, 0x81] for a in new]}))
        print(f"    ({x},{y}): +{len(new)} stable byte(s)   [{len(captured)} tiles]")
    print(f"\ncaptured {len(captured)} tiles. Run:  python tools\\probes\\treasure_addr.py solve")

elif cmd == "vpmatrix":
    # Find the view-projection matrix by rotating the camera: a 4x4 transform is 16 consecutive
    # floats; on a 90-degree iso rotation its rotation part flips while staying finite/bounded.
    # Snapshot -> rotate -> snapshot, then surface 16-float windows that mostly changed and look
    # like a transform (finite, bounded, several non-trivial entries). Inspect for VP structure.
    import struct
    print("VP-matrix hunt. Keep a battle on screen, cursor steady.")
    prompt("snapshot 1 -> Enter ...")
    a = snapshot()
    prompt("now ROTATE the camera ~90 degrees (press Q/E until the view turns), then Enter ...")
    b = snapshot()
    cands = []
    for base in sorted(set(a) & set(b)):
        da, db = a[base], b[base]
        if da is None or db is None:
            continue
        n = min(len(da), len(db))
        ci = 0
        while ci < n - 64:
            if da[ci:ci + 64] == db[ci:ci + 64]:
                ci += 64
                continue
            for j in range(ci, min(ci + 64, n - 64), 4):
                fa = struct.unpack_from("<16f", da, j)
                fb = struct.unpack_from("<16f", db, j)
                if not all(x == x and abs(x) < 1e5 for x in fa + fb):
                    continue
                changed = sum(1 for k in range(16) if fa[k] != fb[k])
                nontrivial = sum(1 for x in fa if abs(x) > 1e-3)
                # a transform: mostly-changed, several real entries, and bounded values (not huge floats)
                if changed >= 10 and nontrivial >= 6 and all(abs(x) < 5000 for x in fa + fb):
                    cands.append((base + j, fa, fb))
            ci += 64
    out = RESULTS / "vpmatrix_cands.json"
    out.write_text(json.dumps([[a, list(fa), list(fb)] for a, fa, fb in cands]))
    print(f"\n{len(cands)} matrix-like window(s) that changed on rotation  ->  {out}")
    for addr, fa, fb in cands[:25]:
        print(f"  {addr:#x}")
        print("    before:", [round(x, 2) for x in fa])
        print("    after :", [round(x, 2) for x in fb])

elif cmd == "neighbor":
    # Targeted single-window diff: read a window with ONLY the anchor marked, then with the anchor
    # PLUS one neighbor marked. The bytes that gain bit 0x80 are the neighbor's store bytes, and
    # their offset from the anchor's known address is the stride -- no whole-memory snapshot, no
    # cursor churn, one new mark. Anchor default = (0,1) = 0x142ebed94 (held steady all session).
    anchor = int(sys.argv[2], 0) if len(sys.argv) > 2 else 0x142EBED94
    radius = int(sys.argv[3], 0) if len(sys.argv) > 3 else 0x40000
    lo = anchor - radius
    prompt("mark ONLY the anchor tile (unmark everything else) -> Enter ...")
    before = rpm(lo, 2 * radius)
    prompt("now ALSO mark the neighbor tile (keep the anchor marked) -> Enter ...")
    after = rpm(lo, 2 * radius)
    if not before or not after:
        print("window unreadable.")
    else:
        new = [lo + i for i in range(len(before)) if (before[i] & MARK_BIT) == 0 and (after[i] & MARK_BIT)]
        print(f"\n{len(new)} byte(s) gained bit 0x80 (the neighbor's store bytes):")
        for a in new:
            d = a - anchor
            mult = f"  = {d // 432} x 432" if (d and d % 432 == 0) else ""
            print(f"  {a:#x}   delta from anchor = {d:+#x} ({d:+d}){mult}")

elif cmd == "findmarks":
    # Locate the CURRENT marked-tile array by unmarking: snapshot while several tiles are marked,
    # unmark them all, snapshot again; bytes that drop bit 0x80 (X -> X & ~0x80) are the per-tile
    # store wherever it lives now. A contiguous run of marks shows up as an evenly-spaced comb.
    prompt("tiles are MARKED now -> Enter (baseline) ...")
    base = snapshot()
    prompt("now UNMARK every tile (press 2 on each), then Enter ...")
    snap2 = snapshot()
    lost = []
    for b in base:
        ba, sa = base[b], snap2.get(b)
        if ba is None or sa is None:
            continue
        n = min(len(ba), len(sa))
        for ci in range(0, n, 4096):
            if ba[ci:ci + 4096] == sa[ci:ci + 4096]:
                continue
            for i in range(ci, min(ci + 4096, n)):
                if (ba[i] & MARK_BIT) and sa[i] == (ba[i] & 0x7F):
                    lost.append(b + i)
    lost.sort()
    print(f"\n{len(lost)} byte(s) went marked -> unmarked  (addr : delta-from-prev):")
    for i, a in enumerate(lost):
        d = a - lost[i - 1] if i else 0
        note = f"  = {d // 432} x 432" if (d and d % 432 == 0) else ""
        print(f"  {a:#x}   {hex(d) if d else '-':>9}{note}")

elif cmd == "pair":
    # Mark TWO ADJACENT tiles, then ONE far move to strip the volatile render bytes. Both tiles
    # are in the same allocation, so the small repeated gap between their stable bytes IS the
    # per-tile stride (x-stride for a horizontal pair, row-pitch for a vertical pair). No mapping,
    # no cross-run drift -- the cleanest way to read the layout off the metal.
    for f in RESULTS.glob("flag_*.json"):
        f.unlink()
    prompt("baseline: NO tiles marked -> Enter ...")
    base = snapshot()
    print("MARK two ADJACENT tiles: press 2 on one, move ONE tile over, press 2 on it.")
    prompt("   (both marked) -> Enter ...")
    a1 = set(clean_diff(base, snapshot()))
    prompt("now MOVE the cursor FAR across the map, then Enter ...")
    stable = sorted(a1 & set(clean_diff(base, snapshot())))
    print(f"\n{len(stable)} stable byte(s):")
    for a in stable:
        print(f"  {a:#x}")
    print("consecutive deltas (the small repeated one = the per-tile stride):")
    for i in range(1, len(stable)):
        d = stable[i] - stable[i - 1]
        print(f"  {d:#x}  ({d})")

elif cmd == "mapscan":
    # Low-burden capture: mark every tile (just mark+Enter each, free cursor), then ONE far move
    # at the end. We snapshot after each mark, and once at the end from a different cursor spot.
    # The stable store = bytes set vs baseline in BOTH the last-tile snap and the moved snap
    # (volatile render bytes relocate between the two cursor positions and drop out). Each tile's
    # stable bytes = what newly appears in that store at its step. All in one allocation -> clean.
    for f in RESULTS.glob("flag_*.json"):
        f.unlink()
    print("mapscan: mark tiles one by one (mark+Enter each). One FAR move at the very end.")
    prompt("baseline: NO tiles marked -> Enter ...")
    base = snapshot()
    rows = []
    while True:
        try:
            line = input("hover+MARK next tile, Enter  |  'move' when ALL tiles are marked: ").strip().lower()
        except (EOFError, KeyboardInterrupt):
            break
        if line == "move":
            break
        x, y = cursor()
        if x is None:
            print("  cursor unreadable -- hover a tile first.")
            continue
        rows.append((x, y, set(clean_diff(base, snapshot()))))
        print(f"  ({x},{y}) recorded   [{len(rows)} tiles]")
    if not rows:
        print("no tiles captured.")
    else:
        prompt("now MOVE the cursor FAR away (camera-scroll far; all tiles stay marked) -> Enter ...")
        final = set(clean_diff(base, snapshot()))
        stable_all = rows[-1][2] & final
        print(f"\nstable store: {len(stable_all)} byte(s) total across {len(rows)} tiles")
        prev = set()
        for x, y, cdf in rows:
            cum = cdf & stable_all
            if not (prev <= cum):
                print(f"  ({x},{y}): *** non-monotonic ({len(prev - cum)} lost) -- a rebase happened mid-run ***")
            new = sorted(cum - prev)
            prev = cum
            (RESULTS / f"flag_{x}_{y}.json").write_text(
                json.dumps({"x": x, "y": y, "hits": [[a, 1, 0x81] for a in new]}))
            print(f"  ({x},{y}): +{len(new)} stable byte(s)")
        print("\nRun:  python tools\\probes\\treasure_addr.py solve")

elif cmd == "survive":
    # Decisive test: does any copy of a tile's mark stay at the SAME address after the cursor
    # moves? If yes, that copy is a stable store the DLL can write; if all relocate, the mark
    # lives only in cursor-relative render buffers and the feature needs per-frame re-location.
    prompt("baseline: NO tiles marked, hover the test tile, cursor STILL -> Enter ...")
    base = snapshot()
    x, y = cursor()
    prompt(f"MARK ({x},{y}) (press 2), keep the cursor STILL -> Enter ...")
    a1 = set(clean_diff(base, snapshot()))
    print(f"  ({x},{y}) marked: {len(a1)} flag byte(s)")
    prompt("MOVE the cursor FAR away (the tile STAYS marked) -> Enter ...")
    a2 = set(clean_diff(base, snapshot()))
    survivors = sorted(a1 & a2)
    (RESULTS / f"flag_{x}_{y}.json").write_text(
        json.dumps({"x": x, "y": y, "hits": [[a, 1, 0x81] for a in survivors]}))
    print(f"\n{len(survivors)} of {len(a1)} byte(s) STILL hold the mark at the same address:")
    for a in survivors:
        print(f"  {a:#x}   <-- stable under cursor move")
    print(f"relocated/new: {len(a2 - a1)},  vanished: {len(a1 - a2)}  ->  saved survivors to flag_{x}_{y}.json")
    if not survivors:
        print("  -> NO stable copy this time (the store may have rebased; re-baseline and retry).")

elif cmd == "multimark":
    # Cumulative simultaneous marking, all diffed against ONE baseline -> every tile's flag is
    # in the same allocation, so cross-tile deltas are pure index (no per-capture drift).
    # Stay in a STATIC moment: hover & mark only; do NOT move a unit or end the turn.
    for f in RESULTS.glob("flag_*.json"):
        f.unlink()   # prior single-capture files drifted across allocations -- start clean
    print("multimark: capture several tiles in one allocation.")
    print("Make sure NO tiles are marked, then mark tiles one by one (each STAYS marked).\n")
    prompt("baseline (nothing marked) -> Enter ...")
    base = snapshot()
    known, captured = set(), []
    while True:
        try:
            line = input("MARK next tile (keep prior marks) then Enter  |  'done': ").strip().lower()
        except (EOFError, KeyboardInterrupt):
            break
        if line == "done":
            break
        x, y = cursor()
        if x is None:
            print("  cursor unreadable -- hover a tile first.")
            continue
        clean = set(clean_diff(base, snapshot()))
        lost = known - clean
        if lost:
            print(f"  *** DRIFT: {len(lost)} prior flag(s) moved -- allocation shifted. Abort & re-run. ***")
        new = sorted(clean - known)
        known |= set(new)
        captured.append((x, y, new))
        (RESULTS / f"flag_{x}_{y}.json").write_text(
            json.dumps({"x": x, "y": y, "hits": [[a, 1, 0x81] for a in new]}))
        print(f"  ({x},{y}): +{len(new)} flag byte(s)   [{len(captured)} tiles, {len(known)} bytes total]")
    print(f"\ncaptured {len(captured)} tiles. Now run:  python tools\\probes\\treasure_addr.py solve")

elif cmd == "solve":
    # Read every findflag result, keep the clean bit-0x80 sets, cluster by address family
    # (the 3 buffers are megabytes apart), and fit addr = base + xstride*x + ypitch*y per buffer.
    tiles = []
    for f in sorted(RESULTS.glob("flag_*.json")):
        d = json.loads(f.read_text())
        clean = sorted(a for a, o, n in d["hits"] if (o & MARK_BIT) == 0 and n == (o | MARK_BIT))
        if clean:
            tiles.append((d["x"], d["y"], clean))
    allc = sorted(a for _, _, cs in tiles for a in cs)
    clusters, lo, prev = [], (allc[0] if allc else 0), (allc[0] if allc else 0)
    for a in allc[1:]:
        if a - prev > 0x40000:
            clusters.append((lo, prev))
            lo = a
        prev = a
    if allc:
        clusters.append((lo, prev))
    print(f"{len(tiles)} tile(s), {len(clusters)} buffer cluster(s):")
    for clo, chi in clusters:
        pts = []
        for x, y, cs in tiles:
            inc = [a for a in cs if clo <= a <= chi]
            if inc:
                pts.append((x, y, min(inc)))
        if len(pts) < 2:
            continue
        print(f"\nbuffer {clo:#x}..{chi:#x}  ({len(pts)} point(s)):")
        for x, y, a in pts:
            print(f"    ({x},{y}) -> {a:#x}")
        if len(pts) >= 3:
            r = fit_layout(pts)
            if r:
                xs, yp, base, res = r
                print(f"    FIT  addr = {int(base):#x} + ({xs:+g})*x + ({yp:+g})*y   (max residual {res:g})")
                if res == 0:
                    print(f"    -> x-stride={int(xs)} (={int(xs):#x}), row-pitch={int(yp)} (={int(yp):#x})")
        else:
            (x0, y0, a0), (x1, y1, a1) = pts
            print(f"    delta ({x1 - x0:+d}x, {y1 - y0:+d}y) -> {a1 - a0:+#x}  (need a 3rd tile to fit)")

else:
    print(__doc__)
