#!/usr/bin/env python
"""LW-251 round 7: race the boot. Sign the exe-image CLUT tables BEFORE consumption.

Rounds 2-6 (ledger [g2d-equipment-sheet-override]) proved battle weapon colours resolve
ONCE at process start from binary-baked data: every post-boot lever is dead (live pokes
of the image tables self-heal; the loaded battle_wep_spr.bin block gets copied into a
working table without the weapons caring). Round 3's scan FOUND classic-CLUT copies baked
into the exe image itself (64 hits recorded in lw251_clut_hits.json, two .data spans
around 0x140816000 and past 0x14091F000; image base fixed 0x140000000, no ASLR, so the
addresses are stable across launches). The 2026-08-19 verify round added a decisive fact:
those rows DO NOT EXIST in fft_enhanced.exe on disk (0 of 16 needles in the 362MB file);
both spans sit inside Denuvo's .xpdata section (region B's rows in zero-fill past the
raw-backed end), so the tables MATERIALIZE during boot and the race below is real. This
probe tests the one untested premise the next build arc rests on:

  PREMISE: a signature written into those image tables AFTER they materialize but BEFORE
  the one-shot boot conversion consumes them changes the bow's rendered battle colours.

Method: start this probe FIRST, then launch the game. The probe waits for
fft_enhanced.exe, attaches immediately, and then (a) polls the 64 known round-3 row
addresses every ~25ms and hunts the two image spans for rows at new addresses (needles =
colours 1..15 of every palette in battle_wep_spr.bin's 512-byte block), (b) dumps each
span when its rows first appear (plus a pre-materialization before-image) for offline
structure analysis, (c) overwrites every verified row with a loud hue ramp. The colour is
decided by ADDRESS (boot-time VirtualQuery runs can merge, so caller identity is not
trusted):

  image row below 0x14091F000 -> RED      image row at/above 0x14091F000 -> GREEN
  fresh copies materializing OUTSIDE the image (background sweep) -> MAGENTA

then (d) keeps re-asserting at ~25ms, logging every revert (the round-3 self-heal will
show up here; the question is who wins the race at consumption time).

PRE-REGISTERED READ (decide from a BOW in battle; the crossbow is NOT on tex_161):
  bow RED      -> the low span (0x140816000 table) is consumed: premise PROVEN.
  bow GREEN    -> the high span (0x14091F000+ table) is consumed: premise PROVEN.
  bow MAGENTA  -> a boot-created non-image copy is the consumption point: premise PROVEN
                  in a different shape (DLL must pattern-scan after that copy exists).
  bow VANILLA + sigs HELD -> VALID ONLY IF the log's first `signed` timestamps clearly
                  predate the first loading screen; then the premise is CONTRADICTED for
                  every found copy (the true source is still hidden; hook the conversion
                  instead of the data). Late-signed + held means the RACE WAS LOST and
                  the premise is UNTESTED, not contradicted. Read the t= values.
  bow VANILLA + sigs continuously REVERTED -> the healer outraces an external writer;
                  escalate to an in-process (DLL constructor) round before calling the
                  premise dead. (Check for write_failed records first: a broken writer
                  mimics this branch.)
  game CRASHES or exits during boot AFTER sigs landed -> the signed bytes were read by
                  something pre-consumption (Denuvo integrity or the unpacker): premise
                  ALIVE but the mechanism is hostile to external writes; move to
                  post-unpack timing or the in-process round. NEVER read a crash as
                  CONTRADICTED. A restart clears every poke.

WRITES MEMORY (probe protocol: owner-sanctioned session; verified writes only, i.e. a
row is overwritten only when its current bytes byte-match a known vanilla palette row;
undo journal written BEFORE each write + verified `restore` verb; a row whose bytes turn
ALIEN, neither vanilla nor ours, is logged loudly and never touched again). All pokes are
RAM-only palette data in RW pages; a restart clears everything.

USAGE:
  python lw251_boot_clut_race.py race            # start BEFORE launching the game
  python lw251_boot_clut_race.py race --no-sweep # image tables only, no heap sweep
  python lw251_boot_clut_race.py status          # from another shell: current row states
  python lw251_boot_clut_race.py restore         # AFTER Ctrl+C of the race loop only
State: lw251_boot_race_state.json; timeline: lw251_boot_race_log.jsonl (previous run
rotated to .prev at race start; both next to this file); span dumps go to --dump-dir
(default %TEMP%). Stop the race loop with Ctrl+C once the verdict is read; state is
already on disk.
"""
import ctypes as C
from ctypes import wintypes as W
import json
import os
import struct
import sys
import tempfile
import threading
import time

