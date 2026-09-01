"""LW-386 Poacher's Den probe: does the LIVE game's poach-item table still say what each carcass
produces, and what does the Den store hold right now?

Background (player report, Discord 2026-09-01, on the public 2.3.3 build): the Blue Dragon's two
carcasses showed up in the Poacher's Den as katanas (Asura, Kotetsu) and the Red Dragon's as
Bizen Osafune and Murasame, while the regular Dragon and the pig line produced the right items.
Those are item ids 38..41 for carcass keys 87..90. Vanilla data says keys 87..90 produce
Cashmere (120), Dragon Rod (57), Sortilege (239) and Dragon Whisker (105); the mod ships no
poach table and touches no monster job row, so the first question is whether the produced-item
field in the RUNNING game still reads vanilla.

Ground truth this probe leans on:
  * docs/LIVE_LEDGER.md [poachers-den-carcass-store]: the Den store is u8[96] at
    Offsets.PoachStoreBase (0x1411A7A1B), one count per PoachItem Key, index Key - 1, and a
    poked count renders in the reopened Den (owner-eyewitnessed 2026-08-12).
  * working/nxd_poachitem/poachitem.sqlite (tools/extract_poach_map.py's decode): the
    PoachItem sheet, 96 rows, 0x38 bytes each in memory, Cost at +0x24, SellPrice at +0x28,
    the PRODUCED ITEM ID at +0x2C, IconId +0x30, IsRare +0x34. The in-memory row layout equals
    the file layout (three copies were found live 2026-09-01, all reading vanilla).

Modes:
  python tools\\probes\\lw386_den_produced_probe.py store
      READ-ONLY. Print every non-zero carcass count in the Den store.
  python tools\\probes\\lw386_den_produced_probe.py table
      READ-ONLY. Find every in-memory copy of the PoachItem sheet by the Cost/SellPrice pair
      of one distinctive row, verify all 96 rows' Cost/SellPrice against the sqlite decode, and
      print every produced-item field that DIFFERS from the decode (none = the table is intact).
  python tools\\probes\\lw386_den_produced_probe.py poke <key> <count>
      WRITES ONE BYTE of the Den store (the count for <key>). The prior value is saved to
      %TEMP%\\lw386_store_snap.json first (verify-before-write: the byte is read back and must
      equal the requested value). Owner-driven only: open the Den afterwards and read what the
      carcass line shows and produces.
  python tools\\probes\\lw386_den_produced_probe.py restore
      Writes every poked byte back to its saved value and deletes the snapshot.

Every access is RPM/WPM on the game's handle; nothing here can crash the game.
"""
import json
import os
import sqlite3
import struct
import sys
import tempfile

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), ".."))
import dualgun_probe as d          # find_pid / rd / wr / k32
import ai_target_scan as scan      # walk_regions
from lib.paths import ROOT

STORE_BASE = 0x1411A7A1B          # Offsets.PoachStoreBase, LIVE_LEDGER [poachers-den-carcass-store]
STORE_KEYS = 96
ROW_SIZE = 0x38
OFF_COST, OFF_SELL, OFF_PRODUCED, OFF_ICON, OFF_RARE = 0x24, 0x28, 0x2C, 0x30, 0x34
SQLITE = ROOT / "working" / "nxd_poachitem" / "poachitem.sqlite"
SNAP = os.path.join(tempfile.gettempdir(), "lw386_store_snap.json")
CHUNK = 1 << 22


def open_handle(write=False):
    pid = d.find_pid(d.PROC)
    if not pid:
        print("game not running")
        sys.exit(1)
    return d.k32.OpenProcess(d.PV_W if write else d.PV, False, pid)


def sheet():
    """{key: (cost, sell, produced, name)} from the offline decode."""
    con = sqlite3.connect(str(SQLITE))
    rows = con.execute("SELECT Key, Cost, SellPrice, Unknown2C, Unknown8 FROM [PoachItem-en] ORDER BY Key").fetchall()
    return {int(k): (int(c), int(s), int(p), n) for k, c, s, p, n in rows}


