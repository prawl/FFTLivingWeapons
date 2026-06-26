#!/usr/bin/env python
"""
Dual-gun probe (WRITE, fully reversible). Test the novel "+3 gun twin-fire" signature:
force a SECOND gun into a unit's off-hand so its basic Attack fires twice at range.

In FFT, dual-wield = the Two Swords / Dual Wield SUPPORT ability (id 221). It does two
things: (a) lets a weapon sit in the normally-shield off-hand slot, and (b) makes a basic
Attack strike with BOTH equipped weapons. This probe writes the equip-block + support pieces
in a SMALLEST-EXPERIMENT-FIRST ladder and HOLDS them so a human watching the screen can read
the result -- then restores every byte it touched on exit.

GROUND TRUTH (LivingWeapon/Offsets.cs, all 1.5-CONFIRMED -- the pre-1.5 addresses in
ct_probe.py are STALE; this probe owns its own constants so it can't inherit that drift):

  ROSTER struct (pre-construction blueprint, static module addr, no ASLR):
    base 0x1411A7D10, stride 0x258, 20 party slots.  Equip block (u16 LE, empty = 0x00FF):
      +0x0A RSupport (u8!) picked support ability id   (Offsets.cs:84)
      +0x12 accessory                                   (Offsets.cs:74)
      +0x14 RRHand  right-hand weapon                   (Offsets.cs:79)
      +0x16 RLHand  left-hand   (live: STAYS EMPTY)     (Offsets.cs:80)
      +0x18 ROffHand the REAL dual-wield off-hand       (Offsets.cs:81-83, live roster dump)
      +0x1A shield  (NOT a named const; per Offsets.cs:82 comment shields land here)
      +0x1D RLevel  +0x1E RBrave  +0x1F RFaith          (fingerprint)
      +0x230 RNameId (identity: Ramza = 1)              (Offsets.cs:88)

  COMBAT struct (live battle unit, built FROM the roster at construction):
    CombatAnchor 0x141855CE0, stride 0x200.  CWeapon +0x20 (self-map key).  CSupport +0x98
    (4 bytes, base id 198, MSB-first).  We locate the live unit by fingerprint (lvl/brave/
    faith) -- NOT by any band-array constant -- so a struct relocation can't fool us.
    Dual Wield id 221 -> pos 221-198 = 23 -> support byte 2, mask 0x80>>7 = 0x01.
    (Signatures.cs:118 IsBuildTimeOnly(221) = TRUE -- the live ledger 2026-06-08 says this
     grant reads back fine but does NOTHING. Step 2 EXISTS TO RE-PROVE that wall for guns.)

  Guns = item ids 71-76 (data/items.json): 71-73 formula-3 physical, 74-76 formula-4 element.

WHY THIS PROBE IS THE RIGHT FIRST MOVE: the off-hand SLOT write is the cheap, proven-shape,
reversible 80% (the same equip-block the anti-break work round-trips). The unproven 20% is
whether a GUN is engine-legal to dual-wield AND whether the dual-strike fires from a LIVE
write. The FFTHandsFree wiki says guns are dual-wield-INELIGIBLE in vanilla data; the ledger
says the Dual Wield support is construction-welded. So the most likely live outcome is
"nothing fires twice" -- and this probe is built to make that verdict UNAMBIGUOUS, not to
assume it. If even the slot write is rejected, the fallback ladder says what to try next.

HARD RULES honored: guarded RPM/WPM on our own handle (never a raw deref); every byte saved
before write and restored on exit (Ctrl+C safe); a failed read aborts BEFORE any write.

USAGE (game running, IN a live battle, with the target unit DEPLOYED on the field):
  python dualgun_probe.py list
        # read-only: dump every occupied roster slot's equip block + fingerprint + nameId.
        # Pick your wielder (note its nameId, or its lvl/brave/faith).
  python dualgun_probe.py show  <nameId>
        # read-only: full equip block + the live combat-struct support bitfield for that unit.
  python dualgun_probe.py twin  <nameId> [gunId=copy-main] [seconds=180]
        # STEP 1: write the gun into roster off-hand (+0x18), clear shield (+0x1A), HOLD.
        #   default gunId = whatever is in the main hand (so a true twin); pass 71-76 to mix.
        #   Watch: does a 2nd gun render? On this unit's Attack, does it fire twice?
  python dualgun_probe.py twin+dw <nameId> [gunId=copy-main] [seconds=180]
        # STEP 2: STEP 1 + also OR-set the Dual Wield support bit (id 221) on the LIVE combat
        #   struct AND the roster RSupport byte, held every tick. This is the full live attempt.
  python dualgun_probe.py restore
        # emergency: restore from the saved snapshot file without re-running (if a hold crashed).

IMPORTANT: a roster write commits to the SAVE only on a WON battle; a QUIT reverts it
(LIVE_LEDGER 2026-06-16). Still, this probe always restores on clean exit. Don't restart the
battle mid-hold (the live combat struct relocates -- the probe re-finds it, but re-baseline
by re-running `show` if anything looks off).
"""
import ctypes as C
from ctypes import wintypes as W
import json
import os
import sys
import tempfile
import time

