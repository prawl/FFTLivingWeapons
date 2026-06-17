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
| ~~`MirrorWeapon`~~ | ~~0x141876CA4~~ | -- | SUPERSEDED -- found live at 0x141876EB4 (+0x6660); see the Display section below |

## Display card re-find -- ALL 5 ADDRS FOUND + WIRED + DEPLOYED 2026-06-17 (live card-repaint verify pending)
The in-battle Kills card did not repaint. Gate is `StatusCardOpen = inBattle && battleMode==3 &&
paused && submenuOpen` (BattleState.cs) -- `MenuCursor` is NOT used (code comment: "don't gate on it").
All five build-pinned display addrs re-anchored via `tools/probes/display_probe.py` (read-only):

| Offsets.cs const | NEW (1.5) | delta | method / evidence |
|---|---|---|---|
| `BattleMode` | 0x1409069A0 | +0x6350 | (already wired) reads 3 when card open |
| `SubmenuFlag` | **0x140D4085E** | +0x6752 | 3-state solve (live/menu/card): ==1 in card ONLY (0 on enemy turn AND plain command menu). Reconfirmed across 3 captures + last session |
| `PauseFlag` | **0x140C6B1C8** | +0x676C | consistency-sample (10Hz constant-1-paused / constant-0-running) then watch: flipped 0->1->0 on a live card open/close. Two synced copies (0x140C6B1C8 / 0x140C6B307), using the lower |
| `MirrorWeapon` | **0x141876EB4** | +0x6660 | two-card differential: read 80 on Ramza's card, 56 on the Umbral Rod card -- the ONLY u16 tracking both |
| `MirrorOffHand` | **0x141876EB6** | +0x6660 | MirrorWeapon +2 (read 143 = Ramza's shield) |
| `WpScratch` | **0x141876E96** | +0x6660 | MirrorWeapon -0x1E; read 6 = Venombolt's WP with Ramza's card up |

KEY LESSONS this round:
- A 3-frame open/closed diff for `PauseFlag` is USELESS -- the action menu already pauses (pause holds
  1 across the whole player turn, even cursor-on-map mode 1), and animated UI bytes swamp the diff. The
  win was a HIGH-FREQUENCY CONSISTENCY SAMPLE: bytes that stayed constant-1 the entire time a menu was
  held vs constant-0 the entire enemy turn. That cut ~80 noisy candidates to 2.
- `battleMode==3` is NOT "menu open" -- it reads 3 for the whole player turn (idle + menu + card) and
  even flickers to 3 transiently during enemy turns. `pause` + `submenu` are the real discriminators.
- Shifts are NON-MONOTONIC: SubmenuFlag +0x6752, the 0x14187 mirror region +0x6660, both ABOVE the
  higher-address roster/band +0x644x. Differential each; never interpolate.

## JobCommand table base -- RE-FOUND + LIVE-VERIFIED 2026-06-17
The dangerous one. `Barrage.AbilityBase` (rec 0's AbilityId1; ShadowBlade reuses it verbatim).
LIVE-VERIFIED 2026-06-17 (Patrick at the controls): Barrage renders + casts from a +3 Yoichi Bow
Thief's command menu, and Shadow Blade from a Sanguine Sword Squire/Knight. Committed.

| const | pre-1.5 | NEW (1.5) | delta | method / evidence |
|---|---|---|---|---|
| `Barrage.AbilityBase` | 0x140679193 | **0x14067E213** | +0x5080 | signature scan: rec 8 Aim (bytes 150-157) + rec 9 Martial Arts (bytes 100-107) exactly 25 bytes apart -- UNIQUE hit; whole table then read coherently (Steal rec 14 = 108-115, Black Magicks rec 11, Iaido rec 19, Machinist rec 37 = 213-215, black-magic subset rec 163). |

Tool: `tools/probes/jobcommand_find_probe.py` (READ-ONLY; `find` = locate+verify, `dump <base> <lo> <hi>`).
Delta +0x5080 is NON-MONOTONIC vs the 0x14077 region's +0x6000 (lower address slid less) -- found by
signature, NOT interpolated. **STALE-BUT-VALID confirmed:** the pre-1.5 base 0x140679193 is still
*mapped* on 1.5 and reads as unrelated structured data (other ability records), so the not-yet-redeployed
dev DLL would *corrupt* that region the instant a +3 Yoichi Bow Thief / Sanguine Sword Squire-Knight is
equipped. Wired into `Barrage.cs` + `barrage_probe.py`; pinned by
`BarrageTests.AbilityBase_is_pinned_to_the_verified_1_5_table_base`. Gates green (dotnet 1249, analyze 7/7).

## Treasure Master -- REVIVED on 1.5 + LIVE-VERIFIED 2026-06-17 (rebase, not re-capture)
TM auto-disarmed on 1.5 (build-key mismatch). Revived WITHOUT re-hovering all ~284 tiles, via a
per-region address REBASE of the captured flag data, plus two runtime anchors.

**Runtime anchors (wired into `Offsets.cs`):**
| const | new (1.5) | delta | method |
|---|---|---|---|
| `LiveBattleMapId` | 0x140784478 | +0x6C3C | two-map differential (reads 76 Zeklaus / 80 Araguay; unique) |
| `TerrainGrid` | 0x140C6B440 | +0x6440 | wide scan for the 1456-byte block whose v2 hash == map 80's STORED fp -> exact start; **proves terrain DATA unchanged on 1.5** so all stored fpHashes stay valid |

**Capture-tool anchors (re-found live; `capture_treasure.py` + `treasure_flags.py`):**
`CURSOR_X 0x140C6AFB8`, `CURSOR_Y 0x140C6ADAC` (diff3 + watchall), `MAP_ID_ADDR 0x140784478`,
`TERRAIN_FP_ADDR 0x140C6B440`. Added `TM_MAP_OVERRIDE` env to capture a known map before MAP_ID was found.

**The rebase (tools/treasure_rebase.py):** the recompile shifted each ~1MB flag-data region by a uniform
delta -- regions 0x140d/0x140e/0x140f/0x141000 = +0x61D0, region 0x141100 = +0x6450. Measured from 2
freshly re-captured ground-truth maps (74 Siedge Weald, 76 Zeklaus) and applied to the other 69. Verified
by a 5-lens adversarial workflow (independent re-derivation, a 100% holdout where map 74's deltas predict
map 76's real addresses, candidate integrity, logic review, bake compat -- all PASS) and then LIVE: marks
paint on Araguay Woods (a REBASED map we never captured). 1518 addresses rebased; 9 sparse addrs (regions
0x140c/0x141500, maps 16/47 only) dropped via --drop-uncovered. Maps 74/76 set map-id-only (their capture
read the stale terrain and stamped a bogus fp). 71 maps ship / 15 stub (pre-existing never-captured gap).

**Open TM follow-ups:** maps 16 (Limberry Keep) + 47 (Sal Ghidos) sit at the 3-addr floor (sparse regions
dropped) -- a direct re-capture restores their margin + the 9 dropped addrs. The 15 stub maps were never
captured. A v3-fingerprinted rebased map's arming is implied by the terrain-unchanged proof but only a v2
map (Araguay) was watched -- spot-check a v3 map opportunistically.

## NOT yet found (next session)
- `Acted`/existence-array exact marker (behavioral watch). NOTE: kill attribution works anyway (the
  damage-event resolves the acting weapon, e.g. `[w:37]`); `actorFp=(0,0,0)` only blocks Larceny's
  own actor check. Confirm whether anything beyond Larceny needs `Acted` re-found.
- Display: DONE -- `MirrorWeapon`/`MirrorOffHand`/`WpScratch`/`PauseFlag`/`SubmenuFlag` all found + wired
  (see the Display section below). `MenuCursor` left at pre-1.5 -- StatusCardOpen does not use it (unused).
- Status/CT/Dead-bit RELATIVE offsets (band `+0xNN`) -- expected to SURVIVE (no struct change in 1.5);
  spot-check after the foundation rebuild.

## Method (reusable)
- All finds via external read-only RPM (no game crash risk). Snapshots/diffs in `%TEMP%` (throwaway).
- Flags (battleMode): two-state DIFFERENTIAL (in-battle vs world-map) + intersection across transitions.
- Structs (roster/band/turn-queue): FINGERPRINT scan on Ramza's stats across the module image.
- Same-region siblings: predict by the region delta, then verify by read/behavior.
- Sticky values (slot0/slot9) resist differential -- use structural/behavioral methods.
