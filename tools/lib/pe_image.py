"""Read a span of the game exe the way the loader maps it, from the file on disk, no game needed.

A virtual address is walked through the PE section table to its file offset. Bytes past a
section's raw data are zero until the game writes them, so a span there is returned as zeros
rather than mistaken for file content. Lifted from tools/probes/static_table_file_image_probe.py
so a pipeline tool can share it without importing a probe.
"""
import struct

IMAGE_BASE = 0x140000000


def require_pe(data):
    """Refuses anything but a PE image with a readable header, one line instead of a struct traceback."""
    if len(data) < 0x40 or data[:2] != b"MZ":
        raise SystemExit("not a PE image (no MZ header)")
    e_lfanew = struct.unpack_from("<I", data, 0x3C)[0]
    if e_lfanew + 0x18 > len(data) or data[e_lfanew:e_lfanew + 4] != b"PE" + bytes(2):
        raise SystemExit("not a PE image (no PE signature)")


def pe_identity(data):
    require_pe(data)
    """(TimeDateStamp, SizeOfImage) of a PE image held in memory."""
    e_lfanew = struct.unpack_from("<I", data, 0x3C)[0]
    tds = struct.unpack_from("<I", data, e_lfanew + 8)[0]
    soi = struct.unpack_from("<I", data, e_lfanew + 24 + 56)[0]
    return tds, soi


def sections(data):
    """[(name, rva, vsize, raw_ptr, raw_size, flags)] in header order."""
    e_lfanew = struct.unpack_from("<I", data, 0x3C)[0]
    n_sec = struct.unpack_from("<H", data, e_lfanew + 6)[0]
    opt_size = struct.unpack_from("<H", data, e_lfanew + 20)[0]
    tbl = e_lfanew + 24 + opt_size
    out = []
    for i in range(n_sec):
        off = tbl + i * 40
        name = data[off:off + 8].rstrip(b"\x00").decode("ascii", "replace")
        vsize, va, raw_size, raw_ptr, _, _, _, _, flags = struct.unpack_from("<IIIIIIHHI", data, off + 8)
        out.append((name, va, vsize, raw_ptr, raw_size, flags))
    return out


def va_to_file(secs, va):
    """(file offset or None when past raw data, section name or None when unmapped)."""
    rva = va - IMAGE_BASE
    for name, sva, vsize, raw_ptr, raw_size, _ in secs:
        if sva <= rva < sva + max(vsize, raw_size):
            delta = rva - sva
            if delta >= raw_size:
                return None, name
            return raw_ptr + delta, name
    return None, None


def read_span(data, secs, va, n):
    """Bytes at va as the game maps them at module map time, or None when va is in no section."""
    off, sec = va_to_file(secs, va)
    if sec is None:
        return None
    if off is None:
        return bytes(n)
    return data[off:off + n]