PROC = "FFT_enhanced"

# --- 1.5-CONFIRMED constants (LivingWeapon/Offsets.cs). NOT imported from ct_probe (stale). ---
ROSTER_BASE = 0x1411A7D10
ROSTER_STRIDE = 0x258
ROSTER_SLOTS = 20
R_SUPPORT = 0x0A   # u8  picked support ability id
R_ACCESSORY = 0x12  # u16
R_RHAND = 0x14     # u16 right-hand weapon (main)
R_LHAND = 0x16     # u16 left-hand (live: empty)
R_OFFHAND = 0x18   # u16 the REAL dual-wield off-hand weapon
R_SHIELD = 0x1A    # u16 shield (occupies the off-hand visual slot on a 1H+shield loadout)
R_HELM = 0x0E      # u16
R_BODY = 0x10      # u16
R_LEVEL = 0x1D     # u8
R_BRAVE = 0x1E     # u8
R_FAITH = 0x1F     # u8
R_NAMEID = 0x230   # u16

COMBAT_ANCHOR = 0x141855CE0
COMBAT_STRIDE = 0x200
COMBAT_SEARCH = 24      # scan +/- this many slots around the anchor
C_WEAPON = 0x20        # u16 equipped weapon id (self-map key)
C_LEVEL = 0x29         # u8
C_BRAVE = 0x2A         # u8
C_FAITH = 0x2C         # u8
C_HP = 0x30            # u16
C_MAXHP = 0x32         # u16
C_SUPPORT = 0x98       # 4 bytes, base id 198, MSB-first

EMPTY = 0xFFFF         # we write the wide empty sentinel; engine also uses 0x00FF (read either)
# NOTE (live 2026-06-21): the equip slot stores the IC-NATIVE item id, NOT the items.json id.
# Proven on Ramza -- an equipped Stoneshooter reads 67, but items.json id 73 = Stoneshooter
# (items.json 67 = "Warbrand", a Sword). So Offsets.cs's "+0x14 == items.json id" is WRONG, and
# 71-76 is NOT the gun range in this id space. We therefore do NOT gate on a gun-id range: the
# twin test just duplicates whatever is in the main hand (or an explicit RAW equipped-space id).
DUAL_WIELD_ID = 221    # Two Swords / Dual Wield support ability
SUPPORT_BASE = 198     # ability id of support bit 0

SNAP_PATH = os.path.join(tempfile.gettempdir(), "fft_dualgun_snapshot.json")

# --- kernel32 / psapi RPM/WPM (same idiom as ct_probe / kit_stamp_probe) ---
k32 = C.WinDLL("kernel32", use_last_error=True)
psapi = C.WinDLL("psapi", use_last_error=True)
PV = 0x0010 | 0x0008 | 0x0400          # VM_READ | VM_OPERATION | QUERY_INFORMATION
PV_W = PV | 0x0020                      # + VM_WRITE


