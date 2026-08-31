#!/usr/bin/env python
"""LW-351 READ-ONLY probe: are the new item ids listed in the two menu order templates?

Plain language: the party inventory and the unit equip picker each decide which weapons they are
willing to show from a little table of item ids. This prints both tables (every id up to the end
marker), says whether each extended id (261+) is in them and where, and prints how many of each
the player is carrying plus what roster slot 0 has in hand. Writes nothing, ever.

WHY IT EXISTS (2026-08-30): those tables are part of the SAVE FILE. The load routine restores them
byte-for-byte out of the save struct (copy at 0x14021B4BD for the inventory table, 0x14021B5BB for
the picker block) and the serializer writes them back (0x1402194BC), so a save written before a new
id ever appeared in one restores a table that will never name it. The mod seats owned extended ids
itself right after the load returns (LivingWeapon/Persistence/TemplateSeat.cs); this probe is how
that is checked from outside the game.

THE TWO TERMINATORS, do not mix them up: these templates end at 0x00FF. The menu LISTS the
order-rebuild hook repairs end at 0xFFFF.

Addresses and capacities come from LivingWeapon/Offsets.cs (LW-41 rule: a re-anchor there
re-anchors this probe in the same commit). The capacities are derived, not guessed:
  inventory 0x1407B2550: the load-apply copy into it moves 2 iterations of 0x80 bytes plus a
    0x1A-byte tail = 0x11A = 282 bytes = 141 words (loop 0x14021B560; counter r10 = 2 from
    `lea r10d,[r11+2]` at 0x14021B1C4; stride rsi = 0x80 from `lea esi,[rbx+0x7c]` at 0x14021B11C
    with ebx = 4). The next object any pointer names above it is 0x1407B266C, 0x11C higher.
  picker 0x141874540: its own pointer table (0x14067FA90) names the next sub-table at
    0x14187465A, exactly 0x11A = 282 bytes = 141 words higher.
`--disk` re-derives the pointer tables and those bounds from the exe on disk and needs no game.

Usage:
  python tools/probes/lw351_order_template_probe.py            # live, both tables + bag counts
  python tools/probes/lw351_order_template_probe.py --disk     # offline re-derivation of the bounds
"""
import pathlib
import struct
import sys

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parents[1]))
from lib import offsets as _offsets  # noqa: E402

INV, INV_WORDS, PICK, PICK_WORDS, BAG = _offsets.require(
    ["InventoryOrderTemplate", "InventoryOrderTemplateWords",
     "PickerOrderTemplate", "PickerOrderTemplateWords", "BagCountArray"])

END = 0x00FF
FIRST_EXTENDED = 261
LAST_EXTENDED = 268          # the LW-351 stage-2 ceiling; ids above it simply print as absent
ROSTER0 = 0x1411A7D10        # slot 0 combat row; rHand u16 at +0x14, lHand at +0x16
IMAGE_BASE = 0x140000000
EXE = (r"C:\Program Files (x86)\Steam\steamapps\common"
       r"\FINAL FANTASY TACTICS - The Ivalice Chronicles\FFT_enhanced.exe")
# The two pointer tables the game fetches the templates through; slot 0 of each is the weapons
# table, and the neighboring slots are what bounds it (see the header).
PTR_TABLES = [("inventory", 0x14067F498), ("picker", 0x14067FA90)]


def words_to_marker(blob, capacity):
    """(words before the marker, marker index or None) over at most `capacity` words."""
    w = list(struct.unpack("<%dH" % capacity, blob[:capacity * 2]))
    return (w[:w.index(END)], w.index(END)) if END in w else (w, None)


def report(label, addr, blob, capacity):
    listed, marker = words_to_marker(blob, capacity)
    print("%s template 0x%X (capacity %d words)" % (label, addr, capacity))
    if marker is None:
        print("   NO 0x00FF END MARKER in %d words: the table is not in a shape the mod will touch" % capacity)
    else:
        print("   %d ids listed, end marker at word %d, %d free word(s) after it"
              % (len(listed), marker, capacity - 1 - marker))
    for eid in range(FIRST_EXTENDED, LAST_EXTENDED + 1):
        if eid in listed:
            print("   id %d: LISTED at word %d" % (eid, listed.index(eid)))
    absent = [e for e in range(FIRST_EXTENDED, LAST_EXTENDED + 1) if e not in listed]
    print("   extended ids not listed: %s" % (absent or "none"))
    print("   first 8: %s" % listed[:8])
    print("   last 8 before the marker: %s" % listed[-8:])
    return listed, marker


