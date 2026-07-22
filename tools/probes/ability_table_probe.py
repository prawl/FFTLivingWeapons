"""Live validation of the game's hardcoded ability BEHAVIOR table (READ-ONLY).

WHY THIS EXISTS
---------------
Choosing an ability id to hijack for the Defender's "Provoke" command needs to know what each
candidate ability actually DOES: its range, whether it hits one target or an area, what status it
inflicts, its MP cost and charge time.  None of that is available from the modloader's table dumps:
`TableData/AbilityActionData.xml` is a 288-byte EMPTY STUB (`<AbilityActionTable></AbilityActionTable>`
plus a comment saying it provides nothing), and every vanilla row in the pristine
`overrideabilityactiondata` decode reads -1 (no override).  The data lives only in the executable's
own hardcoded table.

That table was located offline by scanning `FFT_enhanced.exe` on disk (2026-07-22).  THIS PROBE'S
ONLY JOB is to decide whether that offline read describes the LIVE process, because everything
downstream (which id we rename, which id we grant, whether a live session is worth spending) rests
on it.  It writes nothing.

WHAT IT CHECKS, AND WHY EACH CHECK EARNS ITS PLACE
--------------------------------------------------
1. STRUCTURAL.  Every action row begins with the 4-byte sentinel FF FF FF FF.  A correct anchor
   makes 368/368 rows carry it; a wrong anchor makes almost none.
2. SEMANTIC.  Known MP costs read back at their known ability ids (Cure 6, Cura 10, Curaga 16).
   Structure alone can be satisfied by a coincidentally similar region; MP costs cannot.
3. NEGATIVE CONTROL.  The same two checks are re-run at a deliberately WRONG address (the pin
   shifted by a few bytes).  They MUST fail there.  Without this, a check that passes everywhere
   proves nothing, which is the exact failure mode the 2026-07-22 step-6 instrument was rebuilt to
   avoid ("a confident wrong number is worse than no number").
A run that cannot satisfy 1 and 2 at the pin, AND fail them at the control, reports NOT CONFIRMED
and tells you to run `find`.  It never prints a decoded candidate table off an unconfirmed anchor.

ADDRESSING
----------
The image base is fixed at 0x140000000 (no ASLR), so file offsets map to virtual addresses by a
constant delta.  Anchor for the delta: the JobCommand table sits at file offset 0x67D6DB and its
virtual address is known independently from LivingWeapon/Barrage.cs:43 (0x14067E213 is record 0's
first ability byte; the rec-8 signature that located it lives at 0x14067E2DB).  0x67D6DB + delta =
0x14067E2DB gives delta = 0x140000C00, which the PE section table corroborates.

THE DECOY (read this before trusting any verdict here)
------------------------------------------------------
The action table exists TWICE, back to back.  The file-offset arithmetic above lands on the FIRST
copy, and the engine reads the SECOND, one table length (368 x 20 = 0x1CC0) later.  Both copies
hold identical data, so every check this probe performs -- row sentinels, MP anchors, the negative
control -- passes at BOTH.  A verdict of CONFIRMED therefore means "a real action table is here",
not "this is the table the engine reads".  Only an edit distinguishes them, and that cost a live
cycle on 2026-07-22.  Defaults now point at the live copy (ACTION_VA); the decoy stays named as
ACTION_DECOY_VA so `verify` can show it passing and make the point unmissable.

Verbs
-----
  python tools\\probes\\ability_table_probe.py verify
      The gate.  Confirm the table is where we think it is, then print the decoded rows for the
      Provoke candidate ids.  Read-only.

  python tools\\probes\\ability_table_probe.py row <id> [<id> ...]
      Decode specific ability ids (raw 20 bytes plus named fields).  Read-only.

  python tools\\probes\\ability_table_probe.py find
      The pin failed.  Scan the image for the table's row-sentinel signature and report every
      candidate base, with the same anchor checks applied to each.  Read-only.

  python tools\\probes\\ability_table_probe.py findrow <id> [--pattern <hex>] [lo] [hi]
      Find EVERY copy of one ability's 20-byte row. Use it when an edit to the pinned table has no
      effect in game: hold the pin at a changed value, then search for the ORIGINAL bytes, and any
      hit elsewhere is the copy the engine is really reading. Read-only.

  python tools\\probes\\ability_table_probe.py orphans
      List every ability id whose shipped name is NULL/empty (the hijack pool), with its decoded
      behavior row.  Read-only.

  python tools\\probes\\ability_table_probe.py --selftest
      Offline checks (no game needed): address arithmetic and the row decoder.
"""
import ctypes
import ctypes.wintypes as w
import sqlite3
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
ABILITY_SQLITE = ROOT / "working" / "nxd_ability" / "ability.sqlite"

