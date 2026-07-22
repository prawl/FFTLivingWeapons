"""Interactive JobCommand grant probe: put ANY ability id into a unit's command list, live.

WHY THIS EXISTS
---------------
The Defender's "Provoke" command needs an existing-but-unused ability id to hijack, because this
game has no Provoke and the ability BEHAVIOR table override is permanently parked.  Several
candidate ids survive offline analysis, but offline analysis cannot answer the only questions that
matter: does the id RENDER in the menu, can it be SELECTED, does it TARGET one enemy, and does the
engine actually EXECUTE it or silently swallow it?

Rather than write a Provoke module per candidate and redeploy between each, this probe performs the
same JobCommand injection the shipped Barrage and Shadow Blade signatures perform, from Python, for
an arbitrary ability id.  Try three candidates in one sitting, no rebuild, no restart.

It is a straight port of the PROVEN mechanism, not a new one.  Every constant and every bit of the
extend/learned math comes from LivingWeapon/Barrage.Policy.cs, which is live-proven twice (Barrage
cast end-to-end 2026-06-10; Shadow Blade on a Knight 2026-06-14) and unit-tested.

It also edits the ability ACTION table, which is what makes a hijack viable rather than merely
possible: a candidate's real effect is usually the thing that disqualifies it, and that effect is
often a single byte.  Ability 189 reaches five tiles, targets one enemy, costs nothing and charges
instantly -- an ideal Provoke shell -- but inflicts Immobilize, and the Immobilize is one byte at
row offset +15 that merely INDEXES the inflict table.  `action` holds that byte at another value so
you can find out, live, what the ability does without it.

SAFETY
------
Every byte a hold will disturb is snapshotted to the OS temp directory BEFORE the first write, and
restored on exit, on Ctrl+C, and on demand via `restore`.  Snapshots survive a crash, so a hard kill
mid-hold is recoverable.  There is ONE SNAPSHOT FILE PER HOLD, because a grant hold and an action
hold are meant to run in separate terminals at the same time and a shared file would let each
clobber the other's undo; bare `restore` sweeps every outstanding one.  Every write is read-verified.
Both tables live in the executable's image, which is not writable by default, so the probe flips
page protection for the write and puts it back.

TWO THINGS THAT WILL BITE
-------------------------
1. THE MENU MAY BE CONSTRUCTION-BAKED.  A unit's command list is believed to be built when the unit
   is constructed, so a grant applied mid-battle may not appear until the next battle loads (memory
   note menu-command-injection; the kit-transplant ledger row reads the other way, so this is
   genuinely open).  Grant OUT of battle, hold through the formation screen, and let the battle
   construct with the grant already in place.  Keep the hold running the whole time.
2. THE GRANT IS JOB-GLOBAL.  JobCommand records belong to a JOB, not a unit, so every unit of that
   job -- INCLUDING ENEMIES -- inherits the injected ability.  That is a known shipped leak, not a
   bug in this probe.  Expect to see enemies of the same job carrying the command.

Verbs
-----
  python tools\\probes\\ability_grant_probe.py roster
      List roster slots: job byte, resolved JobCommand record, secondary command, learned index.
      Read-only. Use it to pick a slot.

  python tools\\probes\\ability_grant_probe.py dump <rec>
      Decode a JobCommand record: its extend flags and all 16 ability slots, with names.
      Read-only.

  python tools\\probes\\ability_grant_probe.py grant <rosterSlot> <abilityId> [--rec N] [--slot N]
      Inject the ability into the roster slot's job record (or --rec), hold the learned bit, and
      HOLD both until Ctrl+C.  Restores on exit.

  python tools\\probes\\ability_grant_probe.py action <abilityId> <offset|inflict> <value> [--base A]
      Hold one byte of an ability's ACTION row, so you can see what the ability does with a
      different effect.  `inflict` is a name for offset 15, the status index; 0 means "apply no
      status at all".  Reports whether the ENGINE fought the write, which decides whether a shipped
      version must hold the byte every tick or may set it once.
      e.g. action 189 inflict 0    strip the Immobilize off ability 189

      --base MATTERS: there are TWO copies of this table, back to back, 368*20 = 0x1CC0 bytes apart
      (found live 2026-07-22 by ability_table_probe.py findrow, after an edit to the first copy read
      back correctly and changed nothing in game).  ACTION_VA below is the FIRST copy; the second
      begins at 0x14078B2DC.  Both hold identical data, so no anchor check can tell them apart --
      only an edit can.  Pass --base to aim at the other one.

  python tools\\probes\\ability_grant_probe.py restore [key]
      Restore outstanding holds. With no key, sweeps every snapshot in the temp directory -- the
      after-a-crash case, where you should not have to remember what was held.

  python tools\\probes\\ability_grant_probe.py --selftest
      Offline checks (no game needed): the extend/learned bit math against Barrage.Policy.cs.
"""
import ctypes
import ctypes.wintypes as w
import json
import sqlite3
import sys
import tempfile
import time
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
ABILITY_SQLITE = ROOT / "working" / "nxd_ability" / "ability.sqlite"
# One snapshot file PER HOLD, not one global file: a grant hold and an action-row hold run in
# separate terminals at the same time, and a shared filename would let each clobber the other's
# undo. `restore` sweeps them all.
SNAPSHOT_DIR = Path(tempfile.gettempdir())
SNAPSHOT_GLOB = "lw_ability_probe_*.json"