class _PMC(C.Structure):
    _fields_ = [("cb", W.DWORD), ("PageFaultCount", W.DWORD),
                ("PeakWorkingSetSize", C.c_size_t), ("WorkingSetSize", C.c_size_t),
                ("QuotaPeakPagedPoolUsage", C.c_size_t), ("QuotaPagedPoolUsage", C.c_size_t),
                ("QuotaPeakNonPagedPoolUsage", C.c_size_t), ("QuotaNonPagedPoolUsage", C.c_size_t),
                ("PagefileUsage", C.c_size_t), ("PeakPagefileUsage", C.c_size_t)]


def _working_set(pid):
    h = k32.OpenProcess(0x0400 | 0x0010, False, pid)   # QUERY_INFORMATION | VM_READ
    if not h:
        return 0
    pmc = _PMC(); pmc.cb = C.sizeof(pmc)
    ok = psapi.GetProcessMemoryInfo(h, C.byref(pmc), pmc.cb)
    k32.CloseHandle(h)
    return pmc.WorkingSetSize if ok else 0


def find_pid(name):
    # Return the matching process with the LARGEST working set (the real rendered game).
    # FIRST-match was the 2026-06-21 bug: with two FFT_enhanced instances open, it kept
    # attaching to a stale ~630MB instance, so every write silently hit a dead process
    # while the live ~3GB game never changed.
    class PE32(C.Structure):
        _fields_ = [("dwSize", W.DWORD), ("cntUsage", W.DWORD), ("th32ProcessID", W.DWORD),
                    ("th32DefaultHeapID", C.POINTER(C.c_ulong)), ("th32ModuleID", W.DWORD),
                    ("cntThreads", W.DWORD), ("th32ParentProcessID", W.DWORD),
                    ("pcPriClassBase", C.c_long), ("dwFlags", W.DWORD), ("szExeFile", C.c_char * 260)]
    snap = k32.CreateToolhelp32Snapshot(0x2, 0)
    e = PE32(); e.dwSize = C.sizeof(e); matches = []
    if k32.Process32First(snap, C.byref(e)):
        while True:
            if name.lower() in e.szExeFile.decode(errors="ignore").lower():
                matches.append(e.th32ProcessID)
            if not k32.Process32Next(snap, C.byref(e)):
                break
    k32.CloseHandle(snap)
    if not matches:
        return None
    return max(matches, key=_working_set)


def rd(h, a, n):
    b = (C.c_ubyte * n)(); g = C.c_size_t(0)
    if k32.ReadProcessMemory(h, C.c_void_p(a), b, n, C.byref(g)) and g.value == n:
        return bytes(b)
    return None


def wr(h, a, data):
    buf = (C.c_ubyte * len(data))(*data)
    return bool(k32.WriteProcessMemory(h, C.c_void_p(a), buf, len(data), C.byref(C.c_size_t(0))))


def u8(h, a):
    b = rd(h, a, 1); return b[0] if b else None


def u16(h, a):
    b = rd(h, a, 2); return (b[0] | (b[1] << 8)) if b else None


def w16(h, a, v):
    return wr(h, a, bytes([v & 0xFF, (v >> 8) & 0xFF]))


def fmt_item(v):
    if v is None: return "??"
    if v in (0x00FF, 0xFFFF): return "(empty)"
    return str(v)


# --- roster slot location -------------------------------------------------
def iter_roster(h):
    """Yield (slot, base, lvl, br, fa, nameId) for every occupied roster slot."""
    for s in range(ROSTER_SLOTS):
        base = ROSTER_BASE + s * ROSTER_STRIDE
        lvl = u8(h, base + R_LEVEL)
        if lvl is None or lvl < 1 or lvl > 99:
            continue            # empty slot / unreadable
        yield (s, base,
               lvl, u8(h, base + R_BRAVE), u8(h, base + R_FAITH),
               u16(h, base + R_NAMEID))


