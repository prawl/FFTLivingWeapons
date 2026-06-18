"""Battle cheats probe for live in-game testing.

Verbs
-----
  python tools\\probes\\battle_cheats.py give_move [abilityId]
      Grant a movement passive to the unit hovered by the cursor.  Default ability
      id is 243 (Master Teleportation, same as Rapture's proven RaptureMoveId).

      Flow:
        1. Hover a unit in-game (the condensed struct at 0x14077D2A0 mirrors whoever
           the cursor is on -- the "follows hover" trap documented in the repo, but
           PERFECT for selection UX).
        2. Press Enter at this prompt.
        3. The probe reads HP/MaxHP/Level from the condensed struct, fingerprint-
           matches into the authoritative band (0x14184xxxx family, same walk as
           the DLL's Wielder.Locate), and writes the movement byte at band+0x80 (+3
           bytes, base id 230, MSB-first -- same encoding as Rapture.WriteField).
        4. Reports SET (read-back matched) or MISS, then HOLDS every ~200ms until
           Ctrl+C.  On exit, restores the original bytes.

  python tools\\probes\\battle_cheats.py buff_enemies [name]
      Plant a stealable buff on EVERY live enemy so Arcanum's Larceny (+3) has a
      target on the next damaging hit.  No hover/targeting -- it sweeps the whole band.
      With NO name it deals the six buffs round-robin (reraise, haste, protect, shell,
      reflect, regen, repeat) so the steal log varies and exercises the precedence.
      Pass a name (reraise / haste / protect / shell / reflect / regen) to force that
      one buff on everybody.  Offsets are the six LarcenyPolicy.Stealable rows, band-
      relative, straight from Offsets.cs.  (Alias: buff_enemy.)

      Flow:
        1. Equip a +3 Arcanum (sword id 30) on a player; enter a battle (a DEV build
           seeds every weapon to +3, so just equipping it is enough).
        2. Run this verb -> a buff bit is SET on every enemy's band entry.
        3. Attack any buffed enemy with the Arcanum wielder.
        4. The probe READ-watches the bits (it does NOT re-assert, so it never fights
           the DLL) and prints STRIPPED + which buff for each enemy as Larceny clears
           it.  Watch livingweapon.log for 'larceny: STOLE <buff>' + the icon on the wielder.
        NOTE: any enemy dealt reraise auto-revives once -- pass e.g. 'protect' to avoid that.

  python tools\\probes\\battle_cheats.py kill_all
      KO every enemy in the current battle.  Enumerates the authoritative band
      (BandReadBase, 49 slots, stride 0x200), classifies each valid entry as
      player-side (slot index >= 24, corresponding to n >= 0 around the anchor)
      or enemy-side (slot index < 24, n < 0), and on enemy entries with HP > 0
      writes HP=0 + dead-bit (band +0x45 | 0x20).

      Filtering mirrors KillEnemiesPlanner.Plan (FFTHandsFree CheatHandlers.cs):
        - Skip IsPlayer
        - Skip MaxHp <= 0 (empty slot)
        - Skip Hp <= 0 (already dead)
        - Dead-bit mask: 0x20 at band+0x45 (ADeadStatus from Offsets.cs)

  python tools\\probes\\battle_cheats.py --selftest
      Offline self-test.  No game required.  Validates movement-bit encoding
      math, dead-bit constant, and band-address arithmetic.
"""
import ctypes
import ctypes.wintypes as w
import struct
import sys
import time

# ---------------------------------------------------------------------------
# Process / memory constants  (all from LivingWeapon/Offsets.cs)
# ---------------------------------------------------------------------------
PROCESS_VM_READ       = 0x0010
PROCESS_VM_WRITE      = 0x0020
PROCESS_VM_OPERATION  = 0x0008
PROCESS_QUERY_INFORMATION = 0x0400

# Condensed struct: unit under the CURSOR (TurnQueue in Offsets.cs -- named for its battle
# role but documented as "follows hover" in the trap memory).
# 1.5 re-anchor 2026-06-17: +0x6000 (was 0x14077D2A0). Matches Offsets.TurnQueue.
CONDENSED_BASE = 0x1407832A0
TQ_LEVEL  = 0x00   # u16
TQ_HP     = 0x0C   # u16
TQ_MAXHP  = 0x10   # u16

# Band (authoritative live structs; static array freezes on restart)
# BandReadBase = CombatAnchor + BandEntry - 24*CombatStride
# 1.5 re-anchor 2026-06-17: CombatAnchor moved +0x6450 (was 0x14184F890). Matches
# Offsets.CombatAnchor -- the band-RELATIVE A* offsets below are unchanged on 1.5.
COMBAT_ANCHOR  = 0x141855CE0
BAND_ENTRY     = 0x1C          # unit copy sits 0x1C into each combat-band slot
COMBAT_STRIDE  = 0x200
BAND_SLOTS     = 49            # n = -24 .. +24 around the anchor
BAND_READ_BASE = COMBAT_ANCHOR + BAND_ENTRY - 24 * COMBAT_STRIDE
# Slot s=24 => n=0 => the anchor slot.  s < 24 => enemy-side (n < 0).
PLAYER_SLOT_THRESHOLD = 24     # slots >= this are player-side (n >= 0)

