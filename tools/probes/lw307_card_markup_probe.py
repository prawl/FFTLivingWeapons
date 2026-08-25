#!/usr/bin/env python
"""LW-307: does the weapon description CARD consume inline color markup?

THE QUESTION. [inline-color-markup-in-ui-text] proved the grammar on the world map:
a well-formed <color=NN> tag recolors the text after it, a broken one prints literally.
The card surface is the untested half. If the card parses it too, LW-322's "Grows: Speed"
text can ship colored in its lane hue and the Kills counter can finally wear color.

METHOD. The card's text lives in heap pool copies (the same pool the runtime's Kills paint
overwrites, proven shipped mechanism). This probe finds every in-memory copy of one
weapon's FLAVOR line (the stable description lead) and rewrites it IN PLACE at the same
byte length: keep the first KEEP_PREFIX chars untouched, sacrifice the next 18 chars of
prose for "<color=NN>", and close with "</color>" at the end. Length-neutral, so no string
table shifts. The pre-poke bytes are banked for restore.

VERDICT READING (owner, card on screen; leave and reopen the list to force a redraw):
  colored tail after the first few words  -> CARD PARSES MARKUP, LW-307 proven
  literal "<color=80>" visible on card    -> card path does NOT parse (also a clean verdict)
  no change at all                        -> the poked copies were not the card's source;
                                             re-run scan WITH the card open and poke again

CAVEATS: while poked, the runtime's Kills-counter anchor for this weapon may miss (it
finds the counter by the flavor line); cosmetic, self-heals on restore/battle churn.
Restore before real play. A battle load or menu churn can regenerate copies; re-scan then.

USAGE (game running):
  python lw307_card_markup_probe.py scan "Warbrand"     # find + bank every copy
  python lw307_card_markup_probe.py poke all            # tag every copy (default color 80)
  python lw307_card_markup_probe.py poke all 68         # try another numeric color
  python lw307_card_markup_probe.py poke 0              # tag only hit #0
  python lw307_card_markup_probe.py restore             # put every copy back
"""
import ctypes
import json
import pathlib
import sys

HERE = pathlib.Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))
import battle_cheats as bc  # noqa: E402

UNDO = HERE / "lw307_markup_undo.json"
META = HERE.parents[1] / "LivingWeapon" / "meta.json"
KEEP_PREFIX = 24          # untouched lead chars; protects most of the anchor prefix
TAG_OPEN_LEN = 10         # len("<color=NN>") with a two-digit color
TAG_CLOSE = "</color>"


def flavor_of(key):
    meta = json.loads(META.read_text(encoding="utf8"))
    key = str(key).strip().lower()
    hits = [(i, m) for i, m in meta.items() if i == key or m["name"].lower() == key]
    if not hits:
        hits = [(i, m) for i, m in meta.items() if key in m["name"].lower()]
    if len(hits) != 1:
        sys.exit(f"need exactly one weapon match for {key!r}, got {len(hits)}")
    wid, m = hits[0]
    return wid, m["name"], m["flavor"]


