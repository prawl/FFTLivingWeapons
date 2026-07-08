"""Taunt / AI-focus discovery probe for live in-game testing.

GOAL (the taunt arc): force one enemy's AI to attack a chosen unit -- the runtime
half of the polearm +3 "taunt" signature.  The OFFLINE lever is confirmed:
the 40-byte ENTD unit record carries a per-unit AI target -- `+0x24 Target Unit ID`
plus `+0x23` bit 0x40 `FocusUnit` (see docs memory taunt-research-entd-target).
The engine loads that record into the live unit struct at battle start.  What we
DO NOT yet know is WHERE in the live 0x200 combat struct the loaded copy lands --
the band entry layout does NOT mirror the ENTD record 1:1.  So this is a DISCOVERY
probe, not a finished cheat: it lets you plant a candidate focus + target and HOLD
it while you watch whether the enemy retargets, sweeping offsets until one obeys.

Once a (flag_offset, mask, uid_offset) triple is proven live, it graduates into
Offsets.cs and a real Signature module -- exactly how poison/doom/charm did.

Verbs
-----
  python tools\\probes\\taunt_probe.py list
      Enumerate EVERY live unit on the map (both sides): band slot, side, the
      roster nameId (band +0x1E0, our one live identity handle), hp, level, pos.
      Read-only.  Use it to pick an attacker slot and a target slot.

  python tools\\probes\\taunt_probe.py focus [attacker_slot] [target_slot]
      Plant an AI-focus candidate on ATTACKER pointing at TARGET, then HOLD it
      (~150ms) and read-watch so you can eyeball whether the attacker's AI beelines
      the target.  With no slot args it prints the unit list and prompts, mirroring
      kill_all's confirm: type the attacker slot, then `x <slot>` for the target
      (e.g. `x 8`).  Ctrl+C restores the original bytes.

      The candidate write is (all overridable so you can sweep live):
        --flag  OFF:MASK  set bit MASK at attacker band+OFF   (FocusUnit analog)
        --uid   OFF[:w]   write the target's identity at attacker band+OFF
                          (w = 1 or 2 bytes; default 2 = the nameId we can read)
        --val   N         override the identity value written (default: target nameId)
      Defaults are a GUESS translated from the ENTD record and are almost certainly
      wrong -- that's the point; sweep OFF until the AI turns.  A write is always
      read-verified and every original byte is saved + restored on exit.

  python tools\\probes\\taunt_probe.py scan [target_slot]
      Locate-the-field helper.  Reads TARGET's nameId, then scans every OTHER live
      unit's full 0x200 struct for a u8/u16 equal to it and prints the hits.  Run
      this in a VANILLA escort/hunter battle (Zirekile / Riovanes rooftop) where a
      unit already has a real FocusUnit target baked into the ENTD -- the offset that
      shows up on the hunter but not on bystanders is the live Target-uid field.

  python tools\\probes\\taunt_probe.py apeek   <abs_hex> [width]
  python tools\\probes\\taunt_probe.py awatch  <abs_hex> [width] [seconds]
  python tools\\probes\\taunt_probe.py apoke   <abs_hex> <value> [width]
  python tools\\probes\\taunt_probe.py afreeze <abs_hex> <value> [width] [seconds]
      ABSOLUTE-address primitives for the "hide the overhead display" hunt (the
      band-relative peek/poke above can't reach a raw global).  Freeze-test a
      candidate that dispfind, the FFTHandsFree heap-diff, or Cheat Engine surfaced:
        awatch  -- poll the address live; press the in-battle '1' key and watch if the
                   value steps with the empty->bars->bars+names cycle (a real hit does).
        afreeze -- hold the address at its EMPTY value (re-write every ~8ms to beat the
                   ~16ms per-frame engine rewrite); Alt-Tab in and press '1' to force
                   bars ON.  If the bars refuse to appear and it STICKS, that's the
                   lever the Guardian/taunt signature holds to hide the red team-swap bar.
        apeek / apoke -- one-shot absolute read / write (apoke leaves it; no restore).
      Width = 1|2|4|8 bytes (default 1).  Seconds default 30.  Ctrl+C stops early.
      Seed candidates (untested): 0x140782970 (best; sits in the battle-globals
      cluster), 0x140D4A264 (FFTColorCustomizer "UI overlay present" global; blunt --
      likely gates the WHOLE HUD, not just the overhead bars).

  python tools\\probes\\taunt_probe.py --selftest
      Offline self-test (no game required): band-address math + arg parsing.
"""
import ctypes
import ctypes.wintypes as w
import struct
import sys
import time

# ---------------------------------------------------------------------------
# Process / memory constants  (all from LivingWeapon/Offsets.cs, 1.5 anchors)
# ---------------------------------------------------------------------------
PROCESS_VM_READ           = 0x0010
PROCESS_VM_WRITE          = 0x0020
PROCESS_VM_OPERATION      = 0x0008
PROCESS_QUERY_INFORMATION = 0x0400

# Band (authoritative live structs).  BandReadBase = CombatAnchor + BandEntry - 24*stride.
COMBAT_ANCHOR = 0x141855CE0   # Offsets.CombatAnchor (1.5)
BAND_ENTRY    = 0x1C
COMBAT_STRIDE = 0x200
BAND_SLOTS    = 49            # n = -24 .. +24
BAND_READ_BASE = COMBAT_ANCHOR + BAND_ENTRY - 24 * COMBAT_STRIDE
PLAYER_SLOT_THRESHOLD = 24    # slots >= 24 are player-side (n >= 0)

# Band-entry field offsets (Offsets.cs A* constants)
A_LEVEL  = 0x0D
A_BRAVE  = 0x0E
A_FAITH  = 0x10
A_HP     = 0x14   # u16
A_MAXHP  = 0x16   # u16
A_GX     = 0x33
A_GY     = 0x34
A_NAMEID = 0x1E0  # u16 roster-nameId back-ref (Offsets.ANameId) -- our live identity handle

# Invisible/Transparent status (Offsets.AInvisible band+0x47 bit 0x10): "makes the AI ignore
# the unit -- single-target enemies skip it; AoE splash can still reach it." Feign Death uses it.
# Path-B taunt: hold it on every player unit EXCEPT the wielder so enemies funnel onto the wielder.
A_INVIS_OFF  = 0x47
A_INVIS_MASK = 0x10

