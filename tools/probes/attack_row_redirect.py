"""LW-31 stage 3: the OWNER-EYEBALL redirect poke for the battle Attack row.

Decoded 2026-07-06 (attack_table_scan.py, live recon): each JobCommand text-catalog copy
holds per-command records of nine u32s {nameOff, descOff, poolHeadOff, id, poolHead8Off,
0, 0, 0, ordinal}; offsets are RECORD-BASE-RELATIVE. The Attack command is record id 1,
sitting exactly 0x1FC1 bytes above its own "Attack" label (nameOff == the gap, verified
identical on all three catalog copies in one launch). Redirecting the row is therefore ONE
u32 write per copy: repoint nameOff at any NUL-terminated string in range.

WRITES MEMORY (4-byte offset fields only, never string bytes). Run only with the owner
watching, ideally in a throwaway battle; `restore` undoes everything. Verify-before-write:
a record is touched only while its label still reads "Attack" and its nameOff holds a
value this probe expects (vanilla 0x1FC1 or one of its own prior redirects).

  python tools/probes/attack_row_redirect.py status    <label hexaddr ...>
  python tools/probes/attack_row_redirect.py evasive   <label hexaddr ...>  row = "Evasive Stance" (14 chars, proves >6)
  python tools/probes/attack_row_redirect.py desc      <label hexaddr ...>  row = the desc string (length stress)
  python tools/probes/attack_row_redirect.py descsplit <label hexaddr ...>  the v2 mechanism proof: footprint
      becomes "Bubba Blade+3\0Kills: 42. Loki approves." with nameOff -> footprint start and
      descOff -> past the NUL. Row should read the name, hover desc the tail. Proves the
      descOff half (2026-07-06 plan review blocker: nameOff was proven alone).
  python tools/probes/attack_row_redirect.py restore   <label hexaddr ...>  vanilla offsets AND vanilla desc text

Label addresses come from `attack_table_scan.py vanilla` (per-launch relocation: re-scan
every launch).
"""
import ctypes
import ctypes.wintypes as wt
import struct
import sys

PROCESS_ALL = 0x0010 | 0x0020 | 0x0008 | 0x0400  # VM_READ | VM_WRITE | VM_OPERATION | QUERY
GAP = 0x1FC1            # label addr - Attack record base, catalog-layout constant
VANILLA_NAME_OFF = 0x1FC1
VANILLA_DESC_OFF = 0x1FC8
VANILLA_DESC = b"Attacks with the equipped weapon, or bare fists if no weapon is equipped."
SPLIT_NAME = b"Bubba Blade+3"
SPLIT_TAIL = b"Kills: 42. Loki approves."
k32 = ctypes.windll.kernel32
psapi = ctypes.windll.psapi


def find_pid(name=b"fft_enhanced.exe"):
    arr = (wt.DWORD * 4096)()
    needed = wt.DWORD()
    psapi.EnumProcesses(arr, ctypes.sizeof(arr), ctypes.byref(needed))
    for i in range(needed.value // ctypes.sizeof(wt.DWORD)):
        h = k32.OpenProcess(PROCESS_ALL, False, arr[i])
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


def wpm_u32(h, addr, val):
    return wpm(h, addr, struct.pack("<I", val))


def read_cstr(h, addr, cap=100):
    raw = rpm(h, addr, cap)
    if not raw:
        return None
    end = raw.find(b"\x00")
    s = raw[:end] if end >= 0 else raw
    return s.decode("ascii", "replace")


def inspect(h, label):
    """Returns (base, fields) after the verify ladder, or (None, reason)."""
    base = label - GAP
    head = rpm(h, label, 7)
    if head != b"Attack\x00":
        return None, f"label no longer reads 'Attack' ({head!r}); stale address, re-scan"
    raw = rpm(h, base, 0x24)
    if raw is None:
        return None, "record unreadable"
    f = struct.unpack("<9I", raw)
    if f[3] != 1:
        return None, f"record id is {f[3]}, not 1; geometry mismatch, do not write"
    return base, f


def targets_for(h, base, f, mode):
    """The nameOff value to write for a mode, derived from live geometry only."""
    if mode == "restore":
        return VANILLA_NAME_OFF
    if mode == "desc":
        return f[1]                        # descOff: row renders the 73-char desc string
    if mode == "evasive":
        rec2 = base + 0x24                 # id 2 = Evasive Stance, the next record
        raw = rpm(h, rec2, 0x10)
        if raw is None:
            return None
        n2, _, _, id2 = struct.unpack("<4I", raw)
        if id2 != 2:
            return None
        return (rec2 + n2) - base          # its name string, re-based to OUR record
    return None


def main():
    if len(sys.argv) < 3:
        print(__doc__)
        sys.exit(1)
    mode = sys.argv[1]
    labels = [int(a, 16) for a in sys.argv[2:]]
    pid, h = find_pid()
    if not h:
        print("game not running")
        sys.exit(1)
    print(f"pid {pid}, mode {mode}")
    known = {VANILLA_NAME_OFF}
    try:
        for label in labels:
            base, f = inspect(h, label)
            if base is None:
                print(f"{label:012X}: SKIP ({f})")
                continue
            cur_name = read_cstr(h, base + f[0], 100)
            if mode == "status":
                print(f"{label:012X}: record {base:012X} nameOff=0x{f[0]:X} -> {cur_name!r} descOff=0x{f[1]:X}"
                      f" -> {read_cstr(h, base + f[1], 100)!r}")
                continue
            if mode == "descsplit":
                # The v2 name-split proof: full footprint image (73 chars + terminator, NUL
                # padded so no stale bytes survive), then BOTH offsets in one 8-byte write.
                image = SPLIT_NAME + b"\x00" + SPLIT_TAIL
                image += b"\x00" * (len(VANILLA_DESC) + 1 - len(image))
                ok1 = wpm(h, label + 7, image)
                ok2 = wpm(h, base, struct.pack("<II", VANILLA_DESC_OFF, VANILLA_DESC_OFF + len(SPLIT_NAME) + 1))
                print(f"{label:012X}: image {'OK' if ok1 else 'REFUSED'}, offsets {'OK' if ok2 else 'REFUSED'};"
                      f" row should read {SPLIT_NAME!r}, hover desc {SPLIT_TAIL!r}")
                continue
            if mode == "restore":
                ok1 = wpm(h, base, struct.pack("<II", VANILLA_NAME_OFF, VANILLA_DESC_OFF))
                ok2 = wpm(h, label + 7, VANILLA_DESC + b"\x00")
                print(f"{label:012X}: offsets {'OK' if ok1 else 'REFUSED'}, vanilla desc {'OK' if ok2 else 'REFUSED'}")
                continue
            new_off = targets_for(h, base, f, mode)
            if new_off is None:
                print(f"{label:012X}: no target derivable for mode {mode}; skipped")
                continue
            # accept a rewrite only from vanilla or from one of our own prior states
            known.add(f[1])                              # desc mode's own value
            if f[0] not in known and read_cstr(h, base + f[0], 20) != "Evasive Stance":
                print(f"{label:012X}: nameOff 0x{f[0]:X} is not a state this probe wrote; refusing")
                continue
            preview = read_cstr(h, base + new_off, 100)
            if not preview:
                print(f"{label:012X}: target string unreadable; refusing")
                continue
            ok = wpm_u32(h, base + 0, new_off)
            print(f"{label:012X}: nameOff 0x{f[0]:X} -> 0x{new_off:X} ({'OK' if ok else 'WRITE REFUSED'}); row should read {preview[:40]!r}")
    finally:
        k32.CloseHandle(h)


if __name__ == "__main__":
    main()
