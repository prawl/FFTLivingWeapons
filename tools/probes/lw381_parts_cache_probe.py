"""LW-381: locate the game's UITextureParts cache and one icon's entry in it. READ ONLY.

Opens the game with PROCESS_QUERY_INFORMATION | PROCESS_VM_READ (0x0410); never writes.

WHAT WE KNOW (1.5.2, static + live disassembly 2026-09-01)
  * Menu icons are loaded by URI: "UITextureParts://ui/ffto/icon/%s" (string 0x1407042D0),
    through the loader at 0x1403FBE3C: Load(manager, uri, flag).
  * The loader first runs a LOOKUP (0x1403FBB80) over the manager's entry vector, a plain
    array of entry pointers between [manager+0xE0] and [manager+0xE8]. An entry is matched on
    its interned id (+8) when both sides have one, else by strcmp on its name (+0). A hit is
    returned as is: no re-read. A miss creates an entry (0x1403FB960) and loads it from
    "<uri minus scheme>.utexpt" (0x1403F3DAC, async handle stored at entry+0x54, state at
    +0x58/+0x59).
  * Every drawing consumer (0x1403E890D, 0x1403E8B50, 0x1403E9D12, 0x1403EB011, 0x1403EC036,
    0x1403EC803) performs the lookup by NAME on every draw, then asks the entry for a part.
    Nobody caches the entry pointer.
  So an icon that was drawn once stays served from its entry for as long as the entry sits in
  the vector, and a pac patch can only show once the entry is gone and a screen calls Load
  again. Early-session refreshes happened because something purged the vector; late-session
  refusals because nothing did. The lever this probe prepares is the vector itself.

WHAT THIS PROBE DOES
  1. Finds every in-memory string starting "UITextureParts://ui/ffto/icon/" and lists the
     ones naming the requested icon id (so the exact URI is observed, not guessed).
  2. For each such string S: finds pointers to S (the entry's +0 name field) -> entry E.
  3. For each E: finds pointers to E (the vector slot) -> the manager (slot lies inside
     [manager+0xE0, manager+0xE8)). Verifies the manager by reading +0xE0/+0xE8 and checking
     the slot sits between them, then prints the vector size and the entry's fields.

USAGE (game running, menu open or closed)
    python tools/probes/lw381_parts_cache_probe.py 22
"""
import ctypes
import struct
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
from lw381_icon_memscan import CHUNK, find_pid, k32, read, regions  # noqa: E402

PREFIX = b"UITextureParts://ui/ffto/icon/"
MEM_PRIVATE = 0x20000


def sweep(h, needle, want_type=None):
    """Absolute addresses of every occurrence of needle in committed readable memory."""
    out = []
    for base, size, mtype in regions(h):
        if want_type is not None and mtype != want_type:
            continue
        carry, carry_addr, off = b"", base, 0
        while off < size:
            n = min(CHUNK, size - off)
            buf = read(h, base + off, n)
            if buf is None:
                break
            hay, hay_addr = carry + buf, carry_addr
            i = hay.find(needle)
            while i >= 0:
                out.append(hay_addr + i)
                i = hay.find(needle, i + 1)
            keep = min(len(hay), len(needle) - 1)
            carry = hay[len(hay) - keep:] if keep else b""
            carry_addr = hay_addr + len(hay) - keep
            off += n
    return out


def sweep_many(h, needles, want_type=None):
    """{needle: [addr, ...]} for every needle, in ONE pass over committed memory. The per
    needle sweep() above costs a full pass each; a pointer chase that needs a dozen needles
    took ten minutes that way, this takes one."""
    needles = list(needles)
    out = {n: [] for n in needles}
    if not needles:
        return out
    maxlen = max(len(n) for n in needles)
    for base, size, mtype in regions(h):
        if want_type is not None and mtype != want_type:
            continue
        carry, carry_addr, off = b"", base, 0
        while off < size:
            n = min(CHUNK, size - off)
            buf = read(h, base + off, n)
            if buf is None:
                break
            hay, hay_addr = carry + buf, carry_addr
            for nd in needles:
                i = hay.find(nd)
                while i >= 0:
                    out[nd].append(hay_addr + i)
                    i = hay.find(nd, i + 1)
            keep = min(len(hay), maxlen - 1)
            carry = hay[len(hay) - keep:] if keep else b""
            carry_addr = hay_addr + len(hay) - keep
            off += n
    return out