# Team / friend-foe flag (found live 2026-07-08 via teamscan; matches PSX Check_Allies/Enemies).
# bit 0x10: allies read 0, enemies read 1. Two copies -- the base ENTD flag at combat+0x05 and the
# per-unit "modified" copy the AI target-check XORs, at combat+0x1EE = band+0x1D2 (PSX +0x1BA).
# Flip a hidden ally's bit to 1 (match enemies) -> the enemy AI sees it as same-team and skips it,
# with NO Invisible status (no icon/transparency).  This is the continuity-clean taunt candidate.
A_TEAM_OFF  = 0x1D2   # band-relative (== combat+0x1EE, the AI-consulted "modified" team flag)
A_TEAM_MASK = 0x10

MAX_LEVEL_DRIFT = 9

# --- taunt candidate defaults (GUESS -- meant to be swept, not trusted) --------
# The ENTD record has FocusUnit at +0x23 bit 0x40 and Target uid at +0x24, but those
# are offline-record offsets; the live combat struct layout differs.  These defaults
# just give `focus` something to plant on the first run; override with --flag/--uid.
# UID field FOUND LIVE 2026-07-08 (Zirekile Falls, Ovelia scan): band +0x17E (u16)
# held Ovelia's uid (12) on hunters {13,17,18} and Delita's uid (5) on {12,15} --
# clean complementary partition => this is the AI Target-uid field. Combat-relative
# +0x19A.  The FocusUnit FLAG offset is still unknown (default mask 0 = skip the flag
# write; existing hunters already have it set, so uid-only redirect tests the field).
DEFAULT_FLAG_OFF  = 0x00
DEFAULT_FLAG_MASK = 0x00   # 0 = skip flag write entirely (uid-only)
DEFAULT_UID_OFF   = 0x17E
DEFAULT_UID_WIDTH = 2

# ---------------------------------------------------------------------------
# Process handle (lazy, fail-gracefully)  -- same pattern as battle_cheats.py
# ---------------------------------------------------------------------------
k32   = ctypes.windll.kernel32
psapi = ctypes.windll.psapi
_HANDLE = None