PROC = "fft_enhanced.exe"
BIN = r"C:\Users\ptyRa\OneDrive\Desktop\Pac Files\0002\fftpack\unit\battle_wep_spr.bin"
HERE = os.path.dirname(os.path.abspath(__file__))
STATE = os.path.join(HERE, "lw251_boot_race_state.json")
LOG = os.path.join(HERE, "lw251_boot_race_log.jsonl")
HITS = os.path.join(HERE, "lw251_clut_hits.json")

# Round-3 span facts (lw251_clut_hits.json): image hits run 0x14088E172..0x140893518
# (low span) and 0x140C6EA96 upward (high span). REGION_B is the colour boundary; the
# hunt windows are clamped to it so a boot-merged VirtualQuery run cannot cross-colour.
REGION_A = 0x140816000
REGION_B = 0x14091F000
SPAN_PAD = 0x20000      # hunt this far past the last known hit of each span
ROW = 30                # a needle = colours 1..15 of one palette, 15 x u16
SWEEP_WINDOW_S = 150    # background sweep runs this long after attach
KEEPALIVE_S = 0.025
REHUNT_S = 2.0          # span hunt cadence AFTER that span's first sign (before: every pass)
STILL_ACTIVE = 259

k32 = C.WinDLL("kernel32", use_last_error=True)
PROCESS_ALL = 0x0010 | 0x0020 | 0x0008 | 0x0400
MEM_COMMIT = 0x1000
MEM_IMAGE = 0x1000000
MEM_PRIVATE = 0x20000
PAGE_NOACCESS, PAGE_GUARD = 0x01, 0x100


class MBI(C.Structure):
    _fields_ = [("BaseAddress", C.c_void_p), ("AllocationBase", C.c_void_p),
                ("AllocationProtect", W.DWORD), ("PartitionId", W.WORD),
                ("RegionSize", C.c_size_t), ("State", W.DWORD),
                ("Protect", W.DWORD), ("Type", W.DWORD)]


class PE32(C.Structure):
    _fields_ = [("dwSize", W.DWORD), ("cntUsage", W.DWORD), ("th32ProcessID", W.DWORD),
                ("th32DefaultHeapID", C.POINTER(C.c_ulong)), ("th32ModuleID", W.DWORD),
                ("cntThreads", W.DWORD), ("th32ParentProcessID", W.DWORD),
                ("pcPriClassBase", C.c_long), ("dwFlags", W.DWORD),
                ("szExeFile", C.c_char * 260)]


def find_pid():
    snap = k32.CreateToolhelp32Snapshot(0x2, 0)
    e = PE32(); e.dwSize = C.sizeof(PE32)
    pid = None
    if k32.Process32First(snap, C.byref(e)):
        while True:
            if e.szExeFile.decode(errors="ignore").lower() == PROC:
                pid = e.th32ProcessID
                break
            if not k32.Process32Next(snap, C.byref(e)):
                break
    k32.CloseHandle(snap)
    return pid


def rpm(h, addr, size):
    buf = C.create_string_buffer(size)
    got = C.c_size_t()
    if not k32.ReadProcessMemory(h, C.c_void_p(addr), buf, size, C.byref(got)):
        return None
    return buf.raw[: got.value]


def wpm(h, addr, data):
    old = W.DWORD()
    k32.VirtualProtectEx(h, C.c_void_p(addr), len(data), 0x40, C.byref(old))
    got = C.c_size_t()
    ok = k32.WriteProcessMemory(h, C.c_void_p(addr), data, len(data), C.byref(got))
    if old.value:
        k32.VirtualProtectEx(h, C.c_void_p(addr), len(data), old.value, C.byref(W.DWORD()))
    return bool(ok) and got.value == len(data)


def load_needles():
    """One 30-byte needle per classic WEP palette (colours 1..15), duplicates collapsed.
    All 16 palettes pass the distinct-value floor (pal1/pal2 sit at exactly 6: ten zero
    colours plus a 5-value tail), so the floor is a safety net against a future edited
    bin, not an active filter; even the 6-distinct needles matched only palette-shaped
    memory in round 3's full-process scan."""
    raw = open(BIN, "rb").read(512)
    seen, out = set(), {}
    for pi in range(16):
        vals = struct.unpack_from("<16H", raw, pi * 32)[1:]
        if len(set(vals)) < 6:
            continue
        needle = struct.pack("<15H", *vals)
        if needle in seen:
            continue
        seen.add(needle)
        out[needle] = pi
    return out  # {needle_bytes: classic_palette_index}


