"""Read only. What do the two menu flags the twin pistol grant leans on, and the hand and
support fields of Ramza's roster row, read BEFORE the game initializes them, straight from the
exe file image on disk?

Why. LW-400 gives the twin pistol grant its own door. The door probes MenuOpenFlag and
PartyBrowseFlag (each must read 0 or 1) and the off hand, support, main hand and shield words of
roster slot 0 (each must sit inside the item id or ability key range), and it may step from the
mod's first tick. A door that reads uninitialized storage must not mistake it for a moved
address, so this probe maps each pinned VA through the PE section table: file backed bytes are
printed as the game maps them, and an address past a section's raw data is reported as zero
until the game writes it. It also prints the owning section's virtual span, which says whether
the page is mapped (and so readable) from module map time, and whether all fifty roster rows fit
inside it.
The PINS list is hand copied from Offsets.cs and must be re-copied after any re-anchor.

Usage. python tools/probes/twin_flags_file_image_probe.py [path\\to\\fft_enhanced.exe]
"""
import struct
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from lib.paths import STEAM_FFT  # noqa: E402

IMAGE_BASE = 0x140000000
ROSTER_BASE = 0x1411A7D10
ROSTER_STRIDE = 0x258
ROSTER_SLOTS = 50
PINS = [
    ("MenuOpenFlag", 0x140D508D0, 1),
    ("PartyBrowseFlag", 0x140D408E2, 1),
    ("Roster slot 0 RSupport (+0x0A)", ROSTER_BASE + 0x0A, 2),
    ("Roster slot 0 RRHand (+0x14)", ROSTER_BASE + 0x14, 2),
    ("Roster slot 0 ROffHand (+0x18)", ROSTER_BASE + 0x18, 2),
    ("Roster slot 0 RShield (+0x1A)", ROSTER_BASE + 0x1A, 2),
    ("Roster slot 0 RLevel (+0x1D)", ROSTER_BASE + 0x1D, 1),
    ("Roster slot 0 RNameId (+0x230)", ROSTER_BASE + 0x230, 2),
    ("Roster slot 49 last byte", ROSTER_BASE + ROSTER_SLOTS * ROSTER_STRIDE - 1, 1),
]


def sections(data):
    e_lfanew = struct.unpack_from("<I", data, 0x3C)[0]
    n_sec = struct.unpack_from("<H", data, e_lfanew + 6)[0]
    opt_size = struct.unpack_from("<H", data, e_lfanew + 20)[0]
    opt = e_lfanew + 24
    tds = struct.unpack_from("<I", data, e_lfanew + 8)[0]
    soi = struct.unpack_from("<I", data, opt + 56)[0]
    tbl = opt + opt_size
    out = []
    for i in range(n_sec):
        off = tbl + i * 40
        name = data[off:off + 8].rstrip(b"\x00").decode("ascii", "replace")
        vsize, va, raw_size, raw_ptr = struct.unpack_from("<IIII", data, off + 8)
        out.append((name, va, vsize, raw_ptr, raw_size))
    return out, tds, soi


def locate(secs, va):
    rva = va - IMAGE_BASE
    for name, sva, vsize, raw_ptr, raw_size in secs:
        if sva <= rva < sva + max(vsize, raw_size):
            delta = rva - sva
            file_off = None if delta >= raw_size else raw_ptr + delta
            return file_off, (name, sva, vsize, raw_size)
    return None, None


def main():
    exe = Path(sys.argv[1]) if len(sys.argv) > 1 else STEAM_FFT / "fft_enhanced.exe"
    data = exe.read_bytes()
    secs, tds, soi = sections(data)
    print(f"exe {exe}")
    print(f"TimeDateStamp 0x{tds:08X} SizeOfImage 0x{soi:08X}")
    for name, sva, vsize, raw_ptr, raw_size in secs:
        print(f"  section {name:8s} va 0x{IMAGE_BASE + sva:X}..0x{IMAGE_BASE + sva + vsize:X}"
              f"  raw 0x{raw_size:X} of virtual 0x{vsize:X}")
    for name, va, n in PINS:
        off, sec = locate(secs, va)
        if sec is None:
            print(f"{name:36s} 0x{va:X}  NOT IN ANY SECTION")
            continue
        sec_name, sva, vsize, raw_size = sec
        mapped_end = IMAGE_BASE + sva + vsize
        inside = "inside the mapped span" if va + n <= mapped_end else "PAST THE MAPPED SPAN"
        if off is None:
            print(f"{name:36s} 0x{va:X}  section {sec_name}: past raw data, reads ZERO until the game writes it; {inside}")
        else:
            print(f"{name:36s} 0x{va:X}  section {sec_name}: file backed {data[off:off + n].hex(' ')}; {inside}")


if __name__ == "__main__":
    main()