# Band entry field offsets (matching Offsets.cs A* constants)
A_LEVEL    = 0x0D   # u8
A_BRAVE    = 0x0E   # u8
A_FAITH    = 0x10   # u8
A_HP       = 0x14   # u16
A_MAXHP    = 0x16   # u16
A_GX       = 0x33   # u8
A_GY       = 0x34   # u8
A_DEAD_STATUS = 0x45  # u8 -- bit 0x20 = Dead, bit 0x10 = Undead
A_DEAD_BIT    = 0x20

# Allegiance / team byte at combat+0x54.  Band entry = combat+0x1C, so band-relative
# the byte sits at 0x54 - 0x1C = 0x38.  Live-proven 2026-06-11 (Olivia escort battle):
# every hostile unit shares one value (0x1B that battle) while the guest read 0x06.
# kill_all spares any enemy-side unit whose allegiance differs from the hostile majority.
A_ALLEG = 0x38   # u8, band-relative (== combat+0x54)

# Physical Attack stat at combat+0x3E (Offsets.CPa); band entry = combat+0x1C, so
# band-relative the byte sits at 0x3E - 0x1C = 0x22.  Drives physical (formula 2/etc.)
# damage.  The GrowthEngine leaves "unexpected" values alone, so a direct write sticks
# within a battle (a battle restart resets it -- re-run the verb).
A_PA = 0x22   # u8, band-relative (== combat+0x3E)

# Speed (CT charge rate) at combat+0x40 (Offsets.CSpeed) -> band-relative 0x40-0x1C = 0x24.
# CT / "slam" byte at combat+0x41 (Offsets.CtOff, what ExtraTurn writes) -> band 0x41-0x1C = 0x25.
A_SPEED = 0x24   # u8, band-relative (== combat+0x40)
A_CT    = 0x25   # u8, band-relative (== combat+0x41, the ACtSlam write byte)

# Stealable-buff bits (band-relative) -- mirrors LarcenyPolicy.Stealable + Offsets.cs exactly.
# Order = Larceny's steal precedence (highest-value first); the first one the foe actually has
# is the one Arcanum lifts.  buff_enemy plants whichever the caller names (default reraise).
STEALABLE_BUFFS = {
    "reraise": (0x47, 0x20),
    "haste":   (0x48, 0x08),
    "protect": (0x48, 0x20),
    "shell":   (0x48, 0x10),
    "reflect": (0x49, 0x02),
    "regen":   (0x48, 0x40),
}

# Band-relative movement field (Offsets.AMovement = CMovement - BandEntry = 0x9C - 0x1C = 0x80)
A_MOVEMENT = 0x80   # 3 bytes, base ability id 230, MSB-first
MOVEMENT_BASE    = 230
MOVEMENT_BYTES   = 3

# Level drift: live level may exceed roster level by up to this (Band.MaxLevelDrift)
MAX_LEVEL_DRIFT = 9

# ---------------------------------------------------------------------------
# Process handle (lazy, fail-gracefully)
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
    h = _handle()
    if not h:
        print("process not found (fft_enhanced.exe not running)")
        sys.exit(1)
    return h


# ---------------------------------------------------------------------------
# RPM / WPM helpers  (same pattern as treasure_flags.py)
# ---------------------------------------------------------------------------
def rpm(addr: int, n: int) -> bytes | None:
    h = _handle()
    if not h:
        return None
    buf = ctypes.create_string_buffer(n)
    got = ctypes.c_size_t()
    ok = k32.ReadProcessMemory(h, ctypes.c_void_p(addr), buf, n, ctypes.byref(got))
    if not ok or got.value != n:
        return None
    return buf.raw


def wpm(addr: int, data: bytes) -> bool:
    h = _handle()
    if not h:
        return False
    n = ctypes.c_size_t()
    ok = k32.WriteProcessMemory(h, ctypes.c_void_p(addr), data, len(data), ctypes.byref(n))
    return bool(ok) and n.value == len(data)


def ru8(addr: int) -> int | None:
    b = rpm(addr, 1)
    return b[0] if b is not None else None


def ru16(addr: int) -> int | None:
    b = rpm(addr, 2)
    return struct.unpack_from("<H", b)[0] if b is not None else None


def wu8(addr: int, val: int) -> bool:
    return wpm(addr, bytes([val & 0xFF]))


def wu16(addr: int, val: int) -> bool:
    return wpm(addr, struct.pack("<H", val & 0xFFFF))


# ---------------------------------------------------------------------------
# Band entry validation  (mirrors Band.IsValid in Band.cs)
# ---------------------------------------------------------------------------
def _band_entry_addr(slot: int) -> int:
    """Address of band entry for slot s (0..BAND_SLOTS-1)."""
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


def _level_matches(roster_lvl: int, live_lvl: int) -> bool:
    """Band.LevelMatchesRoster: live may exceed roster by up to MAX_LEVEL_DRIFT."""
    return live_lvl >= roster_lvl and (live_lvl - roster_lvl) <= MAX_LEVEL_DRIFT


# ---------------------------------------------------------------------------
# Movement encoding  (mirrors Signatures.ResolveMovement)
# ---------------------------------------------------------------------------
def movement_bit(ability_id: int):
    """Return (byte_offset, mask) for a movement ability id, or None if out of range."""
    pos = ability_id - MOVEMENT_BASE
    if pos < 0 or pos >= MOVEMENT_BYTES * 8:
        return None
    return pos // 8, 0x80 >> (pos % 8)