PROCESS_VM_READ = 0x0010
PROCESS_QUERY_INFORMATION = 0x0400
PROC_NAME = "fft_enhanced.exe"

# file offset -> virtual address delta; see ADDRESSING in the module docstring.
FILE_TO_VA = 0x140000C00

# The ability ACTION table: 368 rows of 20 bytes. File offset found offline 2026-07-22.
ACTION_FILE_OFF = 0x788A1C
ACTION_ROWS = 368
ACTION_STRIDE = 20
ROW_SENTINEL = b"\xff\xff\xff\xff"                 # bytes 0..3 of every action row

# THERE ARE TWO COPIES OF THIS TABLE, BACK TO BACK, AND ONLY ONE OF THEM IS READ.
# The offline file-offset scan finds the FIRST copy. Writes to it land, read back perfectly, and
# change nothing in game (found live 2026-07-22, after an edit that verified clean did nothing).
# The engine reads the SECOND copy, exactly one table length later. Both hold identical data, so
# no sentinel check, no MP anchor and no read-back can tell them apart: only an EDIT can.
# Every default in this file therefore aims at the live copy. The decoy is kept named, not
# deleted, because `verify` deliberately demonstrates that it passes every check too.
ACTION_DECOY_VA = ACTION_FILE_OFF + FILE_TO_VA     # 0x14078961C -- accepted writes, engine ignores
ACTION_VA = ACTION_DECOY_VA + ACTION_ROWS * ACTION_STRIDE   # 0x14078B2DC -- the one that matters

# The inflict-status combination table: 128 rows of 6 bytes, laid out [mode][s0..s4]. The MODE
# BYTE COMES FIRST. Row N is what an action row's InflictStatus byte (+15) indexes into; s0..s4 are
# the five status bitfield bytes mirroring the band's composed layout (+0x45..+0x49, MSB-first).
# CORRECTED 2026-07-22: this base was one byte late (0x80EFA1) with the mode read from the row's
# END. Reading one byte late still put s0..s4 on the real status bytes, so status decodes were
# right, but every printed mode belonged to the NEXT row. That cost a live cycle: a hand-written row
# got correct status bytes and kept its own mode of 0x00 (None), cast at 0%, and was wrongly read as
# "the engine refuses rows it did not ship". Base verified 128/128 against the modloader's own
# decode and against ItemOptionsData.xml OptionType (14/128 at the old framing).
INFLICT_FILE_OFF = 0x80EFA0
INFLICT_VA = INFLICT_FILE_OFF + FILE_TO_VA         # 0x14080FBA0
INFLICT_ROWS = 128
INFLICT_STRIDE = 6

# Field offsets inside a 20-byte action row. Names and order come from the modloader's own Nex
# layout (Nex/Layouts/ffto/OverrideAbilityActionData.layout); +11 Element was confirmed from data
# rather than assumed: 74 rows are nonzero and every one is a single element bit except id 158
# Hallowed Bolt (Holy|Lightning). Bytes 18-19 are padding, zero on all 736 physical entries.
F_RANGE, F_AREA, F_VERT = 4, 5, 6
F_FLAGS1, F_FLAGS2, F_FLAGS3, F_FLAGS4, F_ELEMENT = 7, 8, 9, 10, 11
F_FORMULA, F_X, F_Y, F_INFLICT, F_CT, F_MP = 12, 13, 14, 15, 16, 17

