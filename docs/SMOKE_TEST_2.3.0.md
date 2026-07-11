# FFTLivingWeapons 2.3.0 Release Smoke Test Plan

STATUS: CONTRACT (the 2.3.0 pre-ship owner live pass, LW-60; archives to docs/archive/ once 2.3.0 ships)

*Work top to bottom; blockers surface first. Check each box live. Owner-only flips: the boxes here,
docs/VERIFY_LIVE.md rows, docs/LIVE_LEDGER.md PROVEN flips, and the docs/TODO.md AWAITING-LIVE exits.
Modeled on docs/archive/2.0_RELEASE_CHECKLIST.md; scope ground truth is docs/RELEASE_SCOPE.md.*

Two rows in this pass are the release's named debts: the LW-51 cold-launch New Game eyeball (6.1)
and the LW-71 struck-pre-turn Iai repro (2.2). Several others may already be satisfied by the
2026-07-11 live session (marked "possibly pre-satisfied"); accepting that session's evidence
instead of re-running is the owner's call, made per row, not wholesale.

---

## 0. Read first: how to run this pass (every trap here has bitten before)

- **Flavor.** This pass runs on a PROD deploy: kill `fft_enhanced.exe`, then `.\BuildLinked.ps1 -Prod`
  (thresholds {5,25,50}, NO tally seeding). Rows tagged **(DEV OK)** may instead be exercised on a
  plain dev build in a throwaway session when the real save lacks a +3 of the needed weapon (dev
  thresholds {1,2,3} plus seed-to-3 arm every signature instantly), but any box you flip on dev
  evidence should say so. Re-deploy `-Prod` before the closing scan_logs run, and remember a plain
  dev run refuses to stomp a prod install without `-Force` (that guard is itself row 8.6).
- **Evidence lives in the FILE.** `livingweapon.log` (rotated to `livingweapon.prev.log` each
  launch) carries every line including `DBG `; the console shows Info and above only, and the
  once-per-battle coverage line usually demotes to file-only (LW-75, known, not a regression).
  When a row says "log", grep the file.
- **Do not click into the console mid-battle.** QuickEdit selection suspends the mod thread
  (LW-8): kills, growth, and toasts stall until you press Escape. Read the file afterwards.
- **Flight tapes flush on battle EDGES.** To bank a tape, END the battle (win, lose, flee); a hard
  process kill loses the in-memory ring. Never kill-and-deploy to "check" a flight; Alt-Tab out,
  the file is already on disk once the edge fired. Read tapes with `python tools/parse_flight.py`.
- **After every deploy, eyeball preservation.** The restore lines must appear AND the files must
  exist under the Reloaded `User/Mods/prawl.fft.livingweapons` folder (`kills.json`,
  `legends.json`, `gunslinger.json`, `flight/`). One deploy lost preserved files intermittently
  (LW-28, open backlog); there is no loud post-restore check yet, so the eyeball is the check.
- **Unexplainable weirdness: bisect the mod list FIRST** (docs/DEV_TEST_RECIPES.md, step zero)
  before blaming this repo. Harness cheats (give_all_items, WP bump, give_move, kill_all) and the
  signature-grant ACTIVE check also live in that doc.
- **Closing gate.** Every live session of this pass ends with
  `python tools/scan_logs.py --require-battle --flight` reading exit 0 (LW-54). Exit 1 means a
  runtime failure (any ERROR line, a guard stand-down, or armed-never-rose despite a battle);
  exit 2 means it could not run. Capture the output next to the boxes you flip.

---

## 1. Smoke test (10 minutes)

> Goal: clean prod deploy, guarded startup, one full battle round-trip, card paint, green scan.

- [ ] **Deploy prod.** `.\BuildLinked.ps1 -Prod`: every step green (generate, analyze gate, meta,
  full test suite (2426 at authoring), DLL publish, deploy), `build_flavor.txt` = prod, no
  `REFUSING TO DEPLOY`, preservation restore lines present. **[BLOCKER]**
- [ ] **Launch header.** `livingweapon.log` opens with the launch header: version + PROD flavor,
  settings echo, the lifetime kill-tally line. No startup-failed trace. **[BLOCKER]**
