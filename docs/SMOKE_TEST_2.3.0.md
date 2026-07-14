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
- **After every deploy, eyeball preservation.** The restore line must appear AND the files must
  exist under the Reloaded `User/Mods/prawl.fft.livingweapons` folder (`kills.json`,
  `legends.json`, `gunslinger.json`, `flight/`). One deploy lost preserved files intermittently
  (LW-28, open backlog); BuildLinked now fails RED on a lost preserved item (post-restore check,
  backup copies kept in %TEMP%), so a green deploy plus the eyeball is belt and suspenders.
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

- [x] (PASSED 2026-07-14: suite 2525/0, analyze 0, flavor marker prod, preservation restore +
  post-restore check green, prior-session scan CLEAN) **Deploy prod.** `.\BuildLinked.ps1 -Prod`:
  every step green (generate, analyze gate, meta,
  full test suite (2426 at authoring), DLL publish, deploy), `build_flavor.txt` = prod, no
  `REFUSING TO DEPLOY`, preservation restore lines present. **[BLOCKER]**
- [x] (PASSED 2026-07-14: 11:54:45 header, version 2.2.2 production build, config echo, tally +
  legends lines, no startup trace) **Launch header.** `livingweapon.log` opens with the launch
  header: version + PROD flavor,
  settings echo, the lifetime kill-tally line. No startup-failed trace. **[BLOCKER]**
- [x] (PASSED 2026-07-14: armed 11:56:20 "matches all memory landmarks", no stand-down, no
  message box, with Blue And Red Mages 2.0.2 enabled) **Guard arms.** Seconds after a save loads,
  the armed line fires ("Living Weapons is armed";
  LaunchGuard verified the PE key, the JobCommand signature, and Ramza's roster shape). NO
  stand-down line, NO OS message box. **[BLOCKER]**
- [x] (PASSED 2026-07-14, owner live: battle 11:57:01 to 12:00:30, "3 kills credited (Chaos
  Blade 3)", tally and legends saved, equip-card Kills meter verified by the owner) **One battle
  round-trip.** Battle started, a player kill credits with victim identity, battle
  ended with the summary and tally save; afterwards the equip card shows the Kills meter for that
  weapon. **[BLOCKER]**
- [x] (PASSED 2026-07-14: CLEAN, 224 lines, 0 warnings, 0 errors, header + armed + battle +
  flight archives all present) **Scan green.** `python tools/scan_logs.py --require-battle
  --flight` exits 0. **[BLOCKER]**

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
  tape records reason=own-turn). **[MAJOR]**
- [ ] 3.2 **Reworded card renders (LW-46, restart-only).** The Galewind card's +3 block now reads
  "Turns a struck enemy into your puppet for its full turn. 3-turn cooldown." (the false "No
  Lucavi" clause was dropped to match the allow-everyone gate; a Lucavi CAN be puppeted, by
  design). Eyeball the card on a restarted game: new text, fits the box, no clipping. **[MAJOR]**
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
- [x] 4.3 (PASSED 2026-07-14, owner flip: battle-1 line at INFO tier exactly once, "All 4
  enemies are accounted for" matched the field; the no-tracked-weapon battle stayed file-only
  by design; LW-75 exited on this evidence) **Coverage line counts only fielded enemies (LW-34,
  possibly pre-satisfied).** In the
  FILE, "All N enemies are accounted for" matches the enemies actually visible on the field (the
  line lands about a minute in; phantom conditional-spawn seats are excluded). Zero
  no-longer-visible warnings in a normal battle. NEW with LW-75 (f91e0d2): in a battle with a
  Living Weapon fielded, the same line now also reaches the CONSOLE exactly once, surfacing the
  moment the mod arms (possibly a minute-plus into the battle), never twice. **[MAJOR]**