def find_roster_slot(h, name_id):
    for s, base, lvl, br, fa, nid in iter_roster(h):
        if nid == name_id:
            return base, (lvl, br, fa)
    return None, None


# --- live combat-struct location by fingerprint (no band constant trusted) ---
def find_combat_struct(h, fp):
    """Locate the live combat struct for fingerprint (lvl,br,fa) by scanning +/- slots around
    CombatAnchor and matching CLevel/CBrave/CFaith on a sane (HP<=MaxHP) struct. Returns base
    or None. Read-only; used to set/read the support bitfield on the AUTHORITATIVE live unit."""
    lvl, br, fa = fp
    for n in range(-COMBAT_SEARCH, COMBAT_SEARCH + 1):
        base = COMBAT_ANCHOR + n * COMBAT_STRIDE
        clvl = u8(h, base + C_LEVEL)
        if clvl != lvl:
            continue
        if u8(h, base + C_BRAVE) != br or u8(h, base + C_FAITH) != fa:
            continue
        mhp = u16(h, base + C_MAXHP); hp = u16(h, base + C_HP)
        if not mhp or mhp > 9999 or hp is None or hp > mhp:
            continue
        return base
    return None


def support_bit(ability_id):
    """(byteOffset, mask) of a support id in the 4-byte MSB-first support bitfield (base 198)."""
    pos = ability_id - SUPPORT_BASE
    return pos // 8, 0x80 >> (pos % 8)


def decode_supports(field):
    ids = []
    for bo in range(len(field)):
        for bit in range(8):
            if field[bo] & (0x80 >> bit):
                ids.append(SUPPORT_BASE + bo * 8 + bit)
    return ids


# --- read-only dumps ------------------------------------------------------
def cmd_list(h):
    print(f"roster @{ROSTER_BASE:012X} stride 0x{ROSTER_STRIDE:X}\n")
    print(f"{'slot':>4} {'nameId':>6} {'lvl':>3} {'br':>3} {'fa':>3} "
          f"{'rHand':>6} {'lHand':>6} {'offHand':>7} {'shield':>6} {'support':>7}")
    any_row = False
    for s, base, lvl, br, fa, nid in iter_roster(h):
        any_row = True
        rh = fmt_item(u16(h, base + R_RHAND))
        lh = fmt_item(u16(h, base + R_LHAND))
        oh = fmt_item(u16(h, base + R_OFFHAND))
        sh = fmt_item(u16(h, base + R_SHIELD))
        sup = u8(h, base + R_SUPPORT)
        print(f"{s:>4} {nid:>6} {lvl:>3} {br:>3} {fa:>3} "
              f"{rh:>6} {lh:>6} {oh:>7} {sh:>6} {sup if sup is not None else '??':>7}")
    if not any_row:
        print("  (no occupied roster slots -- is a game/save loaded?)")
    print("\nPick your wielder's nameId, then: python dualgun_probe.py show <nameId>")


def cmd_show(h, name_id):
    base, fp = find_roster_slot(h, name_id)
    if base is None:
        print(f"nameId {name_id} not found in roster (run `list`)."); return
    lvl, br, fa = fp
    print(f"roster slot @{base:012X}  nameId={name_id}  fp(lvl/br/fa)={lvl}/{br}/{fa}")
    print(f"  rHand(+0x14)   = {fmt_item(u16(h, base + R_RHAND))}")
    print(f"  lHand(+0x16)   = {fmt_item(u16(h, base + R_LHAND))}")
    print(f"  offHand(+0x18) = {fmt_item(u16(h, base + R_OFFHAND))}")
    print(f"  shield(+0x1A)  = {fmt_item(u16(h, base + R_SHIELD))}")
    print(f"  support(+0x0A) = {u8(h, base + R_SUPPORT)} (Dual Wield = {DUAL_WIELD_ID})")
    rh = u16(h, base + R_RHAND)
    if rh is None or rh in (0x00FF, 0xFFFF):
        print(f"  NOTE: main hand is {fmt_item(rh)} (empty/unreadable) -- equip a weapon, or pass "
              f"an explicit off-hand id, before the twin test.")
    else:
        print(f"  -> twin test will duplicate main-hand id {rh} into the off-hand "
              f"(raw equipped-space id; pass an explicit id to mix a different weapon).")
    cb = find_combat_struct(h, fp)
    if cb is None:
        print("  live combat struct: NOT located (unit not deployed this battle?)")
    else:
        sup = rd(h, cb + C_SUPPORT, 4)
        bo, mask = support_bit(DUAL_WIELD_ID)
        cur = "set" if (sup and sup[bo] & mask) else "clear"
        print(f"  live combat struct @{cb:012X}  weapon(+0x20)={u16(h, cb + C_WEAPON)}")
        print(f"    support bitfield(+0x98) = {' '.join(f'{x:02X}' for x in sup) if sup else '??'} "
              f"-> ids {decode_supports(sup) if sup else '??'}")
        print(f"    Dual Wield bit (byte {bo} mask {mask:#04x}) is {cur}")


