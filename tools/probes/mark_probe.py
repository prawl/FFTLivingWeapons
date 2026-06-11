"""Treasure Master hunt: find where the in-battle tile MARKS live (hover + press 2).

Snapshot/diff instrument over the module's writable data (the statics family that
already carries the battle structs). The watchspan discipline: build a CHURN MASK of
addresses that change on their own, then diff across exactly one player action (mark a
tile), churn subtracted, and intersect across trials until the candidates are few.

Session data lives in %TEMP%\\fft_mark_probe\\ (runtime artifacts, not repo files).

  python tools\\probes\\mark_probe.py regions             # sanity: what we snapshot
  python tools\\probes\\mark_probe.py churn [secs]        # build the churn mask (default 6s)
  python tools\\probes\\mark_probe.py snap <name>         # full snapshot -> <name>.bin
  python tools\\probes\\mark_probe.py diff <a> <b>        # changed runs (churn-masked) + <a>_<b>.diff.json
  python tools\\probes\\mark_probe.py intersect <d1> <d2> [...]   # addrs present in every diff json
  python tools\\probes\\mark_probe.py read <addr> <n>     # hex dump n bytes
  python tools\\probes\\mark_probe.py poke <addr> <hexbytes>     # WPM write (the verification poke)
"""
import ctypes, ctypes.wintypes as w, json, os, pathlib, struct, sys, time

PROCESS_VM_READ = 0x0010
PROCESS_VM_WRITE = 0x0020
PROCESS_VM_OPERATION = 0x0008
PROCESS_QUERY_INFORMATION = 0x0400
MEM_COMMIT, MEM_IMAGE = 0x1000, 0x1000000
PAGE_GUARD, PAGE_NOACCESS = 0x100, 0x01
WRITABLE = 0x04 | 0x08 | 0x40 | 0x80

IMAGE_BASE = 0x140000000
IMAGE_END = 0x143000000          # generous; the walk stops at the last image region anyway
SESSION = pathlib.Path(os.environ.get("TEMP", ".")) / "fft_mark_probe"
SESSION.mkdir(exist_ok=True)

k32 = ctypes.windll.kernel32
psapi = ctypes.windll.psapi


class MBI(ctypes.Structure):
    _fields_ = [("BaseAddress", ctypes.c_void_p), ("AllocationBase", ctypes.c_void_p),
                ("AllocationProtect", w.DWORD), ("PartitionId", w.WORD),
                ("RegionSize", ctypes.c_size_t), ("State", w.DWORD),
                ("Protect", w.DWORD), ("Type", w.DWORD)]


def find_pid(name):
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
            return arr[i], h
        k32.CloseHandle(h)
    return None, None


pid, h = find_pid("fft_enhanced.exe")
if not h:
    print("game not running")
    sys.exit(1)


def rpm(addr, n):
    buf = ctypes.create_string_buffer(n)
    got = ctypes.c_size_t()
    if not k32.ReadProcessMemory(h, ctypes.c_void_p(addr), buf, n, ctypes.byref(got)) or got.value != n:
        return None
    return buf.raw


def wpm(addr, data):
    n = ctypes.c_size_t()
    ok = k32.WriteProcessMemory(h, ctypes.c_void_p(addr), data, len(data), ctypes.byref(n))
    return bool(ok) and n.value == len(data)


def writable_image_regions():
    """(base, size) for every committed, writable, non-guard IMAGE region in the module."""
    out, addr, mbi = [], IMAGE_BASE, MBI()
    while addr < IMAGE_END:
        if not k32.VirtualQueryEx(h, ctypes.c_void_p(addr), ctypes.byref(mbi), ctypes.sizeof(mbi)):
            break
        base, size = mbi.BaseAddress or 0, mbi.RegionSize
        if (mbi.State == MEM_COMMIT and mbi.Type == MEM_IMAGE
                and (mbi.Protect & WRITABLE) and not (mbi.Protect & (PAGE_GUARD | PAGE_NOACCESS))):
            out.append((base, size))
        addr = base + size if base + size > addr else addr + 0x1000
    return out


def snapshot():
    return {base: rpm(base, size) for base, size in writable_image_regions()}


def save_snap(name):
    snap = snapshot()
    with (SESSION / f"{name}.bin").open("wb") as f:
        for base, data in snap.items():
            if data is None:
                continue
            f.write(struct.pack("<QQ", base, len(data)))
            f.write(data)
    total = sum(len(d) for d in snap.values() if d)
    print(f"snap {name}: {len(snap)} regions, {total/1e6:.1f} MB -> {SESSION / (name + '.bin')}")


