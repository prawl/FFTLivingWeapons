#!/usr/bin/env python
"""LW-346 LIVE byte check of the 261 cap-break rig's gate addresses (READ-ONLY).

WHY LIVE. The offline pass (lw346_capbreak_reanchor.py) hit the wall it was built to
detect: every gate site's on-disk bytes differ from the rig's live-verified oldbytes
in EVERY build, including the very build the rig was proven on -- the sites sit in
Denuvo-processed ranges that only hold their real bytes in the running process. Disk
cannot answer "did the gates survive 1.5.2"; only the live image can.

WHAT IT CHECKS. The rig's constants (FFTHandsFree capbreak-equip branch, live-verified
2026-06-26 on the 1.5.0 build) against the running fft_enhanced.exe:
  1. equip clamp     @0x140284C82: byte 0x06, inside lea ecx,[rdx+6] (8D 4A 06 @C80)
  2. count-cap imm   @0x140284875: 41 B8 03 01 00 00 (mov r8d,0x103; patch byte @+3)
  3. catalog lea     @0x1402B8CDA: 48 8D 04 85 + disp32 10 F9 67 00 @CDE
  4. weapon thunk    @0x1402B8CE8: byte0 == E9 (target decoded live by the rig, so a
  5. validity thunk  @0x1402B8F30:   moved target is FINE; a non-E9 shape is a FAIL)
Plus: live PE TimeDateStamp must be the 1.5.2 key 0x6A5EA53C; the two catalog data
bases (0x14067F910 ext / 0x14080EA90 main) are compared live-vs-disk to prove the
data region is plaintext; and every multi-byte signature is re-checked at site+0x10
as a NEGATIVE CONTROL (must mismatch there, else the check discriminates nothing).
On a site mismatch, the live .code section is scanned for the signature (exact, then
disp32-masked) so a drifted gate arrives with candidate new addresses attached.

Writes nothing. Run with the game at the world map or later (image fully decrypted).
Usage: python tools/probes/lw346_capbreak_live_bytecheck.py
"""
import ctypes as C
import ctypes.wintypes as W
import re
import struct
import sys

PROC = "fft_enhanced.exe"
BASE = 0x140000000
PE_KEY_152 = 0x6A5EA53C
DISK_152 = (r"C:\Program Files (x86)\Steam\steamapps\common"
            r"\FINAL FANTASY TACTICS - The Ivalice Chronicles\FFT_enhanced.exe")

k32 = C.WinDLL("kernel32", use_last_error=True)
PV = 0x0410  # PROCESS_VM_READ | PROCESS_QUERY_INFORMATION


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


def live_sections(h):
    hdr = rd(h, BASE, 0x600)
    e_lfanew = struct.unpack_from("<I", hdr, 0x3C)[0]
    ts = struct.unpack_from("<I", hdr, e_lfanew + 8)[0]
    nsec = struct.unpack_from("<H", hdr, e_lfanew + 6)[0]
    opt_size = struct.unpack_from("<H", hdr, e_lfanew + 20)[0]
    secs = []
    for i in range(nsec):
        s = e_lfanew + 24 + opt_size + i * 40
        name = hdr[s:s + 8].rstrip(b"\0").decode("ascii", "replace")
        vsize, va = struct.unpack_from("<II", hdr, s + 8)
        secs.append((name, BASE + va, vsize))
    return ts, secs


def scan_section(h, sec_va, sec_len, sig, mask=None):
    """Scan a live section for sig; mask = (offset, len) wildcard. Returns VAs (max 8)."""
    if mask:
        mo, ml = mask
        pat = re.escape(sig[:mo]) + b"." * ml + re.escape(sig[mo + ml:])
    else:
        pat = re.escape(sig)
    hits = []
    CHUNK = 1 << 20
    ov = len(sig) - 1
    off = 0
    while off < sec_len and len(hits) < 8:
        want = min(CHUNK + ov, sec_len - off)
        blob = rd(h, sec_va + off, want)
        if blob:
            for m in re.finditer(pat, blob, re.DOTALL):
                if m.start() < CHUNK:
                    hits.append(sec_va + off + m.start())
        off += CHUNK
    return hits