# --- the write+hold ladder ------------------------------------------------
def snapshot(h, base, cb):
    """Save every byte we might touch, to file, BEFORE any write. Aborts (returns None) if any
    read fails -- never write off a partial snapshot."""
    snap = {"roster_base": base, "combat_base": cb}
    for off in (R_OFFHAND, R_SHIELD, R_SUPPORT):
        v = (u8 if off == R_SUPPORT else u16)(h, base + off)
        if v is None:
            print("  snapshot read failed -- aborting before any write."); return None
        snap[str(off)] = v
    if cb is not None:
        sup = rd(h, cb + C_SUPPORT, 4)
        if sup is None:
            print("  combat support read failed -- aborting before any write."); return None
        snap["combat_support"] = list(sup)
    with open(SNAP_PATH, "w") as f:
        json.dump(snap, f)
    print(f"  snapshot saved -> {SNAP_PATH}")
    return snap


def restore_from(h, snap):
    base = snap["roster_base"]
    w16(h, base + R_OFFHAND, snap[str(R_OFFHAND)])
    w16(h, base + R_SHIELD, snap[str(R_SHIELD)])
    wr(h, base + R_SUPPORT, bytes([snap[str(R_SUPPORT)]]))
    cb = snap.get("combat_base")
    if cb is not None and "combat_support" in snap:
        wr(h, cb + C_SUPPORT, bytes(snap["combat_support"]))
    print("  restored roster off-hand/shield/support" +
          ("" if cb is None else " + combat support bitfield") +
          f" (off-hand back to {fmt_item(snap[str(R_OFFHAND)])}).")


