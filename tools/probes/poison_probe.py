#!/usr/bin/env python
"""
Poison status/duration probe (HANDOFF open thread 7, the Venombolt search point).

GOAL: find (a) Poison's status bit on the authoritative band copy (the Charm
analog -- charm is +0x49 mask 0x20), and (b) any per-unit countdown byte that
tracks the status's remaining life -- IF poison expires at all in IC.

USAGE (game running, battle live; pick a distinctive enemy -- unique mhp/lvl):
  python ct_probe.py dump                            # grab the target's mhp/lvl
  python poison_probe.py diff <mhp> <lvl> [seconds=300] [hz=5]
        # baselines BOTH unit copies (static slot + auth band), then streams
        # every byte change. Poison the unit when prompted. Read the stream for:
        #   - a bit flipping ON the moment poison lands  -> the status bit
        #   - a byte that sets to N and counts DOWN      -> the duration timer
        #   - the same bit flipping OFF at expiry/cure   -> confirmation
        # Noisy offsets auto-mute after 8 changes; CT (+0x25) is pre-muted.
        # Ctrl+C anytime -> prints the summary table.
  python poison_probe.py survey [lo=0x40] [hi=0x90]
        # one-shot: every unit's band-copy bytes in [lo,hi) side by side.
        # An already-poisoned unit stands out from its unpoisoned peers --
        # use when poison landed BEFORE a baseline could be taken.
  python poison_probe.py watch <mhp> <lvl> <off> [off...]      # focused re-check
        # watch specific offsets (hex ok: 0x49) on both copies, 10Hz, 120s.
  python poison_probe.py holdbit <off> <mask> <mhp> <lvl> [seconds=60]
        # OR the mask into the AUTH-copy byte every 30ms (the Galewind hold
        # tech). Confirms the signature: does held Poison keep ticking? does
        # Antidote/Esuna fail to clear it? HP is echoed 1/s to see the ticks.

DON'T restart the battle mid-probe (the static array freezes on restart);
start a fresh battle and relaunch instead. RPM/WPM only -- cannot crash the game.
"""
import os
import sys
import time
from collections import defaultdict

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from ct_probe import (ARRAY_BASE, BAND_ANCHOR, BAND_CHUNK, BAND_RADIUS, PROC,
                      PV, PV_W, SLOT_HI, SLOT_LO, STRIDE, A_HP, A_LVL,
                      A_MAXHP, A_OBRAVE, A_OFAITH, find_pid, k32, rd,
                      scan_auth, scan_static, u16, wr)

MUTE_N = 8
HINTS = {0x0D: "lvl", 0x0E: "brave", 0x10: "faith", 0x12: "inb",
         0x14: "HP lo", 0x15: "HP hi", 0x16: "maxHP lo", 0x17: "maxHP hi",
         0x22: "PA", 0x23: "MA", 0x24: "Speed", 0x25: "CT",
         0x49: "status (charm=0x20)", 0x54: "allegiance",
         0x74: "reaction bits", 0x78: "support bits", 0x7D: "movement bits"}


def hint(off):
    return f"  <{HINTS[off]}>" if off in HINTS else ""


def scan_static_union(h, passes=15, dt=0.05):
    """The static array's inBattle flag PULSES 0/1 per unit mid-battle; a single
    pass only sees whoever's flag is up. Union several passes to catch them all."""
    out = {}
    for _ in range(passes):
        for key, s in scan_static(h).items():
            out.setdefault(key, s)
        time.sleep(dt)
    return out


def locate_unit(h, mhp, lvl):
    static = scan_static_union(h, passes=8)
    key = next((k for k in static
                if static[k]["mhp"] == mhp and static[k]["lvl"] == lvl), None)
    if key is None:
        return None
    fp = tuple(int(x) for x in key.split("/"))
    auth = scan_auth(h, {fp})
    return {"key": key, "static": static[key]["addr"], "band": auth.get(key)}


def locate_blocking(h, mhp, lvl):
    while True:
        u = locate_unit(h, mhp, lvl)
        if u and u["band"] is not None:
            return u
        print("  unit not located (inb pulse / mid-action?) -- retrying...")
        time.sleep(0.5)


