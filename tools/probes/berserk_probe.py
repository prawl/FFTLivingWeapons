#!/usr/bin/env python
"""
SPIKE (WRITE, auto-reverting): "controllable Berserk" for the Shura katana idea.

Question: can we get Berserk's damage buff while KEEPING player control -- by setting the
Berserk status bit AND holding the human-agency bit so the engine can't seize the unit?

Two bits, same 1.5 combat array (BASE 0x141853CE0, stride 0x200), located by (brave, faith):
  - Berserk status : slot +0x63  bit 0x08   (== band +0x47; status-EFFECT honoring is the spike)
  - Agency (human) : slot +0x05  bit 0x08   (+ shadow +0x1EE) -- PROVEN control lever (PuppetHold)

Agency offsets/polarity from tools/probes/combat_scan.py + puppet_probe.py (SET 0x08 = human).
Berserk offset from the status map (band +0x47/0x08) via the +0x1C combat/band relation.

STAGED TEST (run each right when the samurai is about to act; the hold auto-reverts after `secs`):
  1. survey                         -- list units; find the samurai's brave/faith.
  2. berserk <br> <fa> [secs]       -- Berserk ONLY. Watch: damage up? control lost (AI seizes it)?
  3. rage    <br> <fa> [secs]       -- Berserk + agency-hold. Watch: do you KEEP control mid-rage?
                                       Then try a SKILL: full menu, or Attack-only?
  4. clear   <br> <fa>              -- one-shot recovery if a hold was hard-killed.

SAFETY: single-bit OR-set, guarded; re-verifies the slot's (br,fa) every tick and bails if it
drifts (unit died / array relocated); ALWAYS restores the original bytes on exit. RPM/WPM only.
Do NOT restart the battle mid-probe (the array relocates). Largest-working-set pid (2-proc trap).
"""
import ctypes as C
from ctypes import wintypes as W
import sys, time

PROC = "fft_enhanced"
BASE = 0x141853CE0
STRIDE = 0x200
N = 28

OFF_JOB, OFF_AGENCY, OFF_SHADOW = 0x03, 0x05, 0x1EE
OFF_WEAPON = 0x20
OFF_LEVEL, OFF_BRAVE, OFF_FAITH = 0x29, 0x2A, 0x2C
OFF_PA, OFF_MA, OFF_SPD = 0x3E, 0x3F, 0x40
OFF_BERSERK = 0x63          # band +0x47 (Reraise/Invisible/Berserk byte); Berserk = bit 0x08
HUMAN = 0x08
BERSERK = 0x08

k32 = C.WinDLL("kernel32", use_last_error=True)
psapi = C.WinDLL("psapi", use_last_error=True)
ACCESS = 0x0438   # QUERY_INFO | VM_OPERATION | VM_READ | VM_WRITE


class _PMC(C.Structure):
    _fields_ = [("cb", W.DWORD), ("pf", W.DWORD)] + [("a%d" % i, C.c_size_t) for i in range(8)]


