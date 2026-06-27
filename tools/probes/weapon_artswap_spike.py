#!/usr/bin/env python
"""
SPIKE: does a borrowed battle weapon MODEL survive a post-construction roster restore?

THE QUESTION (two docs dated 2026-06-26 conflict):
  - LIVE_LEDGER row 69 / ITEM_CAP_261_BREAK_JOURNEY:910-920 -- the battlefield swing model
    BAKES AT CONSTRUCTION from the equip-slot id; find-what-accesses caught NOTHING reading
    CWeapon per-frame. => present a real-model id at build time, the unit swings it; you can
    then restore the real id and the model PERSISTS.
  - WEAPON_VISUALS_SCOPING.md #4 -- the model FOLLOWS live CWeapon (+0x20), and the engine
    re-asserts CWeapon from the roster EVERY TICK; CWeapon binds model AND stats together
    (Warbrand 304 -> 121 when CWeapon held at Cleaver). => restore the real id and the model
    REVERTS; there is no render-only lever.

Only ONE can be true. It decides whether a living weapon can wear a borrowed model (id37 art)
while keeping its own identity (id261 growth/stats/name): a CLEAN construction-window swap is
viable IFF the baked model persists after we restore the roster.

THE TEST (vanilla ids only -- no cap-break rig, no id261, fully reverts on game restart):
  1. dump          -- find the test unit's slot + current main-hand weapon id (roster +0x14, u16).
  2. swap S 37     -- world map: set roster slot S main-hand -> 37 (Chaos Blade, proven-art).
  3. (enter a battle) -- construction bakes the model from 37. Observe: Chaos Blade swing + its
                         damage number (Patrick's eyes -- both model AND stats should be 37's).
  4. restore S <orig> -- IN BATTLE, post-construction: set main-hand back to the real id.
                         Wait ~1s (the engine re-asserts CWeapon from roster every tick).
                         OBSERVE THE DECISIVE OUTCOME:
                           * swing STAYS Chaos Blade + damage FLIPS back to real -> MODEL BAKED
                             ONCE, STATS LIVE -> CLEAN SOLUTION EXISTS -> GO (build the swap loop).
                           * swing REVERTS to the real weapon  -> MODEL IS LIVE-READ -> model and
                             identity are inseparable -> NO-GO -> fall back to art-deferred.

The id261 case inherits this answer: if a vanilla baked model survives the restore, id261's
borrowed-37 model will too (same construction read, same roster lever).

Roster blueprint: base 0x1411A7D10, stride 0x258. Main-hand equip id = +0x14 (u16),
off-hand = +0x18 (u16). (Ramza slot 0 main-hand = 0x1411A7D24, matches the cap-break ledger.)

USAGE (game booted, party on the world map for swap; in a battle to observe):
  python tools\\probes\\weapon_artswap_spike.py                 # dump roster: slot / job / main / off / nick
  python tools\\probes\\weapon_artswap_spike.py swap 0 37       # slot 0 main-hand -> 37 (Chaos Blade)
  python tools\\probes\\weapon_artswap_spike.py restore 0 19    # slot 0 main-hand -> 19 (revert; print orig on swap)
  python tools\\probes\\weapon_artswap_spike.py read 0          # re-read slot 0 main/off hand ids
  python tools\\probes\\weapon_artswap_spike.py watchw 0xADDR   # poll a u16 (e.g. a CWeapon addr from CE) for 30s

Nothing here touches id>260 or the cap-break hooks. All writes revert on game restart.
"""
import ctypes as C
from ctypes import wintypes as W
import sys
import time

PROC = "fft_enhanced"
BASE = 0x1411A7D10
STRIDE = 0x258
N = 32
OFF_JOB = 0x02
OFF_MAIN = 0x14     # main-hand equip item id (u16)
OFF_OFF = 0x18      # off-hand equip item id (u16)
OFF_LEVEL = 0x1D
OFF_NICK = 0xDC
NICK_LEN = 16

