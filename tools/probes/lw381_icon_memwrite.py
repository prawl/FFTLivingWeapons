"""LW-381 premise proof: overwrite a menu icon's RESIDENT copy in the game's heap.

THIS PROBE WRITES TO THE RUNNING GAME. Its read-only sibling is lw381_icon_memscan.py; that
one can never modify anything, this one deliberately can. Every write is recorded to an undo
file first and `--restore` puts them all back.

WHY
---
An icon that re-tiers mid-session gets the new art into the deployed .tex AND into
<game>/data/enhanced/modded.pac, both byte-verified, and the drawn icon does not change until
a restart. The scan probe found why the file route cannot help: the engine keeps DECODED
copies of each icon in ordinary RW heap memory (MEM_PRIVATE), verbatim from the file's byte 82
onward (an 82 byte .tex header the engine strips), several copies per surface, and the menu
draws from those. Patching the file changes nothing those copies read.

So the question this probe answers, and it is the load bearing premise of any fix:

    if we overwrite a resident copy in place, does the drawn icon change?

Answer yes and the fix is a guarded sweep after each re-tier. Answer no and the copies we can
see are not the ones being drawn, and the whole memory route is dead for the cost of one probe.

SAFETY
------
  * Same length only. The payload written is exactly the bytes the file holds from offset 82
    to its end, over the same span. Nothing grows, nothing moves.
  * COVERAGE GATE. A candidate is only written when the resident run matches the file for at
    least MIN_COVERAGE of its length. This exists because the scan found a 245 byte
    coincidental run inside unrelated memory (2 percent of a 12384 byte icon); writing there
    would have scribbled on something else entirely. Never lower this to "get more hits".
  * Undo first. Original bytes are read back and written to the undo file BEFORE any write.
  * Cosmetic only. Icon art is not game state: nothing here touches a save, a tally, or a
    struct the engine computes from.

USAGE (game running, the inventory list on screen)
--------------------------------------------------
    python tools/probes/lw381_icon_memwrite.py 4 --tier 3     # make icon 4 wear tier 3 art
    python tools/probes/lw381_icon_memwrite.py --restore      # put every byte back
    python tools/probes/lw381_icon_memwrite.py 4 --dry-run    # show what it WOULD write

Pick an id whose weapon has no kills, so the glow you see cannot be the real one.
"""
import ctypes
import json
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
from lw381_icon_memscan import (  # noqa: E402
    CHUNK, GLOW_DIR, TYPE_NAME, PROT_NAME, find_pid, k32, measure_run, needles_for,
    read, read_span, region_of, regions,
)

UNDO = Path(__file__).resolve().parent / "lw381_icon_memwrite_undo.json"
MIN_COVERAGE = 0.95   # see SAFETY; the observed real copies run 99.3 to 99.8 percent
HEADER = 82           # bytes of .tex header the engine strips before keeping the payload


def scan(h, pins):
    """{label: [(addr, type)]} for every needle, one sequential pass over committed memory."""
    hits = {label: [] for label, _, _, _ in pins}
    maxlen = max(len(n) for _, n, _, _ in pins)
    for base, size, mtype in regions(h):
        carry, carry_addr, off = b"", base, 0
        while off < size:
            n = min(CHUNK, size - off)
            buf = read(h, base + off, n)
            if buf is None:
                break
            hay, hay_addr = carry + buf, carry_addr
            for label, needle, _, _ in pins:
                i = hay.find(needle)
                while i >= 0:
                    hits[label].append((hay_addr + i, mtype))
                    i = hay.find(needle, i + 1)
            keep = min(len(hay), maxlen - 1)
            carry = hay[len(hay) - keep:] if keep else b""
            carry_addr = hay_addr + len(hay) - keep
            off += n
    return hits


def restore(h):
    if not UNDO.exists():
        print("no undo file; nothing to restore.")
        return 0
    rec = json.loads(UNDO.read_text())
    n = 0
    for w in rec["writes"]:
        data = bytes.fromhex(w["original"])
        wrote = ctypes.c_size_t(0)
        ok = k32.WriteProcessMemory(h, ctypes.c_void_p(int(w["addr"], 16)), data, len(data),
                                    ctypes.byref(wrote))
        state = "restored" if ok and wrote.value == len(data) else "FAILED"
        print(f"  {state} {len(data)} bytes @ {w['addr']} ({w['label']})")
        n += 1 if ok else 0
    if n == len(rec["writes"]):
        UNDO.unlink()
        print(f"all {n} write(s) restored; undo file removed.")
    else:
        print(f"{n} of {len(rec['writes'])} restored; undo file KEPT for a retry.")
    return n