def find_pid(name):
    arr = (W.DWORD * 4096)(); need = W.DWORD()
    psapi.EnumProcesses(arr, C.sizeof(arr), C.byref(need))
    best, bw = None, -1
    for i in range(need.value // C.sizeof(W.DWORD)):
        pid = arr[i]
        h = k32.OpenProcess(0x0410, False, pid)
        if not h:
            continue
        buf = C.create_unicode_buffer(260)
        if psapi.GetModuleBaseNameW(h, None, buf, 260) and buf.value.lower() == name.lower() + ".exe":
            p = _PMC(); p.cb = C.sizeof(p)
            psapi.GetProcessMemoryInfo(h, C.byref(p), p.cb)
            if p.a2 > bw:
                best, bw = pid, p.a2
        k32.CloseHandle(h)
    return best


_H = None


def rpm(addr, n):
    buf = C.create_string_buffer(n); got = C.c_size_t()
    ok = k32.ReadProcessMemory(_H, C.c_void_p(addr), buf, n, C.byref(got))
    return buf.raw if ok and got.value == n else None


def wpm(addr, data):
    got = C.c_size_t()
    return bool(k32.WriteProcessMemory(_H, C.c_void_p(addr), data, len(data), C.byref(got))) and got.value == len(data)


def slot_fp(i):
    """Return (level, brave, faith, job, weapon, pa, agency, berserk_byte) for slot i, or None."""
    d = rpm(BASE + i * STRIDE, STRIDE)
    if d is None:
        return None
    lvl, br, fa = d[OFF_LEVEL], d[OFF_BRAVE], d[OFF_FAITH]
    if not (1 <= lvl <= 99) or not (1 <= br <= 100) or not (1 <= fa <= 100):
        return None
    return dict(i=i, lvl=lvl, br=br, fa=fa, job=d[OFF_JOB], wpn=d[OFF_WEAPON] | (d[OFF_WEAPON + 1] << 8),
                pa=d[OFF_PA], agency=d[OFF_AGENCY], status=d[OFF_BERSERK])


def survey():
    print(f"slots @ 0x{BASE:X} stride 0x{STRIDE:X}\n")
    hdr = f"{'i':>2} {'addr':>11} {'ctl':>5} {'job':>4} {'lvl':>3} {'br':>3} {'fa':>3} {'wpn':>4} {'PA':>3} {'brsk':>4}"
    print(hdr); print("-" * len(hdr))
    for i in range(N):
        u = slot_fp(i)
        if not u:
            continue
        ctl = "HUMAN" if (u["agency"] & HUMAN) else "AI"
        bsk = "YES" if (u["status"] & BERSERK) else "-"
        print(f"{i:>2} 0x{BASE + i*STRIDE:09X} {ctl:>5} 0x{u['job']:02X} {u['lvl']:>3} {u['br']:>3} {u['fa']:>3} "
              f"{u['wpn']:>4} {u['pa']:>3} {bsk:>4}")
    print("\nFind the samurai (Katana weapon id 38-47/70, or by br/fa), then: berserk/rage <br> <fa>.")


def locate(br, fa):
    hits = [i for i in range(N) if (u := slot_fp(i)) and u["br"] == br and u["fa"] == fa]
    if not hits:
        print(f"no live slot with brave={br} faith={fa} (in battle? on field? try survey)"); return None
    if len(hits) > 1:
        print(f"WARNING: {len(hits)} slots match br/fa {br}/{fa} (slots {hits}); using the first")
    return hits[0]


def hold(br, fa, bits, secs, label):
    """bits = [(offset, mask), ...]; OR-hold each every 30ms for `secs`, then restore originals.
    Re-verifies the slot's (br,fa) each tick; bails (and restores) if it drifts."""
    i = locate(br, fa)
    if i is None:
        return
    base = BASE + i * STRIDE
    # snapshot originals (whole bytes we touch)
    orig = {off: rpm(base + off, 1)[0] for off, _ in bits}
    print(f"{label}: slot {i} @ 0x{base:09X}  holding {', '.join(f'+0x{o:X}|0x{m:02X}' for o, m in bits)} for {secs}s")
    print(f"  originals: {', '.join(f'+0x{o:X}=0x{v:02X}' for o, v in orig.items())}   (Ctrl+C reverts early)")
    t0 = time.time()
    try:
        while time.time() - t0 < secs:
            u = slot_fp(i)
            if not u or u["br"] != br or u["fa"] != fa:
                print("  slot drifted (unit died / array moved) -- stopping + reverting"); break
            for off, mask in bits:
                cur = rpm(base + off, 1)
                if cur is not None and not (cur[0] & mask):
                    wpm(base + off, bytes([cur[0] | mask]))
            time.sleep(0.030)
    except KeyboardInterrupt:
        print("\n  interrupted")
    finally:
        for off, v in orig.items():
            wpm(base + off, bytes([v]))
        back = {off: rpm(base + off, 1)[0] for off, _ in bits}
        print(f"  reverted: {', '.join(f'+0x{o:X}=0x{v:02X}' for o, v in back.items())}")


def clear(br, fa):
    i = locate(br, fa)
    if i is None:
        return
    base = BASE + i * STRIDE
    for off in (OFF_BERSERK,):           # clear Berserk; leave agency (clearing agency could strand control)
        cur = rpm(base + off, 1)
        if cur is not None and (cur[0] & BERSERK):
            wpm(base + off, bytes([cur[0] & ~BERSERK]))
    print(f"slot {i}: Berserk bit cleared (agency left as-is)")


def main():
    global _H
    pid = find_pid(PROC)
    if not pid:
        print(f"{PROC}.exe not running"); sys.exit(1)
    _H = k32.OpenProcess(ACCESS, False, pid)
    if not _H:
        print(f"OpenProcess failed (err {C.get_last_error()})"); sys.exit(1)
    print(f"pid {pid} (largest working set)\n")
    a = sys.argv[1:]
    if not a or a[0] == "survey":
        survey()
    elif a[0] == "berserk":
        hold(int(a[1]), int(a[2]), [(OFF_BERSERK, BERSERK)], int(a[3]) if len(a) > 3 else 45, "BERSERK-only")
    elif a[0] == "rage":
        hold(int(a[1]), int(a[2]), [(OFF_BERSERK, BERSERK), (OFF_AGENCY, HUMAN), (OFF_SHADOW, HUMAN)],
             int(a[3]) if len(a) > 3 else 45, "RAGE (berserk + agency)")
    elif a[0] == "clear":
        clear(int(a[1]), int(a[2]))
    else:
        print("verbs: survey | berserk <br> <fa> [secs] | rage <br> <fa> [secs] | clear <br> <fa>")


if __name__ == "__main__":
    main()
