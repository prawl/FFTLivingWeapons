#!/usr/bin/env python
"""Offline re-anchor of RUNTIME globals by RIP-relative cross-reference between two exes.

WHY THIS EXISTS (2026-07-24, the 1.5.2 re-anchor). The exe on disk cannot show what a runtime
global CONTAINS -- roster rows, combat bands and battle flags all live in zero-initialized image
space and hold nothing until the game runs. That is why docs/PATCH_REANCHOR.md Phase B is a live
session. But the exe DOES show what the code SAYS about them: every one of those globals is reached
by a RIP-relative instruction whose 32-bit displacement encodes the global's address. So the
address can be recovered from the instruction stream without the game running at all.

METHOD (per anchor):
  1. In the OLD exe, find every RIP-relative site whose computed target equals the old pinned
     address. Sites are found by treating each byte as a candidate modrm with mod=00 rm=101 and
     checking that VA(site) + 5 + disp32 lands exactly on the target, then requiring a plausible
     x86 opcode immediately before it.
  2. For each site, build a CONTEXT SIGNATURE: the surrounding bytes with the 4 displacement bytes
     punched out (they are exactly what changes when either the code or the global moves). Find
     that context in the NEW exe.
  3. Read the NEW displacement at the matched site and recompute the target. That is the anchor's
     new address, as the game's own code understands it.
  4. Require CONSENSUS: report the address only when independent sites agree. Disagreement is
     reported as such, never averaged.

WHAT THIS IS AND IS NOT. This is strong evidence about ADDRESSES, and it is blind to SEMANTICS. It
cannot tell you that a flag still MEANS what it used to mean (docs/PATCH_REANCHOR.md's 1.5.1 pause
lesson: an address survived while its meaning narrowed), and it cannot confirm a struct's shape.
Treat every row it prints as a Phase B candidate to be confirmed live, not as a finding. It exists
to turn a blank-page live hunt into a verification pass.

Usage:
    python tools/probes/rip_xref_reanchor.py [old_exe] [new_exe]
"""
import struct
import sys

import numpy as np

IMAGE_BASE = 0x140000000
DEFAULT_OLD = r"C:\Users\ptyRa\FFT_IC_backup_1.5.1\FFT_enhanced.exe"
DEFAULT_NEW = (r"C:\Program Files (x86)\Steam\steamapps\common"
               r"\FINAL FANTASY TACTICS - The Ivalice Chronicles\FFT_enhanced.exe")

# Opcodes that legitimately precede a mod=00 rm=101 modrm with NO trailing immediate, so that the
# instruction ends right after the disp32 and target == VA(modrm) + 5 + disp32. REX prefixes are
# handled by simply allowing the byte before the opcode to be anything.
PLAUSIBLE_OPCODES = {
    0x8B,  # mov r, r/m
    0x8D,  # lea
    0x89,  # mov r/m, r
    0x8A,  # mov r8, r/m8
    0x88,  # mov r/m8, r8
    0x63,  # movsxd
    0x03, 0x01, 0x2B, 0x29, 0x33, 0x31, 0x3B, 0x39, 0x85, 0x84,  # arith/test
    0x0B, 0x09, 0x23, 0x21,
    0xFF,  # inc/dec/call/jmp r/m
    0x38, 0x3A,  # cmp r/m8
}

# The mod's pinned runtime anchors: (label, old address, what it is).
ANCHORS = [
    ("Offsets.Slot0",        0x140782A30, "battle-phase word"),
    ("Offsets.Slot9",        0x140782A54, "battle sentinel"),
    ("Offsets.Acted",        0x140782A8C, "action-complete flag"),
    ("Offsets.EventId",      0x140782A94, "cutscene event id"),
    ("Offsets.TurnQueue",    0x1407832A0, "condensed active-unit struct"),
    ("Offsets.ActorPtr",     0x14186AF68, "pointer to acting unit's frame"),
    ("Offsets.ArrayBase",    0x140899F50, "static unit array"),
    ("Offsets.RosterBase",   0x1411A7D10, "roster table"),
    ("Offsets.InventoryCountBase", 0x1411A7C00, "inventory counts"),
    ("Offsets.CombatAnchor", 0x141855CE0, "combat band (Ramza slot)"),
    ("Offsets.BattleMode",   0x1409069A0, "in-battle discriminator"),
    ("Offsets.PauseFlag",    0x140C6B1C8, "status-card pause flag"),
    ("Offsets.SubmenuFlag",  0x140D4080C, "submenu flag (the 1.5.1 mover)"),
    ("Offsets.MirrorWeapon", 0x141876EB4, "equip-card weapon mirror"),
    ("Offsets.MirrorOffHand", 0x141876EB6, "equip-card off-hand mirror"),
    ("Offsets.WpScratch",    0x141876E96, "equip-card WP scratch"),
    ("Offsets.LiveBattleMapId", 0x140784478, "current map id"),
    ("Offsets.TerrainGrid",  0x140C6B440, "terrain records"),
    ("Offsets.MenuCursor",   0x1407FC620, "menu cursor (unused by gates)"),
]

CTX_BEFORE = 10   # context bytes kept before the modrm byte
CTX_AFTER = 10    # context bytes kept after the disp32


def load(path):
    data = open(path, "rb").read()
    e = struct.unpack_from("<I", data, 0x3C)[0]
    nsec = struct.unpack_from("<H", data, e + 6)[0]
    optsz = struct.unpack_from("<H", data, e + 20)[0]
    tbl = e + 24 + optsz
    secs = []
    for i in range(nsec):
        o = tbl + i * 40
        name = data[o:o + 8].rstrip(b"\x00").decode("ascii", "replace")
        vsize, va, rsize, rptr = struct.unpack_from("<IIII", data, o + 8)
        secs.append((name, va, vsize, rptr, rsize))
    return data, secs


