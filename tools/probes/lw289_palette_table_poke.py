#!/usr/bin/env python
"""LW-289 round 6: WRITE the in-memory weapon palette table and see if the colour follows.

WHERE THIS CAME FROM. Round 5 (lw289_palette_table_scan.py) scanned the running game for the
254-byte PSX item-graphics block and found FOUR full-length copies:

    0x00015C241F4C  RW   modded    our shipped battle_bin.bin, loaded into a buffer
    0x00015D74A09C  RW   modded    the same, a second buffer
    0x000164B5E344  RW   modded    the same, a third buffer
    0x00416DCA3CA4  RW   VANILLA   a separate copy our file override never reached

That last one explains round 4 exactly. The game DOES load our battle_bin (three copies of our
bytes prove it) and it DOES NOT colour weapons from it. Some other copy of the same table carries
the vanilla values, and that copy is the only remaining candidate for what the renderer reads.

It sits in heap, not in the exe image (base 0x140000000), so the address is rebuilt every launch
and must never be hardcoded. This probe rescans every run and writes whatever it finds.

THE EXPERIMENT. Repoint ONE weapon in the vanilla copy and leave a weapon that currently shares
its palette untouched.

    Claymore   item 22, X 15 -> 8    should turn CYAN
    Flamberge  item 26, X 15         UNTOUCHED CONTROL, must stay ROSE

Both render palette 15 in game today, confirmed by measurement in rounds 1, 2, 3 and 4. Splitting
them is the whole question: per-item assignment is what the feature needs, and a palette-wide
change would look identical on a single weapon.

  Claymore CYAN, Flamberge ROSE   -> per-item palette assignment WORKS via a runtime write.
                                     LW-289 is unblocked, and a runtime lever is better than a
                                     file override because it is live tunable.
  BOTH turn cyan                  -> we moved something palette-wide, not per item.
  NEITHER moves                   -> this copy is not the source either. Report the address and
                                     hunt the next candidate; do not conclude the table is
                                     unreachable from one negative.
  the write is refused or reverts -> report it; the memory may be re-populated per battle load
                                     from whatever the real source is, which would itself be a
                                     useful finding (write later, or write repeatedly).

TIMING MATTERS. The game requests the weapon files once per BATTLE LOAD. Poke from the world map
or the formation screen, THEN enter a battle. If the value is consumed at battle load, poking
mid-battle is too late. If the poke reverts by itself, that tells us the table is refreshed per
load and the write has to be repeated or moved earlier.

SAFETY. Reads and writes go through ReadProcessMemory / WriteProcessMemory on a normal process
handle, which fail with an error code rather than crashing the game the way a raw dereference in
an in-process module would. Every write is verified by reading back, every original byte is saved
to an undo file, and the probe refuses to write unless the byte it is about to change still holds
the value it expects.

USAGE:
  python lw289_palette_table_poke.py --selftest    # pure checks, no game
  python lw289_palette_table_poke.py --scan        # locate the copies, write nothing
  python lw289_palette_table_poke.py --poke        # apply the experiment above
  python lw289_palette_table_poke.py --undo        # restore every byte from the undo file
"""
import ctypes as C
import json
import os
import sys
from ctypes import wintypes as W

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from lw289_battle_bin_palette_map import (  # noqa: E402
    FIRST_ITEM, LAST_ITEM, extract, gate, record_offset,
)
from lw289_palette_table_scan import (  # noqa: E402
    MBI, MEM_COMMIT, PAGE_GUARD, PAGE_NOACCESS, PROT, find_pid, k32, records, rpm,
)

HERE = os.path.dirname(os.path.abspath(__file__))
UNDO = os.path.join(HERE, "lw289_palette_poke_undo.json")
PROCESS_RW = 0x0400 | 0x0010 | 0x0020        # QUERY_INFORMATION | VM_READ | VM_WRITE
PROCESS_QUERY_VM = 0x0400 | 0x0010

# item id -> new weapon palette X. The control is any item sharing the old palette that is NOT here.
TREATMENT = {22: 8}
CONTROL = 26
NAMES = {22: "Claymore", 26: "Flamberge"}


