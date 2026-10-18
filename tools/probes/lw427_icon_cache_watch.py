#!/usr/bin/env python
"""LW-427 Phase 0 leg 1: watch the game's equip icon caches, one manager at a time. READ ONLY.

Opens the game with PROCESS_QUERY_INFORMATION | PROCESS_VM_READ (via tools/lib/livebase.py) and
never writes, so it cannot hide, restore or corrupt anything.

WHY
  LW-381 hid an icon's cache records so the next screen reloaded the new glow, and LW-423 turned
  that off after the battle status page drew a blank row. The adversarial review of 2026-10-18
  found the likely cause: a screen only creates a missing entry when it calls Load, normally on
  open, and each UI context keeps its own entry vector in its own manager. So the questions are
  per manager. Which managers exist, which screen owns each, and when does each one throw its
  icon entries away and rebuild them (menu open or close, battle start or end)?

WHAT IT RECORDS
  Discovery (on start and on the "d" command) finds every UITextureParts manager holding an
  equip icon entry: icon URI strings, then aligned pointers to them (entries), then pointers to
  the entries (vector slots), then the (begin, end) pair at manager+0xE0/+0xE8 that brackets a
  slot. Each pass is timed, which is the sweep cost question.
  Every 100 ms it then reads the screen bytes (MenuOpenFlag, PartyBrowseFlag, BattleMode,
  PauseFlag, SubmenuFlag) and every known manager's vector, and logs per manager: an icon entry
  added, its load state reaching 0x0101 (with the load time in ms), an entry removed, and the
  vector moving. Owner labels typed into the console land on the same timeline.

USAGE (game running, a save loaded)
    python tools/probes/lw427_icon_cache_watch.py
  then type into this console and press Enter:
    <any label>   e.g. "party open", "inventory", "battle status", "won"
    d             rediscover managers (a new screen can bring a new manager)
    q             quit and print the summary
  Tape: %TEMP%\\lw427_cache_watch_<stamp>.jsonl, summary printed on quit.
"""
import ctypes
import ctypes.wintypes as w
import json
import os
import struct
import sys
import threading
import time
from pathlib import Path

import numpy as np

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from lib import offsets as _offsets  # noqa: E402
from lib.livebase import open_game, rebase  # noqa: E402

ICON_PREFIX = b"UITextureParts://ui/ffto/icon/equip_item"
SCHEME = b"UITextureParts://"
SCREEN_PINS = ["MenuOpenFlag", "PartyBrowseFlag", "BattleMode", "PauseFlag", "SubmenuFlag"]
POLL_S = 0.1
CHUNK = 4 << 20
MEM_COMMIT, MEM_PRIVATE = 0x1000, 0x20000
PAGE_GUARD, PAGE_NOACCESS, PAGE_NOCACHE, PAGE_WRITECOMBINE = 0x100, 0x01, 0x200, 0x400
LOADED = 0x0101
# A real entry vector holds well under this many pointers; a bigger span is two unrelated qwords.
MAX_VECTOR_BYTES = 0x40000

k32 = ctypes.WinDLL("kernel32", use_last_error=True)
k32.ReadProcessMemory.argtypes = [w.HANDLE, ctypes.c_void_p, ctypes.c_void_p, ctypes.c_size_t,
                                  ctypes.POINTER(ctypes.c_size_t)]
k32.VirtualQueryEx.argtypes = [w.HANDLE, ctypes.c_void_p, ctypes.c_void_p, ctypes.c_size_t]
k32.VirtualQueryEx.restype = ctypes.c_size_t


class MBI(ctypes.Structure):
    _fields_ = [("BaseAddress", ctypes.c_void_p), ("AllocationBase", ctypes.c_void_p),
                ("AllocationProtect", w.DWORD), ("_a", w.DWORD), ("RegionSize", ctypes.c_size_t),
                ("State", w.DWORD), ("Protect", w.DWORD), ("Type", w.DWORD), ("_b", w.DWORD)]


def regions(h, want_type=None):
    addr, out, mbi = 0, [], MBI()
    while addr < (1 << 47):
        if not k32.VirtualQueryEx(h, ctypes.c_void_p(addr), ctypes.byref(mbi), ctypes.sizeof(mbi)):
            break
        base, size = mbi.BaseAddress or 0, mbi.RegionSize
        if size == 0:
            break
        # Uncached and write combined pages are GPU mappings that crawl when read.
        if (mbi.State == MEM_COMMIT
                and not mbi.Protect & (PAGE_GUARD | PAGE_NOACCESS | PAGE_NOCACHE | PAGE_WRITECOMBINE)
                and (want_type is None or mbi.Type == want_type)):
            out.append((base, size))
        addr = base + size
    return out