- [ ] **Guard arms.** Seconds after a save loads, the armed line fires ("Living Weapons is armed";
  LaunchGuard verified the PE key, the JobCommand signature, and Ramza's roster shape). NO
  stand-down line, NO OS message box. **[BLOCKER]**
- [ ] **One battle round-trip.** Battle started, a player kill credits with victim identity, battle
  ended with the summary and tally save; afterwards the equip card shows the Kills meter for that
  weapon. **[BLOCKER]**
- [ ] **Scan green.** `python tools/scan_logs.py --require-battle --flight` exits 0. **[BLOCKER]**

---

## 2. Samurai Swords (BLOCKER: the release identity, all three at +3)

Prod +3 = 50 lifetime kills; use the real save's grown katanas or tag rows (DEV OK).

- [ ] 2.1 **Iai (Ame-no-Murakumo id 42): the opening turn.** The wielder takes the literal first
  turn of the battle regardless of build (Speed quietly held above the field max at battle-open),
  then releases on that first turn; the release log line names its source ("released by the turn
  flags"). Speed reads normal afterwards. **[MAJOR]**
- [ ] 2.2 **Iai struck-pre-turn repro (the LW-71 deferred check).** Arrange an enemy to STRIKE the
  Iai wielder before its opening turn (a fast enemy or a lucky layout). The hold must survive the
  hit (no false release; pre-fix, the parked actor pointer released here and could write the
  wrong unit's Speed), then release normally on the wielder's own turn, named to the turn flags.
  **[MAJOR]**
- [ ] 2.3 **Kobu (Kiyomori id 43): the Brave climb.** Strike a foe whose current Brave exceeds the
  wielder's: the wielder's Brave visibly rises to match (one-shot write, capped 97, battle-scoped,
  never lowers the foe despite the card's "steals" wording). The LIVE_LEDGER Kobu row is still
  Uncertain (the 2026-07-02 rework fired once live, never flipped): this box is its flip evidence.
  **[MAJOR]**
- [ ] 2.4 **Mushin (Kiku-ichimonji id 45): bank and spend.** A full Wait turn (no move, no act)
  banks one charge (the file logs the bank); the wielder's next own action lands one boosted hit
  (held PA about 2.05x natural, roughly 1.6x a normal +3 swing) and the charge clears (the file
  logs the spend). A move-then-wait turn neither banks nor spends; two waits do not stack.
  **[MAJOR]**
- [ ] 2.5 **Trio integration.** All three katanas fielded in one battle: each behavior fires, no
  tick-error lines, one clean battle exit. **[MINOR]**

---

## 3. Galewind Puppeteer (BLOCKER: expiry truth + card truth)

- [ ] 3.1 **Own-turn release (LW-5, re-confirm).** A +3 Galewind hit dominates the struck enemy;
  the player gets full menu control of it on ITS next turn; it releases after that turn, not on
  the wielder's clock (file: "Puppet control ended after the enemy took its turn"; the flight
  tape records reason=own-turn). Note: docs/RELEASE_SCOPE.md section 2's committed-behavior
  paragraph predates this landing (the stretch shipped as LW-5); the 8.8 sweep corrects it.
  **[MAJOR]**
- [ ] 3.2 **Card text matches shipped behavior (LW-46, DECIDE BEFORE SHIP).** The card currently
  reads "Turns a struck enemy into your puppet for its full turn. No Lucavi; 3-turn cooldown."
  but IsDominatable allows everyone (a Lucavi CAN be puppeted) and the shipped cooldown is 4
  global turns. Either build the Lucavi carve-out or reword the card (and check the cooldown
  wording in the same pass); this box flips only when the shipped card and the shipped behavior
  agree. Data change = restart-only + patch_names re-bake. **[BLOCKER]**
- [ ] 3.3 **Cooldown + single puppet.** A second dominate within the cooldown window does not arm;
  only one puppet at a time. **[MINOR]**

---

## 4. Kill attribution + coverage truth

- [ ] 4.1 **Flags-first credit, manual (LW-63, possibly pre-satisfied 2026-07-11).** In a manual
  battle, kills credit the true killer even when another unit is hovered/parked on (the file logs
  the resolve "via the turn flags" at Debug tier; the flight tape records latch src=turn-flags).
  **[BLOCKER]**
- [ ] 4.2 **Flags-first credit, auto-battle (LW-63, possibly pre-satisfied).** One auto-battle:
  every kill credits the correct weapon (the 2026-07-11 tape credited all five). **[BLOCKER]**
- [ ] 4.3 **Coverage line counts only fielded enemies (LW-34, possibly pre-satisfied).** In the
  FILE, "All N enemies are accounted for" matches the enemies actually visible on the field (the
  line lands about a minute in; phantom conditional-spawn seats are excluded). Zero
  no-longer-visible warnings in a normal battle. **[MAJOR]**
- [ ] 4.4 **Census-finished line + no flood (LW-69, the one unobserved piece).** Open the
  Attack/Abilities card mid-battle and let the sweep complete: the file shows the census-finished
  line carrying its rejected count, ZERO per-candidate "evicting the cached copy" lines, and no
  single line class dominating the session log. Owner flips the TODO row on this evidence.
  **[MAJOR]**
- [ ] 4.5 **Attack-card gate under auto-battle (LW-55's open premise).** LW-63's auto-battle tape
  already proved the per-unit turn flags rise during auto-turns; the remaining question is only
  whether the Attack card composes the dossier during an auto-battle turn if opened. Worst case
  is a vanilla card (narrowing-only gates), so a vanilla observation here is acceptable, not a
  failure; record what you saw either way. **[MINOR]**
- [ ] 4.6 **Slow-cast false-exit watch (LW-42).** Queue a long charge-time spell and let the camera
  linger through the cast: the battle must NOT false-exit mid-fight (the 1.5 port left the
  mode-1/5 slot0==0xFF excuse dead; a false exit resets the kill tracker and shows as a spurious
  "battle ended" in the file mid-battle). If it fires, capture the log and stop the pass.
  **[BLOCKER]**
- [ ] 4.7 **No false duplicate-blocks (LW-68 spot check).** In a battle where a boss or leveled
  enemy takes a max-HP-shifting hit before dying, its kill still credits (the orphan-alive-edge
  rescue is tape-only vocabulary: end the battle and read the flight tape if in doubt); no
  "Blocked a repeat credit" WARN in a battle that credited nothing. No dedicated setup needed.
  **[MINOR]**

---

## 5. Cards, display, and the item pass (data rows take effect on game RESTART)

- [ ] 5.1 **Equip-card Kills meter, fast paint (LW-37).** Open the equip card on a tracked weapon:
  the meter ("Kills: N/T to +") is present on first open with no perceptible sweep delay, and
  matches kills.json. **[BLOCKER]**
- [ ] 5.2 **Suffix truth after reset (LW-59, possibly pre-satisfied 2026-07-11).** After the
  section-6 New Game reset: the equip card shows the PLAIN weapon name (no stale +N) beside the
  fresh meter, and the suffix climbs again with new kills. **[MAJOR]**
- [ ] 5.3 **Abilities-menu funnel (LW-31 family).** On a wielder's turn the Attack row renames to
  the weapon and its hover card carries the dossier; on the FIRST turn of the session's second
  battle the rename is already warm (LW-38). Known and accepted: fingerprint-twin units fall back
  to vanilla (LW-39); the first battle after a session load may show generic "Attack" until the
  first acted edge (LW-57). Marks never appear on any card (release-hidden, LW-35). This box is
  the flip evidence for the LIVE_LEDGER Attack-row-rename row (owner-flip-only per the LW-31
  exit). **[MAJOR]**
- [ ] 5.4 **Tuning batch cards (dd45229, restart-only).** Spot-check in-game cards against
  data/items.json for: the elemental rods (nerf), Trailwarden Jerkin (gated to T4 so it no longer
  stacks early with the unchanged T1 Wayfarer Boots), Sanctguard id 133 (rider retune), Claymore
  (CARD REWORD only, ForcedTwoHands wording; stats unchanged by design), and Kiku-ichimonji
  (the rebaked Mushin +3 block reads and fits; its card eyeball was never recorded). **[MAJOR]**
- [ ] 5.5 **Longest cards still fit the box.** Open the cards at the top of the description
  budget: Arcanum (exactly at the 205-char DESC_MAX), Zwill Straightblade, Cursed Ring, Staff of
  the Magi, Warlock's Staff: description + Kills line fit on screen (DESC_MAX is a rough guard;
  the true constraint is wrapped lines, eyeball it). **[MINOR]**
- [ ] 5.6 **Offensive Chemist fully gone, Barrage intact.** Grenade items (ids 246-250) appear
  nowhere (shops, inventory, cards); the Barrage COMMAND on a +3 Yoichi wielder still shows its
  correct name and description (Barrage's text key 358 ships via the same cell-merged
  ability.en.nxd re-bake that used to carry the chemist keys 374-378; a bad re-bake corrupts
  Barrage, the Bloodpact precedent). **[MAJOR]**
- [ ] 5.7 **Config surface (LW-52).** Reloaded launcher, Configure Mod: exactly ONE option remains
  (Treasure Master Always On); BannerToasts, DevSeedKills, and VerboseLog are gone. **[MAJOR]**
- [ ] 5.8 **Release note eyeball (LW-72 rider).** README's Language support section reads right
  (non-English players get full gameplay; item text, Kills counter, and toasts are English-only
  readouts). **[MINOR]**

---

## 6. Save integrity (BLOCKER)

- [ ] 6.1 **Cold-launch New Game eyeball (the LW-51 deferred check).** From a COLD game launch
  (not an in-session restart): main menu, New Game. Expected: the prior kills.json is archived
  into the archive/ subfolder of the save dir (archive/kills.N.json; the file line "Archived the
  previous kills.json" prints the full path) and the tally resets (launch header shows the fresh count;
  equip cards read 0 with plain names, see 5.2). The Orbonne opener's scripted kills stay
  UNCREDITED BY DESIGN (LW-56: the stand-in units are structurally unbridgeable to the roster;
  "no kills were credited" there is correct, not a regression). The first REAL battle afterwards
  credits kill number 1. A Continue load must NOT trip the reset (the detector needs the opener
  dialogue held, not a one-frame event dip). Dev-build caveat while eyeballing toasts: after an
  out-of-battle reset a dev build can swallow the first-blood toast (LW-70); prod is safe.
  **[BLOCKER]**
- [ ] 6.2 **Save files live update-safe (LW-51/LW-29).** kills.json, legends.json, gunslinger.json
  sit under Reloaded `User/Mods/prawl.fft.livingweapons`, NOT the deploy folder, so a 2.2.2 to
  2.3.0 mod update cannot wipe them. **[MAJOR]**
- [ ] 6.3 **Deploy preservation round-trip (LW-28 watch).** Across this pass's deploys: restore
  lines present each time, files intact afterwards, tally count unchanged. Any silent loss:
  capture the deploy transcript + %TEMP% state and stop deploying until understood. **[MAJOR]**
- [ ] 6.4 **Guard stand-down drill (optional, DEV OK, live-proven 2026-07-10).** Only if guard code
  was touched since: set `LW_FORCE_FINGERPRINT_MISMATCH=1` on a DEV build: one loud stand-down
  line naming the landmark, one OS message box, a flight_*_standdown.jsonl archive (LW-53), zero
  writes for the session, and scan_logs exits 1 on that log. Unset and redeploy prod after.
  **[MINOR]**

---

## 7. Regression pass (everything that already shipped keeps working)

Signature rows: prod +3 = 50 kills; use grown weapons on the real save or tag the box (DEV OK).
Watch the FILE for the once-per-battle signature/grant narration; a benched +3 narrates at
Debug/file tier only.

- [ ] 7.1 **Kill tally increments + persists** across battles and a session restart (credit lines
  in-battle, battle-end save, launch header count carries over). **[BLOCKER]**
- [ ] 7.2 **Stat growth holds.** A tiered weapon's wielder shows PA/MA/Speed at
  round(natural x (1+factor)) in battle; growth lines in the file. **[MAJOR]**
- [ ] 7.3 **Battle enter/exit cycle clean:** started, ended, fresh started across two consecutive
  battles; no double-exit, no mid-battle exit. **[BLOCKER]**
- [ ] 7.4 **Zwill Straightblade (id 10) extra turn:** a Zwill kill grants the killer an immediate
  extra turn. **[MAJOR]**
- [ ] 7.5 **Venombolt (id 80) Plague:** poison it inflicts never cures and ticks harder; the
  poison itself never lands the killing blow. **[MAJOR]**
- [ ] 7.6 **Yoichi Bow (id 90) Barrage:** the Barrage command appears in the wielder's command
  list (and survives the learn screen). **[MAJOR]**
- [ ] 7.7 **Sanguine Sword (id 23) Shadow Blade:** the command appears for the wielder (known,
  accepted leak: same-job enemies can also see it; job-global record). **[MINOR]**
- [ ] 7.8 **Stormarc (id 86) Chain Lightning:** damaging one enemy chips nearby enemies (up to 3
  hops, decaying). **[MAJOR]**
- [ ] 7.9 **Huntress (id 89) Maim:** a struck enemy stops firing its reaction (Counter etc.) for 3
  turns. **[MINOR]**
- [ ] 7.10 **Eclipsebolt (id 78) Eagle Eye:** an enemy carrying Doom has its countdown snapped to
  1. **[MINOR]**
- [ ] 7.11 **Arcanum (id 30) Larceny:** striking a buffed enemy strips the buff onto the wielder
  for 3 of its turns. **[MINOR]**
- [ ] 7.12 **Umbral Rod (id 56) Spiritual Font:** ending a turn on a NEW tile silently restores
  10% HP and MP (watch the unit card values; silent with no Umbral Rod fielded). **[MINOR]**
- [ ] 7.13 **Rod of Faith (id 58) Rapture:** below 30% HP the wielder's movement becomes
  teleportation until they recover. **[MINOR]**
- [ ] 7.14 **Mending Staff (id 61) Renewal:** allies within 1 tile of the wielder regain about 10%
  max HP at the wielder's turn end (silent write, watch the numbers). **[MINOR]**
- [ ] 7.15 **Sanctus Staff (id 64) Benediction:** while the wielder was the last player to act,
  ally healing lands about 30% larger (survives charged-heal resolve gaps). Its LIVE_LEDGER row
  has been "ready for PROVEN" since 2026-06-16; this box is the flip evidence. **[MINOR]**
- [ ] 7.16 **Staff of the Magi (id 66) Sanctuary:** fallen allies hold 3 crystal hearts all battle
  while the bearer lives (never crystallize). **[MINOR]**
- [ ] 7.17 **Warlock's Staff (id 60) Choir + the open multi-bearer row (VERIFY_LIVE row 1).** The
  bearer's own charged magicks resolve instantly (holder-only; the old adjacent-ally aura was
  dialed back by design). For the still-open row 1: TWO deployed +3 bearers each cast instantly
  in the same battle, and a benched third copy neither casts instantly nor blocks the others.
  Flip VERIFY_LIVE row 1 together with this box (its "projects a duet" wording predates the
  holder-only dial-back; instant-cast per bearer is the current contract). **[MAJOR]**
- [ ] 7.18 **Wrathblade (id 27) Feign Death:** a lethal hit leaves the wielder playing dead, then
  auto-revived at low HP after about two of its turns, no crystal. **[MINOR]**
- [ ] 7.19 **Outrider Pistol (id 71) Gun Slinger:** out of battle the off-hand fills with a second
  pistol + Dual Wield; in battle Attack fires twice. Known, accepted: a SECOND simultaneous
  wielder equips slowly (LW-43); the LIVE_LEDGER roster-write row is still pending its flip, this
  box is its evidence. **[MAJOR]**
- [ ] 7.20 **Support grants fire with readback=SET** (Gloomfang Concentration, Mortal Coil Attack
  Boost, Sanguine Gauche / Hushblade defense boosts): one grant line each in the file when the
  wielder fields; per-ability oracles in docs/DEV_TEST_RECIPES.md. **[MINOR]**
- [ ] 7.21 **Marks stay hidden everywhere (LW-35)** while legends.json keeps growing beside
  kills.json (collection on, display off; the Reliquary Phase 1 live pass itself is deferred past
  2.3.0, see Appendix A). **[MAJOR]**
- [ ] 7.22 **Treasure Master still gates on the Scholar's Ring** (ships in 2.3.0; removal is
  post-release, LW-10): ring equipped on a DEPLOYED unit = marks; no ring = one idle line, no
  marks. TreasureAlwaysOn stays default False. **[MINOR]**
- [ ] 7.23 **Dormant modules stay dormant (expected, not bugs):** CharmLock (superseded by
  Puppeteer), LifeSap, and Wyrmblood have no live data wiring and must produce no behavior and no
  narration. Nothing to see is the pass condition. **[MINOR]**
- [ ] 7.24 **World-map battle re-enter registers (LW-40 re-check).** Leave an in-progress battle
  to the world map, then restart it: the mod registers the battle (battle started in the file,
  Attack row renamed, kills credit). The enter/exit machinery changed twice after LW-40's
  2026-07-07 verification (LW-56's forced new-game exit edge, LW-34's coverage work), and the
  pre-fix failure mode was total silent dormancy. **[MAJOR]**
- [ ] 7.25 **Toasts still DELIVER (positive control).** One tier-up crossing on prod (pick a
  weapon sitting just under 5, 25, or 50 in kills.json) delivers its banner via the facing
  prompt, with the enqueue and deliver lines in the file. The toast plumbing changed twice this
  cycle (LW-35 release-hid deed toasts, LW-52 removed the BannerToasts toggle), and every other
  toast row in this pass only proves ABSENCE. **[MAJOR]**

---

## 8. Build / release gates (GO/NO-GO inputs)

- [ ] 8.1 **Dominance gate green:** `python tools\analyze.py` exit 0. **[BLOCKER]**
- [ ] 8.2 **Unit tests: 0 failed** (`dotnet test LivingWeapon.Tests\LivingWeapon.Tests.csproj`;
  2426 at authoring). **[BLOCKER]**
- [ ] 8.3 **Publish.ps1 clean:** package verify OK for every required file (meta.json and
  treasure.json included), exit 0. **[BLOCKER]**
- [ ] 8.4 **PROD DLL truth:** thresholds {5,25,50}, no LWDEV, no seeding; kills.json not a dev-seed
  sea of exact-3 entries. **[BLOCKER]**
- [ ] 8.5 **release.yml + pipeline.ps1 sentinels** still list every shipped file. **[MAJOR]**
- [ ] 8.6 **Build-flavor guard:** a plain `.\BuildLinked.ps1` over the prod install refuses (exit
  1, no files touched). **[MAJOR]**
- [ ] 8.7 **Version + tag:** ModVersion 2.2.2 to 2.3.0 in mod/ModConfig.json; matching v2.3.0 tag
  cut; mod description current. **[BLOCKER]**
- [ ] 8.8 **Ledger + scope hygiene at ship:** docs/RELEASE_SCOPE.md boxes all ticked (several are
  done-but-unticked as of authoring) and its section-2 committed-behavior paragraph corrected to
  the shipped own-turn release (see 3.1); docs/TODO.md Now items exited to the changelog via
  owner flips (LW-69, LW-60); VERIFY_LIVE row 1 flipped (7.17). **[MAJOR]**

---

## GO / NO-GO (any fail = do not release)

1. analyze.py exit 0; full suite 0 failed; Publish.ps1 + package verify clean.
2. PROD DLL: thresholds {5,25,50}, no LWDEV, no seeding; flavor guard refuses dev-over-prod.
3. Guard arms on a normal launch; no stand-down; scan_logs `--require-battle --flight` exit 0 on
   the final session.
4. Samurai trio behaviors verified live (section 2), including the struck-pre-turn Iai repro.
5. Galewind card text agrees with shipped puppet behavior (LW-46 resolved and re-baked).
6. Kills credit correctly in manual AND auto battles; coverage line counts fielded enemies; the
   census-finished line observed with zero flood.
7. Cold-launch New Game: tally archives + resets; Continue does not trip it; save files live
   update-safe; deploy preservation held all pass.
8. Equip card: fast meter, no stale suffix after reset; Attack-row funnel warm from battle 2.
9. Item pass cards match items.json; grenades gone with Barrage's text intact; config surface is
   the single toggle.
10. ModVersion 2.3.0 + tag cut; RELEASE_SCOPE boxes green; TODO Now cleared.

---

## Open risks to watch (known, accepted, or tracked elsewhere)

- **LW-7 auto-battle turn-count collapse:** turn COUNTING can still collapse under auto-battle
  (credit is safe post-LW-63); affects diagnostics, not tallies. Backlog.
- **LW-28 intermittent preservation loss:** the round-trip usually holds; the loud post-restore
  check is not built yet. Eyeball every deploy (0. Read first).
- **LW-42 dead slot0 excuses:** a long cast at mode 1/5 could accumulate the exit debounce and
  false-exit (row 4.6 is the watch); re-anchor is backlog.
- **LW-39 fingerprint twins:** two party units at identical level + HP/MaxHP make the Attack-card
  resolve fail closed to vanilla. By design until the fingerprint widens.
- **LW-57 first-open latency:** the first battle after a session load can show generic "Attack"
  until the first acted edge primes the resolve. Tracked; not a 2.3.0 blocker.
- **LW-75 console demotion:** the coverage line nearly always lands file-only because the armed
  gate rises later; the file evidence is unaffected. Candidate fix backlogged.
- **LW-23 / LW-24 toast delivery:** a deed toast can starve a same-kill tier-up toast, and a late
  tier-up banner is swallowed by locked policy (owner 2026-07-05) rather than delivered on the
  wrong unit's turn. Cosmetic; both backlog (and Mark deed toasts are release-hidden anyway).
- **CharmLock's contradicted expiry clock:** the LIVE_LEDGER row on +0x25 turn reads stands, but
  the module is dormant in shipped data; no player-visible surface in 2.3.0.
- **LW-8 QuickEdit stall:** see 0. Read first; candidate async console sink is backlog.

---

## Appendix A: deferred by decision (NOT part of this pass)

- **Reliquary Phase 1 live pass** (VERIFY_LIVE rows 6-9: Mark toasts, card story line,
  undead/Requiem classifier, legends.json narration): deferred past 2.3.0, rides backlog LW-6.
  With Marks release-hidden (LW-35), rows 6-8 have no observable surface in a 2.3.0 build; only
  legends.json persistence is checkable (folded into 7.21). If the owner pulls the Reliquary pass
  back in, recreate the rows from docs/VERIFY_LIVE.md's deferred section and re-scope.
- **Treasure Master removal** (LW-10), **Murasame signature** (LW-47), **Stormbrand replacement**
  (LW-14), and the rest of docs/RELEASE_SCOPE.md's DEFERRED list: post-release work, no rows here.
- **Tier-2 per-save tally isolation** (LW-61): alternating playthroughs still share one tally by
  design this release; only the Tier-1 reset (6.1) ships.

## Appendix B: harness references

- Cheats and probes: docs/DEV_TEST_RECIPES.md (give_all_items, WP bump for one-hit kills,
  give_move / kill_all, the signature-grant ACTIVE check, mod-list bisection).
- Log + tape tooling: `python tools/scan_logs.py --selftest` to trust the scanner;
  `python tools/parse_flight.py <file>` for tapes; docs/LOGGING.md for the verb glossary and
  flush triggers.
- Live-claim bookkeeping: docs/LIVE_LEDGER.md rows wanting flips from this pass: Kobu (2.3),
  Gun Slinger roster-write (7.19), the Attack-row rename (5.3), Benediction (7.15);
  docs/VERIFY_LIVE.md row 1 rides 7.17.
