#!/usr/bin/env python
"""LW-372 premise probe: does the Items tab draw and scroll past 149 rows once the list cap allows it?

Plain language: the game copies the weapons-and-shields chart onto a fixed notepad before it
draws the Items tab, and a line limit in the list builder stops that copy at 149 entries (since
LW-371; vanilla 145). LW-372 will raise the limit to 255 for the big notepad (256 lines) and hand
the two menus that use a small notepad a mod-owned one. Before building that, this probe answers
the one question no disassembly can: whether the Items tab's OWN drawing code copes with 250 rows
(scrolls, no clipping, no crash). It raises the two cap bytes from outside, gives the current save
every vanilla weapon and shield kind it lacks (one copy each, written onto the relocated count
page the DLL's boot line names), and watches the chart and the list buffer while the owner opens
the Items tab.

RULES FOR THE RUN (the two stack-buffer paths the LW-372 hook exists to protect):
  - DO NOT open a unit's equip picker and DO NOT enter a shop while the probe is applied: their
    list buffers hold 152 words (fnA 0x1402875A4 / fnB 0x140336B88, the LW-371 plan finding 5 and
    premise P14) and a 250-entry list there overruns the stack cookie: an uncatchable process
    kill. The Items tab (static buffer 0x141811470, 256 words) and its Sort are the safe path.
  - Undo before opening either menu.

What it patches: the builder entry cap byte at 0x140288CC3 (the DLL ships 0x95 = 149 there since
LW-371; this probe expects THAT value, not vanilla's 0x91) -> 0xFF (255), and the list insert
bound's imm32 at 0x14028631A (the DLL ships 0x96 = 150) -> 0x100 (256 = cap + 1). Undo restores
the DLL's bytes. Both are verified by read-back. Bag writes go to the relocated count page
(`item count lists relocated to 0x...` in livingweapon.log), one byte per id, never past 0x105.

RETIRED 2026-08-31 (same day): LW-372 SHIPPED the widened caps (the DLL writes 0xFF/0x100 at
boot, with the list-builder hook protecting the two stack callers), so --apply now refuses by
design on an armed build (it expects the pre-LW-372 baseline 0x95/0x96). The read-only state
and --watch remain useful; the probe stays as the Phase 0 provenance record (ledger row
[items-tab-draws-past-149], PROVEN).

Usage:  python tools/probes/lw372_list_cap_probe.py            # read-only state
        python tools/probes/lw372_list_cap_probe.py --apply    # RETIRED: refuses on the shipped build
        python tools/probes/lw372_list_cap_probe.py --watch    # chart marker + list length, twice a second
        python tools/probes/lw372_list_cap_probe.py --undo     # caps back to the pre-LW-372 0x95/0x96
"""
import json
import re
import struct
import sys
import time
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import code_patch as CP

ROOT = Path(__file__).resolve().parents[2]
LOG = Path(r"C:\Program Files (x86)\Steam\steamapps\common\FINAL FANTASY TACTICS - The Ivalice Chronicles\Reloaded\Mods\prawl.fft.livingweapons\livingweapon.log")
CAP_BYTE = 0x140288CC3          # cmp esi, imm32 at 0x140288CC1: the low byte
INSERT_IMM = 0x14028631A        # cmp edx, imm32 at 0x140286318: the four imm bytes
DLL_CAP, DLL_INSERT = 0x95, 0x96
NEW_CAP, NEW_INSERT = 0xFF, 0x100
LIST_BUF = 0x141811470          # the static menu list buffer, 256 words, 0xFFFF terminated
HAND = {"Knife", "NinjaBlade", "Sword", "KnightSword", "Katana", "Axe", "Rod", "Staff", "Flail", "Gun",
        "Crossbow", "Bow", "Instrument", "Book", "Polearm", "Pole", "Bag", "Cloth", "Shield"}
UNDO = Path(__file__).resolve().parent / "lw372_list_cap_probe_undo.json"