def cmd_store(h):
    data = d.rd(h, STORE_BASE, STORE_KEYS)
    if data is None:
        print("store unreadable")
        return
    names = {k: v[3] for k, v in sheet().items()}
    nz = [(k + 1, data[k]) for k in range(STORE_KEYS) if data[k]]
    print(f"Den store @ {STORE_BASE:#x}: {len(nz)} non-zero of {STORE_KEYS}")
    for key, count in nz:
        print(f"  key {key:3d} x{count}  {names.get(key, '?')}")


def find_tables(h, rows):
    """Absolute addresses of every in-memory copy of row 1 (table base), by fingerprint."""
    # Row 86 (Dragon Carcass, rare) has the sheet's rarest Cost/SellPrice pair: 29000/14500.
    anchor_key = 86
    cost, sell = rows[anchor_key][0], rows[anchor_key][1]
    needle = struct.pack("<II", cost, sell)
    bases = []
    for base, size in scan.walk_regions(h):
        off = 0
        while off < size:
            n = min(CHUNK + 16, size - off)
            data = d.rd(h, base + off, n)
            if data:
                i = data.find(needle)
                while i != -1:
                    hit = base + off + i
                    bases.append(hit - OFF_COST - (anchor_key - 1) * ROW_SIZE)
                    i = data.find(needle, i + 1)
            off += CHUNK
    return sorted(set(bases))


def cmd_table(h):
    rows = sheet()
    bases = find_tables(h, rows)
    print(f"{len(bases)} candidate copies of the PoachItem sheet")
    for base in bases:
        blob = d.rd(h, base, STORE_KEYS * ROW_SIZE)
        if blob is None:
            print(f"  {base:#x}: unreadable, skipped")
            continue
        bad_pairs, diffs = 0, []
        for key, (cost, sell, produced, name) in rows.items():
            r = (key - 1) * ROW_SIZE
            c, s, p = struct.unpack_from("<III", blob, r + OFF_COST)
            if (c, s) != (cost, sell):
                bad_pairs += 1
            elif p != produced:
                diffs.append((key, name, produced, p))
        if bad_pairs:
            print(f"  {base:#x}: NOT the sheet ({bad_pairs} rows' Cost/SellPrice differ), skipped")
            continue
        verdict = "INTACT (every produced-item field matches the decode)" if not diffs else f"{len(diffs)} produced-item DIFFS"
        print(f"  {base:#x}: all 96 Cost/SellPrice pairs match; produced items {verdict}")
        for key, name, want, got in diffs:
            print(f"    key {key:3d} {name!r}: sheet says item {want}, memory says item {got}")


def cmd_poke(h, key, count):
    if not (1 <= key <= STORE_KEYS) or not (0 <= count <= 255):
        print("key must be 1..96 and count 0..255")
        sys.exit(2)
    addr = STORE_BASE + (key - 1)
    cur = d.rd(h, addr, 1)
    if cur is None:
        print("store unreadable")
        sys.exit(1)
    snap = json.load(open(SNAP)) if os.path.exists(SNAP) else {}
    snap.setdefault(str(key), cur[0])          # keep the FIRST original if poked twice
    json.dump(snap, open(SNAP, "w"))
    d.wr(h, addr, bytes([count]))
    back = d.rd(h, addr, 1)
    ok = back is not None and back[0] == count
    print(f"key {key}: {cur[0]} -> {count} @ {addr:#x} {'OK' if ok else 'WRITE DID NOT LAND'}; original saved to {SNAP}")


def cmd_restore(h):
    if not os.path.exists(SNAP):
        print("nothing to restore")
        return
    snap = json.load(open(SNAP))
    for key, orig in snap.items():
        addr = STORE_BASE + (int(key) - 1)
        d.wr(h, addr, bytes([int(orig)]))
        back = d.rd(h, addr, 1)
        print(f"key {key}: restored to {orig} {'OK' if back and back[0] == int(orig) else 'FAILED'}")
    os.remove(SNAP)


def main():
    if len(sys.argv) < 2 or sys.argv[1] not in ("store", "table", "poke", "restore"):
        print(__doc__)
        sys.exit(2)
    mode = sys.argv[1]
    if mode == "store":
        cmd_store(open_handle())
    elif mode == "table":
        cmd_table(open_handle())
    elif mode == "poke":
        cmd_poke(open_handle(write=True), int(sys.argv[2]), int(sys.argv[3]))
    else:
        cmd_restore(open_handle(write=True))


if __name__ == "__main__":
    main()