# Inflict-row mode byte (row offset 0).
INFLICT_MODES = {0x00: "None", 0x10: "Cancel", 0x20: "Separate", 0x40: "Random", 0x80: "AllOrNothing"}

# Semantic anchors: ability id -> expected MP cost. These are ordinary player spells whose costs
# are common knowledge and are independent of anything this probe is trying to establish.
MP_ANCHORS = {1: 6, 2: 10, 3: 16}

# The ids the Provoke arc is choosing between (2026-07-22 candidate analysis).
CANDIDATES = [40, 184, 189, 219, 220, 357, 360, 366]
# Control: 358 is Barrage, already hijacked and shipped by this repo, so its row is a known
# quantity -- a sanity read, not an anchor.
CONTROLS = [358]


# --------------------------------------------------------------------------- process access
class MODULEENTRY32(ctypes.Structure):
    _fields_ = [("dwSize", w.DWORD), ("th32ModuleID", w.DWORD), ("th32ProcessID", w.DWORD),
                ("GlblcntUsage", w.DWORD), ("ProccntUsage", w.DWORD), ("modBaseAddr", ctypes.POINTER(ctypes.c_byte)),
                ("modBaseSize", w.DWORD), ("hModule", w.HMODULE), ("szModule", ctypes.c_char * 256),
                ("szExePath", ctypes.c_char * 260)]


class PROCESSENTRY32(ctypes.Structure):
    _fields_ = [("dwSize", w.DWORD), ("cntUsage", w.DWORD), ("th32ProcessID", w.DWORD),
                ("th32DefaultHeapID", ctypes.POINTER(ctypes.c_ulong)), ("th32ModuleID", w.DWORD),
                ("cntThreads", w.DWORD), ("th32ParentProcessID", w.DWORD), ("pcPriClassBase", ctypes.c_long),
                ("dwFlags", w.DWORD), ("szExeFile", ctypes.c_char * 260)]


k32 = ctypes.windll.kernel32


def find_pid(name=PROC_NAME):
    """PID of the running game, or None. Picks the largest working set if several match."""
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
    """Read-only process memory. Every read is bounded and returns None on failure -- never raises,
    so a bad address reports honestly instead of aborting a live session."""

    def __init__(self, pid):
        self.h = k32.OpenProcess(PROCESS_VM_READ | PROCESS_QUERY_INFORMATION, False, pid)
        if not self.h:
            raise SystemExit(f"could not open pid {pid} for reading (error {k32.GetLastError()})")

    def read(self, addr, size):
        buf = (ctypes.c_ubyte * size)()
        got = ctypes.c_size_t(0)
        ok = k32.ReadProcessMemory(self.h, ctypes.c_void_p(addr), ctypes.byref(buf),
                                   ctypes.c_size_t(size), ctypes.byref(got))
        if not ok or got.value != size:
            return None
        return bytes(buf)

    def close(self):
        if self.h:
            k32.CloseHandle(self.h)
            self.h = None


# --------------------------------------------------------------------------- decoding
def action_addr(ability_id, base=ACTION_VA):
    return base + ability_id * ACTION_STRIDE


def decode_row(raw):
    """Decode a 20-byte action row into named fields. Pure -- covered by --selftest."""
    if raw is None or len(raw) != ACTION_STRIDE:
        return None
    return {
        "sentinel_ok": raw[0:4] == ROW_SENTINEL,
        "range": raw[F_RANGE], "area": raw[F_AREA], "vert": raw[F_VERT],
        "element": raw[F_ELEMENT],
        "flags": (raw[F_FLAGS1], raw[F_FLAGS2], raw[F_FLAGS3], raw[F_FLAGS4]),
        "formula": raw[F_FORMULA], "x": raw[F_X], "y": raw[F_Y],
        "inflict": raw[F_INFLICT], "ct": raw[F_CT], "mp": raw[F_MP],
        "raw": raw.hex(" "),
    }


