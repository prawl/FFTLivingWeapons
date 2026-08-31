#!/usr/bin/env python
"""LW-368 round 2 premise probe: relocate the game's two per-item byte lists LIVE and watch the game.

Plain language: the game keeps "how many of each item you own" as one byte per item in a fixed
block that ends exactly where Ramza's roster row begins (ids 0..271 fit; extended item twelve
would write into Ramza). The only way past eleven extended items is to move that block (and its
sibling flag block right below the u16 list) into memory the mod owns and re-point every place in
the game's code that names the old block. This probe does that from OUTSIDE the game, the way a
debugger would, so the premise ("the game keeps working with the lists relocated") is proven or
refuted before any of it is built into the DLL.

What it finds (read-only `--scan`): every reference to the two list bases in the game's code,
in the three shapes the LW-351 sweeps taught us matter:
  rip   `lea/mov/cmp ... [rip+disp32]` whose target is the list base (the xref sweep's form)
  rva   `[reg+reg+0x011A7C00]` / `[reg+0x011A7C00]`: the disp32 is the list's RVA and the
        base register holds the image base (the reset routine 0x140284500 uses this form; the
        rip sweep is blind to it, found 2026-08-31 by disassembling the routine)
  imm   the RVA or the full address as an immediate (`mov eax, 0x11A7C00`, `mov rax, imm64`)
Each raw byte hit is classified with capstone's operand details (the displacement/immediate
field must sit exactly at the hit); an unclassifiable hit is listed and `--apply` REFUSES.

What `--apply` does: allocates a 64 KB page just BELOW the image base (inside -2 GB of it; the
2 GB above are one reservation the game's arena owns; every site shape sign-extends its field,
see alloc_page_below_image), copies the live 0x110 bytes of each list into it (count list at
+0x000 with 0x400 bytes of room, sibling list at +0x400), rewrites every site's field through
VirtualProtectEx + WriteProcessMemory, verifies each by read-back, and writes
tools/probes/lw368_count_list_relocate_undo.json. `--undo` copies the page's first 0x110 bytes
of each list BACK into the old blocks (so nothing the player did in between is lost) and then
restores every site's original bytes. `--verify` reports each site's current state.

KNOWN LIMITS (deliberate for a probe): LivingWeapon.dll keeps using the OLD block
(Offsets.BagCountArray) for its own extended-count replay and reset hook, so during the probe
the extended items' counts are whatever was copied at apply time; the owner's check is about
VANILLA items (buy, drop, equip, save, load, the Poacher's Den) holding correct counts. The
copy-protected code region is swept and patched too (its pages read fine once a save has loaded);
a reference the sweeps cannot see (an obfuscated pointer) shows up as a vanilla count going
wrong, which is exactly what the probe exists to catch. Apply on a quiet screen (world map).

RESULTS: two-list apply 2026-08-31 ~02:35 (count + sibling, 45 fields): the owner's five checks
held (counts, a purchase, equip/unequip, save and reload, the Den); undone cleanly (the .done
record). Three-list apply ~02:55 (u16 added, 63 fields, one in the copy-protected region): the
Den glance held, then the owner QUIT mid-battle at 03:11 to disable a monster mod, so the process
died with the fields patched and no undo ran (the *_threelist_crashed.json.done record); the u16
relocation is therefore INCONCLUSIVE and stays out of LW-368 round 2. Re-run with the u16 list only
when a fresh premise probe (battle, save, reload, Den) is planned for it.

Usage:  python tools/probes/lw368_count_list_relocate.py            # --scan (read-only)
        python tools/probes/lw368_count_list_relocate.py --apply
        python tools/probes/lw368_count_list_relocate.py --verify
        python tools/probes/lw368_count_list_relocate.py --undo
"""
import ctypes
import json
import struct
import sys
from ctypes import wintypes
from pathlib import Path

import numpy as np
from capstone import CS_ARCH_X86, CS_MODE_64, Cs
from capstone.x86 import X86_OP_IMM, X86_OP_MEM, X86_REG_RIP