def main():
    # Parse positionally, consuming the value after --tier: a naive
    # "everything not starting with --" also swallows that value and silently plans writes for
    # an icon id the caller never named (observed: `4 --tier 3` planned icon 3 as well).
    argv, ids, tier, dry, do_restore = sys.argv[1:], [], "3", False, False
    i = 0
    while i < len(argv):
        a = argv[i]
        if a == "--tier":
            tier = argv[i + 1]
            i += 2
            continue
        if a == "--dry-run":
            dry = True
        elif a == "--restore":
            do_restore = True
        elif a.startswith("--"):
            print(f"unknown option {a}")
            sys.exit(2)
        else:
            ids.append(int(a, 0))
        i += 1

    pid = find_pid()
    if pid is None:
        print("fft_enhanced.exe is not running.")
        sys.exit(1)
    access = 0x0410 if dry else 0x0438   # +VM_WRITE +VM_OPERATION when we mean to write
    h = k32.OpenProcess(access, False, pid)
    if not h:
        print(f"could not open pid {pid} (error {ctypes.get_last_error()}).")
        sys.exit(1)
    print(f"game pid {pid}")

    if do_restore:
        restore(h)
        k32.CloseHandle(h)
        return

    if not ids:
        print(__doc__)
        sys.exit(2)

    pins = needles_for(set(ids))
    if not pins:
        print("no usable needles for those ids (is this a living weapon with glow bakes?).")
        sys.exit(1)

    # Only the plain/base form matters: that is what a no-kills weapon has resident.
    pins = [p for p in pins if p[0].endswith("base ")]
    print(f"scanning for {len(pins)} resident base copy needle(s) ...")
    hits = scan(h, pins)

    plan = []
    for label, needle, s, blob in pins:
        surface = label.split()[2]
        variant = None
        for e in json.loads((GLOW_DIR / "manifest.json").read_text())["icons"]:
            if e["id"] in ids and e["surface"] == surface:
                variant = (GLOW_DIR / e["variants"][tier]).read_bytes()
        if variant is None or len(variant) != len(blob):
            print(f"  {label}: no tier {tier} bake of matching length, skipped")
            continue
        for addr, mtype in hits[label]:
            lo, hi = measure_run(h, addr, s, blob)
            cov = (hi - lo) / len(blob)
            file0 = addr - s
            mark = "WRITE" if cov >= MIN_COVERAGE else "skip "
            reg = region_of(h, addr)
            prot = PROT_NAME.get(reg[3] & 0xFF, hex(reg[3])) if reg else "?"
            print(f"  {mark} {label} @0x{addr:X} coverage {cov*100:5.1f}% "
                  f"{TYPE_NAME.get(mtype, hex(mtype))} prot={prot}")
            if cov >= MIN_COVERAGE:
                plan.append((label, file0 + HEADER, variant[HEADER:], len(blob)))

    if not plan:
        print("\nnothing met the coverage gate; no writes attempted.")
        k32.CloseHandle(h)
        return
    if dry:
        print(f"\ndry run: would write {len(plan)} copy(ies), "
              f"{sum(len(p[2]) for p in plan)} bytes total. Nothing was written.")
        k32.CloseHandle(h)
        return

    writes = []
    for label, addr, payload, _ in plan:
        original = read_span(h, addr, len(payload))
        if len(original) != len(payload):
            print(f"  refused {label} @0x{addr:X}: could not read back {len(payload)} bytes first")
            continue
        writes.append({"label": label, "addr": hex(addr), "original": original.hex()})
    UNDO.write_text(json.dumps({"pid": pid, "tier": tier, "writes": writes}, indent=1))
    print(f"\nundo written first: {UNDO} ({len(writes)} entr(ies))")

    done = 0
    for label, addr, payload, _ in plan:
        if not any(w["addr"] == hex(addr) for w in writes):
            continue
        wrote = ctypes.c_size_t(0)
        ok = k32.WriteProcessMemory(h, ctypes.c_void_p(addr), payload, len(payload),
                                    ctypes.byref(wrote))
        print(f"  {'wrote' if ok else 'FAILED'} {wrote.value} bytes @0x{addr:X} ({label})")
        done += 1 if ok else 0

    print(f"\n{done} copy(ies) now carry tier {tier} art.")
    print("Re-enter the inventory list (leave it and come back) and look at the row.")
    print(f"Put it back with:  python {Path(__file__).name} --restore")
    k32.CloseHandle(h)


if __name__ == "__main__":
    main()