def load_names():
    """Key -> (Name, Description, IconId) from the pristine vanilla decode. Empty dict if absent:
    names are a convenience for reading the output, never part of a verdict."""
    if not ABILITY_SQLITE.exists():
        return {}
    con = sqlite3.connect(ABILITY_SQLITE)
    try:
        rows = con.execute('SELECT Key, Name, Description, IconId FROM "Ability-en"').fetchall()
    except sqlite3.Error:
        return {}
    finally:
        con.close()
    return {r[0]: (r[1], r[2], r[3]) for r in rows}


def name_of(names, ability_id):
    n = names.get(ability_id, (None, None, None))[0]
    if n is None:
        return "<NULL>"
    return n if n.strip() else "<empty>"


# --------------------------------------------------------------------------- the anchor gate
def check_anchor(mem, base):
    """Run the structural and semantic checks at `base`. Returns a dict of OBSERVATIONS, with no
    verdict attached -- the caller decides. Recording what was seen rather than asserting what
    should be seen is deliberate (2026-07-22 instrument-rebuild lesson)."""
    span = mem.read(base, ACTION_ROWS * ACTION_STRIDE)
    if span is None:
        return {"readable": False, "sentinels": 0, "rows": 0, "mp": {}}
    missing = [i for i in range(ACTION_ROWS)
               if span[i * ACTION_STRIDE:i * ACTION_STRIDE + 4] != ROW_SENTINEL]
    mp = {}
    for ability_id, expected in MP_ANCHORS.items():
        row = decode_row(span[ability_id * ACTION_STRIDE:(ability_id + 1) * ACTION_STRIDE])
        mp[ability_id] = (row["mp"] if row else None, expected)
    return {"readable": True, "sentinels": ACTION_ROWS - len(missing), "missing": missing,
            "rows": ACTION_ROWS, "mp": mp}


# A handful of rows may legitimately lack the sentinel: AbilityData.xml documents id 0 as
# "Empty/Dummy", and the table's tail may hold similar placeholders. The signal this check trades
# on is the SEPARATION between the pin and the control, not perfection -- the control scores 0/368,
# so a near-total run is decisive and demanding 368/368 only produces false negatives (observed
# live 2026-07-22: 367/368 at a pin whose MP anchors all matched).
SENTINEL_TOLERANCE = 4


def anchor_passes(obs):
    if not obs["readable"]:
        return False
    if len(obs["missing"]) > SENTINEL_TOLERANCE:
        return False
    return all(got == exp for got, exp in obs["mp"].values())


def print_anchor(label, obs):
    if not obs["readable"]:
        print(f"  {label}: NOT READABLE")
        return
    print(f"  {label}: row sentinel FF FF FF FF on {obs['sentinels']}/{obs['rows']} rows "
          f"(tolerance: up to {SENTINEL_TOLERANCE} may legitimately lack it)")
    if obs["missing"]:
        shown = obs["missing"][:12]
        tail = "" if len(obs["missing"]) == len(shown) else f" ... +{len(obs['missing']) - len(shown)} more"
        print(f"      rows without the sentinel: {shown}{tail}")
    for ability_id, (got, exp) in sorted(obs["mp"].items()):
        mark = "ok" if got == exp else "MISMATCH"
        print(f"      MP of ability {ability_id}: read {got}, expected {exp}  [{mark}]")


# --------------------------------------------------------------------------- inflict table
def decode_inflict(mem, index):
    """The five status bitfield bytes an InflictStatus index applies, as (byte, mask) pairs against
    the band's composed layout base +0x45. Returns None when unreadable or index 0 (= no status)."""
    if index == 0:
        return []
    raw = mem.read(INFLICT_VA + index * INFLICT_STRIDE, INFLICT_STRIDE)
    if raw is None:
        return None
    bits = []
    for byte_idx in range(5):
        val = raw[1 + byte_idx]          # +1: the mode byte occupies row offset 0
        for bit in range(8):
            mask = 0x80 >> bit
            if val & mask:
                bits.append((0x45 + byte_idx, mask))
    return bits


