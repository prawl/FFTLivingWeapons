# Session Handoff â€” extra-turn VERIFIED LIVE + the day's arc COMMITTED (2026-06-09, final)

## STATUS: DONE AND COMMITTED
Patrick verified the Zwill extra-turn live: kill -> bonus turn right after the kill-turn, INCLUDING a
15-second post-kill deliberation (the no-signal window held). The WP=99 harness is reverted (proposed.wp
back to 12), tables/meta regenerated, gates green (analyze PASS, 151/151), and the whole arc is committed
on `living-weapon` (code: "Read battle state from the live auth band and grant the Zwill an extra turn on
kill"). Remaining loose ends for future sessions: SlamCt=105 queue-jump probe (currently the proven 100),
the speed-lever alternative if queue waits ever feel long, generalizing the hardcoded id/tier into a
meta.json flag for more extra-turn weapons, and the TODO's open product calls (Doom kills counting,
kill-revert-on-loss, the Silencer reaction-suppression probe).

## (Context) extra-turn v8 design
The v7 locator ambiguity is FIXED: `ExtraTurn.Locate` now walks the Band (`Band.Entry/IsValid`, entry
frame: weapon +0x04, brave/faith +0x0E/+0x10, CT +0x25 == combat+0x41, HP +0x14) with the **twin filter**
-- a real-position match beats a (0,0) one (the frozen roster duplicate sits at (0,0); live-proven
14184F8AC at (7,4) vs twin 1418500AC). The Â±1MB BandScan fallback was removed (the walk re-finds within
a tick). `SlamCt` set to the live-proven **100** (105 queue-jump unproven; probe before raising -- the
no-signal window makes 100 safe). State machine (pull-down counting) unchanged, 151/151.
LIVE VERIFY: kill with the Zwill -> expect `arm` -> `classified -> Owed (two pull-downs owed)` ->
`pull-down #1` -> `pull-down #2` -> `release (Consumed)`, bonus turn on screen right after the
kill-turn; late credits classify Pinning/one pull-down; a >12s post-kill deliberation must NOT expire
the grant; no ghost turn after a NoSignal release (CT restore). THEN: revert Zwill WP 99->12 in
data/items.json, regen, commit. Speed-lever alternative (hold Speed>=100, GrowthEngine primitives)
stays the fallback if queue waits prove long -- see the parked section below.

## LATEST: auth-band migration v2 + the inb-pulse fix (DEPLOYED, 151/151)
Patrick's battle-RESTART test exposed that the static array (0x140893C00) FREEZES on restart (enemies
inb==0, full HP, no updates) while the auth band (0x14184xxxx, entry = slotBase+0x1C, static field
layout) stays live (fresh corpse + true positions only there; roster units have a frozen TWIN at
position (0,0)). **Migration (adversarially reviewed, capture-hybrid):** KillTracker corpse scan,
ActorResolver, TurnTracker, and GrowthEngine.Signatures.ReadHp now read the BAND (`Band.cs`,
`BandReadBase = CombatAnchor+0x1Câˆ’24*0x200`, 49 slots, validity = sane bounds incl. brave/faith â‰¤100);
the static array remains the ENEMY-IDENTITY/team oracle (capture (lvl,br,fa,mhp) from enemy-side slots
each onField tick â€” **by sane fields, NOT inb: live RE shows the array's "inBattle" u16 PULSES 0/1 per
unit mid-battle** (half the live enemies read 0; gating on it refused their kills as "not a captured
enemy" â€” Patrick hit exactly this). Guards: 3-tick alive/dead streaks (identity-bound, onField-gated),
seen-alive-this-battle, credited-identity belt with slot-scoped revive eviction, coverage invariant log
(`band coverage N/M`). KillTracker split (KillTracker.cs + KillTracker.Corpses.cs). Implementation by
delegated agent, independently verified, one drift fixed (Observe onField gate). LIVE VERIFY PENDING:
normal kills, restart+kill (the fixed case), `band coverage N/N` clean, no inflation.

## (Previous) tracker hardening v3
Patrick's live test of v7 found: first kill of every battle missed, double-turn dead. Evidence (ms-stamped
log) pinned three causes, all now fixed in the deployed **tracker hardening v3** (adversarially reviewed;
my first draft was rejected with 3 blockers â€” battleMode is a CURSOR-TILE-CLASS encoder, Paused reads 0,
mid-battle dialogue reads 0 + real eventId, Moved false-flips in pause subscreens):
1. **First-kill miss**: the acted edge lags the corpse by ~7.5s (measured); the 3s pending TTL expired
   first. FIX: corpse-time fallback latch (no acted gate, 3 stable identical resolves, pause-gated, only
   while no latch exists) + pending expiry by TWO debounced acted-falling edges (30s backstop, TTL=900).
2. **Polling death / no resets across battles**: slot9 dropped mid-battle and stuck across the world map.
   FIX: new pure `BattleState` machine (`LivingWeapon/BattleState.cs`, 24 tests) â€” instant enter
   (sentinels OR battleMode 2/4 OR 3+slot0), DEBOUNCED exit (4s sustained `!CharmLock.InLiveBattle`,
   timer suspended while PauseFlag==1 or IsRealEvent(eventId@0x14077CA94; aliases as nameId in combat)).
   Engine is a thin adapter; `battle: enter/exit` edges always logged; CharmLock heartbeat decoupled from
   nowIn (fed on InLiveBattle alone).
3. **Kill inflation guard**: ResetBattle â†’ baseline poll marks pre-existing corpses settled (false/dup
   resets and quick-loads can never inflate the tally).
142/142 tests, gate PASS, deployed via BuildLinked. Implementation was done by a delegated agent to spec;
verified independently. NOT yet live-verified by Patrick. Live-verify: fresh battle, kill on the FIRST
action â†’ KILL line ~1s; `battle:` edges between chained battles; turn counter resets; pause/dialogue >5s
mid-battle produce NO exit edge.

## Extra-turn: PARKED, fully diagnosed + a new design lever (Patrick's idea)
- v7 died of LOCATOR AMBIGUITY: every arm â†’ `ambiguous locate (2 matches)` â€” two stride-aligned copies of
  the wielder in the combat band; refuse-on-tie disabled the feature. The ambiguity log now prints the
  candidate ADDRESSES once per grant. Fix path: reuse GrowthEngine.LocateStruct's proven selector (first
  stride match + PA/MA 1..199 sanity + per-battle cache â€” its stat writes provably land on the live copy).
