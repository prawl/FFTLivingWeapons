"""Read-only dump of the pre-battle SOURCE record(s) the engine hands to
CopyUnitToBattleUnit (arg2 = pPartyUnit).

This is the c0b step of the enemy-roster repaint spike: confirm that enemy units
flow through battle-setup with their OWN writable source record, and map that
record's layout. Pure RPM -- no writes, no hooks, crash-safe.

Workflow:
  1. Enable Dicene's `fftivc.handsfree` mod, enter a battle. Its hook logs a line
     per constructed unit:  [fftivc.handsfree] CopyUnitToBattleUnit, ... PartyUnit: 0x<ADDR>, PartyUnitFlags: 0x<F>
  2. Copy every PartyUnit address from that battle.
  3. Run:  python tools\\probes\\roster_read.py 0x<ADDR1> 0x<ADDR2> ...
     (addresses accepted with or without 0x, comma/space separated.)

It dumps 0x60 raw bytes per record + a best-guess field decode (by analogy to the
player world-roster 0x258 layout -- the source record's REAL layout is what we are
confirming, so trust the raw bytes over the guessed fields until the offsets are
verified against an on-screen unit).

Anchor fact (from Dicene's decompile, Mod.cs:64,71): the agency byte on the SOURCE
record is +0x04, bit 0x08 (SET=human / CLEAR=AI). If +0x04 reads a sane small value
you are almost certainly looking at the right struct.
"""

import ctypes
import ctypes.wintypes as w
import struct
import sys

PROC = "fft_enhanced.exe"
DUMP_LEN = 0x60

# --- player world-roster field offsets (0x258 stride). GUESS for the source record. ---
FIELDS = [
    (0x04, 1, "agency?      (Dicene src +0x04, bit0x08=human)"),
    (0x0A, 1, "support?"),
    (0x12, 1, "accessory?"),
    (0x14, 2, "rHand?"),
    (0x16, 2, "lHand?"),
    (0x18, 2, "offHand?"),
    (0x1A, 2, "shield?"),
    (0x1D, 1, "level?"),
    (0x1E, 1, "brave?"),
    (0x1F, 1, "faith?"),
]

# ---------------------------------------------------------------------------
k32 = ctypes.WinDLL("kernel32", use_last_error=True)
psapi = ctypes.WinDLL("psapi", use_last_error=True)
PROCESS_QUERY_INFORMATION = 0x0400
PROCESS_VM_READ = 0x0010
_HANDLE = None


def _open_process():
    arr = (w.DWORD * 4096)()
    needed = w.DWORD()
    if not psapi.EnumProcesses(arr, ctypes.sizeof(arr), ctypes.byref(needed)):
        return None
    for i in range(needed.value // ctypes.sizeof(w.DWORD)):
        h = k32.OpenProcess(PROCESS_QUERY_INFORMATION | PROCESS_VM_READ, False, arr[i])
        if not h:
            continue
        buf = ctypes.create_unicode_buffer(260)
        if psapi.GetModuleBaseNameW(h, None, buf, 260) and buf.value.lower() == PROC.lower():
            return h
        k32.CloseHandle(h)
    return None


def _handle():
    global _HANDLE
    if _HANDLE is None:
        _HANDLE = _open_process()
    return _HANDLE


def rpm(addr: int, n: int):
    h = _handle()
    if not h:
        return None
    buf = ctypes.create_string_buffer(n)
    got = ctypes.c_size_t()
    ok = k32.ReadProcessMemory(h, ctypes.c_void_p(addr), buf, n, ctypes.byref(got))
    return buf.raw if ok and got.value == n else None


def _parse_addrs(argv):
    out = []
    for tok in " ".join(argv).replace(",", " ").split():
        try:
            out.append(int(tok, 16) if tok.lower().startswith("0x") else int(tok, 16))
        except ValueError:
            print(f"skip unparseable token: {tok}")
    return out


def _hexdump(base: int, data: bytes):
    for off in range(0, len(data), 16):
        chunk = data[off:off + 16]
        hexs = " ".join(f"{b:02X}" for b in chunk)
        asci = "".join(chr(b) if 32 <= b < 127 else "." for b in chunk)
        print(f"  +0x{off:02X}  {hexs:<47}  {asci}")


def _decode(data: bytes):
    print("  guessed fields (player-roster analogy -- VERIFY against on-screen unit):")
    for off, width, label in FIELDS:
        if off + width > len(data):
            continue
        if width == 1:
            v = data[off]
        else:
            v = struct.unpack_from("<H", data, off)[0]
        flag = ""
        if off == 0x04:
            flag = "  -> HUMAN (bit0x08 set)" if (v & 0x08) else "  -> AI (bit0x08 clear)"
        print(f"    +0x{off:02X} {label:<48} = 0x{v:0{width*2}X} ({v}){flag}")


def main():
    addrs = _parse_addrs(sys.argv[1:])
    if not addrs:
        print(__doc__)
        print("usage: python tools\\probes\\roster_read.py 0x<ADDR> [0x<ADDR> ...]")
        sys.exit(1)
    if not _handle():
        print(f"process not found ({PROC} not running)")
        sys.exit(1)
    print(f"reading {len(addrs)} source record(s), {DUMP_LEN} bytes each\n")
    for a in addrs:
        data = rpm(a, DUMP_LEN)
        print(f"=== pPartyUnit 0x{a:X} ===")
        if data is None:
            print("  UNREADABLE (not committed / wrong address)\n")
            continue
        _hexdump(a, data)
        _decode(data)
        print()


if __name__ == "__main__":
    main()