- [x] 4.4 (PASSED 2026-07-14, owner flip: census-finished carried "8191 candidates rejected",
  zero evicting lines, no dominating line class in the 224-line session; LW-69 exited on this
  evidence) **Census-finished line + no flood (LW-69, the one unobserved piece).** Let a battle run
  a few turns: the file shows the census-finished line carrying its rejected count, ZERO
  per-candidate "evicting the cached copy" lines, and no single line class dominating the
  session log. The census hardening commit 9d347c9 makes this reliably observable: a
  battle-edge abort now re-arms the census next battle instead of silently keeping a partial
  cache (the pre-fix reason this line was never seen). Bonus check: enter a battle, flee it
  immediately, enter another; the second battle logs a fresh census armed AND finished line,
  and the finished count does not double (warm hits merged, not duplicated). Owner flips the
  TODO row on this evidence. **[MAJOR]**
- [ ] 4.5 **Attack-card gate under auto-battle (LW-55's open premise).** LW-63's auto-battle tape
  already proved the per-unit turn flags rise during auto-turns; the remaining question is only
  whether the Attack card composes the dossier during an auto-battle turn if opened. Worst case
  is a vanilla card (narrowing-only gates), so a vanilla observation here is acceptable, not a
  failure; record what you saw either way. **[MINOR]**
- [x] 4.6 (PASSED 2026-07-14, owner live: a long Hastija charge and cast played through with no
  false battle-end; no mid-battle exit line in the file) **Slow-cast false-exit watch (backlog
  LW-42).** Queue a long charge-time spell and let
  the camera linger through the cast: the battle must NOT false-exit mid-fight (the 1.5 port left
  the mode-1/5 slot0==0xFF excuse dead; a false exit resets the kill tracker and shows as a
  spurious "battle ended" in the file mid-battle). If it fires, capture the log and stop the pass.
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
- [x] 5.3 (PASSED 2026-07-14, owner live: weapon name present on the very first turn of the
  session's FIRST battle; a mid-battle weapon swap re-resolved correctly; LW-57 exited and the
  LIVE_LEDGER Attack-row rename row flipped PROVEN on this evidence) **Abilities-menu funnel
  (LW-31 family).** On a wielder's turn the Attack row renames to
  the weapon and its hover card carries the dossier; on the FIRST turn of the session's second
  battle the rename is already warm (LW-38), and with the LW-57 fix (9d347c9: the census no
  longer starves the repaint driver) the FIRST battle after a session load must ALSO show the
  weapon name on the very first turn's menu open, within about a second of the turn opening.
  Known and accepted: fingerprint-twin units fall back to vanilla (backlog LW-39). Marks never
  appear on any card (release-hidden, LW-35). This box is
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
- [ ] 5.8 **Release note eyeball (LW-72 rider) + README truth.** README's Language support
  section reads right (non-English players get full gameplay; item text, Kills counter, and
  toasts are English-only readouts). Also confirm the two stale claims flagged 2026-07-13 are
  corrected before ship: install step 5 must no longer call this "a data-only mod" (the Living
  Weapon DLL runs live in-process; only tables/nxd/tex are restart-bound), and the How-it-works
  diagram must credit item.en.nxd to tools/patch_names.py, not generate.py. **[MINOR]**

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
  dialogue held, not a one-frame event dip). If run on a DEV build: the first post-reset kill
  SHOULD now toast (the LW-70 re-baseline fix); a missing first-blood toast there is a real
  regression, no longer a known quirk. **[BLOCKER]**
- [ ] 6.2 **Save files live update-safe (LW-51/LW-29).** kills.json, legends.json, gunslinger.json
  sit under Reloaded `User/Mods/prawl.fft.livingweapons`, NOT the deploy folder, so a 2.2.2 to
  2.3.0 mod update cannot wipe them. **[MAJOR]**
- [ ] 6.3 **Deploy preservation round-trip (backlog LW-28 watch).** Across this pass's deploys:
  restore lines present each time, files intact afterwards, tally count unchanged. Any silent
  loss: capture the deploy transcript + %TEMP% state and stop deploying until understood.
  **[MAJOR]**