def load_known_addrs():
    """Round-3 image hit addresses: the fast-path discovery candidates (no ASLR, same
    game build, so they are stable) AND the seed for every hunt window. Returning empty
    is fatal at startup, never a silent degrade: the real rows sit 0x78172 and 0x34FA96
    past the two region bases, so a base-seeded fallback would hunt windows containing
    none of them and produce a confident no-find run."""
    try:
        hits = json.load(open(HITS))["hits"]
    except (OSError, ValueError, KeyError):
        return []
    return sorted({h["addr"] for h in hits if h.get("type") == MEM_IMAGE})


SIGS = {"red": (1, 0.12, 0.12), "green": (0.12, 1, 0.12), "magenta": (1, 0.12, 1)}


def sig_block(mult):
    """Loud BGR555 luminance ramp over colours 1..15 (same shape round 3 used)."""
    out = bytearray()
    for k in range(15):
        lum = 8 + int(k * (23 / 14))
        r, g, b = (min(31, int(lum * m)) for m in mult)
        out += struct.pack("<H", r | (g << 5) | (b << 10))
    return bytes(out)


SIG_BLOCKS = {name: sig_block(m) for name, m in SIGS.items()}


def image_colour(addr):
    """Sig colour for an IMAGE row, decided by address alone (BLOCKER fix: a boot-time
    merged VirtualQuery run must not let high-span rows get signed red)."""
    return "green" if addr >= REGION_B else "red"


