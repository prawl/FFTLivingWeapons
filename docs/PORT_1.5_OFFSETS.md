# FFT:IC 1.5 -- Offset Re-anchor Ledger

> Live re-find of `LivingWeapon/Offsets.cs` against the recompiled 1.5 exe. Companion to
> `PORT_1.5.md` (the strategy). All addresses here were found by EXTERNAL read-only RPM probes
> against the running 1.5 game (pid varies). **Live-found, not yet wired into `Offsets.cs` or
> behaviorally re-confirmed in-DLL** unless a row says CONFIRMED. Started 2026-06-16.

## Build identity (the 1.5 target)
- `fft_enhanced.exe` SHA256 `3625FD9B6ADABBBC07F4AF6C1807D48F0122ECA5301BFC138F2A3ECE2E6F2D4C`
- Size 365,841,152 bytes (was 359,214,920); Steam build `23353019` (was `20688883`)
- PE fingerprint (from the TreasureMaster auto-disarm log line): TimeDateStamp `0x6A0F86A9`,
  SizeOfImage `0x190EB000` (pre-1.5 was `0x690C1269` / `0x156C8000`)
- Pre-1.5 rollback exe: `C:\Users\ptyRa\FFT_IC_backup_pre1.5\FFT_enhanced.exe` (SHA `6DEDDA92...EB9C28`)

## What the recompile did
**Essentially every absolute address moved.** Confirmed broken at old addresses: sentinels
(slot0/slot9/battleMode), RosterBase, CombatAnchor, InventoryCountBase, JobCommand. What still
works (NOT address-pinned): the data half (modloader tables/nxd/tex) and the dynamic display
heap-sweep (string-scan, no fixed anchor). TreasureMaster correctly auto-disarmed on the build-key
mismatch.

### The shift is a GRADIENT, not a constant
The displacement grows with address (the image expanded, so later regions slide further). Do NOT
assume one global delta -- but WITHIN a tight address neighborhood the delta is stable enough to
predict-then-verify:

| region | measured delta |
|---|---|
| `0x14077xxxx` (turn-queue) | **+0x6000** |
| `0x14090xxxx` (battleMode) | **+0x6350** |
| `0x1411Axxxx` (roster / inventory) | **+0x6440** |
| `0x14185xxxx` (combat band) | **+0x6450** |

## REBUILD #1 -- LIVE-CONFIRMED 2026-06-16 (the 5 anchors + predictions wired into Offsets.cs)
Booted the re-anchored DEV DLL on 1.5. Log + probe confirm the FOUNDATION revived:
- `battle: started (slot0=10 slot9=FFFFFFFF mode=2)` -- battle detection works (battleMode=2; predicted
  slot9 reads 0xFFFFFFFF). NOTE: slot0 reads 0x10 (not 0xFF) -- the existence marker differs in 1.5, but
  battleMode carries the gate so it does not matter for detection.
- `growth: found combat struct for party slot 0` + `font: MP field layout verified across 13 units`
  -- band locate + **the RELATIVE band-entry offsets SURVIVED** the recompile (validated across 13 units).
- **Growth HOLDING**: Ramza PA 18 (natural) -> **23** (grown, +3 dev tier) at 0x141855CE0+0x3E; twin at
  +0x800 correctly left at 18. SpiritualFont signature ACTIVE.
- Test fix: `ScholarRingTests` write-address tripwire literal bumped to the new InventoryCountBase.
- STILL OPEN: kill attribution (larceny log shows `actorFp=(0,0,0)` -> the predicted `Acted` may be
  wrong; verify by a kill ticking the counter), JobCommand sigs (ShadowBlade/Barrage -- base not re-found),
  ArrayBase (left at old -> enemy-side oracle off), display card paint (MirrorWeapon predicted, unverified).

## CONFIRMED anchors (live-validated by fingerprint/behavior; REBUILD #1 promoted these to live-confirmed)
Fingerprint used: Ramza -- LVL 99, BR 97, F 75, HP 486/486, weapon 80 (Venombolt), roster slot 0, nameId 1.

