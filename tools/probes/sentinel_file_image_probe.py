"""Read only. What do the six battle sentinels and Ramza's roster row read BEFORE the game
initializes them, straight from the exe file image on disk?

Why. LW-393 unchains the sensing lane from the roster door, so it probes the sentinels from the
mod's first tick, while the game is still booting. The lane's out of battle rule treats an all zero
tuple as a Mismatch (a moved address), which is right after arming but would be wrong if the bytes
sit in uninitialized storage and read zero until the game's own init runs. This probe maps each
pinned VA through the PE section table: file backed bytes are printed as the game maps them,
and an address past a section's raw data is reported as zero until init.
The PINS list is hand copied from Offsets.cs and must be re-copied after any re-anchor.

Usage. python tools/probes/sentinel_file_image_probe.py [path\to\fft_enhanced.exe]
"""
import struct
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from lib.paths import STEAM_FFT  # noqa: E402

IMAGE_BASE = 0x140000000
PINS = [
    ("Slot0", 0x140782A30, 4),
    ("Slot9", 0x140782A54, 4),
    ("EventId", 0x140782A94, 2),
    ("BattleMode", 0x1409069A0, 1),
    ("PauseFlag", 0x140C6B1C8, 1),
    ("SubmenuFlag", 0x140D4080C, 1),
    ("RosterBase level byte region (first 16)", 0x1411A7D10, 16),
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


def va_to_file(secs, va):
    rva = va - IMAGE_BASE
    for name, sva, vsize, raw_ptr, raw_size in secs:
        if sva <= rva < sva + max(vsize, raw_size):
            delta = rva - sva
            if delta >= raw_size:
                return None, name
            return raw_ptr + delta, name
    return None, None


def main():
    exe = Path(sys.argv[1]) if len(sys.argv) > 1 else STEAM_FFT / "fft_enhanced.exe"
    data = exe.read_bytes()
    secs, tds, soi = sections(data)
    print(f"exe {exe}")
    print(f"TimeDateStamp 0x{tds:08X} SizeOfImage 0x{soi:08X}")
    for name, va, n in PINS:
        off, sec = va_to_file(secs, va)
        if sec is None:
            print(f"{name:40s} 0x{va:X}  NOT IN ANY SECTION")
        elif off is None:
            print(f"{name:40s} 0x{va:X}  section {sec}: past raw data, reads ZERO until the game writes it")
        else:
            print(f"{name:40s} 0x{va:X}  section {sec}: file backed {data[off:off + n].hex(' ')}")


if __name__ == "__main__":
    main()
