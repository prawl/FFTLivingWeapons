"""LW-381 icon memory scan: is a menu icon's texture resident in the game's own RAM?

READ ONLY. This probe never writes to the process, never writes to modded.pac, and never
touches a file in the mod tree. It opens the game with PROCESS_QUERY_INFORMATION |
PROCESS_VM_READ (0x0410) and nothing else, so it cannot modify anything even by accident.

WHY THIS EXISTS
---------------
A weapon that crosses a growth tier mid-session gets its new glow written to the deployed
loose .tex AND mirrored into <game>/data/enhanced/modded.pac, both verified byte-correct on
disk, and the game still draws the old icon until it is restarted. Every recorded outcome
splits the same way (docs/LIVE_LEDGER.md, [live-icon-repaint] PROVEN and the RETRACTED
[icon-refresh-unreliable] row):

    refreshed : 2026-08-16 (minutes after boot), 2026-09-01 (fresh deploy, 3x in one process)
    refused   : 2026-08-25, 2026-08-26 (long sessions), 2026-09-01 07:34 (8 min in, but
                with a full 24 turn battle behind it)

So the file is right and the engine is not re-reading it. The next question is not "what
evicts the cache" but "where is the copy the engine actually draws from". If that copy lives
in ordinary process memory we can write it directly and skip the whole eviction problem.

WHAT IT ANSWERS
---------------
For each icon asked about, it searches every committed readable region for the icon's bytes
in each form the bake can produce (plain base, and tier 1/2/3), and reports every hit with
the region's TYPE, which is the actual diagnostic:

    MEM_MAPPED  a file mapping. Almost certainly modded.pac itself mapped into the process,
                i.e. the same bytes we already patch on disk, reached a different way.
    MEM_PRIVATE a heap/allocated copy. THIS is the interesting one: a resident decode or a
                resource-manager buffer, and the thing worth overwriting.
    MEM_IMAGE   part of a loaded module. Not expected; would mean the art is baked into an
                executable image.

A hit carrying the OLD form (the tier the icon no longer is) is the jackpot: that is the
stale copy the engine is drawing.

Finding nothing is also a real answer. It means the bytes are not held verbatim in RAM
(decoded and uploaded to the GPU, or stored swizzled/recompressed), and the in memory route
is closed without our having written a thing.

USAGE (game must be running)
----------------------------
    python tools/probes/lw381_icon_memscan.py 19            # Vagabond, all surfaces
    python tools/probes/lw381_icon_memscan.py 19 77 78      # several ids
    python tools/probes/lw381_icon_memscan.py 19 --full     # verify each hit's whole blob

Needles are cut from bytes that DIFFER between the base bake and the tier bake, so a hit
identifies WHICH version that copy holds rather than just "the icon is somewhere".
"""
import ctypes
import ctypes.wintypes as wt
import json
import os
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from lib.paths import STEAM_FFT  # noqa: E402

MOD_ID = "prawl.fft.livingweapons"
GLOW_DIR = Path(os.environ.get("RELOADEDIIMODS", "")) / MOD_ID / "glow_icons"
NEEDLE = 64          # bytes per needle; long enough to be unique, short enough to be cheap
CHUNK = 4 << 20      # 4 MB reads

MEM_COMMIT = 0x1000
PAGE_GUARD = 0x100
PAGE_NOACCESS = 0x01
TYPE_NAME = {0x1000000: "MEM_IMAGE", 0x40000: "MEM_MAPPED", 0x20000: "MEM_PRIVATE"}

k32 = ctypes.WinDLL("kernel32", use_last_error=True)


class MBI(ctypes.Structure):
    _fields_ = [("BaseAddress", ctypes.c_void_p), ("AllocationBase", ctypes.c_void_p),
                ("AllocationProtect", wt.DWORD), ("__align", wt.DWORD),
                ("RegionSize", ctypes.c_size_t), ("State", wt.DWORD),
                ("Protect", wt.DWORD), ("Type", wt.DWORD), ("__align2", wt.DWORD)]


def find_pid(name="fft_enhanced.exe"):
    """PID of the running game, or None. Uses the toolhelp snapshot, no third-party deps."""
    class ENTRY(ctypes.Structure):
        _fields_ = [("dwSize", wt.DWORD), ("cntUsage", wt.DWORD), ("th32ProcessID", wt.DWORD),
                    ("th32DefaultHeapID", ctypes.POINTER(ctypes.c_ulong)),
                    ("th32ModuleID", wt.DWORD), ("cntThreads", wt.DWORD),
                    ("th32ParentProcessID", wt.DWORD), ("pcPriClassBase", ctypes.c_long),
                    ("dwFlags", wt.DWORD), ("szExeFile", ctypes.c_char * 260)]
    snap = k32.CreateToolhelp32Snapshot(0x2, 0)
    e = ENTRY(); e.dwSize = ctypes.sizeof(ENTRY)
    ok = k32.Process32First(snap, ctypes.byref(e))
    while ok:
        if e.szExeFile.decode(errors="replace").lower() == name.lower():
            k32.CloseHandle(snap)
            return e.th32ProcessID
        ok = k32.Process32Next(snap, ctypes.byref(e))
    k32.CloseHandle(snap)
    return None