def wpm(h, addr, data):
    written = C.c_size_t()
    ok = k32.WriteProcessMemory(h, C.c_void_p(addr), data, len(data), C.byref(written))
    return bool(ok) and written.value == len(data)


def psx_block(raw, modded):
    rec = records(raw, modded)
    ids = list(range(FIRST_ITEM, LAST_ITEM + 1))
    return bytes(b for i in ids for b in ((rec[i][0] << 4) | rec[i][1], rec[i][2]))


def find_copies(h, needle):
    """Every committed readable region, scanned for one needle. Returns [(addr, prot)]."""
    hits, addr = [], 0
    while addr < 0x7FFFFFFFFFFF:
        mbi = MBI()
        if not k32.VirtualQueryEx(h, C.c_void_p(addr), C.byref(mbi), C.sizeof(mbi)):
            break
        base = mbi.BaseAddress or 0
        nxt = base + mbi.RegionSize
        if (mbi.State == MEM_COMMIT and mbi.Protect
                and not (mbi.Protect & (PAGE_NOACCESS | PAGE_GUARD))):
            pos, tail = base, b""
            while pos < nxt:
                chunk = rpm(h, pos, min(8 << 20, nxt - pos))
                if chunk is None:
                    break
                hay = tail + chunk
                o = hay.find(needle)
                while o != -1:
                    hits.append((pos - len(tail) + o, PROT.get(mbi.Protect, hex(mbi.Protect))))
                    o = hay.find(needle, o + 1)
                tail = hay[-(len(needle) - 1):]
                pos += len(chunk)
        addr = nxt if nxt > addr else addr + 0x1000
    return hits


def locate(h):
    """Find the vanilla copies and the copies of our own shipped file, and tell them apart."""
    raw = extract(os.path.join(os.environ.get("TEMP", "."), "lw289"))
    gate(raw)
    van = find_copies(h, psx_block(raw, False))
    mod = find_copies(h, psx_block(raw, True))
    return raw, van, mod


def report(van, mod):
    print(f"copies of the VANILLA table : {len(van)}")
    for a, p in van:
        print(f"   0x{a:012X}  {p}")
    print(f"copies of OUR SHIPPED file  : {len(mod)}")
    for a, p in mod:
        print(f"   0x{a:012X}  {p}")
    if not van:
        print("\nNo vanilla copy present. Either the deployed battle_bin.bin was reverted, or the")
        print("table moved. Rerun lw289_palette_table_scan.py before poking anything.")


def poke():
    pid = find_pid() or sys.exit("fft_enhanced.exe is not running")
    h = k32.OpenProcess(PROCESS_RW, False, pid)
    if not h:
        sys.exit(f"OpenProcess failed {C.get_last_error()}")
    raw, van, mod = locate(h)
    report(van, mod)
    if not van:
        sys.exit(1)
    if os.path.isfile(UNDO):
        sys.exit(f"{UNDO} already exists; run --undo first so two pokes never stack")
    rec = records(raw, False)
    saved = []
    print()
    for iid, newx in sorted(TREATMENT.items()):
        oldx, y, _ = rec[iid]
        want_old = (oldx << 4) | y
        want_new = (newx << 4) | y
        rel = record_offset(iid) - record_offset(FIRST_ITEM)
        for base, _ in van:
            addr = base + rel
            cur = rpm(h, addr, 1)
            if cur is None or cur[0] != want_old:
                print(f"   SKIP 0x{addr:012X}: holds {cur[0] if cur else None:#04x}, "
                      f"expected {want_old:#04x}")
                continue
            if not wpm(h, addr, bytes([want_new])):
                print(f"   FAILED 0x{addr:012X}: WriteProcessMemory error "
                      f"{C.get_last_error()}")
                continue
            back = rpm(h, addr, 1)
            ok = back is not None and back[0] == want_new
            print(f"   {'WROTE ' if ok else 'UNVERIFIED '}0x{addr:012X}  "
                  f"{NAMES.get(iid, iid)}  X {oldx} -> {newx}  "
                  f"({want_old:#04x} -> {want_new:#04x}, readback "
                  f"{back[0]:#04x} {'OK' if ok else 'MISMATCH'})")
            if ok:
                saved.append({"addr": addr, "old": want_old, "new": want_new, "item": iid})
    k32.CloseHandle(h)
    if not saved:
        sys.exit("\nnothing was written; nothing to test")
    json.dump(saved, open(UNDO, "w", encoding="utf-8"), indent=1)
    print(f"\nundo file: {UNDO}")
    print()
    print("NOW: from the world map or formation screen, enter a DAYLIGHT battle and swing")
    print(f"{NAMES[list(TREATMENT)[0]]} and {NAMES[CONTROL]}.")
    print(f"  {NAMES[list(TREATMENT)[0]]:10} should be CYAN   (palette 8)")
    print(f"  {NAMES[CONTROL]:10} must stay ROSE   (palette 15), it is the control")
    print("If the poke reverts on its own, say so: that means the table is refreshed per battle")
    print("load and the write has to happen later or repeatedly, which is itself a finding.")


