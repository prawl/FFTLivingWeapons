"""Provoke mark probe: can the mark be taken back OFF an enemy?

WHY THIS EXISTS
---------------
The Provoke mark is engine-applied and, per the handoff, NEVER expires and cannot be re-applied
while present: a recast on an already-provoked unit honestly reads 0%. The owner's design call
(2026-07-22) is that the runtime CLEARS the mark when the hold releases, which is what makes
Provoke usable more than once on the same enemy in a battle.

Nothing has ever cleared this mark. That is a new mechanism, not a reuse of a proven one, so it
gets verified live before a line of C# is written (the LW-56 rule: a logic-verify is blind to a
wrong premise, and that one burned four cycles).

THE THREE-LAYER MODEL this tests (status_map.py header; LIVE_LEDGER LW-58 row, Uncertain):
    innate     band +0x3B..+0x3F   job/equipment derived; never touched
    inflicted  band +0x1D3..+0x1D7 the persistent layer the engine ORs accepted bits into
    composed   band +0x45..+0x49   the displayed layer, re-derived from inflicted OR innate
The stated posture is "write BOTH layers"; a composed-only write is the orphan-flag mistake. This
probe deliberately lets you clear EITHER layer alone, because which one actually releases the
engine's "already has it" refusal is the entire question and guessing costs a cycle.

THE MARK is status id 0 -- band +0x45 bit 0x80, inflicted +0x1D3 bit 0x80 (bit math from
status_map.py: byte = id >> 3, mask = 0x80 >> (id & 7)). Id 0 is the ONE blank slot in the whole
40-status decode table, which is why it was free to hijack and why tools/probes/status_probe.py's
map has no entry for it.

THE HAZARD THAT DECIDES THE WRITE SHAPE: band +0x45 is a SHARED byte. It carries Dead (0x20),
Undead (0x10), Charging (0x08), Jump (0x04), Defending (0x02) and Performing (0x01), and
KillTracker's death detection reads it. Every write here is a mask-scoped read-modify-write. A
whole-byte write would corrupt death detection, which is why the shipped runtime has MemBits.

ADDRESSING: rebuilt on the 1.5.1 constants straight out of LivingWeapon/Offsets.cs, NOT copied
from the older probes. tools/probes/status_probe.py still anchors on the pre-1.5 CombatAnchor
0x14184F890 and therefore finds nothing on this build -- that is LW-93, and this file is one more
instrument on the working base rather than another casualty of it.

SAFETY
------
`find` and `watch` are read-only. `clear` snapshots every byte it will touch BEFORE the first
write, restores on exit and on Ctrl+C, and re-reads after every write so an engine that puts the
bit straight back is reported rather than assumed. It refuses any seat that does not look like a
live unit, and it never writes a whole byte.

Verbs
-----
  python tools\\probes\\provoke_mark_probe.py find [--id N]
      List live band seats with both status layers decoded, flagging whoever wears the mark.
      Read-only. Run it before and after a cast.

  python tools\\probes\\provoke_mark_probe.py watch [seconds] [--id N]
      Print a line whenever any unit's composed or inflicted status bytes change. Cast Provoke
      while this runs to see exactly which layers the engine lights up, in which order.

  python tools\\probes\\provoke_mark_probe.py clear <slot> [--layer composed|inflicted|both] [--id N]
      Mask-scoped clear of one status bit on one seat, then watch for 3 seconds to report whether
      the engine restores it. --layer both is the posture the status map recommends; the single
      -layer runs are the experiment that says whether it is actually required.

  python tools\\probes\\provoke_mark_probe.py --selftest
      Offline checks (no game needed): bit math, addressing, and the shared-byte mask discipline.
"""
import ctypes
import ctypes.wintypes as w
import json
import sys
import tempfile
import time
from pathlib import Path

PROC_NAME = "fft_enhanced.exe"
PV = 0x0010 | 0x0020 | 0x0008 | 0x0400   # VM_READ | VM_WRITE | VM_OPERATION | QUERY_INFORMATION

