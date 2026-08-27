#!/usr/bin/env python
"""LW-346 READ-ONLY live audit of the post-build inventory-list compaction passes (1.5.2).

Plain language: after the party inventory list is built, several clean-up passes walk the
list and throw out any id the game's validity check rejects. One of them threw out the new
Moonblade (id 261) without fixing the row count the menu already took, which is the crash on
the last weapon row. This probe reads the running game (never writes) and prints, for each
clean-up pass: its disassembly around the validity call, the call target (is it really the
hooked thunk 0x1402B8EBC?), the mask applied to the list word BEFORE the call, and the exact
bytes at the branch the handoff proposes to patch (so a wrong old byte is caught here, not
at boot). It also dumps the word list buffer, the thunk's current first bytes (still E9?),
and the validity routine's shape.

Addresses: docs/research/ITEM_CAP_261_BREAK_JOURNEY.md (2026-08-26 night) / handoff.md.
Run with the game up (any screen after the image is decrypted). Writes nothing.
Usage: python tools/probes/lw346_compaction_audit.py [--words N]
"""
import ctypes as C
import ctypes.wintypes as W
import struct
import sys

from capstone import Cs, CS_ARCH_X86, CS_MODE_64

PROC = "fft_enhanced.exe"
BASE = 0x140000000
PE_KEY_152 = 0x6A5EA53C
VALIDITY_THUNK = 0x1402B8EBC
CATALOG_THUNK = 0x1402B8C74
WORD_BUF = 0x141811470

# (label, validity call site, proposed branch patch site, expected old bytes at the branch)
SITES = [
    ("sorter 0x140285B10", 0x140285B52, 0x140285B59, b"\x75\x25"),
    ("sib 0x140286265",    0x140286265, 0x14028626C, b"\x75\x23"),
    ("sib 0x1402879C3",    0x1402879C3, 0x1402879CA, b"\x0F\x85"),
    ("sib 0x140287B23",    0x140287B23, 0x140287B30, b"\x0F\x85"),
    ("sib 0x140288175",    0x140288175, 0x14028817C, b"\x0F\x85"),
    ("sib 0x1402882E2",    0x1402882E2, 0x1402882E9, b"\x0F\x85"),
]
# Compactors WITHOUT a validity call (context only).
OTHERS = [("builder-C compactor", 0x1403971C1), ("swap-style compactor", 0x140287D64)]
# Byte sites the marker/rig patch; expected value THIS boot (cap3/C armed, B-sibling not yet).
BYTE_SITES = [
    ("display cap 3 (builder A loop bound) 0x140288CDA", 0x140288CDA),
    ("cap C (builder C loop bound) 0x140397121", 0x140397121),
    ("B-sibling getter cap 0x140287570", 0x140287570),
]

k32 = C.WinDLL("kernel32", use_last_error=True)
PV = 0x0410  # PROCESS_VM_READ | PROCESS_QUERY_INFORMATION (read-only handle)


class PE32(C.Structure):
    _fields_ = [("dwSize", W.DWORD), ("cntUsage", W.DWORD), ("th32ProcessID", W.DWORD),
                ("th32DefaultHeapID", C.POINTER(C.c_ulong)), ("th32ModuleID", W.DWORD),
                ("cntThreads", W.DWORD), ("th32ParentProcessID", W.DWORD),
                ("pcPriClassBase", C.c_long), ("dwFlags", W.DWORD),
                ("szExeFile", C.c_char * 260)]


def find_pid(name):
    snap = k32.CreateToolhelp32Snapshot(2, 0)
    e = PE32(); e.dwSize = C.sizeof(PE32)
    pid = None
    if k32.Process32First(snap, C.byref(e)):
        while True:
            if name.lower() in e.szExeFile.decode(errors="ignore").lower():
                pid = e.th32ProcessID; break
            if not k32.Process32Next(snap, C.byref(e)): break
    k32.CloseHandle(snap)
    return pid


def rd(h, a, n):
    buf = (C.c_ubyte * n)(); g = C.c_size_t(0)
    if k32.ReadProcessMemory(h, C.c_void_p(a), buf, n, C.byref(g)) and g.value == n:
        return bytes(buf)
    return None


def hx(b):
    return " ".join("%02X" % x for x in b) if b else "<unreadable>"


md = Cs(CS_ARCH_X86, CS_MODE_64)


def func_start(h, addr, back=0x300):
    """Walk back from addr to the end of the nearest int3 (CC CC) pad; fall back to addr-back."""
    blob = rd(h, addr - back, back)
    if not blob:
        return addr - 0x40
    i = back - 1
    while i > 1:
        if blob[i - 1] == 0xCC and blob[i - 2] == 0xCC and blob[i] != 0xCC:
            return addr - back + i
        i -= 1
    return addr - back


