"""RE-FIND THE STATUS APPLY ENGINE (LW-122).

The DLL's hardened cold call refused with a prologue mismatch at 0x150BF66DC on 2026-07-22. The
guard behaved exactly as designed (a stale address became a logged refusal instead of an
uncatchable AV), so the question is only where the routine lives NOW.

TWO HYPOTHESES, and they want different fixes:
  A. The game was patched and the routine moved once. Fix: re-find and re-pin the constant.
  B. The address is in a DYNAMICALLY MAPPED region and changes per launch. The target sits ABOVE
     the fixed main image (0x140000000), which the spike's own comment already flagged, and the
     1.5.1 re-anchor notes record this whole address class as the one that does not hold still.
     Fix: the DLL must FIND it at runtime, not carry a constant.

`peek` distinguishes them cheaply: run it twice across two game launches. Same bytes at the same
address = A. Different, or unmapped = B.

The known signature is the routine's prologue, `48 89 5C 24 08 48 89 6C 24 10` (mov [rsp+8],rbx;
mov [rsp+0x10],rbp). That is a COMMON x64 frame setup, so `scan` will find many candidates and a
hit alone proves nothing: it narrows the field for a human, it does not pick a winner. Do not
cold-call a scan hit.

The safer lead is `spring`: 0x1401FB064 lives in the FIXED main image and the spike's own notes
call it the inner dispatch that jumps into the VM. If it is a jmp/call thunk, its target is the
current routine, resolved fresh every launch, which is exactly what hypothesis B needs.

    python tools\\probes\\apply_engine_find.py peek     # what is at the old address right now
    python tools\\probes\\apply_engine_find.py spring   # decode the fixed-address dispatch thunk
    python tools\\probes\\apply_engine_find.py scan     # prologue candidates in the high region
"""
import pathlib
import struct
import sys

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
from battle_cheats import rpm, _require_game

OLD_APPLY = 0x150BF66DC     # what the DLL still carries
DISPATCH = 0x1401FB064      # fixed-image inner dispatch (the spike's documented VM springboard)
PROLOGUE = bytes([0x48, 0x89, 0x5C, 0x24, 0x08, 0x48, 0x89, 0x6C, 0x24, 0x10])


def cmd_peek():
    for name, addr in (("old apply target", OLD_APPLY), ("fixed dispatch", DISPATCH)):
        b = rpm(addr, 24)
        if b is None:
            print(f"{name} {addr:#x}: NOT MAPPED this launch")
            continue
        match = "MATCHES the known prologue" if b[:10] == PROLOGUE else "does NOT match the prologue"
        print(f"{name} {addr:#x}: {b.hex(' ')}")
        print(f"    -> {match}")


def cmd_spring():
    """Decode the fixed dispatch as a jump thunk. E9 = rel32 jmp, FF 25 = jmp [rip+rel32]."""
    b = rpm(DISPATCH, 16)
    if b is None:
        print(f"{DISPATCH:#x} is not mapped."); return
    print(f"{DISPATCH:#x}: {b.hex(' ')}")
    if b[0] == 0xE9:
        rel = struct.unpack("<i", b[1:5])[0]
        tgt = DISPATCH + 5 + rel
        print(f"  E9 rel32 -> jmp {tgt:#x}")
        t = rpm(tgt, 16)
        print(f"  target bytes: {t.hex(' ') if t else 'unmapped'}"
              f"{'  <-- PROLOGUE MATCH' if t and t[:10] == PROLOGUE else ''}")
    elif b[0] == 0xFF and b[1] == 0x25:
        rel = struct.unpack("<i", b[2:6])[0]
        slot = DISPATCH + 6 + rel
        ptr = rpm(slot, 8)
        tgt = struct.unpack("<Q", ptr)[0] if ptr else None
        print(f"  FF25 rip-relative -> pointer slot {slot:#x} -> {tgt:#x}" if tgt else "  pointer unreadable")
        if tgt:
            t = rpm(tgt, 16)
            print(f"  target bytes: {t.hex(' ') if t else 'unmapped'}"
                  f"{'  <-- PROLOGUE MATCH' if t and t[:10] == PROLOGUE else ''}")
            print(f"  NOTE: an indirect slot is the good case for hypothesis B, because the DLL can "
                  f"read {slot:#x} at runtime and never carry a stale constant again.")
    else:
        print("  not a simple jmp thunk; this needs a disassembler rather than a byte peek.")


def cmd_scan(span=0x400000):
    """Prologue candidates in a window around the old address. A hit is a LEAD, not an answer:
    this byte pattern is the standard x64 frame setup and appears in thousands of functions."""
    base = OLD_APPLY - span // 2
    print(f"scanning {base:#x}..{base + span:#x} for the prologue (leads only, never call one)")
    hits, step = [], 0x10000
    for off in range(0, span, step):
        buf = rpm(base + off, step + len(PROLOGUE))
        if not buf:
            continue
        i = buf.find(PROLOGUE)
        while i != -1:
            hits.append(base + off + i)
            i = buf.find(PROLOGUE, i + 1)
        if len(hits) > 60:
            print("  over 60 hits; the pattern is too common to narrow this way. Stopping.")
            break
    print(f"  {len(hits)} candidate(s)" + (f", first few: {[hex(h) for h in hits[:8]]}" if hits else ""))
    if OLD_APPLY in hits:
        print(f"  NOTE: the old address {OLD_APPLY:#x} IS among the hits, so it is mapped and "
              f"prologue-shaped; the DLL's refusal would then mean a read failure, not a mismatch.")


def main():
    _require_game()
    a = sys.argv[1:]
    if a and a[0] == "peek":
        cmd_peek()
    elif a and a[0] == "spring":
        cmd_spring()
    elif a and a[0] == "scan":
        cmd_scan(int(a[1], 0) if len(a) > 1 else 0x400000)
    else:
        print(__doc__)


if __name__ == "__main__":
    main()
