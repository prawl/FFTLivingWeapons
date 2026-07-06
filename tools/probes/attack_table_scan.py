"""LW-31 stage 3 recon: external, READ-ONLY scan of the running fft_enhanced.exe for the
battle Attack menu's packed string tables and whatever references them.

Three modes (all ReadProcessMemory only; this probe cannot write and cannot crash the game):

  python tools/probes/attack_table_scan.py scan
      Walk every committed readable region, find each STANDALONE "Attack" C-string
      (NUL-bounded, ASCII and UTF-16LE: the same standalone rule as
      LivingWeapon/AttackCardProbeText.IsStandaloneHit), classify the desc that follows
      (vanilla 73-char / other / none), and hexdump the record block ABOVE each hit
      (a prior in-session dump showed id-like ints 0xE1/0xE2 and offset-like ints there).
      Also reports address deltas between hits (looking for the 0xA0-stride UTF-16 pool).

  python tools/probes/attack_table_scan.py ptrhunt <hexaddr> [...]
      Hunt every committed readable region for 8-byte little-endian POINTER VALUES that
      reference the given addresses (exact, and a small ladder of upstream offsets:
      -2 -4 -8 -0x10 -0x18 -0x20, for length-prefixed / object-headed string layouts).
      The slots found are the stage-3 redirect candidates.

  python tools/probes/attack_table_scan.py dump <hexaddr> [...] [lead=0x140] [tail=0x100]
      Hexdump lead bytes before plus tail bytes after each address (the
      read_prompt_addrs.py replacement; addrs come from the F6 census log or ptrhunt).

  python tools/probes/attack_table_scan.py records <label hexaddr>
      Mechanical decode of the record block above a packed "Attack" label: finds the
      0x24-stride {offset x3, id, offset2, 0 x4} records, solves what the offsets are
      relative to (they resolve to a COMMON anchor near the pool start; the solver
      reports the base convention that puts the anchor exactly on the label), and walks
      the string pool forward printing every NUL-terminated string in order.
"""
import ctypes
import ctypes.wintypes as wt
import struct
import sys

PROCESS_VM_READ = 0x0010
PROCESS_QUERY_INFORMATION = 0x0400
MEM_COMMIT = 0x1000
PAGE_GUARD = 0x100
PAGE_NOACCESS = 0x01
READABLE = {0x02, 0x04, 0x08, 0x20, 0x40, 0x80}  # RO, RW, WC, XR, XRW, XWC

CHUNK = 4 * 1024 * 1024
OVERLAP = 0x400          # keeps a straddling label+desc pair whole across chunk seams
LEAD_IN = 0x140          # record-block window dumped above each hit
TAIL = 0x100
VANILLA_DESC = "Attacks with the equipped weapon, or bare fists if no weapon is equipped."

k32 = ctypes.windll.kernel32
psapi = ctypes.windll.psapi


class MBI(ctypes.Structure):
    _fields_ = [("BaseAddress", ctypes.c_ulonglong), ("AllocationBase", ctypes.c_ulonglong),
                ("AllocationProtect", wt.DWORD), ("PartitionId", wt.WORD),
                ("RegionSize", ctypes.c_ulonglong), ("State", wt.DWORD),
                ("Protect", wt.DWORD), ("Type", wt.DWORD)]