def read_movement_field(entry_addr: int) -> bytes | None:
    """Read the 3-byte movement field at band entry + A_MOVEMENT."""
    return rpm(entry_addr + A_MOVEMENT, MOVEMENT_BYTES)


def write_movement_field(entry_addr: int, field: bytes) -> bool:
    """Write the 3-byte movement field.  Guarded: read-verify the address is live first."""
    if rpm(entry_addr + A_MOVEMENT, MOVEMENT_BYTES) is None:
        return False
    return wpm(entry_addr + A_MOVEMENT, field)


def build_grant_field(ability_id: int) -> bytes | None:
    """Build a 3-byte field image with only the granted ability's bit set."""
    enc = movement_bit(ability_id)
    if enc is None:
        return None
    off, mask = enc
    field = bytearray(MOVEMENT_BYTES)
    field[off] = mask
    return bytes(field)


# ---------------------------------------------------------------------------
# Verb: give_move
# ---------------------------------------------------------------------------
def cmd_give_move(ability_id: int) -> None:
    _require_game()

    enc = movement_bit(ability_id)
    if enc is None:
        print(f"ability id {ability_id} is outside the movement field "
              f"(valid range: {MOVEMENT_BASE}..{MOVEMENT_BASE + MOVEMENT_BYTES * 8 - 1})")
        sys.exit(1)

    grant_field = build_grant_field(ability_id)
    assert grant_field is not None

    print(f"give_move: ability {ability_id} -> byte offset {enc[0]}, mask 0x{enc[1]:02X}")
    print("Hover the target unit in-game, then press Enter ...")
    try:
        input()
    except (EOFError, KeyboardInterrupt):
        print("\naborted.")
        return

    # Step 1: read the condensed struct (cursor-hover mirror)
    c_level = ru16(CONDENSED_BASE + TQ_LEVEL)
    c_hp    = ru16(CONDENSED_BASE + TQ_HP)
    c_maxhp = ru16(CONDENSED_BASE + TQ_MAXHP)

    if c_maxhp is None or c_maxhp == 0 or c_maxhp >= 2000:
        print(f"condensed struct looks invalid (maxhp={c_maxhp}) -- are you in a battle?")
        return
    if c_level is None or c_level < 1 or c_level > 99:
        print(f"condensed level looks invalid (level={c_level})")
        return

    print(f"condensed: level={c_level} hp={c_hp} maxhp={c_maxhp}")

    # Step 2: fingerprint-match into the authoritative band
    # Match: valid entry, hp+maxhp match the condensed readings, level drift ok.
    # Twin filter: prefer real-position (gx/gy != 0,0) over frozen (0,0) copy.
    match_addr = 0
    match_real = False

    for s in range(BAND_SLOTS):
        e = _band_entry_addr(s)
        if not _is_valid_entry(e):
            continue
        e_mhp = ru16(e + A_MAXHP)
        e_hp  = ru16(e + A_HP)
        e_lvl = ru8(e + A_LEVEL)
        if e_mhp != c_maxhp or e_hp != c_hp:
            continue
        if not _level_matches(c_level, e_lvl):
            continue
        gx = ru8(e + A_GX)
        gy = ru8(e + A_GY)
        real_pos = (gx != 0 or gy != 0)
        if match_real and not real_pos:
            continue   # existing real match beats a (0,0) twin
        if real_pos and not match_real and match_addr != 0:
            # new real beats old (0,0): discard and restart
            match_addr = 0
            match_real = True
        if real_pos:
            match_real = True
        match_addr = e

    if match_addr == 0:
        print("locate MISS: no band entry matched the hovered unit's HP/MaxHP/Level")
        print("  Try: hover while in a live battle, not in menus")
        return

    e_br = ru8(match_addr + A_BRAVE)
    e_fa = ru8(match_addr + A_FAITH)
    print(f"located band entry: 0x{match_addr:X}  brave={e_br} faith={e_fa}")

    # Step 3: save current movement field
    saved = read_movement_field(match_addr)
    if saved is None:
        print("could not read movement field -- entry may have moved")
        return
    print(f"saved movement: {saved.hex().upper()}")

    # Step 4: write the grant
    if not write_movement_field(match_addr, grant_field):
        print("write FAILED (page not writable?)")
        return

    # Step 5: read-back verification
    rb = read_movement_field(match_addr)
    off, mask = enc
    if rb is not None and (rb[off] & mask) != 0:
        print(f"SET  ability {ability_id} granted (read-back confirmed)")
    else:
        print(f"MISS ability {ability_id}: write did not stick (rb={rb.hex().upper() if rb else 'None'})")

    # Step 6: hold loop -- re-assert every ~200ms until Ctrl+C
    print("Holding grant.  Ctrl+C to stop and restore.")
    try:
        while True:
            # SameUnit check: brave/faith must still match (Rapture.SameUnit discipline)
            br_now = ru8(match_addr + A_BRAVE)
            fa_now = ru8(match_addr + A_FAITH)
            if br_now != e_br or fa_now != e_fa:
                print(f"  unit migrated (brave/faith changed: was {e_br}/{e_fa}, now {br_now}/{fa_now}) -- stopped holding")
                break
            write_movement_field(match_addr, grant_field)
            time.sleep(0.2)
    except KeyboardInterrupt:
        print()

    # Restore
    print(f"Restoring movement to {saved.hex().upper()} ...")
    if not write_movement_field(match_addr, saved):
        print("  WARN: restore write failed (entry may have migrated)")
    else:
        rb2 = read_movement_field(match_addr)
        if rb2 == saved:
            print("  restored OK")
        else:
            print(f"  restore read-back: {rb2.hex().upper() if rb2 else 'None'} (expected {saved.hex().upper()})")