def band_ok(h, addr, mhp, lvl):
    b = rd(h, addr, 0x18)
    return bool(b) and u16(b, A_MAXHP) == mhp and b[A_LVL] == lvl


def cmd_diff(h, mhp, lvl, seconds, hz):
    u = locate_blocking(h, mhp, lvl)
    print(f"target fp={u['key']}  static@{u['static']:012X}  band@{u['band']:012X}")
    prev = {"static": rd(h, u["static"], STRIDE), "band": rd(h, u["band"], STRIDE)}
    print(f"baseline taken. POISON THE UNIT NOW. streaming changes for {seconds:.0f}s "
          f"(offsets auto-mute after {MUTE_N} changes; CT +0x25 pre-muted)...\n")
    hist = defaultdict(list)                      # (copy, off) -> [(t, old, new)]
    muted = {("static", 0x25), ("band", 0x25)}
    t0 = time.time()
    try:
        while time.time() - t0 < seconds:
            bb = rd(h, u["band"], STRIDE)
            if not bb or not band_ok(h, u["band"], mhp, lvl):
                nu = locate_unit(h, mhp, lvl)
                if not nu or nu["band"] is None:
                    time.sleep(1.0 / hz)
                    continue
                print(f"  [reloc] band {u['band']:012X} -> {nu['band']:012X}; re-baselining")
                u = nu
                prev = {"static": rd(h, u["static"], STRIDE),
                        "band": rd(h, u["band"], STRIDE)}
                continue
            t = time.time() - t0
            sb = rd(h, u["static"], STRIDE)
            for name, cur in (("static", sb), ("band", bb)):
                old = prev.get(name)
                if not cur or not old:
                    continue
                for off in range(STRIDE):
                    if cur[off] == old[off]:
                        continue
                    k = (name, off)
                    hist[k].append((t, old[off], cur[off]))
                    if k in muted:
                        continue
                    if len(hist[k]) > MUTE_N:
                        muted.add(k)
                        print(f"  [mute] {name}+0x{off:03X} (noisy)")
                        continue
                    print(f"t={t:6.1f}s  [{name:6}] +0x{off:03X}: "
                          f"{old[off]:02X} -> {cur[off]:02X}{hint(off)}")
                prev[name] = cur
            time.sleep(1.0 / hz)
    except KeyboardInterrupt:
        print("\n(interrupted)")
    print("\n==== SUMMARY: every offset that changed (first 12 transitions each) ====")
    for (name, off), evs in sorted(hist.items()):
        m = "  [MUTED/noisy]" if (name, off) in muted and off != 0x25 else ""
        seq = " ".join(f"{a:02X}>{b:02X}@{tt:.0f}s" for tt, a, b in evs[:12])
        print(f"[{name:6}] +0x{off:03X}{hint(off)}{m}  x{len(evs)}: {seq}")


def bandscan(h):
    """Blind walk of the auth band for unit-shaped structs (STATIC layout: lvl
    @0x0D br@0x0E fa@0x10 hp@0x14 mhp@0x16 PA@0x22 MA@0x23 Spd@0x24 CT@0x25).
    The static array freezes on battle restart, so this needs no fingerprints.
    Returns [(addr, fp, hp, mhp)]; frozen-roster twins included -- the LIVE copy
    is the one whose HP/CT actually moves."""
    hits = []
    lo = BAND_ANCHOR - BAND_RADIUS
    total = BAND_RADIUS * 2
    off = 0
    while off < total:
        n = min(BAND_CHUNK + 0x200, total - off)
        buf = rd(h, lo + off, n)
        if buf:
            lim = min(BAND_CHUNK, len(buf) - 0x200)
            for i in range(lim):
                lvl, br, fa = buf[i + 0x0D], buf[i + 0x0E], buf[i + 0x10]
                if not (1 <= lvl <= 99 and 1 <= br <= 100 and 1 <= fa <= 100):
                    continue
                hp = buf[i + 0x14] | (buf[i + 0x15] << 8)
                mhp = buf[i + 0x16] | (buf[i + 0x17] << 8)
                if not (1 <= mhp <= 2000 and hp <= mhp):
                    continue
                pa, ma, spd, ct = buf[i + 0x22], buf[i + 0x23], buf[i + 0x24], buf[i + 0x25]
                if not (1 <= pa <= 60 and 1 <= ma <= 60 and 1 <= spd <= 30 and ct <= 120):
                    continue
                hits.append((lo + off + i, (mhp, lvl, br, fa), hp, mhp))
        off += BAND_CHUNK
    seen, out = set(), []
    for a, fp, hp, mhp in hits:
        if a not in seen:
            seen.add(a)
            out.append((a, fp, hp, mhp))
    return out


