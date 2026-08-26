#!/usr/bin/env python
"""LW-346 candidate confirmation for the re-anchored 261 cap-break gates (READ-ONLY).

lw346_capbreak_live_bytecheck.py proved all five gate sites drifted off their 1.5.0
addresses on the live 1.5.2 image, and its .code scans produced a candidate map with
two clean block deltas: the equip-resolver pair moved -0x78 (clamp 0x140284C80 ->
0x140284C08, count-cap 0x140284875 -> 0x1402847FD, old 0x40B internal spacing intact)
and the catalog-accessor cluster moved -0x74 (lea+disp32 exact-matched at 0x1402B8C66
WITH the unchanged 0x0067F910 disp32). This probe verifies the full candidate set in
one pass, including the two encrypted-thunk sites the scan could not signature-match
(their E9 rel32 operands move independently): each must read E9 at old-0x74 with a
plausible in-image target. Negative controls re-check each signature at +0x10.

PASS here = the offline/live byte check is DONE and these constants are ready to be
wired into FFTHandsFree CapBreakEquipHook.cs / ExtendedCatalogRelocator.cs (one
commit, per docs/PATCH_REANCHOR.md). The live arm re-probe (Moonblade equips on
1.5.2) remains a separate owner-driven step.

Writes nothing. Usage: python tools/probes/lw346_capbreak_live_confirm.py
"""
import ctypes as C
import ctypes.wintypes as W
import struct
import sys

PROC = "fft_enhanced.exe"
BASE = 0x140000000
IMG_TOP = BASE + 0x18D78000

k32 = C.WinDLL("kernel32", use_last_error=True)
PV = 0x0410


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


# (label, old_va, new_va, expected_sig or None for E9-shape, note)
CANDIDATES = [
    ("equip-clamp lea", 0x140284C80, 0x140284C08, bytes([0x8D, 0x4A, 0x06]),
     "new clamp disp8 = new_va+2 = 0x140284C0A"),
    ("count-cap mov", 0x140284875, 0x1402847FD, bytes([0x41, 0xB8, 0x03, 0x01, 0x00, 0x00]),
     "new imm patch byte = new_va+3 = 0x140284800"),
    ("catalog lea+disp32", 0x1402B8CDA, 0x1402B8C66,
     bytes([0x48, 0x8D, 0x04, 0x85, 0x10, 0xF9, 0x67, 0x00]),
     "new disp32 site = new_va+4 = 0x1402B8C6A; ext catalog base unchanged"),
    ("catalog accessor entry", 0x1402B8CB8, 0x1402B8C44, None,
     "context dump only (no byte oracle on record)"),
    ("weapon-stat thunk", 0x1402B8CE8, 0x1402B8C74, b"\xE9",
     "E9 shape + in-image target required"),
    ("validity thunk", 0x1402B8F30, 0x1402B8EBC, b"\xE9",
     "E9 shape + in-image target required"),
]


