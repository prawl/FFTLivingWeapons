"""Read / swap the SpriteSet byte on the PERSISTENT player roster (pre-battle blueprint).

This is the PROVEN sprite-swap lever: the battle sprite is built from the roster
blueprint at construction (battle entry / Organize-menu refresh), so writing the
roster SpriteSet byte BEFORE the body is built re-skins the unit -- unlike the
combat-struct +0x00 write, which is label-only (welded at construction).
See docs/LIVE_LEDGER.md (Uncertain: "Live combat-struct job/identity write is
LABEL-ONLY") and memory ic-job-id-remap.

WARNING: this is the SAVE's roster. The write is in-memory only (resets on game
restart) UNLESS you save the game after, which persists the cosmetic change.
`set` captures + prints the original bytes so you can revert; or just restart.

Roster blueprint: base 0x1411A7D10, stride 0x258 (Dicene uniteditor UnitData).
Fields used here:  SpriteSet +0x00 (u8) | UnitIndex +0x01 | Job +0x02 (u8) |
Palette +0x03 | Level +0x1D | Brave +0x1E | Faith +0x1F | Nickname +0xDC (16B).

Usage:
    python tools\\probes\\roster_sprite.py                 # dump populated slots
    python tools\\probes\\roster_sprite.py set 3 0x1E      # slot 3 SpriteSet -> Agrias
    python tools\\probes\\roster_sprite.py set 3 0x1E 0x00 # also force Palette 0
Then trigger a rebuild: leave/re-enter the Organize screen, or start a battle.
"""

import ctypes
import ctypes.wintypes as w
import struct
import sys

PROC = "fft_enhanced.exe"
BASE = 0x1411A7D10
STRIDE = 0x258
N = 32          # scan generously; populated slots are filtered by sane level/job
OFF_SPRITE = 0x00
OFF_UNITIDX = 0x01
OFF_JOB = 0x02
OFF_PALETTE = 0x03
OFF_LEVEL = 0x1D
OFF_BRAVE = 0x1E
OFF_FAITH = 0x1F
OFF_NICK = 0xDC
OFF_FLAGS = 0x04
OFF_VOICE = 0x230   # Dicene "VoiceID" -- really the special-character/name id
NICK_LEN = 16
DUMP_LEN = 0x60
FULL_LEN = 0x258

# Named SpriteSetID values worth swapping TO (subset of Dicene Constants.cs).
SPRITE_NAMES = {
    0x01: "Ramza_CH1", 0x02: "Ramza_CH2_3", 0x03: "Ramza_CH4",
    0x04: "Delita_CH1", 0x05: "Delita_CH2_3", 0x06: "Delita_CH4",
    0x07: "Argath", 0x08: "Zalbaag", 0x09: "Dycedarg", 0x0A: "Larg",
    0x0B: "Goltanna", 0x0C: "Ovelia", 0x0D: "Orlandeau", 0x0E: "Funebris",
    0x0F: "Reis_Human", 0x10: "Zalmour", 0x11: "Gaffgarion", 0x12: "Marach",
    0x13: "Simon", 0x14: "Alma", 0x15: "Orran", 0x16: "Mustadio",
    0x18: "Delacroix", 0x19: "Rapha", 0x1B: "Elmdore", 0x1C: "Tietra",
    0x1D: "Barrington", 0x1E: "Agrias", 0x1F: "Beowulf", 0x20: "Wiegraf_CH1",
    0x21: "Valmafra", 0x23: "Ludovich", 0x24: "Folmarv", 0x25: "Loffrey",
    0x26: "Isilud", 0x27: "Cletienne", 0x28: "Wiegraf_CH3", 0x2A: "Meliadoul",
    0x2B: "Barich", 0x2D: "Celia", 0x2E: "Lettie", 0x31: "Ajora",
    0x32: "Cloud", 0x33: "Zalbaag_possessed",
    0x80: "GenericMale", 0x81: "GenericFemale", 0x82: "GenericMonster",
    0xA2: "Balthier", 0xA3: "Luso", 0xA5: "Argath_Deathknight",
    0xA6: "Aliste", 0xA7: "Bremondt", 0xA8: "Bremondt_DarkDragon",
}

k32 = ctypes.WinDLL("kernel32", use_last_error=True)
psapi = ctypes.WinDLL("psapi", use_last_error=True)
ACCESS = 0x0438  # QUERY_INFO | VM_OPERATION | VM_READ | VM_WRITE
_H = None