def snapshot_path(key):
    return SNAPSHOT_DIR / f"lw_ability_probe_{key}.json"

PROCESS_VM_READ = 0x0010
PROCESS_VM_WRITE = 0x0020
PROCESS_VM_OPERATION = 0x0008
PROCESS_QUERY_INFORMATION = 0x0400
PAGE_EXECUTE_READWRITE = 0x40
PROC_NAME = "fft_enhanced.exe"

# --- JobCommand table (LivingWeapon/Barrage.cs:43-46, 1.5 re-found live 2026-06-17) -------------
ABILITY_BASE = 0x14067E213   # record 0's AbilityId1 byte
REC_SIZE = 25                # 3 flag bytes + 16 ability bytes + 6 RSM bytes
FLAG_PREFIX = 3
ABILITY_COUNT = 16

# --- roster (Offsets.cs RosterBase/RosterStride, 1.5/1.5.1) --------------------------------------
ROSTER_BASE = 0x1411A7D10
ROSTER_STRIDE = 0x258
R_JOB = 0x02          # u8 job byte
R_SECONDARY = 0x07    # u8 secondary command = a JobCommand record id
R_LEARNED_BASE = 0x32
LEARNED_STRIDE = 3
ROSTER_SLOTS = 20

# --- ability ACTION table (located offline 2026-07-22, CONFIRMED live the same day by
#     tools/probes/ability_table_probe.py: 367/368 row sentinels, MP anchors exact, and a negative
#     control 7 bytes away scoring 0/368). 368 rows of 20 bytes. Field offsets within a row:
#     +4 Range, +5 EffectArea, +6 Vertical, +12 Formula, +13 X, +14 Y, +15 InflictStatus,
#     +16 CT, +17 MP. Bytes 7-11 are targeting/behavior flags, not yet decoded.
ACTION_VA = 0x14078961C
ACTION_STRIDE = 20
ACTION_ROWS = 368
ACTION_INFLICT = 15   # the byte that indexes the inflict-status table

HOLD_MS = 150         # re-assert cadence; menu write-backs are what the hold defends against

k32 = ctypes.windll.kernel32


# --------------------------------------------------------------------------- pure math
# Verbatim from LivingWeapon/Barrage.Policy.cs -- covered by --selftest against its documented
# anchors so a divergence from the shipped C# shows up offline, not in a live session.
def slot_byte(ability_id):
    """The byte stored in an ability slot: the id's low 8 bits."""
    return ability_id & 0xFF