def _open_process(name="fft_enhanced.exe"):
    arr = (w.DWORD * 4096)()
    needed = w.DWORD()
    psapi.EnumProcesses(ctypes.byref(arr), ctypes.sizeof(arr), ctypes.byref(needed))
    want = PROCESS_QUERY_INFORMATION | PROCESS_VM_READ | PROCESS_VM_WRITE | PROCESS_VM_OPERATION
    for i in range(needed.value // ctypes.sizeof(w.DWORD)):
        h = k32.OpenProcess(want, False, arr[i])
        if not h:
            continue
        buf = ctypes.create_unicode_buffer(260)
        if psapi.GetModuleBaseNameW(h, None, buf, 260) and buf.value.lower() == name.lower():
            return h
        k32.CloseHandle(h)
    return None


def _handle():
    global _HANDLE
    if _HANDLE is None:
        _HANDLE = _open_process()
    return _HANDLE


def _require_game():
    if not _handle():
        print("process not found (fft_enhanced.exe not running)")
        sys.exit(1)


# ---------------------------------------------------------------------------
# RPM / WPM helpers
# ---------------------------------------------------------------------------
def rpm(addr: int, n: int):
    h = _handle()
    if not h:
        return None
    buf = ctypes.create_string_buffer(n)
    got = ctypes.c_size_t()
    ok = k32.ReadProcessMemory(h, ctypes.c_void_p(addr), buf, n, ctypes.byref(got))
    return buf.raw if ok and got.value == n else None


def wpm(addr: int, data: bytes) -> bool:
    h = _handle()
    if not h:
        return False
    n = ctypes.c_size_t()
    ok = k32.WriteProcessMemory(h, ctypes.c_void_p(addr), data, len(data), ctypes.byref(n))
    return bool(ok) and n.value == len(data)


def ru8(addr: int):
    b = rpm(addr, 1)
    return b[0] if b is not None else None


def ru16(addr: int):
    b = rpm(addr, 2)
    return struct.unpack_from("<H", b)[0] if b is not None else None


def wu8(addr: int, val: int) -> bool:
    return wpm(addr, bytes([val & 0xFF]))


def wu16(addr: int, val: int) -> bool:
    return wpm(addr, struct.pack("<H", val & 0xFFFF))


def _to_le(val: int, width: int) -> bytes:
    """Little-endian pack of `val` in `width` bytes, masking to width (so -1 -> 0xFF..)."""
    return (val & ((1 << (8 * width)) - 1)).to_bytes(width, "little")


def _read_abs(addr: int, width: int):
    """Absolute read: `width` bytes at `addr` as a little-endian int, or None if unreadable."""
    blob = rpm(addr, width)
    return int.from_bytes(blob, "little") if blob is not None else None


# ---------------------------------------------------------------------------
# Band enumeration  (mirrors Band.IsValid + battle_cheats)
# ---------------------------------------------------------------------------
def _band_entry_addr(slot: int) -> int:
    return BAND_READ_BASE + slot * COMBAT_STRIDE


def _is_valid_entry(addr: int) -> bool:
    lvl = ru8(addr + A_LEVEL)
    if lvl is None or lvl < 1 or lvl > 99:
        return False
    br = ru8(addr + A_BRAVE)
    if br is None or br < 1 or br > 100:
        return False
    fa = ru8(addr + A_FAITH)
    if fa is None or fa < 1 or fa > 100:
        return False
    mhp = ru16(addr + A_MAXHP)
    if mhp is None or mhp < 1 or mhp >= 2000:
        return False
    gx = ru8(addr + A_GX)
    gy = ru8(addr + A_GY)
    if gx is None or gy is None:
        return False
    return gx <= 30 and gy <= 30


def _enumerate():
    """Return [(slot, side, nameId, hp, mhp, lvl, gx, gy, brave, faith)] for every live unit."""
    out = []
    for s in range(BAND_SLOTS):
        e = _band_entry_addr(s)
        if not _is_valid_entry(e):
            continue
        mhp = ru16(e + A_MAXHP)
        if not mhp or mhp <= 0:
            continue
        side = "player" if s >= PLAYER_SLOT_THRESHOLD else "enemy"
        out.append((s, side, ru16(e + A_NAMEID), ru16(e + A_HP), mhp,
                    ru8(e + A_LEVEL), ru8(e + A_GX), ru8(e + A_GY),
                    ru8(e + A_BRAVE), ru8(e + A_FAITH)))
    return out


def _print_units(units):
    print(f"{'slot':>4} {'side':<7} {'nameId':>6} {'hp':>5}/{'mhp':<5} {'lvl':>3} {'pos':<9}")
    for s, side, nameid, hp, mhp, lvl, gx, gy, br, fa in units:
        nid = "??" if nameid is None else str(nameid)
        print(f"{s:>4} {side:<7} {nid:>6} {hp!s:>5}/{mhp!s:<5} {lvl!s:>3} ({gx},{gy})")


# ---------------------------------------------------------------------------
# Verb: list
# ---------------------------------------------------------------------------
def cmd_list() -> None:
    _require_game()
    units = _enumerate()
    if not units:
        print("No live units found -- are you in a battle (fully loaded)?")
        return
    _print_units(units)


# ---------------------------------------------------------------------------
# Verb: focus  (plant + hold + watch a candidate AI-focus write)
# ---------------------------------------------------------------------------
def cmd_peek(slot, off, width) -> None:
    """Read <width> bytes at band+off for a slot (no write).  Use it to read a unit's
    live target field after it commits an attack -- if the AI stamped its chosen victim's
    uid into 0x17E, this reveals that uid (e.g. a player unit's target-namespace id)."""
    _require_game()
    units = {u[0]: u for u in _enumerate()}
    if slot not in units:
        print(f"slot {slot} is not a live unit -- run `list`.")
        return
    e = _band_entry_addr(slot)
    blob = rpm(e + off, width)
    if blob is None:
        print("read failed (bad offset?).")
        return
    val = int.from_bytes(blob, "little")
    print(f"slot {slot} band+0x{off:X} ({width}B) = {val} (0x{val:0{width*2}X})  raw={blob.hex().upper()}")


def cmd_dispfind(lo=0x140000000, hi=0x142000000) -> None:
    """Find the overhead-display-mode value (the in-battle '1' key toggle: empty/bar/bar+name).
    The display mode is a SETTING -- rock stable until you press '1'. Animated HUD buffers are the
    opposite (never stable). So we demand PERFECT stability across 3 idle reads in EACH state, then a
    change between states. That crushes the render-buffer noise. Between snapshots do NOTHING but the
    one '1' press when told -- no cursor, no unit moves (those churn UI state too)."""
    _require_game()
    CHUNK = 0x10000

    def snapshot(label):
        input(f"  >>> {label}\n      then press Enter here ...")
        snap, a = {}, lo
        while a < hi:
            b = rpm(a, CHUNK)
            if b is not None:
                snap[a] = b
            a += CHUNK
        return snap

    print(f"dispfind: scanning 0x{lo:X}..0x{hi:X}. 7 snapshots -- the FULL cycle back to empty.\n")
    e1 = snapshot("Set the display to EMPTY (press 1 until NO bars). Leave it.")
    e2 = snapshot("Do NOTHING (idle read, empty).")
    b1 = snapshot("Press 1 ONCE -> BARS appear. Then don't touch anything.")
    b2 = snapshot("Do NOTHING (idle read, bars).")
    n1 = snapshot("Press 1 ONCE -> BARS+NAMES appear. Then don't touch anything.")
    n2 = snapshot("Do NOTHING (idle read, bars+names).")
    f1 = snapshot("Press 1 ONCE -> back to EMPTY. (This is the counter-killer.)")

    # The animated HUD/render buffer lives in this band and produced ~99% false hits; drop it.
    NOISE_LO, NOISE_HI = 0x140C700000, 0x140CA00000

    commons = set(e1) & set(e2) & set(b1) & set(b2) & set(n1) & set(n2) & set(f1)
    byte_hits = []
    bit_hits = []
    # Flag-bit patterns across (empty, bars, names, back-to-empty), stable within each state.
    # show-bars flag = 0,1,1,0 (or inverted 1,0,0,1); show-names flag = 0,0,1,0 (or 1,1,0,1).
    BARS_PAT  = {(0, 1, 1, 0), (1, 0, 0, 1)}
    NAMES_PAT = {(0, 0, 1, 0), (1, 1, 0, 1)}
    for base in sorted(commons):
        E1, E2, B1, B2, N1, N2, F1 = (e1[base], e2[base], b1[base], b2[base],
                                      n1[base], n2[base], f1[base])
        n = min(len(E1), len(E2), len(B1), len(B2), len(N1), len(N2), len(F1))
        for i in range(n):
            addr = base + i
            if NOISE_LO <= addr < NOISE_HI:
                continue
            ev, bv, nv, fv = E1[i], B1[i], N1[i], F1[i]
            if ev == bv == nv == fv:             # byte never changed at all -> skip fast (most bytes)
                continue
            stable = (E2[i] == ev and B2[i] == bv and N2[i] == nv)
            if not stable:
                continue                          # churns within a state -> noise
            # byte-level: 3 distinct + returns to empty
            if ev != bv and bv != nv and ev != nv and fv == ev:
                byte_hits.append((addr, ev, bv, nv))
            # bit-level: any bit following a display-flag pattern (catches bit-packed modes)
            for bit in range(8):
                pat = ((ev >> bit) & 1, (bv >> bit) & 1, (nv >> bit) & 1, (fv >> bit) & 1)
                if pat in BARS_PAT:
                    bit_hits.append((addr, bit, "BARS", pat))
                elif pat in NAMES_PAT:
                    bit_hits.append((addr, bit, "names", pat))

    if not byte_hits and not bit_hits:
        print("\nNothing cycled empty->bars->names->EMPTY at byte OR bit level in this range. It's very "
              "likely OUT OF RANGE. Widen: dispfind 0x140000000 0x145000000  (or 0x13F000000 0x140000000).")
        return
    if bit_hits:
        bars = [h for h in bit_hits if h[2] == "BARS"]
        print(f"\n{len(bars)} SHOW-BARS flag-bit candidate(s) (bit = 0 empty / 1 bars / 1 names / 0 back) "
              f"-- these HIDE THE BAR, test first:")
        for addr, bit, kind, pat in bars[:40]:
            print(f"  0x{addr:X} bit{bit} (mask 0x{1<<bit:02X})  pattern e/b/n/back = {pat}")
        if len(bars) > 40:
            print(f"  ... {len(bars)-40} more BARS-flag bits.")
        others = len(bit_hits) - len(bars)
        if others:
            print(f"  (+ {others} show-names flag bits, less useful for the bar.)")
    if byte_hits:
        byte_hits.sort(key=lambda h: max(h[1],h[2],h[3]) - min(h[1],h[2],h[3]))
        print(f"\n{len(byte_hits)} full-byte cycler(s):")
        for addr, ev, bv, nv in byte_hits[:20]:
            print(f"  0x{addr:X}   empty={ev} bars={bv} names={nv}")
    print("\nFreeze a SHOW-BARS bit: force it to its EMPTY value (clear it if pattern starts 0), press 1 "
          "-- if the bars die and it STICKS, that's the flag we hold during the taunt.")


def cmd_teamflip(flip_slots, flip_mod=True, flip_base=False) -> None:
    """Team-swap taunt test: HOLD the team flag bit (|= 0x10, matching enemies) on the given slots so
    the enemy AI reads them as its own team and skips them.  Two copies of the flag exist -- the base
    (combat+0x05) and the AI's modified copy (band+0x1D2 = combat+0x1EE).  Choose which to flip to learn
    which system reads which:
        default          -> modified copy only (band+0x1D2)     [known: turns the health bar RED]
        --base           -> BOTH copies
        --base-only      -> base copy only (combat+0x05)         [UNTESTED: does IT redden the bar?]
    Holds every 30ms; Ctrl+C clears + restores.

    THE DECOUPLE QUESTION: if one copy steers the AI while the OTHER drives the red health-bar / loss
    check, we flip only the AI copy -> enemies redirect with no red bar and no premature game-over.
    Watch for each mode: (1) enemies skip them? (2) health bar red or normal? (3) our side unaffected?"""
    _require_game()
    units = {u[0]: u for u in _enumerate()}
    targets = [s for s in flip_slots if s in units]
    if not targets:
        print("no live units at those slots -- run `list`.")
        return

    saved = {}   # slot -> (entry_addr, orig_modified, orig_base, brave, faith)
    for s in targets:
        e = _band_entry_addr(s)
        modv = ru8(e + A_TEAM_OFF)
        basev = ru8(e - BAND_ENTRY + 0x05)   # combat+0x05 (before the band entry)
        if modv is None:
            continue
        saved[s] = (e, modv, basev, ru8(e + A_BRAVE), ru8(e + A_FAITH))
    if not saved:
        print("could not read the team byte on any target -- aborted.")
        return

    which = "modified band+0x1D2" if (flip_mod and not flip_base) else \
            "base combat+0x05" if (flip_base and not flip_mod) else "BOTH copies"
    print(f"teamflip [{which}]: setting team bit 0x{A_TEAM_MASK:02X} (-> enemy-team) on slots {sorted(saved)}")
    for s, (e, modv, basev, _b, _f) in saved.items():
        bstr = f"0x{basev:02X}" if basev is not None else "??"
        print(f"  slot {s}: band+0x1D2 was 0x{modv:02X}  base combat+0x05 was {bstr}")
    print("Holding.  Ctrl+C to clear.  Watch: enemies skip them? bar red or normal? our side ok?")

    def _set(e):
        if flip_mod:
            c = ru8(e + A_TEAM_OFF)
            if c is not None:
                wu8(e + A_TEAM_OFF, c | A_TEAM_MASK)
        if flip_base:
            b = ru8(e - BAND_ENTRY + 0x05)
            if b is not None:
                wu8(e - BAND_ENTRY + 0x05, b | A_TEAM_MASK)

    try:
        while True:
            for s, (e, _mv, _bv, br, fa) in list(saved.items()):
                if ru8(e + A_BRAVE) != br or ru8(e + A_FAITH) != fa:
                    del saved[s]
                    continue
                _set(e)
            if not saved:
                print("  all held units migrated -- stopping.")
                break
            time.sleep(0.03)
    except KeyboardInterrupt:
        print()

    for s, (e, modv, basev, _b, _f) in saved.items():
        if flip_mod:
            wu8(e + A_TEAM_OFF, modv)                   # restore exact original bytes
        if flip_base and basev is not None:
            wu8(e - BAND_ENTRY + 0x05, basev)
    print(f"Restored team byte on {len(saved)} unit(s).")


def cmd_teamscan(ally_slots, enemy_slots) -> None:
    """Find the TEAM byte: scan each unit's full combat slot (0x200 from combat base) for offsets where
    every ALLY shares one value that NO enemy has.  That byte is the AI's friend/foe discriminator (PSX
    Check_Allies/Enemies XOR'd ENTD-flag +0x05 / +0x1BA).  Finding it gives the DLL a guest-identifier
    and a possible NO-VISUAL taunt (make enemies read allies as their own team -> they skip them, no
    Invisible status).  Provide known allies (party + guests) and known enemies by slot; run `list` first.
    Prints combat-relative offset and band-relative (combat - 0x1C)."""
    _require_game()
    units = {u[0]: u for u in _enumerate()}
    allies = [s for s in ally_slots if s in units]
    enemies = [s for s in enemy_slots if s in units]
    if len(allies) < 2 or not enemies:
        print("need >=2 live ally slots and >=1 live enemy slot (run `list`).")
        return

    # Read full combat slots (combat base = band entry - BandEntry).
    def slot_blob(s):
        return rpm(_band_entry_addr(s) - BAND_ENTRY, COMBAT_STRIDE)
    ally_blobs = {s: slot_blob(s) for s in allies}
    enemy_blobs = {s: slot_blob(s) for s in enemies}
    if any(b is None for b in ally_blobs.values()) or any(b is None for b in enemy_blobs.values()):
        print("could not read a combat slot for some unit -- aborted.")
        return

    print(f"teamscan: allies={allies} vs enemies={enemies}")
    # DISJOINT test: an offset is a team-discriminator candidate if the set of ally values and the set
    # of enemy values never overlap (allies may span >1 team-color, as long as none collides with an
    # enemy).  Rank by "cleanliness" = fewest distinct values per group (a true team byte reads a
    # single value per team).  Byte-level first, then bit-level for a 1-bit team flag.
    byte_hits = []
    for off in range(COMBAT_STRIDE):
        avals = {ally_blobs[s][off] for s in allies}
        evals = {enemy_blobs[s][off] for s in enemies}
        if avals.isdisjoint(evals):
            byte_hits.append((len(avals) + len(evals), off, sorted(avals), sorted(evals)))
    byte_hits.sort()

    bit_hits = []
    for off in range(COMBAT_STRIDE):
        for bit in range(8):
            abits = {(ally_blobs[s][off] >> bit) & 1 for s in allies}
            ebits = {(enemy_blobs[s][off] >> bit) & 1 for s in enemies}
            if len(abits) == 1 and len(ebits) == 1 and abits != ebits:
                bit_hits.append((off, bit, next(iter(abits)), next(iter(ebits))))

    if not byte_hits and not bit_hits:
        print("no offset separates allies from enemies at all -- team id likely lives outside the combat "
              "slot (AI-Data struct / roster). Add more units, or we hunt the team field elsewhere.")
        return

    def _bstr(off):
        b = off - BAND_ENTRY
        return f"band+0x{b:X}" if b >= 0 else f"(combat-only)"

    if bit_hits:
        print(f"\n{len(bit_hits)} single-BIT team flag candidate(s) (all allies one bit-value, all enemies the other):")
        for off, bit, av, ev in bit_hits[:30]:
            print(f"  combat+0x{off:03X} bit{bit} (mask 0x{1<<bit:02X})  {_bstr(off):<16} allies={av} enemies={ev}")

    print(f"\n{len(byte_hits)} byte-level disjoint candidate(s), cleanest first:")
    for score, off, avals, evals in byte_hits[:30]:
        a = [f'0x{v:02X}' for v in avals]
        e = [f'0x{v:02X}' for v in evals]
        print(f"  combat+0x{off:03X} {_bstr(off):<16} allies={a} enemies={e}")
    if len(byte_hits) > 30:
        print(f"  ... {len(byte_hits)-30} more (add enemy slots to narrow).")
    print("\nThe real team byte: allies read 1-2 values (party color + guest color), enemies read a "
          "distinct value. Ignore HP/stat offsets that are disjoint by coincidence.")


def cmd_taunt(wielder_slot, hide_slots=None) -> None:
    """Path-B Guardian test: HOLD the Invisible bit (band+0x47 |= 0x10) on player-side units so the AI
    skips them and single-target enemies funnel onto the still-visible wielder.  Default hides EVERY
    player unit except the wielder (full dogpile).  Pass explicit slots to hide only those (selective
    guard).  Holds every ~150ms; Ctrl+C clears the bit and restores.

    NOTE (design): this is whole-battlefield -- ALL enemies target the visible wielder, it is not a
    per-enemy taunt.  The Invisible bit also makes the hidden units transparent + shows the status icon
    (welded to the same bit); a continuity-clean ship version would use the AI team-perception byte
    instead (no icon), which is not located in IC yet."""
    _require_game()
    units = {u[0]: u for u in _enumerate()}
    if wielder_slot not in units or units[wielder_slot][1] != "player":
        print(f"wielder slot {wielder_slot} is not a live player unit -- run `list`.")
        return

    # Decide who to hide.  Explicit slots may be ANY side (guests sit on enemy-side band slots but are
    # player-allied -- they draw fire and must be hidden too).  Default = every player-side unit but the
    # wielder; add guest slots explicitly, e.g. `taunt 24 8 9`.
    if hide_slots:
        targets = [s for s in hide_slots if s in units and s != wielder_slot]
    else:
        targets = [s for s in units if units[s][1] == "player" and s != wielder_slot]
    if not targets:
        print("no units to hide (need the wielder + at least one other unit; name guest slots explicitly).")
        return

    # Save originals + report.
    saved = {}   # slot -> (addr, original_0x47_byte, brave, faith)
    for s in targets:
        e = _band_entry_addr(s)
        cur = ru8(e + A_INVIS_OFF)
        if cur is None:
            continue
        saved[s] = (e, cur, ru8(e + A_BRAVE), ru8(e + A_FAITH))
    if not saved:
        print("could not read the status byte on any target -- aborted.")
        return

    w = units[wielder_slot]
    print(f"GUARDIAN: wielder = slot {wielder_slot} (nameId {w[2]}) stays visible; "
          f"hiding {len(saved)} ally(ies): {sorted(saved)}")
    print("Holding Invisible.  End your turn and watch the enemy phase converge on the wielder.  Ctrl+C to clear.")

    try:
        while True:
            for s, (e, _orig, br, fa) in list(saved.items()):
                if ru8(e + A_BRAVE) != br or ru8(e + A_FAITH) != fa:
                    del saved[s]                       # slot migrated -- stop holding it
                    continue
                cur = ru8(e + A_INVIS_OFF)
                if cur is not None:
                    wu8(e + A_INVIS_OFF, cur | A_INVIS_MASK)
            if not saved:
                print("  all held units migrated -- stopping.")
                break
            time.sleep(0.03)   # aggressive re-apply: acting clears Invisible, re-hide before the AI re-targets
    except KeyboardInterrupt:
        print()

    # Restore: clear the invisible bit we set (leave any other bits as they are now).
    for s, (e, _orig, _br, _fa) in saved.items():
        cur = ru8(e + A_INVIS_OFF)
        if cur is not None:
            wu8(e + A_INVIS_OFF, cur & ~A_INVIS_MASK & 0xFF)
    print(f"Cleared Invisible on {len(saved)} unit(s).")


def cmd_poke(slot, off, val, width) -> None:
    """Raw write primitive: band+off = val (width bytes), read-back, LEAVE it (no restore).
    Sanity check that our band addressing lands on REAL unit data -- poke a VISIBLE field
    (HP band+0x14 u16, Brave +0x0E, Faith +0x10) and watch the on-screen value change.
    HP band+0x14 is the cleanest: the floating HP bar redraws immediately.  This uses the
    exact write path `focus` uses, so a visible change proves the addressing is sound and
    the taunt fields' inertness is about MEANING, not a broken write."""
    _require_game()
    units = {u[0]: u for u in _enumerate()}
    if slot not in units:
        print(f"slot {slot} is not a live unit -- run `list`.")
        return
    e = _band_entry_addr(slot)
    old = rpm(e + off, width)
    if old is None:
        print("read failed (bad offset?).")
        return
    ok = wu16(e + off, val) if width == 2 else wu8(e + off, val)
    rb = rpm(e + off, width)
    oldv = int.from_bytes(old, "little")
    rbv = int.from_bytes(rb, "little") if rb is not None else None
    print(f"slot {slot} band+0x{off:X} ({width}B): {oldv} -> wrote {val}, read-back {rbv}  "
          f"(write_ok={ok})")
    print("Left written (no restore).  Watch the game for the change; a battle restart resets it.")


# ---------------------------------------------------------------------------
# Absolute-address primitives (the "hide the overhead display" hunt)
# band-relative peek/poke can't reach a raw global; these take the ABSOLUTE
# address dispfind / the FFTHandsFree heap-diff / Cheat Engine surfaced.  See the
# module docstring for the awatch->afreeze test flow.
# ---------------------------------------------------------------------------
def cmd_apeek(addr, width) -> None:
    _require_game()
    val = _read_abs(addr, width)
    if val is None:
        print(f"read failed at 0x{addr:X} ({width}B) -- unmapped/unreadable page?")
        return
    print(f"0x{addr:X} ({width}B) = {val} (0x{val:0{width * 2}X})")


def cmd_apoke(addr, val, width) -> None:
    _require_game()
    old = _read_abs(addr, width)
    if old is None:
        print(f"read failed at 0x{addr:X} -- aborting (won't blind-write an unreadable page).")
        return
    ok = wpm(addr, _to_le(val, width))
    rb = _read_abs(addr, width)
    print(f"0x{addr:X} ({width}B): {old} -> wrote {val}, read-back {rb} (write_ok={ok})")
    print("Left written (no restore); a battle restart resets it.")


def cmd_awatch(addr, width, seconds=30.0) -> None:
    """Poll an absolute address and print it whenever it changes.  Press the in-battle
    '1' key while this runs: if the value steps through the empty/bars/bars+names cycle,
    it's a live display-mode candidate worth afreeze-testing."""
    _require_game()
    print(f"Watching 0x{addr:X} ({width}B) for {seconds:.0f}s -- press '1' in battle and watch it step. "
          f"Ctrl+C to stop.")
    last = object()
    start = time.time()
    try:
        while time.time() - start < seconds:
            v = _read_abs(addr, width)
            if v != last:
                shown = f"{v} (0x{v:0{width * 2}X})" if v is not None else "<unreadable>"
                print(f"  +{time.time() - start:5.1f}s  0x{addr:X} = {shown}")
                last = v
            time.sleep(0.03)
    except KeyboardInterrupt:
        print()


def cmd_afreeze(addr, val, width, seconds=30.0) -> None:
    """Hold an absolute address at `val`, re-writing every ~8ms so the engine's per-frame
    rewrite can't win.  Alt-Tab to the game and press '1' to try to force the bars on; if
    they refuse and it sticks, this is the display-mode lever.  Restores on exit."""
    _require_game()
    orig = _read_abs(addr, width)
    if orig is None:
        print(f"read failed at 0x{addr:X} -- aborting (won't hold an unreadable page).")
        return
    print(f"Freezing 0x{addr:X} ({width}B) at {val} (was {orig}) for {seconds:.0f}s, re-writing every ~8ms.")
    print("Alt-Tab to the game and press '1' to force bars ON.  If they refuse and it STICKS, that's the "
          "lever.  Ctrl+C to release + restore early.")
    data = _to_le(val, width)
    start = time.time()
    holds = 0
    try:
        while time.time() - start < seconds:
            wpm(addr, data)
            holds += 1
            time.sleep(0.008)
    except KeyboardInterrupt:
        print()
    wpm(addr, _to_le(orig, width))
    print(f"Held {holds} writes; restored 0x{addr:X} to {orig}.")


def cmd_focus(attacker_slot, target_slot, flag_off, flag_mask, uid_off, uid_w,
              val_override, hold=True, setbytes=None) -> None:
    _require_game()
    units = _enumerate()
    if not units:
        print("No live units found -- are you in a battle?")
        return
    by_slot = {u[0]: u for u in units}

    # Interactive selection (mirrors kill_all's confirm) when slots not given as args.
    if attacker_slot is None or target_slot is None:
        _print_units(units)
        try:
            attacker_slot = int(input("Attacker slot (whose focus to change): ").strip())
            ans = input("Target -- type `x <slot>` (who they should attack): ").strip().lower()
        except (EOFError, KeyboardInterrupt, ValueError):
            print("\naborted.")
            return
        if not ans.startswith("x"):
            print("Expected `x <slot>` -- aborted.")
            return
        try:
            target_slot = int(ans[1:].strip())
        except ValueError:
            print("Could not parse target slot -- aborted.")
            return

    if attacker_slot not in by_slot:
        print(f"Attacker slot {attacker_slot} is not a live unit -- run `list`.")
        return
    if target_slot not in by_slot:
        print(f"Target slot {target_slot} is not a live unit -- run `list`.")
        return
    if attacker_slot == target_slot:
        print("Attacker and target are the same unit -- pick two.")
        return

    atk = by_slot[attacker_slot]
    tgt = by_slot[target_slot]
    e = _band_entry_addr(attacker_slot)
    atk_br, atk_fa = atk[8], atk[9]

    # Identity value to plant: the target's nameId unless overridden.
    val = val_override if val_override is not None else tgt[2]
    if val is None:
        print("Could not read target identity (nameId) -- aborted.")
        return

    write_flag = flag_mask != 0   # mask 0 => uid-only redirect (skip the flag byte)
    setbytes = setbytes or []     # [(off, val)] absolute byte ASSIGNS (can CLEAR bits, unlike --flag)

    print(f"\nATTACKER slot {attacker_slot} (nameId {atk[2]}, {atk[1]}) "
          f"-> TARGET slot {target_slot} (nameId {tgt[2]}, {tgt[1]})")
    if write_flag:
        print(f"  flag: band+0x{flag_off:02X} |= 0x{flag_mask:02X}")
    else:
        print("  flag: (skipped -- uid-only; use --flag OFF:MASK to also plant a FocusUnit flag)")
    print(f"  uid : band+0x{uid_off:02X} = {val} ({uid_w}-byte)")
    for off, bval in setbytes:
        print(f"  set : band+0x{off:02X} = 0x{bval:02X} (absolute assign)")

    # Save originals.
    saved_flag = ru8(e + flag_off) if write_flag else None
    saved_uid  = rpm(e + uid_off, uid_w)
    saved_set  = [(off, ru8(e + off)) for off, _ in setbytes]
    if saved_uid is None or (write_flag and saved_flag is None) or any(v is None for _, v in saved_set):
        print("Could not read the candidate bytes on the attacker -- aborted (bad offset?).")
        return

    def _write():
        if write_flag:
            wu8(e + flag_off, (ru8(e + flag_off) or 0) | flag_mask)
        if uid_w == 1:
            wu8(e + uid_off, val)
        else:
            wu16(e + uid_off, val)
        for off, bval in setbytes:
            wu8(e + off, bval)

    _write()
    rb_uid = ru16(e + uid_off) if uid_w == 2 else ru8(e + uid_off)
    was_uid = struct.unpack('<H', saved_uid)[0] if uid_w == 2 else saved_uid[0]
    print(f"  SET uid={rb_uid} (was {was_uid})" +
          (f"  flag=0x{ru8(e + flag_off):02X}" if write_flag else ""))
    if not hold:
        print("Planted once (no hold, no restore) -- let the unit act, then `peek` the field.")
        return
    print("Holding + watching.  Reports if the ENGINE overwrites our value (re-latch race)")
    print("or the field diverges.  Watch the attacker's AI turn.  Ctrl+C to stop + restore.")

    # Telemetry: sample the field BEFORE each re-write.  If the engine changed it out from
    # under us since our last write, that's the re-latch signal (the field is engine-owned
    # and read at turn-start, not a value we can simply hold).  Heartbeat ~1s.
    reasserts = 0
    engine_overwrites = 0
    last_beat = None
    try:
        while True:
            if ru8(e + A_BRAVE) != atk_br or ru8(e + A_FAITH) != atk_fa:
                print("  attacker slot migrated (brave/faith changed) -- stopped holding.")
                break
            live = ru16(e + uid_off) if uid_w == 2 else ru8(e + uid_off)
            if live is not None and live != val:
                engine_overwrites += 1
                print(f"  ENGINE OVERWROTE band+0x{uid_off:02X}: read {live}, "
                      f"re-asserting {val}  (re-latch #{engine_overwrites})")
            _write()
            reasserts += 1
            if reasserts % 7 == 0:                       # ~1s heartbeat (7 * 0.15s)
                lf = ru8(e + flag_off) if write_flag else None
                beat = (live, lf)
                if beat != last_beat:
                    fl = f" flag=0x{lf:02X}" if lf is not None else ""
                    print(f"  [hold] live uid={live}{fl}  (overwrites so far: {engine_overwrites})")
                    last_beat = beat
            time.sleep(0.15)
    except KeyboardInterrupt:
        print()
    print(f"  held {reasserts} re-asserts, {engine_overwrites} engine overwrites observed.")

    # Restore.
    if write_flag:
        wu8(e + flag_off, saved_flag)
    wpm(e + uid_off, saved_uid)
    for off, oval in saved_set:
        wu8(e + off, oval)
    print("Restored original bytes.")


# ---------------------------------------------------------------------------
# Verb: scan  (locate-the-field: find where a target's identity appears in others)
# ---------------------------------------------------------------------------
def cmd_scan(target_slot) -> None:
    _require_game()
    units = _enumerate()
    if not units:
        print("No live units found -- are you in a battle?")
        return
    by_slot = {u[0]: u for u in units}
    if target_slot is None:
        _print_units(units)
        try:
            target_slot = int(input("Target slot to hunt for: ").strip())
        except (EOFError, KeyboardInterrupt, ValueError):
            print("\naborted.")
            return
    if target_slot not in by_slot:
        print(f"Slot {target_slot} is not a live unit.")
        return
    tgt_name = by_slot[target_slot][2]
    if tgt_name is None:
        print("Could not read target nameId.")
        return
    print(f"Hunting for target slot {target_slot} nameId={tgt_name} (0x{tgt_name:04X}) "
          f"inside every OTHER unit's 0x200 struct ...")
    lo, hi = tgt_name & 0xFF, (tgt_name >> 8) & 0xFF
    for s, side, nameid, hp, mhp, lvl, gx, gy, br, fa in units:
        if s == target_slot:
            continue
        blob = rpm(_band_entry_addr(s), COMBAT_STRIDE)
        if blob is None:
            continue
        hits8, hits16 = [], []
        for off in range(COMBAT_STRIDE):
            if blob[off] == lo and off != A_NAMEID:      # u8 low byte
                hits8.append(off)
            if off + 1 < COMBAT_STRIDE and blob[off] == lo and blob[off + 1] == hi and off != A_NAMEID:
                hits16.append(off)
        if hits16:
            print(f"  slot {s:>2} ({side}): u16 hits at " + ", ".join(f"0x{o:02X}" for o in hits16))
        elif hits8:
            print(f"  slot {s:>2} ({side}): u8-lowbyte hits at " +
                  ", ".join(f"0x{o:02X}" for o in hits8[:12]) + (" ..." if len(hits8) > 12 else ""))
    print("An offset that shows up ONLY on a unit with a baked FocusUnit target "
          "(hunter/escort battle) is the live Target-uid field.")


# ---------------------------------------------------------------------------
# Arg parsing for the candidate overrides
# ---------------------------------------------------------------------------
def _parse_flag(s: str):
    """OFF:MASK -> (off, mask)."""
    off_s, mask_s = s.split(":")
    return int(off_s, 0), int(mask_s, 0)


def _parse_uid(s: str):
    """OFF[:w] -> (off, width)."""
    if ":" in s:
        off_s, w_s = s.split(":")
        return int(off_s, 0), int(w_s, 0)
    return int(s, 0), DEFAULT_UID_WIDTH


def _extract_opts(args):
    """Pull --flag/--uid/--val out of args; return (positional, flag, uid, val)."""
    flag = (DEFAULT_FLAG_OFF, DEFAULT_FLAG_MASK)
    uid  = (DEFAULT_UID_OFF, DEFAULT_UID_WIDTH)
    val  = None
    pos = []
    i = 0
    while i < len(args):
        a = args[i]
        if a == "--flag" and i + 1 < len(args):
            flag = _parse_flag(args[i + 1]); i += 2
        elif a == "--uid" and i + 1 < len(args):
            uid = _parse_uid(args[i + 1]); i += 2
        elif a == "--val" and i + 1 < len(args):
            val = int(args[i + 1], 0); i += 2
        else:
            pos.append(a); i += 1
    return pos, flag, uid, val


# ---------------------------------------------------------------------------
# Self-test
# ---------------------------------------------------------------------------
def _selftest() -> bool:
    ok = True

    slot0, slot24, slot48 = _band_entry_addr(0), _band_entry_addr(24), _band_entry_addr(48)
    exp0  = COMBAT_ANCHOR + BAND_ENTRY - 24 * COMBAT_STRIDE
    exp24 = COMBAT_ANCHOR + BAND_ENTRY
    exp48 = COMBAT_ANCHOR + BAND_ENTRY + 24 * COMBAT_STRIDE
    if (slot0, slot24, slot48) == (exp0, exp24, exp48):
        print(f"  band arithmetic: slot0=0x{slot0:X} slot24=0x{slot24:X}  OK")
    else:
        print("  band arithmetic: FAIL"); ok = False

    if _parse_flag("0x23:0x40") == (0x23, 0x40):
        print("  --flag parse: OK")
    else:
        print("  --flag parse: FAIL"); ok = False

    if _parse_uid("0x24") == (0x24, 2) and _parse_uid("0x24:1") == (0x24, 1):
        print("  --uid parse: OK")
    else:
        print("  --uid parse: FAIL"); ok = False

    pos, flag, uid, val = _extract_opts(["3", "x8-ignored", "--flag", "0x50:0x40", "--uid", "0x26:1", "--val", "5"])
    if flag == (0x50, 0x40) and uid == (0x26, 1) and val == 5 and pos == ["3", "x8-ignored"]:
        print("  opt extraction: OK")
    else:
        print(f"  opt extraction: FAIL pos={pos} flag={flag} uid={uid} val={val}"); ok = False

    if A_NAMEID == 0x1E0:
        print("  A_NAMEID == 0x1E0  OK")
    else:
        print("  A_NAMEID: FAIL"); ok = False

    if _to_le(0x1234, 2) == b"\x34\x12" and _to_le(1, 1) == b"\x01" and _to_le(-1, 1) == b"\xff" \
            and _to_le(0xDEADBEEF, 4) == b"\xef\xbe\xad\xde":
        print("  _to_le LE pack: OK")
    else:
        print("  _to_le: FAIL"); ok = False

    if int("0x140782970", 0) == 0x140782970 and int("0x140D4A264", 0) == 0x140D4A264:
        print("  absolute-address parse: OK")
    else:
        print("  absolute-address parse: FAIL"); ok = False

    return ok


# ---------------------------------------------------------------------------
# Entry point
# ---------------------------------------------------------------------------
def main() -> None:
    args = sys.argv[1:]
    if not args or args[0] in ("-h", "--help", "help"):
        print(__doc__)
        return

    if args[0] == "--selftest":
        print("Running self-test (no game required) ...")
        if _selftest():
            print("\nAll self-tests PASSED.")
            sys.exit(0)
        print("\nSelf-test FAILED.")
        sys.exit(1)

    if args[0] == "list":
        cmd_list()
        return

    if args[0] == "focus":
        rest = args[1:]
        hold = "--once" not in rest
        rest = [a for a in rest if a != "--once"]
        # --setbyte OFF:VAL (repeatable) -> absolute byte assign (can clear bits)
        setbytes = []
        pruned, i = [], 0
        while i < len(rest):
            if rest[i] == "--setbyte" and i + 1 < len(rest):
                off_s, val_s = rest[i + 1].split(":")
                setbytes.append((int(off_s, 0), int(val_s, 0)))
                i += 2
            else:
                pruned.append(rest[i]); i += 1
        pos, flag, uid, val = _extract_opts(pruned)
        atk = int(pos[0]) if len(pos) > 0 else None
        tgt = int(pos[1]) if len(pos) > 1 else None
        cmd_focus(atk, tgt, flag[0], flag[1], uid[0], uid[1], val, hold=hold, setbytes=setbytes)
        return

    if args[0] == "peek":
        if len(args) < 3:
            print("usage: peek <slot> <off> [width]")
            sys.exit(2)
        slot = int(args[1], 0)
        off = int(args[2], 0)
        width = int(args[3], 0) if len(args) > 3 else 1
        cmd_peek(slot, off, width)
        return

    if args[0] == "taunt":
        if len(args) < 2:
            print("usage: taunt <wielder_slot> [hide_slot ...]   (default: hide all other player units)")
            sys.exit(2)
        wielder = int(args[1], 0)
        hide = [int(a, 0) for a in args[2:]] if len(args) > 2 else None
        cmd_taunt(wielder, hide)
        return

    if args[0] == "dispfind":
        lo = int(args[1], 0) if len(args) > 1 else 0x140000000
        hi = int(args[2], 0) if len(args) > 2 else 0x142000000
        cmd_dispfind(lo, hi)
        return

    if args[0] == "teamflip":
        rest = args[1:]
        base_only = "--base-only" in rest
        also_base = "--base" in rest
        rest = [a for a in rest if a not in ("--base", "--base-only")]
        if not rest:
            print("usage: teamflip <slot> [slot ...] [--base | --base-only]")
            print("  (default = modified copy band+0x1D2; --base = both; --base-only = base combat+0x05)")
            sys.exit(2)
        flip_mod = not base_only
        flip_base = also_base or base_only
        cmd_teamflip([int(a, 0) for a in rest], flip_mod=flip_mod, flip_base=flip_base)
        return

    if args[0] == "teamscan":
        # teamscan <ally>,<ally>,... <enemy>,<enemy>,...   (two comma-lists) OR
        # teamscan --allies a b c --enemies x y z
        rest = args[1:]
        allies, enemies = [], []
        if "--allies" in rest and "--enemies" in rest:
            ai, ei = rest.index("--allies"), rest.index("--enemies")
            a_seg = rest[ai + 1:ei] if ai < ei else rest[ai + 1:]
            e_seg = rest[ei + 1:ai] if ei < ai else rest[ei + 1:]
            allies = [int(x, 0) for x in a_seg]
            enemies = [int(x, 0) for x in e_seg]
        elif len(rest) == 2 and "," in rest[0]:
            allies = [int(x, 0) for x in rest[0].split(",") if x]
            enemies = [int(x, 0) for x in rest[1].split(",") if x]
        else:
            print("usage: teamscan --allies <slot...> --enemies <slot...>")
            print("   or: teamscan 24,25,8,9 10,11,12   (allies comma-list, enemies comma-list)")
            sys.exit(2)
        cmd_teamscan(allies, enemies)
        return

    if args[0] == "poke":
        if len(args) < 4:
            print("usage: poke <slot> <off> <val> [width]   (e.g. poke 12 0x14 30 2  -> set HP)")
            sys.exit(2)
        slot = int(args[1], 0)
        off = int(args[2], 0)
        val = int(args[3], 0)
        width = int(args[4], 0) if len(args) > 4 else 1
        cmd_poke(slot, off, val, width)
        return

    if args[0] == "apeek":
        if len(args) < 2:
            print("usage: apeek <abs_hex> [width=1|2|4|8]")
            sys.exit(2)
        cmd_apeek(int(args[1], 0), int(args[2], 0) if len(args) > 2 else 1)
        return

    if args[0] == "awatch":
        if len(args) < 2:
            print("usage: awatch <abs_hex> [width=1|2|4|8] [seconds=30]")
            sys.exit(2)
        w = int(args[2], 0) if len(args) > 2 else 1
        secs = float(args[3]) if len(args) > 3 else 30.0
        cmd_awatch(int(args[1], 0), w, secs)
        return

    if args[0] == "apoke":
        if len(args) < 3:
            print("usage: apoke <abs_hex> <value> [width=1|2|4|8]")
            sys.exit(2)
        cmd_apoke(int(args[1], 0), int(args[2], 0), int(args[3], 0) if len(args) > 3 else 1)
        return

    if args[0] == "afreeze":
        if len(args) < 3:
            print("usage: afreeze <abs_hex> <value> [width=1|2|4|8] [seconds=30]")
            sys.exit(2)
        w = int(args[3], 0) if len(args) > 3 else 1
        secs = float(args[4]) if len(args) > 4 else 30.0
        cmd_afreeze(int(args[1], 0), int(args[2], 0), w, secs)
        return

    if args[0] == "scan":
        tgt = int(args[1]) if len(args) > 1 else None
        cmd_scan(tgt)
        return

    print(f"Unknown verb: {args[0]!r}")
    print(__doc__)
    sys.exit(2)


if __name__ == "__main__":
    main()