def find_pid(name=b"fft_enhanced.exe"):
    arr = (wt.DWORD * 4096)()
    needed = wt.DWORD()
    psapi.EnumProcesses(arr, ctypes.sizeof(arr), ctypes.byref(needed))
    for i in range(needed.value // ctypes.sizeof(wt.DWORD)):
        h = k32.OpenProcess(PROCESS_VM_READ | PROCESS_QUERY_INFORMATION, False, arr[i])
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


def regions(h):
    """Yield (base, size) for every committed, readable, non-guard region."""
    addr = 0
    mbi = MBI()
    while k32.VirtualQueryEx(h, ctypes.c_void_p(addr), ctypes.byref(mbi), ctypes.sizeof(mbi)):
        if (mbi.State == MEM_COMMIT and not (mbi.Protect & (PAGE_GUARD | PAGE_NOACCESS))
                and (mbi.Protect & 0xFF) in READABLE):
            yield mbi.BaseAddress, mbi.RegionSize
        next_addr = mbi.BaseAddress + mbi.RegionSize
        if next_addr <= addr:
            break
        addr = next_addr


def chunks(h, base, size):
    """Yield (abs_base, data) chunks of one region with OVERLAP bytes of carry-over."""
    pos = base
    end = base + size
    carry = b""
    carry_base = base
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
    return s.encode("ascii") if enc == 1 else s.encode("utf-16-le")


def is_nul(buf, pos, enc):
    return 0 <= pos and pos + enc <= len(buf) and all(b == 0 for b in buf[pos:pos + enc])


def read_cstr(h, addr, enc, cap=300):
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


def gloss(b):
    return "".join(chr(c) if 0x20 <= c <= 0x7E else "." for c in b)


def hexdump(h, addr, lead=LEAD_IN, tail=TAIL):
    data = rpm(h, addr - lead, lead + tail)
    if data is None:
        # Region edge: retry without the lead-in before giving up.
        data = rpm(h, addr, tail)
        if data is None:
            print("  unreadable (freed or unmapped)")
            return
        lead = 0
    for off in range(0, len(data), 16):
        chunk = data[off:off + 16]
        mark = " <== hit" if off <= lead < off + 16 else ""
        print(f"  {addr - lead + off:012X} ({off - lead:+06X}): {chunk.hex(' ')}  |{gloss(chunk)}|{mark}")


def qword_gloss(h, addr, region_index):
    """Annotate the lead-in's aligned qwords: which look like pointers into committed memory."""
    data = rpm(h, addr - LEAD_IN, LEAD_IN)
    if data is None:
        return
    print("  lead-in qwords that land inside committed regions:")
    found = False
    for off in range(0, LEAD_IN - 7, 8):
        (val,) = struct.unpack_from("<Q", data, off)
        if 0x10000 <= val < 0x7FFFFFFFFFFF:
            for rb, rs in region_index:
                if rb <= val < rb + rs:
                    print(f"    {addr - LEAD_IN + off:012X} ({off - LEAD_IN:+06X}): -> {val:012X}")
                    found = True
                    break
    if not found:
        print("    (none)")


def cmd_scan(h):
    label = "Attack"
    pats = {1: enc_bytes(label, 1), 2: enc_bytes(label, 2)}
    hits = []          # (addr, enc, desc_text, desc_chars)
    seen = set()
    region_list = list(regions(h))
    total = sum(s for _, s in region_list)
    print(f"scanning {len(region_list)} regions, {total / (1 << 30):.2f} GiB committed readable")
    for rbase, rsize in region_list:
        for cbase, buf in chunks(h, rbase, rsize):
            for enc, pat in pats.items():
                start = 0
                while True:
                    pos = buf.find(pat, start)
                    if pos < 0:
                        break
                    start = pos + 1
                    addr = cbase + pos
                    if addr in seen:
                        continue
                    # standalone rule (AttackCardProbeText.IsStandaloneHit): NUL-bounded both
                    # sides; a boundary we cannot see within this buffer is NOT a hit.
                    before_ok = (addr == rbase) or is_nul(buf, pos - enc, enc)
                    after_ok = is_nul(buf, pos + len(pat), enc)
                    if not (before_ok and after_ok):
                        continue
                    seen.add(addr)
                    desc_addr = addr + (len(label) + 1) * enc
                    text, nchars = read_cstr(h, desc_addr, enc)
                    hits.append((addr, enc, text, nchars))
    hits.sort()
    print(f"\n{len(hits)} standalone hits")
    for addr, enc, text, nchars in hits:
        kind = ("VANILLA-DESC" if text == VANILLA_DESC
                else "EMPTY" if nchars == 0 else f"OTHER({nchars})")
        print(f"\n=== {addr:012X} enc{enc} {kind} desc={text[:80]!r} ===")
        hexdump(h, addr)
        qword_gloss(h, addr, region_list)
    if len(hits) > 1:
        print("\nhit-to-hit deltas (0xA0 stride would mark the UTF-16 pool):")
        for (a1, e1, _, _), (a2, e2, _, _) in zip(hits, hits[1:]):
            print(f"  {a1:012X} -> {a2:012X}  delta 0x{a2 - a1:X}  (enc{e1}->enc{e2})")


def cmd_ptrhunt(h, targets, exact=False):
    ladder = [0] if exact else [0, 2, 4, 8, 0x10, 0x18, 0x20]
    wanted = {}
    for t in targets:
        for d in ladder:
            wanted[struct.pack("<Q", t - d)] = (t, d)
    region_list = list(regions(h))
    total = sum(s for _, s in region_list)
    print(f"hunting {len(wanted)} pointer values over {total / (1 << 30):.2f} GiB")
    found = 0
    for rbase, rsize in region_list:
        for cbase, buf in chunks(h, rbase, rsize):
            for pat, (t, d) in wanted.items():
                start = 0
                while True:
                    pos = buf.find(pat, start)
                    if pos < 0:
                        break
                    start = pos + 1
                    slot = cbase + pos
                    if slot % 8 == 0:   # real pointer slots are 8-aligned; note misaligned skips
                        print(f"\nSLOT {slot:012X} holds {t - d:012X} (target {t:012X} - 0x{d:X})")
                        hexdump(h, slot, lead=0x40, tail=0x40)
                        found += 1
    print(f"\n{found} aligned slots found")


def cmd_records(h, label_addr, span=0x600):
    data = rpm(h, label_addr - span, span)
    if data is None:
        print("lead-in window unreadable")
        return

    # An id field is a u32 preceded by three EQUAL nonzero u32s (the offset triplet).
    # Byte-granular walk: the records need not be 4-aligned relative to our window.
    cands = []
    for p in range(12, len(data) - 8):
        idv, off2 = struct.unpack_from("<II", data, p)
        if not 0 < idv < 0x400:
            continue
        a, b, c = (struct.unpack_from("<I", data, p - d)[0] for d in (12, 8, 4))
        if a == b == c and 0 < a < 0x10000 and a != idv:
            cands.append((p, idv, a, off2))

    print(f"{len(cands)} triplet+id record candidates in the 0x{span:X} window above the label")
    for p, idv, off, off2 in cands:
        field0 = label_addr - span + p - 12   # abs addr of the offset triplet's first u32
        print(f"  field0@{field0:012X}  id=0x{idv:X}  off=0x{off:X}  off2=0x{off2:X}"
              f"  field0+off={field0 + off:012X}  field0+off2={field0 + off2:012X}")

    if cands:
        # If field0+off is constant across records, the offsets aim at one common anchor;
        # report where that anchor sits relative to the label so the engine's true base
        # convention (base = field0 + delta) can be read off directly.
        anchors = {label_addr - span + p - 12 + off for p, _, off, _ in cands}
        if len(anchors) == 1:
            a = anchors.pop()
            print(f"COMMON ANCHOR (field0-relative): {a:012X}  = label {label_addr - a:+X} bytes after it")
        else:
            print(f"no single field0-relative anchor ({len(anchors)} distinct); offsets are per-record")

    # Walk the packed pool forward from the label: NUL-terminated ASCII strings in order.
    print("\npool walk from the label:")
    pool = rpm(h, label_addr, 0x800)
    if pool is None:
        print("  unreadable")
        return
    pos = 0
    for i in range(40):
        end = pool.find(b"\x00", pos)
        if end < 0 or end == pos and i > 0 and pool[pos:pos + 4] == b"\x00" * 4:
            break
        s = pool[pos:end]
        printable = all(0x20 <= ch < 0x7F for ch in s) and len(s) > 0
        print(f"  [{i:2}] {label_addr + pos:012X} len={len(s):3} "
              + (f"{s.decode('ascii')!r}" if printable else f"(non-ascii {s[:16].hex()})"))
        if not printable and len(s) > 0:
            break
        pos = end + 1


def cmd_rawrecs(h, field0_addr, count=48):
    """Raw 0x24-stride record decode from a known record's field0 position: print all nine
    u32 fields per record and resolve any field that, taken record-relative, lands on a
    printable NUL-terminated ASCII string (the per-row name/desc locator hunt)."""
    for i in range(count):
        base = field0_addr + i * 0x24
        raw = rpm(h, base, 0x24)
        if raw is None or len(raw) < 0x24:
            print(f"{base:012X}: unreadable")
            return
        fields = struct.unpack("<9I", raw)
        line = f"{base:012X}: " + " ".join(f"{f:8X}" for f in fields)
        gloss_parts = []
        for fi, f in enumerate(fields):
            if 0 < f < 0x100000:
                text, n = read_cstr(h, base + f, 1, 48)
                if n >= 2 and all(0x20 <= ord(c) < 0x7F for c in text):
                    gloss_parts.append(f"f{fi}->{text!r}")
        print(line + ("   " + " | ".join(gloss_parts) if gloss_parts else ""))


def cmd_whopoints(h, target, back=0x8000, fwd=0x1000):
    """Find record-relative references: every u32 position p in [target-back, target+fwd)
    where p + u32(p) == target (the offset convention the catalog records use). Byte-granular,
    alignment reported. This catches a row locator no matter which field slot holds it."""
    lo = target - back
    data = rpm(h, lo, back + fwd)
    if data is None:
        print("window unreadable")
        return
    found = 0
    for p in range(0, len(data) - 3):
        (val,) = struct.unpack_from("<I", data, p)
        if val != 0 and lo + p + val == target:
            print(f"  u32@{lo + p:012X} (align {(lo + p) % 4}) holds 0x{val:X} -> {target:012X}")
            found += 1
    print(f"{found} record-relative references to {target:012X} in [-0x{back:X},+0x{fwd:X})")


def cmd_vanilla(h):
    """Fast catalog locate via two anchors: the canonical 73-char vanilla Attack desc
    (label = hit - 7), and the Squire job-command desc (label = hit - 0x7B, the serialized
    pool's fixed gap), which the mod NEVER paints, so copies whose Attack desc currently
    wears a composed line are still found. Runs the record decode on each verified label."""
    anchors = [
        (VANILLA_DESC.encode("ascii"), 7),
        (b"Squire job command. Comprises the most fundamental of all battle abilities.", 0x7B),
    ]
    labels = set()
    region_list = list(regions(h))
    total = sum(s for _, s in region_list)
    print(f"searching 2 anchors over {total / (1 << 30):.2f} GiB committed readable")
    for rbase, rsize in region_list:
        for cbase, buf in chunks(h, rbase, rsize):
            for pat, gap in anchors:
                start = 0
                while True:
                    pos = buf.find(pat, start)
                    if pos < 0:
                        break
                    start = pos + 1
                    labels.add(cbase + pos - gap)
    print(f"{len(labels)} label candidates")
    for label_addr in sorted(labels):
        head = rpm(h, label_addr, 7)
        packed = head == b"Attack\x00"
        print(f"\n=== label {label_addr:012X}  packed={packed} ===")
        if packed:
            cmd_records(h, label_addr)


def parse_kv(args, key, default):
    for a in args:
        if a.startswith(key + "="):
            return int(a.split("=", 1)[1], 16)
    return default


def main():
    pid, h = find_pid()
    if not h:
        print("game not running")
        sys.exit(1)
    print(f"pid {pid}")
    mode = sys.argv[1] if len(sys.argv) > 1 else "scan"
    rest = sys.argv[2:]
    addrs = [int(a, 16) for a in rest if "=" not in a]
    try:
        if mode == "scan":
            cmd_scan(h)
        elif mode == "vanilla":
            cmd_vanilla(h)
        elif mode == "ptrhunt":
            cmd_ptrhunt(h, addrs, exact=parse_kv(rest, "exact", 0) == 1)
        elif mode == "dump":
            lead = parse_kv(rest, "lead", LEAD_IN)
            tail = parse_kv(rest, "tail", TAIL)
            for addr in addrs:
                print(f"=== {addr:012X} ===")
                hexdump(h, addr, lead=lead, tail=tail)
        elif mode == "whopoints":
            back = parse_kv(rest, "back", 0x8000)
            fwd = parse_kv(rest, "fwd", 0x1000)
            for addr in addrs:
                print(f"=== relative refs to {addr:012X} ===")
                cmd_whopoints(h, addr, back, fwd)
        elif mode == "rawrecs":
            count = parse_kv(rest, "count", 0x30)
            for addr in addrs:
                cmd_rawrecs(h, addr, count)
        elif mode == "records":
            span = parse_kv(rest, "span", 0x600)
            for addr in addrs:
                print(f"=== records above {addr:012X} ===")
                cmd_records(h, addr, span)
        else:
            print(__doc__)
    finally:
        k32.CloseHandle(h)


if __name__ == "__main__":
    main()
