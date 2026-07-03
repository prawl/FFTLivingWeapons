#!/usr/bin/env python
"""Hunt the battle-callout bubble's LIFE TIMER (why the toast bubble tears down after ~1s)
and live-test duration levers. Two modes, RPM/WPM via the ct_probe scaffolding.

WATCH (default): resolve the callout controller from the static chain
[[[0x143CD9DA8]+0x10]+0x48]+0x58 (FOUR derefs -- ShowSpike.TryFirePending was normative
before the trim; docs/CALLOUT_BANNER_JOURNEY.md "The mechanism, fully specified"), then
sample three spans every ~10ms:
    ctrl+0x000..0x180   (type +0x50, embedded holder from +0x58, token copies +0x98/+0xC0,
                         SHOW FLAG +0xE0 == holder+0x88)
    objB+0x000..0x2C0   ([ctrl+0x40], the type-0 name bubble object: +0x1A0, +0x218 state,
                         +0x280)
    wdgt+0x000..0x140   ([holder+0x10] = [ctrl+0x68], the bound text widget: +0x34 bind
                         state, +0xC7 alpha, +0xCC; REBOUND per show, so re-resolved every
                         sample)
Every changed byte-run is timestamped and tagged in-show/out-of-show (show flag ctrl+0xE0);
the end summary ranks offsets by show-correlation -- the life timer is a field that ticks
ONLY while the bubble lives (or a countdown armed at show start).

HOLD (--hold span+off=val:width@secs): arm on the NEXT show-flag rise, then write+hold the
value every 15ms for the duration -- the per-unit STATE hold pattern every shipped signature
uses (poison pin, CT slam, StatHold). Example candidate test:
    python tools\\probes\\callout_life_probe.py --hold wdgt+0x38=0:4@6
Multiple --hold args allowed; all arm on the same rise. Fail-safe: writes stop at duration
end; nothing is patched (data writes only, no code).

Usage:
    python tools\\probes\\callout_life_probe.py [seconds=90]
    python tools\\probes\\callout_life_probe.py --hold objB+0x21C=13:4@6 [seconds=60]

Trigger recipe (dev build): in battle press F2 (queues a toast), then make any attack --
the piggyback delivers on that callout; natural callouts also exercise the same show."""
import struct
import sys
import time
import os

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from ct_probe import PROC, PV, PV_W, find_pid, k32, rd, wr

GSLOT = 0x143CD9DA8
CTRL_VTABLE = 0x140712990
CTRL_ID = 0x998
SHOW_FLAG_OFF = 0xE0            # ctrl+0xE0 == holder+0x88 (journey doc, controller layout)
SPAN_DEFS = (("ctrl", 0x180), ("objB", 0x2C0), ("wdgt", 0x140))
SAMPLE_S = 0.010
HOLD_TICK_S = 0.015
MAX_PRINTS_PER_OFFSET = 8       # after this, count silently (anti-spam for frame counters)


def u64(b, o=0):
    return struct.unpack_from("<Q", b, o)[0]


def rd8(h, a):
    b = rd(h, a, 8)
    return u64(b) if b else 0


def resolve(h):
    """Four-deref chain to the controller; returns (ctrl, objB, wdgt) or None."""
    g = rd8(h, GSLOT)
    p = rd8(h, g + 0x10) if g else 0
    subsys = rd8(h, p + 0x48) if p else 0
    ctrl = rd8(h, subsys + 0x58) if subsys else 0
    if not ctrl or rd8(h, ctrl) != CTRL_VTABLE or rd8(h, ctrl + 8) != CTRL_ID:
        return None
    objb = rd8(h, ctrl + 0x40)
    wdgt = rd8(h, ctrl + 0x68)  # [holder+0x10]; holder is embedded at ctrl+0x58
    return ctrl, objb, wdgt


def runs_of_change(old, new):
    """Yield (offset, old_bytes, new_bytes) for maximal changed runs."""
    i, n = 0, len(new)
    while i < n:
        if old[i] == new[i]:
            i += 1
            continue
        j = i
        while j < n and old[j] != new[j]:
            j += 1
        yield i, old[i:j], new[i:j]
        i = j


def interp(bs):
    """Human hints for a changed run: u32/float for width 4, u16 for 2."""
    if len(bs) == 4:
        u = struct.unpack("<I", bs)[0]
        f = struct.unpack("<f", bs)[0]
        hint = f"u32={u}"
        if u and 1e-6 < abs(f) < 1e6:
            hint += f" f={f:.4f}"
        return hint
    if len(bs) == 2:
        return f"u16={struct.unpack('<H', bs)[0]}"
    return ""