def cstr(h, addr, cap=128):
    b = read(h, addr, cap) or b""
    return b.split(b"\0", 1)[0].decode(errors="replace")


def q(h, addr):
    b = read(h, addr, 8)
    return struct.unpack("<Q", b)[0] if b and len(b) == 8 else None


def main():
    if len(sys.argv) < 2:
        print(__doc__); sys.exit(2)
    icon = int(sys.argv[1], 0)
    tag = f"_{icon:03d}_".encode()

    pid = find_pid()
    if pid is None:
        print("fft_enhanced.exe is not running."); sys.exit(1)
    h = k32.OpenProcess(0x0410, False, pid)
    if not h:
        print(f"could not open pid {pid} (error {ctypes.get_last_error()})."); sys.exit(1)
    print(f"game pid {pid}")

    strings = sweep(h, PREFIX)
    names = {}
    for s in strings:
        txt = cstr(h, s)
        if tag.decode() in txt:
            names[s] = txt
    print(f"{len(strings)} icon URI string(s) resident; {len(names)} name icon {icon}:")
    for s, txt in sorted(names.items()):
        print(f"  0x{s:X}  {txt}")
    if not names:
        print("no URI string for this icon is resident: it has never been loaded this session,")
        print("or its name is interned somewhere this sweep did not reach.")
        k32.CloseHandle(h); return

    for s, txt in sorted(names.items()):
        ptrs = sweep(h, struct.pack("<Q", s), MEM_PRIVATE)
        entries = [p for p in ptrs if (p & 7) == 0]
        print(f"\nstring 0x{s:X}: {len(entries)} aligned pointer(s) to it (candidate entries):")
        for e in entries:
            name_ptr, ident = q(h, e), q(h, e + 8)
            handle = read(h, e + 0x54, 4); state = read(h, e + 0x58, 2)
            handle = struct.unpack("<I", handle)[0] if handle else None
            print(f"  entry 0x{e:X}: name->0x{name_ptr:X} id={ident & 0xFFFFFFFF if ident is not None else None} "
                  f"loadHandle={handle} state={state.hex() if state else None}")
            slots = [p for p in sweep(h, struct.pack("<Q", e), MEM_PRIVATE) if (p & 7) == 0]
            for slot in slots:
                # the slot must lie inside a manager's [begin,end) vector; find that manager by
                # scanning for a qword equal to some vector base <= slot, i.e. look for the
                # pair (begin,end) bracketing the slot: search pointers to candidate begins.
                # Cheap route: the vector is contiguous entry pointers, so walk back from the
                # slot while the qwords still look like entry pointers (name ptr -> PREFIX).
                begin = slot
                while True:
                    prev = q(h, begin - 8)
                    if prev is None or (prev & 7) or not (read(h, q(h, prev) or 0, len(PREFIX)) or b"").startswith(b"UITextureParts://"):
                        break
                    begin -= 8
                end = slot + 8
                while True:
                    nxt = q(h, end)
                    if nxt is None or (nxt & 7) or not (read(h, q(h, nxt) or 0, len(PREFIX)) or b"").startswith(b"UITextureParts://"):
                        break
                    end += 8
                count = (end - begin) // 8
                idx = (slot - begin) // 8
                # now find the manager: a qword equal to `begin` at some M+0xE0 with M+0xE8 == end
                mgrs = [m - 0xE0 for m in sweep(h, struct.pack("<Q", begin), MEM_PRIVATE) if q(h, m + 8) == end]
                print(f"    slot 0x{slot:X}: vector [0x{begin:X}, 0x{end:X}) = {count} entries, index {idx}; "
                      f"manager(s) with +0xE0/+0xE8 == this vector: {[hex(m) for m in mgrs] or 'none found'}")
    k32.CloseHandle(h)


if __name__ == "__main__":
    main()