# --- band addressing, from LivingWeapon/Offsets.cs (1.5.1) ---------------------------------------
# CombatAnchor 0x141855CE0 is the "1.5 CONFIRMED +0x6450" re-anchor (Offsets.cs:157); the value in
# status_probe.py (0x14184F890) is the PRE-1.5 address and finds nothing on this build.
COMBAT_ANCHOR = 0x141855CE0
COMBAT_STRIDE = 0x200
BAND_ENTRY = 0x1C
# Offsets.BandReadBase: the band scan starts at n=-24, the lowest valid index.
BAND_READ_BASE = COMBAT_ANCHOR + BAND_ENTRY - 24 * COMBAT_STRIDE
BAND_SLOTS = 49

COMPOSED_BASE = 0x45     # band-relative, 5 bytes (status_map.py COMPOSED_BASE)
INFLICTED_BASE = 0x1D3   # band-relative, 5 bytes (status_map.py INFLICTED_BASE)
LAYER_BYTES = 5

MARK_ID = 0              # the Provoke mark: the one unnamed id in the 40-status table

# Band-relative fingerprint fields, mirroring the sane-value filter the working probes use.
A_LEVEL, A_BRAVE, A_FAITH = 0x0D, 0x0E, 0x10
A_HP, A_MAXHP = 0x14, 0x16
A_GX, A_GY = 0x33, 0x34

# The +0x45 byte is SHARED. Printed by `clear` before it writes, so the operator sees what else
# lives in the byte being modified. Straight from tools/probes/status_probe.py's STATUS map.
SHARED_45 = [(0x40, "Crystal"), (0x20, "Dead"), (0x10, "Undead"), (0x08, "Charging"),
             (0x04, "Jump"), (0x02, "Defending"), (0x01, "Performing")]

SNAPSHOT = Path(tempfile.gettempdir()) / "lw_provoke_mark_probe.json"

k32 = ctypes.windll.kernel32


# --------------------------------------------------------------------------- pure math
def status_byte_index(status_id):
    """Which of the 5 layer bytes holds this status. status_map.py: byte = id >> 3."""
    return status_id >> 3


def status_mask(status_id):
    """Which bit within that byte. status_map.py: mask = 0x80 >> (id & 7)."""
    return 0x80 >> (status_id & 7)


def composed_addr(entry, status_id):
    return entry + COMPOSED_BASE + status_byte_index(status_id)


def inflicted_addr(entry, status_id):
    return entry + INFLICTED_BASE + status_byte_index(status_id)


def entry_addr(slot):
    return BAND_READ_BASE + slot * COMBAT_STRIDE


def clear_bit(current, mask):
    """Mask-scoped clear. Kept as a named pure function so the selftest can prove the shared byte
    survives: clearing the mark must not disturb Dead/Undead/Charging/Jump."""
    return current & ~mask & 0xFF


def looks_live(b):
    """The sane-value fingerprint the working 1.5.1 probes use, instead of a slot marker (the
    slot-marker filters are what broke the ct_probe family on this build -- LW-93).

    TIGHTENED after its first live run: the stat-range test ALONE admitted slot 43, which read
    hp 437 of maxHp 96 at tile (255,129) with both status layers full of high-entropy bytes, and
    the probe duly reported it as wearing the mark. Two cheap cross-field consistency tests kill
    it: hp cannot exceed maxHp, and tile coordinates are small on every map in this game. This is
    the same lesson LW-124 files against the runtime's own band walks -- a filter that only asks
    "are these values sane one at a time" counts furniture as party members."""
    mhp = b[A_MAXHP] | (b[A_MAXHP + 1] << 8)
    hp = b[A_HP] | (b[A_HP + 1] << 8)
    return (1 <= mhp < 2000 and hp <= mhp
            and 1 <= b[A_LEVEL] <= 99
            and 1 <= b[A_BRAVE] <= 100 and 1 <= b[A_FAITH] <= 100
            and b[A_GX] < 64 and b[A_GY] < 64)


def decode_shared(byte_val):
    hits = [n for m, n in SHARED_45 if byte_val & m]
    return ", ".join(hits) if hits else "nothing else"