sys.path.insert(0, str(Path(__file__).resolve().parent))
import code_patch as CP            # find_pid / open_proc / rpm / wpm_guarded (guarded cross-process writes)
import lw346_xref_scan as XS       # sections() / rd_big() (read-only section walk)

IMAGE_SCN_MEM_EXECUTE = 0x20000000


def write_ok(h, va, data):
    """code_patch.wpm_guarded signals failure by raising SystemExit; fold that into a bool."""
    try:
        CP.wpm_guarded(h, va, data)
        return True
    except SystemExit as e:
        print(f"  write failed at {va:#x}: {e}")
        return False


def read_or_none(h, va, n):
    try:
        return CP.rpm(h, va, n)
    except SystemExit:
        return None

IMAGE_BASE = 0x140000000
LISTS = {                          # name -> (live base, bytes the game means, offset inside the new page, room)
    "count":   (0x1411A7C00, 0x110, 0x000, 0x400),
    "sibling": (0x1411A7700, 0x110, 0x400, 0x400),
    # Round 2 plan review (2026-08-31): the u16 list has exactly 0x105 entries and ONE spare byte
    # before PoachStoreBase 0x1411A7A1B, so every extended id's word already sits inside the
    # Poacher's Den stores (id 261 -> keys 1/2; id 381 -> the third store). The reset routine's
    # seed loop writes 0x105+N words there and the Den memsets that follow re-zero the spill,
    # but the ids' own reads come back as carcass counts. Relocating it removes the overlap;
    # 0x20A live bytes are copied (the Den bytes past them are not this list's), room 0x400 words.
    "u16":     (0x1411A7810, 0x20A, 0x800, 0x800),
}
# Round 2b (2026-08-31): a reference may name an INTERIOR address of a list (the new-game seed
# stores starting counts at fixed ids: `mov byte [rip+d],1`, `mov dword [rip+d],imm32`), so the
# scan matches any target inside the list's live entries, records `off` = target - base and, for
# rip sites, `trail` = the immediate bytes after the field (next-ip = field + 4 + trail). The +0x108
# dword of each 0x110 block is a live game state variable, NOT a list entry: entries stop at 0x105.
LIST_ENTRIES = {"count": 0x105, "sibling": 0x105, "u16": 0x20A}
PAGE_BYTES = 0x10000
UNDO = Path(__file__).resolve().parent / "lw368_count_list_relocate_undo.json"

MEM_COMMIT = 0x1000
MEM_RESERVE = 0x2000
PAGE_READWRITE = 0x04
k32 = CP.k32
k32.VirtualAllocEx.restype = wintypes.LPVOID
k32.VirtualAllocEx.argtypes = [wintypes.HANDLE, wintypes.LPVOID, ctypes.c_size_t, wintypes.DWORD, wintypes.DWORD]

md = Cs(CS_ARCH_X86, CS_MODE_64)
md.detail = True


def find_pattern(buf, pat):
    p = np.frombuffer(pat, dtype=np.uint8)
    n = len(p)
    if len(buf) < n:
        return []
    m = np.ones(len(buf) - n + 1, dtype=bool)
    for i, b in enumerate(p):
        m &= buf[i:len(buf) - n + 1 + i] == b
    return [int(i) for i in np.nonzero(m)[0]]


def rip_hits(buf, base, target):
    """Positions p of a disp32 such that p+4(+0|+1|+4)+disp32 == target (the xref sweep's rule)."""
    if len(buf) < 8:
        return []
    d = buf[:-3].view(np.uint8)
    disp = (buf[0:len(buf) - 3].astype(np.uint32) | (buf[1:len(buf) - 2].astype(np.uint32) << 8)
            | (buf[2:len(buf) - 1].astype(np.uint32) << 16) | (buf[3:len(buf)].astype(np.uint32) << 24)).astype(np.int64)
    disp = np.where(disp >= 1 << 31, disp - (1 << 32), disp)
    pos = np.arange(len(disp), dtype=np.int64) + base
    out = set()
    for trail in (0, 1, 4):
        out.update(int(x) for x in np.nonzero(pos + 4 + trail + disp == target)[0])
    return sorted(out)


