#!/usr/bin/env python
"""LW-427 Phase 0 leg 2: hide one icon's texture record plus ONLY the chosen managers' parts copies.

THIS PROBE WRITES TO THE RUNNING GAME in apply mode. The list mode opens the process read only.

WHY
  Leg 1 (lw427_icon_cache_watch.py, tapes 2026-10-18 02:23 and 02:31) showed the icon caches
  never drop an entry, the battle status page brings no cache of its own, and the inventory's
  manager is the one that grows when the inventory opens. LW-381 hid every copy of an icon
  and LW-423 saw the battle status page draw blank. This leg asks the deciding question. If
  the shared texture record is hidden together with only the inventory manager's parts copy,
  does the inventory show the new art while every other screen keeps the old art?

WHAT IT WRITES
  The proven LW-381 hide recipe, unchanged: a texture record gets name pointer +1 and the hash
  dword at +0x30 zeroed (lw381_tex_cache_rename.py), a parts entry gets name pointer +1 and the
  id at +8 zeroed (lw381_parts_cache_rename.py). Both fields are re read right before the write
  and read back after it. Parts entries outside the chosen managers are never touched.

THERE IS NO RESTORE
  The adversarial review of 2026-10-18 showed the game frees hidden records, so writing old
  values back can land in freed memory. To undo, quit the game. The files on disk are correct,
  so the next launch draws every icon fresh.

USAGE (game running)
    python tools/probes/lw427_scoped_hide.py list 33
    python tools/probes/lw427_scoped_hide.py apply 33 --mgr 0x233A3094500
  --mgr takes one or more comma separated manager addresses from the list output.
"""
import ctypes
import struct
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
from lw381_icon_memscan import find_pid, k32, read  # noqa: E402
from lw381_parts_cache_probe import MEM_PRIVATE, q, sweep_many  # noqa: E402
from lw381_parts_cache_rename import entries_for  # noqa: E402
from lw381_tex_cache_rename import records_for  # noqa: E402
from lw427_icon_cache_watch import MAX_VECTOR_BYTES, manager_pass  # noqa: E402
sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from lib import offsets as _offsets  # noqa: E402
from lib.livebase import open_game, rebase  # noqa: E402

SCHEME = b"UITextureParts://"
TEX_SIZES = {"card": 0xC860, "small": 0x3060}
(BATTLE_MODE,) = _offsets.require(["BattleMode"], _offsets.load())


def looks_like_entry(h, p):
    if p is None or p & 7:
        return False
    return (read(h, q(h, p) or 0, len(SCHEME)) or b"") == SCHEME


