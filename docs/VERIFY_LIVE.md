# Verify Live: committed but not yet watched in-game

STATUS: CONTRACT (the live verification script; owner-only checkbox flips)

These changes pass both gates (analyze.py + LivingWeapon.Tests), but the gates prove logic, not
engine behavior. Each row needs an in-game confirmation before it counts as proven. Deploy with
`.\BuildLinked.ps1`, watch it fire, then check the box (and flip the relevant LIVE_LEDGER row if
it has one; only Patrick flips PROVEN). If a verification FAILS, do not silently revert: capture
what you saw and reopen here.

## Carried rows (pre-2026-07-05)

| # | Commit | Change | How to verify live | Done? |
|---|--------|--------|--------------------|-------|
| 1 | e98c2f2 | **Choir multi-bearer** (holder-only since the aura dial-back; this row's original "projects a duet" wording predates it): every deployed +3 Warlock's Staff main-hand bearer casts instantly, no aura. | VERIFIED 2026-07-15 (owner live, dev lane, smoke row 7.17): two units with +3 Warlock's Staff (id 60) in MAIN hand, same battle, both seated on the 13:05 tape census, both cast instantly on screen. Benched-copy noise: structurally covered (benched units hold no band seat); passively re-checked in the remaining smoke battles. | [x] |
| 2 | b861806 | **Lethal-actor stamp** (superseded by the KillerStamp death-edge stamp, f4bf5df). | Superseded: f4bf5df's verification below covers this path and stricter cases. | [x] |
| 3 | 279e7b8 | **Warbrand spriteIdOverride**. | DEAD: the override never took effect; removal is in the release scope (docs/RELEASE_SCOPE.md doc+hygiene item). No verification possible. | [x] |

## 2026-07-05 batch, already verified live

| # | Commit | Change | Verified | Done? |
|---|--------|--------|----------|-------|
| 4 | f4bf5df | **Death-edge culprit stamp** (stale-latch attribution fix). | Four correct stamp-overrides on tape same day: three in the Lionel Gate re-run (manual alternation) + one under auto-battle that corrected the Queklain kill to Ramza's Windrunner, matching eyewitness. Archives flight_20260705_111650 + _113131. | [x] |
| 5 | a3106d0 | **Deploy preservation round-trip** (save files + flight/). | Sentinels + all 20 archives survived 2 consecutive deploys and 1 lock-induced failed deploy (catch-path restore observed). | [x] |

## 2026-07-05 batch, verified live 2026-07-07 (owner)

| # | Commit | Change | Verified | Done? |
|---|--------|--------|----------|-------|
| 10 | 58d5c7b | **Desc budget trims** (DATA: takes effect on game RESTART). | Sanguine Sword, Wrathblade, Stormarc all fit the box with the Kills line visible; Rod of Faith and Swiftedge still fit. | [x] |
| 11 | dd37068 | **Log facelift** (tagless subject-first console, armed gating, launch header, battle summary). | The row-11 protocol below passed live. | [x] |
| 12 | pending build | **Boco fix** (unarmed stale latch eating an armed player's kill). | Unarmed unit acts, then an armed player kills with an item/throw: the kill credits the armed weapon, not the "no Living Weapon" burial. | [x] |

## 2026-07-05 batch, DEFERRED to a later patch

Reliquary Phase 1's live pass (Marks + card story + undead/legends) is pushed past 2.3.0. These
rows ride backlog LW-6 ("Phase 1 SHIPPED 061e36c, awaiting its live pass"); recreate them in the
next release's verification doc when the Reliquary live pass is scheduled.

| # | Commit | Change | How to verify live (when revived) |
|---|--------|--------|-----------------------------------|
| 6 | 061e36c | **Reliquary Phase 1: Mark toasts** (dev threshold: 2 kills per archetype). | Kill 2 humans with one weapon: toast "{weapon} has earned its Mark: Manslayer!". Repeat for casters (Spellbreaker) and monsters (Beastbane). Toast delivers via the facing prompt as before. |
| 7 | 061e36c + 5db7a90 | **Reliquary Phase 1: the card's story line** (Ledger voice, colon separator). | After any kill, the equip card's flavor line reads "{name}: {k} felled; last, a {victim}." After a Mark: "{name}, Manslayer: {n} men felled; last, ...". The Kills counter KEEPS UPDATING across several battles and card opens (the three-way-anchor regression). Weapons with no deeds keep their baked flavor untouched. Sasuke's Blade NEVER narrates (26-char budget, by design). |
| 8 | 061e36c | **Undead classifier / Requiem path** (live-unexercised; the one classifier never fired). | Kill 2 undead (skeleton/ghoul) with one weapon: Requiem toast; card reads "last, a risen one"; legends.json counts index 3 rises. Use weapon kills. |
| 9 | 061e36c | **legends.json persistence**. | After an earning battle: legends.json appears beside kills.json (a .bak after the second save); survives a redeploy (row 5's mechanism); deeds/marks intact after game restart. |

## Row 11 protocol: the log facelift + gate-regression sweep

1. **Launch header** (console, on boot): version + build flavor, settings echo, "kill tally holds
   N lifetime kills across M weapons", legends summary, tracking count, loop started, hooks
   footer. No verb brackets on any Info line. A fresh install adds one [WARN] [save] fresh-start
   line (expected).
2. **Armed battle** (a Living Weapon main-hand deployed): full match report, expected 8-14 lines:
   battle started, coverage line, kill credits WITH victim identity ("Windrunner claims kill
   number 8, felling an undead foe at (7,6)."), signature moments, Mark/toast if earned, the
   battle-end summary with per-weapon counts and correct singular/plural ("1 Mark").
   ABSENT noise to confirm gone: per-turn "finished its turn" lines, "died but was not a tracked
   enemy" on ally deaths, puppeteer/kobu verdict spam, hex sentinel dumps.
3. **Unarmed battle** (no Living Weapon fielded): console shows EXACTLY the two bookends
   (started / ended: no kills credited). Anything more = gate false-positive; anything less =
   worse.
4. **Gate false-negative check** (the top regression risk): in the ARMED battle, if the console
   prints only bookends, the armed-predicate is failing (sticky-latch timing or main-hand locate
   flake). Capture livingweapon.log immediately.
5. **File cross-check** (evidence-thinning check): pick two events missing from console (a turn
   line, a signature verdict); both MUST exist in livingweapon.log as [DEBUG] [verb] lines.
   Missing from the FILE too = report immediately, that is a real regression.
6. **Dedup**: trigger the same warning twice in one battle (console shows it once); next battle
   it may show once again.
7. **WARN/ERROR keep their verb on console** (any warning line shows [WARN] [save]-style tokens).
8. **Fast-forward soak** (thread-safety): one long auto-battle at max game speed with toasts
   firing. No crash, no frozen console, log file lines never interleaved/truncated.
9. **Mid-battle Reequip** (gate flip): if reachable, swap the living weapon off mid-battle;
   signature console lines stop; the file keeps recording.

## Automated log scan (LW-54)

Every live-verify session ends with one mechanical gate: after the deploy and the battles above,
run the log scanner. It reads the newest `livingweapon.log` straight from the deployed mod folder
(resolved like BuildLinked: `$RELOADEDIIMODS/prawl.fft.livingweapons`, else the default Steam
path) and hard-fails on runtime trouble a human eyeball tends to skim past.

```
python tools/scan_logs.py            # exit 0 = clean, 1 = runtime failure(s), 2 = could not run
python tools/scan_logs.py --flight   # also inspect the newest flight/*.jsonl archive
python tools/scan_logs.py --require-battle   # also fail if no battle ran in this log
```

It fails (exit 1) on any `[ERROR]` line, a fingerprint-guard stand-down (the mod switched itself
off), or a "played a battle but never armed" state (writes stayed disabled). `[WARN]` lines never
fail it (they are the "degraded but coping" tier); a fresh-install save notice is expected. Exit 2
means no log was found (deploy and play a battle first) or a bad argument. This is a VERIFY step,
NOT a build gate: the build never runs the game, so at build time the log is a stale artifact and
build failures are already caught by generate/analyze/test/compile. Confirm the tool itself with
`python tools/scan_logs.py --selftest`.

## Notes
- A green `tools/scan_logs.py` run is the closing gate of any live-verify session; capture its
  output alongside the row you are flipping.
- Rows 10-12 verified live 2026-07-07 (owner), closing LW-2's release-verify scope. Rows 6-9
  (Reliquary Phase 1 live pass) are deferred past 2.3.0 and tracked under backlog LW-6.
- Reliquary AC checkbox flips and LIVE_LEDGER rows remain Patrick-only.
- The 2.3.0 release verification doc is docs/archive/SMOKE_TEST_2.3.0.md (LW-60), built from this file
  plus docs/RELEASE_SCOPE.md's IN list. Row 1 (Choir multi-bearer) is folded into its regression
  section (7.17); flip both places together.