def undo():
    if not os.path.isfile(UNDO):
        sys.exit(f"no {UNDO}; nothing to undo")
    saved = json.load(open(UNDO, encoding="utf-8"))
    pid = find_pid()
    if not pid:
        os.remove(UNDO)
        print("game not running; the poke was in memory only, so it is already gone. "
              "Removed the undo file.")
        return
    h = k32.OpenProcess(PROCESS_RW, False, pid)
    if not h:
        sys.exit(f"OpenProcess failed {C.get_last_error()}")
    for s in saved:
        cur = rpm(h, s["addr"], 1)
        if cur is None:
            print(f"   0x{s['addr']:012X}: unreadable now, skipped")
            continue
        if cur[0] != s["new"]:
            print(f"   0x{s['addr']:012X}: holds {cur[0]:#04x}, not our {s['new']:#04x}; "
                  f"the region was reused or refreshed. NOT restoring, that would corrupt it.")
            continue
        print(f"   restored 0x{s['addr']:012X} -> {s['old']:#04x}"
              if wpm(h, s["addr"], bytes([s["old"]]))
              else f"   FAILED to restore 0x{s['addr']:012X}")
    k32.CloseHandle(h)
    os.remove(UNDO)
    print(f"removed {UNDO}")


def selftest():
    fake = bytearray(0x40000)
    for iid in range(FIRST_ITEM, LAST_ITEM + 1):
        off = record_offset(iid)
        fake[off] = ((3 + iid % 13) << 4) | (iid % 3)
        fake[off + 1] = iid % 251
    a = psx_block(bytes(fake), False)
    b = psx_block(bytes(fake), True)
    assert len(a) == len(b) == (LAST_ITEM - FIRST_ITEM + 1) * 2, "block length wrong"
    assert a != b, "vanilla and modded blocks are identical; the scan could not tell them apart"
    # The relative offset arithmetic the poke depends on must land on the right record.
    for iid in list(TREATMENT) + [CONTROL]:
        rel = record_offset(iid) - record_offset(FIRST_ITEM)
        assert 0 <= rel < len(a), f"item {iid} relative offset {rel} outside the block"
        assert rel % 2 == 0, f"item {iid} relative offset {rel} is not record aligned"
        assert a[rel] == fake[record_offset(iid)], f"item {iid} rel offset points at the wrong byte"
    # The control must currently share a palette with the treated item, else it proves nothing.
    assert CONTROL not in TREATMENT, "the control must not also be treated"
    # And the treatment must actually move it somewhere else.
    for iid, newx in TREATMENT.items():
        assert 0 <= newx <= 15, "palette out of range"
    assert all(iid in NAMES for iid in list(TREATMENT) + [CONTROL]), \
        "every item in the owner-facing script needs its in-game name"
    print("selftest OK")


def main():
    if "--selftest" in sys.argv:
        selftest()
        return
    selftest()
    if "--undo" in sys.argv:
        undo()
        return
    if "--scan" in sys.argv:
        pid = find_pid() or sys.exit("fft_enhanced.exe is not running")
        h = k32.OpenProcess(PROCESS_QUERY_VM, False, pid)
        _, van, mod = locate(h)
        report(van, mod)
        k32.CloseHandle(h)
        return
    if "--poke" in sys.argv:
        poke()
        return
    sys.exit(__doc__.split("USAGE:")[1])


if __name__ == "__main__":
    main()