def parse_hold(spec):
    """span+off=val:width@secs -> (span, off, val, width, secs)"""
    loc, rest = spec.split("=", 1)
    span, off = loc.split("+", 1)
    if span not in ("ctrl", "objB", "wdgt"):
        sys.exit(f"bad hold span {span!r} (ctrl|objB|wdgt)")
    val_part, secs = rest.split("@", 1)
    val, width = (val_part.split(":", 1) + ["1"])[:2] if ":" in val_part else (val_part, "1")
    return span, int(off, 0), int(val, 0), int(width), float(secs)


def main():
    holds, secs, args = [], 90, sys.argv[1:]
    while args:
        a = args.pop(0)
        if a == "--hold":
            holds.append(parse_hold(args.pop(0)))
        else:
            secs = int(a)
    pid = find_pid(f"{PROC}.exe")
    if not pid:
        sys.exit(f"{PROC}.exe not running")
    h = k32.OpenProcess(PV_W if holds else PV, False, pid)

    bases = None
    while bases is None:
        bases = resolve(h)
        if bases is None:
            print("chain not resolved yet (controller absent?) -- retrying...")
            time.sleep(0.5)
    ctrl, objb, wdgt = bases
    print(f"ctrl=0x{ctrl:X} objB=0x{objb:X} wdgt=0x{wdgt:X}")
    if holds:
        print("HOLD MODE -- arming on the next show rise: " +
              ", ".join(f"{s}+0x{o:X}={v}:{w}@{d}s" for s, o, v, w, d in holds))

    last = {}
    counts = {}          # (span, off) -> [total, in_show]
    printed = {}
    show = False
    show_t0 = 0.0
    hold_until = 0.0
    t0 = time.time()
    print(f"watching for {secs}s -- F2 + attack in battle (or any natural callout)...")
    while time.time() - t0 < secs:
        t = time.time() - t0
        r = resolve(h)
        if r is None:
            time.sleep(0.1)
            continue
        ctrl, objb, wdgt = r
        addr = {"ctrl": ctrl, "objB": objb, "wdgt": wdgt}
        for span, size in SPAN_DEFS:
            base = addr[span]
            if not base:
                continue
            buf = rd(h, base, size)
            if buf is None:
                continue
            key = (span, base)
            if key not in last:
                # first sight of this instance (widget rebinds per show) -- baseline only
                stale = [k for k in last if k[0] == span and k != key]
                for k in stale:
                    del last[k]
                if stale:
                    print(f"  t={t:6.2f}s {span} rebound -> 0x{base:X} (re-baselined)")
                last[key] = buf
                continue
            old = last[key]
            if buf != old:
                for off, ob, nb in runs_of_change(old, buf):
                    ck = (span, off)
                    tot, ins = counts.get(ck, (0, 0))
                    counts[ck] = (tot + 1, ins + (1 if show else 0))
                    p = printed.get(ck, 0)
                    if p < MAX_PRINTS_PER_OFFSET:
                        printed[ck] = p + 1
                        tag = "SHOW" if show else "    "
                        print(f"  t={t:6.2f}s {tag} {span}+0x{off:03X} "
                              f"{ob.hex()} -> {nb.hex()}  {interp(nb)}"
                              + ("  [muting]" if p + 1 == MAX_PRINTS_PER_OFFSET else ""))
                last[key] = buf
        # show-flag edge bookkeeping (+ hold arming)
        flag = rd(h, ctrl + SHOW_FLAG_OFF, 1)
        now_show = bool(flag and flag[0] == 1)
        if now_show and not show:
            show_t0 = t
            print(f"  t={t:6.2f}s ==== SHOW rise ====")
            if holds:
                hold_until = time.time() + max(d for *_x, d in holds)
        if not now_show and show:
            print(f"  t={t:6.2f}s ==== SHOW fall (life {t - show_t0:.2f}s) ====")
        show = now_show
        if holds and time.time() < hold_until:
            for span, off, val, width, _d in holds:
                wr(h, addr[span] + off, val.to_bytes(width, "little"))
        time.sleep(SAMPLE_S)

    print("\n==== summary: changed offsets (show-correlated first) ====")
    rows = sorted(counts.items(),
                  key=lambda kv: (-(kv[1][1] / kv[1][0]), -kv[1][0]))
    for (span, off), (tot, ins) in rows:
        print(f"  {span}+0x{off:03X}  changes={tot:<5} in-show={ins:<5} ({ins / tot:4.0%})")
    print("done")


if __name__ == "__main__":
    main()