SITES = [
    ("equip-clamp lea", 0x140284C80, bytes([0x8D, 0x4A, 0x06]), None,
     "clamp disp8 @+2; patch 06->07"),
    ("count-cap mov", 0x140284875, bytes([0x41, 0xB8, 0x03, 0x01, 0x00, 0x00]), None,
     "imm byte @+3; patch 01->02"),
    ("catalog lea+disp32", 0x1402B8CDA,
     bytes([0x48, 0x8D, 0x04, 0x85, 0x10, 0xF9, 0x67, 0x00]), (4, 4),
     "disp32 @+4 is the relocation patch site"),
    ("weapon-stat thunk", 0x1402B8CE8, bytes([0xE9]), None,
     "shape check only; rig decodes rel32 live"),
    ("validity thunk", 0x1402B8F30, bytes([0xE9]), None,
     "shape check only; rig decodes rel32 live"),
]

DATA = [
    ("ext-catalog base", 0x14067F910, 60),
    ("main-catalog base", 0x14080EA90, 96),
    ("id37 record (clone src)", 0x14080EC4C, 12),
]


def main():
    pid = find_pid(PROC)
    if not pid:
        sys.exit(f"{PROC} not running")
    h = k32.OpenProcess(PV, False, pid)
    if not h:
        sys.exit(f"OpenProcess failed err={C.get_last_error()}")

    ts, secs = live_sections(h)
    code = next((s for s in secs if s[0] == ".code"), None)
    print(f"pid={pid}  live PE ts=0x{ts:08X}  "
          f"{'== 1.5.2 key OK' if ts == PE_KEY_152 else '!! NOT the 1.5.2 key %#x' % PE_KEY_152}")
    print()

    verdicts = []
    for name, va, sig, mask, note in SITES:
        live = rd(h, va, max(len(sig), 5))
        ok = live is not None and live[:len(sig)] == sig
        # negative control: multi-byte signatures must NOT match at va+0x10
        neg = "n/a"
        if len(sig) >= 3:
            shifted = rd(h, va + 0x10, len(sig))
            neg = "FAILED-AT-SHIFT ok" if shifted != sig else "!! MATCHES SHIFTED (non-discriminating)"
        print(f"== {name} @0x{va:X}  ({note})")
        print(f"   live: {hx(live)}   expect: {hx(sig)}{' + rel32' if sig[0] == 0xE9 else ''}")
        print(f"   verdict: {'MATCH' if ok else 'MISMATCH'}   negative-control: {neg}")
        if live and live[0] == 0xE9:
            rel = struct.unpack("<i", live[1:5])[0]
            print(f"   decoded jmp target: 0x{va + 5 + rel:X}")
        if not ok and code and len(sig) >= 3:
            hits = scan_section(h, code[1], code[2], sig)
            kind = "exact"
            if not hits and mask:
                hits = scan_section(h, code[1], code[2], sig, mask)
                kind = "masked"
            print(f"   {kind} .code scan: {len(hits)} hit(s): "
                  + (", ".join("0x%X" % x for x in hits) or "none"))
        verdicts.append((name, ok))
        print()

    disk = open(DISK_152, "rb").read() if DATA else b""
    # disk VA->off via section table (raw copy of the offline tool's method)
    e_lfanew = struct.unpack_from("<I", disk, 0x3C)[0]
    nsec = struct.unpack_from("<H", disk, e_lfanew + 6)[0]
    opt_size = struct.unpack_from("<H", disk, e_lfanew + 20)[0]
    dsecs = []
    for i in range(nsec):
        s = e_lfanew + 24 + opt_size + i * 40
        vsize, dva, rsize, rptr = struct.unpack_from("<IIII", disk, s + 8)
        dsecs.append((dva, vsize, rptr, rsize))

    def disk_at(va, n):
        rva = va - BASE
        for dva, vsize, rptr, rsize in dsecs:
            if dva <= rva < dva + vsize and rva - dva < rsize:
                o = rptr + (rva - dva)
                return disk[o:o + n]
        return None

    for name, va, n in DATA:
        live = rd(h, va, n)
        d = disk_at(va, n)
        same = live is not None and live == d
        print(f"== {name} @0x{va:X}: live {'== disk (plaintext confirmed)' if same else '!= disk'}")
        print(f"   live: {hx(live[:24]) if live else '<unreadable>'}")
        if not same:
            print(f"   disk: {hx(d[:24]) if d else '<not in raw>'}")
        verdicts.append((name + " live==disk", same))
        print()

    good = sum(1 for _, ok in verdicts if ok)
    print(f"SUMMARY: {good}/{len(verdicts)} checks green")
    for name, ok in verdicts:
        print(f"   {'PASS' if ok else 'FAIL'}  {name}")
    k32.CloseHandle(h)


if __name__ == "__main__":
    main()
