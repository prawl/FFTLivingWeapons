#!/usr/bin/env python
"""LW-371 premise probe: relocate the two weapon menu order templates LIVE and watch the game.

Plain language: the game lists the weapons and shields you carry from two little charts (one
for the party Inventory screen, one for the unit equip picker) that hold at most 140 kinds
plus an end marker, and its chart housekeeper never checks that limit: a 141st kind is shoved
in at the front and the last word falls off the end into the neighboring data. Both charts
also round-trip through the save file (a fixed 141-word field each), so that overflow is
saved. LW-371 moves the charts the game WORKS ON onto a page the mod owns (room for hundreds
of words) while the save file keeps its 141-word field. This probe does the move from OUTSIDE
the game, the way a debugger would, so the premise ("the game keeps working with the two
charts relocated, and inserts the 141st kind on the page") is proven or refuted before any of
it is built into the DLL.

WHAT THE SWEEP FOUND (2026-08-31, live 1.5.2 process, tools/probes/lw368_count_list_relocate.py's
classifier over the two chart spans, plus a data-section pointer sweep):
  - The game's live readers/writers of a weapon chart never name it directly: the housekeeper
    (0x140285F80 / 0x140286070 / 0x14039684C), the menu rebuild's five callers and the
    menu-open regenerate (0x140334E72 -> 0x140285F2C) all fetch the chart through a POINTER
    TABLE slot: inventory chart = 0x14067F498[0] and 0x140689C38[0], picker chart =
    0x14067FA90[0]. Re-pointing those three qwords moves every live reader at once.
  - Every plain-code `lea` that names a chart directly is a whole-save-struct bulk copy
    (movups loops over 0x80-byte strides): serialize 0x1402194BC / 0x14021973C, load-apply
    0x14021B4BD / 0x14021B5BB, the second restore 0x14021E42F / 0x14021E52B, and two
    mirror-copy pairs (0x1402CF3C5 + 0x1402CFCA4, 0x1402CF571 + 0x1402CFDFF; 0x140327201 +
    0x140327573, 0x14032738C + 0x1403276FE). They are LEFT ALONE on purpose: the save file must
    keep its 141-word field, so the old blocks stay the save's staging area and the DLL will
    sync page <-> old block at the save edges it already hooks (SaveEdgeHooks).
  - The copy-protected new-game initializer (0x150C9FB15 writes the marker at the inventory
    chart's word 0; 0x150C9FBF5.. memcpys a fresh picker block and writes the five sub-table
    markers, 0x150C9FCAD..CC9) also targets the OLD blocks and is left alone: a stale page
    self-heals on the first menu open (the regenerate rebuilds the chart from ownership).
  - 0x140286179 `lea rsi` -> picker block +0 is NOT a weapon-chart reader: its only uses are
    [rsi+0x1E6+..], the picker block's FIFTH sub-table (all owned items, 262 words), a
    separate ceiling this probe only MEASURES (--watch prints its marker).
  - The menu LIST the charts order is capped by the shared list builder 0x140288B94 at 145
    entries (`cmp esi,0x91` at 0x140288CC1) and by the list insert 0x140286228 at 146
    (`cmp edx,0x92` at 0x140286318). The static list buffer 0x141811470 has 256 words; the two
    callers that pass a STACK buffer (0x1402875A9 [rsp+0x70] of a 0x1B0 frame, 0x140336B97
    [rsp+0x50] of a 0x190 frame) have exactly 0x140 bytes = 160 words of room and no local
    above the buffer (both functions read whole), so 159 entries + terminator is the largest
    cap that is safe for EVERY caller without touching a frame. --caps widens both to that.
    CORRECTION (2026-08-31, the plan reviewer and the second verifier): the room is 0x130 bytes,
    not 0x140 (the GS cookie sits at rsp+0x1A0 / rsp+0x180, addressed through rbp), and the
    weapons-only picker caller 0x1402875A4 appends the unit's two hand items after the capped
    list before its own terminator, so the buffers must hold cap + 3 words and the largest safe
    cap is 149 (0x95 / 0x96, 152 words = 0x130 exactly). Run 2 applied 0x9F / 0xA0 and never
    built a list past 145, which is the only reason it did not overrun the cookie; CAPS below
    now carry 0x95 / 0x96, the bytes the DLL ships.

What `--apply` does: REFUSES if either live chart has no end marker inside its 141 words (an
already-overflowed chart; see the crash-1 note in apply()), then allocates a 64 KB page just
BELOW the image base (the LW-368 allocator rule), copies the live 0x11A bytes of each chart
onto it (inventory at +0x000, picker at +0x400, 0x400 bytes of room each = up to 511 kinds)
with the unused room WALLED by 0xFFFF words (every chart walker stops at a word >= 0x114, so a
runaway walk can never leave the region), then rewrites the three pointer slots
(each verified to still hold its vanilla value first) and, with `--caps`, the two cap bytes
(0x91 -> 0x9F at 0x140288CC3, 0x92 -> 0xA0 at 0x14028631A, each verified vanilla first).
Writes tools/probes/lw371_order_template_relocate_undo.json. `--undo` writes each chart BACK
into its old block as the vanilla-safe projection the DLL will use (ids below 261 in page
order, at most 140 of them, then the marker; extended ids are dropped the way the game's own
Sort drops them, and the mod re-seats owned ones on the next load or rebuild), then restores
the slots and the cap bytes. `--verify` reports each site. `--watch` prints, twice a second,
where each chart's marker sits (page and old block), the all-items sub-table's marker, and the
list buffer's length, so the owner's actions are visible as they happen.

KNOWN LIMITS (deliberate for a probe): LivingWeapon.dll keeps seating extended ids into the OLD
blocks (Offsets.InventoryOrderTemplate / PickerOrderTemplate) while the probe is applied, so
during the probe an extended id bought fresh reaches the page only through the game's own
housekeeper and the rebuild hook's re-append (both address-agnostic), which is exactly the path
under test. A save written while applied carries the OLD block (stale) in its chart field; a
reload restores the old block and leaves the page as it was; the menus must still list every
owned kind after that (the self-heal premise). Apply on a quiet screen (world map).

Usage:  python tools/probes/lw371_order_template_relocate.py            # --scan (read-only)
        python tools/probes/lw371_order_template_relocate.py --apply [--caps]
        python tools/probes/lw371_order_template_relocate.py --verify
        python tools/probes/lw371_order_template_relocate.py --watch
        python tools/probes/lw371_order_template_relocate.py --undo
"""
import json
import struct
import sys
import time
from pathlib import Path