def cmd_twin(h, name_id, gun_id, with_dw, seconds):
    base, fp = find_roster_slot(h, name_id)
    if base is None:
        print(f"nameId {name_id} not found in roster (run `list`)."); return
    lvl, br, fa = fp
    main = u16(h, base + R_RHAND)
    if gun_id is None:
        gun_id = main
    if gun_id is None or gun_id in (0x00FF, 0xFFFF):
        print(f"refusing: main hand reads {fmt_item(gun_id)} (empty/unreadable) and no explicit "
              f"off-hand id was given. Equip a weapon, or pass a raw equipped-space id.")
        return

    cb = find_combat_struct(h, fp) if with_dw else None
    if with_dw and cb is None:
        print("Dual Wield mode but live combat struct not located -- the unit must be DEPLOYED. "
              "Proceeding with the ROSTER RSupport write only (no live combat-bit hold).")

    print(f"target nameId={name_id}  roster@{base:012X}  fp={lvl}/{br}/{fa}  main hand={fmt_item(main)}")
    print(f"plan: write gun {gun_id} -> off-hand(+0x18), clear shield(+0x1A)" +
          (f", set Dual Wield (id {DUAL_WIELD_ID}) on roster +0x0A" +
           (" AND live combat +0x98" if cb else "") if with_dw else "") + ".")

    snap = snapshot(h, base, cb)
    if snap is None:
        return

    bo, mask = support_bit(DUAL_WIELD_ID)
    asserts = 0
    t0 = time.time()
    last_s = -1
    try:
        # initial write
        w16(h, base + R_OFFHAND, gun_id)
        w16(h, base + R_SHIELD, EMPTY)
        if with_dw:
            wr(h, base + R_SUPPORT, bytes([DUAL_WIELD_ID]))
            if cb is not None:
                cur = rd(h, cb + C_SUPPORT + bo, 1)
                if cur is not None:
                    wr(h, cb + C_SUPPORT + bo, bytes([cur[0] | mask]))
        oh = u16(h, base + R_OFFHAND)
        sh = u16(h, base + R_SHIELD)
        print(f"  WROTE: off-hand readback={fmt_item(oh)}  shield readback={fmt_item(sh)}")
        if oh != gun_id:
            print("  WARNING: off-hand did NOT read back as the gun -- the engine may be "
                  "rejecting an off-hand gun id at the field level (FAIL signal for the SLOT write).")
        print(f"\n  HOLDING for {seconds:.0f}s. NOW on the screen:")
        print(f"   - look at this unit's model: does a SECOND gun appear? (expected: NO -- "
              f"weapon graphics are construction-welded)")
        print(f"   - take this unit's turn, choose Attack on an enemy in gun range:")
        print(f"       * TWO separate shots / two damage numbers = TWIN-FIRE WORKS (huge -- update ledger)")
        print(f"       * one shot only = dual-strike is construction-gated (expected per ledger 2026-06-08)")
        print(f"   - any crash / garbage = STOP (Ctrl+C restores).\n")

        while time.time() - t0 < seconds:
            # re-hold the roster off-hand + shield (the engine may re-normalize on Organize/menu)
            if u16(h, base + R_OFFHAND) != gun_id:
                w16(h, base + R_OFFHAND, gun_id); asserts += 1
            if u16(h, base + R_SHIELD) not in (EMPTY, 0x00FF):
                w16(h, base + R_SHIELD, EMPTY); asserts += 1
            if with_dw:
                if u8(h, base + R_SUPPORT) != DUAL_WIELD_ID:
                    wr(h, base + R_SUPPORT, bytes([DUAL_WIELD_ID])); asserts += 1
                if cb is not None:
                    # re-find if the combat struct relocated (fingerprint drifted)
                    if u8(h, cb + C_LEVEL) != lvl or u8(h, cb + C_BRAVE) != br:
                        cb = find_combat_struct(h, fp)
                    if cb is not None:
                        cur = rd(h, cb + C_SUPPORT + bo, 1)
                        if cur is not None and not (cur[0] & mask):
                            wr(h, cb + C_SUPPORT + bo, bytes([cur[0] | mask])); asserts += 1
            s = int(time.time() - t0)
            if s != last_s:
                last_s = s
                oh = fmt_item(u16(h, base + R_OFFHAND))
                cw = u16(h, cb + C_WEAPON) if cb else None
                print(f"  t={s:>3}s  off-hand={oh}  combatWeapon={fmt_item(cw)}  re-holds={asserts}")
            time.sleep(0.10)
    except KeyboardInterrupt:
        print("\n(interrupted)")
    finally:
        restore_from(h, snap)
        try:
            os.remove(SNAP_PATH)
        except OSError:
            pass
    print(f"done. engine re-normalized our writes {asserts}x (0 = held cleanly; many = an "
          f"Organize/normalize source fights us -- the hold still wins while the screen shows the test).")


SLOTS = {"rhand": R_RHAND, "lhand": R_LHAND, "offhand": R_OFFHAND, "shield": R_SHIELD}