| Offsets.cs const | old | NEW (1.5) | delta | evidence |
|---|---|---|---|---|
| `BattleMode` | 0x140900650 | **0x1409069a0** | +0x6350 | u8, reads 3 in battle / 0 on map; tracked across 3 transitions; lone candidate near old addr |
| `TurnQueue` | 0x14077D2A0 | **0x1407832A0** | +0x6000 | condensed entry: team=0, nameId=1 (Ramza), hp=486/486 |
| `RosterBase` | 0x1411A18D0 | **0x1411A7D10** | +0x6440 | slot0=Ramza (lvl99, rhand=80, nameId1); slots +1..+7 = real party; -1/-2 empty |
| `InventoryCountBase` | 0x1411A17C0 | **0x1411A7C00** | +0x6440 | dev "give-all" inventory (ids 1-35 = 99) at predicted addr |
| `CombatAnchor` | 0x14184F890 | **0x141855CE0** | +0x6450 | Ramza combat struct: weapon80/lvl99/br97/f75/hp486/pa18/ma8/spd15; twin at +0x800 |
| `ArrayBase` | 0x140893C00 | **0x140899F50** | +0x6350 | static unit array; captures 11 enemies (slots 4-14), excludes Ramza (slot 20). REBUILD #2 live-confirmed: kill credited (Chaos Blade #59) |

## PREDICTED -- needs verify
| const | predicted | basis | how to confirm |
|---|---|---|---|
| `Slot0` | 0x140782A30 | +0x6000 | static read = 0x10 (NOT 0xff). Needs behavioral watch; existence-array marker may differ in 1.5 |
| `Slot9` | 0x140782A54 | +0x6000 | read = 0xffffffff (terminator plausible) |
| `Acted` | 0x140782A8C | +0x6000 | watch it flip 0->1 when a unit acts |
| `EventId` | 0x140782A94 | +0x6000 | read = 401 (unverified) |
| `MirrorWeapon` | 0x141876CA4 | +0x6450 | read 0 (no card open); verify with a status card up showing weapon 80 |

## Display card re-find -- IN PROGRESS (2026-06-17)
The in-battle Kills card does not repaint. Gate is `StatusCardOpen = inBattle && battleMode==3 &&
paused && submenuOpen` (BattleState.cs) -- `MenuCursor` is NOT used (code comment: "don't gate on it").
Live card open/close differential (intersected over 2 full cycles):
- `battleMode==3` when card open -- CONFIRMED (0x1409069A0 reads 3).
- `SubmenuFlag` -> **0x140D4085E** (was 0x140D3A10C, +0x6752) -- found, clean (1 open / 0 closed, isolated).
- `PauseFlag` -> **0x140C80199** CANDIDATE (was 0x140C64A5C) -- by-elimination only; NOT confidence-locked.
  Wire + live-verify (does the card repaint?) before trusting. Single open/close diff is swamped by
  live-battle noise; needs multi-cycle intersection or a quieter capture.
- `MirrorWeapon` (which weapon's count to show): +0x6450 prediction 0x141876CA4 read 0 -- WRONG. Re-find
  via a card-open fingerprint (a u16 holding the viewed unit's weapon id) -- the 0x14187 region was OUTSIDE
  the captured snapshot range, so it needs its own capture. ShouldPaintCard out-of-battle branch uses
  OnField (works), so the equip-screen card may already repaint once MirrorWeapon is correct.
NOTE: shifts are NON-MONOTONIC (SubmenuFlag +0x6752 > the higher-address +0x644x regions) -- do not
predict display addrs by interpolation; differential each.

## NOT yet found (next session)
- `Acted`/existence-array exact marker (behavioral watch). NOTE: kill attribution works anyway (the
  damage-event resolves the acting weapon, e.g. `[w:37]`); `actorFp=(0,0,0)` only blocks Larceny's
  own actor check. Confirm whether anything beyond Larceny needs `Acted` re-found.
- `JobCommand AbilityBase` ~0x140679436 (Barrage/ShadowBlade inject -- the dangerous WRITE; needs the
  25-byte record signature; region delta likely < +0x6000). Local const in `Barrage.cs`, reused by
  `ShadowBlade.cs`.
- Display mirrors `MirrorOffHand`/`WpScratch`, `PauseFlag`/`SubmenuFlag`/`MenuCursor`
- Status/CT/Dead-bit RELATIVE offsets (band `+0xNN`) -- expected to SURVIVE (no struct change in 1.5);
  spot-check after the foundation rebuild.

## Method (reusable)
- All finds via external read-only RPM (no game crash risk). Snapshots/diffs in `%TEMP%` (throwaway).
- Flags (battleMode): two-state DIFFERENTIAL (in-battle vs world-map) + intersection across transitions.
- Structs (roster/band/turn-queue): FINGERPRINT scan on Ramza's stats across the module image.
- Same-region siblings: predict by the region delta, then verify by read/behavior.
- Sticky values (slot0/slot9) resist differential -- use structural/behavioral methods.