# ---------------------------------------------------------------------------
# Verb: buff_enemies  (plant a stealable buff on EVERY enemy so Larceny has targets)
# ---------------------------------------------------------------------------
def cmd_buff_enemies(name: str | None = None) -> None:
    """Plant a stealable buff on EVERY live enemy-side unit, then READ-watch the bits and report
    each one as Larceny strips it (or Ctrl+C). No hover/targeting -- it sweeps the whole band.

    By default the SIX buffs are dealt round-robin across the enemies (reraise, haste, protect,
    shell, reflect, regen, then repeat) so the steal log shows variety and exercises Larceny's
    precedence. Pass a name to force that one buff on everybody instead.

    The probe never re-asserts a bit, so it cannot fight the DLL's steal: a bit going clear IS the
    signal that hit's steal fired. See the module docstring for the full flow."""
    _require_game()
    if name is not None:
        name = name.lower()
        if name not in STEALABLE_BUFFS:
            print(f"unknown buff {name!r}; choose one of: {', '.join(STEALABLE_BUFFS)}")
            sys.exit(1)
    cycle = list(STEALABLE_BUFFS.items())   # [(name, (off, mask)), ...] in precedence order
    mode = name if name is not None else "varied (round-robin all 6)"
    print(f"buff_enemies: {mode}")

    targets = []   # (s, addr, br, fa, bname, off, mask)
    idx = 0
    for s in range(BAND_SLOTS):
        e = _band_entry_addr(s)
        if not _is_valid_entry(e) or s >= PLAYER_SLOT_THRESHOLD:
            continue                                   # invalid / player-side
        mhp = ru16(e + A_MAXHP)
        hp = ru16(e + A_HP)
        if not mhp or mhp <= 0 or not hp or hp <= 0:
            continue                                   # empty / dead
        if name is not None:
            bname, (off, mask) = name, STEALABLE_BUFFS[name]
        else:
            bname, (off, mask) = cycle[idx % len(cycle)]
            idx += 1
        cur = ru8(e + off)
        if cur is None:
            continue
        already = (cur & mask) != 0
        wu8(e + off, cur | mask)
        rb = ru8(e + off)
        if rb is None:
            print(f"  slot s={s:02d} 0x{e:X}  {bname} MISS (read-back failed)")
            continue
        ok = (rb & mask) != 0
        note = " (already had it)" if already else ""
        print(f"  slot s={s:02d} 0x{e:X}  {bname:<7} {'SET' if ok else 'MISS'} "
              f"(0x{cur:02X}->0x{rb:02X}){note}")
        targets.append((s, e, ru8(e + A_BRAVE), ru8(e + A_FAITH), bname, off, mask))

    if not targets:
        print("no live enemy-side units found -- are you in a battle (and is it loaded)?")
        return
    print(f"Buffed {len(targets)} enemy(ies). Now HIT them with the Arcanum wielder.")
    print("Watching the bits (no re-assert).  Ctrl+C to stop.")

    stripped = set()
    try:
        while len(stripped) < len(targets):
            for s, e, br, fa, bname, off, mask in targets:
                if s in stripped:
                    continue
                if ru8(e + A_BRAVE) != br or ru8(e + A_FAITH) != fa:
                    stripped.add(s)                    # slot reused/migrated -- stop tracking it
                    continue
                v = ru8(e + off)
                if v is None or (v & mask) == 0:
                    stripped.add(s)
                    print(f"  STRIPPED slot s={s:02d} -- Larceny cleared {bname} (a steal fired on this enemy).")
            time.sleep(0.2)
        print("All planted buffs gone. Confirm in livingweapon.log: "
              "'larceny: STOLE' + the buff icon on the wielder.")
    except KeyboardInterrupt:
        print()