def disasm(h, start, end, mark=()):
    blob = rd(h, start, end - start)
    if not blob:
        print("   <unreadable %X..%X>" % (start, end)); return
    for ins in md.disasm(blob, start):
        flag = " <==" if ins.address in mark else ""
        print("   %X  %-22s %s %s%s" % (ins.address, hx(ins.bytes), ins.mnemonic, ins.op_str, flag))


def call_target(h, site):
    b = rd(h, site, 5)
    if b and b[0] == 0xE8:
        return site + 5 + struct.unpack("<i", b[1:5])[0], b
    return None, b


def main():
    want_words = 320
    if "--words" in sys.argv:
        want_words = int(sys.argv[sys.argv.index("--words") + 1])
    pid = find_pid(PROC)
    if not pid:
        print("game not running"); return 2
    h = k32.OpenProcess(PV, False, pid)
    if not h:
        print("OpenProcess failed", C.get_last_error()); return 2
    hdr = rd(h, BASE, 0x400)
    ts = struct.unpack_from("<I", hdr, struct.unpack_from("<I", hdr, 0x3C)[0] + 8)[0]
    print("pid %d  PE TimeDateStamp 0x%08X  (%s)" % (pid, ts, "1.5.2 key OK" if ts == PE_KEY_152 else "UNEXPECTED BUILD"))

    print("\n== thunks (first 8 bytes; E9 = rig detour present) ==")
    for name, a in (("validity thunk", VALIDITY_THUNK), ("catalog thunk", CATALOG_THUNK)):
        b = rd(h, a, 8)
        tgt = (a + 5 + struct.unpack("<i", b[1:5])[0]) if b and b[0] == 0xE9 else None
        print("  %-15s %X: %s%s" % (name, a, hx(b), ("  -> jmp 0x%X" % tgt) if tgt else "  (NO E9)"))
        if tgt:
            print("     target head: %s" % hx(rd(h, tgt, 24)))

    print("\n== patch byte sites (this boot) ==")
    for name, a in BYTE_SITES:
        print("  %-50s %s" % (name, hx(rd(h, a, 1))))

    print("\n== word list buffer 0x%X ==" % WORD_BUF)
    raw = rd(h, WORD_BUF, want_words * 2)
    words = list(struct.unpack("<%dH" % want_words, raw)) if raw else []
    terms = [i for i, w in enumerate(words) if w == 0xFFFF]
    first = terms[0] if terms else len(words)
    ids = [w & 0x3FF for w in words[:first]]
    print("  entries before first FFFF: %d   FFFF positions (first 6): %s" % (first, terms[:6]))
    print("  ids: %s" % ids)
    print("  flags(>>10) set: %s" % sorted({w >> 10 for w in words[:first]}))
    print("  261 present: %s   256..260 present: %s" % (0x105 in ids, [i for i in ids if 256 <= i <= 260]))

    print("\n== compaction passes with a validity call ==")
    for label, call, br, old in SITES:
        tgt, cb = call_target(h, call)
        okc = "OK (thunk)" if tgt == VALIDITY_THUNK else "!! target 0x%X" % (tgt or 0)
        cur = rd(h, br, len(old))
        okb = "OK" if cur == old else "!! MISMATCH (want %s)" % hx(old)
        print("\n-- %s: call %X -> %s ; branch %X bytes %s %s" % (label, call, okc, br, hx(cur), okb))
        fs = func_start(h, call)
        disasm(h, fs, call + 0x60, mark=(call, br))

    print("\n== compactors without a validity call (context) ==")
    for label, a in OTHERS:
        print("\n-- %s @ %X" % (label, a))
        disasm(h, a - 0x40, a + 0x50, mark=(a,))

    print("\n== validity routine behind the thunk (first 48 instructions) ==")
    b = rd(h, VALIDITY_THUNK, 5)
    if b and b[0] == 0xE9:
        stub = VALIDITY_THUNK + 5 + struct.unpack("<i", b[1:5])[0]
        blob = rd(h, stub, 0x80)
        if blob:
            n = 0
            for ins in md.disasm(blob, stub):
                print("   %X  %-22s %s %s" % (ins.address, hx(ins.bytes), ins.mnemonic, ins.op_str))
                n += 1
                if n >= 48: break
    k32.CloseHandle(h)
    return 0


if __name__ == "__main__":
    sys.exit(main())