# Band composed-status bit map (byte == band +0x45+N), from FFTHandsFree's StatusDecoder via
# tools/probes/evasive_stance_probe.py:84-95. "Wall" (+0x48/0x01) is the one the owner's 2026-07-22
# status sweep found rendering an icon with NO text description and no observable effect, which is
# what makes it the ideal Provoke marker: visible, inert, and unused by anything else.
STATUS_NAMES = {
    0x45: [(0x40, "Crystal"), (0x20, "Dead"), (0x10, "Undead"), (0x08, "Charging"),
           (0x04, "Jump"), (0x02, "Defending"), (0x01, "Performing")],
    0x46: [(0x80, "Petrify"), (0x40, "Invite"), (0x20, "Blind"), (0x10, "Confuse"),
           (0x08, "Silence"), (0x04, "Vampire"), (0x02, "Cursed"), (0x01, "Treasure")],
    0x47: [(0x80, "Oil"), (0x40, "Float"), (0x20, "Reraise"), (0x10, "Transparent"),
           (0x08, "Berserk"), (0x04, "Chicken"), (0x02, "Frog"), (0x01, "Critical")],
    0x48: [(0x80, "Poison"), (0x40, "Regen"), (0x20, "Protect"), (0x10, "Shell"),
           (0x08, "Haste"), (0x04, "Slow"), (0x02, "Stop"), (0x01, "Wall")],
    0x49: [(0x80, "Faith"), (0x40, "Innocent"), (0x20, "Charm"), (0x10, "Sleep"),
           (0x08, "DontMove"), (0x04, "DontAct"), (0x02, "Reflect"), (0x01, "DeathSentence")],
}


def status_name(off, mask):
    for m, n in STATUS_NAMES.get(off, []):
        if m == mask:
            return n
    return f"bit +0x{off:02X}/0x{mask:02X}"


def fmt_inflict(bits):
    if bits is None:
        return "unreadable"
    if not bits:
        return "none"
    return ", ".join(f"{status_name(off, mask)} (+0x{off:02X}/0x{mask:02X})" for off, mask in bits)


def verb_inflict(mem, base, want):
    """Dump the inflict-status combination table: which statuses each index applies.

    An ability's action row carries an INDEX into this table at +15, not a status directly. So
    changing what an ability inflicts means picking a different index here. Rows applying exactly
    ONE status are the useful ones; a row applying exactly one HARMLESS status is what a hijacked
    ability wants, because it keeps the ability's success roll at 100% (a zeroed index makes the
    targeting screen read 0%) while landing nothing that matters."""
    print(f"inflict table @ 0x{base:X}  ({INFLICT_ROWS} rows x {INFLICT_STRIDE} bytes)")
    print()
    matches = []
    singles = []
    for idx in range(INFLICT_ROWS):
        raw = mem.read(base + idx * INFLICT_STRIDE, INFLICT_STRIDE)
        if raw is None:
            continue
        bits = []
        for byte_idx in range(5):
            for bit in range(8):
                mask = 0x80 >> bit
                if raw[1 + byte_idx] & mask:      # +1: mode byte is at row offset 0
                    bits.append((0x45 + byte_idx, mask))
        mode = raw[0]
        if not bits:
            continue
        names = [status_name(o, m) for o, m in bits]
        flag = ""
        if len(bits) == 1:
            singles.append((idx, names[0]))
            flag = "  <- single status"
        if want and want.lower() in (n.lower() for n in names):
            matches.append((idx, names, mode))
            flag += "  <<< MATCH"
        mode_name = INFLICT_MODES.get(mode, "?")
        print(f"  idx {idx:3d}  mode 0x{mode:02X} {mode_name:<12} {', '.join(names)}{flag}")

    print()
    print(f"{len(singles)} indices apply exactly one status.")
    if want:
        print()
        if not matches:
            print(f"NO index applies {want!r}. To use it you would have to WRITE a row here: pick an")
            print("  index nothing references, then write SIX bytes -- the mode byte FIRST (0x80")
            print("  AllOrNothing is what every working single-status row uses), then the five status")
            print("  bytes. Leaving the mode at 0x00 (None) makes the ability cast at 0%.")
        else:
            for idx, names, mode in matches:
                sole = " (ALONE -- usable as-is)" if len(names) == 1 else f" (together with {len(names)-1} other status(es))"
                print(f"  idx {idx} applies {want}{sole}, mode 0x{mode:02X}")
    return 0