def main():
    pid = find_pid(PROC)
    if not pid:
        sys.exit(f"{PROC} not running")
    h = k32.OpenProcess(PV, False, pid)
    if not h:
        sys.exit(f"OpenProcess failed err={C.get_last_error()}")

    results = []
    for label, old, new, sig, note in CANDIDATES:
        ctx = rd(h, new - 8, 8 + 16)
        print(f"== {label}: 0x{old:X} -> 0x{new:X} (delta {new - old:+#x})")
        print(f"   context [-8..+16]: {hx(ctx)}")
        print(f"   note: {note}")
        if sig is None:
            print()
            continue
        live = rd(h, new, max(len(sig), 5))
        ok = live is not None and live[:len(sig)] == sig
        if sig == b"\xE9" and ok:
            rel = struct.unpack("<i", live[1:5])[0]
            tgt = new + 5 + rel
            in_img = BASE <= tgt < IMG_TOP
            print(f"   E9 target: 0x{tgt:X}  ({'in-image OK' if in_img else '!! OUT OF IMAGE'})")
            ok = ok and in_img
        shifted = rd(h, new + 0x10, len(sig))
        neg = shifted != sig
        print(f"   verdict: {'CONFIRMED' if ok else 'REJECTED'}   "
              f"negative-control(+0x10): {'ok' if neg else '!! also matches'}")
        results.append((label, ok and neg))
        print()

    # spacing invariants: the two blocks must preserve old internal spacing
    print("== block-spacing invariants")
    print(f"   resolver pair: new 0x140284C08-0x1402847FD = "
          f"{0x140284C08 - 0x1402847FD:#x} (old {0x140284C80 - 0x140284875:#x})")
    print(f"   catalog cluster: lea->weapon {0x1402B8C74 - 0x1402B8C66:#x} (old "
          f"{0x1402B8CE8 - 0x1402B8CDA:#x}); lea->validity "
          f"{0x1402B8EBC - 0x1402B8C66:#x} (old {0x1402B8F30 - 0x1402B8CDA:#x})")
    print()

    # ---- display rig (CapBreakDisplayHook.cs): four iteration caps, imm 0x105 low byte ----
    # Caps 1/2 sit inside the proven -0x78 resolver block; caps 3/4 live in an unprobed
    # region (0x140288/9xxx) so we window-scan [old-0x300, old+0x100] for the 05 01 00 00
    # imm bytes and report every hit with context (deltas are hints, never interpolated).
    print("== display iteration caps (old 0x05 -> patch 0x06; imm 0x105 = the 261 bound)")
    DISPLAY = [("cap1", 0x14028479C, -0x78), ("cap2", 0x140284841, -0x78),
               ("cap3", 0x140288D52, None), ("cap4", 0x1402890EC, None)]
    for label, old, delta in DISPLAY:
        if delta is not None:
            cand = old + delta
            b = rd(h, cand, 4)
            ok = b is not None and b[:2] == b"\x05\x01"
            ctx = rd(h, cand - 8, 16)
            print(f"   {label}: 0x{old:X} -> 0x{cand:X} ({delta:+#x}): {hx(b)} "
                  f"{'CONFIRMED' if ok else 'REJECTED'}   ctx: {hx(ctx)}")
        else:
            lo = old - 0x300
            blob = rd(h, lo, 0x400)
            hits = []
            if blob:
                p = 0
                while True:
                    i = blob.find(b"\x05\x01\x00\x00", p)
                    if i < 0: break
                    hits.append(lo + i); p = i + 1
            print(f"   {label}: 0x{old:X} window scan [{lo:#x}..+0x400]: {len(hits)} hit(s)")
            for va in hits:
                c = rd(h, va - 8, 12)
                print(f"      0x{va:X} (delta {va - old:+#x})  ctx[-8..+4]: {hx(c)}")
    print()

    # weak live sanity on the count array base (runtime BSS; expect small inventory counts)
    counts = rd(h, 0x1411A7C00, 64)
    if counts:
        small = sum(1 for b in counts if b <= 99)
        print(f"== count-array @0x1411A7C00 first 64 ids: {small}/64 values <=99 "
              f"({'plausible' if small >= 56 else 'SUSPECT'})")
        print(f"   bytes: {hx(counts[:32])}")
    print()

    good = sum(1 for _, ok in results if ok)
    print(f"SUMMARY: {good}/{len(results)} candidates confirmed")
    if good == len(results):
        print()
        print("NEW 1.5.2 CONSTANTS (wire into FFTHandsFree in one commit):")
        print("   CapBreakEquipHook.ClampAddr           = 0x140284C0AL  (was 0x140284c82)")
        print("   CapBreakEquipHook.CountCapAddr        = 0x140284800L  (was 0x140284878)")
        print("   CapBreakEquipHook.WeaponStatAccessorAddr = 0x1402B8C74L (was 0x1402B8CE8)")
        print("   CapBreakEquipHook.ValidityThunkAddr   = 0x1402B8EBCL  (was 0x1402B8F30)")
        print("   ExtendedCatalogRelocator.ExtendedDisp32Addr = 0x1402B8C6AL (was 0x1402B8CDE)")
        print("   catalog accessor entry (doc only)     = 0x1402B8C44   (was 0x1402B8CB8)")
        print("   ExtendedCatalogBase / MainCatalogBase / CountArrayBase: UNCHANGED")
    k32.CloseHandle(h)


if __name__ == "__main__":
    main()