- [ ] 6.4 **Guard stand-down drill (optional, DEV OK, live-proven 2026-07-10; drill lane updated
  2026-07-14).** Only if guard code was touched since: on a DEV build, drop a marker FILE named
  `LW_FORCE_FINGERPRINT_MISMATCH` into the deployed mod dir AFTER the deploy (the env-var lane is
  dead on this box, LW-83; BuildLinked's clean step wipes the marker each deploy): one loud
  drill-tagged stand-down line naming the landmark with observed-vs-expected detail, one OS
  message box, a flight_*_standdown.jsonl archive (LW-53), zero writes for the session, and
  scan_logs exits 1 on that log. Delete the marker and redeploy prod after. **[MINOR]**
- [x] 6.5 (PASSED 2026-07-14, owner dev drill: every expected line byte-exact, negative control
  clean, drill log banked) **AnchorScan scout drill (LW-82, DEV OK by design).** Same marker-file drill as 6.4, run
  once the LW-82 build ships: after the drill stand-down at the title screen, the FILE shows the
  jobcommand anchor found at its pin BEFORE any save loads, the roster anchor not found plus an
  early summary (pass behavior pre-save, not a failure), and within about 15s of loading a save
  the roster line upgrades to found-at-pin with the sibling prediction line and one re-emitted
  summary. Then delete the marker and relaunch: normal arming and ZERO scout lines. This row is
  the flip evidence for the two 2026-07-14 LIVE_LEDGER AnchorScan premise rows. **[MAJOR]**

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
  wielder equips slowly (backlog LW-43); the LIVE_LEDGER roster-write row is still pending its
  flip, this box is its evidence. Ledger caveat (the 7.17 precedent): that row's "Blaster id 76"
  wording predates the move to Outrider Pistol id 71; correct the weapon id when flipping so the
  flip does not PROVEN-stamp a stale claim. **[MAJOR]**
- [ ] 7.20 **Support grants fire with readback=SET** (Gloomfang Concentration, Mortal Coil Attack
  Boost, Sanguine Gauche / Hushblade defense boosts): one grant line each in the file when the
  wielder fields; per-ability oracles in docs/DEV_TEST_RECIPES.md. **[MINOR]**
- [ ] 7.21 **Marks stay hidden everywhere (LW-35)** while legends.json keeps growing beside
  kills.json (collection on, display off; the Reliquary Phase 1 live pass itself is deferred past
  2.3.0, see Appendix A). **[MAJOR]**
- [x] 7.22 (PASSED 2026-07-14, owner live: Scholar's Ring equipped on a deployed unit produced
  NO marks, the disarm line is in the file, zero grant lines all day, id 260 count unchanged;
  LW-86 exited on this evidence) **Treasure Master ships DISARMED on 1.5.1** (owner decision 2026-07-14; removal is
  post-release, backlog LW-10). The dataset still carries the pre-1.5.1 build key (0x6A0F86A9
  vs live 0x6A3C5497), so the L0 gate stands the module down at startup, by design. Oracle:
  Scholar's Ring equipped on a DEPLOYED unit = NO marks, and the file shows the one-time WARN
  "Treasure marks are disarmed: the dataset was built for a different game build"; zero
  treasure writes all session. TreasureAlwaysOn stays default False. The file must also show NO
  "Granted a Scholar's Ring" line and the id 260 inventory count must not change all session
  (LW-86: the production grant is compiled out); flipping this box is LW-86's live evidence.
  Evidence banked 2026-07-14 (first PROD session): zero "Granted a Scholar's Ring" lines across
  the whole session log, and the disarm line appeared at 11:56:20 (DEBUG tier, idle variant, no
  ring fielded). Still owed before the tick: the ring-equipped-on-a-deployed-unit = NO-marks
  check. **[MINOR]**
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
- [ ] 7.26 **Holy Lance (id 104) Cavalier's Charge:** with the +3 wielder riding a chocobo, Speed
  reads natural+3 in battle; reverts on dismount and at battle exit; no other unit's Speed moves.
  Last verified 2026-06-27 (DEV), BEFORE the growth-locate and roster-walk reworks (eb76fe5,
  474d494) that rebuilt the machinery it rides; the LIVE_LEDGER mounted-grant row is still
  unflipped and this box is its flip evidence. (DEV OK) **[MAJOR]**
- [ ] 7.27 **Swiftedge (id 28) Afterimage:** the +3 wielder's Speed climbs +1 for each turn it
  acts, capping at +5; taking a hit resets the ramp; no other unit's Speed drifts (watch the unit
  card across two turns and one hit). Row 7.2's growth-formula check does NOT cover this weapon:
  the Afterimage hold owns its Speed lane. (DEV OK) **[MINOR]**
