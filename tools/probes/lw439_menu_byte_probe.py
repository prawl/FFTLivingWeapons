"""Read only. Is the world map menu byte (Offsets.MenuOpenFlag, 0x140D508D0) byte 4 of a pointer
into the game image, as the LW-439 offline analysis says?

Why. On the Denuvo-free build the byte read 246 with the party menu open where the August tapes
read 1. Offline, the byte sits at +0x20 of the first of two 0xE800 byte per frame record buffers
(base 0x140D508B0), byte 4 of the SECOND record's link pointer into the ordering tables in .data,
which lands there when the screen's first record is the 0x1C byte packet. Byte 4 of a pointer into
the image is 0x01 on the old fixed base 0x140000000 and 0xF6 on the ASLR base 0x7FF65FC60000 the
2026-10-17 launches recorded. This probe reads the whole qword live so the owner can see the
pointer, not just its one byte.

What to do. Launch the game, load a save, run the probe, then open the party menu, wait two
seconds, close it. Each change prints one line. Expected with a menu open: the qword is an
address inside the game image (base <= q < base + SizeOfImage) and the byte equals
(base >> 32) & 0xFF. Expected with the menu closed: the byte is 0.

Usage. python tools/probes/lw439_menu_byte_probe.py [seconds, default 60]
"""
import ctypes
import ctypes.wintypes
import struct
import sys
import time
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from lib.livebase import open_game, rebase  # noqa: E402
from lib.paths import STEAM_FFT  # noqa: E402
from lib.pe_image import pe_identity  # noqa: E402

MENU_BYTE = 0x140D508D0
QWORD_AT = MENU_BYTE - 4

k32 = ctypes.windll.kernel32
k32.ReadProcessMemory.argtypes = [ctypes.wintypes.HANDLE, ctypes.c_void_p, ctypes.c_void_p,
                                  ctypes.c_size_t, ctypes.POINTER(ctypes.c_size_t)]


def rpm(handle, addr, n):
    buf = ctypes.create_string_buffer(n)
    got = ctypes.c_size_t()
    ok = k32.ReadProcessMemory(handle, ctypes.c_void_p(addr), buf, n, ctypes.byref(got))
    return buf.raw if ok and got.value == n else None


def describe(q, byte, base, size):
    """The qword is the second record's link pointer into the ordering tables in .data. Its byte 4
    is the base's byte 4, or one more when the low 32 bits carry. A zero qword is no link in that
    slot this frame, which is what the closed menu reads, not proof that no menu exists."""
    if q == 0:
        return "byte 0  qword 0  no link in that slot this frame"
    inside = base <= q < base + size
    tag = f"image pointer, rva 0x{q - base:X}" if inside else "not an image pointer"
    expect = (base >> 32) & 0xFF
    ok = byte in (expect, (expect + 1) & 0xFF)
    match = "byte matches base byte 4" if ok else f"byte differs from base byte 4 (0x{expect:02X})"
    return f"byte {byte} (0x{byte:02X})  qword 0x{q:016X}  {tag}  {match}"


def main():
    seconds = float(sys.argv[1]) if len(sys.argv) > 1 else 60.0
    _, size = pe_identity((STEAM_FFT / "fft_enhanced.exe").read_bytes())
    pid, handle, base = open_game()
    live = rebase(QWORD_AT, base)
    print(f"pid {pid} base 0x{base:X} SizeOfImage 0x{size:X} reading 8 bytes at 0x{live:X} (menu byte at +4)")
    last = None
    end = time.time() + seconds
    while time.time() < end:
        raw = rpm(handle, live, 8)
        if raw is None:
            line = "unreadable"
        else:
            q = struct.unpack_from("<Q", raw)[0]
            line = describe(q, raw[4], base, size)
        if line != last:
            print(f"{time.strftime('%H:%M:%S')}  {line}")
            last = line
        time.sleep(0.1)


if __name__ == "__main__":
    main()
