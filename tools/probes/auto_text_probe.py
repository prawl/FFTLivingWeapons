#!/usr/bin/env python
"""Find which resident copy of the "Auto" tag the renderer actually reads.

CONTEXT (2026-08-24, title-display planning). Auto-battle stamps "Auto" above the unit and on
its status card. The future titles feature ("Mage Slayer") wants to know where that text
lives and how many characters the surface tolerates. A CE scan for the string produced 279
hits (tools/addrs.txt); mass-poking them crashed the game, which is what happens when a hit
sits in code or in a length-prefixed structure rather than in plain string data.

METHOD, cheapest first:
  classify   read-only: 96 bytes of context around every hit, bucket them:
               STANDALONE  "Auto" + NUL in a data heap, string-like neighborhood (poke these)
               WORD        "Auto" is a prefix of a longer identifier (AutoSave...) (never poke)
               BINARY      surrounded by non-text bytes (render/glyph/code copies) (never poke)
  poke A     write "Bubb" (same length, NUL kept) at one shortlisted addr, banking the
             original bytes in auto_text_poke_undo.json first; look at the screen.
  bisect N   poke the Nth half-split of the STANDALONE shortlist in one go (same-length,
             string sites only, all banked); halves the search in one look.
  undo       restore every banked byte.

The screen tells the verdict: the copy that changes the visible "Auto" is the magic one.
Capacity question after that: read what follows the winner (slack NULs vs a packed
neighbor) before dreaming an 11-char title into a 4-char hole. The length-unbound path
remains the FnSetTextString-family swap the mod already ships for the facing prompt.
"""
import json
import pathlib
import string
import sys

HERE = pathlib.Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))
import battle_cheats as bc

ADDRS_FILE = HERE.parents[1] / "tools" / "addrs.txt"
UNDO_FILE = HERE / "auto_text_poke_undo.json"
POKE = b"Bubb"          # same length as "Auto"; distinctive on screen
CTX = 48                # bytes of context either side

PRINTABLE = set(string.printable.encode()) - set(b"\x0b\x0c")


def load_addrs():
    out = []
    for line in ADDRS_FILE.read_text().splitlines():
        line = line.strip()
        if line:
            out.append(int(line, 16))
    return out


def printable_run(buf, start):
    """Length of the NUL-terminated printable ASCII run starting at buf[start]."""
    i = start
    while i < len(buf) and buf[i] in PRINTABLE and buf[i] != 0:
        i += 1
    return i - start


def neighborhood_text(buf):
    """The printable strings visible in the context window, for the human read."""
    frags, cur = [], []
    for b in buf:
        if b in PRINTABLE and b != 0:
            cur.append(chr(b))
        else:
            if len(cur) >= 3:
                frags.append("".join(cur))
            cur = []
    if len(cur) >= 3:
        frags.append("".join(cur))
    return " | ".join(frags[:8])


def classify_one(addr):
    buf = bc.rpm(addr - CTX, CTX * 2 + 4)
    if buf is None:
        return ("UNREADABLE", "", "")
    hit = buf[CTX:CTX + 4]
    if hit != b"Auto":
        return ("MOVED", hit.decode("latin1"), neighborhood_text(buf))
    before = buf[CTX - 1]
    if chr(before).isalnum():
        # the tail of a longer identifier (TextAuto, ShowAuto, HideAuto...):
        # a NODE NAME, not text content; poking these is the crash lane.
        j = CTX
        while j > 0 and chr(buf[j - 1]).isalnum():
            j -= 1
        run = printable_run(buf, j)
        return ("WORD", buf[j:j + run].decode("latin1"), neighborhood_text(buf))
    after = buf[CTX + 4]
    if after != 0 and after in PRINTABLE:
        run = printable_run(buf, CTX)
        word = buf[CTX:CTX + run].decode("latin1")
        return ("WORD", word, neighborhood_text(buf))
    window = buf[:CTX] + buf[CTX + 5:]
    texty = sum(1 for b in window if b in PRINTABLE or b == 0)
    verdict = "STANDALONE" if texty / len(window) >= 0.55 else "BINARY"
    return (verdict, "Auto", neighborhood_text(buf))