def extend_bit(slot_idx0):
    """The extend bit for a 0-indexed slot in the LE-composed ExtAb u16. MSB-first PER BYTE:
    byte0 covers slots 1-8 (bit7 = slot 1), byte1 covers slots 9-16 (bit7 = slot 9)."""
    return (0x80 >> (slot_idx0 % 8)) << (8 * (slot_idx0 // 8))


def find_empty_slot(ability_bytes, ext_ab):
    """First empty 1-indexed slot, or -1. A slot is empty only when its byte is 0 AND its extend
    bit is 0, because byte 0 with the extend bit set is ability 256."""
    for i in range(ABILITY_COUNT):
        if ability_bytes[i] == 0 and (ext_ab & extend_bit(i)) == 0:
            return i + 1
    return -1


def learned_byte_index(slot_idx1):
    return 0 if slot_idx1 <= 8 else 1


def learned_bit_mask(slot_idx1):
    return 1 << (7 - (slot_idx1 - 1) % 8)


def resolve_job(job):
    """Generic job band 74..92 -> (record id, learned jobIdx). Returns (None, None) otherwise.
    Special-executor jobs are NOT excluded here the way Barrage excludes them: this is a probe and
    trying a swallowing executor on purpose is a legitimate experiment. `roster` flags them."""
    if job < 74 or job > 92:
        return None, None
    return job - 69, (17 if job == 92 else job - 74)


SPECIAL_EXECUTORS = {75: "Chemist/Items", 77: "Archer/Aim", 87: "Dragoon/Jump",
                     89: "Ninja/Throw", 90: "Arithmetician"}


def flag_addr(rec):
    return ABILITY_BASE + rec * REC_SIZE - FLAG_PREFIX


def ability_addr(rec):
    return ABILITY_BASE + rec * REC_SIZE


def learned_addr(roster_slot, job_idx, slot_idx1):
    return (ROSTER_BASE + roster_slot * ROSTER_STRIDE + R_LEARNED_BASE
            + job_idx * LEARNED_STRIDE + learned_byte_index(slot_idx1))


# --------------------------------------------------------------------------- process access
class PROCESSENTRY32(ctypes.Structure):
    _fields_ = [("dwSize", w.DWORD), ("cntUsage", w.DWORD), ("th32ProcessID", w.DWORD),
                ("th32DefaultHeapID", ctypes.POINTER(ctypes.c_ulong)), ("th32ModuleID", w.DWORD),
                ("cntThreads", w.DWORD), ("th32ParentProcessID", w.DWORD), ("pcPriClassBase", ctypes.c_long),
                ("dwFlags", w.DWORD), ("szExeFile", ctypes.c_char * 260)]


def find_pid(name=PROC_NAME):
    snap = k32.CreateToolhelp32Snapshot(0x00000002, 0)
    if snap == -1:
        return None
    entry = PROCESSENTRY32()
    entry.dwSize = ctypes.sizeof(PROCESSENTRY32)
    hits = []
    ok = k32.Process32First(snap, ctypes.byref(entry))
    while ok:
        if entry.szExeFile.decode(errors="ignore").lower() == name.lower():
            hits.append(entry.th32ProcessID)
        ok = k32.Process32Next(snap, ctypes.byref(entry))
    k32.CloseHandle(snap)
    return hits[0] if hits else None


class Mem:
    def __init__(self, pid):
        self.h = k32.OpenProcess(PROCESS_VM_READ | PROCESS_VM_WRITE | PROCESS_VM_OPERATION
                                 | PROCESS_QUERY_INFORMATION, False, pid)
        if not self.h:
            raise SystemExit(f"could not open pid {pid} (error {k32.GetLastError()})")

    def read(self, addr, size):
        buf = (ctypes.c_ubyte * size)()
        got = ctypes.c_size_t(0)
        ok = k32.ReadProcessMemory(self.h, ctypes.c_void_p(addr), ctypes.byref(buf),
                                   ctypes.c_size_t(size), ctypes.byref(got))
        return bytes(buf) if ok and got.value == size else None

    def write(self, addr, data):
        """Write and READ BACK. The JobCommand table lives in the image, which is not writable by
        default, so fall back to flipping page protection and putting it back. Returns True only
        when the bytes actually read back as written."""
        if self._raw_write(addr, data) and self.read(addr, len(data)) == data:
            return True
        old = w.DWORD(0)
        if not k32.VirtualProtectEx(self.h, ctypes.c_void_p(addr), ctypes.c_size_t(len(data)),
                                    PAGE_EXECUTE_READWRITE, ctypes.byref(old)):
            return False
        ok = self._raw_write(addr, data)
        restored = w.DWORD(0)
        k32.VirtualProtectEx(self.h, ctypes.c_void_p(addr), ctypes.c_size_t(len(data)),
                             old, ctypes.byref(restored))
        return ok and self.read(addr, len(data)) == data

    def _raw_write(self, addr, data):
        buf = (ctypes.c_ubyte * len(data))(*data)
        put = ctypes.c_size_t(0)
        return bool(k32.WriteProcessMemory(self.h, ctypes.c_void_p(addr), ctypes.byref(buf),
                                           ctypes.c_size_t(len(data)), ctypes.byref(put))
                    and put.value == len(data))

    def close(self):
        if self.h:
            k32.CloseHandle(self.h)
            self.h = None


def load_names():
    if not ABILITY_SQLITE.exists():
        return {}
    con = sqlite3.connect(ABILITY_SQLITE)
    try:
        return {r[0]: r[1] for r in con.execute('SELECT Key, Name FROM "Ability-en"')}
    except sqlite3.Error:
        return {}
    finally:
        con.close()


def name_of(names, ability_id):
    n = names.get(ability_id)
    if n is None:
        return "<NULL>"
    return n if str(n).strip() else "<empty>"


# --------------------------------------------------------------------------- record decoding
def read_record(mem, rec):
    """(ext_ab, ability_bytes, raw25) for a JobCommand record, or (None, None, None)."""
    raw = mem.read(flag_addr(rec), REC_SIZE)
    if raw is None:
        return None, None, None
    ext_ab = raw[0] | (raw[1] << 8)
    return ext_ab, list(raw[FLAG_PREFIX:FLAG_PREFIX + ABILITY_COUNT]), raw


def slot_ability_id(ability_bytes, ext_ab, i):
    return ability_bytes[i] + (256 if ext_ab & extend_bit(i) else 0)


def verb_dump(mem, names, rec):
    ext_ab, ability_bytes, raw = read_record(mem, rec)
    if raw is None:
        print(f"record {rec}: UNREADABLE at 0x{flag_addr(rec):X}")
        return 1
    print(f"record {rec} @ 0x{flag_addr(rec):X}   ExtAb 0x{ext_ab:04X}   raw {raw.hex(' ')}")
    for i in range(ABILITY_COUNT):
        ability_id = slot_ability_id(ability_bytes, ext_ab, i)
        if ability_id == 0:
            print(f"  slot {i + 1:2d}: (empty)")
        else:
            ext = "+ext" if ext_ab & extend_bit(i) else "    "
            print(f"  slot {i + 1:2d}: {ability_id:3d} {ext}  {name_of(names, ability_id)}")
    free = find_empty_slot(ability_bytes, ext_ab)
    print(f"  first free slot: {free if free > 0 else 'NONE -- this record is full'}")
    return 0


def verb_roster(mem, names):
    print("slot  job  record  jobIdx  secondary  note")
    for s in range(ROSTER_SLOTS):
        base = ROSTER_BASE + s * ROSTER_STRIDE
        raw = mem.read(base + R_JOB, 6)
        if raw is None:
            continue
        job = raw[0]
        secondary = mem.read(base + R_SECONDARY, 1)
        secondary = secondary[0] if secondary else 0
        rec, job_idx = resolve_job(job)
        note = ""
        if job == 0:
            continue
        if rec is None:
            note = "job outside the generic 74-92 band (story unique / monster) -- use --rec"
        elif job in SPECIAL_EXECUTORS:
            note = f"SPECIAL EXECUTOR ({SPECIAL_EXECUTORS[job]}): may swallow foreign ids"
        print(f"{s:4d}  {job:3d}  {str(rec):>6}  {str(job_idx):>6}  {secondary:9d}  {note}")
    return 0


# --------------------------------------------------------------------------- grant
def save_snapshot(key, entries):
    """entries: [(label, addr, original_bytes), ...] -- everything this run will disturb, captured
    BEFORE the first write so a hard kill mid-hold stays recoverable via `restore`."""
    path = snapshot_path(key)
    path.write_text(json.dumps(
        [{"label": lbl, "addr": addr, "bytes": list(raw)} for lbl, addr, raw in entries],
        indent=1), encoding="utf-8")
    return path


def restore_one(mem, path):
    entries = json.loads(path.read_text(encoding="utf-8"))
    all_ok = True
    for e in entries:
        ok = mem.write(e["addr"], bytes(e["bytes"]))
        all_ok = all_ok and ok
        print(f"  restore {e['label']} @ 0x{e['addr']:X}: {'OK' if ok else 'FAILED'}")
    if all_ok:
        path.unlink()
    else:
        print(f"  snapshot KEPT at {path} -- rerun `restore` once the game is responsive")
    return all_ok


def verb_restore(mem, key=None):
    """With a key, restore that hold. Without one, sweep EVERY outstanding snapshot -- the
    after-a-crash case, where you do not want to remember what was held."""
    paths = [snapshot_path(key)] if key else sorted(SNAPSHOT_DIR.glob(SNAPSHOT_GLOB))
    paths = [p for p in paths if p.exists()]
    if not paths:
        print(f"no outstanding snapshots in {SNAPSHOT_DIR} -- nothing to restore")
        return 1
    all_ok = True
    for p in paths:
        print(f"{p.name}:")
        all_ok = restore_one(mem, p) and all_ok
    return 0 if all_ok else 1


def verb_grant(mem, names, roster_slot, ability_id, rec_override, slot_override):
    base = ROSTER_BASE + roster_slot * ROSTER_STRIDE
    jr = mem.read(base + R_JOB, 1)
    if jr is None:
        print(f"roster slot {roster_slot} unreadable")
        return 1
    job = jr[0]
    rec, job_idx = resolve_job(job)
    if rec_override is not None:
        rec = rec_override
        if job_idx is None:
            job_idx = 0
            print(f"note: job {job} is outside the generic band; assuming learned jobIdx 0")
    if rec is None:
        print(f"job {job} does not resolve to a JobCommand record -- pass --rec N explicitly")
        return 1
    if job in SPECIAL_EXECUTORS:
        print(f"WARNING: job {job} is a special executor ({SPECIAL_EXECUTORS[job]}). It may render")
        print("         the ability and silently swallow it at confirm. That is a real result, not")
        print("         a probe failure -- just record it as such.")

    ext_ab, ability_bytes, raw = read_record(mem, rec)
    if raw is None:
        print(f"record {rec} unreadable at 0x{flag_addr(rec):X}")
        return 1
    slot1 = slot_override if slot_override else find_empty_slot(ability_bytes, ext_ab)
    if slot1 < 1:
        print(f"record {rec} has no empty ability slot. Pass --slot N to overwrite slot N")
        print("(the original 25 bytes are snapshotted and restored, so overwriting is recoverable)")
        return 1

    l_addr = learned_addr(roster_slot, job_idx, slot1)
    l_raw = mem.read(l_addr, 1)
    if l_raw is None:
        print(f"learned byte unreadable at 0x{l_addr:X}")
        return 1

    snap = save_snapshot(f"rec{rec}", [(f"JobCommand record {rec}", flag_addr(rec), raw),
                                       ("learned byte", l_addr, l_raw)])
    print(f"snapshot -> {snap}")
    print(f"roster slot {roster_slot} (job {job}) -> record {rec}, learned jobIdx {job_idx}")
    print(f"injecting ability {ability_id} ({name_of(names, ability_id)}) into slot {slot1}")
    print(f"  slot byte 0x{slot_byte(ability_id):02X}, extend bit "
          f"{'SET' if ability_id >= 256 else 'clear'} (0x{extend_bit(slot1 - 1):04X})")
    print(f"  learned bit: byte 0x{l_addr:X} mask 0x{learned_bit_mask(slot1):02X}")
    if ability_id >= 256:
        print(f"  NOTE: if the menu shows ability {slot_byte(ability_id)} "
              f"({name_of(names, slot_byte(ability_id))}) instead, the extend bit did not land.")
    print()
    print("HOLDING. Open the unit's command menu to check. Ctrl+C restores and exits.")

    slot_idx0 = slot1 - 1
    writes = 0
    failures = 0
    learned_reasserts = 0
    try:
        while True:
            cur_ext, cur_ab, cur_raw = read_record(mem, rec)
            if cur_raw is not None:
                want_byte = slot_byte(ability_id)
                want_ext = (cur_ext | extend_bit(slot_idx0)) if ability_id >= 256 \
                    else (cur_ext & ~extend_bit(slot_idx0))
                if cur_ab[slot_idx0] != want_byte:
                    if mem.write(ability_addr(rec) + slot_idx0, bytes([want_byte])):
                        writes += 1
                    else:
                        failures += 1
                if cur_ext != want_ext:
                    if mem.write(flag_addr(rec), bytes([want_ext & 0xFF, (want_ext >> 8) & 0xFF])):
                        writes += 1
                    else:
                        failures += 1
            cur_learned = mem.read(l_addr, 1)
            if cur_learned is not None and not (cur_learned[0] & learned_bit_mask(slot1)):
                if mem.write(l_addr, bytes([cur_learned[0] | learned_bit_mask(slot1)])):
                    learned_reasserts += 1
                else:
                    failures += 1
            time.sleep(HOLD_MS / 1000.0)
    except KeyboardInterrupt:
        print()
        print(f"held: {writes} record writes, {learned_reasserts} learned-bit re-asserts, "
              f"{failures} FAILED writes")
        if failures:
            print("  a nonzero failure count means the hold was not continuous -- treat any")
            print("  in-game observation from this run as unreliable.")
        verb_restore(mem, f"rec{rec}")
    return 0


# --------------------------------------------------------------------------- action-row edits
def action_row_addr(ability_id, base=None):
    return (base if base is not None else ACTION_VA) + ability_id * ACTION_STRIDE


def verb_action(mem, names, ability_id, offset, value, base=None):
    """Hold one byte of an ability's ACTION row. The motivating case: ability 189 reaches five
    tiles, targets a single enemy, costs nothing and charges instantly -- an ideal Provoke shell --
    but inflicts Immobilize, which comes from ONE byte (+15) that merely indexes the inflict table.
    Zero it and the ability inflicts nothing; repoint it and it inflicts something else.

    The engine may rebuild this table (the JobCommand table does, at boot, which is why Barrage
    re-asserts every tick), so this HOLDS rather than pokes once, and reports how often it had to
    re-write. A nonzero re-assert count is itself a finding: it means the table is engine-owned and
    a shipped version must hold it too."""
    if not 0 <= ability_id < ACTION_ROWS:
        print(f"ability id {ability_id} is outside the action table (0..{ACTION_ROWS - 1})")
        return 1
    if not 0 <= offset < ACTION_STRIDE:
        print(f"offset {offset} is outside a {ACTION_STRIDE}-byte row")
        return 1
    row_addr = action_row_addr(ability_id, base)
    before = mem.read(row_addr, ACTION_STRIDE)
    if before is None:
        print(f"action row for ability {ability_id} unreadable at 0x{row_addr:X}")
        return 1
    if before[0:4] != b"\xff\xff\xff\xff":
        print(f"row at 0x{row_addr:X} lacks the FF FF FF FF sentinel -- this is not an action row.")
        print("Run `ability_table_probe.py verify` before writing anything here.")
        return 1

    addr = row_addr + offset
    snap = save_snapshot(f"action{ability_id}_{row_addr:X}", [(f"ability {ability_id} action row", row_addr, before)])
    print(f"snapshot -> {snap}")
    which = "PINNED table" if base is None else f"table base 0x{base:X}"
    print(f"ability {ability_id} ({name_of(names, ability_id)}) action row @ 0x{row_addr:X}  [{which}]")
    print(f"  before: {before.hex(' ')}")
    print(f"  holding byte +{offset} (0x{addr:X}): 0x{before[offset]:02X} -> 0x{value:02X}")
    if offset == ACTION_INFLICT:
        print("  (+15 is InflictStatus: 0 means the ability applies no status at all)")
    print()
    print("HOLDING. Cast the ability in game. Ctrl+C restores the whole row and exits.")

    writes = 0
    failures = 0
    try:
        while True:
            cur = mem.read(addr, 1)
            if cur is not None and cur[0] != value:
                if mem.write(addr, bytes([value])):
                    writes += 1
                else:
                    failures += 1
            time.sleep(HOLD_MS / 1000.0)
    except KeyboardInterrupt:
        print()
        print(f"held: {writes} re-writes, {failures} FAILED writes")
        if writes > 1:
            print("  more than one re-write means the ENGINE is rewriting this byte, so a shipped")
            print("  version must hold it every tick rather than poking it once.")
        elif writes == 1:
            print("  exactly one write and no re-writes: the value stuck on its own, so the engine")
            print("  does not own this byte while a battle runs.")
        if failures:
            print("  FAILED writes mean the hold had gaps -- treat what you saw as unreliable.")
        verb_restore(mem, f"action{ability_id}_{row_addr:X}")
    return 0


def parse_poke(spec):
    """'0xADDR=hexbytes' -> (addr, bytes). Pure, so --selftest covers the parsing."""
    if "=" not in spec:
        raise ValueError(f"expected <addr>=<hexbytes>, got {spec!r}")
    addr_s, hex_s = spec.split("=", 1)
    data = bytes.fromhex(hex_s.replace(" ", "").replace("_", ""))
    if not data:
        raise ValueError(f"no bytes given in {spec!r}")
    return int(addr_s, 0), data


def verb_poke(mem, specs):
    """Hold arbitrary byte-runs at arbitrary addresses, several at once in ONE terminal.

    Exists because making a hijacked ability behave takes more than one simultaneous edit and one
    hold per terminal does not scale. The motivating case: give ability 189 a harmless marker status
    instead of Immobilize, which needs a new row written into the inflict-status table AND the
    ability's action-row +15 repointed at it, both held together while the grant runs elsewhere.

    Every original is snapshotted before the first write and restored on exit."""
    try:
        targets = [parse_poke(s) for s in specs]
    except ValueError as e:
        print(f"bad argument: {e}")
        return 2

    entries = []
    for addr, data in targets:
        before = mem.read(addr, len(data))
        if before is None:
            print(f"0x{addr:X} is not readable for {len(data)} bytes -- refusing to write anything")
            return 1
        entries.append((f"{len(data)}B @ 0x{addr:X}", addr, before))

    key = "poke_" + "_".join(f"{a:X}" for a, _ in targets)
    snap = save_snapshot(key, entries)
    print(f"snapshot -> {snap}")
    for (addr, data), (_, _, before) in zip(targets, entries):
        print(f"  0x{addr:X}: {before.hex(' ')}  ->  {data.hex(' ')}")
    print()
    print("HOLDING. Ctrl+C restores every byte and exits.")

    writes = {a: 0 for a, _ in targets}
    failures = 0
    try:
        while True:
            for addr, data in targets:
                cur = mem.read(addr, len(data))
                if cur is not None and cur != data:
                    if mem.write(addr, data):
                        writes[addr] += 1
                    else:
                        failures += 1
            time.sleep(HOLD_MS / 1000.0)
    except KeyboardInterrupt:
        print()
        for addr, _ in targets:
            n = writes[addr]
            note = "stuck after one write" if n == 1 else (
                "ENGINE KEEPS REWRITING IT" if n > 1 else "never needed a write")
            print(f"  0x{addr:X}: {n} write(s), {note}")
        if failures:
            print(f"  {failures} FAILED writes -- the hold had gaps, treat observations as unreliable")
        verb_restore(mem, key)
    return 0


# --------------------------------------------------------------------------- selftest
def selftest():
    fails = []

    def check(label, got, want):
        if got != want:
            fails.append(f"{label}: got {got!r}, want {want!r}")

    # Anchors quoted in Barrage.Policy.cs's own doc comments.
    check("extend bit slot 1", extend_bit(0), 0x0080)
    check("extend bit slot 9", extend_bit(8), 0x8000)
    check("extend bit slot 10", extend_bit(9), 0x4000)
    check("slot byte of 358 (Barrage)", slot_byte(358), 102)
    check("slot byte of 360", slot_byte(360), 104)
    check("slot byte of 189", slot_byte(189), 189)
    check("learned byte index slot 8", learned_byte_index(8), 0)
    check("learned byte index slot 9", learned_byte_index(9), 1)
    check("learned mask slot 1", learned_bit_mask(1), 0x80)
    check("learned mask slot 9", learned_bit_mask(9), 0x80)
    check("learned mask slot 16", learned_bit_mask(16), 0x01)

    # Job resolution anchors: Squire 74 -> rec 5; Dancer 92 shares Bard's learned index 17.
    check("job 74", resolve_job(74), (5, 0))
    check("job 83 (Thief)", resolve_job(83), (14, 9))
    check("job 92 (Dancer)", resolve_job(92), (23, 17))
    check("job 93 unmapped", resolve_job(93), (None, None))

    # Record addressing: record 0's ability bytes start at ABILITY_BASE by definition.
    check("ability addr rec 0", ability_addr(0), ABILITY_BASE)
    check("flag addr rec 0", flag_addr(0), ABILITY_BASE - 3)
    check("ability addr rec 7", ability_addr(7), ABILITY_BASE + 7 * 25)

    # poke spec parsing
    check("poke spec addr", parse_poke("0x14080FC4F=000000010080")[0], 0x14080FC4F)
    check("poke spec bytes", parse_poke("0x14080FC4F=000000010080")[1],
          bytes([0x00, 0x00, 0x00, 0x01, 0x00, 0x80]))
    check("poke spec single byte", parse_poke("0x14078C1AF=1d")[1], bytes([0x1D]))
    check("poke spec tolerates spaces", parse_poke("0x100=00 01 02")[1], bytes([0, 1, 2]))

    # Inflict-table row addressing, and the Wall bit (band +0x48/0x01 per
    # tools/probes/evasive_stance_probe.py:91-92): byte index 3 of the five status bytes.
    check("inflict row 29 @ pinned base", 0x14080FBA1 + 29 * 6, 0x14080FC4F)
    check("live action row 189 inflict byte", 0x14078B2DC + 189 * 20 + 15, 0x14078C1AF)

    # Action-row addressing, anchored on the id whose row was read live today.
    check("action row of id 0", action_row_addr(0), ACTION_VA)
    check("action row of id 189", action_row_addr(189), 0x14078A4E0)
    check("inflict byte of id 189", action_row_addr(189) + ACTION_INFLICT, 0x14078A4EF)
    check("action row of id 360", action_row_addr(360), 0x14078B23C)

    # An empty slot needs BOTH a zero byte and a clear extend bit (byte 0 + ext = ability 256).
    check("free slot in an empty record", find_empty_slot([0] * 16, 0x0000), 1)
    check("byte 0 with extend set is NOT free", find_empty_slot([0] * 16, extend_bit(0)), 2)
    check("full record", find_empty_slot([1] * 16, 0x0000), -1)
    check("first gap found", find_empty_slot([1, 1, 0] + [1] * 13, 0x0000), 3)

    if fails:
        print("SELFTEST FAILED")
        for f in fails:
            print("  " + f)
        return 1
    print("selftest OK")
    return 0


def main():
    args = sys.argv[1:]
    if not args or args[0] in ("-h", "--help"):
        print(__doc__)
        return 0
    if args[0] == "--selftest":
        return selftest()

    rec_override = None
    slot_override = None
    base_override = None
    positional = []
    i = 0
    while i < len(args):
        if args[i] == "--rec":
            rec_override = int(args[i + 1], 0)
            i += 2
        elif args[i] == "--slot":
            slot_override = int(args[i + 1], 0)
            i += 2
        elif args[i] == "--base":
            base_override = int(args[i + 1], 0)
            i += 2
        else:
            positional.append(args[i])
            i += 1

    pid = find_pid()
    if pid is None:
        print(f"{PROC_NAME} is not running")
        return 2
    mem = Mem(pid)
    names = load_names()
    try:
        verb = positional[0]
        if verb == "roster":
            return verb_roster(mem, names)
        if verb == "dump":
            if len(positional) < 2:
                print("usage: dump <rec>")
                return 2
            return verb_dump(mem, names, int(positional[1], 0))
        if verb == "restore":
            return verb_restore(mem, positional[1] if len(positional) > 1 else None)
        if verb == "grant":
            if len(positional) < 3:
                print("usage: grant <rosterSlot> <abilityId> [--rec N] [--slot N]")
                return 2
            return verb_grant(mem, names, int(positional[1], 0), int(positional[2], 0),
                              rec_override, slot_override)
        if verb == "poke":
            if len(positional) < 2:
                print("usage: poke <addr>=<hexbytes> [<addr>=<hexbytes> ...]")
                print("   e.g. poke 0x14080FC4F=000000010080 0x14078C1AF=1d")
                return 2
            return verb_poke(mem, positional[1:])
        if verb == "action":
            if len(positional) < 4:
                print("usage: action <abilityId> <offset|inflict> <value> [--base <addr>]")
                print("   e.g. action 189 inflict 0     strip the status ability 189 inflicts")
                return 2
            off = ACTION_INFLICT if positional[2] == "inflict" else int(positional[2], 0)
            return verb_action(mem, names, int(positional[1], 0), off, int(positional[3], 0),
                               base_override)
        print(f"unknown verb {verb!r}")
        print(__doc__)
        return 2
    finally:
        mem.close()


if __name__ == "__main__":
    sys.exit(main())
