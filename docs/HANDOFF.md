# Session Handoff — Living Weapon state of the world (2026-06-09, end of day)

Everything below is COMMITTED on `living-weapon` and deployed as a DEV build (LwDev=true: kill
thresholds {1,2,3}, all weapons seeded to P2, verbose `ev:` event log on). Git history carries the
day's eight-commit arc; this file carries only what a fresh session needs.

## What ships now (all live-verified by Patrick)

- **Zwill extra turn (v8)**: a credited kill slams the wielder's scheduler CT (band entry +0x25 ==
  combat base+0x41) until the bonus move's own turn-end pull-down confirms consumption
  (`ExtraTurn.cs`/`ExtraTurn.Policy.cs`). Locator = Band walk + twin filter (a real grid position
  beats the frozen (0,0) roster duplicate). SlamCt=100 (the proven value). Verified: bonus turn
  lands right after the kill-turn and survives long post-kill deliberation. No chaining.
- **Tracker reads the live auth band** (`Band.cs`; the static array FREEZES on battle restart and
  its inBattle u16 PULSES per unit — never gate on it). Capture-hybrid: the static array supplies
  enemy IDENTITIES (sane-fields capture, enemy-side slots); the band supplies all liveness
  (hp/corpse/position). Guards: 3-tick identity-bound alive/dead streaks, onField gating,
  seen-alive, credited-identity belt with revive eviction, `band coverage N/M` log.
- **BattleState machine** (`BattleState.cs`, pure + tested): instant enter, debounced exit (4s
  sustained out-of-live, suspended by pause/IsRealEvent). `battle: enter/exit` edges always logged.
- **First-kill credit**: corpse-time fallback latch (3 stable resolves, pause-gated, only while no
  latch) + acted-period latch freeze + 2-acted-fall pending expiry (30s backstop).
- **Cards**: every signature knife states its ability ("While this weapon is equipped at +3, ...",
  always visible); the counter is the LAST line of every card and reads "Kills: 42" (left-aligned
  digits painted into a fixed 4-char slot). The painted "Grant" line is REMOVED (unequipped cards
  — which the painter never touches — showed it as a floating bare "Grant"). The +N name suffix is
  the earned-state signal. Cutpurse no longer May-Casts Steal Gil.
- **Grant verification**: once-per-battle `GRANT ... readback=SET|MISS` log + a redundancy `note:`
  when the wielder picked the same support (the bits OR — stacking is structurally impossible).
  Per-ability in-game oracles documented in `DEV_TEST_RECIPES.md`.
- **Master sheet**: `docs/living_weapon_grid.csv` (the knives csv was merged in and deleted) — all
  121 weapons in the knives schema; knife rows = shipped truth; other rows carry their old DRAFT
  grant ideas inside sigNote for the future category rollout.

## Open threads (none blocking)

1. **SlamCt 105 queue-jump probe** — does writing >100 cut ahead of other ready units? Probe
   `%TEMP%\fft_probes\ct_probe.py hold combat 105 <mhp> <lvl>` next live session. Fallback lever if
   queue waits ever feel long: hold the killer's SPEED >=100 (GrowthEngine primitives, untested).
2. **Generalize the extra turn** — hardcoded ZwillId/AtTier in ExtraTurn.Policy.cs; future =
   a signature flag in items.json -> meta.json once a second extra-turn weapon exists.
3. **Signature rollout to the other 17 categories** — re-curate each grid row's sigNote drafts to
   the knife model (most weapons pure growth; iconic few get one P3 support/stat signature).
4. **On-crit mechanics (discussed, unprobed)** — crit chance is immutable but DETECTION looks
   feasible: scan for the "Critical!" banner string event-gated by BattleLog damage events, or the
   knockback+damage heuristic. Unlocks "on crit, do X" signatures (Staggering Crits = drain the
   victim's CT is the pilot candidate). Needs one probe session.
5. **Silencer (reaction suppression)** — clear an enemy's reaction bitfield (+0x94 family, the
   grant tech in reverse). One unknown: does a cleared bit actually suppress at trigger time?
   Probe: zero a Counter-bearer's bits, hit it. Sibling idea: Lodestone (movement bits).
6. **Status-bit mapping** — only Charm's bit (+0x49 mask 0x20) is mapped; a 20-minute probe
   (inflict each status, diff the bytes) unlocks status-on-X signatures.
7. **TODO product calls**: should Doom/DoT kills credit the caster (needs cause-tracking RE)?
   revert kills on battle loss/quit (needs a per-battle staging tally)?
8. **Known cosmetic seams**: unequipped weapons' cards show the baked "Kills: 0" (the painter only
   targets the viewed unit's equipped weapons — deliberate crash-history boundary); restarted
   battles keep the `turn: #N this battle` counter running (no reset edge fires — correct, minor).
9. **Test-file debt**: KillTrackerTests.cs (~800 lines) far exceeds the 200-line house rule that
   production files honor; split by concern when convenient.

## Dev harness facts

- Deploy = kill FFT_enhanced.exe, `.\BuildLinked.ps1`. Tables/nxd apply on game RESTART.
- Descriptions: `data/items.json` (p3Desc etc.) -> `python tools/patch_names.py` (sqlite +
  FF16Tools sqlite-to-nxd) -> committed `mod/FFTIVC/data/enhanced/nxd/item.en.nxd`. The "Kills: "
  literal + 4-char slot must stay in lockstep between patch_names.py and Display/ByteScan.
- Probes live in `%TEMP%\fft_probes\` (ct_probe.py + one-off dump scripts). RPM-based, crash-safe.
- Recipes (give-all-items, WP bump, grant verification oracles): `docs/DEV_TEST_RECIPES.md`.
- Before any RELEASE: build with `Publish.ps1` (prod thresholds {5,20,50}, no seeding, lean logs).