def rip_hits_range(buf, base, lo, hi):
    """Positions p of a disp32 such that p+4(+0|+1|+2|+4)+disp32 lands in [lo, hi)."""
    if len(buf) < 8:
        return []
    n = len(buf)
    disp = (buf[0:n - 3].astype(np.uint32) | (buf[1:n - 2].astype(np.uint32) << 8)
            | (buf[2:n - 1].astype(np.uint32) << 16) | (buf[3:n].astype(np.uint32) << 24)).astype(np.int64)
    disp = np.where(disp >= 1 << 31, disp - (1 << 32), disp)
    pos = np.arange(len(disp), dtype=np.int64) + base
    out = set()
    for trail in (0, 1, 2, 4):
        tgt = pos + 4 + trail + disp
        out.update(int(x) for x in np.nonzero((tgt >= lo) & (tgt < hi))[0])
    return sorted(out)


def rva_hits_range(buf, lo, hi):
    """Positions p of a little-endian u32 whose value lands in [lo, hi) (image-relative disp32)."""
    if len(buf) < 4:
        return []
    n = len(buf)
    u = (buf[0:n - 3].astype(np.uint32) | (buf[1:n - 2].astype(np.uint32) << 8)
         | (buf[2:n - 1].astype(np.uint32) << 16) | (buf[3:n].astype(np.uint32) << 24)).astype(np.int64)
    return [int(x) for x in np.nonzero((u >= lo) & (u < hi))[0]]


def classify(h, hit, lists):
    """The instruction owning the field at `hit`, or None. Returns a dict describing the site."""
    raw = read_or_none(h, hit - 8, 8 + 16)
    if raw is None or len(raw) < 24:
        return None
    # Longest decode first: a REX-prefixed `lea r9,[rip+d]` also decodes from its second byte as
    # `lea ecx,[rip+d]` with the same field position and the same end address, so both rewrite
    # identically; preferring the longer one just keeps the printed text honest.
    for back in range(8, 0, -1):
        start = hit - back
        code = raw[8 - back:]
        try:
            insn = next(md.disasm(code, start))
        except StopIteration:
            continue
        if insn.size <= back:
            continue
        # displacement field exactly at the hit. capstone 5 reports disp_size 2 for a rip-relative
        # store with a 0x66 operand-size prefix (`mov word ptr [rip+d], imm16`, the new-game seed's
        # word store at 0x140284306) although the field is four bytes, so the field's own bytes are
        # the check: they must decode to the displacement capstone resolved.
        if insn.disp_offset == back and struct.unpack_from("<i", raw, 8)[0] == next((op.mem.disp for op in insn.operands if op.type == X86_OP_MEM), None):
            for op in insn.operands:
                if op.type != X86_OP_MEM:
                    continue
                if op.mem.base == X86_REG_RIP:
                    target = insn.address + insn.size + op.mem.disp   # insn.size includes any trailing immediate
                    for name, (lbase, lbytes, _, _) in lists.items():
                        if lbase <= target < lbase + LIST_ENTRIES.get(name, lbytes):
                            return dict(addr=hit, start=start, size=insn.size, kind="rip", list=name,
                                        off=target - lbase, trail=insn.size - back - 4,
                                        text=f"{insn.mnemonic} {insn.op_str}")
                else:
                    for name, (lbase, lbytes, _, _) in lists.items():
                        rva = lbase - IMAGE_BASE
                        if rva <= op.mem.disp < rva + LIST_ENTRIES.get(name, lbytes):
                            return dict(addr=hit, start=start, size=insn.size, kind="rva", list=name,
                                        off=op.mem.disp - rva, trail=0, text=f"{insn.mnemonic} {insn.op_str}")
        if insn.imm_offset == back and insn.imm_size in (4, 8):
            for op in insn.operands:
                if op.type != X86_OP_IMM:
                    continue
                for name, (lbase, _, _, _) in lists.items():
                    if insn.imm_size == 4 and op.imm == lbase - IMAGE_BASE:
                        return dict(addr=hit, start=start, size=insn.size, kind="imm32", list=name,
                                    off=0, text=f"{insn.mnemonic} {insn.op_str}")
                    if insn.imm_size == 8 and op.imm == lbase:
                        return dict(addr=hit, start=start, size=insn.size, kind="imm64", list=name,
                                    off=0, text=f"{insn.mnemonic} {insn.op_str}")
    return None


