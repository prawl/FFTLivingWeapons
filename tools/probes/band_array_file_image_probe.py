"""Read only. What do the battle band, the combat structs and the static unit array read BEFORE
the game populates them, straight from the exe file image on disk?

Why. LW-398 gives the band region (measured from CombatAnchor) and the static unit array their own
door in the parts registry. Both are only filled while a battle is on, so the door must read
Waiting, never Mismatch, from the first tick until the first battle. A probe that keyed on zeros
would be wrong if either region were file backed with nonzero bytes (a static table the mod could
check offline) and right if both sit in zero filled storage the game writes at battle load. This
probe maps each pinned VA through the PE section table: file backed bytes are printed as the game
maps them, and an address past a section's raw data is reported as zero until init. For each whole
region it also counts the nonzero file backed bytes, so "zero until the game writes it" is a
measured fact for the full span and not just the first slot.
The PINS list is hand copied from Offsets.cs and must be re-copied after any re-anchor.

Usage. python tools/probes/band_array_file_image_probe.py [path\to\fft_enhanced.exe]
"""
import struct
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from lib.paths import STEAM_FFT  # noqa: E402

IMAGE_BASE = 0x140000000
COMBAT_ANCHOR = 0x141855CE0
COMBAT_STRIDE = 0x200
BAND_ENTRY = 0x1C
BAND_SLOTS = 49
FRAME_READ_BASE = COMBAT_ANCHOR - 24 * COMBAT_STRIDE
BAND_READ_BASE = FRAME_READ_BASE + BAND_ENTRY
ARRAY_BASE = 0x140899F50
ARRAY_STRIDE = 0x200
SLOTS_BACK = 20
N_SLOTS = 30
ARRAY_READ_BASE = ARRAY_BASE - (SLOTS_BACK - 1) * ARRAY_STRIDE

PINS = [
    ("CombatAnchor (Ramza slot, first 16)", COMBAT_ANCHOR, 16),
    ("FrameReadBase (band frame 0, first 16)", FRAME_READ_BASE, 16),
    ("BandReadBase (band entry 0, first 16)", BAND_READ_BASE, 16),
    ("ArrayBase (first 16)", ARRAY_BASE, 16),
    ("ArrayReadBase (slot 0, first 16)", ARRAY_READ_BASE, 16),
    ("TurnQueue (first 16)", 0x1407832A0, 16),
    ("ActorPtr (8)", 0x14186AF68, 8),
    ("Acted (1)", 0x140782A8C, 1),
    ("RosterBase slot 0 (first 16)", 0x1411A7D10, 16),
]

REGIONS = [
    ("band frames, 49 slots", FRAME_READ_BASE, BAND_SLOTS * COMBAT_STRIDE),
    ("static unit array, 30 slots", ARRAY_READ_BASE, N_SLOTS * ARRAY_STRIDE),
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


def region_report(data, secs, label, start, length):
    backed = 0
    nonzero = 0
    unmapped = 0
    for va in range(start, start + length):
        off, sec = va_to_file(secs, va)
        if sec is None:
            unmapped += 1
        elif off is not None:
            backed += 1
            if data[off] != 0:
                nonzero += 1
    sec0 = va_to_file(secs, start)[1]
    sec1 = va_to_file(secs, start + length - 1)[1]
    print(f"{label:30s} 0x{start:X}..0x{start + length - 1:X} sections {sec0}..{sec1}: "
          f"{backed} of {length} bytes file backed, {nonzero} nonzero, {unmapped} outside any section")


def main():
    exe = Path(sys.argv[1]) if len(sys.argv) > 1 else STEAM_FFT / "fft_enhanced.exe"
    data = exe.read_bytes()
    secs, tds, soi = sections(data)
    print(f"exe {exe}")
    print(f"TimeDateStamp 0x{tds:08X} SizeOfImage 0x{soi:08X}")
    for name, va, vsize, raw_ptr, raw_size in secs:
        print(f"  section {name:8s} va 0x{IMAGE_BASE + va:X} vsize 0x{vsize:X} raw 0x{raw_size:X}")
    for name, va, n in PINS:
        off, sec = va_to_file(secs, va)
        if sec is None:
            print(f"{name:42s} 0x{va:X}  NOT IN ANY SECTION")
        elif off is None:
            print(f"{name:42s} 0x{va:X}  section {sec}: past raw data, reads ZERO until the game writes it")
        else:
            print(f"{name:42s} 0x{va:X}  section {sec}: file backed {data[off:off + n].hex(' ')}")
    for label, start, length in REGIONS:
        region_report(data, secs, label, start, length)


if __name__ == "__main__":
    main()