import numpy as np

sys.path.insert(0, str(Path(__file__).resolve().parent))
import code_patch as CP                      # find_pid / open_proc / rpm / wpm_guarded
import lw346_xref_scan as XS                 # sections() / rd_big()
import lw368_count_list_relocate as L        # the classifier, the page allocator, write_ok

IMAGE_BASE = 0x140000000
INV = 0x1407B2550          # Offsets.InventoryOrderTemplate
PICK = 0x141874540         # Offsets.PickerOrderTemplate (head of a 0x3F2-byte block of five sub-tables)
WORDS = 141                # capacity of each chart in u16 words (Offsets.*TemplateWords)
SPAN = WORDS * 2           # 0x11A bytes
PICK_BLOCK = 0x3F2
ALL_ITEMS = PICK + 0x1E6   # the fifth picker sub-table: every owned item id, 262 words
LIST_BUF = 0x141811470     # the static menu list buffer, 256 words, 0xFFFF-terminated
END = 0x00FF
FIRST_EXTENDED = 261
PAGE_ROOM = 0x400          # bytes per relocated chart on the page (511 words + marker)

# The three pointer-table slots the game fetches the charts through (vanilla qword = the chart).
SLOTS = [
    ("inventory chart, pointer table 0x14067F498 slot 0", 0x14067F498, INV),
    ("inventory chart, pointer table 0x140689C38 slot 0", 0x140689C38, INV),
    ("picker chart, pointer table 0x14067FA90 slot 0", 0x14067FA90, PICK),
]
# The two list caps: (label, address of the imm32's low byte, vanilla byte, widened byte, the
# whole instruction's vanilla bytes at the instruction start for the pre-write check).
CAPS = [
    ("list builder entry cap (cmp esi,0x91 -> 0x95) at 0x140288CC1", 0x140288CC3, 0x91, 0x95, 0x140288CC1, bytes.fromhex("81fe91000000")),
    ("list insert bound (cmp edx,0x92 -> 0x96) at 0x140286318", 0x14028631A, 0x92, 0x96, 0x140286318, bytes.fromhex("81fa92000000")),
]
UNDO = Path(__file__).resolve().parent / "lw371_order_template_relocate_undo.json"