def code_blob(data, secs):
    """The first section is the executable one in both builds (verified: same VA span)."""
    name, va, vsize, rptr, rsize = secs[0]
    return data[rptr:rptr + rsize], IMAGE_BASE + va


def site_targets(blob, base):
    """Vectorised: implied RIP target for every byte position treated as a modrm."""
    b = np.frombuffer(blob, dtype=np.uint8)
    n = len(b) - 5
    disp = (b[1:n + 1].astype(np.int64)
            | (b[2:n + 2].astype(np.int64) << 8)
            | (b[3:n + 3].astype(np.int64) << 16)
            | (b[4:n + 4].astype(np.int64) << 24))
    disp = np.where(disp >= 0x80000000, disp - 0x100000000, disp)
    va = base + np.arange(n, dtype=np.int64)
    return b, va + 5 + disp


def find_sites(blob, base, target, limit=40):
    b, targets = site_targets(blob, base)
    idx = np.nonzero(targets == target)[0]
    out = []
    for p in idx:
        p = int(p)
        if p < CTX_BEFORE or p + 5 + CTX_AFTER > len(blob):
            continue
        if (b[p] & 0xC7) != 0x05:          # mod=00, rm=101 -> RIP-relative
            continue
        op = int(b[p - 1])
        op2 = int(b[p - 2])
        if op not in PLAUSIBLE_OPCODES and not (op2 == 0x0F and op in (0xB6, 0xB7, 0xBE, 0xBF)):
            continue
        out.append(p)
        if len(out) >= limit:
            break
    return out


def context_of(blob, p):
    """Bytes around a site with the disp32 punched out: (prefix_incl_modrm, suffix)."""
    return blob[p - CTX_BEFORE:p + 1], blob[p + 5:p + 5 + CTX_AFTER]


def resolve_in_new(newblob, newbase, prefix, suffix, limit=8):
    """Find the same instruction in the new blob; return the targets its disp now encodes."""
    found, start = [], 0
    while len(found) < limit:
        i = newblob.find(prefix, start)
        if i < 0:
            break
        start = i + 1
        p = i + CTX_BEFORE
        if newblob[p + 5:p + 5 + CTX_AFTER] != suffix:
            continue
        disp = struct.unpack_from("<i", newblob, p + 1)[0]
        found.append((newbase + p, newbase + p + 5 + disp))
    return found


def main():
    old_path = sys.argv[1] if len(sys.argv) > 2 else DEFAULT_OLD
    new_path = sys.argv[2] if len(sys.argv) > 2 else DEFAULT_NEW
    odata, osecs = load(old_path)
    ndata, nsecs = load(new_path)
    oblob, obase = code_blob(odata, osecs)
    nblob, nbase = code_blob(ndata, nsecs)
    print("OLD %s" % old_path)
    print("NEW %s" % new_path)
    print("code section: old 0x%09X (%d bytes), new 0x%09X (%d bytes)\n"
          % (obase, len(oblob), nbase, len(nblob)))
    print("%-30s %-13s %-13s %s" % ("anchor", "old address", "new address", "evidence"))
    print("-" * 96)
    verdicts = []
    for label, addr, note in ANCHORS:
        sites = find_sites(oblob, obase, addr)
        if not sites:
            print("%-30s 0x%09X %-13s no RIP xref found in old code -- live re-find required"
                  % (label, addr, "?"))
            verdicts.append((label, addr, None, "no-xref"))
            continue
        tally = {}
        matched_sites = 0
        for p in sites:
            pre, suf = context_of(oblob, p)
            if len(set(pre)) < 4:
                continue
            hits = resolve_in_new(nblob, nbase, pre, suf)
            if len(hits) != 1:
                continue
            matched_sites += 1
            tally[hits[0][1]] = tally.get(hits[0][1], 0) + 1
        if not tally:
            print("%-30s 0x%09X %-13s %d old xref(s), none re-locatable -- live re-find required"
                  % (label, addr, "?", len(sites)))
            verdicts.append((label, addr, None, "not-relocatable"))
            continue
        best = max(tally.items(), key=lambda kv: kv[1])
        agree = best[1]
        total = sum(tally.values())
        if len(tally) > 1:
            detail = "SPLIT %s" % ", ".join("0x%09X x%d" % (k, v) for k, v in sorted(tally.items()))
            print("%-30s 0x%09X %-13s %s" % (label, addr, "?", detail))
            verdicts.append((label, addr, None, "split"))
            continue
        delta = best[0] - addr
        tag = "UNCHANGED" if delta == 0 else "MOVED %s0x%X" % ("-" if delta < 0 else "+", abs(delta))
        print("%-30s 0x%09X 0x%09X   %s (%d/%d xref sites agree)"
              % (label, addr, best[0], tag, agree, total))
        verdicts.append((label, addr, best[0], tag))
    print()
    moved = [v for v in verdicts if v[2] is not None and v[2] != v[1]]
    same = [v for v in verdicts if v[2] is not None and v[2] == v[1]]
    unknown = [v for v in verdicts if v[2] is None]
    print("SUMMARY: %d unchanged, %d moved, %d unresolved offline" % (len(same), len(moved), len(unknown)))
    for v in moved:
        print("   MOVED: %s 0x%09X -> 0x%09X" % (v[0], v[1], v[2]))
    for v in unknown:
        print("   UNRESOLVED (%s): %s 0x%09X" % (v[3], v[0], v[1]))
    return 0


if __name__ == "__main__":
    sys.exit(main())