# ---------------------------------------------------------------------------
# Verb: kill_all
# ---------------------------------------------------------------------------
def cmd_kill_all() -> None:
    """KO every enemy in the current battle via the authoritative band.

    Port of FFTHandsFree CheatHandlers.cs / KillEnemiesPlanner.Plan, adapted for
    external RPM/WPM access.  The original in-process handler runs a runtime HP-table
    discovery scan (searching 0x141800000..0x141900000 using scan-state player
    fingerprints from the bridge).  This port uses the fixed-address band directly
    (BandReadBase, 49 slots), which is what the DLL always uses -- no scan state needed.

    Classification: slot index s < PLAYER_SLOT_THRESHOLD (24) = enemy-side (n < 0 around
    the anchor); s >= 24 = player-side (n >= 0).  This matches the GrowthEngine scan-
    order logic (player-side pass first) and the static-array EnemySlotMax convention.

    Per-enemy kill writes (KillEnemiesPlanner.Plan recipe, verified live session 49):
      band+0x14 (A_HP)  u16 = 0x0000
      band+0x45 (A_DEAD_STATUS) u8 |= 0x20  (ADeadBit)
    The Reraise-clear (battle-array +0x47 bit 0x20) from the original handler is NOT
    ported here: it requires cross-referencing the static battle array by HP fingerprint.
    For teleport-recon purposes, enemies dead without Reraise-clear revive on the same
    turn if they have Reraise equipped.  kill_enemies_hard (kill twice) is the workaround.
    """
    _require_game()

    skipped_player = 0
    skipped_dead = 0
    skipped_invalid = 0

    # Phase 1: enumerate every live enemy-side unit -- no writes yet.
    candidates = []   # (s, e, hp, lvl, gx, gy, alleg)
    for s in range(BAND_SLOTS):
        e = _band_entry_addr(s)
        if not _is_valid_entry(e):
            skipped_invalid += 1
            continue

        hp   = ru16(e + A_HP)
        mhp  = ru16(e + A_MAXHP)

        if mhp is None or mhp <= 0:
            skipped_invalid += 1
            continue

        if s >= PLAYER_SLOT_THRESHOLD:
            skipped_player += 1
            continue

        if hp is None or hp <= 0:
            skipped_dead += 1
            continue

        lvl   = ru8(e + A_LEVEL)
        gx    = ru8(e + A_GX)
        gy    = ru8(e + A_GY)
        alleg = ru8(e + A_ALLEG)
        candidates.append((s, e, hp, lvl, gx, gy, alleg))

    # NO reliable guest auto-discriminator.  The +0x54 byte is NOT a clean team id:
    # across battles the same value (0x06) was a guest once and a killed enemy another
    # time, values split 3-way within one battle (0x06/0x16/0x1B), and the charm bit
    # 0x20 is unset on everyone.  So target ALL live enemy-side units and rely on the
    # y/n/x confirm: a guest is excluded by hand (its allegiance is shown to spot it).
    targets = [(s, e, hp, lvl, gx, gy, alleg) for s, e, hp, lvl, gx, gy, alleg in candidates]
    spared_allied = []

    for s, lvl, hp, gx, gy, alleg in spared_allied:
        print(f"  SPARED slot s={s:02d}: lvl={lvl} hp={hp} pos=({gx},{gy}) "
              f"alleg=0x{alleg:02X} (fights for the player)")

    if not targets:
        print("\nNo enemies found.  Are you in a live battle?")
        return

    print(f"\nAbout to KO {len(targets)} enemy-side unit(s):")
    for s, e, hp, lvl, gx, gy, alleg in targets:
        print(f"  slot s={s:02d} (n={s-24:+d})  lvl={lvl} hp={hp} pos=({gx},{gy})")
    print("GUESTS ARE NOT AUTO-DETECTED -- they sit in enemy-side slots and there is no")
    print("reliable team byte.  Exclude any guest/escort by slot, e.g. 'x 8 11', BEFORE")
    print("confirming.  Identify them by HP/position in the list above.")
    ans = input("KO these units? [y / n / x <slots>] ").strip().lower()
    if ans.startswith("x"):
        try:
            excl = {int(t) for t in ans[1:].split()}
        except ValueError:
            print("Could not parse slot numbers -- aborted, nothing written.")
            return
        spared = [t for t in targets if t[0] in excl]
        targets = [t for t in targets if t[0] not in excl]
        for s, e, hp, lvl, gx, gy, alleg in spared:
            print(f"  excluded slot s={s:02d} (lvl={lvl} hp={hp})")
        if not targets:
            print("Nothing left to KO -- aborted.")
            return
        print(f"Proceeding with the remaining {len(targets)} unit(s).")
    elif ans not in ("y", "yes"):
        print("Aborted -- nothing written.")
        return

    # Phase 2: write.
    killed = 0
    for s, e, hp, lvl, gx, gy, alleg in targets:
        hp_ok = wu16(e + A_HP, 0)
        db = ru8(e + A_DEAD_STATUS)
        dead_byte = (db | A_DEAD_BIT) if db is not None else A_DEAD_BIT
        db_ok = wu8(e + A_DEAD_STATUS, dead_byte)
        print(f"  slot s={s:02d}  0x{e:X}  hp {hp}->0  dead_byte=0x{dead_byte:02X}  "
              f"hp_ok={hp_ok} db_ok={db_ok}")
        killed += 1

    print(f"\nkill_all: {killed} enemies killed, {len(spared_allied)} allied spared, "
          f"{skipped_player} player slots skipped, "
          f"{skipped_dead} already dead, "
          f"{skipped_invalid} invalid/empty slots")
    print("End the current turn to see victory.")