class Race:
    def __init__(self, sweep, dump_dir):
        self.sweep = sweep
        self.dump_dir = dump_dir
        self.needles = load_needles()
        self.known = load_known_addrs()
        # Hunt windows: the known addresses fall in four tight clusters (20+20+4+20
        # rows; three look like copies of the same 20-row table). Pad each cluster
        # instead of hunting the whole 40MB .xpdata stretch, and never let a window
        # cross the REGION_B colour boundary.
        merged = []
        for a in sorted(self.known):   # non-empty: startup refuses otherwise
            if merged and a - merged[-1][1] <= SPAN_PAD:
                merged[-1][1] = a + ROW + SPAN_PAD
            else:
                merged.append([a - SPAN_PAD, a + ROW + SPAN_PAD])
        self.spans = []
        for s, e in merged:
            if s < REGION_B < e:
                self.spans += [(s, REGION_B), (REGION_B, e)]
            else:
                self.spans.append((s, e))
        self.h = None
        self.t0 = None
        self.lock = threading.Lock()
        self.rows = {}   # addr -> {"orig": hex, "sig": name, "state": str, "reverts": n}
        self.dumped = set()
        self.pre_dumped = set()
        self.span_signed = set()   # span start -> first sign landed (relaxes hunt cadence)
        self.log_f = None   # opened at first launch detection (see _ensure_log)
        self.stop = threading.Event()
        self.sweep_t = None

    def _ensure_log(self):
        """Rotate and open the timeline the moment a launch is actually detected, not at
        construction: a Ctrl+C while waiting for the game would otherwise leave a stub
        LOG that the next run promotes over the previous run's real evidence. The state
        file rotates with it so `status` cannot report a stale run as current."""
        if self.log_f is not None:
            return
        for path in (LOG, STATE):
            if os.path.exists(path):
                os.replace(path, path + ".prev")
        self.log_f = open(LOG, "a", buffering=1)

    def log(self, ev, console=True, **kw):
        """The FILE gets every record; the CONSOLE is a volume knob (repo convention).
        A sustained revert war would otherwise spend more per pass on console writes than
        the 25ms re-assert budget it is supposed to be measuring."""
        rec = {"t": None if self.t0 is None else round(time.time() - self.t0, 3),
               "ev": ev}
        rec.update(kw)
        with self.lock:
            if self.log_f is not None:
                self.log_f.write(json.dumps(rec) + "\n")
        if console:
            print(f"[{rec['t']}] {ev} " + " ".join(f"{k}={v}" for k, v in kw.items()))

    def save_state(self):
        try:
            with self.lock:
                tmp = STATE + ".tmp"
                json.dump({"rows": {hex(a): r for a, r in self.rows.items()}},
                          open(tmp, "w"), indent=1)
                os.replace(tmp, STATE)
        except OSError as e:
            # A reader holding the file (the `status` verb) or a scanner can block the
            # replace on Windows. The jsonl timeline is the primary evidence and the
            # next sign re-saves, so a failed snapshot must never stop the keepalive.
            self.log("save_state_failed", err=str(e))

    def game_alive(self):
        code = W.DWORD()
        if not k32.GetExitCodeProcess(self.h, C.byref(code)):
            return False
        return code.value == STILL_ACTIVE

    def attach(self):
        print("waiting for game launch (start the game now)...")
        while True:
            pid = find_pid()
            if not pid:
                time.sleep(0.02)
                continue
            t0 = time.time()
            self._ensure_log()
            h = None
            while find_pid() == pid:
                h = k32.OpenProcess(PROCESS_ALL, False, pid)
                if h:
                    break
                self.log("openprocess_retry", err=C.get_last_error())
                time.sleep(0.05)
            if not h:
                # launcher/respawn pattern: first pid died, wait for the real one
                print(f"pid {pid} vanished before attach; waiting for a successor...")
                continue
            self.h, self.t0 = h, t0
            self._ensure_log()
            self.log("run_start", pid=pid, wallclock=time.strftime("%Y-%m-%d %H:%M:%S"),
                     needles=len(self.needles), known_addrs=len(self.known))
            return

    def known_addr_pass(self):
        """Fast-path discovery: poll the 64 round-3 addresses every tick until each is
        signed (BLOCKER fix: 2s hunt latency could lose the race silently)."""
        for addr in self.known:
            if addr in self.rows:
                continue
            cur = rpm(self.h, addr, ROW)
            if cur is not None and cur in self.needles:
                self.sign(addr, cur, image_colour(addr),
                          pal=self.needles[cur], src="known")

    def read_span(self, start, end):
        """Chunked read of committed runs inside [start, end); unreadable or uncommitted
        stretches become zero-fill instead of aborting the pass (mid-unpack pages)."""
        buf = bytearray(end - start)
        got_any = False
        addr = start
        while addr < end:
            mbi = MBI()
            if not k32.VirtualQueryEx(self.h, C.c_void_p(addr), C.byref(mbi),
                                      C.sizeof(mbi)):
                break
            b = mbi.BaseAddress or 0
            run_end = min(b + mbi.RegionSize, end)
            readable = (mbi.State == MEM_COMMIT
                        and not (mbi.Protect & (PAGE_NOACCESS | PAGE_GUARD))
                        and mbi.Protect != 0)
            if readable:
                pos = max(addr, b)
                while pos < run_end:
                    chunk = rpm(self.h, pos, min(0x10000, run_end - pos))
                    if not chunk:
                        pos += 0x10000
                        continue
                    buf[pos - start: pos - start + len(chunk)] = chunk
                    got_any = True
                    pos += len(chunk)
            addr = run_end if run_end > addr else addr + 0x1000
        return bytes(buf) if got_any else None

    def hunt_span(self, start, end):
        """Find vanilla palette rows in one image span; sign each; dump the span the
        first time rows appear in it (that buffer IS the materialized snapshot)."""
        data = self.read_span(start, end)
        if data is None:
            return
        if start not in self.pre_dumped:
            self.dump(data, start, "pre")
            self.pre_dumped.add(start)
        found = False
        for needle, pi in self.needles.items():
            o = data.find(needle)
            while o != -1:
                addr = start + o
                found = True
                if addr not in self.rows:
                    self.sign(addr, needle, image_colour(addr), pal=pi, src="hunt")
                o = data.find(needle, o + 1)
        if found:
            self.span_signed.add(start)
            if start not in self.dumped:
                self.dump(data, start, "live")
                self.dumped.add(start)

    def dump(self, data, start, tag):
        try:
            path = os.path.join(self.dump_dir, f"lw251_span_{start:x}_{tag}.bin")
            with open(path, "wb") as f:
                f.write(data)
            self.log("span_dump", start=hex(start), size=len(data), tag=tag, path=path)
        except OSError as e:
            self.log("dump_failed", start=hex(start), err=str(e))

    def sign(self, addr, expect, sig_name, **ctx):
        """Verified write, journal FIRST: the undo entry hits disk before the poke so a
        kill between them can never leave an unjournaled signature."""
        cur = rpm(self.h, addr, ROW)
        if cur != expect:
            return
        with self.lock:
            self.rows[addr] = {"orig": expect.hex(), "sig": sig_name,
                               "state": "pending", "reverts": 0}
        self.save_state()
        if not wpm(self.h, addr, SIG_BLOCKS[sig_name]):
            self.rows[addr]["state"] = "write_failed"
            self.log("write_failed", addr=hex(addr), **ctx)
            return
        self.rows[addr]["state"] = "signed"
        self.rows[addr]["ever_signed"] = True
        self.log("signed", addr=hex(addr), sig=sig_name, **ctx)
        self.save_state()

    def keepalive_pass(self):
        for addr, r in list(self.rows.items()):
            if r["state"] == "alien":
                continue
            cur = rpm(self.h, addr, ROW)
            if cur is None:
                r["state"] = "unreadable"
                continue
            if cur == SIG_BLOCKS[r["sig"]]:
                r["state"] = "signed"
            elif cur == bytes.fromhex(r["orig"]):
                if not r.get("ever_signed"):
                    # The write never landed here, so reading vanilla says nothing about
                    # a healer. Retry (still verified) without polluting the revert
                    # stream that the pre-registered branch-5 reading depends on.
                    if wpm(self.h, addr, SIG_BLOCKS[r["sig"]]):
                        r["ever_signed"] = True
                        r["state"] = "signed"
                    elif r.get("wfail") is None:
                        r["wfail"] = True
                        self.log("write_failed", addr=hex(addr), src="keepalive")
                    continue
                r["reverts"] += 1
                r["state"] = "reverted"
                self.log("revert", console=r["reverts"] <= 3,
                         addr=hex(addr), n=r["reverts"])
                if not wpm(self.h, addr, SIG_BLOCKS[r["sig"]]) \
                        and r.get("wfail") is None:
                    r["wfail"] = True
                    self.log("write_failed", addr=hex(addr), src="keepalive")
            else:
                r["state"] = "alien"
                self.log("alien", addr=hex(addr), now=cur.hex())

    def sweep_thread(self):
        """Sign fresh classic-CLUT copies OUTSIDE the image magenta as boot creates
        them. Every pass rescans ALL committed private regions (a region committed early
        and filled later would be missed by a seen-once skip; sign() dedups by address
        and verifies vanilla bytes, so rescans are safe). Separate thread so a slow walk
        never starves the image-table keepalive."""
        try:
            deadline = time.time() + SWEEP_WINDOW_S
            passes = 0
            while not self.stop.is_set() and time.time() < deadline:
                passes += 1
                addr = 0
                while addr < 0x7FFFFFFFFFFF and not self.stop.is_set():
                    mbi = MBI()
                    if not k32.VirtualQueryEx(self.h, C.c_void_p(addr), C.byref(mbi),
                                              C.sizeof(mbi)):
                        break
                    b = mbi.BaseAddress or 0
                    size = mbi.RegionSize
                    addr = b + size
                    # PRIVATE only: a vanilla palette row inside a writable FILE-BACKED
                    # mapping would be stamped through to disk and survive a restart,
                    # breaking this probe's "a restart clears everything" contract.
                    # Every known non-image hit is MEM_PRIVATE anyway (round 3's json).
                    if (mbi.State != MEM_COMMIT or mbi.Type != MEM_PRIVATE
                            or (mbi.Protect & (PAGE_NOACCESS | PAGE_GUARD))
                            or mbi.Protect == 0):
                        continue
                    pos, tail = b, b""
                    while pos < b + size and not self.stop.is_set():
                        chunk = rpm(self.h, pos, min(8 << 20, b + size - pos))
                        if chunk is None:
                            break
                        hay = tail + chunk
                        for needle, pi in self.needles.items():
                            o = hay.find(needle)
                            while o != -1:
                                va = pos - len(tail) + o
                                if va not in self.rows:
                                    self.sign(va, needle, "magenta", pal=pi,
                                              region=hex(b), src="sweep")
                                o = hay.find(needle, o + 1)
                        tail = chunk[-(ROW - 1):]
                        pos += len(chunk)
                time.sleep(1.0)
            self.log("sweep_done", passes=passes)
        except Exception as e:  # a dead magenta lane must be loud, not silent
            self.log("sweep_crashed", err=repr(e))

    def run(self):
        self.attach()
        if self.sweep:
            self.sweep_t = threading.Thread(target=self.sweep_thread, daemon=True)
            self.sweep_t.start()
        last_hunt = 0.0
        try:
            while True:
                now = time.time()
                # Span hunt runs FIRST: the known rows are themselves needle matches, so
                # ramping them before the hunt would erase the very rows whose discovery
                # triggers the materialized-table dump and the span_signed demotion.
                # An unsigned span is hunted every pass, a signed one every REHUNT_S.
                for lo, hi in self.spans:
                    if lo in self.span_signed and now - last_hunt < REHUNT_S:
                        continue
                    self.hunt_span(lo, hi)
                if now - last_hunt >= REHUNT_S:
                    last_hunt = now
                self.known_addr_pass()   # fast mop-up for rows a hunt pass missed
                self.keepalive_pass()
                if not self.game_alive():
                    # A launcher/Denuvo respawn can retire the pid we attached to before
                    # anything materializes. Quitting here would burn the launch (and the
                    # already-running guard blocks a restart), so wait for the successor.
                    if not self.rows and time.time() - self.t0 < 60:
                        self.log("game_exited_early", note="respawn; awaiting successor")
                        k32.CloseHandle(self.h)
                        self.h = None
                        self.attach()
                        continue
                    self.log("game_exited")
                    break
                time.sleep(KEEPALIVE_S)
        except KeyboardInterrupt:
            pass
        finally:
            self.stop.set()
            if self.sweep_t is not None:
                self.sweep_t.join(timeout=5)
            self.save_state()
            counts = {}
            for r in self.rows.values():
                counts[r["state"]] = counts.get(r["state"], 0) + 1
                counts[f"sig:{r['sig']}"] = counts.get(f"sig:{r['sig']}", 0) + 1
            self.log("race_end", **counts)
            print(f"state -> {STATE}; timeline -> {LOG}")


