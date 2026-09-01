"""Set a weapon's LIVE kill count inside the running game, without a restart.

DEV HARNESS CHEAT. This probe WRITES to the running process. It exists so a tier crossing can
be staged on demand instead of farmed, which is what the LW-381 glow question needs: change the
tally, let the mod's out of battle tick re-judge, then open a menu and see whether the new glow
is drawn.

WHY A FILE EDIT IS NOT ENOUGH
-----------------------------
kills.json is the mod's SAVE of the tally, not its source. The runtime holds the counts in a
managed Dictionary<int,int> (LivingWeapon/Kills/KillTally.cs, `Kills`) and only ever writes that
file at battle end; nothing re-reads it mid-session. Editing the file therefore changes nothing
the running game can see, and the next battle end overwrites the edit. tools/glow_bounce.py
refuses to run while the game is up for exactly this reason. To move the live tally we have to
move the dictionary.

HOW IT IS FOUND
---------------
A .NET Dictionary<int,int> stores its contents in an Entry[] of 16 byte records:

    +0  uint hashCode      for Int32 keys the hash IS the key, so dword[0] == dword[2]
    +4  int  next
    +8  int  key
    +12 int  value

That gives a strong, self checking fingerprint, and we know every pair the tally should hold, so
a candidate is only accepted when several NEIGHBOURING entries also decode to known (id, count)
pairs. A lone matching record somewhere in a 4 GB heap proves nothing; a record sitting beside
three other correct ones is the tally.

SAFETY
------
  * Writes exactly 4 bytes, the value field of one entry. That is the same field the mod itself
    writes when it credits a kill.
  * Requires MIN_NEIGHBOURS corroborating entries before it will write anything.
  * Re-verifies the entry immediately before writing, because a GC can move the array between
    the scan and the write.
  * Prints the original value so it can be put back by re-running with the old number.
  * Not a save edit: the live tally is written to kills.json by the game's own next battle end.

USAGE (game running)
--------------------
    python tools/probes/lw381_tally_poke.py 22 10      # Claymore to 10 kills, live
    python tools/probes/lw381_tally_poke.py 22 10 --dry-run
"""
import ctypes
import json
import struct
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
from lw381_icon_memscan import CHUNK, find_pid, k32, read, regions  # noqa: E402

TALLY = (Path(r"c:/program files (x86)/steam/steamapps/common"
              r"/FINAL FANTASY TACTICS - The Ivalice Chronicles/Reloaded/User/Mods"
              r"/prawl.fft.livingweapons/kills.json"))
ENTRY = 16
MIN_NEIGHBOURS = 3   # corroborating (id, count) entries required around the candidate
SPAN = 24            # entries either side of the candidate to look for corroboration


def known_pairs():
    d = json.loads(TALLY.read_text())
    d = d.get("kills", d)
    return {int(k): v for k, v in d.items() if isinstance(v, int)}


def decode(buf, off):
    """(hash, next, key, value) of the entry at off, or None if it runs off the buffer."""
    if off < 0 or off + ENTRY > len(buf):
        return None
    return struct.unpack_from("<IiiI", buf, off)


def main():
    argv = [a for a in sys.argv[1:] if not a.startswith("--")]
    dry = "--dry-run" in sys.argv
    if len(argv) != 2:
        print(__doc__)
        sys.exit(2)
    want_id, want_val = int(argv[0], 0), int(argv[1], 0)

    pairs = known_pairs()
    print(f"tally file holds {len(pairs)} weapon(s); corroborating against those pairs")
    print(f"target: id {want_id} -> {want_val} kills\n")

    pid = find_pid()
    if pid is None:
        print("fft_enhanced.exe is not running."); sys.exit(1)
    h = k32.OpenProcess(0x0410 if dry else 0x0438, False, pid)
    if not h:
        print(f"could not open pid {pid} (error {ctypes.get_last_error()})."); sys.exit(1)

    # The value the LIVE dictionary holds may differ from the file (the file may have been
    # edited, or a battle may have run since). Accept any plausible count for the target key
    # and let the neighbours prove the array.
    hits = []
    for base, size, mtype in regions(h):
        if mtype != 0x20000:          # MEM_PRIVATE only; the managed heap is private
            continue
        off = 0
        while off < size:
            n = min(CHUNK, size - off)
            buf = read(h, base + off, n)
            if buf is None:
                break
            for i in range(0, len(buf) - ENTRY, 4):
                e = decode(buf, i)
                if e is None or e[0] != want_id or e[2] != want_id or e[3] > 0xFFFF:
                    continue
                good = 0
                for j in range(-SPAN, SPAN + 1):
                    if j == 0:
                        continue
                    o = i + j * ENTRY
                    n2 = decode(buf, o)
                    if n2 and n2[0] == n2[2] and pairs.get(n2[2]) == n2[3]:
                        good += 1
                if good >= MIN_NEIGHBOURS:
                    hits.append((base + off + i, e[3], good))
            off += n

    if not hits:
        print("no tally entry found. The dictionary may have been moved by a GC mid scan, or the")
        print("mod is not armed. Re-run; if it keeps failing the layout assumption is wrong.")
        k32.CloseHandle(h); sys.exit(1)

    print(f"{'address':<16} {'current':>7} {'corroborating neighbours':>26}")
    for addr, val, good in hits:
        print(f"0x{addr:<14X} {val:>7} {good:>26}")

    # A .NET GC copies objects between generations and leaves the old bytes behind, so several
    # candidates decode identically and only one is the dictionary the mod is actually reading.
    # Guessing which is hopeless, but writing them ALL is safe and sufficient: the corroboration
    # gate already proves each is a tally entry array, a stale copy is dead memory nobody reads,
    # and the live one is guaranteed to be among them. Highest neighbour count only, so a
    # partial/truncated copy never qualifies.
    best = max(g for _, _, g in hits)
    hits = [x for x in hits if x[2] == best]
    if len(hits) > 1:
        print(f"\n{len(hits)} candidate(s) at the top corroboration level ({best} neighbours);"
              " writing all of them (see the note in the source).")

    addr, cur, good = hits[0]
    if dry:
        print(f"\ndry run: would write {want_val} over {cur} at 0x{addr + 12:X}. Nothing written.")
        k32.CloseHandle(h); return

    payload = struct.pack("<I", want_val)
    done, old = 0, None
    for addr, cur, good in hits:
        # Re-verify each entry immediately before its write: a GC may have moved the array.
        fresh = read(h, addr, ENTRY)
        e = decode(fresh, 0) if fresh else None
        if not e or e[0] != want_id or e[2] != want_id:
            print(f"  skipped 0x{addr:X}: moved between scan and write (GC)")
            continue
        old = e[3] if old is None else old
        wrote = ctypes.c_size_t(0)
        ok = k32.WriteProcessMemory(h, ctypes.c_void_p(addr + 12), payload, 4, ctypes.byref(wrote))
        back = read(h, addr, ENTRY)
        print(f"  {'wrote' if ok else 'FAILED'} 0x{addr + 12:X}: {e[3]} -> "
              f"{decode(back, 0)[3] if back else '?'}")
        done += 1 if ok else 0
    print(f"\n{done} of {len(hits)} copy(ies) now read {want_val} kills for id {want_id}.")
    if done and old is not None:
        print(f"Put it back with:  python {Path(__file__).name} {want_id} {old}")
        print("The mod re-judges icons out of battle within a couple of seconds; then open a menu.")
    k32.CloseHandle(h)


if __name__ == "__main__":
    main()
