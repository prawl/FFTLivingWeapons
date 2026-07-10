"""LW-51 per-playthrough recon: read the live event/scenario state from fft_enhanced.exe.

READ-ONLY. Reads a handful of static main-module addresses (no ASLR on this build) and labels
them, so a new-game start can be identified by its event id. The target is Offsets.EventId
(0x140782A94, u16) -- the event file number during cutscenes/dialogue; it ALIASES as the active
unit's nameId during battle, so it is only meaningful when BattleMode == 0 (out of battle). The
opening (Orbonne prologue) has its own distinct event id; FFTHandsFree's pre-1.5 catalog put the
Orbonne pre-battle dialogue at event 302, but 1.5 numbering needs this live confirm.

  python tools/probes/read_state.py            # one-shot labeled read
  python tools/probes/read_state.py watch       # poll ~3/sec, print on any change (catch the opening)

EventId = 0x140782A94 is 1.5-PREDICTED (+0x6000); its 8-byte neighbor Acted = 0x140782A8C is
1.5-CONFIRMED at the same shift, so the region is anchored. The window dump around EventId lets a
slightly-off prediction still be spotted (look for a plausible 1..399 value that changes at the
opening).
"""
import ctypes
import ctypes.wintypes as wt
import struct
import sys
import time

PROCESS_VM_READ = 0x0010
PROCESS_QUERY_INFORMATION = 0x0400

EVENT_ID = 0x140782A94     # u16, the target (1.5 predicted +0x6000, was 0x14077CA94)
ACTED = 0x140782A8C        # u8, 1.5 CONFIRMED anchor 8 bytes below EventId
BATTLE_MODE = 0x1409069A0  # u8, 0 = out of battle (EventId valid), 3 = in battle (EventId aliases)
MAP_ID = 0x140784478       # u8, current battle map id (corroborator)

k32 = ctypes.windll.kernel32
psapi = ctypes.windll.psapi
k32.OpenProcess.restype = wt.HANDLE


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
    return buf.raw[:got.value] if ok and got.value == n else None


def u8(h, a):
    b = rpm(h, a, 1)
    return b[0] if b else None


def u16(h, a):
    b = rpm(h, a, 2)
    return struct.unpack("<H", b)[0] if b else None


def event_gloss(v):
    if v is None:
        return "unreadable"
    if v == 0xFFFF:
        return "0xFFFF = unset (no event active)"
    if 1 <= v <= 399:
        return f"{v} = REAL story event (cutscene/dialogue active)"
    return f"{v} = 400+ (aliased active-unit nameId, not a real event; are we in battle?)"


def snapshot(h):
    ev = u16(h, EVENT_ID)
    bm = u8(h, BATTLE_MODE)
    mp = u8(h, MAP_ID)
    ac = u8(h, ACTED)
    return ev, bm, mp, ac


def render(h):
    ev, bm, mp, ac = snapshot(h)
    lines = [
        f"  EventId  (0x{EVENT_ID:X}): {event_gloss(ev)}",
        f"  BattleMode (0x{BATTLE_MODE:X}): {bm}   ({'OUT of battle -- EventId is valid' if bm == 0 else 'in battle -- EventId aliases as nameId'})",
        f"  MapId    (0x{MAP_ID:X}): {mp}",
        f"  Acted    (0x{ACTED:X}): {ac}   (confirmed-anchor sanity)",
    ]
    win = rpm(h, EVENT_ID - 0x10, 0x24)
    if win:
        vals = [struct.unpack_from("<H", win, i)[0] for i in range(0, len(win) - 1, 2)]
        tagged = []
        for i, v in enumerate(vals):
            a = EVENT_ID - 0x10 + i * 2
            mark = " <== EventId" if a == EVENT_ID else ""
            tagged.append(f"    {a:X}: {v:5d} (0x{v:04X}){mark}")
        lines.append("  window (u16 around EventId, look for a plausible 1..399 that changes at the opening):")
        lines.extend(tagged)
    return "\n".join(lines), (ev, bm, mp, ac)


def main():
    watch = len(sys.argv) > 1 and sys.argv[1] == "watch"
    pid, h = find_pid()
    if not h:
        print("game not running (fft_enhanced.exe not found)")
        return 1
    print(f"pid {pid}")
    try:
        if not watch:
            text, _ = render(h)
            print(text)
            return 0
        print("watching (Ctrl+C to stop); prints on change...")
        last = None
        while True:
            text, key = render(h)
            if key != last:
                print("\n" + time.strftime("%H:%M:%S"))
                print(text)
                last = key
            time.sleep(0.3)
    except KeyboardInterrupt:
        pass
    finally:
        k32.CloseHandle(h)
    return 0


if __name__ == "__main__":
    sys.exit(main())