- **Speed-lever alternative (Patrick, 2026-06-09)**: instead of slamming CT, hold the killer's SPEED
  (combat +0x40) at â‰¥100 for the grant window â€” CT refills +Speed per clock tick, so the gauge fills in
  ONE tick after the kill-turn ends. Every primitive is already shipping (GrowthEngine locates the struct,
  writes Speed today for Swiftfang growth, and HoldTimedStat has capture-naturalâ†’restore). Same queue-
  priority unknown as CT=100 (does "ready" cut the line?), same consume-then-restore state machine needed
  (else infinite turn chain), but no scheduler-fighting and no new locator. Probe both next session:
  hold speed 255 vs hold CT 105, watch the queue.
- SlamCt=105 still unverified; WP=99 harness still in (revert before release).

---

# (Previous handoff) Zwill extra-turn v7 (CT pull-down counting) built + deployed; awaiting live verify (2026-06-09)

## TL;DR
The v6 extra-turn bugs are diagnosed and the feature was REDESIGNED, not patched: the v6 `killerActive`
test (and the old handoff's proposed "fresh read" fix) sat on a poisoned oracle â€” **the condensed
active-unit struct at `0x14077D2A0` shows the unit under the CURSOR, not the active unit** (FFTHandsFree
BATTLE_COORDINATES.md). v7 (`LivingWeapon/ExtraTurn.cs` + `ExtraTurn.Policy.cs`) drives the grant entirely
off the killer's OWN scheduler CT and never touches the condensed struct. KillTracker was hardened in the
same pass (latch freeze, TTL time base, dev event timeline, log rotation). **112/112 tests, gate PASS,
deployed via BuildLinked. NOT yet live-verified, NOTHING committed, WP=99 harness still in.**

## What the evidence showed (don't re-derive)
From the 12:44â€“12:55 live log of the v6 build:
- **Bug A confirmed**: all 13 grants stuck `Owed: active=True` â†’ `window expired`. Never reached Pinning.
- **Bug B dissolved**: 12/13 kill credits landed with killer CT=100 â†’ credit lands DURING the kill-turn;
  KillTracker was never ~15s late. The "delayed bonus" was Bug A parking CT at 100 â†’ window expiry â†’ the
  killer's natural queue slot arriving later. (1/13 credited post-turn at ct=5 â€” late credits exist, rare.)
- The located auth copy `@14184F8AC` = `CombatAnchor+0x1C` (CT = combat base+0x41 âœ“); Ramza's struct never
  actually relocated; the one 2s locate gap self-healed at the same address.

## v7 design (adversarially reviewed; review findings folded in)
- **Pull-down counting**: the engine pulls CT below ~70 in exactly one case â€” a turn of that unit ENDED.
  We read before each re-slam, so pull-downs are countable. Arm-time CT â‰¥100 (2 agreeing reads) â‡’ kill-turn
  running â‡’ expect 2 pull-downs (kill-turn end, bonus end); <100 â‡’ expect 1. Release on the last = consumed.
- **Locate by wielder**: killer = the roster slot holding the Zwill (`RRHand/RLHand/ROffHand == 10`); found
  per tick by stride walk `CombatAnchor Â± 24*0x200` matching `CWeapon(+0x20)` âˆˆ wielder's hand ids AND
  brave/faith (+0x2A/+0x2C); exact-Zwill match outranks; ambiguity â‡’ no slam; throttled band-scan fallback.
- **Hardening**: window = NO-SIGNAL timeout (12s, refreshed while CTâ‰¥100 â€” a player deliberating through a
  long kill-turn can't expire the grant) + 90s absolute cap; **CT=0 restore on every non-consumed release**
  (kills the parked-100 ghost grant = the old Bug B mechanism); took needs a 3-read streak (refractory â€”
  turn-end oscillation can't double-count); killer HP==0 (3 reads) â‡’ release; Arming does NOT slam
  (classification reads stay unpolluted). States: `Idle â†’ Arming â†’ Owed â†’ Pinning â†’ Idle`.
- **Patrick's decisions**: extra-turn REPLACES Dual Wield (items.json id 10 now has a label-only signature
  `displayLabel: "Extra Turn"` â€” paints the card at P3, arms no support bit; 221 was build-time-only and
  never worked live anyway); NO chaining (kill mid-grant just logs); verbose events DEV-only.
- **`SlamCt = 105` is wired but UNVERIFIED** â€” only 100 is live-proven. First thing in the verify session:
  `%TEMP%\fft_probes\ct_probe.py hold combat 105 <mhp> <lvl>` â€” does the write stick, and does 105 schedule
  ahead of a unit parked at 100? If not: set `SlamCt = 100` in ExtraTurn.Policy.cs (the no-signal window
  makes the wait safe; Pinning simply outlasts queue traffic).

## KillTracker / tracker hardening (same deploy)
- **Latch freeze**: `_lastPlayerWeapons` latches ONCE per acted-period (first successful resolve after the
  acted rising edge), frozen until acted reads 0 for 3 ticks (byte-drift debounce). Fixes the post-act
  ally-hover re-latch (the condensed struct follows the cursor even at acted==1).
- **PendingTtl 30 â†’ 90**: the constant assumed the 100ms poll (~3s); at the 33ms tick it expired at ~1s.
- **BattleLog.cs** (dev builds): per-tick damage/heal/move event lines tagged with the latched weapons.
  TurnTracker now logs each acted edge. `Log` rotates per launch (`livingweapon.prev.log`) and stamps
  milliseconds â€” on-screen action vs captured event is now comparable at tick granularity.
- Known accepted limitation (pre-existing, both here and in FFTHandsFree): during an ENEMY action the AI
  cursor sits on its player target, so the victim can latch; DoT enemy deaths credit the stale last actor.

## Live verify (the only open step before commit)
1. Probe the 105 question (above); adjust `SlamCt` if needed, redeploy.
2. Zwill kills at normal speed, including one where you deliberate >12s after the kill before ending the
   turn. Expect in the log: `arm` â†’ `classified -> Owed (kill-turn running, two pull-down(s) owed)` â†’
   `pull-down #1` â†’ `pull-down #2` â†’ `release (Consumed)`, and the bonus turn on screen right after the
   kill-turn ends. Late-credit kills classify Pinning and need one pull-down. Check no ghost turn after
   any `release (NoSignal)` â€” the CT restore should prevent it.
3. The dev `ev:` lines + `turn:` lines + ms timestamps are the latency oracle for the tracker hardening.

## After verify (in order)
1. Revert **Zwill WP 99 â†’ 12** in `data/items.json` (proposed.wp), regenerate, gates green.
2. Commit (imperative mood, no attribution â€” pre-commit hook enforces).
3. Follow-up (not this round): generalize the hardcoded id/tier via meta.json (e.g. `extraTurn: true` on
   the signature), fold ExtraTurn's wielder resolve into it for multi-weapon support.

## Git / build state
- Branch `living-weapon` (HEAD `eac0423`), everything UNCOMMITTED. Untracked: `ExtraTurn.cs`,
  `ExtraTurn.Policy.cs`, `BattleLog.cs`, `ExtraTurnTests.cs`, `BattleLogTests.cs`, `DEV_TEST_RECIPES.md`,
  this file. Modified: `Engine.cs`, `KillTracker.cs`, `TurnTracker.cs`, `Log.cs`, `Tuning.cs`, `Offsets.cs`
  (+`CHp`), `SignatureTests.cs`, `KillTrackerTests.cs`, `data/items.json` (WP hack + signature swap),
  `meta.json`, tables, `docs/TODO.md`, `docs/living_weapon_knives.csv`.
- 112/112 tests, analyze gate PASS, deployed (DEV: LwDev=true â€” thresholds {1,2,3}, all weapons seeded P2,
  verbose events ON).
