"""LW-37 feasibility recon: does the EQUIP-CARD item-description text have a REDIRECTABLE record
structure (like the JobCommand/Attack catalog LW-31 cracked), or is it a flat inline string?

LW-37 wants to retire the slow whole-heap Kills-meter sweep by repointing the viewed weapon's desc
at a mod-owned buffer (one write), exactly as AttackRow does for the battle Attack card. That only
works if the item desc is reached through an INDIRECTION we can hijack. Two candidate levers:

  (A) a MATERIALIZED POINTER PAIR: an 8-byte {namePtr, descPtr} in a widget/FText struct holding the
      ABSOLUTE VA of the desc string. Repoint descPtr -> our buffer (full 64-bit VA, no +/-2GB
      constraint). This is the promising-but-untested lever the LW-31 decode flagged, and the shape a
      UE5 FText render copy would most plausibly use.
  (B) a RECORD-RELATIVE u32 offset (the Attack catalog convention): a u32 field at address p where
      p + u32(p) == descAddr. Repoint it, but the new text must land within u32-forward range of the
      record base (the plant-in-range problem, harder).

If NEITHER lever reaches the on-screen desc string, the desc is a flat inline copy with nothing to
repoint, and LW-37-as-specced is NOT possible (fallback: a faster TARGETED in-place paint, a
different project). This probe is the one-session recon that decides it. See docs/RELEASE_SCOPE.md
section 8, docs/TODO.md LW-37, and the LW-37 research writeup.

  python tools/probes/item_text_census.py find "<on-screen item text>" [enc=0]
      READ-ONLY. Search all committed readable memory for the substring (ASCII + UTF-16LE unless
      enc=1 or enc=2), print every hit with a hexdump + nearby pointer-looking qwords. Pick the hit
      that is the RENDERED card copy (usually the one `refs` finds pointers to) for the next step.

  python tools/probes/item_text_census.py refs <desc hexaddr> [...]
      READ-ONLY, THE CRUX. For each string address, hunt (A) whole-heap 8-byte absolute pointers to
      it (with an upstream ladder -2 -4 -8 -0x10 -0x18 -0x20 for length-prefixed / object-headed
      layouts) and (B) local record-relative u32 offsets (p + u32(p) == addr). Hexdumps each pointer
      slot so an adjacent {namePtr, descPtr} pair is visible. Prints a feasibility VERDICT.

  python tools/probes/item_text_census.py dump <hexaddr> [...] [lead=0x80] [tail=0x80]
      READ-ONLY hexdump around an address.

  python tools/probes/item_text_census.py poke <slot hexaddr> "<new text>" [enc=1] [hold=200]
      WRITES MEMORY. The definitive confirm for lever (A): allocate a buffer in the game, write
      <new text>, overwrite the 8-byte pointer at <slot> with the buffer VA, and HOLD it (re-apply
      every <hold> ms in case the engine rebuilds the widget) until Ctrl+C, which restores the
      original pointer and frees the buffer. Run only with the owner watching. If the on-screen card
      text changes, lever (A) is proven and LW-37 is possible.

  python tools/probes/item_text_census.py write <hexaddr> "<new text>" [enc=1] [hold=200]
      WRITES MEMORY IN PLACE. The pool-overwrite lever: overwrite len(new text) bytes AT <hexaddr>
      (no NUL added, so surrounding pool text and padding survive), HOLD it (re-apply every <hold> ms
      in case the pool is rebuilt), and restore the original bytes on Ctrl+C. Point it at a pool
      string (e.g. the "Kills:" line); reopen the card. If the card re-materializes our bytes, a
      TARGETED pool overwrite is the viable fast paint (LW-37's real goal) even though the pointer
      redirect is walled. Keep the replacement <= the field's padded width so the flavor stays intact.

  python tools/probes/item_text_census.py --selftest
      No game required: exercises the pure matchers (substring, absolute-pointer, relative-offset)
      against synthetic buffers with planted needles.
"""
import ctypes
import ctypes.wintypes as wt
import struct
import sys
import time