# ---------------------------------------------------------------------------
# Verb: teams  (diagnostic -- who would kill_all touch, and why)
# ---------------------------------------------------------------------------
def cmd_revive() -> None:
    """Undo an accidental KO: for every enemy-side unit at hp=0 with the dead bit set,
    clear dead bit 0x20 at +0x45 and restore hp to maxHp.  Confirms before writing.
    Use right after a kill_all that caught a guest (Mustadio/Agrias) -- before the
    turn ends and the engine commits the death."""
    _require_game()
    downed = []
    for s in range(BAND_SLOTS):
        e = _band_entry_addr(s)
        if not _is_valid_entry(e):
            continue
        mhp = ru16(e + A_MAXHP)
        if mhp is None or mhp <= 0 or s >= PLAYER_SLOT_THRESHOLD:
            continue
        hp = ru16(e + A_HP)
        ds = ru8(e + A_DEAD_STATUS)
        if hp == 0 and ds is not None and (ds & A_DEAD_BIT):
            downed.append((s, e, mhp, ru8(e + A_GX), ru8(e + A_GY)))

    if not downed:
        print("No downed enemy-side units to revive.")
        return
    print(f"Revive {len(downed)} downed unit(s) to full HP:")
    for s, e, mhp, gx, gy in downed:
        print(f"  slot s={s:02d} pos=({gx},{gy}) -> hp {mhp}")
    if input("Revive these? [y/n] ").strip().lower() not in ("y", "yes"):
        print("Aborted -- nothing written.")
        return
    for s, e, mhp, gx, gy in downed:
        ds = ru8(e + A_DEAD_STATUS)
        wu8(e + A_DEAD_STATUS, (ds & ~A_DEAD_BIT) & 0xFF if ds is not None else 0)
        wu16(e + A_HP, mhp)
        print(f"  slot s={s:02d} revived to {mhp} hp")
    print("Done.  Note: if the turn already ended, the engine may have committed the KO.")


# ---------------------------------------------------------------------------
# Verb: godmode  (survive a boss alpha-strike)
# ---------------------------------------------------------------------------
def cmd_godmode(hp_floor: int = 999) -> None:
    """Hold every player-side unit at full HP (MaxHP raised to >= hp_floor) every ~10ms so
    a boss that acts first and one-shots the party cannot kill it.

    START THIS BEFORE entering the battle (on the formation / world-map screen). The band
    is empty pre-battle; the loop engages the instant units load -- before the boss can act
    -- so no in-battle pause is needed. Bumping MaxHP means the hit never reaches 0 (cleaner
    than reviving a corpse). Ctrl+C restores each unit's real MaxHP and stops.

    Level-99 does NOT work for this: FFT derives HP from level at battle start, so changing
    level mid-battle does not recompute stats. Holding HP directly is the reliable path.

    DISTINCT per-slot target (do NOT collapse to a flat floor): the Living Weapon actor
    resolver IDs the acting unit by matching the condensed struct's (maxHp, hp, level)
    against the battle band. A flat 999 floor equalizes every party member's MaxHP/HP, so
    that key collides -> the resolve goes ambiguous -> (0,0,0) -> the actor latch goes
    stale and every hit mis-credits the last cleanly-resolved weapon (live: everything
    tagged [w:89]). Stepping each slot DOWN from the floor (999, 998, 997, ...) keeps every
    player's (maxHp,hp) UNIQUE so the resolver stays unambiguous. We step DOWN, not up:
    999 is the HP ceiling, so a target above it risks the engine re-clamping back to 999
    (re-collapsing the values) or a UI glitch. 999-24 = 975 is still full-godmode HP held
    every 10ms -- survives any alpha strike. This is why the target is per-slot, not flat."""
    _require_game()
    print(f"GODMODE armed: player HP held full, MaxHP floor {hp_floor}.  Ctrl+C to stop.")
    print("Start this BEFORE entering the boss battle -- it engages the moment units load.")
    orig_maxhp: dict[int, int] = {}   # slot -> real MaxHP, saved on first sighting (pre-bump)
    seeded = False
    try:
        while True:
            for s in range(PLAYER_SLOT_THRESHOLD, BAND_SLOTS):
                e = _band_entry_addr(s)
                if not _is_valid_entry(e):
                    continue
                mhp = ru16(e + A_MAXHP)
                if mhp is None or mhp <= 0 or mhp > 60000:
                    continue
                if s not in orig_maxhp:
                    orig_maxhp[s] = mhp           # remember the real max before bumping
                    if not seeded:
                        print("  units detected -- holding.")
                        seeded = True
                # Step DOWN from the floor by the slot offset so each player's (maxHp,hp)
                # stays UNIQUE (keeps the actor resolver unambiguous -- see the docstring).
                # Stay at/under 999 (the HP ceiling) so the engine can't re-clamp the value.
                target = max(hp_floor - (s - PLAYER_SLOT_THRESHOLD), 1)
                if mhp != target:
                    wu16(e + A_MAXHP, target)
                wu16(e + A_HP, target)            # full heal every pass
                ds = ru8(e + A_DEAD_STATUS)
                if ds is not None and (ds & A_DEAD_BIT):
                    wu8(e + A_DEAD_STATUS, ds & ~A_DEAD_BIT & 0xFF)   # un-kill if it slipped through
            time.sleep(0.01)
    except KeyboardInterrupt:
        print("\nStopping godmode -- restoring real MaxHP ...")
        for s, omhp in orig_maxhp.items():
            e = _band_entry_addr(s)
            if not _is_valid_entry(e):
                continue
            wu16(e + A_MAXHP, omhp)
            cur = ru16(e + A_HP)
            if cur is not None and cur > omhp:
                wu16(e + A_HP, omhp)              # clamp HP back down to the real max
        print(f"Restored MaxHP on {len(orig_maxhp)} unit(s).")