def words(h, addr, n):
    raw = L.read_or_none(h, addr, n * 2)
    return None if raw is None or len(raw) < n * 2 else list(struct.unpack("<%dH" % n, raw))


def marker_of(w, end=END):
    return w.index(end) if (w is not None and end in w) else None


def chart_state(h, addr, cap):
    w = words(h, addr, cap)
    if w is None:
        return "unreadable"
    m = marker_of(w)
    if m is None:
        return f"NO MARKER in {cap} words"
    ext = [x for x in w[:m] if (x & 0x3FF) >= FIRST_EXTENDED]
    return f"marker@{m} ({m} kinds, {len(ext)} extended)"


def scan(h):
    """Read-only: every code reference to either chart span (classified) and every data qword
    pointing into them."""
    L.LISTS = {"inv": (INV, SPAN, 0x000, PAGE_ROOM), "pick": (PICK, PICK_BLOCK, 0x400, PAGE_ROOM)}
    L.LIST_ENTRIES = {"inv": SPAN, "pick": PICK_BLOCK}
    sites, uncl = L.scan(h, verbose=False)
    print(f"{len(sites)} classified code site(s), {len(uncl)} unclassified raw hit(s) "
          f"({sum(1 for n, _ in uncl if n == '.xdata')} of them in the copy-protected .xdata, coincidences of a 0x3F2-byte span)")
    for s in sites:
        print(f"  {s['section']:<6} {s['list']:<5} {s['kind']:<5} field@{s['addr']:#x} off+{s['off']:#x}  {s['start']:#x}: {s['text']}")
    for name, hit in uncl:
        if name != ".xdata":
            print(f"  UNCLASSIFIED {name} raw hit at {hit:#x} (a coincidental byte pattern; read its context before trusting)")
    print("data-section qword pointers into either chart span:")
    for name, base, size, chars in XS.sections(h):
        if chars & L.IMAGE_SCN_MEM_EXECUTE:
            continue
        buf = XS.rd_big(h, base, size)
        if not buf or len(buf) < 8:
            continue
        b = np.frombuffer(buf, dtype=np.uint8)
        n = len(b) - 7
        q = np.zeros(n, dtype=np.uint64)
        for i in range(8):
            q |= b[i:n + i].astype(np.uint64) << np.uint64(8 * i)
        for lname, lo, hi in (("inv", INV, INV + SPAN + 2), ("pick", PICK, PICK + PICK_BLOCK)):
            for i in np.nonzero((q >= lo) & (q < hi))[0]:
                print(f"  {name:<8} {lname:<5} ptr@{base + int(i):#x} -> {int(q[i]):#x} (+{int(q[i]) - (INV if lname == 'inv' else PICK):#x})")
    print("live charts:")
    print(f"  inventory {INV:#x}: {chart_state(h, INV, WORDS)}")
    print(f"  picker    {PICK:#x}: {chart_state(h, PICK, WORDS)}")
    print(f"  all-items {ALL_ITEMS:#x}: {chart_state(h, ALL_ITEMS, 262)}  (262 words; a marker past 261 means it overflowed its block)")


def projection(page_words):
    """The vanilla-safe chart the old block gets on undo: ids below 261 in page order, at most
    140, then the marker."""
    m = marker_of(page_words)
    body = page_words[:m] if m is not None else []
    keep = [x for x in body if (x & 0x3FF) < FIRST_EXTENDED][:WORDS - 1]
    return keep + [END]


