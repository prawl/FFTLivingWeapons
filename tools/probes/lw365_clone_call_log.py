"""LW-365 call logger on the mod's own accessor clones (READ-mostly; the only writes are the
5-byte jumps it plants on the five clone entries the mod already owns and a fresh RWX page).

Plain language: the game asks the mod's cloned item lookups "what is weapon N" many times per
swing. This probe records who asked and with which id, so a swing can be compared between a
vanilla weapon and a moved design without a debugger. It found the LW-365 divergence in one
pair of swings: every lookup saw the right id except the very last art call, which received
0 for the moved design and the weapon id for the vanilla one.

Ring: 128 entries of 32 bytes at page+0x400. Entry = (rcx | tag<<56, return address,
[rsp+0x50] (the art routine's own return address when the pair-art clone is the callee),
rsi). The pair-art hook skips the per-frame art-builder calls (return 0x14026BC8E).

    python tools/probes/lw365_clone_call_log.py install   # plant the five hooks (game running)
    python tools/probes/lw365_clone_call_log.py read      # dump the ring, oldest first
    python tools/probes/lw365_clone_call_log.py clear
    python tools/probes/lw365_clone_call_log.py restore   # put the 7 original bytes back

Clone entries (2026-08-31 build): rangeIdx 0x114B10000 (for 0x1402B8BCC), pairArt 0x114B70000
(0x1402B8E60), validity 0x114B00000 (0x1402B8EE8), artSib 0x114B60000 (0x1402B8E04),
weaponStat 0x114AE0000 (0x1402B8C74); all start with `89 c8 25 ff 03 00 00`. Re-read them
with the mod's boot log if the NearAllocator lands elsewhere. Restore before deploying.
"""
import ctypes as C
import ctypes.wintypes as W
import json
import os
import struct
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from lw346_live_disasm import find_pid, k32, rd as _rd  # noqa: E402

_H = None


def _handle():
    global _H
    if _H is None:
        _H = k32.OpenProcess(0x0438, False, find_pid("fft_enhanced.exe"))
    return _H


def rd(a, n):
    return _rd(_handle(), a, n)


k32.VirtualAllocEx.restype = C.c_void_p
k32.VirtualAllocEx.argtypes = [W.HANDLE, C.c_void_p, C.c_size_t, W.DWORD, W.DWORD]
k32.VirtualProtectEx.argtypes = [W.HANDLE, C.c_void_p, C.c_size_t, W.DWORD, C.POINTER(W.DWORD)]

CLONES = {1: 0x114B10000, 2: 0x114B70000, 3: 0x114B00000, 4: 0x114B60000, 5: 0x114AE0000}
NAMES = {1: "rangeIdx", 2: "pairArt", 3: "validity", 4: "artSib", 5: "weaponStat", 6: "setup@b27", 7: "setup@entry",
         8: "der<-cx@b75", 9: "der<-si@e56", 10: "der<-0@d1a", 11: "der<-0@d95", 12: "der<-0@f6e", 13: "prep:valid", 14: "prep:list"}
PRO = bytes.fromhex("89c825ff030000")
STATE = os.path.join(os.environ.get("TEMP", "."), "lw365_clone_call_log_state.json")
RING_BYTES = 0x1010
def wpm(a, b):
    buf = (C.c_ubyte * len(b))(*b); g = C.c_size_t(0)
    return bool(k32.WriteProcessMemory(_handle(), C.c_void_p(a), buf, len(b), C.byref(g))) and g.value == len(b)
cmd = sys.argv[1]
if cmd == "install":
    for c in CLONES.values():
        assert rd(c, 7) == PRO, f"clone {c:#x} prologue differs: {rd(c,7).hex()}"
    page = None
    for hint in list(range(0x114AD0000, 0x110000000, -0x10000)) + list(range(0x116000000, 0x120000000, 0x10000)):
        page = k32.VirtualAllocEx(_handle(), C.c_void_p(hint), 0x2000, 0x3000, 0x40)
        if page: break
    assert page, "alloc failed"
    RING = page + 0x400
    assert wpm(RING, b"\0" * RING_BYTES)
    stub_at = page
    for tag, c in CLONES.items():
        s = bytearray()
        s += b"\x50\x53"                                       # push rax; push rbx
        skip_fix = None
        if tag == 2:
            s += b"\x48\xB8" + struct.pack("<Q", 0x14026BC8E)  # mov rax, art-builder return address
            s += b"\x48\x39\x44\x24\x10"                       # cmp [rsp+0x10], rax
            s += b"\x0F\x84\x00\x00\x00\x00"                   # je SKIP (rel32 patched below)
            skip_fix = len(s)
        s += b"\x48\xBB" + struct.pack("<Q", RING)
        s += b"\x8B\x03\xC1\xE0\x05\x48\x01\xC3"
        s += b"\x48\x89\x4B\x10"                               # q0 = rcx
        s += b"\xC6\x43\x17" + bytes([tag])
        s += b"\x48\x8B\x44\x24\x10\x48\x89\x43\x18"           # q1 = ret
        s += b"\x48\x8B\x44\x24\x50\x48\x89\x43\x20"           # q2 = [rsp+0x50]
        s += b"\x48\x89\x73\x28"                               # q3 = rsi
        s += b"\x48\xBB" + struct.pack("<Q", RING)
        s += b"\x8B\x03\xFF\xC0\x25\x7F\x00\x00\x00\x89\x03"
        if skip_fix is not None:
            struct.pack_into("<i", s, skip_fix - 4, len(s) - skip_fix)
        s += b"\x5B\x58"
        s += PRO
        s += b"\xE9" + struct.pack("<i", (c + 7) - (stub_at + len(s) + 5))
        assert len(s) <= 0x80, len(s)
        assert wpm(stub_at, bytes(s)), "stub write failed"
        jmp = b"\xE9" + struct.pack("<i", stub_at - (c + 5))
        assert wpm(c, jmp) and rd(c, 5) == jmp, f"patch {c:#x} failed"
        print(f"hooked {NAMES[tag]:10} clone {c:#x} -> stub {stub_at:#x}")
        stub_at += 0x80
    json.dump({"page": page, "ring": RING}, open(STATE, "w"))
    print(f"stub page {page:#x}, ring {RING:#x}")
elif cmd == "read":
    RING = json.load(open(STATE))["ring"]
    b = rd(RING, RING_BYTES); idx = struct.unpack_from("<I", b, 0)[0]
    print(f"ring idx={idx}")
    for i in range(128):
        j = (idx + i) % 128
        v, q1, q2, q3 = struct.unpack_from("<QQQQ", b, 0x10 + j*32)
        if v == 0 and q1 == 0: continue
        tag = (v >> 56) & 0xFF; rcx = v & 0x00FFFFFFFFFFFFFF
        print(f"  [{j:3}] {NAMES.get(tag, str(tag)):12} q0={rcx:#x} q1={q1:#x} q2={q2:#x} q3={q3:#x}")
elif cmd == "clear":
    RING = json.load(open(STATE))["ring"]; print("cleared" if wpm(RING, b"\0" * RING_BYTES) else "clear failed")
elif cmd == "restore":
    for c in CLONES.values():
        ok = wpm(c, PRO) and rd(c, 7) == PRO; print(f"restored {c:#x}: {ok}")