def cmd_poke(h, name_id, slot_name, gun_id, with_dw, seconds):
    """Force an item id into ONE chosen equip slot (lhand +0x16 / offhand +0x18 / shield +0x1A)
    and HOLD it, optionally also granting Dual Wield. Single-slot: does NOT clear any other slot,
    so it isolates 'which slot does the engine actually honor for a visible/usable second weapon'.
    Restores the slot (and support) on clean exit / Ctrl+C. For the PRIMARY slot (rhand +0x14) it
    ALSO writes+holds the LIVE combat weapon (+0x20) so a deployed unit's weapon changes mid-battle
    (the roster +0x14 alone is construction-welded)."""
    base, fp = find_roster_slot(h, name_id)
    if base is None:
        print(f"nameId {name_id} not found in roster (run `list`)."); return
    off = SLOTS.get(slot_name)
    if off is None:
        print(f"unknown slot '{slot_name}'. Use one of: {', '.join(SLOTS)}."); return
    is_primary = (off == R_RHAND)
    lvl, br, fa = fp
    main = u16(h, base + R_RHAND)
    if gun_id is None:
        gun_id = main
    if gun_id is None or gun_id in (0x00FF, 0xFFFF):
        print(f"refusing: main hand reads {fmt_item(gun_id)} (empty/unreadable) and no explicit "
              f"id was given. Equip a weapon, or pass a raw equipped-space id."); return

    orig = u16(h, base + off)
    if orig is None:
        print("  target slot read failed -- aborting before any write."); return
    cb = find_combat_struct(h, fp) if (with_dw or is_primary) else None
    orig_cweapon = u16(h, cb + C_WEAPON) if (is_primary and cb is not None) else None
    if is_primary and cb is None:
        print("  NOTE: live combat struct not located (unit not deployed?) -- writing the ROSTER "
              "primary only; a deployed unit won't change until rebuilt from the blueprint.")
    if is_primary and gun_id == main:
        print("  NOTE: the id to write == the current main hand; nothing will visibly change. Pass "
              "a DIFFERENT id (e.g. another unit's weapon id from `show`) to see a real swap.")
    orig_sup = u8(h, base + R_SUPPORT) if with_dw else None
    if with_dw and orig_sup is None:
        print("  support read failed -- aborting before any write."); return
    orig_csup = None
    if with_dw and cb is not None:
        _c = rd(h, cb + C_SUPPORT, 4)
        orig_csup = list(_c) if _c else None

    bo, mask = support_bit(DUAL_WIELD_ID)
    print(f"target nameId={name_id}  roster@{base:012X}  fp={lvl}/{br}/{fa}  main hand={fmt_item(main)}")
    print(f"plan: write id {gun_id} -> {slot_name}(+0x{off:02X}); orig {slot_name}={fmt_item(orig)}" +
          (f"; set Dual Wield ({DUAL_WIELD_ID}) roster +0x0A" + (" + live combat +0x98" if cb else "")
           if with_dw else "") + ".")
    asserts = 0
    t0 = time.time(); last_s = -1
    try:
        w16(h, base + off, gun_id)
        if is_primary and cb is not None:
            w16(h, cb + C_WEAPON, gun_id)
        if with_dw:
            wr(h, base + R_SUPPORT, bytes([DUAL_WIELD_ID]))
            if cb is not None:
                cur = rd(h, cb + C_SUPPORT + bo, 1)
                if cur is not None:
                    wr(h, cb + C_SUPPORT + bo, bytes([cur[0] | mask]))
        rb = u16(h, base + off)
        cwb = u16(h, cb + C_WEAPON) if (is_primary and cb is not None) else None
        print(f"  WROTE: {slot_name} readback={fmt_item(rb)}" +
              (f"  combatWeapon readback={fmt_item(cwb)}" if cwb is not None else ""))
        if rb != gun_id:
            print(f"  WARNING: {slot_name} did NOT read back as {gun_id} -- the engine rejects this "
                  f"id in this slot (field-level reject).")
        print(f"\n  HOLDING {seconds:.0f}s. Watch the unit's MODEL (2nd weapon?) and its ATTACK "
              f"(1 vs 2 shots). Ctrl+C restores.\n")
        while time.time() - t0 < seconds:
            if u16(h, base + off) != gun_id:
                w16(h, base + off, gun_id); asserts += 1
            if cb is not None and (u8(h, cb + C_LEVEL) != lvl or u8(h, cb + C_BRAVE) != br):
                cb = find_combat_struct(h, fp)   # struct relocated -> re-find
            if is_primary and cb is not None and u16(h, cb + C_WEAPON) != gun_id:
                w16(h, cb + C_WEAPON, gun_id); asserts += 1
            if with_dw:
                if u8(h, base + R_SUPPORT) != DUAL_WIELD_ID:
                    wr(h, base + R_SUPPORT, bytes([DUAL_WIELD_ID])); asserts += 1
                if cb is not None:
                    cur = rd(h, cb + C_SUPPORT + bo, 1)
                    if cur is not None and not (cur[0] & mask):
                        wr(h, cb + C_SUPPORT + bo, bytes([cur[0] | mask])); asserts += 1
            s = int(time.time() - t0)
            if s != last_s:
                last_s = s
                cw = u16(h, cb + C_WEAPON) if cb else None
                print(f"  t={s:>3}s  {slot_name}={fmt_item(u16(h, base + off))}  "
                      f"combatWeapon={fmt_item(cw)}  re-holds={asserts}")
            time.sleep(0.10)
    except KeyboardInterrupt:
        print("\n(interrupted)")
    finally:
        w16(h, base + off, orig)
        if orig_cweapon is not None and cb is not None:
            w16(h, cb + C_WEAPON, orig_cweapon)
        if with_dw and orig_sup is not None:
            wr(h, base + R_SUPPORT, bytes([orig_sup]))
        if orig_csup is not None and cb is not None:
            wr(h, cb + C_SUPPORT, bytes(orig_csup))
        print(f"  restored {slot_name} -> {fmt_item(orig)}" +
              (f", combat weapon -> {fmt_item(orig_cweapon)}" if orig_cweapon is not None else "") +
              (" + support" if with_dw else "") + ".")
    print(f"done. re-holds={asserts} (0 = held cleanly; high = an Organize/normalize source fights us).")


