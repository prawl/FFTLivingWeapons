#!/usr/bin/env python
"""
READ-ONLY probe for LW-382, step 2: dump the three MENU ORDER TEMPLATES.

WHY. Step 1 (bag_count_split_probe.py) cleared the bag counts: with the mod ON the relocated
page held 41 nonzero ids / 360 items, and with the mod OFF the vanilla block held the SAME 41
ids / 360 items. The sibling flag list reads all zeros in BOTH configurations, so an empty flag
list is its normal resting state, not a defect. The counts are therefore not why the party menu
shows nothing.

What is left is the thing the list actually RENDERS from: the order template, a chart of u16
item ids. Counts correct plus an empty chart equals a menu that lists nothing while the shop,
which does not read the chart, still lists everything. These charts are save state, which also
explains why the symptom survives a relaunch.

TemplateRelocation moves all three charts onto a mod-owned page and widens the walkers that
maintain them. Vanilla addresses and word counts are transcribed from LivingWeapon/Offsets.cs;
the page layout (inventory +0x000, picker +0x400, all-items +0x800, capacity 511 words each)
from LivingWeapon/Extended/TemplateRelocation.Sites.cs.

DELIBERATELY NO VERDICT LINE. Step 1's probe printed a cross-comparison verdict and was wrong
twice: once because it compared only the count lists while the interesting signal was elsewhere,
and once because with the mod OFF the relocated address is not unmapped but mapped by unrelated
data, which it happily read and compared as if it were ours. This one reports what it sees and
lets the reader decide. Run it BOTH ways (mod on, mod off) on the SAME save and diff the output
by eye.

RUN IT with the game running, the save loaded, and the party menu inventory open:

    python tools/probes/order_template_probe.py                      # mod OFF: vanilla is live
    python tools/probes/order_template_probe.py --page 0x114AB0000   # mod ON: add the page

The relocated page address is the "menu order charts relocated to 0x..." value in
livingweapon.log's "Extended inventory armed" line.
"""
import ctypes as C
from ctypes import wintypes as W
import sys

PROC = "fft_enhanced"

# Offsets.cs: the VANILLA charts (live when the mod is off / before relocation arms).
VANILLA = [
    ("inventory order template", 0x1407B2550, 141),
    ("equip-picker order template", 0x141874540, 141),
    ("picker all-items template", 0x141874726, 262),
]

# TemplateRelocation.Sites.cs: the same three charts on the mod's own page, in page order.
PAGE_LAYOUT = [
    ("inventory order template", 0x000, 511),
    ("equip-picker order template", 0x400, 511),
    ("picker all-items template", 0x800, 511),
]

# Chart terminators. 0xFFFF is the wall the relocation writes into spare room (LW-371). 0x00FF
# is the game's own "none" value (the same 255 that means an empty hand in the roster census);
# the vanilla baseline captured 2026-09-01 shows 16 real ids, then 0x00FF, then a run of zeros,
# so treating 0x00FF as live overcounts every chart by one. 0x0000 is unwritten space.
TERMINATOR = 0xFFFF
NONE_MARKER = 0x00FF
ENDERS = (0x0000, NONE_MARKER, TERMINATOR)

k32 = C.WinDLL("kernel32", use_last_error=True)
psapi = C.WinDLL("psapi", use_last_error=True)


class _PMC(C.Structure):
    _fields_ = [("cb", W.DWORD), ("a1", W.DWORD)] + [
        (f"a{i}", C.c_size_t) for i in range(2, 11)]


def find_pid(name):
    """Largest working set: the launcher spawns a second process with the same image name."""
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


def words(raw):
    return [raw[i] | (raw[i + 1] << 8) for i in range(0, len(raw) - 1, 2)]


def dump(label, addr, count):
    raw = rpm(addr, count * 2)
    if raw is None:
        print(f"  {label:<30} @ 0x{addr:X}  UNREADABLE ({count} words)")
        return
    w = words(raw)
    live = [x for x in w if x not in ENDERS]
    # how many entries before the chart first stops naming an item
    run = 0
    for x in w:
        if x in ENDERS:
            break
        run += 1
    zeros = sum(1 for x in w if x == 0)
    nones = sum(1 for x in w if x == NONE_MARKER)
    walls = sum(1 for x in w if x == TERMINATOR)
    print(f"  {label:<30} @ 0x{addr:X}  {count} words: "
          f"{len(live)} live id(s), leading run {run}, {zeros} zero, {nones} x 00FF, {walls} x FFFF")
    if w:
        head = " ".join(f"{x:04X}" for x in w[:24])
        print(f"  {'':<30}   head: {head}{' ...' if count > 24 else ''}")


def main():
    global _H
    page = None
    if "--page" in sys.argv:
        page = int(sys.argv[sys.argv.index("--page") + 1], 0)

    pid = find_pid(PROC)
    if not pid:
        print(f"{PROC}.exe is not running.")
        sys.exit(2)
    _H = k32.OpenProcess(0x0410, False, pid)   # read only, no write access requested
    if not _H:
        print(f"OpenProcess failed (err {C.get_last_error()})")
        sys.exit(1)
    print(f"attached to pid {pid} (READ ONLY)\n")

    print("VANILLA chart addresses (live when the mod is off; dead once relocation arms):")
    for label, addr, count in VANILLA:
        dump(label, addr, count)

    if page is None:
        print("\n(no --page given, so the relocated charts were not read; pass the address from\n"
              " livingweapon.log's \"menu order charts relocated to 0x...\" when the mod is on)")
    else:
        print(f"\nRELOCATED charts on the mod's page (0x{page:X}):")
        for label, off, count in PAGE_LAYOUT:
            dump(label, page + off, count)

    print("\nHOW TO READ THIS: a chart is a list of u16 item ids. A healthy inventory chart names\n"
          "the ids the menu will list, so its leading run should be close to the number of\n"
          "distinct items owned. A leading run of 0 means the menu is told to render nothing,\n"
          "which is the symptom under investigation. Compare the SAME line between a mod-on and\n"
          "a mod-off run of the same save.")

    k32.CloseHandle(_H)


if __name__ == "__main__":
    main()