def cmd_bandsurvey(h, lo=0x40, hi=0x90):
    units = bandscan(h)
    print(f"{len(units)} unit-shaped structs in the band. bytes +0x{lo:02X}..+0x{hi - 1:02X}:\n")
    print(" " * 45 + " ".join(f"{o & 0xFF:02X}" for o in range(lo, hi)))
    for a, fp, hp, mhp in units:
        b = rd(h, a + lo, hi - lo)
        hx = " ".join(f"{x:02X}" for x in b) if b else "??"
        fps = "%d/%d/%d/%d" % fp
        print(f"@{a:012X} {fps:18} {hp:>4}/{mhp:<4} {hx}")


def cmd_survey(h, lo=0x40, hi=0x90):
    static = scan_static_union(h)
    fps = {tuple(int(x) for x in k.split("/")) for k in static}
    auth = scan_auth(h, fps)
    print(f"{len(static)} units, {len(auth)} band-located. band bytes +0x{lo:02X}..+0x{hi - 1:02X}:\n")
    print(" " * 34 + " ".join(f"{o & 0xFF:02X}" for o in range(lo, hi)))
    for key, s in sorted(static.items(), key=lambda kv: -kv[1]["slot"]):
        side = "PLAYER" if s["slot"] >= 1 else "enemy"
        a = auth.get(key)
        if a is None:
            print(f"{side:7}{key:16} {s['hp']:>4}/{s['mhp']:<4} --band MISS--")
            continue
        b = rd(h, a + lo, hi - lo)
        hx = " ".join(f"{x:02X}" for x in b) if b else "??"
        print(f"{side:7}{key:16} {s['hp']:>4}/{s['mhp']:<4} {hx}")


def cmd_watch(h, mhp, lvl, offs, seconds=120, hz=10):
    u = locate_blocking(h, mhp, lvl)
    print(f"watching {['0x%02X' % o for o in offs]} on static@{u['static']:012X} "
          f"band@{u['band']:012X} for {seconds}s @ {hz}Hz (prints on change)\n")
    last = {}
    t0 = time.time()
    try:
        while time.time() - t0 < seconds:
            if not band_ok(h, u["band"], mhp, lvl):
                u = locate_blocking(h, mhp, lvl)
            for name, base in (("static", u["static"]), ("band", u["band"])):
                for off in offs:
                    b = rd(h, base + off, 1)
                    if b is None:
                        continue
                    k = (name, off)
                    if last.get(k) != b[0]:
                        print(f"t={time.time() - t0:6.1f}s  [{name:6}] +0x{off:03X}: "
                              f"{last.get(k, -1) & 0xFF:02X} -> {b[0]:02X}{hint(off)}")
                        last[k] = b[0]
            time.sleep(1.0 / hz)
    except KeyboardInterrupt:
        print("(interrupted)")