# --------------------------------------------------------------------------- process access
class PE32(ctypes.Structure):
    _fields_ = [("dwSize", w.DWORD), ("cntUsage", w.DWORD), ("th32ProcessID", w.DWORD),
                ("th32DefaultHeapID", ctypes.POINTER(ctypes.c_ulong)), ("th32ModuleID", w.DWORD),
                ("cntThreads", w.DWORD), ("th32ParentProcessID", w.DWORD),
                ("pcPriClassBase", ctypes.c_long), ("dwFlags", w.DWORD),
                ("szExeFile", ctypes.c_char * 260)]


def find_pid():
    snap = k32.CreateToolhelp32Snapshot(2, 0)
    if snap == -1:
        return None
    e = PE32()
    e.dwSize = ctypes.sizeof(PE32)
    hit = None
    ok = k32.Process32First(snap, ctypes.byref(e))
    while ok:
        if e.szExeFile.decode(errors="ignore").lower() == PROC_NAME.lower():
            hit = e.th32ProcessID
            break
        ok = k32.Process32Next(snap, ctypes.byref(e))
    k32.CloseHandle(snap)
    return hit


class Mem:
    def __init__(self, pid):
        self.h = k32.OpenProcess(PV, False, pid)
        if not self.h:
            raise SystemExit(f"could not open pid {pid} (error {k32.GetLastError()})")

    def read(self, addr, size):
        buf = (ctypes.c_ubyte * size)()
        got = ctypes.c_size_t(0)
        ok = k32.ReadProcessMemory(self.h, ctypes.c_void_p(addr), ctypes.byref(buf),
                                   ctypes.c_size_t(size), ctypes.byref(got))
        return bytes(buf) if ok and got.value == size else None

    def write(self, addr, data):
        buf = (ctypes.c_ubyte * len(data))(*data)
        put = ctypes.c_size_t(0)
        ok = bool(k32.WriteProcessMemory(self.h, ctypes.c_void_p(addr), ctypes.byref(buf),
                                         ctypes.c_size_t(len(data)), ctypes.byref(put)))
        return ok and put.value == len(data) and self.read(addr, len(data)) == data

    def close(self):
        if self.h:
            k32.CloseHandle(self.h)
            self.h = None


def live_seats(mem):
    """Yield (slot, entry_addr, header_bytes) for every seat passing the fingerprint."""
    for s in range(BAND_SLOTS):
        e = entry_addr(s)
        b = mem.read(e, 0x60)
        if b and looks_live(b):
            yield s, e, b


def layers(mem, entry):
    comp = mem.read(entry + COMPOSED_BASE, LAYER_BYTES)
    infl = mem.read(entry + INFLICTED_BASE, LAYER_BYTES)
    return comp, infl


def wears(layer_bytes, status_id):
    if layer_bytes is None:
        return False
    return bool(layer_bytes[status_byte_index(status_id)] & status_mask(status_id))


# --------------------------------------------------------------------------- verbs
def verb_find(mem, status_id):
    print(f"band base 0x{BAND_READ_BASE:X}, {BAND_SLOTS} seats; "
          f"mark = status id {status_id} "
          f"(composed +0x{COMPOSED_BASE + status_byte_index(status_id):02X} "
          f"mask 0x{status_mask(status_id):02X})")
    print(f"{'slot':>4} {'hp':>9} {'lvl':>3} {'pos':>8}  {'composed':<16} {'inflicted':<16} mark")
    found = 0
    marked = []
    for s, e, b in live_seats(mem):
        comp, infl = layers(mem, e)
        hp = b[A_HP] | (b[A_HP + 1] << 8)
        mhp = b[A_MAXHP] | (b[A_MAXHP + 1] << 8)
        c = comp.hex(" ") if comp else "unreadable"
        i = infl.hex(" ") if infl else "unreadable"
        flag = ""
        if wears(comp, status_id) or wears(infl, status_id):
            bits = []
            if wears(comp, status_id):
                bits.append("composed")
            if wears(infl, status_id):
                bits.append("inflicted")
            flag = "MARKED (" + "+".join(bits) + ")"
            marked.append(s)
        print(f"{s:4d} {hp:4d}/{mhp:<4d} {b[A_LEVEL]:3d} ({b[A_GX]:2d},{b[A_GY]:2d})  "
              f"{c:<16} {i:<16} {flag}")
        found += 1
    if found == 0:
        print("(no live seats -- is a battle running?)")
    elif not marked:
        print(f"\nnobody wears status id {status_id}. Cast Provoke, then run `find` again.")
    else:
        print(f"\nmarked seats: {marked}. `clear <slot>` takes the mark off; "
              f"try --layer composed and --layer inflicted separately to learn which one matters.")
    return 0