# --------------------------------------------------------------------------- report
def report_ids(mem, names, ids, base=ACTION_VA):
    for ability_id in ids:
        raw = mem.read(action_addr(ability_id, base), ACTION_STRIDE)
        row = decode_row(raw)
        if row is None:
            print(f"  id {ability_id:3d}  UNREADABLE at 0x{action_addr(ability_id, base):X}")
            continue
        bits = decode_inflict(mem, row["inflict"])
        target = "single target" if row["area"] == 0 else f"AREA radius {row['area']}"
        rng = "weapon range" if row["range"] == 0 else str(row["range"])
        print(f"  id {ability_id:3d}  {name_of(names, ability_id):<22} "
              f"range {rng:<13} {target:<18} mp {row['mp']:<3} ct {row['ct']:<3} formula {row['formula']:<3}")
        print(f"           inflict idx {row['inflict']:<3} -> {fmt_inflict(bits)}")
        if not row["sentinel_ok"]:
            print("           WARNING: row sentinel absent -- this row is not what we think it is")
        print(f"           raw {row['raw']}")


# --------------------------------------------------------------------------- verbs
def verb_verify(mem, names):
    print(f"ability action table pin: 0x{ACTION_VA:X}  ({ACTION_ROWS} rows x {ACTION_STRIDE} bytes)")
    print(f"decoy copy:               0x{ACTION_DECOY_VA:X}  (one table length earlier)")
    print()
    print("ANCHOR CHECKS")
    at_pin = check_anchor(mem, ACTION_VA)
    print_anchor("at the pin      ", at_pin)
    # Negative control: the same checks a few bytes off must FAIL, or the checks prove nothing.
    control_base = ACTION_VA + 7
    at_control = check_anchor(mem, control_base)
    print_anchor(f"at pin+7 (control)", at_control)
    # The decoy is NOT a control: it is expected to PASS. Printing it is the whole point -- it is
    # the standing reminder that these checks cannot identify which copy the engine reads.
    at_decoy = check_anchor(mem, ACTION_DECOY_VA)
    print_anchor("at the decoy    ", at_decoy)
    print()

    pin_ok = anchor_passes(at_pin)
    control_ok = anchor_passes(at_control)
    if anchor_passes(at_decoy):
        print("NOTE: the decoy copy passes every check as well, exactly as expected. That is not a")
        print("      failure, it is the trap: structure cannot tell the two copies apart, so the")
        print(f"      pin must stay at 0x{ACTION_VA:X} and any write aimed here must say why.")
        print()
    if pin_ok and not control_ok:
        print("VERDICT: CONFIRMED. A real action table is at the pin, and the checks are not")
        print("         vacuous (they fail 7 bytes away). This does NOT by itself prove the engine")
        print("         reads THIS copy rather than the decoy; only an edit proves that, and the")
        print("         2026-07-22 live session is what established which one is live.")
    elif pin_ok and control_ok:
        print("VERDICT: NOT CONFIRMED -- the checks passed at the control address too, so they are")
        print("         vacuous and prove nothing. Do not act on the rows below.")
        return 1
    else:
        print("VERDICT: NOT CONFIRMED at the pin. The table has moved or the delta is wrong.")
        print("         Run `find` to re-locate it. Do NOT guess.")
        return 1

    print()
    print("PROVOKE CANDIDATES")
    report_ids(mem, names, CANDIDATES)
    print()
    print("CONTROL (already shipped by this repo)")
    report_ids(mem, names, CONTROLS)
    return 0