def walk_hits(needle):
    """Every private committed RW copy of `needle` (the auto_text_probe region walk)."""
    bc._require_game()
    h = bc._handle()

    class MBI(ctypes.Structure):
        _fields_ = [("BaseAddress", ctypes.c_ulonglong), ("AllocationBase", ctypes.c_ulonglong),
                    ("AllocationProtect", ctypes.c_ulong), ("_a1", ctypes.c_ulong),
                    ("RegionSize", ctypes.c_ulonglong), ("State", ctypes.c_ulong),
                    ("Protect", ctypes.c_ulong), ("Type", ctypes.c_ulong), ("_a2", ctypes.c_ulong)]

    MEM_COMMIT, MEM_PRIVATE, PAGE_RW, PAGE_GUARD = 0x1000, 0x20000, 0x04, 0x100
    found, addr, scanned = [], 0, 0
    mbi = MBI()
    while addr < 0x7FFFFFFFFFFF:
        if not ctypes.windll.kernel32.VirtualQueryEx(h, ctypes.c_void_p(addr),
                                                     ctypes.byref(mbi), ctypes.sizeof(mbi)):
            break
        base, size = mbi.BaseAddress, mbi.RegionSize
        if (mbi.State == MEM_COMMIT and mbi.Type == MEM_PRIVATE
                and mbi.Protect & PAGE_RW and not mbi.Protect & PAGE_GUARD):
            scanned += size
            CHUNK = 4 * 1024 * 1024
            off = 0
            while off < size:
                buf = bc.rpm(base + off, min(CHUNK + len(needle), size - off))
                if buf:
                    i = buf.find(needle)
                    while i != -1:
                        found.append(base + off + i)
                        i = buf.find(needle, i + 1)
                off += CHUNK
        addr = base + size
    return found, scanned


def tagged_line(orig, color):
    """Same-length rewrite: prefix + <color=NN> + remaining prose (18 chars sacrificed)
    + </color>. Asserts its own length so a bad edit can never shift the pool."""
    open_tag = f"<color={color}>"
    if len(open_tag) != TAG_OPEN_LEN:
        sys.exit("color must be exactly two characters (e.g. 80), the proven numeric form")
    cut = KEEP_PREFIX + TAG_OPEN_LEN + len(TAG_CLOSE)
    if len(orig) <= cut + 8:
        sys.exit("flavor line too short to tag at same length")
    out = orig[:KEEP_PREFIX] + open_tag + orig[cut:] + TAG_CLOSE
    assert len(out) == len(orig), (len(out), len(orig))
    return out


def cmd_scan(key):
    wid, name, flavor = flavor_of(key)
    needle = flavor.encode("utf-8")
    hits, scanned = walk_hits(needle)
    enc = "utf-8"
    if not hits:
        hits, _ = walk_hits(flavor.encode("utf-16-le"))
        enc = "utf-16-le"
    UNDO.write_text(json.dumps({
        "id": wid, "name": name, "flavor": flavor, "encoding": enc,
        "hits": [f"{a:X}" for a in hits],
    }, indent=1), encoding="utf8")
    print(f"{name}: scanned {scanned >> 20}MB private RW, {len(hits)} copies ({enc})")
    for i, a in enumerate(hits):
        print(f"  [{i}] {a:#014x}")
    if not hits:
        print("no copies found; open the weapon's equip card first, then re-scan")


def cmd_poke(which, color="80"):
    st = json.loads(UNDO.read_text(encoding="utf8"))
    new = tagged_line(st["flavor"], color).encode(st["encoding"])
    idxs = range(len(st["hits"])) if which == "all" else [int(which)]
    wrote = 0
    for i in idxs:
        a = int(st["hits"][i], 16)
        if bc.wpm(a, new):
            wrote += 1
        else:
            print(f"  [{i}] {a:#014x} WRITE FAILED")
    print(f"poked {wrote} cop{'ies' if wrote != 1 else 'y'} with color {color}; "
          f"leave and reopen the equip list, then read the card")
    print("  colored tail = card parses markup; literal <color=..> = it does not")


def cmd_restore():
    st = json.loads(UNDO.read_text(encoding="utf8"))
    orig = st["flavor"].encode(st["encoding"])
    wrote = sum(1 for hx in st["hits"] if bc.wpm(int(hx, 16), orig))
    print(f"restored {wrote}/{len(st['hits'])} copies")


if __name__ == "__main__":
    mode = sys.argv[1] if len(sys.argv) > 1 else ""
    if mode == "scan" and len(sys.argv) > 2:
        cmd_scan(sys.argv[2])
    elif mode == "poke" and len(sys.argv) > 2:
        cmd_poke(*sys.argv[2:4])
    elif mode == "restore":
        cmd_restore()
    else:
        print(__doc__)