def cmd_watchaddr(h, addr, seconds=300, hz=5):
    """Diff-stream the full struct at a FIXED band address (use after bandsurvey).
    Bails with a notice if the unit fingerprint at the address changes (reloc)."""
    base = rd(h, addr, STRIDE)
    if base is None:
        print(f"can't read @{addr:012X}")
        return
    fp0 = (u16(base, 0x16), base[0x0D], base[0x0E], base[0x10])
    print(f"watching @{addr:012X} fp={'%d/%d/%d/%d' % fp0} for {seconds:.0f}s "
          f"(CT +0x25 pre-muted, noisy offsets auto-mute after {MUTE_N})\n")
    hist = defaultdict(list)
    muted = {0x25}
    prev = base
    t0 = time.time()
    try:
        while time.time() - t0 < seconds:
            cur = rd(h, addr, STRIDE)
            t = time.time() - t0
            if cur is None:
                time.sleep(1.0 / hz)
                continue
            if (u16(cur, 0x16), cur[0x0D]) != (fp0[0], fp0[1]):
                print(f"t={t:6.1f}s  STRUCT RELOCATED (fp changed) -- re-run bandsurvey")
                break
            for off in range(STRIDE):
                if cur[off] == prev[off]:
                    continue
                hist[off].append((t, prev[off], cur[off]))
                if off in muted:
                    continue
                if len(hist[off]) > MUTE_N:
                    muted.add(off)
                    print(f"  [mute] +0x{off:03X} (noisy)")
                    continue
                print(f"t={t:6.1f}s  +0x{off:03X}: {prev[off]:02X} -> {cur[off]:02X}{hint(off)}")
            prev = cur
            time.sleep(1.0 / hz)
    except KeyboardInterrupt:
        print("\n(interrupted)")
    print("\n==== SUMMARY: every offset that changed (first 12 transitions each) ====")
    for off, evs in sorted(hist.items()):
        m = "  [noisy]" if off in muted and off != 0x25 else ""
        seq = " ".join(f"{a:02X}>{b:02X}@{tt:.0f}s" for tt, a, b in evs[:12])
        print(f"+0x{off:03X}{hint(off)}{m}  x{len(evs)}: {seq}")