def load_snap(name):
    snap, raw = {}, (SESSION / f"{name}.bin").read_bytes()
    off = 0
    while off < len(raw):
        base, size = struct.unpack_from("<QQ", raw, off)
        off += 16
        snap[base] = raw[off:off + size]
        off += size
    return snap


def diff_snaps(a, b):
    """Changed byte runs between two snapshots: [(addr, before_hex, after_hex)]."""
    runs = []
    for base in sorted(set(a) & set(b)):
        da, db = a[base], b[base]
        n, i = min(len(da), len(db)), 0
        while i < n:
            if da[i] != db[i]:
                j = i
                while j < n and da[j] != db[j] and j - i < 64:
                    j += 1
                runs.append((base + i, da[i:j].hex(), db[i:j].hex()))
                i = j
            else:
                i += 1
    return runs


def load_mask():
    p = SESSION / "churn.json"
    return set(json.loads(p.read_text())) if p.exists() else set()


def masked(runs, mask):
    return [r for r in runs if r[0] not in mask]


cmd = sys.argv[1] if len(sys.argv) > 1 else "regions"

if cmd == "regions":
    regs = writable_image_regions()
    for base, size in regs:
        print(f"  {base:#x}  {size/1024:.0f} KB")
    print(f"{len(regs)} regions, {sum(s for _, s in regs)/1e6:.1f} MB total")

elif cmd == "churn":
    secs = float(sys.argv[2]) if len(sys.argv) > 2 else 6.0
    print(f"sampling churn for {secs}s -- HANDS OFF the controls...")
    base_snap, mask, t0 = snapshot(), set(), time.time()
    while time.time() - t0 < secs:
        time.sleep(0.25)
        for addr, before, after in diff_snaps(base_snap, snapshot()):
            mask.update(range(addr, addr + len(before) // 2))
    (SESSION / "churn.json").write_text(json.dumps(sorted(mask)))
    print(f"churn mask: {len(mask)} self-changing bytes -> churn.json")

elif cmd == "snap":
    save_snap(sys.argv[2])

elif cmd == "diff":
    a, b = sys.argv[2], sys.argv[3]
    runs = masked(diff_snaps(load_snap(a), load_snap(b)), load_mask())
    out = SESSION / f"{a}_{b}.diff.json"
    out.write_text(json.dumps([(addr, bh, ah) for addr, bh, ah in runs]))
    print(f"{len(runs)} changed runs after churn mask -> {out}")
    for addr, bh, ah in runs[:120]:
        print(f"  {addr:#x}  {bh} -> {ah}")
    if len(runs) > 120:
        print(f"  ... {len(runs) - 120} more in the json")

elif cmd == "intersect":
    sets = []
    for name in sys.argv[2:]:
        data = json.loads((SESSION / f"{name}.diff.json").read_text())
        sets.append({addr for addr, _, _ in data})
    common = set.intersection(*sets)
    print(f"{len(common)} addresses changed in ALL {len(sets)} diffs:")
    for addr in sorted(common):
        print(f"  {addr:#x}")

elif cmd == "read":
    addr, n = int(sys.argv[2], 0), int(sys.argv[3], 0)
    data = rpm(addr, n)
    print(data.hex(" ") if data else "unreadable")

elif cmd == "find":
    # Scan ALL committed writable memory (heap included) for a byte pattern via the
    # C-speed bytes.find(). No wildcards (the coordinate-list hunt doesn't need them);
    # for hunting a mark list this is exact-match only.
    pat = sys.argv[2]
    needle = bytes.fromhex(pat)
    addr, mbi, hits, scanned = 0, MBI(), [], 0
    LIMIT = 0x7FFF_FFFF_0000
    while addr < LIMIT and len(hits) < 400:
        if not k32.VirtualQueryEx(h, ctypes.c_void_p(addr), ctypes.byref(mbi), ctypes.sizeof(mbi)):
            break
        base, size = mbi.BaseAddress or 0, mbi.RegionSize
        nxt = base + size if base + size > addr else addr + 0x1000
        if (mbi.State == MEM_COMMIT and (mbi.Protect & WRITABLE)
                and not (mbi.Protect & (PAGE_GUARD | PAGE_NOACCESS))):
            buf = rpm(base, size)
            if buf:
                scanned += size
                i = buf.find(needle)
                while i != -1 and len(hits) < 400:
                    hits.append(base + i)
                    i = buf.find(needle, i + 1)
        addr = nxt
    print(f"scanned {scanned/1e6:.0f} MB, {len(hits)} hits for {pat}:")
    for a in hits:
        print(f"  {a:#x}")

elif cmd == "poke":
    addr, data = int(sys.argv[2], 0), bytes.fromhex(sys.argv[3])
    print("OK" if wpm(addr, data) else "WRITE FAILED")

else:
    print(__doc__)