def regions(h):
    """Every committed, readable, non-guard region, as (base, size, type)."""
    addr, out = 0, []
    mbi = MBI()
    while addr < (1 << 47):
        if not k32.VirtualQueryEx(h, ctypes.c_void_p(addr), ctypes.byref(mbi), ctypes.sizeof(mbi)):
            break
        base = mbi.BaseAddress or 0
        size = mbi.RegionSize
        if size == 0:
            break
        if (mbi.State == MEM_COMMIT and not (mbi.Protect & PAGE_GUARD)
                and not (mbi.Protect & PAGE_NOACCESS)):
            out.append((base, size, mbi.Type))
        addr = base + size
    return out


def read(h, addr, n):
    buf = ctypes.create_string_buffer(n)
    got = ctypes.c_size_t(0)
    if not k32.ReadProcessMemory(h, ctypes.c_void_p(addr), buf, n, ctypes.byref(got)):
        return None
    return buf.raw[:got.value]


def needles_for(ids):
    """(label, needle_bytes, offset_in_file, full_path) per icon surface and per bake form.

    The window is cut around a byte that actually DIFFERS between base and the tier, so a hit
    tells us which bake that copy holds. An icon whose tier bake is byte-identical to base
    carries no information and is skipped out loud.
    """
    man = json.loads((GLOW_DIR / "manifest.json").read_text())
    out = []
    for e in man["icons"]:
        if e["id"] not in ids:
            continue
        base_p = GLOW_DIR / "base_backup" / Path(e["baseRel"]).name
        if not base_p.exists():
            print(f"  (id {e['id']} {e['surface']}: no base snapshot, skipped)")
            continue
        base = base_p.read_bytes()
        forms = {"base": base}
        for tier, fname in e["variants"].items():
            p = GLOW_DIR / fname
            if p.exists():
                forms["tier" + tier] = p.read_bytes()
        ref = forms.get("tier1", base)
        diffs = [i for i in range(min(len(base), len(ref))) if base[i] != ref[i]]
        if not diffs:
            print(f"  (id {e['id']} {e['surface']}: base and tier1 bakes are identical, skipped)")
            continue
        c = diffs[len(diffs) // 2]
        s = max(0, c - NEEDLE // 2)
        for form, blob in forms.items():
            out.append((f"id {e['id']:>3} {e['surface']:<6} {form:<5}", blob[s:s + NEEDLE], s, blob))
    return out


def region_of(h, addr):
    """(base, size, type, protect) for the one region containing addr, or None."""
    mbi = MBI()
    if not k32.VirtualQueryEx(h, ctypes.c_void_p(addr), ctypes.byref(mbi), ctypes.sizeof(mbi)):
        return None
    return (mbi.BaseAddress or 0, mbi.RegionSize, mbi.Type, mbi.Protect)


PROT_NAME = {0x02: "R", 0x04: "RW", 0x08: "WC", 0x20: "RX", 0x40: "RWX", 0x80: "RWXC"}


def read_span(h, addr, n, page=0x1000):
    """Read n bytes page by page; stops at the first unreadable page. Returns what it got."""
    out = bytearray()
    got = 0
    while got < n:
        step = min(page, n - got)
        b = read(h, addr + got, step)
        if b is None or len(b) != step:
            break
        out += b
        got += step
    return bytes(out)


def measure_run(h, hit_addr, file_off, blob):
    """How far the resident copy agrees with the file, walking out from the needle hit.

    Returns (lo, hi) as FILE offsets of the matching run, so hi - lo is how much of this
    icon is actually present at this address and where it starts in the file.
    """
    start_addr = hit_addr - file_off          # where file offset 0 would land
    lo = hi = file_off
    back = read_span(h, max(start_addr, hit_addr - file_off), file_off) if file_off else b""
    if back:
        base = len(back) - file_off if len(back) >= file_off else 0
        i = file_off - 1
        j = len(back) - 1
        while i >= 0 and j >= 0 and back[j] == blob[i]:
            i -= 1
            j -= 1
        lo = i + 1
    fwd = read_span(h, hit_addr, len(blob) - file_off)
    k = 0
    while k < len(fwd) and file_off + k < len(blob) and fwd[k] == blob[file_off + k]:
        k += 1
    hi = file_off + k
    return lo, hi


def align_report(h, pins, hits):
    print("\nALIGNMENT PASS (read only): how much of each icon is really resident, and where")
    print("-" * 78)
    for label, needle, s, blob in pins:
        for addr, mtype in hits[label][:4]:
            reg = region_of(h, addr)
            prot = PROT_NAME.get(reg[3] & 0xFF, hex(reg[3])) if reg else "?"
            lo, hi = measure_run(h, addr, s, blob)
            span = hi - lo
            pct = 100.0 * span / len(blob)
            print(f"{label}  @0x{addr:X}  {TYPE_NAME.get(mtype, hex(mtype))} prot={prot}")
            print(f"    resident run: file bytes [{lo}..{hi}) = {span} of {len(blob)} ({pct:.1f}%)"
                  f"; file offset 0 would sit at 0x{addr - s:X}")
            if reg:
                print(f"    region: base 0x{reg[0]:X} size 0x{reg[1]:X}")


def main():
    args = [a for a in sys.argv[1:] if not a.startswith("--")]
    full = "--full" in sys.argv
    align = "--align" in sys.argv
    if not args:
        print(__doc__)
        sys.exit(2)
    ids = {int(a, 0) for a in args}

    pid = find_pid()
    if pid is None:
        print("fft_enhanced.exe is not running; start the game and try again.")
        sys.exit(1)
    h = k32.OpenProcess(0x0410, False, pid)   # QUERY_INFORMATION | VM_READ, read only
    if not h:
        print(f"could not open pid {pid} for reading (error {ctypes.get_last_error()}); "
              "try running this from an elevated shell.")
        sys.exit(1)

    print(f"game pid {pid}; glow dir {GLOW_DIR}")
    pins = needles_for(ids)
    if not pins:
        print("no usable needles for those ids.")
        sys.exit(1)
    print(f"{len(pins)} needle(s) of {NEEDLE} bytes\n")

    regs = regions(h)
    total = sum(r[1] for r in regs)
    print(f"scanning {len(regs)} committed region(s), {total / (1 << 30):.2f} GB ...\n")

    hits = {label: [] for label, _, _, _ in pins}
    maxlen = max(len(n) for _, n, _, _ in pins)
    for base, size, mtype in regs:
        carry = b""
        carry_addr = base
        off = 0
        while off < size:
            n = min(CHUNK, size - off)
            buf = read(h, base + off, n)
            if buf is None:
                break
            hay = carry + buf
            hay_addr = carry_addr
            for label, needle, _, _ in pins:
                i = hay.find(needle)
                while i >= 0:
                    hits[label].append((hay_addr + i, mtype))
                    i = hay.find(needle, i + 1)
            keep = min(len(hay), maxlen - 1)
            carry = hay[len(hay) - keep:] if keep else b""
            carry_addr = hay_addr + len(hay) - keep
            off += n

    print(f"{'needle':<26} {'hits':>4}  where")
    print("-" * 78)
    any_private = False
    for label, needle, s, blob in pins:
        hs = hits[label]
        if not hs:
            print(f"{label:<26} {0:>4}  (not resident in this form)")
            continue
        shown = []
        for addr, mtype in hs[:6]:
            tn = TYPE_NAME.get(mtype, hex(mtype))
            if tn == "MEM_PRIVATE":
                any_private = True
            verdict = ""
            if full:
                whole = read(h, addr - s, len(blob))
                verdict = " FULL-MATCH" if whole == blob else " partial"
            shown.append(f"0x{addr:X} {tn}{verdict}")
        more = f" (+{len(hs) - 6} more)" if len(hs) > 6 else ""
        print(f"{label:<26} {len(hs):>4}  " + "; ".join(shown) + more)

    if align:
        align_report(h, pins, hits)

    print()
    if any_private:
        print("MEM_PRIVATE hit: a copy of this icon lives in ordinary process memory. That is the")
        print("copy worth overwriting; a same-length write there needs no cache eviction at all.")
    elif any(hits[l] for l in hits):
        print("Hits are all mapped/image memory, i.e. the pac reached another way, not a decode.")
        print("Overwriting there is the same act as patching the file, which we know does not")
        print("refresh a drawn icon. The in-memory route is closed unless a private copy appears")
        print("at a different moment (try again with a menu open).")
    else:
        print("No form found anywhere. The bytes are not held verbatim in RAM (decoded and")
        print("uploaded, or stored swizzled/recompressed), so a byte overwrite cannot reach them.")
        print("Route closed; the answer lies in the resource manager, not in a memory write.")

    k32.CloseHandle(h)


if __name__ == "__main__":
    main()