def cmd_pa99(value: int = 99) -> None:
    """Set every live player-side unit's Physical Attack to <value> (default 99) so a weak
    party hits hard. One-shot -- a battle restart resets stats, so re-run after restarting.
    PA drives formula-2 (basic melee) damage; it does NOT affect gun damage (WP-squared)."""
    _require_game()
    value = max(1, min(255, value))
    done = []
    for s in range(PLAYER_SLOT_THRESHOLD, BAND_SLOTS):
        e = _band_entry_addr(s)
        if not _is_valid_entry(e):
            continue
        mhp = ru16(e + A_MAXHP)
        if not mhp or mhp <= 0:
            continue
        old = ru8(e + A_PA)
        wu8(e + A_PA, value)
        done.append((s, ru8(e + A_LEVEL), old, ru8(e + A_PA)))
    if not done:
        print("No live player-side units found.  Are you in a battle?")
        return
    print(f"PA set to {value} on {len(done)} unit(s):")
    for s, lvl, o, n in done:
        print(f"  slot {s:02d} lvl {lvl}: PA {o} -> {n}")
    print("Re-run after a battle restart (it resets stats).")


def cmd_sentinels() -> None:
    """Print the battle-state sentinels the treasure module gates on. Run this on the
    formation/placement screen, or in a two-phase boss fight's second phase, so the
    treasure gate (battleDisplayed = slot9==0xFFFFFFFF and battleMode != 0) can be checked."""
    _require_game()
    def ru32(a):
        b = rpm(a, 4)
        return int.from_bytes(b, "little") if b else None
    # 1.5 re-anchored (Offsets.cs): Slot0/Slot9 +0x6000, BattleMode +0x6350,
    # LiveBattleMapId +0x6C3C, PauseFlag.
    slot0 = ru32(0x140782A30)
    slot9 = ru32(0x140782A54)
    mode  = ru8(0x1409069A0)
    mapid = ru8(0x140784478)
    pause = ru8(0x140C6B1C8)
    disp  = (slot9 == 0xFFFFFFFF) and (mode not in (None, 0))
    print(f"slot0={slot0:#x} slot9={slot9:#x} battleMode={mode} mapId={mapid} pauseFlag={pause}")
    print(f"battleDisplayed (slot9==FFFFFFFF and mode!=0) = {disp}  "
          f"-> treasure {'CAN' if disp else 'will NOT'} arm here")


def cmd_myturn() -> None:
    """Dominate the turn order: set every player-side unit's Speed to 99 and every enemy-side
    unit's Speed to 1, and reset enemy CT to 0 so any enemy about to act is pushed back. The
    player then charges to a turn ~99x faster than the enemies -- effectively every turn. CT
    is charge-rate based (same system ExtraTurn uses). One-shot -- re-run after a restart."""
    _require_game()
    players = enemies = 0
    for s in range(BAND_SLOTS):
        e = _band_entry_addr(s)
        if not _is_valid_entry(e):
            continue
        mhp = ru16(e + A_MAXHP)
        if not mhp or mhp <= 0:
            continue
        hp = ru16(e + A_HP)
        if s >= PLAYER_SLOT_THRESHOLD:
            wu8(e + A_SPEED, 99)
            players += 1
        elif hp and hp > 0:
            wu8(e + A_SPEED, 1)
            wu8(e + A_CT, 0)       # reset any charged enemy back to the start
            enemies += 1
    print(f"myturn: {players} player unit(s) -> Speed 99, {enemies} enemy unit(s) -> Speed 1 + CT reset.")
    print("You now charge ~99x faster than the enemies.  Re-run after a battle restart.")


def cmd_teams() -> None:
    """Dump every live band slot with its side and the kill_all verdict.
    Every live enemy-side unit is a TARGET; guests/escorts also sit enemy-side and
    are NOT distinguishable by any team byte (the +0x54 allegiance value is shown for
    reference only -- it does not separate guests from enemies).  Exclude guests by
    slot in kill_all."""
    _require_game()
    print(f"{'slot':>4} {'side':<7} {'lvl':>3} {'hp':>5}/{'mhp':<5} {'pos':<9} "
          f"{'alleg':<6} kill_all?")
    for s in range(BAND_SLOTS):
        e = _band_entry_addr(s)
        if not _is_valid_entry(e):
            continue
        mhp = ru16(e + A_MAXHP)
        if mhp is None or mhp <= 0:
            continue
        hp    = ru16(e + A_HP)
        lvl   = ru8(e + A_LEVEL)
        gx    = ru8(e + A_GX)
        gy    = ru8(e + A_GY)
        alleg = ru8(e + A_ALLEG)
        if s >= PLAYER_SLOT_THRESHOLD:
            verdict = "no (player side)"
        elif hp is None or hp <= 0:
            verdict = "no (dead)"
        else:
            verdict = "TARGET (exclude by slot if guest)"
        a = f"0x{alleg:02X}" if alleg is not None else "??"
        print(f"{s:>4} {('player' if s>=PLAYER_SLOT_THRESHOLD else 'enemy'):<7} "
              f"{lvl!s:>3} {hp!s:>5}/{mhp!s:<5} ({gx},{gy})    {a:<6} {verdict}")