def cmd_classify():
    bc._require_game()
    buckets = {}
    rows = []
    for a in load_addrs():
        verdict, word, ctx = classify_one(a)
        buckets.setdefault(verdict, []).append(a)
        rows.append((verdict, a, word, ctx))
    order = ["STANDALONE", "WORD", "BINARY", "MOVED", "UNREADABLE"]
    for v in order:
        if v not in buckets:
            continue
        print(f"\n=== {v} ({len(buckets[v])}) ===")
        for verdict, a, word, ctx in rows:
            if verdict != v:
                continue
            if v == "WORD":
                print(f"  {a:#014x}  {word}")
            else:
                print(f"  {a:#014x}  ctx: {ctx[:150]}")
    short = buckets.get("STANDALONE", [])
    print(f"\nshortlist (STANDALONE): {len(short)} of {len(rows)} hits")
    print("next: poke one (`poke <addr>`), or `bisect 0` to test the first half at once")


def cmd_scan():
    """Re-find the bare Auto holders after a restart: walk every private RW region for
    the 16-byte slot shape (Auto + NUL + 11 zero bytes) and refresh tools/addrs.txt.
    Read-only. The 12-zero tail excludes packed name tables (TextAuto etc.) outright."""
    import ctypes
    bc._require_game()
    h = bc._handle()

    class MBI(ctypes.Structure):
        _fields_ = [("BaseAddress", ctypes.c_ulonglong),
                    ("AllocationBase", ctypes.c_ulonglong),
                    ("AllocationProtect", ctypes.c_ulong),
                    ("_a1", ctypes.c_ulong),
                    ("RegionSize", ctypes.c_ulonglong),
                    ("State", ctypes.c_ulong),
                    ("Protect", ctypes.c_ulong),
                    ("Type", ctypes.c_ulong),
                    ("_a2", ctypes.c_ulong)]

    MEM_COMMIT, MEM_PRIVATE, PAGE_RW, PAGE_GUARD = 0x1000, 0x20000, 0x04, 0x100
    pattern = b"Auto" + b"\x00" * 12
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
                buf = bc.rpm(base + off, min(CHUNK + len(pattern), size - off))
                if buf:
                    i = buf.find(pattern)
                    while i != -1:
                        found.append(base + off + i)
                        i = buf.find(pattern, i + 1)
                off += CHUNK
        addr = base + size
    ADDRS_FILE.write_text("\n".join(f"{a:X}" for a in found) + "\n")
    print(f"scanned {scanned // (1 << 20)}MB private RW; {len(found)} bare holders -> {ADDRS_FILE.name}")
    for a in found:
        print(f"  {a:#014x}")


def bank_and_write(targets):
    undo = {}
    if UNDO_FILE.exists():
        undo = json.loads(UNDO_FILE.read_text())
    for a in targets:
        orig = bc.rpm(a, len(POKE))
        if orig is None:
            print(f"  {a:#014x} unreadable, skipped")
            continue
        undo.setdefault(f"{a:#x}", orig.hex())     # first bank wins; re-poke keeps the true original
        if bc.wpm(a, POKE):
            print(f"  {a:#014x} <- {POKE.decode()}")
        else:
            print(f"  {a:#014x} write REFUSED")
    UNDO_FILE.write_text(json.dumps(undo, indent=1))
    print(f"undo bytes banked in {UNDO_FILE.name}")


def shortlist():
    bc._require_game()
    return [a for a in load_addrs() if classify_one(a)[0] == "STANDALONE"]


def cmd_poke(addr):
    bank_and_write([addr])


