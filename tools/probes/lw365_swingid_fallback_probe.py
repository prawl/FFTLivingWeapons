"""LW-365 cure probe (WRITES one 7-byte jump into game code at 0x140282073 plus a stub in the
page allocated by lw365_clone_call_log.py; run that probe's install first).

Plain language: when a unit swings at an empty tile the game copies one standing "weapon id"
word into the swing's animation object; for a moved design that word is 0 at swing time, so
the art lookup draws bare hands. This stub reads that word and, when it is 0, substitutes the
acting unit's own right-hand id (units base 0x141853CE0 + 0x20 + rsi, rsi = acting slot << 9
in that frame). PROVEN live 2026-08-31 late: the Ravager (id 262) drew its own blade at an
empty tile; vanilla units never take the substitution (their word is nonzero).

    python tools/probes/lw365_swingid_fallback_probe.py install
    python tools/probes/lw365_swingid_fallback_probe.py restore
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

SITE = 0x140282073; ORIG = bytes.fromhex("0fb70500e75200")
st = json.load(open(os.path.join(os.environ.get("TEMP", "."), "lw365_clone_call_log_state.json")))
PAGE, RING = st["page"], st["ring"]; STUB = PAGE + 0x1810
def wpm(a, b):
    buf = (C.c_ubyte * len(b))(*b); g = C.c_size_t(0)
    return bool(k32.WriteProcessMemory(_handle(), C.c_void_p(a), buf, len(b), C.byref(g))) and g.value == len(b)
def wcode(a, b):
    old = W.DWORD(0)
    assert k32.VirtualProtectEx(_handle(), C.c_void_p(a & ~0xFFF), 0x1000, 0x40, C.byref(old))
    ok = wpm(a, b); k32.VirtualProtectEx(_handle(), C.c_void_p(a & ~0xFFF), 0x1000, old.value, C.byref(old)); return ok
if sys.argv[1] == "install":
    assert rd(SITE, 7) == ORIG, rd(SITE, 7).hex()
    s = bytearray()
    s += bytes.fromhex("53")                                              # push rbx
    s += bytes.fromhex("48b8") + struct.pack("<Q", 0x1407B077A) + bytes.fromhex("0fb700")   # eax = word [standing id]
    s += bytes.fromhex("6685c0")                                          # test ax, ax
    s += bytes.fromhex("750e")                                            # jnz +14 -> pop rbx
    s += bytes.fromhex("48b8") + struct.pack("<Q", 0x141853CE0 + 0x20)    # rax = units base + 0x20
    s += bytes.fromhex("0fb70430")                                        # eax = word [rax + rsi]
    s += bytes.fromhex("5b")                                              # pop rbx
    s += bytes.fromhex("e9") + struct.pack("<i", (SITE + 7) - (STUB + len(s) + 5))
    assert wpm(STUB, bytes(s))
    jmp = bytes.fromhex("e9") + struct.pack("<i", STUB - (SITE + 5)) + bytes.fromhex("9090")
    assert wcode(SITE, jmp) and rd(SITE, 7) == jmp
    print(f"fallback hook at {SITE:#x} -> {STUB:#x} ({len(s)} bytes)")
elif sys.argv[1] == "restore":
    print("restored:", wcode(SITE, ORIG) and rd(SITE, 7) == ORIG)