def apply(h):
    if UNDO.exists():
        raise SystemExit(f"{UNDO.name} exists: undo (or delete it) before applying again")
    for label, addr, vanilla in SLOTS:
        cur = L.read_or_none(h, addr, 8)
        if cur is None or struct.unpack("<Q", cur)[0] != vanilla:
            raise SystemExit(f"REFUSING: {label} reads {cur.hex() if cur else 'unreadable'}, not the vanilla chart address")
    want_caps = "--caps" in sys.argv
    if want_caps:
        for label, addr, van, new, insn, vbytes in CAPS:
            cur = L.read_or_none(h, insn, 6)
            if cur is None or bytes(cur) != vbytes:
                raise SystemExit(f"REFUSING: {label} reads {cur.hex() if cur else 'unreadable'}, not vanilla {vbytes.hex()}")
    # CRASH 1 (2026-08-31 06:31): the give-all save's charts had ALREADY overflowed (141 kinds,
    # marker pushed past word 140 by the game's own housekeeper at 06:30:37, log line "no 00FF
    # end marker in its first 141 words") and the first version of this probe copied them onto
    # a page followed by zeros. The housekeeper's walk stops only at the marker or at a word
    # >= 0x114; zeros are neither, so the next insert (a Ravager purchase) walked off the 64 KB
    # page into unmapped memory and the game died. Two rules bought with that crash: REFUSE a
    # chart with no marker inside its 141 words (the probe cannot know where such a chart
    # ends), and WALL the unused page room with 0xFFFF words, which every chart walker treats
    # as the end, so a runaway walk can never leave the region again.
    # `--assume-full` is the one sanctioned exception, for the state crash 1 left behind and any
    # save that holds it: a chart whose 141 words are ALL nonzero ids (the marker was pushed to
    # word 141, into the padding word on the inventory side and into the helmet chart's first
    # word on the picker side, where that chart's own housekeeper then rebuilt itself). The
    # copy then takes the 141 ids and writes the marker at word 141 on the page, which has room.
    assume_full = "--assume-full" in sys.argv
    full = {}
    for lname, src in (("inventory", INV), ("picker", PICK)):
        w = words(h, src, WORDS)
        if marker_of(w) is not None:
            continue
        if assume_full and w is not None and all(0 < x != END for x in w):
            full[lname] = True
            print(f"  {lname}: no marker, all {WORDS} words are ids; --assume-full places the marker at word {WORDS} on the page")
            continue
        raise SystemExit(f"REFUSING: the live {lname} chart has no end marker inside its {WORDS} words "
                         f"(already overflowed or damaged); load a save whose charts hold at most {WORDS - 1} kinds first"
                         + ("" if assume_full else ", or pass --assume-full if every one of its 141 words is a real id"))
    page = L.alloc_page_below_image(h)
    print(f"page allocated at {page:#x} (rva {page - IMAGE_BASE:#x})")
    record = {"page": page, "slots": [], "caps": []}
    wall = b"\xff\xff" * ((PAGE_ROOM - SPAN) // 2)
    for lname, src, off in (("inventory", INV, 0x000), ("picker", PICK, 0x400)):
        live = L.read_or_none(h, src, SPAN)
        if live is None or len(live) != SPAN:
            raise SystemExit(f"cannot read the live {lname} chart")
        tail = (struct.pack("<H", END) + wall[2:]) if full.get(lname) else wall
        if not L.write_ok(h, page + off, bytes(live) + tail):
            raise SystemExit(f"cannot seed the {lname} chart into the page")
        print(f"  {lname}: {SPAN:#x} bytes copied to {page + off:#x} + {len(wall) // 2} wall words; {chart_state(h, page + off, PAGE_ROOM // 2)}")
    for label, addr, vanilla in SLOTS:
        new = page + (0x000 if vanilla == INV else 0x400)
        old = L.read_or_none(h, addr, 8)
        if not L.write_ok(h, addr, struct.pack("<Q", new)):
            UNDO.write_text(json.dumps(record, indent=1))
            undo(h)
            raise SystemExit(f"apply aborted: {label} refused the write")
        back = L.read_or_none(h, addr, 8)
        ok = back is not None and struct.unpack("<Q", back)[0] == new
        record["slots"].append({"label": label, "addr": addr, "old": bytes(old).hex(), "new": struct.pack("<Q", new).hex(), "landed": ok})
        print(f"  {'ok ' if ok else 'BAD'} {label}: {vanilla:#x} -> {new:#x}")
    if want_caps:
        for label, addr, van, new, insn, vbytes in CAPS:
            if not L.write_ok(h, addr, bytes([new])):
                UNDO.write_text(json.dumps(record, indent=1))
                undo(h)
                raise SystemExit(f"apply aborted: {label} refused the write")
            back = L.read_or_none(h, addr, 1)
            ok = back is not None and back[0] == new
            record["caps"].append({"label": label, "addr": addr, "old": van, "new": new, "landed": ok})
            print(f"  {'ok ' if ok else 'BAD'} {label}: {van:#x} -> {new:#x} ({van - 1} -> {new - 1} entries)")
    UNDO.write_text(json.dumps(record, indent=1))
    print(f"applied {len(record['slots'])} slot(s) and {len(record['caps'])} cap(s); undo record at {UNDO}")


def undo(h):
    if not UNDO.exists():
        raise SystemExit("nothing to undo")
    record = json.loads(UNDO.read_text())
    page = record["page"]
    bad = 0
    for lname, dst, off in (("inventory", INV, 0x000), ("picker", PICK, 0x400)):
        w = words(h, page + off, PAGE_ROOM // 2)
        if w is None:
            print(f"  {lname}: page chart unreadable, old block left as is")
            continue
        proj = projection(w)
        m = marker_of(w)
        dropped = (m or 0) - (len(proj) - 1)
        if not L.write_ok(h, dst, struct.pack("<%dH" % len(proj), *proj)):
            bad += 1
            print(f"  FAILED to write the {lname} projection back into {dst:#x}")
            continue
        print(f"  {lname}: {len(proj) - 1} vanilla kind(s) + marker written back into {dst:#x} ({dropped} extended/overflow word(s) dropped; the mod re-seats owned ones)")
    for s in record["slots"]:
        old = bytes.fromhex(s["old"])
        if not L.write_ok(h, s["addr"], old):
            bad += 1
            print(f"  FAILED to restore {s['label']}")
            continue
        back = L.read_or_none(h, s["addr"], 8)
        if back is None or bytes(back) != old:
            bad += 1
            print(f"  MISMATCH after restore: {s['label']}")
    for c in record["caps"]:
        if not L.write_ok(h, c["addr"], bytes([c["old"]])):
            bad += 1
            print(f"  FAILED to restore {c['label']}")
            continue
        back = L.read_or_none(h, c["addr"], 1)
        if back is None or back[0] != c["old"]:
            bad += 1
            print(f"  MISMATCH after restore: {c['label']}")
    if bad:
        raise SystemExit(f"{bad} site(s) not restored; the undo record is kept")
    UNDO.rename(UNDO.with_suffix(".json.done"))
    print(f"restored {len(record['slots'])} slot(s) and {len(record['caps'])} cap(s); the page stays allocated (harmless) and the record is renamed .done")


def verify(h):
    if not UNDO.exists():
        raise SystemExit("no undo record: nothing applied")
    record = json.loads(UNDO.read_text())
    for s in record["slots"]:
        cur = bytes(L.read_or_none(h, s["addr"], 8) or b"").hex()
        print(f"  {'NEW' if cur == s['new'] else ('old' if cur == s['old'] else 'OTHER')}  {s['label']}")
    for c in record["caps"]:
        b = L.read_or_none(h, c["addr"], 1)
        cur = b[0] if b else None
        print(f"  {'NEW' if cur == c['new'] else ('old' if cur == c['old'] else 'OTHER')}  {c['label']}")
    page = record["page"]
    print(f"  page inventory: {chart_state(h, page, PAGE_ROOM // 2)};  page picker: {chart_state(h, page + 0x400, PAGE_ROOM // 2)}")
    print(f"  old inventory:  {chart_state(h, INV, WORDS)};  old picker:  {chart_state(h, PICK, WORDS)}")


def watch(h):
    page = json.loads(UNDO.read_text())["page"] if UNDO.exists() else None
    print("watching (Ctrl+C stops); a line prints only when something changes")
    last = None
    while True:
        lst = words(h, LIST_BUF, 256)
        lm = lst.index(0xFFFF) if lst and 0xFFFF in lst else None
        parts = [
            f"old inv {chart_state(h, INV, WORDS)}",
            f"old pick {chart_state(h, PICK, WORDS)}",
            f"all-items {chart_state(h, ALL_ITEMS, 262)}",
            f"list buf len {lm}",
        ]
        if page:
            parts = [f"PAGE inv {chart_state(h, page, PAGE_ROOM // 2)}", f"PAGE pick {chart_state(h, page + 0x400, PAGE_ROOM // 2)}"] + parts
        line = " | ".join(parts)
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
    elif "--verify" in sys.argv:
        verify(h)
    elif "--watch" in sys.argv:
        watch(h)
    else:
        scan(h)


if __name__ == "__main__":
    main()