k32 = C.WinDLL("kernel32", use_last_error=True)
psapi = C.WinDLL("psapi", use_last_error=True)
ACCESS = 0x0438  # QUERY_INFO | VM_OPERATION | VM_READ | VM_WRITE


class _PMC(C.Structure):
    _fields_ = [("cb", W.DWORD), ("PageFaultCount", W.DWORD),
                ("PeakWorkingSetSize", C.c_size_t), ("WorkingSetSize", C.c_size_t),
                ("QuotaPeakPagedPoolUsage", C.c_size_t), ("QuotaPagedPoolUsage", C.c_size_t),
                ("QuotaPeakNonPagedPoolUsage", C.c_size_t), ("QuotaNonPagedPoolUsage", C.c_size_t),
                ("PagefileUsage", C.c_size_t), ("PeakPagefileUsage", C.c_size_t)]


def _ws(pid):
    h = k32.OpenProcess(0x0410, False, pid)   # QUERY_INFO | VM_READ
    if not h:
        return 0
    p = _PMC(); p.cb = C.sizeof(p)
    ok = psapi.GetProcessMemoryInfo(h, C.byref(p), p.cb)
    k32.CloseHandle(h)
    return p.WorkingSetSize if ok else 0


def find_pid(name):
    """Largest-working-set match -- avoids the first-match 2-process trap (roster_sprite.py bug)."""
    arr = (W.DWORD * 4096)()
    need = W.DWORD()
    if not psapi.EnumProcesses(arr, C.sizeof(arr), C.byref(need)):
        return None
    best, best_ws = None, -1
    for i in range(need.value // C.sizeof(W.DWORD)):
        pid = arr[i]
        h = k32.OpenProcess(0x0410, False, pid)
        if not h:
            continue
        buf = C.create_unicode_buffer(260)
        if psapi.GetModuleBaseNameW(h, None, buf, 260) and buf.value.lower() == (name.lower() + ".exe"):
            ws = _ws(pid)
            if ws > best_ws:
                best, best_ws = pid, ws
        k32.CloseHandle(h)
    return best


_H = None


def rpm(addr, n):
    buf = C.create_string_buffer(n)
    got = C.c_size_t()
    ok = k32.ReadProcessMemory(_H, C.c_void_p(addr), buf, n, C.byref(got))
    return buf.raw if ok and got.value == n else None


def wpm(addr, data):
    got = C.c_size_t()
    ok = k32.WriteProcessMemory(_H, C.c_void_p(addr), data, len(data), C.byref(got))
    return bool(ok) and got.value == len(data)


def u16(d, off):
    return d[off] | (d[off + 1] << 8)


def _nick(d):
    raw = d[OFF_NICK:OFF_NICK + NICK_LEN]
    for enc in ("utf-16-le", "latin-1"):
        try:
            s = raw.decode(enc, "ignore").split("\x00")[0].strip()
            if s and all(32 <= ord(c) < 0x3000 for c in s):
                return s
        except Exception:
            pass
    return "".join(chr(b) if 32 <= b < 127 else "." for b in raw).strip(".")


def dump():
    print(f"roster @ 0x{BASE:X} stride 0x{STRIDE:X}   (main-hand +0x14 / off-hand +0x18, u16)\n")
    hdr = f"{'slot':>4} {'addr':>11} {'job':>4} {'main':>6} {'off':>6} {'lvl':>3}  nick"
    print(hdr); print("-" * len(hdr))
    found = 0
    for i in range(N):
        a = BASE + i * STRIDE
        d = rpm(a, STRIDE)
        if d is None:
            continue
        lvl, job = d[OFF_LEVEL], d[OFF_JOB]
        if not (1 <= lvl <= 99) or job == 0:
            continue
        found += 1
        print(f"{i:>4} 0x{a:09X} 0x{job:02X} {u16(d, OFF_MAIN):>6} {u16(d, OFF_OFF):>6} {lvl:>3}  {_nick(d)}")
    print(f"\n{found} populated slot(s).")
    if not found:
        print("None populated -- be on the world map with a party loaded.")


def read_slot(slot):
    a = BASE + slot * STRIDE
    d = rpm(a, STRIDE)
    if d is None:
        print(f"slot {slot} @ 0x{a:X} unreadable"); return
    print(f"slot {slot} @ 0x{a:X}  main(+0x14)={u16(d, OFF_MAIN)}  off(+0x18)={u16(d, OFF_OFF)}  nick={_nick(d)}")


def set_main(slot, item_id):
    a = BASE + slot * STRIDE + OFF_MAIN
    cur = rpm(a, 2)
    if cur is None:
        print(f"slot {slot} main-hand @ 0x{a:X} unreadable"); return
    old = cur[0] | (cur[1] << 8)
    if wpm(a, bytes([item_id & 0xFF, (item_id >> 8) & 0xFF])):
        print(f"slot {slot} main-hand @ 0x{a:X}: {old} -> {item_id}   (REVERT: restore {slot} {old})")
        print("  Now enter a battle (swap) or watch the swing/damage (restore).")
    else:
        print(f"WRITE FAILED @ 0x{a:X}")


COMBAT_LO = 0x141850000
COMBAT_HI = 0x141863000   # CombatAnchor arena; Ramza CWeapon ~0x141855D00 per LIVE_LEDGER row 68


def findcw(value):
    """Scan the in-battle combat arena for u16==value (e.g. 37 = Ramza's live CWeapon).
    Reads the whole region once, reports candidate +0x20-style fields with a little context."""
    span = COMBAT_HI - COMBAT_LO
    blob = rpm(COMBAT_LO, span)
    if blob is None:
        print(f"combat arena 0x{COMBAT_LO:X}..0x{COMBAT_HI:X} unreadable (are you IN a battle?)"); return
    lo = bytes([value & 0xFF, (value >> 8) & 0xFF])
    hits = []
    off = blob.find(lo)
    while off != -1 and len(hits) < 60:
        hits.append(COMBAT_LO + off)
        off = blob.find(lo, off + 1)
    print(f"u16=={value} : {len(hits)} hit(s) in 0x{COMBAT_LO:X}..0x{COMBAT_HI:X}")
    for a in hits:
        # show the byte just before (often HP/level neighbours) for a human to spot the real struct
        ctx = rpm(a - 0x20, 0x24)
        tag = ""
        if ctx:
            # heuristic: a real combat struct tends to have a plausible level (1..99) somewhere nearby
            pass
        print(f"  0x{a:X}")


def getcw(addr):
    d = rpm(addr, 2)
    print(f"0x{addr:X} = {(d[0] | (d[1] << 8)) if d else '??'}")


def setcw(addr, value):
    cur = rpm(addr, 2)
    old = (cur[0] | (cur[1] << 8)) if cur else None
    if wpm(addr, bytes([value & 0xFF, (value >> 8) & 0xFF])):
        print(f"0x{addr:X}: {old} -> {value}   (REVERT: setcw 0x{addr:X} {old})")
    else:
        print(f"WRITE FAILED @ 0x{addr:X}")


def disasm(addr, count):
    """Disassemble `count` instructions forward from addr (x86-64). Reads code bytes
    via RPM (EXE image pages are readable) and decodes with capstone. Forward-only:
    addr must be a real instruction boundary (e.g. an opcode from CE find-what-accesses)."""
    try:
        from capstone import Cs, CS_ARCH_X86, CS_MODE_64
    except ImportError:
        print("capstone not installed (pip install capstone)"); return
    n = max(16, count * 15)
    blob = rpm(addr, n)
    if blob is None:
        print(f"0x{addr:X} unreadable"); return
    md = Cs(CS_ARCH_X86, CS_MODE_64)
    md.detail = False
    shown = 0
    for ins in md.disasm(blob, addr):
        print(f"  0x{ins.address:X}  {ins.mnemonic:<7} {ins.op_str}")
        shown += 1
        if shown >= count:
            break


def _ru(addr, size):
    d = rpm(addr, size)
    return int.from_bytes(d, "little") if d else None


def vistable(probe_ids):
    """Walk the weapon-visual container located 2026-06-27 and report its bounds + the
    record pointer for each probe id. SAFE (read-only). The resolver 0x1401edc50 loads
    its container from the global singleton at (0x1401EDC61 + 0x3AEC687), then
    0x1403d3b7c does: if (low <= id <= high) return array[(id-low)] else null, with
    low=[c+0x44], high=[c+0x48], array=[c+0x18], type=[c+0x38] (1 = bounded-array branch)."""
    g1ptr = 0x1401EDC61 + 0x3AEC687          # rip-relative target of the singleton load
    g1 = _ru(g1ptr, 8)
    print(f"singleton ptr @ 0x{g1ptr:X} -> container G1 = 0x{g1:X}" if g1 else
          f"singleton @ 0x{g1ptr:X} unreadable/null")
    if not g1:
        return
    typ  = _ru(g1 + 0x38, 4)
    low  = _ru(g1 + 0x44, 4)
    high = _ru(g1 + 0x48, 4)
    arr  = _ru(g1 + 0x18, 8)
    flag41 = _ru(g1 + 0x41, 1)
    map28  = _ru(g1 + 0x28, 8)   # map branch: bucket/storage ptr
    map10  = _ru(g1 + 0x10, 8)   # map branch: other ptr (rcx for the hashed lookup)
    print(f"  type(+0x38)={typ}  flag(+0x41)={flag41}  low(+0x44)={low}  high(+0x48)={high}")
    print(f"  array(+0x18)=0x{arr or 0:X}  map28(+0x28)=0x{map28 or 0:X}  map10(+0x10)=0x{map10 or 0:X}")
    for iid in probe_ids:
        if arr:                                  # flat-array branch
            if low is not None and low <= iid <= high:
                rec = _ru(arr + (iid - low) * 8, 8)
                print(f"  id {iid:>4}: array[{iid-low}] = 0x{rec or 0:X}" + ("  (record)" if rec else "  (NULL)"))
            else:
                print(f"  id {iid:>4}: OUT OF BOUNDS [{low},{high}] -> NULL -> empty model")
        else:
            inb = (low is not None and low <= iid <= high)
            print(f"  id {iid:>4}: array NULL -> HASH-MAP branch; bound-range [{low},{high}] {'includes' if inb else 'EXCLUDES'} it")


def watchw(addr):
    print(f"watching u16 @ 0x{addr:X} for 30s (every 0.5s) ...")
    last = None
    for _ in range(60):
        d = rpm(addr, 2)
        v = (d[0] | (d[1] << 8)) if d else None
        if v != last:
            print(f"  0x{addr:X} = {v}")
            last = v
        time.sleep(0.5)


def main():
    global _H
    pid = find_pid(PROC)
    if not pid:
        print(f"{PROC}.exe not running"); sys.exit(1)
    _H = k32.OpenProcess(ACCESS, False, pid)
    if not _H:
        print(f"OpenProcess failed (err {C.get_last_error()})"); sys.exit(1)
    print(f"pid {pid} (largest working set)\n")
    argv = sys.argv[1:]
    if argv and argv[0] == "swap":
        set_main(int(argv[1]), int(argv[2]))
    elif argv and argv[0] == "restore":
        set_main(int(argv[1]), int(argv[2]))
    elif argv and argv[0] == "read":
        read_slot(int(argv[1]))
    elif argv and argv[0] == "disasm":
        disasm(int(argv[1], 16), int(argv[2]))
    elif argv and argv[0] == "vistable":
        ids = [int(x) for x in argv[1:]] or [37, 67, 260, 261]
        vistable(ids)
    elif argv and argv[0] == "findcw":
        findcw(int(argv[1]))
    elif argv and argv[0] == "getcw":
        getcw(int(argv[1], 16))
    elif argv and argv[0] == "setcw":
        setcw(int(argv[1], 16), int(argv[2]))
    elif argv and argv[0] == "watchw":
        watchw(int(argv[1], 16))
    else:
        dump()


if __name__ == "__main__":
    main()