def verb_find(mem):
    """Scan the image for runs of the row sentinel at ACTION_STRIDE spacing."""
    print("scanning for the action table signature (a long run of FF FF FF FF at 20-byte spacing)")
    lo, hi, chunk = 0x140000000, 0x141000000, 0x100000
    best = []
    addr = lo
    while addr < hi:
        span = mem.read(addr, chunk + ACTION_ROWS * ACTION_STRIDE)
        if span is not None:
            for off in range(0, chunk, 4):
                if span[off:off + 4] != ROW_SENTINEL:
                    continue
                run = 0
                while run < ACTION_ROWS:
                    p = off + run * ACTION_STRIDE
                    if span[p:p + 4] != ROW_SENTINEL:
                        break
                    run += 1
                if run >= 64:
                    best.append((addr + off, run))
        addr += chunk
    if not best:
        print("  no candidate found in 0x140000000..0x141000000")
        return 1
    best.sort(key=lambda t: -t[1])
    for base, run in best[:10]:
        obs = check_anchor(mem, base)
        verdict = "ANCHORS PASS" if anchor_passes(obs) else "anchors fail"
        delta = base - ACTION_VA
        print(f"  base 0x{base:X}  sentinel run {run:3d}  {verdict}  (pin{delta:+#x})")
    return 0


def verb_findrow(mem, ability_id, pattern_hex, lo, hi):
    """Find EVERY copy of one ability's 20-byte action row in the address space.

    The motivating case is sharper than a generic scan. On 2026-07-22 the row at the pinned table
    was edited live (InflictStatus 0x2D -> 0x00, read back confirmed) and the ability KEPT inflicting
    the status, so the engine is reading something else. If a second copy exists, it still holds the
    ORIGINAL bytes -- so searching for the original pattern while the pin is held at a different
    value separates the live copy from the one we are editing, in a single pass. Pass --pattern to
    search for bytes other than whatever the pin currently reads."""
    if pattern_hex:
        pattern = bytes.fromhex(pattern_hex.replace(" ", ""))
    else:
        pattern = mem.read(action_addr(ability_id), ACTION_STRIDE)
        if pattern is None:
            print("could not read the row at the pin to build a search pattern")
            return 1
    print(f"searching 0x{lo:X}..0x{hi:X} for ability {ability_id}'s row pattern:")
    print(f"  {pattern.hex(' ')}")
    print("  (a hit at an address OTHER than the pin is a second copy of the table)")

    hits = []
    chunk = 0x400000
    addr = lo
    scanned = 0
    while addr < hi:
        span = mem.read(addr, chunk + len(pattern))
        if span is not None:
            scanned += chunk
            start = 0
            while True:
                idx = span.find(pattern, start)
                if idx < 0:
                    break
                hits.append(addr + idx)
                start = idx + 1
        addr += chunk
    print(f"  readable bytes scanned: {scanned / 1048576:.0f} MB")
    if not hits:
        print("  NO hits. If even the pin did not match, the row changed under you.")
        return 1
    pin = action_addr(ability_id)
    for h in hits:
        if h == pin:
            print(f"  0x{h:X}  <- the pinned table (the one we edit)")
        else:
            delta = h - pin
            implied = h - ability_id * ACTION_STRIDE
            print(f"  0x{h:X}  SECOND COPY (pin{delta:+#x}); implied table base 0x{implied:X}")
    others = [h for h in hits if h != pin]
    print()
    if not others:
        # Only reachable when the pin itself matched, i.e. nothing else in memory carries this row.
        print("  Only one copy exists. So the engine reads THIS table and ignores our write, which")
        print("  means the value is consumed somewhere other than at cast time -- cached per battle,")
        print("  or baked into the unit when the battle is constructed.")
    else:
        print(f"  {len(others)} other copy/copies carry the ORIGINAL bytes. Re-run the edit against")
        print("  an implied base above (pass --base to the grant probe's `action` verb) to find out")
        print("  which one the engine actually consults.")
    return 0


def verb_orphans(mem, names):
    """Every ability whose shipped name is NULL or blank -- the hijack pool -- with its behavior."""
    pool = sorted(k for k, v in names.items() if v[0] is None or not str(v[0]).strip())
    print(f"{len(pool)} ability ids ship with no name: {pool}")
    print()
    report_ids(mem, names, pool)
    return 0