def is_manager(h, m):
    """A real manager's vector is mostly named entries and its capacity at +0xF0 is not below
    its end; a stack copy of a (begin, end) pair fails one or the other."""
    b, e, cap = q(h, m + 0xE0), q(h, m + 0xE8), q(h, m + 0xF0)
    if None in (b, e, cap) or e <= b or cap < e or e - b > MAX_VECTOR_BYTES:
        return False
    raw = read(h, b, min(e - b, 64 * 8)) or b""
    ptrs = struct.unpack(f"<{len(raw) // 8}Q", raw[:len(raw) // 8 * 8])
    named = sum(looks_like_entry(h, p) for p in ptrs)
    return bool(ptrs) and named * 5 >= len(ptrs) * 4


def managers_of(h, entries):
    """{entry: [(manager, begin, end), ...]} from the slots that point at each entry and the
    managers whose live vector brackets those slots, the same bracket test leg 1 uses."""
    slots = sweep_many(h, [struct.pack("<Q", e) for e, *_ in entries], MEM_PRIVATE)
    slot_entry = {s: struct.unpack("<Q", n)[0] for n, hs in slots.items() for s in hs if not s & 7}
    # Leg 1 drops a candidate whose neighbour 8 bytes down also brackets a slot, which can drop a real one.
    cands = manager_pass(h, slot_entry.keys())
    cands |= {m + 8 for m in cands}
    out, explained = {}, set()
    for m in sorted(cands):
        if not is_manager(h, m):
            continue
        b, e = q(h, m + 0xE0), q(h, m + 0xE8)
        for s, ent in slot_entry.items():
            if b <= s < e and (s - b) % 8 == 0:
                explained.add(s)
                if all(o[0] != m for o in out.get(ent, [])):
                    out.setdefault(ent, []).append((m, b, e))
    loose = {}
    for s, ent in slot_entry.items():
        if s not in explained:
            loose.setdefault(ent, []).append(s)
    return out, loose


def surface_of(text):
    return "small" if "/equip_item_s/" in text else "card"


def in_battle():
    """True unless BattleMode reads a map value (0, or 255 before the first battle)."""
    _pid, rh, base = open_game()
    try:
        mode = read(rh, rebase(BATTLE_MODE, base), 1)
    finally:
        k32.CloseHandle(rh)
    return not mode or mode[0] not in (0, 255)


def main():
    argv = sys.argv[1:]
    if len(argv) < 2 or argv[0] not in ("list", "apply"):
        print(__doc__); sys.exit(2)
    mode, icon = argv[0], (int(argv[1]) if argv[1].isdigit() else int(argv[1], 0))
    chosen = set()
    if "--mgr" in argv:
        i = argv.index("--mgr")
        if i + 1 >= len(argv):
            print("--mgr needs a value."); sys.exit(2)
        chosen = {int(x.strip(), 16) for x in argv[i + 1].split(",") if x.strip()}
    if mode == "apply" and not chosen:
        print("apply needs --mgr with at least one manager from the list output."); sys.exit(2)

    pid = find_pid()
    if pid is None:
        print("fft_enhanced.exe is not running."); sys.exit(1)
    h = k32.OpenProcess(0x0438 if mode == "apply" else 0x0410, False, pid)
    if not h:
        print(f"could not open pid {pid}."); sys.exit(1)

    texs = records_for(h, icon)
    parts = entries_for(h, icon)
    owners, loose = managers_of(h, parts)
    print(f"icon {icon}: {len(texs)} texture record(s), {len(parts)} loaded parts entr(ies)")
    for p, _s, hsh, surface, path in texs:
        print(f"  texture 0x{p:X}  hash 0x{hsh:08X}  {surface:<5} {path}")
    for e, _s, ident, uri in parts:
        own = owners.get(e, [])
        where = ", ".join(f"0x{m:X} ({(en - b) // 8} entries)" for m, b, en in own) or "no manager found"
        flag = "  SHARED" if len(own) > 1 else ""
        print(f"  parts   0x{e:X}  id {ident}  {surface_of(uri):<5} {uri.rsplit('/', 1)[-1]}  in {where}{flag}")
        if loose.get(e):
            print(f"          also pointed at from {len(loose[e])} slot(s) no manager explains, so it is never hidden")
    if mode == "list":
        k32.CloseHandle(h); return

    if in_battle():
        print("refusing to write during a battle; run it on the world map."); k32.CloseHandle(h); return
    # Every owner must be chosen, or a copy another screen draws from would be hidden too.
    targets = {"card": [], "small": []}
    for e, s, ident, uri in parts:
        own = {m for m, _b, _e in owners.get(e, [])}
        if own and own <= chosen and not loose.get(e):
            targets[surface_of(uri)].append((e, s, ident))
    if not any(targets.values()):
        print("no loaded parts entry sits only in the chosen managers; nothing written.")
        k32.CloseHandle(h); return
    print("writing; leave the game idle until this finishes.")
    done = 0
    for surface in ("card", "small"):
        if not targets[surface]:
            continue
        # Re check the parts first, so a texture is never hidden with no parts entry to follow it.
        targets[surface] = [(e, s, ident) for e, s, ident in targets[surface]
                            if q(h, e) == s and q(h, e + 8) == ident and read(h, e + 0x58, 2) == b""]
        if not targets[surface]:
            print(f"  every {surface} parts target changed since scan; {surface} skipped.")
            continue
        hid = 0
        for p, s, hsh, surf, _ in texs:
            if surf != surface:
                continue
            if (q(h, p) != s or read(h, p + 0x30, 4) != struct.pack("<I", hsh)
                    or q(h, p + 0x28) != TEX_SIZES[surface]):
                print(f"  skipped texture 0x{p:X}: changed since scan"); continue
            n, m = ctypes.c_size_t(0), ctypes.c_size_t(0)
            a = k32.WriteProcessMemory(h, ctypes.c_void_p(p), struct.pack("<Q", s + 1), 8, ctypes.byref(n))
            b = k32.WriteProcessMemory(h, ctypes.c_void_p(p + 0x30), struct.pack("<I", 0), 4, ctypes.byref(m))
            good = a and b and q(h, p) == s + 1 and read(h, p + 0x30, 4) == b"\0\0\0\0"
            print(f"  {'hid' if good else 'FAILED'} texture 0x{p:X} ({surface})")
            hid += 1 if good else 0
        if hid == 0:
            # A parts hide with the texture still cached reloads the old art and proves nothing.
            print(f"  no {surface} texture record hid, so no {surface} parts entry was touched.")
            continue
        done += hid
        for e, s, ident in targets[surface]:
            if q(h, e) != s or q(h, e + 8) != ident or read(h, e + 0x58, 2) != b"\x01\x01":
                print(f"  skipped parts 0x{e:X}: changed since scan"); continue
            data = struct.pack("<QQ", s + 1, 0)
            n = ctypes.c_size_t(0)
            ok = k32.WriteProcessMemory(h, ctypes.c_void_p(e), data, 16, ctypes.byref(n))
            good = ok and read(h, e, 16) == data
            print(f"  {'hid' if good else 'FAILED'} parts 0x{e:X} ({surface})")
            done += 1 if good else 0
    print(f"{done} write(s) landed. To undo, quit the game.")
    k32.CloseHandle(h)


if __name__ == "__main__":
    main()