def cmd_restore(h):
    if not os.path.exists(SNAP_PATH):
        print(f"no snapshot at {SNAP_PATH} (already restored, or never ran a write)."); return
    with open(SNAP_PATH) as f:
        snap = json.load(f)
    restore_from(h, snap)
    os.remove(SNAP_PATH)


def main():
    if len(sys.argv) < 2:
        print(__doc__); return
    op = sys.argv[1]
    if op not in ("list", "show", "twin", "twin+dw", "poke", "poke+dw", "restore"):
        print(__doc__); return
    pid = find_pid(PROC)
    if not pid:
        print(f"{PROC} not running"); return
    perm = PV if op in ("list", "show") else PV_W
    h = k32.OpenProcess(perm, False, pid)
    if not h:
        print(f"OpenProcess failed err={C.get_last_error()}"); return
    try:
        if op == "list":
            cmd_list(h)
        elif op == "show":
            cmd_show(h, int(sys.argv[2]))
        elif op == "restore":
            cmd_restore(h)
        elif op in ("poke", "poke+dw"):
            name_id = int(sys.argv[2])
            slot_name = sys.argv[3].lower() if len(sys.argv) > 3 else "shield"
            gun = int(sys.argv[4]) if len(sys.argv) > 4 and sys.argv[4].isdigit() else None
            secs = float(sys.argv[5]) if len(sys.argv) > 5 else 180
            cmd_poke(h, name_id, slot_name, gun, with_dw=(op == "poke+dw"), seconds=secs)
        else:
            name_id = int(sys.argv[2])
            gun = int(sys.argv[3]) if len(sys.argv) > 3 and sys.argv[3].isdigit() else None
            secs = float(sys.argv[4]) if len(sys.argv) > 4 else 180
            cmd_twin(h, name_id, gun, with_dw=(op == "twin+dw"), seconds=secs)
    finally:
        k32.CloseHandle(h)


if __name__ == "__main__":
    main()