def verb_watch(mem, seconds, status_id):
    print(f"watching both status layers for {seconds}s; only CHANGES print. Cast Provoke now.")
    prev = {}
    t_end = time.time() + seconds
    t0 = time.time()
    while time.time() < t_end:
        for s, e, _ in live_seats(mem):
            comp, infl = layers(mem, e)
            if comp is None or infl is None:
                continue
            key = (comp, infl)
            if prev.get(s) != key:
                if s in prev:
                    oc, oi = prev[s]
                    tags = []
                    if oc != comp:
                        tags.append(f"composed {oc.hex(' ')} -> {comp.hex(' ')}")
                    if oi != infl:
                        tags.append(f"inflicted {oi.hex(' ')} -> {infl.hex(' ')}")
                    mark = " <== MARK" if (wears(comp, status_id) and not wears(oc, status_id)) else ""
                    print(f"[{time.time() - t0:7.3f}s] slot {s:2d}  " + "; ".join(tags) + mark)
                prev[s] = key
        time.sleep(0.033)
    print("done.")
    return 0


def verb_clear(mem, slot, layer, status_id):
    e = entry_addr(slot)
    b = mem.read(e, 0x60)
    if b is None or not looks_live(b):
        print(f"slot {slot} does not hold a live unit -- refusing to write. Run `find` first.")
        return 1

    targets = []
    if layer in ("composed", "both"):
        targets.append(("composed", composed_addr(e, status_id)))
    if layer in ("inflicted", "both"):
        targets.append(("inflicted", inflicted_addr(e, status_id)))

    mask = status_mask(status_id)
    originals = []
    for name, addr in targets:
        cur = mem.read(addr, 1)
        if cur is None:
            print(f"{name} byte at 0x{addr:X} unreadable -- refusing to write anything")
            return 1
        originals.append((name, addr, cur[0]))

    SNAPSHOT.write_text(json.dumps([{"label": n, "addr": a, "byte": v}
                                    for n, a, v in originals], indent=1), encoding="utf-8")
    print(f"snapshot -> {SNAPSHOT}")
    print(f"slot {slot}: clearing status id {status_id} (mask 0x{mask:02X}) from {layer}")
    for name, addr, cur in originals:
        print(f"  {name} 0x{addr:X}: 0x{cur:02X} -> 0x{clear_bit(cur, mask):02X}"
              f"   (bit currently {'SET' if cur & mask else 'already clear'})")
        if name == "composed" and status_byte_index(status_id) == 0:
            print(f"    shared byte also carries: {decode_shared(clear_bit(cur, mask))}"
                  f"  -- mask-scoped write, those survive")

    try:
        for name, addr, cur in originals:
            ok = mem.write(addr, bytes([clear_bit(cur, mask)]))
            print(f"  write {name}: {'OK' if ok else 'FAILED'}")
        print("\nwatching 3s for the engine to put it back...")
        t0 = time.time()
        restored = {n: False for n, _, _ in originals}
        while time.time() - t0 < 3.0:
            for name, addr, _ in originals:
                cur = mem.read(addr, 1)
                if cur and (cur[0] & mask) and not restored[name]:
                    restored[name] = True
                    print(f"  [{time.time() - t0:5.3f}s] ENGINE RESTORED the {name} bit")
            time.sleep(0.033)
        for name in restored:
            if not restored[name]:
                print(f"  {name}: stayed clear for 3s")
        print("\nNow look at the game: is the status gone from the target's list, and does a "
              "recast of Provoke land at 100% instead of 0%? That is the actual result.")
        print("Press Ctrl+C to restore the original bytes, or leave it cleared and recast.")
        while True:
            time.sleep(0.2)
    except KeyboardInterrupt:
        print("\nrestoring:")
        for name, addr, cur in originals:
            ok = mem.write(addr, bytes([cur]))
            print(f"  {name} 0x{addr:X} -> 0x{cur:02X}: {'OK' if ok else 'FAILED'}")
        SNAPSHOT.unlink(missing_ok=True)
    return 0