def scan(h, verbose=True):
    sites, unclassified = [], []
    for name, base, size, chars in XS.sections(h):
        if not chars & IMAGE_SCN_MEM_EXECUTE:
            continue
        buf = np.frombuffer(XS.rd_big(h, base, size), dtype=np.uint8)
        raw_hits = set()
        for lname, (lbase, lbytes, _, _) in LISTS.items():
            entries = LIST_ENTRIES.get(lname, lbytes)
            for p in rip_hits_range(buf, base, lbase, lbase + entries):
                raw_hits.add(base + p)
            for p in rva_hits_range(buf, lbase - IMAGE_BASE, lbase - IMAGE_BASE + entries):
                raw_hits.add(base + p)
            for p in find_pattern(buf, struct.pack("<Q", lbase)):
                raw_hits.add(base + p)
        for hit in sorted(raw_hits):
            site = classify(h, hit, LISTS)
            if site is None:
                unclassified.append((name, hit))
            else:
                site["section"] = name
                sites.append(site)
    if verbose:
        print(f"{len(sites)} classified site(s), {len(unclassified)} unclassified raw hit(s)")
        for s in sites:
            print(f"  {s['section']:<6} {s['list']:<7} {s['kind']:<5} field@{s['addr']:#x} off+{s['off']:#x} trail{s.get('trail',0)}  {s['start']:#x}: {s['text']}")
        for name, hit in unclassified:
            print(f"  UNCLASSIFIED {name} raw hit at {hit:#x} (a coincidental byte pattern, or a shape this probe does not know)")
    return sites, unclassified


def alloc_page_below_image(h):
    """A 64 KB page BELOW the image base, inside -2 GB of it. Below because the whole 2 GB above
    the image is one reservation the game's own arena owns (VirtualQueryEx 2026-08-31: one
    allocation base from 0x158D80000 to the end of the window). Below is safe for every site
    shape: rip disp32 is signed, and the image-relative forms are `[idx + r8 + disp32]` with r8
    loaded as the image base (`lea r8,[rip-0x281692]` right before each one) and a SIGN-extended
    disp32, so a negative RVA lands exactly on the page. (A first draft feared two sites were
    `add eax, imm32`; that was the classifier's short decode of an ordinary 8-byte load.)"""
    for cand in range(IMAGE_BASE - 0x100000, IMAGE_BASE - 0x7FF00000, -0x10000):
        got = k32.VirtualAllocEx(h, ctypes.c_void_p(cand), PAGE_BYTES, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE)
        if got:
            return int(got)
    raise SystemExit("no free 64 KB page below the image within 2 GB")


def new_field_bytes(site, page):
    lbase, _, poff, _ = LISTS[site["list"]]
    new_target = page + poff + site["off"]
    if site["kind"] == "rip":
        disp = new_target - (site["start"] + site["size"])   # == new_target - (field + 4 + trail)
        assert -(1 << 31) <= disp < (1 << 31)
        return struct.pack("<i", disp)
    if site["kind"] in ("rva", "imm32"):
        rva = new_target - IMAGE_BASE
        assert -(1 << 31) <= rva < (1 << 31)
        return struct.pack("<i", rva)
    if site["kind"] == "imm64":
        return struct.pack("<Q", new_target)
    raise ValueError(site["kind"])