def status():
    if not os.path.exists(STATE):
        sys.exit("no state file yet (race has not signed anything)")
    st = json.load(open(STATE))
    pid = find_pid()
    h = k32.OpenProcess(PROCESS_ALL, False, pid) if pid else None
    for a_hex, r in st["rows"].items():
        line = f"{a_hex} sig={r['sig']} last={r['state']} reverts={r['reverts']}"
        if h:
            cur = rpm(h, int(a_hex, 16), ROW)
            if cur is None:
                line += " now=UNREADABLE"
            elif cur == SIG_BLOCKS[r["sig"]]:
                line += " now=SIGNED"
            elif cur == bytes.fromhex(r["orig"]):
                line += " now=VANILLA"
            else:
                line += " now=ALIEN"
        print(line)
    if h:
        k32.CloseHandle(h)


def restore():
    """Verified restore: write a row's vanilla bytes back ONLY where it still holds our
    signature; anything else (recycled page, alien bytes) is left alone and reported.
    Run AFTER Ctrl+C of the race loop, or the keepalive re-signs everything."""
    if not os.path.exists(STATE):
        sys.exit("no state file; nothing was signed")
    st = json.load(open(STATE))
    pid = find_pid()
    if not pid:
        sys.exit("game not running; nothing to restore (pokes are RAM-only)")
    h = k32.OpenProcess(PROCESS_ALL, False, pid)
    if not h:
        sys.exit(f"OpenProcess failed {C.get_last_error()}")
    ok = skipped = 0
    for a_hex, r in st["rows"].items():
        addr = int(a_hex, 16)
        cur = rpm(h, addr, ROW)
        if cur != SIG_BLOCKS[r["sig"]]:
            skipped += 1
            continue
        if wpm(h, addr, bytes.fromhex(r["orig"])):
            ok += 1
    k32.CloseHandle(h)
    print(f"restored {ok}, skipped {skipped} (not our bytes any more; left alone)")


if __name__ == "__main__":
    if len(sys.argv) < 2 or sys.argv[1] not in ("race", "status", "restore"):
        sys.exit(__doc__)
    if sys.argv[1] == "status":
        status()
    elif sys.argv[1] == "restore":
        restore()
    else:
        if find_pid() is not None:
            sys.exit("game is ALREADY RUNNING: the race only means anything from a "
                     "fresh boot. Close the game, start this probe, then launch.")
        if not load_known_addrs():
            sys.exit(f"no usable round-3 addresses in {HITS}: refusing a blind race "
                     "(the hunt windows are seeded from that file). Re-run "
                     "lw251_clut_ram_hunt.py scan against a running game first.")
        dump_dir = tempfile.gettempdir()
        if "--dump-dir" in sys.argv:
            try:
                dump_dir = sys.argv[sys.argv.index("--dump-dir") + 1]
            except IndexError:
                sys.exit("--dump-dir needs a path")
        os.makedirs(dump_dir, exist_ok=True)
        probe_file = os.path.join(dump_dir, ".lw251_write_test")
        open(probe_file, "w").close()
        os.remove(probe_file)
        Race(sweep="--no-sweep" not in sys.argv, dump_dir=dump_dir).run()