def live():
    from lw346_live_disasm import find_pid, rd, k32   # noqa: E402  (same helper every lw346 probe uses)
    pid = find_pid("fft_enhanced.exe")
    if not pid:
        print("fft_enhanced.exe is not running"); return 2
    h = k32.OpenProcess(0x0010 | 0x0400, False, pid)   # VM_READ | QUERY_INFORMATION
    if not h:
        print("OpenProcess failed"); return 2
    for label, addr, cap in (("inventory", INV, INV_WORDS), ("picker", PICK, PICK_WORDS)):
        blob = rd(h, addr, cap * 2)
        if blob is None or len(blob) < cap * 2:
            print("%s template 0x%X: UNREADABLE" % (label, addr)); continue
        report(label, addr, blob, cap)
    print("bag counts:")
    for eid in range(FIRST_EXTENDED, LAST_EXTENDED + 1):
        b = rd(h, BAG + eid, 1)
        print("   count[%d] = %s" % (eid, b[0] if b else "unreadable"))
    hands = rd(h, ROSTER0 + 0x14, 4)
    if hands:
        r, l = struct.unpack("<HH", hands)
        print("roster slot 0 hands: rHand=%d (0x%04X) lHand=%d (0x%04X)" % (r, r, l, l))
    print("\nPASS for re-test 6 = every id whose bag count is non-zero is LISTED in BOTH templates,")
    print("each table still has its 0x00FF marker with free words after it, and the mod's log")
    print("shows no order-rebuild re-append for these tables.")
    return 0


def disk():
    """Offline: dump both pointer tables and derive each weapons table's bound from the nearest
    object any pointer in the image names above it."""
    data = pathlib.Path(EXE).read_bytes()
    e = struct.unpack_from("<I", data, 0x3C)[0]
    nsec = struct.unpack_from("<H", data, e + 6)[0]
    opt, opt_size = e + 24, struct.unpack_from("<H", data, e + 20)[0]
    print("TimeDateStamp 0x%08X, SizeOfImage 0x%X"
          % (struct.unpack_from("<I", data, e + 8)[0], struct.unpack_from("<I", data, opt + 56)[0]))
    secs = []
    for i in range(nsec):
        s = opt + opt_size + i * 40
        name = data[s:s + 8].rstrip(b"\0").decode("ascii", "replace")
        vsize, va, rsize, rptr = struct.unpack_from("<IIII", data, s + 8)
        secs.append((name, va, vsize, rptr, rsize))

    def va2off(va):
        rva = va - IMAGE_BASE
        for _, sva, vsize, rptr, rsize in secs:
            if sva <= rva < sva + vsize:
                d = rva - sva
                return rptr + d if d < rsize else None
        return None

    for label, tbl in PTR_TABLES:
        off = va2off(tbl)
        print("\n%s pointer table 0x%X:" % (label, tbl))
        for i in range(10):
            print("   [%d] 0x%016X" % (i, struct.unpack_from("<Q", data, off + i * 8)[0]))
    # Every qword in the initialized data sections that points into either neighborhood: the
    # nearest one above a template base is what bounds that template.
    for base, cap, span in ((INV, INV_WORDS, 0x1000), (PICK, PICK_WORDS, 0x1000)):
        lo, hi = base & ~(span - 1), (base & ~(span - 1)) + span
        targets = set()
        for name, sva, vsize, rptr, rsize in secs:
            if name not in (".data", ".rodata"):
                continue
            blob = data[rptr:rptr + rsize]
            for o in range(0, len(blob) - 8, 8):
                v = struct.unpack_from("<Q", blob, o)[0]
                if lo <= v < hi:
                    targets.add(v)
        above = sorted(t for t in targets if t > base)
        gap = above[0] - base if above else None
        print("\ntemplate 0x%X: next object named by any pointer = %s, gap 0x%X = %d words"
              % (base, "0x%X" % above[0] if above else "none", gap or 0, (gap or 0) // 2))
        print("   Offsets.cs pins %d words for it" % cap)
        if gap and gap // 2 != cap:
            print("   (the pin is the SMALLER of the load-apply copy size and this gap: the copy"
                  " into 0x%X moves 0x11A bytes = %d words, and the %d spare byte(s) up to the"
                  " next object are alignment padding, not table room)" % (base, cap, gap - cap * 2))
    return 0


if __name__ == "__main__":
    sys.exit(disk() if "--disk" in sys.argv[1:] else live())