def apply(h):
    if UNDO.exists():
        raise SystemExit(f"{UNDO.name} exists: undo (or delete it) before applying again")
    sites, unclassified = scan(h)
    if unclassified and "--allow-unclassified" not in sys.argv:
        raise SystemExit("REFUSING: unclassified raw hits above must be explained first "
                         "(--allow-unclassified once they are: they stay UNPATCHED and keep reading the old lists)")
    if unclassified:
        print(f"  proceeding with {len(unclassified)} unclassified hit(s) left UNPATCHED (owner-acknowledged residual readers)")
    if not sites:
        raise SystemExit("REFUSING: no sites found (wrong process?)")
    page = alloc_page_below_image(h)
    print(f"page allocated at {page:#x} (rva {page - IMAGE_BASE:#x})")
    for lname, (lbase, size, poff, room) in LISTS.items():
        live = read_or_none(h, lbase, size)
        if live is None or len(live) != size:
            raise SystemExit(f"cannot read the live {lname} list")
        if not write_ok(h, page + poff, bytes(live) + b"\0" * (room - size)):
            raise SystemExit(f"cannot seed the {lname} list into the page")
        print(f"  {lname}: {size:#x} live bytes copied to {page + poff:#x} (nonzero entries: {sum(1 for b in live if b)})")
    record = {"page": page, "sites": []}
    for s in sites:
        n = 8 if s["kind"] == "imm64" else 4
        old = CP.rpm(h, s["addr"], n)
        new = new_field_bytes(s, page)
        if not write_ok(h, s["addr"], new):
            print(f"  FAILED to write {s['addr']:#x}; undoing what landed")
            record["sites"].append({"addr": s["addr"], "old": bytes(old).hex(), "new": new.hex(), "landed": False})
            UNDO.write_text(json.dumps(record, indent=1))
            undo(h)
            raise SystemExit("apply aborted")
        back = CP.rpm(h, s["addr"], n)
        ok = bytes(back) == new
        record["sites"].append({"addr": s["addr"], "old": bytes(old).hex(), "new": new.hex(), "landed": ok,
                                "kind": s["kind"], "list": s["list"], "text": s["text"]})
        print(f"  {'ok ' if ok else 'BAD'} {s['addr']:#x} {s['kind']:<5} {s['list']:<7} {bytes(old).hex()} -> {new.hex()}   {s['text']}")
    UNDO.write_text(json.dumps(record, indent=1))
    print(f"applied {len(sites)} site(s); undo record at {UNDO}")


def undo(h):
    if not UNDO.exists():
        raise SystemExit("nothing to undo")
    record = json.loads(UNDO.read_text())
    page = record["page"]
    for lname, (lbase, size, poff, _) in LISTS.items():
        cur = read_or_none(h, page + poff, size)
        if cur is not None and len(cur) == size:
            write_ok(h, lbase, bytes(cur))
            print(f"  {lname}: {size:#x} bytes copied back from the page into {lbase:#x}")
    bad = 0
    for s in record["sites"]:
        old = bytes.fromhex(s["old"])
        if not write_ok(h, s["addr"], old):
            bad += 1
            print(f"  FAILED to restore {s['addr']:#x}")
            continue
        back = CP.rpm(h, s["addr"], len(old))
        if bytes(back) != old:
            bad += 1
            print(f"  MISMATCH after restore at {s['addr']:#x}")
    if bad:
        raise SystemExit(f"{bad} site(s) not restored; the undo record is kept")
    UNDO.rename(UNDO.with_suffix(".json.done"))
    print(f"restored {len(record['sites'])} site(s); the page stays allocated (harmless) and the record is renamed .done")


def verify(h):
    if not UNDO.exists():
        raise SystemExit("no undo record: nothing applied")
    record = json.loads(UNDO.read_text())
    for s in record["sites"]:
        n = len(bytes.fromhex(s["new"]))
        cur = bytes(CP.rpm(h, s["addr"], n)).hex()
        state = "NEW" if cur == s["new"] else ("old" if cur == s["old"] else "OTHER")
        print(f"  {s['addr']:#x} {state}  {s.get('text', '')}")


def main():
    pid = CP.find_pid()
    h = CP.open_proc(pid)
    if "--apply" in sys.argv:
        apply(h)
    elif "--undo" in sys.argv:
        undo(h)
    elif "--verify" in sys.argv:
        verify(h)
    else:
        scan(h)


if __name__ == "__main__":
    main()