# ---------------------------------------------------------------------------
# Self-test  (no game required)
# ---------------------------------------------------------------------------
def _selftest() -> bool:
    ok = True

    # Movement-bit encoding: ability 243 (Master Teleportation = RaptureMoveId)
    # pos = 243 - 230 = 13  =>  byte_off = 13//8 = 1, bit_off = 13%8 = 5
    # mask = 0x80 >> 5 = 0x04
    enc = movement_bit(243)
    if enc == (1, 0x04):
        print("  movement_bit(243): off=1 mask=0x04  OK")
    else:
        print(f"  movement_bit(243): FAIL expected (1, 0x04) got {enc}")
        ok = False

    # Boundary: ability 230 = first bit (offset 0, mask 0x80)
    enc230 = movement_bit(230)
    if enc230 == (0, 0x80):
        print("  movement_bit(230): off=0 mask=0x80  OK")
    else:
        print(f"  movement_bit(230): FAIL expected (0, 0x80) got {enc230}")
        ok = False

    # Out-of-range: 253 = 230 + 23 (last valid), 254 = 230+24 out
    enc253 = movement_bit(253)
    enc254 = movement_bit(254)
    enc229 = movement_bit(229)
    if enc253 is not None and enc254 is None and enc229 is None:
        print("  movement_bit range guard: OK")
    else:
        print(f"  movement_bit range guard: FAIL enc253={enc253} enc254={enc254} enc229={enc229}")
        ok = False

    # Grant field: ability 243 => only byte 1 bit 0x04 set, rest 0
    field = build_grant_field(243)
    if field == bytes([0x00, 0x04, 0x00]):
        print("  build_grant_field(243): [00 04 00]  OK")
    else:
        print(f"  build_grant_field(243): FAIL expected [00 04 00] got {list(field) if field else None}")
        ok = False

    # Stealable-buff table matches LarcenyPolicy.Stealable / Offsets.cs (band-relative).
    want_buffs = {"reraise": (0x47, 0x20), "haste": (0x48, 0x08), "protect": (0x48, 0x20),
                  "shell": (0x48, 0x10), "reflect": (0x49, 0x02), "regen": (0x48, 0x40)}
    if STEALABLE_BUFFS == want_buffs:
        print("  STEALABLE_BUFFS offsets match Offsets.cs  OK")
    else:
        print(f"  STEALABLE_BUFFS: FAIL got {STEALABLE_BUFFS}")
        ok = False

    # Dead-bit constant matches Offsets.cs ADeadBit
    if A_DEAD_BIT == 0x20:
        print("  A_DEAD_BIT == 0x20  OK")
    else:
        print(f"  A_DEAD_BIT: FAIL expected 0x20 got 0x{A_DEAD_BIT:02X}")
        ok = False


    # Band address arithmetic: slot 0 => n=-24, slot 24 => n=0 (anchor), slot 48 => n=+24
    slot0  = _band_entry_addr(0)
    slot24 = _band_entry_addr(24)
    slot48 = _band_entry_addr(48)
    expected0  = COMBAT_ANCHOR + BAND_ENTRY - 24 * COMBAT_STRIDE
    expected24 = COMBAT_ANCHOR + BAND_ENTRY
    expected48 = COMBAT_ANCHOR + BAND_ENTRY + 24 * COMBAT_STRIDE
    if slot0 == expected0 and slot24 == expected24 and slot48 == expected48:
        print(f"  band address arithmetic: slot0=0x{slot0:X} slot24=0x{slot24:X}  OK")
    else:
        print(f"  band address arithmetic: FAIL")
        print(f"    slot0:  got 0x{slot0:X}  want 0x{expected0:X}")
        print(f"    slot24: got 0x{slot24:X}  want 0x{expected24:X}")
        print(f"    slot48: got 0x{slot48:X}  want 0x{expected48:X}")
        ok = False

    # Player threshold: slot 24 is player-side (n=0), slot 23 is enemy-side (n=-1)
    if PLAYER_SLOT_THRESHOLD == 24:
        print("  PLAYER_SLOT_THRESHOLD == 24  OK")
    else:
        print(f"  PLAYER_SLOT_THRESHOLD: FAIL expected 24 got {PLAYER_SLOT_THRESHOLD}")
        ok = False

    # Level drift: live=50, roster=42 => ok (diff=8<=9); live=50, roster=40 => fail (diff=10)
    if _level_matches(42, 50) and not _level_matches(40, 50):
        print("  level drift guard: OK")
    else:
        print(f"  level drift guard: FAIL (42->50={_level_matches(42,50)}, 40->50={_level_matches(40,50)})")
        ok = False

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
        passed = _selftest()
        if passed:
            print("\nAll self-tests PASSED.")
            sys.exit(0)
        else:
            print("\nSelf-test FAILED.")
            sys.exit(1)

    if args[0] == "give_move":
        ability_id = int(args[1]) if len(args) > 1 else 243
        cmd_give_move(ability_id)
        return

    if args[0] in ("buff_enemies", "buff_enemy"):
        name = args[1] if len(args) > 1 else None   # None -> varied round-robin
        cmd_buff_enemies(name)
        return

    if args[0] == "kill_all":
        cmd_kill_all()
        return

    if args[0] == "revive":
        cmd_revive()
        return

    if args[0] == "pa99":
        val = int(args[1]) if len(args) > 1 else 99
        cmd_pa99(val)
        return

    if args[0] == "myturn":
        cmd_myturn()
        return

    if args[0] == "sentinels":
        cmd_sentinels()
        return

    if args[0] == "teams":
        cmd_teams()
        return

    if args[0] == "godmode":
        floor = int(args[1]) if len(args) > 1 else 999
        cmd_godmode(floor)
        return

    print(f"Unknown verb: {args[0]!r}")
    print(__doc__)
    sys.exit(2)


if __name__ == "__main__":
    main()