PROCESS_VM_READ = 0x0010
PROCESS_VM_WRITE = 0x0020
PROCESS_VM_OPERATION = 0x0008
PROCESS_QUERY_INFORMATION = 0x0400
MEM_COMMIT = 0x1000
MEM_RESERVE = 0x2000
MEM_RELEASE = 0x8000
PAGE_READWRITE = 0x04
PAGE_GUARD = 0x100
PAGE_NOACCESS = 0x01
READABLE = {0x02, 0x04, 0x08, 0x20, 0x40, 0x80}

CHUNK = 4 * 1024 * 1024
OVERLAP = 0x400            # keeps an 8-byte pointer whole across a chunk seam
PTR_LADDER = [0, 2, 4, 8, 0x10, 0x18, 0x20]   # upstream deltas: length-prefix / object-header layouts
REL_BACK = 0x8000
REL_FWD = 0x1000

k32 = ctypes.windll.kernel32
psapi = ctypes.windll.psapi
k32.OpenProcess.restype = wt.HANDLE
k32.VirtualAllocEx.restype = ctypes.c_ulonglong
k32.VirtualAllocEx.argtypes = [wt.HANDLE, ctypes.c_void_p, ctypes.c_size_t, wt.DWORD, wt.DWORD]
k32.VirtualFreeEx.restype = wt.BOOL
k32.VirtualFreeEx.argtypes = [wt.HANDLE, ctypes.c_void_p, ctypes.c_size_t, wt.DWORD]


class MBI(ctypes.Structure):
    _fields_ = [("BaseAddress", ctypes.c_ulonglong), ("AllocationBase", ctypes.c_ulonglong),
                ("AllocationProtect", wt.DWORD), ("PartitionId", wt.WORD),
                ("RegionSize", ctypes.c_ulonglong), ("State", wt.DWORD),
                ("Protect", wt.DWORD), ("Type", wt.DWORD)]


# --------------------------------------------------------------------------- process / memory I/O

