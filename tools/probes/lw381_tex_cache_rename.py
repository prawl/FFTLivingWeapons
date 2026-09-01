"""LW-381 premise probe, layer two: hide one icon's TEXTURE cache record so the next load re-reads.

WRITES TO THE RUNNING GAME in --apply mode; dry run opens the process read only.

WHY A SECOND LAYER
  Hiding the UITextureParts entries (lw381_parts_cache_rename.py) makes the drawn icon vanish
  at once and come back on the next screen open, but it comes back with the OLD art even when
  modded.pac holds new bytes. So the parts reload found its texture without touching the file:
  a texture-level cache answered by name. This probe hides that record too.

THE RECORD (observed live 2026-09-01, both surfaces of icon 22)
  A heap object in MEM_PRIVATE whose first qword points at the asset path string
  "ui/ffto/icon/<surface>/texture/<name>.tex", with the texture's byte size at +0x28
  (0xC860 card / 0x3060 small, the .tex file size exactly) and the low 32 bits of the pac
  table's 64-bit path hash at +0x30. Hiding = name pointer +1 (a valid string that matches
  nothing) and hash dword -> 0, both recorded first for --restore.

USAGE
    python tools/probes/lw381_tex_cache_rename.py 22            # dry run
    python tools/probes/lw381_tex_cache_rename.py 22 --apply
    python tools/probes/lw381_tex_cache_rename.py --restore
"""
import ctypes
import json
import struct
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
from lw381_icon_memscan import find_pid, k32, read  # noqa: E402
from lw381_parts_cache_probe import sweep_many, cstr, q, MEM_PRIVATE  # noqa: E402

UNDO = Path(__file__).resolve().parent / "lw381_tex_cache_rename_undo.json"
SIZES = {"card": 0xC860, "small": 0x3060}


def records_for(h, icon):
    surfaces = (("card", "equip_item", f"ei_{icon:03d}_uitx"), ("small", "equip_item_s", f"ei_s_{icon:03d}_uitx"))
    paths = {f"ui/ffto/icon/{sub}/texture/{stem}.tex".encode() + b"\0": surface for surface, sub, stem in surfaces}
    hits = sweep_many(h, paths.keys(), MEM_PRIVATE)          # pass 1: the path strings
    starts = {}
    for path, addrs in hits.items():
        for s in addrs:
            if read(h, s - 1, 1) == b"\0":
                starts[struct.pack("<Q", s)] = (s, paths[path], path[:-1].decode())
    ptrs = sweep_many(h, starts.keys(), MEM_PRIVATE)         # pass 2: every pointer to any of them
    out = []
    for needle, holders in ptrs.items():
        s, surface, path = starts[needle]
        for p in holders:
            if p & 7:
                continue
            size = q(h, p + 0x28)
            hsh = read(h, p + 0x30, 4)
            if size != SIZES[surface] or not hsh or hsh == b"\0\0\0\0":
                continue
            out.append((p, s, struct.unpack("<I", hsh)[0], surface, path))
    return out


def main():
    argv = sys.argv[1:]
    apply = "--apply" in argv
    do_restore = "--restore" in argv
    ids = [int(a, 0) for a in argv if not a.startswith("--")]
    pid = find_pid()
    if pid is None:
        print("fft_enhanced.exe is not running."); sys.exit(1)
    h = k32.OpenProcess(0x0438 if (apply or do_restore) else 0x0410, False, pid)
    if not h:
        print(f"could not open pid {pid}."); sys.exit(1)
    print(f"game pid {pid}")

    if do_restore:
        if not UNDO.exists():
            print("no undo file."); return
        rec = json.loads(UNDO.read_text()); ok = 0
        for w in rec["writes"]:
            p = int(w["record"], 16)
            n = ctypes.c_size_t(0)
            a = k32.WriteProcessMemory(h, ctypes.c_void_p(p), struct.pack("<Q", int(w["name"], 16)), 8, ctypes.byref(n))
            m = ctypes.c_size_t(0)
            b = k32.WriteProcessMemory(h, ctypes.c_void_p(p + 0x30), struct.pack("<I", w["hash"]), 4, ctypes.byref(m))
            good = a and b and n.value == 8 and m.value == 4
            ok += 1 if good else 0
            print(f"  {'restored' if good else 'FAILED'} record 0x{p:X}")
        if ok == len(rec["writes"]):
            UNDO.unlink(); print("all restored; undo file removed.")
        return

    if not ids:
        print(__doc__); sys.exit(2)
    found = []
    for icon in ids:
        found += records_for(h, icon)
    print(f"{len(found)} texture record(s):")
    for p, s, hsh, surface, path in found:
        print(f"  record 0x{p:X}  hash 0x{hsh:08X}  {surface:<5} {path}")
    if not found:
        print("none found; the texture is not resident under that name."); return
    if not apply:
        print("\ndry run; nothing written."); return

    UNDO.write_text(json.dumps({"pid": pid, "writes": [
        {"record": hex(p), "name": hex(s), "hash": hsh, "surface": surface} for p, s, hsh, surface, _ in found]}, indent=1))
    print(f"\nundo written first: {UNDO}")
    done = 0
    for p, s, hsh, surface, _ in found:
        if q(h, p) != s or read(h, p + 0x30, 4) != struct.pack("<I", hsh):
            print(f"  skipped 0x{p:X}: changed since scan"); continue
        n = ctypes.c_size_t(0); m = ctypes.c_size_t(0)
        a = k32.WriteProcessMemory(h, ctypes.c_void_p(p), struct.pack("<Q", s + 1), 8, ctypes.byref(n))
        b = k32.WriteProcessMemory(h, ctypes.c_void_p(p + 0x30), struct.pack("<I", 0), 4, ctypes.byref(m))
        good = a and b and q(h, p) == s + 1 and read(h, p + 0x30, 4) == b"\0\0\0\0"
        print(f"  {'hidden' if good else 'FAILED'} 0x{p:X} ({surface})")
        done += 1 if good else 0
    print(f"\n{done} of {len(found)} texture record(s) hidden.")
    k32.CloseHandle(h)


if __name__ == "__main__":
    main()