# --------------------------------------------------------------------------- selftest
def selftest():
    fails = []

    def check(label, got, want):
        if got != want:
            fails.append(f"{label}: got {got!r}, want {want!r}")

    # Bit math against status_map.py's stated rule, anchored on ids whose bytes are known.
    check("id 0 byte", status_byte_index(0), 0)
    check("id 0 mask", status_mask(0), 0x80)
    check("id 7 byte", status_byte_index(7), 0)
    check("id 7 mask", status_mask(7), 0x01)
    check("id 8 byte", status_byte_index(8), 1)
    check("id 8 mask", status_mask(8), 0x80)
    # Poison is id 24 -> band +0x48 bit 0x80 (Proven row); Doom is id 39 -> +0x49 bit 0x01.
    check("poison byte", COMPOSED_BASE + status_byte_index(24), 0x48)
    check("poison mask", status_mask(24), 0x80)
    check("doom byte", COMPOSED_BASE + status_byte_index(39), 0x49)
    check("doom mask", status_mask(39), 0x01)
    # Dead is id 2 -> +0x45 bit 0x20, which pins the whole byte-0 layout.
    check("dead byte", COMPOSED_BASE + status_byte_index(2), 0x45)
    check("dead mask", status_mask(2), 0x20)

    # The mark's two addresses, band-relative.
    check("mark composed offset", COMPOSED_BASE + status_byte_index(MARK_ID), 0x45)
    check("mark inflicted offset", INFLICTED_BASE + status_byte_index(MARK_ID), 0x1D3)

    # Band addressing must reproduce Offsets.BandReadBase exactly.
    check("band read base", BAND_READ_BASE, 0x141855CE0 + 0x1C - 24 * 0x200)
    check("seat 24 is the anchor's own slot", entry_addr(24), 0x141855CE0 + 0x1C)

    # THE SHARED-BYTE DISCIPLINE: clearing the mark must leave every neighbour intact.
    # 0xBC = mark(0x80) + Crystal(0x40) ... use a realistic mix instead: Dead+Undead+mark.
    mixed = 0x80 | 0x20 | 0x10 | 0x08     # mark + Dead + Undead + Charging
    check("clearing the mark keeps Dead/Undead/Charging", clear_bit(mixed, 0x80), 0x38)
    check("clearing an already-clear bit is a no-op", clear_bit(0x38, 0x80), 0x38)
    check("clear never sets a bit", clear_bit(0x00, 0x80), 0x00)

    if fails:
        print("SELFTEST FAILED")
        for f in fails:
            print("  " + f)
        return 1
    print("selftest OK")
    return 0


def main():
    args = sys.argv[1:]
    if not args or args[0] in ("-h", "--help"):
        print(__doc__)
        return 0
    if args[0] == "--selftest":
        return selftest()

    layer = "both"
    status_id = MARK_ID
    positional = []
    i = 0
    while i < len(args):
        if args[i] == "--layer":
            layer = args[i + 1]
            i += 2
        elif args[i] == "--id":
            status_id = int(args[i + 1], 0)
            i += 2
        else:
            positional.append(args[i])
            i += 1
    if layer not in ("composed", "inflicted", "both"):
        print(f"--layer must be composed, inflicted or both (got {layer!r})")
        return 2

    pid = find_pid()
    if pid is None:
        print(f"{PROC_NAME} is not running")
        return 2
    mem = Mem(pid)
    try:
        verb = positional[0]
        if verb == "find":
            return verb_find(mem, status_id)
        if verb == "watch":
            secs = float(positional[1]) if len(positional) > 1 else 30.0
            return verb_watch(mem, secs, status_id)
        if verb == "clear":
            if len(positional) < 2:
                print("usage: clear <slot> [--layer composed|inflicted|both] [--id N]")
                return 2
            return verb_clear(mem, int(positional[1], 0), layer, status_id)
        print(f"unknown verb {verb!r}")
        print(__doc__)
        return 2
    finally:
        mem.close()


if __name__ == "__main__":
    sys.exit(main())