def _open():
    arr = (w.DWORD * 4096)()
    need = w.DWORD()
    if not psapi.EnumProcesses(arr, ctypes.sizeof(arr), ctypes.byref(need)):
        return None
    for i in range(need.value // ctypes.sizeof(w.DWORD)):
        h = k32.OpenProcess(ACCESS, False, arr[i])
        if not h:
            continue
        buf = ctypes.create_unicode_buffer(260)
        if psapi.GetModuleBaseNameW(h, None, buf, 260) and buf.value.lower() == PROC.lower():
            return h
        k32.CloseHandle(h)
    return None


def rpm(addr, n):
    buf = ctypes.create_string_buffer(n)
    got = ctypes.c_size_t()
    ok = k32.ReadProcessMemory(_H, ctypes.c_void_p(addr), buf, n, ctypes.byref(got))
    return buf.raw if ok and got.value == n else None


def wpm(addr, data):
    got = ctypes.c_size_t()
    ok = k32.WriteProcessMemory(_H, ctypes.c_void_p(addr), data, len(data), ctypes.byref(got))
    return bool(ok) and got.value == len(data)


def sname(v):
    return SPRITE_NAMES.get(v, f"0x{v:02X}")


def _nick(data):
    raw = data[OFF_NICK:OFF_NICK + NICK_LEN]
    # try utf-16-le first (remaster), fall back to ascii
    for enc in ("utf-16-le", "latin-1"):
        try:
            s = raw.decode(enc, "ignore").split("\x00")[0].strip()
            if s and all(32 <= ord(c) < 0x3000 for c in s):
                return s
        except Exception:
            pass
    return "".join(chr(b) if 32 <= b < 127 else "." for b in raw).strip(".")


def dump():
    print(f"roster @ 0x{BASE:X} stride 0x{STRIDE:X}\n")
    hdr = f"{'slot':>4} {'addr':>11} {'sprite':>5} {'spriteName':<18} {'job':>4} {'pal':>3} {'lvl':>3} {'br':>3} {'fa':>3}  nick"
    print(hdr)
    print("-" * len(hdr))
    found = 0
    for i in range(N):
        a = BASE + i * STRIDE
        d = rpm(a, DUMP_LEN)
        if d is None:
            continue
        lvl, job = d[OFF_LEVEL], d[OFF_JOB]
        sprite, pal = d[OFF_SPRITE], d[OFF_PALETTE]
        br, fa = d[OFF_BRAVE], d[OFF_FAITH]
        if not (1 <= lvl <= 99) or job == 0:
            continue
        found += 1
        print(f"{i:>4} 0x{a:09X} 0x{sprite:02X} {sname(sprite):<18} 0x{job:02X} "
              f"{pal:>3} {lvl:>3} {br:>3} {fa:>3}  {_nick(d)}")
    print(f"\n{found} populated slot(s).")
    if not found:
        print("None populated -- are you on the world map with a party loaded?")


def ids():
    """Dump identity-candidate fields per slot: UnitIndex, Flags, SpriteSet, Job,
    VoiceID (the special-name id), nickname. Reveals unique vs generic patterns."""
    print(f"roster @ 0x{BASE:X}  identity fields\n")
    hdr = f"{'slot':>4} {'uIdx':>4} {'flags':>5} {'sprite':>6} {'spriteName':<16} {'job':>4} {'voiceID':>9}  nick"
    print(hdr)
    print("-" * len(hdr))
    for i in range(N):
        a = BASE + i * STRIDE
        d = rpm(a, FULL_LEN)
        if d is None:
            continue
        lvl, job = d[OFF_LEVEL], d[OFF_JOB]
        if not (1 <= lvl <= 99) or job == 0:
            continue
        uidx, flags, sprite = d[OFF_UNITIDX], d[OFF_FLAGS], d[OFF_SPRITE]
        voice = struct.unpack_from("<I", d, OFF_VOICE)[0]
        print(f"{i:>4} 0x{uidx:02X} 0x{flags:02X}  0x{sprite:02X} {sname(sprite):<16} "
              f"0x{job:02X} 0x{voice:07X}  {_nick(d)}")


def setfield(slot, off, value, width):
    a = BASE + slot * STRIDE
    d = rpm(a, FULL_LEN)
    if d is None:
        print(f"slot {slot} @ 0x{a:X} unreadable")
        return
    old = int.from_bytes(d[off:off + width], "little")
    data = value.to_bytes(width, "little")
    print(f"slot {slot} @ 0x{a:X}  +0x{off:02X}: 0x{old:0{width*2}X} -> 0x{value:0{width*2}X}")
    if wpm(a + off, data):
        print(f"  REVERT: python tools\\probes\\roster_sprite.py setraw {slot} 0x{off:02X} 0x{old:X} {width}")
    else:
        print("  WRITE FAILED")


def setsprite(slot, sprite, palette=None):
    a = BASE + slot * STRIDE
    d = rpm(a, DUMP_LEN)
    if d is None:
        print(f"slot {slot} @ 0x{a:X} unreadable")
        return
    old_s, old_p, job = d[OFF_SPRITE], d[OFF_PALETTE], d[OFF_JOB]
    print(f"slot {slot} @ 0x{a:X}  nick={_nick(d)}  job=0x{job:02X}")
    print(f"  SpriteSet  0x{old_s:02X} ({sname(old_s)})  ->  0x{sprite:02X} ({sname(sprite)})")
    if not wpm(a + OFF_SPRITE, bytes([sprite])):
        print("  WRITE FAILED (SpriteSet)")
        return
    if palette is not None:
        print(f"  Palette    0x{old_p:02X}  ->  0x{palette:02X}")
        if not wpm(a + OFF_PALETTE, bytes([palette])):
            print("  WRITE FAILED (Palette)")
    print(f"  REVERT: python tools\\probes\\roster_sprite.py set {slot} 0x{old_s:02X} 0x{old_p:02X}")
    print("  Now leave/re-enter Organize or start a battle to rebuild the unit.")


def main():
    global _H
    _H = _open()
    if not _H:
        print(f"{PROC} not running")
        sys.exit(1)
    argv = sys.argv[1:]
    if argv and argv[0] == "set":
        slot = int(argv[1])
        sprite = int(argv[2], 16)
        pal = int(argv[3], 16) if len(argv) > 3 else None
        setsprite(slot, sprite, pal)
    elif argv and argv[0] == "setraw":   # setraw <slot> <off> <value> [width]
        setfield(int(argv[1]), int(argv[2], 16), int(argv[3], 16),
                 int(argv[4]) if len(argv) > 4 else 1)
    elif argv and argv[0] == "ids":
        ids()
    else:
        dump()


if __name__ == "__main__":
    main()
