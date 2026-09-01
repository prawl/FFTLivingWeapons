"""LW-381 premise probe: make the game RELOAD one menu icon by hiding its cache entry.

THIS PROBE WRITES TO THE RUNNING GAME (apply mode). Dry run is read only (0x0410 handle).

MECHANISM (see lw381_parts_cache_probe.py for how it was found)
  The UITextureParts cache lookup (0x1403FBB80) matches an entry on its interned id (+8) when
  both sides carry one, else by strcmp on its name pointer (+0). Consumers look up by name on
  every draw and never keep the entry pointer. So an entry that no longer answers to its name is
  simply gone from the cache's point of view: the next Load(uri) misses, creates a fresh entry
  and reads the current pac. That is the reload lever.

  The hide is two fields per entry, both plain data on a heap object we have already read:
      +8  interned id  -> 0            (forces the strcmp path)
      +0  name pointer -> name + 1     (a valid C string, "ITextureParts://...", matches nothing)
  Nothing is freed, nothing is moved, the old entry keeps working for anyone still holding it,
  and both fields are recorded first so --restore can put them back byte for byte.

  Every entry carrying the icon's card and small URIs is hidden, whichever manager owns it
  (several UI contexts each keep their own vector, and the same name appears in each).

USAGE (game running)
    python tools/probes/lw381_parts_cache_rename.py 22            # dry run: list entries
    python tools/probes/lw381_parts_cache_rename.py 22 --apply    # hide them
    python tools/probes/lw381_parts_cache_rename.py --restore     # undo the last apply

Suggested premise test: patch the icon's pac bytes to unmistakable donor art first
(live_icon_patch_probe.py patch --target 22), --apply, then reopen the equipment screen. A red
shield where the Claymore was means the reload lever works.
"""
import ctypes
import json
import struct
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
from lw381_icon_memscan import find_pid, k32, read  # noqa: E402
from lw381_parts_cache_probe import PREFIX, MEM_PRIVATE, cstr, q, sweep, sweep_many  # noqa: E402

UNDO = Path(__file__).resolve().parent / "lw381_parts_cache_rename_undo.json"


def entries_for(h, icon):
    """[(entry, name_ptr, id, uri)] for every real cache entry naming this icon. Two passes."""
    tag = f"_{icon:03d}_uitx"
    names = {}
    for s in sweep(h, PREFIX, MEM_PRIVATE):                     # pass 1: URI strings
        uri = cstr(h, s)
        if uri.endswith(tag) and ("/equip_item/" in uri or "/equip_item_s/" in uri):
            names[struct.pack("<Q", s)] = (s, uri)
    out = []
    for needle, holders in sweep_many(h, names.keys(), MEM_PRIVATE).items():   # pass 2: pointers
        s, uri = names[needle]
        for e in holders:
            if e & 7:
                continue
            ident = q(h, e + 8)
            state = read(h, e + 0x58, 2)
            if ident is None or ident == 0 or ident > 0x0FFFFFFF or state != b"\x01\x01":
                continue
            out.append((e, s, ident, uri))
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
        print(f"could not open pid {pid} (error {ctypes.get_last_error()})."); sys.exit(1)
    print(f"game pid {pid}")

    if do_restore:
        if not UNDO.exists():
            print("no undo file."); return
        rec = json.loads(UNDO.read_text())
        ok = 0
        for w in rec["writes"]:
            e = int(w["entry"], 16)
            data = struct.pack("<QQ", int(w["name"], 16), w["id"])
            n = ctypes.c_size_t(0)
            if k32.WriteProcessMemory(h, ctypes.c_void_p(e), data, 16, ctypes.byref(n)) and n.value == 16:
                ok += 1
            print(f"  {'restored' if n.value == 16 else 'FAILED'} entry 0x{e:X}")
        if ok == len(rec["writes"]):
            UNDO.unlink(); print(f"all {ok} entr(ies) restored; undo file removed.")
        else:
            print(f"{ok} of {len(rec['writes'])} restored; undo file kept.")
        return

    if not ids:
        print(__doc__); sys.exit(2)
    found = []
    for icon in ids:
        found += entries_for(h, icon)
    print(f"{len(found)} cache entr(ies) to hide:")
    for e, s, ident, uri in found:
        print(f"  entry 0x{e:X}  id {ident}  {uri}")
    if not found:
        print("nothing to hide: the icon has no loaded cache entry right now."); return
    if not apply:
        print("\ndry run; nothing written. Add --apply to hide them.")
        return

    writes = []
    for e, s, ident, uri in found:
        writes.append({"entry": hex(e), "name": hex(s), "id": ident, "uri": uri})
    UNDO.write_text(json.dumps({"pid": pid, "writes": writes}, indent=1))
    print(f"\nundo written first: {UNDO}")
    done = 0
    for e, s, ident, uri in found:
        # re-verify the two fields immediately before writing
        if q(h, e) != s or q(h, e + 8) != ident:
            print(f"  skipped 0x{e:X}: fields changed since the scan"); continue
        data = struct.pack("<QQ", s + 1, 0)
        n = ctypes.c_size_t(0)
        ok = k32.WriteProcessMemory(h, ctypes.c_void_p(e), data, 16, ctypes.byref(n))
        back = read(h, e, 16)
        good = ok and back == data
        print(f"  {'hidden' if good else 'FAILED'} 0x{e:X}  now name->0x{s + 1:X} id 0")
        done += 1 if good else 0
    print(f"\n{done} of {len(found)} entr(ies) hidden. Reopen the equipment screen and look.")
    print(f"Undo (only if the reload did NOT happen):  python {Path(__file__).name} --restore")
    k32.CloseHandle(h)


if __name__ == "__main__":
    main()
