# Bow Pass — Implementation Plan (drafted 2026-06-09, end of the four-mechanics session)

STATUS: ARCHIVED (the bow pass shipped; balance lives in data/items.json)

Design truth: `docs/living_weapon_grid.csv` rows 83–91 (the working-pass mirror sheet was
merged back and retired 2026-06-11, like the knives/crossbows sheets before it).
Data truth: `data/items.json` only; everything else regenerates. TDD per house rules; both
gates must be green at every step.

**Already DONE this session:** Stormarc "Chain Lightning" implemented + VERIFIED LIVE
(Ricochet.cs/.Policy.cs, 41 tests, meta fields `ricochetRadius`/`ricochetPct`). Mechanics
PROVEN for Maim (reaction suppress+restore) and Barrage (JobCommand injection + cast).
Knockback probed + PARKED (render desync — memory `position-write-desync`).

**THIS RUN (locked 2026-06-09 eve, Patrick):** Chain Lightning CONFIRMED done (214/214
tests green, both gates PASS, CSV row 86 says verified live — only the test-config revert
remains). Implement: Phase 0 item 1 (Stormarc revert) → Phase 2 Maim → Phase 5 Barrage
(curator decisions resolved in place below). **Gorgon Gaze is REMOVED** (curator call
2026-06-09 — Phase 4 dropped, Perseus ships pure growth; both CSVs already updated).
Phase 1 Skirmisher and Phase 3 (Aim-gate probe, needs a live session) stay future work.

---

## Phase 0 — pre-ship hygiene (BLOCKERS for any release)
1. **Revert the Stormarc test config** in items.json: `wp 99→5`, `range 6→7`,
   `ricochetRadius 8→3`. (Range 6 existed only to dodge the wp-99 dominance hit on Yoichi;
   at wp 5 range 7 is gate-clean — rerun `analyze.py` to confirm.)
2. **Prose-stale fixes** (identity/flavor contradicting shipped data): Tidecaller (ships
   Torrent 127, prose claims Silence — also DECIDE its identity), Frostarc (wp4/ev5/r4 +
   Blizzard proc), Huntress (WP8 + Arm Shot may-cast). Flavor/desc edits need the
   `patch_names.py` → nxd rebake step.
3. Opportunistic debt (only if touching the files anyway): split KillTrackerTests.cs;
   extract the EagleEye/CharmLock shared enemy-scan helper.

## Phase 1 — zero-unknown signatures (data + existing runtime, one BuildLinked)
- ~~**Yoichi "Fan-Splitter"**~~ — DISPLACED by Barrage (Phase 5) per the bows-csv judge
  merge; kept on file as the fallback signature if Barrage regresses live (support grant
  213 Concentration, p3Desc `Attacks cannot be evaded.`, zero code).
- **Skirmisher "Loosed and Gone"** — timed Speed (`stat Speed, statBonus 2, forTurns 3,
  atTier 3`). FIRST shipping HoldTimedStat user (path exists at GrowthEngine.Signatures
  HoldTimedStat; Galewind pivoted away before ship) — add policy tests if uncovered.
  Oracle: +2 Speed visible turns 1–3, reverts turn 4.
- Cards: bake both p3Desc lines (patch_names → committed item.en.nxd); `check_p3desc` gates.

## Phase 2 — "Maim" (Huntress) — new runtime, all primitives proven
- items.json: signature `{sigName "Maim", atTier 3, crippleTurns 3, p3Desc "Foes it strikes
  lose their reactions for 3 turns."}`. Base edit LOCKED YES (open decision 1): proc 214 Arm
  Shot → 213 Leg Shot (monotone ladder: T5 pins, T6 Perseus deletes). NOTE the id spaces:
  213 here is the Ability-en id (Leg Shot, formula-2 proc) — NOT the RSM id 213
  (Concentration); don't mix. Fix Huntress's stale identity prose in the same edit
  (claims plain WP9; ships WP8 + may-cast).
- Plumbing: `gen_living_weapon_meta.py` + WeaponMeta.cs emit/parse `crippleTurns`
  (mirror `charmLockTurns`).
- Runtime `Maim.cs` + `Maim.Policy.cs` (≤200 lines each):
  - Hit detection = Ricochet's pattern (HP-drop during the +3 wielder's acted period,
    enemy fingerprints). Consider extracting a shared victim-latch helper — **Venombolt's
    Plague runtime (crossbow debt) needs the identical latch**; build them on one helper.
  - On latch: save the victim's reaction field (combat `+0x94`, 4 bytes = band `+0x78`)
    ONCE, then hold zeros each tick (guarded writes).
  - Expiry: 3 of the victim's turns (CharmLock's per-target turn counting) → write the
    saved bytes back. Re-hit refreshes the window. Battle exit clears latches (struct
    rebuild also cleans — CharmLock precedent — but restore on natural expiry regardless).
- Tests: latch/refresh/expiry policy, save/restore byte math, never-latch-allies, pinned
  in-process buffers like RicochetTests.
- Oracle: dev +3 Huntress vs a Counter monster — quiet while maimed, Counter resumes after
  3 of its turns; `cripple_probe.py show` watches the bytes live.