def cmd_watchspan(h, lo, span, seconds=360, hz=4):
    """Diff-stream a whole roster block (units at 0x200 stride from lo) in one
    RPM per tick. Changes print as slotN+0xYY; CT (+0x25) pre-muted per slot."""
    prev = rd(h, lo, span)
    if prev is None:
        print(f"can't read @{lo:012X}")
        return
    labels = {}
    for s in range(span // 0x200):
        b = prev[s * 0x200:s * 0x200 + 0x200]
        labels[s] = f"{u16(b, 0x16)}/{b[0x0D]}/{b[0x0E]}/{b[0x10]}"
    print(f"watching @{lo:012X}+0x{span:X} ({span // 0x200} slots) for {seconds:.0f}s @ {hz}Hz")
    for s, l in labels.items():
        print(f"  slot{s}: fp={l}  @{lo + s * 0x200:012X}")
    print()
    hist = defaultdict(list)
    muted = {(s, 0x25) for s in labels}
    t0 = time.time()
    try:
        while time.time() - t0 < seconds:
            cur = rd(h, lo, span)
            t = time.time() - t0
            if cur is None:
                time.sleep(1.0 / hz)
                continue
            for off in range(span):
                if cur[off] == prev[off]:
                    continue
                s, soff = off // 0x200, off % 0x200
                k = (s, soff)
                hist[k].append((t, prev[off], cur[off]))
                if k in muted:
                    continue
                if len(hist[k]) > MUTE_N:
                    muted.add(k)
                    print(f"  [mute] slot{s}+0x{soff:03X}")
                    continue
                print(f"t={t:6.1f}s  slot{s}({labels[s]:14}) +0x{soff:03X}: "
                      f"{prev[off]:02X} -> {cur[off]:02X}{hint(soff)}")
            prev = cur
            time.sleep(1.0 / hz)
    except KeyboardInterrupt:
        print("\n(interrupted)")
    print("\n==== SUMMARY (first 12 transitions per offset) ====")
    for (s, soff), evs in sorted(hist.items()):
        m = "  [noisy]" if (s, soff) in muted and soff != 0x25 else ""
        seq = " ".join(f"{a:02X}>{b:02X}@{tt:.0f}s" for tt, a, b in evs[:12])
        print(f"slot{s}({labels[s]}) +0x{soff:03X}{hint(soff)}{m}  x{len(evs)}: {seq}")


def find_roster_block(h, cap=0x8000):
    """Auto-locate the live roster block: blind-scan the band for unit-shaped
    structs, bucket by 0x200-alignment residue (the real roster sits at one
    residue, the frozen clone cluster is scattered), pick the busiest residue,
    return (base, span) covering its run. Survives restarts (addresses move)."""
    from collections import Counter
    units = bandscan(h)
    if not units:
        return None, None
    res = Counter(a & 0x1FF for a, _, _, _ in units)
    best = res.most_common(1)[0][0]
    addrs = sorted(a for a, _, _, _ in units if (a & 0x1FF) == best)
    base = addrs[0]
    span = min(cap, addrs[-1] - base + 0x200)
    print(f"  auto-located roster: residue 0x{best:03X}, {len(addrs)} units, "
          f"base @{base:012X} span 0x{span:X}")
    return base, span


def cmd_venom(h, base, span, seconds=360, pct=175):
    """LIVE TEST: persistent Nx poison ('Venombolt P3' prototype). Scans the
    roster block each 30ms; any real unit seen with the poison bit ON is latched:
    bit re-held on cleanse, timer topped (+0x4A=0x24), and every engine tick
    (an exact -floor(mhp/8) HP drop) augmented so the TOTAL is pct% of the engine
    tick -- e.g. pct=175 -> engine -mhp/8 plus our extra -(mhp/8)*75/100. Floored
    at 1 HP, so the augment never kills; only real venom (or your bolts) finish."""
    bonus = max(0, pct - 100)
    n = span // 0x200
    lat = {}
    t0 = time.time()
    last_s = -1
    print(f"VENOM on {n} slots @{base:012X} for {seconds:.0f}s -- poison something.")
    try:
        while time.time() - t0 < seconds:
            buf = rd(h, base, span)
            if buf is None:
                time.sleep(0.03)
                continue
            for s in range(n):
                o = s * 0x200
                mhp, hp, lvl = u16(buf, o + 0x16), u16(buf, o + 0x14), buf[o + 0x0D]
                bit, tmr = buf[o + 0x48] & 0x80, buf[o + 0x4A]
                st = lat.get(s)
                if st is None:
                    if bit and 150 <= mhp <= 1500 and 50 <= lvl <= 99:
                        lat[s] = {"mhp": mhp, "prev": hp, "aug": 0, "cure": 0, "top": 0}
                        print(f"  LATCH slot{s}: mhp={mhp} hp={hp} "
                              f"tick=-{mhp // 8} augment=-{(mhp // 8) * bonus // 100} "
                              f"(total {pct}%)")
                    continue
                if mhp != st["mhp"]:
                    print(f"  slot{s} struct changed -- unlatched")
                    del lat[s]
                    continue
                a = base + o
                if not bit:
                    wr(h, a + 0x48, bytes([buf[o + 0x48] | 0x80]))
                    st["cure"] += 1
                if tmr < 0x10:
                    wr(h, a + 0x4A, b"\x24")
                    st["top"] += 1
                drop = st["prev"] - hp
                if hp > 0 and drop == st["mhp"] // 8:
                    extra = (st["mhp"] // 8) * bonus // 100
                    nhp = max(1, hp - extra)
                    wr(h, a + 0x14, nhp.to_bytes(2, "little"))
                    st["aug"] += 1
                    print(f"  TICK slot{s}: engine -{drop} ({st['prev']}->{hp}), "
                          f"augmented -{extra} -> {nhp}")
                    hp = nhp
                st["prev"] = hp
            s_now = int(time.time() - t0)
            if s_now != last_s and s_now % 2 == 0:
                last_s = s_now
                parts = [f"slot{s}:hp={st['prev']} aug={st['aug']} cures={st['cure']}"
                         for s, st in lat.items()]
                print(f"  t={s_now:>3}s  " + ("  ".join(parts) if parts else "(nothing poisoned yet)"))
            time.sleep(0.03)
    except KeyboardInterrupt:
        print("(interrupted)")
    for s, st in lat.items():
        print(f"venom lifted slot{s}: {st['aug']} augmented ticks, "
              f"{st['cure']} cleanses overridden, {st['top']} timer top-ups")


def cmd_findstatic(h, mhp, lvl, br, fa):
    """Locate a unit's STATIC-array copy (0x140893C00 family) by fingerprint,
    ignoring the unreliable inBattle flag. Prints addr + status block."""
    found = 0
    for n in range(SLOT_LO, SLOT_HI):
        addr = ARRAY_BASE + n * STRIDE
        b = rd(h, addr, STRIDE)
        if not b:
            continue
        if u16(b, A_MAXHP) == mhp and b[A_LVL] == lvl \
                and b[A_OBRAVE] == br and b[A_OFAITH] == fa:
            blk = " ".join(f"{x:02X}" for x in b[0x44:0x4C])
            print(f"slot {n:>3} @{addr:012X} hp={u16(b, A_HP)} [44..4B]={blk}")
            found += 1
    if not found:
        print("no static-array copy matches that fingerprint (array frozen/stale?)")


def cmd_pin(h, addr, off, val, seconds):
    """Pin one byte at addr+off to val (30ms cadence). Echoes the status block
    [+0x44..+0x4B] + HP once a second so the poison bit/timer/ticks stay visible."""
    print(f"PIN @{addr:012X}+0x{off:02X} = 0x{val:02X} for {seconds:.0f}s")
    t0 = time.time()
    writes = 0
    last_s = -1
    try:
        while time.time() - t0 < seconds:
            b = rd(h, addr + off, 1)
            if b is not None and b[0] != val:
                wr(h, addr + off, bytes([val]))
                writes += 1
            s = int(time.time() - t0)
            if s != last_s:
                last_s = s
                blk = rd(h, addr + 0x44, 8)
                hpb = rd(h, addr + 0x14, 2)
                hp = u16(hpb, 0) if hpb else -1
                bs = " ".join(f"{x:02X}" for x in blk) if blk else "??"
                print(f"  t={s:>3}s hp={hp:>4} [44..4B]={bs} rewrites={writes}")
            time.sleep(0.03)
    except KeyboardInterrupt:
        print("(interrupted)")
    print(f"pin ended after {writes} rewrites.")


def cmd_plague(h, addr, seconds):
    """The 'persistent plague' prototype: hold poison bit (+0x48 |= 0x80) AND
    top the duration timer (+0x4A = 0x24) every 30ms. A cleanse clears the bit
    for at most one tick before it re-asserts -- uncurable, never-expiring
    poison. Counts cleanse-overrides so we know the engine fought back."""
    print(f"PLAGUE on @{addr:012X}: holding +0x48|=0x80, +0x4A=0x24 for {seconds:.0f}s")
    t0 = time.time()
    cures_overridden = 0
    expiries_topped = 0
    tint_fixes = 0
    last_s = -1
    try:
        while time.time() - t0 < seconds:
            blk = rd(h, addr + 0x48, 3)
            if blk is not None:
                if not (blk[0] & 0x80):
                    wr(h, addr + 0x48, bytes([blk[0] | 0x80]))
                    cures_overridden += 1
                if blk[2] < 0x10:
                    wr(h, addr + 0x4A, b"\x24")
                    expiries_topped += 1
            mir = rd(h, addr + 0x1D6, 1)
            if mir is not None and not (mir[0] & 0x80):
                wr(h, addr + 0x1D6, bytes([mir[0] | 0x80]))
                tint_fixes += 1
            s = int(time.time() - t0)
            if s != last_s:
                last_s = s
                full = rd(h, addr + 0x44, 8)
                hpb = rd(h, addr + 0x14, 2)
                hp = u16(hpb, 0) if hpb else -1
                bs = " ".join(f"{x:02X}" for x in full) if full else "??"
                print(f"  t={s:>3}s hp={hp:>4} [44..4B]={bs} "
                      f"cures-overridden={cures_overridden} timer-topups={expiries_topped} "
                      f"tint-fixes={tint_fixes}")
            time.sleep(0.03)
    except KeyboardInterrupt:
        print("(interrupted)")
    print(f"plague lifted. overrode {cures_overridden} cleanses, {expiries_topped} timer top-ups.")


def cmd_holdbit(h, off, mask, mhp, lvl, seconds):
    u = locate_blocking(h, mhp, lvl)
    print(f"HOLDING band+0x{off:03X} |= 0x{mask:02X} on fp={u['key']} for {seconds}s. "
          f"Try Antidote/Esuna, let clockticks run -- does Poison persist + tick?")
    t0 = time.time()
    asserts = 0
    last_s = -1
    try:
        while time.time() - t0 < seconds:
            if not band_ok(h, u["band"], mhp, lvl):
                u = locate_blocking(h, mhp, lvl)
            b = rd(h, u["band"] + off, 1)
            if b is not None and (b[0] & mask) != mask:
                wr(h, u["band"] + off, bytes([b[0] | mask]))
                asserts += 1                      # engine cleared it -> we re-set it
            s = int(time.time() - t0)
            if s != last_s:
                last_s = s
                hpb = rd(h, u["band"] + 0x14, 2)
                hp = u16(hpb, 0) if hpb else -1
                print(f"  t={s:>3}s  byte={b[0] if b else -1:02X}  "
                      f"re-asserts={asserts}  hp={hp}")
            time.sleep(0.03)
    except KeyboardInterrupt:
        print("(interrupted)")
    print(f"hold ended. engine cleared the bit {asserts}x (each = an expiry/cure we overrode).")


def main():
    mode = sys.argv[1] if len(sys.argv) > 1 else ""
    if mode not in ("diff", "survey", "bandsurvey", "watch", "watchaddr", "watchspan", "holdbit", "pin", "plague", "venom", "findstatic"):
        print(__doc__)
        return
    pid = find_pid(PROC)
    if not pid:
        print(f"{PROC} not running")
        return
    h = k32.OpenProcess(PV_W if mode in ("holdbit", "pin", "plague", "venom") else PV, False, pid)
    if not h:
        print("OpenProcess failed")
        return
    try:
        a = sys.argv
        if mode == "diff":
            cmd_diff(h, int(a[2]), int(a[3]),
                     float(a[4]) if len(a) > 4 else 300,
                     float(a[5]) if len(a) > 5 else 5)
        elif mode == "survey":
            cmd_survey(h, int(a[2], 0) if len(a) > 2 else 0x40,
                       int(a[3], 0) if len(a) > 3 else 0x90)
        elif mode == "bandsurvey":
            cmd_bandsurvey(h, int(a[2], 0) if len(a) > 2 else 0x40,
                           int(a[3], 0) if len(a) > 3 else 0x90)
        elif mode == "watchaddr":
            cmd_watchaddr(h, int(a[2], 0),
                          float(a[3]) if len(a) > 3 else 300,
                          float(a[4]) if len(a) > 4 else 5)
        elif mode == "watchspan":
            if a[2] == "auto":
                base, span = find_roster_block(h)
                if base is None:
                    print("no roster block found -- is a battle live?")
                    return
                rest = a[3:]
            else:
                base, span = int(a[2], 0), int(a[3], 0)
                rest = a[4:]
            cmd_watchspan(h, base, span,
                          float(rest[0]) if len(rest) > 0 else 360,
                          float(rest[1]) if len(rest) > 1 else 4)
        elif mode == "pin":
            cmd_pin(h, int(a[2], 0), int(a[3], 0), int(a[4], 0),
                    float(a[5]) if len(a) > 5 else 240)
        elif mode == "plague":
            cmd_plague(h, int(a[2], 0), float(a[3]) if len(a) > 3 else 300)
        elif mode == "findstatic":
            cmd_findstatic(h, int(a[2]), int(a[3]), int(a[4]), int(a[5]))
        elif mode == "venom":
            if a[2] == "auto":
                base, span = find_roster_block(h)
                if base is None:
                    print("no roster block found -- is a battle live?")
                    return
                rest = a[3:]                      # venom auto [secs] [pct]
            else:
                base, span = int(a[2], 0), int(a[3], 0)
                rest = a[4:]                      # venom <base> <span> [secs] [pct]
            cmd_venom(h, base, span,
                      float(rest[0]) if len(rest) > 0 else 360,
                      int(rest[1]) if len(rest) > 1 else 175)
        elif mode == "watch":
            cmd_watch(h, int(a[2]), int(a[3]), [int(x, 0) for x in a[4:]])
        else:
            cmd_holdbit(h, int(a[2], 0), int(a[3], 0), int(a[4]), int(a[5]),
                        float(a[6]) if len(a) > 6 else 60)
    finally:
        k32.CloseHandle(h)


if __name__ == "__main__":
    main()
