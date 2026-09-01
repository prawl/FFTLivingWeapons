#!/usr/bin/env python
"""
READ-ONLY probe for LW-382: the party menu reports 0 weapons and 0 items while the shop's buy
screen still lists everything the player owns.

THE QUESTION. LW-368 MOVED the game's two per-item byte lists (the bag COUNT list and its
sibling FLAG list) onto a page the mod allocates, and re-pointed the 55 code references that
name the old blocks. The move is a MOVE, not a mirror: the old blocks go dead and are never
updated again, so they stay frozen at whatever they held the moment they were copied at boot,
which for a fresh process is zeros.

ListRelocation.CheckOldBlocks is the existing safety net for a MISSED reference, but it only
notices a missed WRITER (it re-reads the old blocks and complains when their bytes change).
A missed READER is invisible to it, and a missed reader is exactly what would produce this
bug's symptom pair: the party menu reads the dead old block and sees zeros, the shop reads the
live relocated page and sees the truth.

This probe reads BOTH and prints them side by side, so the question is answered by data instead
of by reasoning. It never writes.

  PREDICTION IF THE SPLIT-SOURCE THEORY IS RIGHT:
      new page  -> roughly as many nonzero entries as the shop lists (47 at time of writing)
      old block -> all zeros (or a stale count that does not match the shop)

  PREDICTION IF IT IS WRONG:
      both blocks agree, and the empty list is being produced somewhere further downstream
      (the list builder or the order template), not by the counts themselves.

RUN IT with the game RUNNING, Living Weapons ENABLED, the save loaded, and ideally the party
menu inventory open showing its wrong 0:

    python tools/probes/bag_count_split_probe.py
    python tools/probes/bag_count_split_probe.py --page 0x114AA0000

The relocated page address is printed in livingweapon.log's "Extended inventory armed" line
("item count lists relocated to 0x..."). It is allocated at runtime, so pass --page whenever the
log reports a different one than this file's default. Addresses and sizes below are transcribed
from LivingWeapon/Offsets.cs and LivingWeapon/Extended/ListRelocation.Sites.cs; the layout is
count list at page+0 and sibling flag list at page+SIBLING_PAGE_OFFSET.
"""
import ctypes as C
from ctypes import wintypes as W
import sys

PROC = "fft_enhanced"

# Offsets.cs: the two VANILLA (now dead once relocation installs) blocks.
OLD_COUNT = 0x1411A7C00
OLD_SIBLING = 0x1411A7700

# ListRelocation.Sites.cs
BLOCK_BYTES = 0x110          # full block incl. the 0xB-byte live-state tail
LIST_PROPER = 0x105          # the list proper: one byte per item id
SIBLING_PAGE_OFFSET = 0x400

DEFAULT_PAGE = 0x114AA0000   # from the 2026-09-01 session log; override with --page

k32 = C.WinDLL("kernel32", use_last_error=True)
psapi = C.WinDLL("psapi", use_last_error=True)


class _PMC(C.Structure):
    _fields_ = [("cb", W.DWORD), ("a1", W.DWORD)] + [
        (f"a{i}", C.c_size_t) for i in range(2, 11)]


def find_pid(name):
    """Largest-working-set match: the launcher spawns a second, tiny process with the same
    image name, and attaching to that one reads nothing (the 2-proc trap other probes hit)."""
    arr = (W.DWORD * 4096)()
    need = W.DWORD()
    psapi.EnumProcesses(arr, C.sizeof(arr), C.byref(need))
    best, bw = None, -1
    for i in range(need.value // C.sizeof(W.DWORD)):
        pid = arr[i]
        h = k32.OpenProcess(0x0410, False, pid)
        if not h:
            continue
        buf = C.create_unicode_buffer(260)
        if psapi.GetModuleBaseNameW(h, None, buf, 260) and buf.value.lower() == name.lower() + ".exe":
            p = _PMC()
            p.cb = C.sizeof(p)
            psapi.GetProcessMemoryInfo(h, C.byref(p), p.cb)
            if p.a2 > bw:
                best, bw = pid, p.a2
        k32.CloseHandle(h)
    return best


_H = None


def rpm(addr, n):
    buf = C.create_string_buffer(n)
    got = C.c_size_t()
    ok = k32.ReadProcessMemory(_H, C.c_void_p(addr), buf, n, C.byref(got))
    return buf.raw if ok and got.value == n else None


def nonzero(block):
    return {i: b for i, b in enumerate(block[:LIST_PROPER]) if b}


def summarize(label, addr, block):
    if block is None:
        print(f"  {label:<22} @ 0x{addr:X}  UNREADABLE")
        return None
    nz = nonzero(block)
    total = sum(nz.values())
    print(f"  {label:<22} @ 0x{addr:X}  {len(nz)} nonzero id(s), {total} item(s) total")
    if nz:
        shown = ", ".join(f"id{i}x{c}" for i, c in list(nz.items())[:14])
        print(f"  {'':<22}   {shown}{' ...' if len(nz) > 14 else ''}")
    return nz


def main():
    global _H
    page = DEFAULT_PAGE
    if "--page" in sys.argv:
        page = int(sys.argv[sys.argv.index("--page") + 1], 0)

    pid = find_pid(PROC)
    if not pid:
        print(f"{PROC}.exe is not running. Launch the game with Living Weapons ENABLED, load the "
              f"save, open the party menu inventory, then re-run.")
        sys.exit(2)
    _H = k32.OpenProcess(0x0410, False, pid)   # QUERY_INFORMATION | VM_READ, no write access
    if not _H:
        print(f"OpenProcess failed (err {C.get_last_error()})")
        sys.exit(1)
    print(f"attached to pid {pid} (READ ONLY)\n")

    print("OLD blocks (vanilla addresses; dead once the relocation installs):")
    old_c = summarize("old count list", OLD_COUNT, rpm(OLD_COUNT, BLOCK_BYTES))
    old_s = summarize("old flag list", OLD_SIBLING, rpm(OLD_SIBLING, BLOCK_BYTES))

    print(f"\nRELOCATED page (0x{page:X}; pass --page if the log names another):")
    new_c = summarize("new count list", page, rpm(page, BLOCK_BYTES))
    new_s = summarize("new flag list", page + SIBLING_PAGE_OFFSET,
                      rpm(page + SIBLING_PAGE_OFFSET, BLOCK_BYTES))

    print("\nVERDICT:")
    if new_c is None and old_c is None:
        print("  both count lists unreadable. Wrong page address, or the mod is not armed.")
    elif new_c and not old_c:
        print("  SPLIT CONFIRMED: the relocated page holds the real counts and the old block is")
        print("  empty. Any reader still pointed at the old block sees an empty inventory, which")
        print("  is the party menu's symptom. Hunt the missed READ reference.")
    elif old_c and not new_c:
        print("  INVERTED: the OLD block holds the counts and the relocated page is empty. The")
        print("  relocation copied nothing, or something zeroed the page after it was filled.")
    elif old_c and new_c and old_c != new_c:
        print("  BOTH populated but DISAGREEING. Two live sources of truth; compare the ids above.")
    elif old_c and new_c:
        print("  Both agree. The counts are NOT the bug; look downstream at the list builder or")
        print("  the order template instead.")
    else:
        print("  BOTH EMPTY. The counts really are zero in memory, so the bug is upstream of the")
        print("  lists: whatever should have populated them on save load did not.")

    k32.CloseHandle(_H)


if __name__ == "__main__":
    main()