## Phase 3 — Chain Lightning balance gate (Patrick leans Aim-only)
- **Action-id probe** (next live session): watchspan/diff the acting unit's structs while
  choosing Attack vs Aim+N. Strong lead: Ability-en 402–410 are the Jump+N/Aim+N tier rows —
  those ids are likely what the action field holds. Deliverable: a "current action ability
  id" offset (also unlocks on-Jump/on-Steal/on-crit signature families).
- If found: Ricochet events gate on actionId ∈ Aim set; retune pct upward (50→75?) since
  Aim's charge time is the cost. If the hunt dead-ends: once-per-acted-period + lower pct.

## Phase 4 — REMOVED (was "Gorgon Gaze")
Dropped 2026-06-09 by curator call. Perseus Bow ships pure growth — its base item (max WP,
max range, Holy, May disable) is the identity. Both CSVs updated; no items.json signature
was ever added, so there is nothing to revert.

## Phase 5 — "Barrage" (Yoichi Bow +3) — DECIDED: weapon-grant runtime (shape b)
Facts: JobCommand table fully mapped (memory `barrage-jobcommand-injection`); injection is
session-only (table rebuilds at boot — so the runtime MUST re-assert it each session, which
is the hold pattern we already live by); **learned-flag IS a wall** — battle menu needs the
unit's learned bit; learned bitfield = roster `+0x32 + jobIdx*3` (bytes 0–1 actions
MSB-first → slot 10 = byte1 `0x40`; FFTHandsFree UNIT_DATA_STRUCTURE.md), currentJobJp at
`+0x80`. `learned_probe.py` is written and ready in `%TEMP%\fft_probes\`.

**Curator decisions RESOLVED (2026-06-09 eve):**
1. **Hosting shape = (b), the +3 weapon grant.** Shape (a) job-wide data ship is rejected
   (a permanent blank-named learn-screen row for every player isn't a signature, it's a job
   mod); (c) park is rejected (Patrick asked for it). Design:
   - While any +3 Yoichi wielder exists in the roster: save the wielder's current job's
     25-byte JobCommand record ONCE, inject ability 358 into the first EMPTY action slot
     (byte = 358−256 = 102 + the slot's ExtAb extend bit, MSB-first per byte — port the
     PROVEN bit math from `barrage_probe.py` and pin it with tests; wrong-bit tell = ghost
     "Vengeance" entries), and SET the wielder's learned bit for that slot.
   - The learned bit is NEVER cleared (clearing = hostile save edit). Permanent teach is
     the flavor: the legendary bow teaches its master the volley. The bit is inert any
     session the record isn't injected, so the residue is harmless.
   - When the grant condition ends mid-session (unequip / wielder gone): restore the saved
     record bytes. Re-assert injection per tick while active (idempotent hold).
   - Job resolution: wielder's current job id from the roster struct → JobCommand rec index
     (rec index == xml Id; Ramza Mettle = recs 25/26/27, live endgame 27). Sane-bounds
     check before ANY write; on resolution failure log once + no-op. NEEDS LIVE VERIFY:
     generic-job rec mapping + the learned-bit offset (learned_probe.py is the oracle).
2. **The blank name: ACCEPTED.** Ships nameless (desc "UNUSED") — documented in sigNote +
   handoff; cheap player fix = co-install Serbagz's ability-name mod. Revisiting
   ability.en.nxd stays parked (read `MECHANICS.md` (Bloodpact bullet) before ever attempting).
3. **Known accepted quirk (document, don't fix):** while injected, OTHER units of the same
   job see the blank entry in their learn screen and could buy it (vanilla 1200 JP). It
   goes inert when the record isn't injected.

Build: items.json signature `{sigName "Barrage", atTier 3, grantCommandAbilityId 358,
p3Desc "Grants its master the Barrage command: a volley of arrows."}`; meta plumbing in
gen_living_weapon_meta.py + WeaponMeta.cs (mirror the ricochet fields); runtime
`Barrage.cs` + `Barrage.Policy.cs` (≤200 lines each, policy pure/testable: slot pick,
record byte math, learned-bit math, when-to-inject/restore; every write Mem-guarded).

## Phase 6 — ship loop
Per phase: `generate.py` → `analyze.py` → `gen_living_weapon_meta.py` → `dotnet test` →
BuildLinked dev verify → grid/bows-csv "Verified Live?" flip → imperative commit. HANDOFF
updated as states change. Release = `Publish.ps1` (prod thresholds, after Phase 0).

## Open curator decisions (one list)
1. ~~Huntress 214→213 Leg Shot swap~~ — LOCKED YES 2026-06-09 (Phase 2, this run).
2. Tidecaller identity (own the Torrent/Toad flavor vs re-flavor; "Breakwater" 212 on file).
3. Chain Lightning gate: Aim-only (preferred) vs once-per-turn; pct retune. Needs the
   action-id probe (live session) — unchanged by this run.
4. ~~Gorgon Gaze~~ — DROPPED 2026-06-09 (curator call; Phase 4 tombstone above).
5. ~~Barrage shape/name~~ — RESOLVED 2026-06-09: shape (b) weapon grant, blank name
   accepted (Phase 5 above).
6. Frostarc range 4→5 playtest lever (declined for now, no dominance either way).