def read(h, addr, n):
    buf = ctypes.create_string_buffer(n)
    got = ctypes.c_size_t(0)
    if not k32.ReadProcessMemory(h, ctypes.c_void_p(addr), buf, n, ctypes.byref(got)):
        return None
    return buf.raw[:got.value]


def q(h, addr):
    b = read(h, addr, 8)
    return struct.unpack("<Q", b)[0] if b and len(b) == 8 else None


def cstr(h, addr, cap=160):
    b = read(h, addr, cap) or b""
    return b.split(b"\0", 1)[0].decode(errors="replace")


def chunks(h, want_type=None):
    for base, size in regions(h, want_type):
        off = 0
        while off < size:
            n = min(CHUNK, size - off)
            buf = read(h, base + off, n)
            if buf:
                yield base + off, buf
            off += n


def find_strings(h):
    """Addresses of every resident equip icon URI string in private memory."""
    out = []
    for addr, buf in chunks(h, MEM_PRIVATE):
        i = buf.find(ICON_PREFIX)
        while i >= 0:
            out.append(addr + i)
            i = buf.find(ICON_PREFIX, i + 1)
    return out


def pointer_pass(h, targets):
    """{location: value} for every 8 aligned qword in private memory whose value is in targets."""
    want = np.array(sorted(set(targets)), dtype=np.uint64)
    out = {}
    if want.size == 0:
        return out
    for addr, buf in chunks(h, MEM_PRIVATE):
        pad = (-addr) % 8
        body = buf[pad:]
        arr = np.frombuffer(body[:len(body) // 8 * 8], dtype="<u8")
        near = np.nonzero((arr >= want[0]) & (arr <= want[-1]))[0]
        if near.size == 0:
            continue
        for i in near[np.isin(arr[near], want)]:
            out[addr + pad + int(i) * 8] = int(arr[i])
    return out


def manager_pass(h, slots):
    """Manager addresses M whose [M+0xE0, M+0xE8) pair brackets at least one vector slot."""
    srt = np.array(sorted(set(slots)), dtype=np.uint64)
    found = set()
    if srt.size == 0:
        return found
    for addr, buf in chunks(h, MEM_PRIVATE):
        pad = (-addr) % 8
        body = buf[pad:]
        arr = np.frombuffer(body[:len(body) // 8 * 8], dtype="<u8")
        if arr.size < 2:
            continue
        a, c = arr[:-1], arr[1:]
        span = c - a
        ok = (c > a) & (span <= MAX_VECTOR_BYTES) & (span % 8 == 0) & (a % 8 == 0)
        if not ok.any():
            continue
        idx = np.searchsorted(srt, a)
        hit = ok & (idx < srt.size)
        nxt = srt[np.minimum(idx, srt.size - 1)]
        hit &= nxt < c
        for i in np.nonzero(hit)[0]:
            found.add(addr + pad + int(i) * 8 - 0xE0)
    # The end and capacity pair at +0xE8 of a real manager can bracket a stale slot too.
    return {m for m in found if m - 8 not in found}


def discover(h, emit):
    t0 = time.perf_counter()
    strings = find_strings(h)
    t1 = time.perf_counter()
    entries = pointer_pass(h, strings)
    t2 = time.perf_counter()
    slots = pointer_pass(h, entries.keys())
    t3 = time.perf_counter()
    mgrs = manager_pass(h, slots.keys())
    t4 = time.perf_counter()
    emit({"ev": "discover", "strings": len(strings), "entries": len(entries), "slots": len(slots),
          "managers": [hex(m) for m in sorted(mgrs)],
          "ms": {"strings": round((t1 - t0) * 1000), "entries": round((t2 - t1) * 1000),
                 "slots": round((t3 - t2) * 1000), "managers": round((t4 - t3) * 1000)}})
    return mgrs


class Watch:
    def __init__(self, h, base, tape):
        self.h, self.tape = h, tape
        O = _offsets.load()
        self.pins = {n: rebase(v, base) for n, v in zip(SCREEN_PINS, _offsets.require(SCREEN_PINS, O))}
        self.t0 = time.perf_counter()
        self.lock = threading.Lock()
        self.mgrs = {}          # mgr -> {"vec": (b, e), "entries": {entry: [name, state, t_add, t_loaded]}}
        self.names = {}         # name ptr -> decoded name, or None when it is not an equip icon
        self.screen = None
        self.events = []
        self.polls = 0

    def now(self):
        return round((time.perf_counter() - self.t0) * 1000)

    def emit(self, rec):
        rec = {"t": self.now(), **rec}  # a caller supplied t wins
        if self.screen is not None:
            rec.setdefault("screen", self.screen)
        with self.lock:
            self.events.append(rec)
            self.tape.write(json.dumps(rec) + "\n")
            self.tape.flush()
        if rec["ev"] in ("label", "discover", "adopt", "screen", "lost"):
            print(json.dumps(rec))

    def is_manager(self, m):
        """A real manager's vector is almost all pointers to entries named by a UITextureParts URI;
        a stray qword pair that happens to bracket a slot is not."""
        b, e = q(self.h, m + 0xE0), q(self.h, m + 0xE8)
        if b is None or e is None or e <= b or e - b > MAX_VECTOR_BYTES:
            return False
        raw = read(self.h, b, min(e - b, 64 * 8)) or b""
        ptrs = struct.unpack(f"<{len(raw) // 8}Q", raw[:len(raw) // 8 * 8])
        named = sum((read(self.h, q(self.h, p) or 0, len(SCHEME)) or b"") == SCHEME for p in ptrs)
        return bool(ptrs) and named * 5 >= len(ptrs) * 4

    def adopt(self, mgrs):
        kept, pairs = [], {st["vec"] for st in self.mgrs.values() if st["vec"]}
        for m in sorted(mgrs):
            pair = (q(self.h, m + 0xE0), q(self.h, m + 0xE8))
            if m in self.mgrs or pair in pairs or not self.is_manager(m):
                continue
            pairs.add(pair)
            kept.append(m)
        self.emit({"ev": "adopt", "candidates": len(mgrs),
                   "kept": [{"mgr": hex(m), "cap": hex(q(self.h, m + 0xF0) or 0)} for m in kept]})
        for m in kept:
            self.mgrs[m] = {"vec": None, "entries": {}, "bad": 0, "baseline": True, "skip": set()}

    def icon_name(self, ptr):
        """Only hits are cached, so a junk read of a freed string can never blind a later icon."""
        if ptr in self.names:
            return self.names[ptr]
        s = cstr(self.h, ptr)
        if not s.encode().startswith(ICON_PREFIX):
            return None
        self.names[ptr] = s
        return s

    def poll_screen(self):
        vals = {}
        for n, a in self.pins.items():
            b = read(self.h, a, 1)
            vals[n] = b[0] if b else None
        if vals != self.screen:
            self.screen = vals
            self.emit({"ev": "screen", "screen": vals})

    def poll_manager(self, m, st):
        # One read for both ends, because a vector move between two reads gives a torn pair.
        pair = read(self.h, m + 0xE0, 16)
        b, e = struct.unpack("<QQ", pair) if pair and len(pair) == 16 else (None, None)
        if b is None or e < b or e - b > MAX_VECTOR_BYTES:
            st["bad"] += 1
            if st["vec"] is not None and st["bad"] >= 3:
                self.emit({"ev": "lost", "mgr": hex(m), "icons": len(st["entries"])})
                for ent, (name, *_rest) in st["entries"].items():
                    self.emit({"ev": "remove", "mgr": hex(m), "entry": hex(ent), "name": name, "why": "manager unreadable"})
                st["entries"].clear()
                st["vec"] = None
            return
        st["bad"] = 0
        if st["vec"] != (b, e):
            if st["vec"] is not None and st["vec"][0] != b:
                self.emit({"ev": "vector", "mgr": hex(m), "from": hex(st["vec"][0]), "to": hex(b), "n": (e - b) // 8})
            st["vec"] = (b, e)
        raw = read(self.h, b, e - b) if e > b else b""
        if raw is None:
            return
        live = set(struct.unpack(f"<{len(raw) // 8}Q", raw[:len(raw) // 8 * 8]))
        ents = st["entries"]
        for ent in [x for x in ents if x not in live]:
            name = ents.pop(ent)[0]
            self.emit({"ev": "remove", "mgr": hex(m), "entry": hex(ent), "name": name})
        for ent in live:
            if ent in ents or ent in st["skip"] or ent & 7:
                continue
            head = read(self.h, ent, 0x5A)
            if not head or len(head) < 0x5A:
                continue
            name = self.icon_name(struct.unpack_from("<Q", head, 0)[0])
            if name is None:
                st["skip"].add(ent)
                continue
            state = struct.unpack_from("<H", head, 0x58)[0]
            t = self.now()
            ents[ent] = [name, state, t, t if state == LOADED else None]
            self.emit({"ev": "add", "mgr": hex(m), "entry": hex(ent), "name": name, "state": hex(state),
                       "id": struct.unpack_from("<I", head, 8)[0], "baseline": st["baseline"]})
        for ent, rec in list(ents.items()):
            head = read(self.h, ent, 0x5A)
            if not head or len(head) < 0x5A:
                continue
            # The game can reuse an entry in place for another icon, which never leaves the vector.
            name = self.icon_name(struct.unpack_from("<Q", head, 0)[0])
            if name != rec[0]:
                self.emit({"ev": "renamed", "mgr": hex(m), "entry": hex(ent), "from": rec[0], "to": name})
                if name is None:
                    del ents[ent]
                    continue
                t = self.now()
                rec[:] = [name, 0, t, None]
            state = struct.unpack_from("<H", head, 0x58)[0]
            if rec[3] is None and state == LOADED:
                rec[1], rec[3] = LOADED, self.now()
                self.emit({"ev": "loaded", "mgr": hex(m), "entry": hex(ent), "name": rec[0], "load_ms": rec[3] - rec[2]})
            elif rec[3] is not None and state != LOADED and rec[1] == LOADED:
                rec[1], rec[2], rec[3] = state, self.now(), None
                self.emit({"ev": "unloaded", "mgr": hex(m), "entry": hex(ent), "name": rec[0], "state": hex(state)})

    def poll(self):
        self.poll_screen()
        self.polls += 1
        for m, st in list(self.mgrs.items()):
            # Non icon entries are skipped for speed and forgotten now and then in case one is reused.
            if self.polls % 50 == 0:
                st["skip"].clear()
            self.poll_manager(m, st)
            if st["vec"] is not None:
                st["baseline"] = False


def summarize(watch):
    ev = watch.events
    loads = [r["load_ms"] for r in ev if r["ev"] == "loaded"]
    print("\n==== SUMMARY ====")
    for r in (x for x in ev if x["ev"] == "discover"):
        print(f"discover at {r['t']} ms: {len(r['managers'])} managers, {r['entries']} entries, pass ms {r['ms']}")
    for m in sorted(watch.mgrs):
        mine = [r for r in ev if r.get("mgr") == hex(m)]
        adds = sum(r["ev"] == "add" for r in mine)
        rems = sum(r["ev"] == "remove" for r in mine)
        print(f"manager {hex(m)}: {adds} adds, {rems} removes, {len(watch.mgrs[m]['entries'])} icon entries now")
    if loads:
        loads.sort()
        print(f"load times over {len(loads)} entries: min {loads[0]} ms, median {loads[len(loads) // 2]} ms, max {loads[-1]} ms")
    print(f"tape: {watch.tape.name}")


def main():
    _pid, h, base = open_game()
    stamp = time.strftime("%Y%m%d_%H%M%S")
    path = Path(os.environ.get("TEMP", ".")) / f"lw427_cache_watch_{stamp}.jsonl"
    with open(path, "w", encoding="utf-8") as tape:
        watch = Watch(h, base, tape)
        watch.emit({"ev": "start", "base": hex(base), "wall": time.strftime("%Y-%m-%d %H:%M:%S")})
        print("discovering managers, this can take a while...")
        watch.adopt(discover(h, watch.emit))
        cmds = []
        # Labels are stamped as typed, since a rediscovery can hold the loop for a minute.
        threading.Thread(target=lambda: [cmds.append((watch.now(), line.strip())) for line in sys.stdin],
                         daemon=True).start()
        print("watching. type a label and Enter, d to rediscover, q to quit.")
        try:
            while True:
                while cmds:
                    t, c = cmds.pop(0)
                    if c == "q":
                        return
                    if c:
                        watch.emit({"t": t, "ev": "label", "label": c})
                    if c == "d":
                        watch.adopt(discover(h, watch.emit))
                watch.poll()
                time.sleep(POLL_S)
        finally:
            summarize(watch)


if __name__ == "__main__":
    main()