- [ ] 7.28 **Materia Blade (id 32) Ultima:** at ANY tier (no +3 grind; the hold is always on) the
  wielder's PA tracks current HP% (above natural at full HP, sagging below it when badly hurt,
  restored on heal; kill tier only raises the curve); only the wielder's PA moves; the file shows
  the hold lines without HP-flap flood (the backlog LW-76 watch). Row 7.2 does not cover this
  weapon either: the Ultima hold owns its PA lane. (DEV OK) **[MINOR]**
- [x] 7.29 (PASSED 2026-07-14, owner live: Red and Blue Mage compose on the pruned PROD deploy
  with zero hand edits, guard armed; Archer Gun access verified; the Equip Axes reforged note
  verified on screen, screenshot 14:00; LW-77 exited on this evidence) **Job-table collision
  prune holds (LW-77).** With Blue And Red Mages 2.0.2 enabled on
  the PROD deploy, Red Mage keeps its abilities with NO hand edit (the pruned JobData.xml lists
  no row 57); Blue Mage stays intact; the guard reads armed throughout. Bonus oracles: a generic
  Archer still shows Gun access (the widened equip list survived the prune) and the Equip Axes
  learn-screen description shows the reforged note (the key-460 ability.en.nxd cell landed).
  Note: this box's tick lands in or after the commit that exits LW-77 to the changelog (the
  LW-86 pattern); the ship notes must also tell in-place upgraders to delete the old mod folder
  first (a stale JobCommandData.xml from 2.2.2 silently retains the collision). Core evidence
  banked 2026-07-14 (first PROD session, owner): Red Mage abilities present with zero hand
  edits, Blue Mage intact, guard armed 11:56:20. Archer Gun access verified (owner, second
  session 2026-07-14). Still owed before the tick: the Equip Axes reforged-note eyeball.
  **[MAJOR]**

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
  cut; mod description current; ModDependencies lists BOTH fftivc.utility.modloader and
  reloaded.sharedlib.hooks (new hard dep this release, hosts the toast-delivery hook; without it
  Mod.cs degrades to a no-toast warning) and the packaged zip carries that ModConfig.json.
  **[BLOCKER]**
- [ ] 8.8 **Ledger + scope hygiene at ship:** docs/RELEASE_SCOPE.md boxes all ticked (several are
  done-but-unticked as of authoring; the section-2 prose was corrected to the shipped own-turn
  release when LW-46 landed); docs/TODO.md Now items exited to the changelog via owner flips
  (LW-69, LW-60); VERIFY_LIVE row 1 flipped (7.17). **[MAJOR]**

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
- **LW-28 intermittent preservation loss:** the round-trip usually holds, and BuildLinked now
  fails red on a lost preserved item (backup copies kept in %TEMP%); the loss cause itself is
  still uninvestigated. Eyeball every deploy anyway (0. Read first).
- **LW-42 dead slot0 excuses:** a long cast at mode 1/5 could accumulate the exit debounce and
  false-exit (row 4.6 is the watch); re-anchor is backlog.
- **LW-39 fingerprint twins:** two party units at identical level + HP/MaxHP make the Attack-card
  resolve fail closed to vanilla. By design until the fingerprint widens.
- **LW-57 first-open latency:** re-scoped INTO 2.3.0 by the owner (2026-07-11) and the fix
  SHIPPED the same day (9d347c9), AWAITING-LIVE. Verified cause was the census starving the
  repaint driver on the session's first battle plus silently-aborted sweeps (LW-38's warm
  cache, 3bcdadc, covers battles 2+ and holds). Rows 5.3 and 4.4 carry the live expectations.
- **LW-75 console demotion:** FIXED (f91e0d2, AWAITING-LIVE): a demoted coverage line now
  promotes to the console once when the armed latch rises; row 4.3 carries the expectation.
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
  Gun Slinger roster-write (7.19; correct its stale "Blaster id 76" wording to Outrider Pistol
  id 71 at flip time), the Cavalier's Charge mounted grant (7.26), the Attack-row rename (5.3),
  Benediction (7.15), the two AnchorScan premise rows (6.5); docs/VERIFY_LIVE.md row 1 rides
  7.17.