# --------------------------------------------------------------------------- selftest
def selftest():
    fails = []

    def check(label, got, want):
        if got != want:
            fails.append(f"{label}: got {got!r}, want {want!r}")

    check("delta anchors on the JobCommand table", 0x67D6DB + FILE_TO_VA, 0x14067E2DB)
    # The file-offset arithmetic yields the DECOY; the live table is one table length later.
    # Both are pinned so a future edit to either constant has to face this pair deliberately.
    check("decoy table VA (what the file offset finds)", ACTION_DECOY_VA, 0x14078961C)
    check("live table VA (what the engine reads)", ACTION_VA, 0x14078B2DC)
    check("the gap is exactly one table", ACTION_VA - ACTION_DECOY_VA, ACTION_ROWS * ACTION_STRIDE)
    check("inflict table VA", INFLICT_VA, 0x14080FBA0)
    check("inflict row 53 (Berserk)", INFLICT_VA + 53 * INFLICT_STRIDE, 0x14080FCDE)
    # The authored Provoke row and the byte that points at it, both on the LIVE table.
    check("authored inflict row 29", INFLICT_VA + 29 * INFLICT_STRIDE, 0x14080FC4E)
    check("ability 189 inflict byte", action_addr(189) + 15, 0x14078C1AF)
    check("row address of id 0", action_addr(0), ACTION_VA)
    check("row address of id 360", action_addr(360), ACTION_VA + 360 * ACTION_STRIDE)

    row = decode_row(bytes([0xFF, 0xFF, 0xFF, 0xFF, 3, 0, 0, 1, 0x51, 0x10, 0, 0, 41, 60, 0, 62, 0, 0, 0, 0]))
    check("decode flags", row["flags"], (1, 0x51, 0x10, 0))
    check("decode element", row["element"], 0)
    check("decode sentinel", row["sentinel_ok"], True)
    check("decode range", row["range"], 3)
    check("decode area", row["area"], 0)
    check("decode formula", row["formula"], 41)
    check("decode inflict", row["inflict"], 62)
    check("decode mp", row["mp"], 0)
    check("short row rejected", decode_row(b"\xff\xff"), None)

    bad = decode_row(bytes([0x00] * 20))
    check("absent sentinel detected", bad["sentinel_ok"], False)

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

    pid = find_pid()
    if pid is None:
        print(f"{PROC_NAME} is not running -- start the game and load a save first")
        return 2
    mem = Mem(pid)
    names = load_names()
    if not names:
        print(f"note: {ABILITY_SQLITE} not readable, ability names will show as <NULL>")
    try:
        verb = args[0]
        if verb == "verify":
            return verb_verify(mem, names)
        if verb == "find":
            return verb_find(mem)
        if verb == "findrow":
            # Consume --pattern AND its value; leaving the value behind made it parse as `lo`.
            pattern = None
            rest = []
            i = 1
            while i < len(args):
                if args[i] == "--pattern":
                    pattern = args[i + 1] if i + 1 < len(args) else None
                    i += 2
                    continue
                rest.append(args[i])
                i += 1
            if not rest:
                print("usage: findrow <abilityId> [--pattern <hex>] [lo] [hi]")
                return 2
            ability_id = int(rest[0], 0)
            lo = int(rest[1], 0) if len(rest) > 1 else 0x140000000
            hi = int(rest[2], 0) if len(rest) > 2 else 0x150000000
            return verb_findrow(mem, ability_id, pattern, lo, hi)
        if verb == "orphans":
            return verb_orphans(mem, names)
        if verb == "inflict":
            base = INFLICT_VA
            want = None
            rest = args[1:]
            i = 0
            while i < len(rest):
                if rest[i] == "--base":
                    base = int(rest[i + 1], 0)
                    i += 2
                    continue
                want = rest[i]
                i += 1
            return verb_inflict(mem, base, want)
        if verb == "row":
            ids = [int(a, 0) for a in args[1:]]
            if not ids:
                print("usage: row <id> [<id> ...]")
                return 2
            report_ids(mem, names, ids)
            return 0
        print(f"unknown verb {verb!r}")
        print(__doc__)
        return 2
    finally:
        mem.close()


if __name__ == "__main__":
    sys.exit(main())