def find_pid(access, name=b"fft_enhanced.exe"):
    arr = (wt.DWORD * 4096)()
    needed = wt.DWORD()
    psapi.EnumProcesses(arr, ctypes.sizeof(arr), ctypes.byref(needed))
    for i in range(needed.value // ctypes.sizeof(wt.DWORD)):
        h = k32.OpenProcess(access, False, arr[i])
        if not h:
            continue
        buf = ctypes.create_string_buffer(260)
        if psapi.GetModuleBaseNameA(h, None, buf, 260) and buf.value.lower() == name:
            return arr[i], h
        k32.CloseHandle(h)
    return None, None


def rpm(h, addr, n):
    buf = ctypes.create_string_buffer(n)
    got = ctypes.c_size_t()
    ok = k32.ReadProcessMemory(h, ctypes.c_void_p(addr), buf, n, ctypes.byref(got))
    return buf.raw[:got.value] if ok else None


def wpm(h, addr, data):
    wrote = ctypes.c_size_t()
    ok = k32.WriteProcessMemory(h, ctypes.c_void_p(addr), data, len(data), ctypes.byref(wrote))
    return bool(ok) and wrote.value == len(data)


def regions(h):
    """Yield (base, size) for every committed, readable, non-guard region."""
    addr = 0
    mbi = MBI()
    while k32.VirtualQueryEx(h, ctypes.c_void_p(addr), ctypes.byref(mbi), ctypes.sizeof(mbi)):
        if (mbi.State == MEM_COMMIT and not (mbi.Protect & (PAGE_GUARD | PAGE_NOACCESS))
                and (mbi.Protect & 0xFF) in READABLE):
            yield mbi.BaseAddress, mbi.RegionSize
        nxt = mbi.BaseAddress + mbi.RegionSize
        if nxt <= addr:
            break
        addr = nxt


def chunks(h, base, size):
    """Yield (abs_base, data) chunks of one region with OVERLAP bytes of carry-over."""
    pos, end, carry, carry_base = base, base + size, b"", base
    while pos < end:
        n = min(CHUNK, end - pos)
        data = rpm(h, pos, n)
        pos += n
        if data is None:
            carry, carry_base = b"", pos
            continue
        buf = carry + data
        yield carry_base, buf
        carry = buf[-OVERLAP:]
        carry_base = pos - len(carry)


def enc_bytes(s, enc):
    return s.encode("ascii", "replace") if enc == 1 else s.encode("utf-16-le")


def gloss(b):
    return "".join(chr(c) if 0x20 <= c <= 0x7E else "." for c in b)


def read_cstr(h, addr, enc, cap=200):
    raw = rpm(h, addr, cap * enc)
    if not raw:
        return "", 0
    out = []
    for i in range(0, len(raw) - enc + 1, enc):
        ch = raw[i] if enc == 1 else raw[i] | (raw[i + 1] << 8)
        if ch == 0:
            break
        out.append(chr(ch) if 0x20 <= ch <= 0xFFFD else ".")
    return "".join(out), len(out)


def hexdump(h, addr, lead=0x80, tail=0x80):
    data = rpm(h, addr - lead, lead + tail)
    if data is None:
        data = rpm(h, addr, tail)
        if data is None:
            print("  unreadable (freed or unmapped)")
            return
        lead = 0
    for off in range(0, len(data), 16):
        row = data[off:off + 16]
        mark = " <== here" if off <= lead < off + 16 else ""
        print(f"  {addr - lead + off:012X} ({off - lead:+06X}): {row.hex(' ')}  |{gloss(row)}|{mark}")


def qword_gloss(h, addr, region_index, lead=0x40, tail=0x40):
    """Aligned qwords around addr that land inside committed memory (pointer-like)."""
    data = rpm(h, addr - lead, lead + tail)
    if data is None:
        return
    shown = False
    for off in range(0, len(data) - 7, 8):
        (val,) = struct.unpack_from("<Q", data, off)
        if 0x10000 <= val < 0x7FFFFFFFFFFF and any(rb <= val < rb + rs for rb, rs in region_index):
            if not shown:
                print("  nearby aligned qwords pointing into committed memory:")
                shown = True
            print(f"    {addr - lead + off:012X} ({off - lead:+06X}): -> {val:012X}")


# --------------------------------------------------------------------------- PURE matchers (tested)

def substr_hits(buf, cbase, needle):
    """Absolute addresses of every occurrence of needle in buf."""
    out, start = [], 0
    while True:
        pos = buf.find(needle, start)
        if pos < 0:
            break
        start = pos + 1
        out.append(cbase + pos)
    return out


def abs_ptr_slots(buf, cbase, targets, ladder):
    """8-aligned addresses in buf holding a little-endian u64 == (target - delta) for delta in the
    ladder. Returns [(slot_abs, target, delta), ...]. Delta > 0 catches a pointer that lands N bytes
    INTO a string object (length prefix / object header before the char data)."""
    wanted = {}
    for t in targets:
        for d in ladder:
            wanted.setdefault(struct.pack("<Q", (t - d) & 0xFFFFFFFFFFFFFFFF), []).append((t, d))
    out = []
    for pat, metas in wanted.items():
        start = 0
        while True:
            pos = buf.find(pat, start)
            if pos < 0:
                break
            start = pos + 1
            slot = cbase + pos
            if slot % 8 == 0:
                for t, d in metas:
                    out.append((slot, t, d))
    return out


def rel_offsets(data, lo, target):
    """Positions p (abs = lo+p) where lo+p + u32(lo+p) == target (the catalog record-base-relative
    offset convention). Byte-granular. Returns [(pos_abs, val), ...]."""
    out = []
    for p in range(0, len(data) - 3):
        (val,) = struct.unpack_from("<I", data, p)
        if val != 0 and lo + p + val == target:
            out.append((lo + p, val))
    return out


# --------------------------------------------------------------------------- modes

def cmd_find(h, text, enc_arg):
    encs = [enc_arg] if enc_arg in (1, 2) else [1, 2]
    region_list = list(regions(h))
    total = sum(s for _, s in region_list)
    print(f"searching {len(region_list)} regions, {total / (1 << 30):.2f} GiB for {text!r} (enc {encs})")
    hits = []
    for enc in encs:
        needle = enc_bytes(text, enc)
        seen = set()
        for rbase, rsize in region_list:
            for cbase, buf in chunks(h, rbase, rsize):
                for addr in substr_hits(buf, cbase, needle):
                    if addr not in seen:
                        seen.add(addr)
                        hits.append((addr, enc))
    hits.sort()
    print(f"\n{len(hits)} hit(s)")
    for addr, enc in hits:
        text_full, n = read_cstr(h, addr, enc)
        print(f"\n=== {addr:012X} enc{enc} ({n} chars) {text_full[:90]!r} ===")
        hexdump(h, addr)
        qword_gloss(h, addr, region_list)
    if hits:
        print("\nNext: pick the rendered card copy and run:  "
              f"item_text_census.py refs {hits[0][0]:012X}")


def cmd_refs(h, targets):
    region_list = list(regions(h))
    total = sum(s for _, s in region_list)

    print(f"(A) hunting absolute 8-byte pointers to {len(targets)} target(s) over "
          f"{total / (1 << 30):.2f} GiB (ladder {['%+d' % d for d in PTR_LADDER]})...")
    abs_hits = {}
    for rbase, rsize in region_list:
        for cbase, buf in chunks(h, rbase, rsize):
            for slot, t, d in abs_ptr_slots(buf, cbase, targets, PTR_LADDER):
                abs_hits.setdefault(slot, (t, d))
    for slot, (t, d) in sorted(abs_hits.items()):
        print(f"\n  PTR SLOT {slot:012X} -> {t - d:012X} (target {t:012X} - 0x{d:X})")
        hexdump(h, slot, lead=0x20, tail=0x20)
    print(f"\n  => {len(abs_hits)} absolute-pointer slot(s) reference the string "
          "(materialized {name,desc} pointer-pair redirect candidates).")

    print("\n(B) hunting record-relative u32 offsets (p + u32(p) == addr, the catalog convention)...")
    rel_total = 0
    for t in targets:
        data = rpm(h, t - REL_BACK, REL_BACK + REL_FWD)
        if data is None:
            print(f"  window around {t:012X} unreadable")
            continue
        found = rel_offsets(data, t - REL_BACK, t)
        for pos, val in found:
            print(f"  u32@{pos:012X} (align {pos % 4}) holds 0x{val:X} -> {t:012X}")
        rel_total += len(found)
    print(f"  => {rel_total} record-relative offset(s) reference the string "
          "(catalog-record descOff redirect candidates).")

    print("\n" + "=" * 78)
    if abs_hits or rel_total:
        print("VERDICT: REDIRECTABLE INDIRECTION FOUND. LW-37 is plausible via the "
              + ("pointer-pair" if abs_hits else "record-offset") + " lever.")
        print("Confirm live: pick a descPtr slot above and run `poke <slot> \"NEW TEXT\"`; if the")
        print("on-screen card changes, the redirect is proven.")
    else:
        print("VERDICT: NO indirection reaches this string. It appears to be a FLAT INLINE copy with")
        print("nothing to repoint. LW-37 as specced is likely NOT possible for this surface; the")
        print("fallback is a faster TARGETED in-place paint (a different project). Re-check you")
        print("picked the RENDERED card copy (not the baked nxd) before concluding.")


def cmd_dump(h, addrs, lead, tail):
    for addr in addrs:
        print(f"=== {addr:012X} ===")
        hexdump(h, addr, lead=lead, tail=tail)


def cmd_poke(h, slot, text, enc, hold_ms):
    orig = rpm(h, slot, 8)
    if orig is None or len(orig) < 8:
        print("slot unreadable; aborting")
        return
    (orig_ptr,) = struct.unpack("<Q", orig)
    print(f"slot {slot:012X} currently -> {orig_ptr:012X} ({read_cstr(h, orig_ptr, enc)[0][:60]!r})")
    payload = enc_bytes(text, enc) + (b"\x00" * enc)
    buf_addr = k32.VirtualAllocEx(h, None, max(0x1000, len(payload)),
                                  MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE)
    if not buf_addr:
        print("VirtualAllocEx failed; is the handle write-capable?")
        return
    if not wpm(h, buf_addr, payload):
        print("failed to write payload into the allocated buffer")
        k32.VirtualFreeEx(h, ctypes.c_void_p(buf_addr), 0, MEM_RELEASE)
        return
    print(f"allocated {buf_addr:012X}, wrote {text!r} (enc{enc}). Repointing slot; open/refresh the")
    print("card. Ctrl+C restores the original pointer and frees the buffer.")
    try:
        wpm(h, slot, struct.pack("<Q", buf_addr))
        while True:
            time.sleep(hold_ms / 1000.0)
            cur = rpm(h, slot, 8)
            if cur and struct.unpack("<Q", cur)[0] != buf_addr:
                wpm(h, slot, struct.pack("<Q", buf_addr))   # re-hold: the engine rebuilt the widget
    except KeyboardInterrupt:
        pass
    finally:
        wpm(h, slot, orig)
        k32.VirtualFreeEx(h, ctypes.c_void_p(buf_addr), 0, MEM_RELEASE)
        print("\nrestored slot and freed buffer.")


def cmd_write(h, addr, text, enc, hold_ms):
    payload = enc_bytes(text, enc)
    orig = rpm(h, addr, len(payload))
    if orig is None or len(orig) < len(payload):
        print("address unreadable; aborting")
        return
    cur, _ = read_cstr(h, addr, enc, 60)
    print(f"addr {addr:012X} currently reads {cur[:50]!r}")
    print(f"overwriting {len(payload)} bytes in place with {text!r} (enc{enc}). Reopen the card;")
    print("Ctrl+C restores the original bytes.")
    try:
        wpm(h, addr, payload)
        while True:
            time.sleep(hold_ms / 1000.0)
            if rpm(h, addr, len(payload)) != payload:
                wpm(h, addr, payload)   # re-hold: the pool was rebuilt
    except KeyboardInterrupt:
        pass
    finally:
        wpm(h, addr, orig)
        print("\nrestored original bytes.")


# --------------------------------------------------------------------------- selftest

def selftest():
    cases = []
    def check(name, cond):
        cases.append((name, bool(cond)))

    # substring matcher (two occurrences: offset 4 and offset 17 == 0x11)
    buf = b"....Gloomfang....Gloomfang..."
    check("substr: two hits at right addrs", substr_hits(buf, 0x1000, b"Gloomfang") == [0x1004, 0x1011])
    check("substr: miss returns empty", substr_hits(buf, 0x1000, b"Excalibur") == [])

    # absolute-pointer matcher: plant a u64 pointer to target at an 8-aligned slot
    target = 0x140ABCDE00
    b = bytearray(0x40)
    struct.pack_into("<Q", b, 0x10, target)            # exact pointer at slot base+0x10 (aligned)
    struct.pack_into("<Q", b, 0x20, target - 0x10)     # pointer 0x10 into the object (ladder delta)
    struct.pack_into("<Q", b, 0x08, target)            # aligned exact at base+0x08 too
    hits = abs_ptr_slots(bytes(b), 0x2000, [target], PTR_LADDER)
    slots = sorted(s for s, _, _ in hits)
    check("absptr: finds exact aligned slots", 0x2010 in slots and 0x2008 in slots)
    check("absptr: finds ladder-delta slot (0x10 into object)",
          any(s == 0x2020 and d == 0x10 for s, _, d in hits))
    # a misaligned pointer must be ignored
    b2 = bytearray(0x20)
    struct.pack_into("<Q", b2, 0x03, target)           # slot 0x2003 is not 8-aligned
    check("absptr: ignores misaligned slot", abs_ptr_slots(bytes(b2), 0x2000, [target], [0]) == [])

    # relative-offset matcher: p + u32(p) == target. Plant the OFFSET value at p; target = lo+p+val.
    lo = 0x300000
    d = bytearray(0x100)
    p = 0x40
    rel_val = 0x1FC1
    rel_target = lo + p + rel_val
    struct.pack_into("<I", d, p, rel_val)
    off_hits = rel_offsets(bytes(d), lo, rel_target)
    check("reloff: finds the record-relative offset", (lo + p, rel_val) in off_hits)
    check("reloff: zero value never matches", all(v != 0 for _, v in off_hits))
    d0 = bytes(0x20)  # all zero
    check("reloff: all-zero buffer has no matches", rel_offsets(d0, lo, lo) == [])

    # encoding
    check("enc1 ascii", enc_bytes("Ab", 1) == b"Ab")
    check("enc2 utf16le", enc_bytes("Ab", 2) == b"A\x00b\x00")

    passed = sum(1 for _, ok in cases if ok)
    for name, ok in cases:
        if not ok:
            print(f"  FAIL: {name}")
    print(f"item_text_census selftest: {passed}/{len(cases)} passed.")
    return 0 if passed == len(cases) else 1


# --------------------------------------------------------------------------- CLI

def parse_kv(args, key, default):
    for a in args:
        if a.startswith(key + "="):
            return int(a.split("=", 1)[1], 0)
    return default


def main():
    argv = sys.argv[1:]
    if not argv or argv[0] in ("-h", "--help"):
        print(__doc__)
        return 0
    if argv[0] == "--selftest":
        return selftest()

    mode = argv[0]
    rest = argv[1:]
    writes = mode in ("poke", "write")
    access = PROCESS_VM_READ | PROCESS_QUERY_INFORMATION
    if writes:
        access |= PROCESS_VM_WRITE | PROCESS_VM_OPERATION
    pid, h = find_pid(access)
    if not h:
        print("game not running (fft_enhanced.exe not found)")
        return 1
    print(f"pid {pid}, mode {mode}")
    try:
        if mode == "find":
            if not rest:
                print("usage: find \"<item text>\" [enc=0]")
                return 2
            cmd_find(h, rest[0], parse_kv(rest, "enc", 0))
        elif mode == "refs":
            addrs = [int(a, 16) for a in rest if "=" not in a]
            if not addrs:
                print("usage: refs <desc hexaddr> [...]")
                return 2
            cmd_refs(h, addrs)
        elif mode == "dump":
            addrs = [int(a, 16) for a in rest if "=" not in a]
            cmd_dump(h, addrs, parse_kv(rest, "lead", 0x80), parse_kv(rest, "tail", 0x80))
        elif mode == "poke":
            if len(rest) < 2:
                print("usage: poke <slot hexaddr> \"<new text>\" [enc=1] [hold=200]")
                return 2
            cmd_poke(h, int(rest[0], 16), rest[1], parse_kv(rest, "enc", 1), parse_kv(rest, "hold", 200))
        elif mode == "write":
            if len(rest) < 2:
                print("usage: write <hexaddr> \"<new text>\" [enc=1] [hold=200]")
                return 2
            cmd_write(h, int(rest[0], 16), rest[1], parse_kv(rest, "enc", 1), parse_kv(rest, "hold", 200))
        else:
            print(__doc__)
            return 2
    finally:
        k32.CloseHandle(h)
    return 0


if __name__ == "__main__":
    sys.exit(main())