def cmd_bisect(which):
    s = shortlist()
    half = s[: len(s) // 2] if which == 0 else s[len(s) // 2:]
    print(f"poking {'first' if which == 0 else 'second'} half: {len(half)} of {len(s)}")
    bank_and_write(half)


def bare_holders():
    """The live text-holder candidates: standalone Auto with NOTHING else printable near it."""
    bc._require_game()
    out = []
    for a in load_addrs():
        verdict, _, ctx = classify_one(a)
        if verdict == "STANDALONE" and ctx == "Auto":
            out.append(a)
    return out


def cmd_pokeall():
    """One distinct marker per bare holder; one look at the screen maps every surface."""
    targets = bare_holders()
    print(f"{len(targets)} bare holders; slack = zero bytes after the terminator (rough capacity)")
    marked = []
    for n, a in enumerate(targets, 1):
        tail = bc.rpm(a + 5, 64) or b""
        slack = next((i for i, b in enumerate(tail) if b != 0), len(tail))
        marker = f"LW{n:02d}".encode()
        marked.append((a, marker, slack))
    bank_and_write([a for a, _, _ in marked])  # banks originals; then stamp markers
    for a, marker, slack in marked:
        bc.wpm(a, marker)
        print(f"  {a:#014x} = {marker.decode()}   slack after NUL: {slack}+ bytes")
    print("look at the screen: whichever marker shows above the head / timeline / card names")
    print("its holder. Then `undo` restores every byte.")


def cmd_snap(label):
    """Bank every valid seat's full combat struct, for the auto-flag diff.

    Protocol: pause the battle, `snap a`, toggle Auto on ONE unit, `snap b`,
    then `flagdiff a b`. Pausing freezes CT so the diff is nearly silent; the
    bytes that changed ONLY on the toggled seat are the auto-battle flag
    candidates (state bit, and possibly a separate tag-display bit beside it).
    """
    bc._require_game()
    seats = {}
    for s in range(bc.BAND_SLOTS):
        entry = bc._band_entry_addr(s)
        if not bc._is_valid_entry(entry):
            continue
        combat = entry - 0x1C
        buf = bc.rpm(combat, bc.COMBAT_STRIDE)
        if buf is not None:
            seats[str(s)] = {"combat": f"{combat:#x}", "bytes": buf.hex()}
    out = HERE / f"auto_text_snap_{label}.json"
    out.write_text(json.dumps(seats))
    print(f"{len(seats)} seats banked -> {out.name}")


def cmd_flagdiff(la, lb):
    a = json.loads((HERE / f"auto_text_snap_{la}.json").read_text())
    b = json.loads((HERE / f"auto_text_snap_{lb}.json").read_text())
    per_seat = {}
    for s in sorted(set(a) & set(b), key=int):
        ba, bb = bytes.fromhex(a[s]["bytes"]), bytes.fromhex(b[s]["bytes"])
        per_seat[s] = [(i, ba[i], bb[i]) for i in range(min(len(ba), len(bb))) if ba[i] != bb[i]]
    noisy = set()
    for s, diffs in per_seat.items():
        for i, _, _ in diffs:
            if sum(1 for d in per_seat.values() if any(x[0] == i for x in d)) > len(per_seat) // 2:
                noisy.add(i)
    for s, diffs in per_seat.items():
        quiet = [(i, x, y) for i, x, y in diffs if i not in noisy]
        if quiet:
            print(f"seat {s} (combat {a[s]['combat']}):")
            for i, x, y in quiet:
                print(f"  combat+{i:#05x}: {x:#04x} -> {y:#04x}   (band-relative +{i - 0x1C:#x})")
    if noisy:
        print(f"suppressed churn offsets (changed on most seats): {sorted(f'{i:#x}' for i in noisy)}")


def cmd_autoflag(seat, state):
    """Drive the vanilla auto-battle byte; behaviour, tag, AND a text re-render follow.

    Owner-isolated in CE 2026-08-24 (FFT_enhanced.exe+1855ECC = band slot 0): the byte is
    combat+0x1EC per unit. 0 = manual (tag hidden), 12 (0x0C) = auto on (tag shown, AI
    acts). Writing it forces the tag text to be re-set from its source and re-rendered,
    which nothing else did; ledger row [auto-battle-mode-byte]. Whether auto-battle
    instruction modes encode other values is unchecked. Related but different: combat+0x05
    bit 0x08 is the manual-control flag Dicene's fftivc.handsfree clears (roster mirror
    partyUnit+0x04), re-clearing in CopyUnitToBattleUnit / CopyJobEffectsToUnit /
    set_status_all because those are the game's re-stamp points.
    """
    bc._require_game()
    entry = bc._band_entry_addr(seat)
    if not bc._is_valid_entry(entry):
        print(f"seat {seat}: no valid unit")
        return
    addr = entry - 0x1C + 0x1EC
    val = bc.ru8(addr)
    if val is None:
        print("auto byte unreadable")
        return
    new = 12 if state == "on" else (0 if state == "off" else int(state, 0))
    bc.wu8(addr, new)
    print(f"seat {seat} auto byte {addr:#x}: {val:#04x} -> {new:#04x}")


def cmd_check():
    """Re-read every poked holder: a marker CLOBBERED back to 'Auto' names the live one.

    The tag text is rendered to glyphs at set-time, so an in-place holder edit shows
    nothing until the game re-sets the text (toggle auto off/on). At that moment the
    live holder is rewritten from the source string, erasing our marker; the spares
    keep theirs. The erased holder is the magic address.
    """
    bc._require_game()
    if not UNDO_FILE.exists():
        print("nothing poked")
        return
    undo = json.loads(UNDO_FILE.read_text())
    for k in undo:
        a = int(k, 16)
        cur = bc.rpm(a, 16)
        if cur is None:
            print(f"  {a:#014x}  UNREADABLE (holder freed?)")
            continue
        s = cur.split(b"\x00")[0].decode("latin1", "replace")
        note = "  <-- REWRITTEN by the game (live holder)" if not s.startswith(("LW", "Bubb")) else ""
        print(f"  {a:#014x}  '{s}'{note}")


def cmd_hold(addr, text, seconds=30.0):
    """Ambush the set->render gap: spam-write a title into a holder until the glyphs
    bake from OUR text. The byte flip only toggles visibility (owner-observed: the tag
    blinks with no holder rewrite); the text re-set rides the MENU toggle / plate
    rebuild, so hold the string down while that happens. 16-byte slot, so 15 chars max."""
    import time
    bc._require_game()
    raw = text.encode("utf-8")
    if len(raw) > 15:
        print(f"'{text}' is {len(raw)} bytes; the slot holds 15 + NUL. Truncating.")
        raw = raw[:15]
    payload = raw + b"\x00" * (16 - len(raw))
    orig = bc.rpm(addr, 16)
    print(f"holding '{text}' at {addr:#x} for {seconds:.0f}s; toggle auto via the MENU now")
    t0, n = time.time(), 0
    while time.time() - t0 < seconds:
        bc.wpm(addr, payload)
        n += 1
    print(f"{n} writes. Holder now: {(bc.rpm(addr, 16) or b'').split(b'\\x00')[0]!r}")
    if orig is not None:
        print(f"(original bytes NOT restored; `undo` has the Auto originals, or restore manually: {orig.hex()})")


def cmd_undo():
    if not UNDO_FILE.exists():
        print("nothing banked")
        return
    undo = json.loads(UNDO_FILE.read_text())
    ok = 0
    for k, v in undo.items():
        if bc.wpm(int(k, 16), bytes.fromhex(v)):
            ok += 1
    print(f"restored {ok}/{len(undo)}")


def main():
    if len(sys.argv) < 2:
        print(__doc__)
        return
    verb = sys.argv[1]
    if verb == "classify":
        cmd_classify()
    elif verb == "scan":
        cmd_scan()
    elif verb == "poke":
        cmd_poke(int(sys.argv[2], 16))
    elif verb == "bisect":
        cmd_bisect(int(sys.argv[2]))
    elif verb == "pokeall":
        cmd_pokeall()
    elif verb == "autoflag":
        cmd_autoflag(int(sys.argv[2]), sys.argv[3])
    elif verb == "check":
        cmd_check()
    elif verb == "hold":
        cmd_hold(int(sys.argv[2], 16), sys.argv[3],
                 float(sys.argv[4]) if len(sys.argv) > 4 else 30.0)
    elif verb == "snap":
        cmd_snap(sys.argv[2])
    elif verb == "flagdiff":
        cmd_flagdiff(sys.argv[2], sys.argv[3])
    elif verb == "undo":
        cmd_undo()
    else:
        print(__doc__)


if __name__ == "__main__":
    main()