def pages():
    log = LOG.read_text(encoding="utf-8", errors="replace")
    c = re.search(r"item count lists relocated to 0x([0-9A-Fa-f]+)", log)
    t = re.search(r"menu order charts relocated to 0x([0-9A-Fa-f]+)", log)
    if not (c and t):
        raise SystemExit("the DLL's boot line with both page addresses is not in livingweapon.log; is the LW-371 build armed?")
    return int(c.group(1), 16), int(t.group(1), 16)


def words(h, addr, n):
    return list(struct.unpack("<%dH" % n, CP.rpm(h, addr, n * 2)))


def state(h):
    counts, charts = pages()
    cap = CP.rpm(h, CAP_BYTE, 1)[0]
    ins = struct.unpack("<I", CP.rpm(h, INSERT_IMM, 4))[0]
    w = words(h, charts, 511)
    mk = w.index(0xFF) if 0xFF in w else None
    lst = words(h, LIST_BUF, 256)
    lm = lst.index(0xFFFF) if 0xFFFF in lst else None
    return dict(cap=cap, ins=ins, chart=mk, list=lm, counts=counts, charts=charts)


def show(s):
    print(f"builder cap {s['cap']} entries, insert bound {s['ins']}, weapons+shields chart {s['chart']} kinds, "
          f"list buffer {s['list']} entries (counts page {s['counts']:#x}, charts page {s['charts']:#x})")


def apply(h):
    if UNDO.exists():
        raise SystemExit(f"{UNDO.name} exists: undo first")
    s = state(h)
    if s["cap"] != DLL_CAP or s["ins"] != DLL_INSERT:
        raise SystemExit(f"REFUSING: caps read {s['cap']:#x}/{s['ins']:#x}, not the DLL's {DLL_CAP:#x}/{DLL_INSERT:#x}")
    items = json.load(open(ROOT / "data" / "items.json", encoding="utf-8"))["items"]
    cat = {i["id"]: i["category"] for i in items}
    bag = CP.rpm(h, s["counts"], 0x105)
    give = [i for i in range(1, 0x105) if bag[i] == 0 and cat.get(i) in HAND]
    CP.wpm_guarded(h, CAP_BYTE, bytes([NEW_CAP]))
    CP.wpm_guarded(h, INSERT_IMM, struct.pack("<I", NEW_INSERT))
    for i in give:
        CP.wpm_guarded(h, s["counts"] + i, bytes([1]))
    back = CP.rpm(h, s["counts"], 0x105)
    UNDO.write_text(json.dumps({"cap": DLL_CAP, "insert": DLL_INSERT, "gave": give}, indent=1))
    s2 = state(h)
    print(f"caps now {s2['cap']} / {s2['ins']}; gave 1 copy each of {len(give)} vanilla hand kinds "
          f"(readback {'OK' if all(back[i] == 1 for i in give) else 'MISMATCH'}); expect the chart at "
          f"{(s['chart'] or 0) + len(give)} after the next Items-tab open")


def undo(h):
    if not UNDO.exists():
        raise SystemExit("nothing to undo")
    r = json.loads(UNDO.read_text())
    CP.wpm_guarded(h, CAP_BYTE, bytes([r["cap"]]))
    CP.wpm_guarded(h, INSERT_IMM, struct.pack("<I", r["insert"]))
    s = state(h)
    if s["cap"] != r["cap"] or s["ins"] != r["insert"]:
        raise SystemExit("restore MISMATCH; the undo record is kept")
    UNDO.rename(UNDO.with_suffix(".json.done"))
    print(f"caps restored to {s['cap']:#x}/{s['ins']:#x}; the given kinds stay in the bag (throwaway save)")


def watch(h):
    last = None
    while True:
        s = state(h)
        line = f"chart {s['chart']} | list {s['list']} | cap {s['cap']}/{s['ins']}"
        if line != last:
            print(time.strftime("%H:%M:%S"), line, flush=True)
            last = line
        time.sleep(0.5)


def main():
    h = CP.open_proc(CP.find_pid())
    if "--apply" in sys.argv:
        apply(h)
    elif "--undo" in sys.argv:
        undo(h)
    elif "--watch" in sys.argv:
        watch(h)
    else:
        show(state(h))


if __name__ == "__main__":
    main()
