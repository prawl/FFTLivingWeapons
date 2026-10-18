# FFTLivingWeapons 2.4.0 Release Smoke Test Plan

STATUS: CONTRACT (the 2.4.0 pre-ship smoke pass; owner flips every box in Part A, Part B runs scripted from the dev box with the game closed; the document is its own task list and carries no ledger id, owner ruling 2026-10-17)

*Plain version: before 2.4.0 ships, every feature added since the 2.3.0 tag (2026-07-16) gets
looked at once more, on purpose, with a written pass and fail signature so nobody has to argue
about what they saw. The pass is split in two. Part A is everything that needs the game running
and the owner's eyes. Part B is everything a script or a file read can prove, and it runs from
the dev box alone with the game closed. Work each part top to bottom; inside each part the sections that hold
a blocker come first, and inside each section the blockers come first. The tags order the work
only; a failing box of any weight is fixed before the tag (owner ruling 2026-10-17). Boxes are
flipped by the owner only, in Part B too (the script run reports, the owner ticks).*

Modeled on docs/archive/SMOKE_TEST_2.3.0.md. Scope ground truth is docs/RELEASE_SCOPE.md (the R5
close out order). The denominator is every docs/CHANGELOG.md row dated 2026-07-16 or later plus the
docs/TODO.md rows that ship before this pass runs; Appendix C accounts for every one of those ids.
Rows that may run on a dev build carry **(DEV OK)**: dev thresholds are {1,2,3} and every weapon's
tally is floored to 3 on load, so every signature is awake at once; prod thresholds are {5,10,15}
with no seeding (LivingWeapon/Tuning.cs). Any box flipped on dev evidence should say so.

---

## 0. Read first: how to run this pass (every trap here has bitten before)

- **Two folders, know which is which.** The DEPLOYED mod folder is
  `$env:RELOADEDIIMODS\prawl.fft.livingweapons`. It holds the DLL, `build_flavor.txt`,
  `livingweapon.log`, `livingweapon.prev.log`, `flight\` and is where every drill marker file goes.
  BuildLinked wipes it on every deploy and round trips `flight\` through a temp dir. The SAVE
  folder is `<Reloaded root>\User\Mods\prawl.fft.livingweapons` (one level above the Mods folder,
  then `User\Mods`). It holds `kills.json`, `legends.json`, `gunslinger.json`,
  `extended_inventory.json`, `pool_regions.json`, `run_flavor.txt` and `archive\`, beside
  Reloaded-II's own `ModUserConfig.json` and `Config.json`. A deploy never touches the save
  folder (LivingWeapon/Persistence/SaveLocation.cs resolves it; BuildLinked cleans only the
  deployed folder). Ids 261 to 267 (Terrastaff, Ravager, Sunderer, Warbrand, Bloodlash,
  Climhazzard, Sasori) are the mod's own weapons; their per save counts live in
  `extended_inventory.json` in the save folder.
- **Flavor.** A PROD deploy is `.\BuildLinked.ps1 -Prod`; a plain `.\BuildLinked.ps1` is DEV. A
  dev build floors EVERY weapon's count in the real `kills.json` to at least 3 (the header line
  `Development build: every weapon's tally is seeded to at least 3 kills; every weapon starts with
  its full powers for fast testing.`) and saves that at battle exit. A plain dev deploy REFUSES to
  stomp a prod install without `-Force`; crossing back dev to prod prints a yellow
  `WARNING: the existing install is DEV-flavored` and keeps the seeded tally. The launch header
  says which flavor is running (`production build` or `development build`).
- **Sitting plan (so the real save is never polluted).** Kill `fft_enhanced.exe` before every
  deploy and every file edit below.
  Sitting 1, PROD, real save: `.\BuildLinked.ps1 -Prod`; boxes 1.1 to 1.5, 3.1 (Ramza's grown knight sword is on the real save), 2.5 prod leg, 2.7 normal
  leg, 6.2 (its card and file reads come right after Continue, before any battle is fought), 4.2's
  count read (the seven design cards, same launch), 3.3, 6.5, 6.6, 6.1, 6.4, 10.1, 10.2, 10.5, 11.1
  to 11.3, in that order. Before 3.3, with the game closed, copy
  `<save folder>\kills.json` to `%TEMP%\kills.prod.json`, then open `kills.json` and set one
  living weapon's count to 4 (a text edit, game closed, is allowed; the file is the mod's own flat
  map of weapon id to count, `{"42": 7, ...}`, LivingWeapon/Kills/KillTally.cs). Pick that weapon
  OFF the knight sword lane (ids 33, 34, 35, 36, 37, 262, 263 grow HP), so box 3.1 is never read on
  a tier that moved between battles (its accepted edge). In the same edit add the one old id line
  box 6.2 asks for. Then 6.5 and 6.6 run with the WHOLE save folder renamed aside and put back
  afterwards (their own steps say how), and only then 6.1 on the real tally.
  After 6.1 (New Game archives the tally), with the game closed, copy the archive the log named
  (`<save folder>\archive\kills.N.json`) back over `<save folder>\kills.json`. Never leave anything
  but `kills.N.json` (and its `.bak`) inside `<save folder>\archive\`: a stray name there makes the
  next New Game log `Failed to archive kills.json` and clear the tally WITHOUT archiving it
  (LivingWeapon/Persistence/SaveLocation.cs, the archive index parser).
  Sittings 2 and 3, DEV, throwaway: `.\BuildLinked.ps1 -Force`; section 2 (drills, 2.11 and 2.12
  included), 3.2, 3.4, 3.5, sections 4, 5, 7, 8, 9, 6.3, 6.7, 10.3, 10.4. The real file is already
  copied aside, so the seeding does no harm (legends.json is NOT copied aside; dev battles add
  deeds to the real one, which the release hides and 11.2 accepts).
  Last sitting: game closed, copy `%TEMP%\kills.prod.json` back over `<save folder>\kills.json`,
  `.\BuildLinked.ps1 -Prod`, one battle, the closing scan, then Part B box B14.
- **Before every launch of this pass.** Task Manager shows no `fft_enhanced.exe` (a leftover
  process shares the log file and the tally with the new one, and the sitting's evidence is two
  sessions interleaved; Steam blocks a second launch, a Reloaded launch does not). Write down the
  launch method (the Reloaded-II Launch button, or Steam with an auto inject shim) and the mod
  list and order that were enabled; box B20 banks both. Launch one sitting 2 leg through the
  other method and read box 1.2's header there.
- **Drills leave marker files, and BuildLinked's clean step deletes them.** Every parts model
  drill in section 2 is armed by dropping an empty file (`LW_FORCE_ROSTER_MISMATCH`,
  `LW_FORCE_BAND_MISMATCH`, `LW_FORCE_TABLE_MISMATCH`, `LW_FORCE_TWIN_MISMATCH`,
  `LW_FORCE_SENSING_MISMATCH`, `LW_FORCE_GUARD_UNREADABLE`, `LW_FORCE_FINGERPRINT_MISMATCH`) into the
  DEPLOYED mod folder AFTER the deploy (LivingWeapon/Guard/DrillTrigger.cs). The env variable lane
  does not reach the game on this box. Cleanup is always "redeploy"; a marker you forget makes the
  next normal leg look broken.
- **The parts report line is the new truth.** Since LW-393 the mod is fourteen parts, each with
  its own check. The log prints `Parts report: <part> ON|OFF|WAITING (<reason>); ...` on every
  change, and once every part is ON it prints `Living Weapons: all parts armed.` instead. The
  thirteen names on prod, in report order: battle sensing, kill counting, stat growth, card paint,
  weapon powers in battle (Maim, Plague and the rest), weapon commands, banners, extended
  inventory, twin weapons, bulwark, gun growth, poach den, provoke (a dev build adds weapon colors
  after bulwark, LW-446). Glossary in docs/LOGGING.md.
  Every check runs ONCE per session and is then final (LivingWeapon/Guard/FingerprintGuard.cs
  never re probes after arming), so a mod or patch that changes the game AFTER a check has passed
  is not seen until the next launch; the parts model is a startup check, not a watchdog. Seven of
  the thirteen doors have no drill (static array, terrain, poach store, provoke tables, item stats,
  the prompt code check, the new item data check); their closure copy is unit tested only and
  their live evidence is the negative read (no WARN naming them, 1.3's all armed line), except the
  new item data check, which box 2.12 forces by hand.
- **The paused part message box.** A real closure shows ONE OS box per closure WAVE, headline
  `Living Weapons: some features are paused` (or `Living Weapons has paused itself for this
  session` when nothing runs), lists Still working and Paused this session by part name, may carry
  a third group `Still being checked (as the game loads and your first battle starts):`, and ends
  with the footer `Living Weapons <version>, game build 0x<stamp>`. A clean run shows no box at
  all. The box rule (LivingWeapon/Guard/PartsBoard.cs): closures landing within about three
  seconds of each other share ONE box; a closure that lands AFTER a box has shown opens a SECOND
  box on a correct build (box 2.11 stages it); the one minute blind box (2.7) is a separate notice.
  Two boxes for one closure wave is a FAIL; a box with no new closure behind it is a FAIL; a
  second box for a later closure is correct. The footer version is whatever the deployed
  ModConfig.json says at that sitting (2.3.4 on a drill sitting before the B8 bump), and
  `Living Weapons unknown` in a footer is a FAIL (the deploy lost ModConfig.json).
- **The game lost Denuvo and gained ASLR (LW-387, LW-388).** The exe no longer loads at
  0x140000000. Every launch header now opens with `Game image base 0x... (preferred 0x140000000,
  delta 0x...); every pinned address is rebased onto it.` A nonzero delta is normal. The fallback
  line "using the preferred base" is a FAIL.
- **Evidence lives in the FILE.** `livingweapon.log` (rotated to `livingweapon.prev.log` each
  launch, both in the deployed folder) carries every line including `DBG`; the console shows Info
  and above only. When a row says "log", grep the file. Do not click into the console mid battle
  (QuickEdit stalls the mod thread).
- **Flight tapes flush on battle EDGES.** To bank a tape, END the battle (win, lose, flee); a
  hard process kill loses the in memory ring. Drill stand downs also flush their own
  `flight_*_standdown.jsonl`. Tapes land in `<deployed folder>\flight\`; read them with
  `python tools/parse_flight.py <file>`.
- **After every deploy, eyeball preservation.** The restore line must print AND `flight\` must
  still exist under the DEPLOYED folder, AND `kills.json`, `legends.json`, `gunslinger.json`
  must still exist under the SAVE folder (a deploy cannot touch them, so a loss there is a
  different bug). BuildLinked fails RED on a lost preserved item. On an install that has not yet
  fought a battle, earned a deed or minted a twin, the matching file does not exist yet (the
  mod creates each on its first event); the loss to catch is a file that EXISTED before the
  deploy and is gone after it.
- **A stall is the mod's only when the file says so.** A multi second freeze counts against box
  5.7 only when a `LW37 locate-complete` line with a large ms figure, or an ERROR, sits at the
  stall's timestamp. Otherwise check GPU clocks and disk latency first; both have faked a mod
  stall on this box before.
- **Unexplainable weirdness: bisect the mod list FIRST** (docs/DEV_TEST_RECIPES.md step zero).
  FFTHandsFree crashes the Denuvo free build, so `give_all_items` is not available, and no script
  in this repo writes a bag; there is no full bag rig this release (box 4.3 says what that costs).
- **Closing scan.** Every live sitting of this pass ends with `python tools/scan_logs.py
  --require-battle --flight` reading exit 0 (0 = clean, 1 = runtime failure, 2 = could not run).
  A drill sitting may carry the `standdown-drill` warn code and still read exit 0.

---

# PART A. Owner only (the game running, the owner's eyes)

## 1. Ten minute smoke (prod deploy, header, parts armed, one battle, scan)

> Goal: a clean prod deploy, the new launch header, every part ON after the first battle, one kill
> round trip, green scan. Every box here is a BLOCKER.

- [x] 1.1 (PASSED 2026-10-17 on main 1c7ba75f: BuildLinked -Prod every stage green, 4134 tests, parity 506/506 byte identical, preservation restore and post restore check passed, build_flavor.txt prod; the plain run printed REFUSING TO DEPLOY ... holds a PROD-flavored install, evidence source marker, exit 1, DLL timestamp unchanged) **Deploy prod, and the dev guard refuses to stomp it.** The install is a release build,
  nothing it preserves was lost, and a careless dev deploy over it is refused. `.\BuildLinked.ps1
  -Prod`: every stage green (generate, analyze gate, meta bake, meta selftest, names selftest,
  scan_logs selftest, recolor selftest, deploy parity selftest, format gate, full test suite, DLL
  publish, deploy). Then, with the shell still open and the game still closed, a plain
  `.\BuildLinked.ps1` with no flags. PASS: `build_flavor.txt` reads prod, the preservation restore
  line prints, the deploy parity report names zero differing and zero missing files, no
  `REFUSING TO DEPLOY` on the prod run; the plain run prints `REFUSING TO DEPLOY: evidence says
  ... holds a PROD-flavored install` and exits 1 before touching a file. FAIL: any red stage, a
  differing file in the parity report, a missing preserved file afterwards, or a green dev deploy
  over prod. (covers LW-297, LW-247, LW-134 refusal half) **[BLOCKER]**
- [ ] 1.2 (PARTIAL 2026-10-17, prod 1c7ba75f, owner launch: image base 0x7FF65FC60000 delta 0x7FF51FC60000 rebased; production build; Save files live at the Reloaded User\Mods folder; kills.json and legends.json primary; stamp 0x6A5E4A1B the build this mod was made for; Extended inventory armed 7 new items. Version reads 2.3.4 until the ModVersion bump, the one clause owed) **Launch header on the Denuvo free build.** The mod knows where the game landed and
  which build it is. Launch, read the top of `livingweapon.log`. PASS: line one `Game image base
  0x... (preferred 0x140000000, delta 0x...); every pinned address is rebased onto it.` with a
  nonzero delta; `Living Weapons version 2.4.0 (production build) is starting inside
  fft_enhanced.exe.`; `Save files live at <Reloaded root>\User\Mods\prawl.fft.livingweapons.`
  where `<Reloaded root>` is the folder holding Reloaded-II.exe (the save folder fallback to the
  deployed folder is silent in code, so this line is the only tell); the tally and legends lines
  read `(kills.json, primary)` and `(legends.json, primary)` on the owner's save (a brand new
  install reads `fresh` there, box 6.5); `Game build stamp: TimeDateStamp 0x6A5E4A1B, SizeOfImage
  0x03F9E000; the build this mod was made for.`; `Extended inventory armed: 7 new item(s)
  [Terrastaff (id 261), ... Sasori (id 267)]`. FAIL: "using the preferred base", "not a build this
  mod knows", a version other than 2.4.0, "development build", a `Save files live at` path inside
  `$env:RELOADEDIIMODS`, or no extended inventory armed line. (covers LW-387, LW-388, LW-397,
  LW-132) **[BLOCKER]**
- [x] 1.3 (PASSED 2026-10-17, prod 1c7ba75f, owner live: kit lane and sensing lines at 10:12:07, sensing within 0.2 s of the header, zero boot grace lines; armed 10:12:23 after Continue; all parts armed at 10:13:06.602 with Battle started at .603; no box, no stand down) **Every part arms, no box.** Continue a save, enter a battle. PASS: `Every memory
  landmark matches; Living Weapons is armed (writes were held until now).` within seconds of
  Continue, then `Living Weapons: all parts armed.` within a second of `Battle started.`; zero
  message boxes; no `standing down to protect your save`; the kit lane line `The job command data
  matches this mod's own copy; the weapon commands (Barrage, Shadow Blade and Provoke) have passed
  their check and run once the party data check passes too.` and the sensing line `The battle
  sentinels read as expected; the new game detector is enabled.` both present, the sensing line
  within a second of the launch header and NO DBG line ending `(boot grace)` anywhere in the log
  (the sentinels read right at boot on this build). FAIL: any part OFF on a clean launch, a box, a
  `(boot grace)` line on a clean launch (the sentinels read wrong at boot; file a row, do not ship
  on the hold), or a part still WAITING a minute after the first battle starts, EXCEPT battle
  sensing when Continue resumed a mid battle autosave (it waits by design until the first out of
  battle frame; box 5.10 leg B is that read). (covers LW-393 leg B, LW-406 all armed line)
  **[BLOCKER]**
- [x] 1.4 (PASSED 2026-10-17, prod 1c7ba75f, owner live: Chaos Blade claims kill number 9, felling a human at (1,10); Battle ended, 1 kill credited (Chaos Blade 1), 0 Marks earned, 0 tiers reached, 6 turns; the owner read the card count go up) **One battle round trip.** A kill counts and shows on the card. PASS: `Battle started.`,
  the credit line `<weapon> claims kill number N, felling <victim> at (x,y).`, a battle end summary
  of the shape `Battle ended: 1 kill credited (Chaos Blade 1), 0 Marks earned, 0 tiers reached; 3
  turns; the kill tally and legends are saved.` (the numbers and names vary; `kill` and `kills`
  pluralize), and the equip card shows `Kills: N/T to +` with the new count. FAIL: `Battle ended:
  no kills were credited` after a living weapon kill, or a card that never paints. **[BLOCKER]**
- [x] 1.5 (PASSED 2026-10-17: scan_logs --require-battle --flight exit 0, RESULT CLEAN, 0 warnings, 0 errors, last parts report all parts armed, tape flight_20261017_101537_battle-exit) **Scan green.** The closing scan finds nothing wrong with the sitting. `python
  tools/scan_logs.py --require-battle --flight`. PASS: exit 0, the summary reads `last parts
  report: Living Weapons: all parts armed.`, 0 errors. FAIL: exit 1 or 2. **[BLOCKER]**

---

## 2. The parts model and its drills (BLOCKER: the release's safety story)

> Plain version: one broken startup check now pauses only the parts that need it, says so once in
> the log and once in a box, and the rest of the mod keeps working. Each drill forces one check
> wrong on a healthy game so the owner can watch exactly that happen. Recipes, exact copy and the
> FAIL maps live in docs/DEV_TEST_RECIPES.md. One dev sitting, seven marker files; the real
> `kills.json` is already copied aside by the sitting plan. Run the first drill leg of the sitting
> in the display mode you actually play in (fullscreen) and one later leg windowed or borderless;
> box 2.10 holds the box visibility reads.

- [x] 2.1 (PASSED 2026-10-17 on dev 62015755, owner live: leg A with the marker, title report WAITING, the ERROR with the drill sentence at Continue, eleven parts OFF, one box listing battle sensing, banners and extended inventory as still working, the prompt hook confirmed, the new items listed, the kill not credited, scan CLEAN with the standdown drill warn; leg B with the marker gone, all parts armed, no box, no new game detection, Chaos Blade claims kill number 11) **Roster drill (LW-393).** The party data check fails; eleven parts stop, three keep
  going, one box. Marker `LW_FORCE_ROSTER_MISMATCH`, recipe legs A and B. PASS leg A: title screen
  report reads battle sensing, extended inventory, banners ON and eleven parts `WAITING (waiting
  for a save to load)`; Continue gives the ERROR `The party data is not where this mod expects it,
  so the parts that need it are standing down to protect your save; see the Parts report line for
  what still runs.` carrying `This stand down was forced by the LW_FORCE_ROSTER_MISMATCH drill flag
  (dev builds only).`; eleven OFF; exactly one box naming banners and extended inventory under
  Still working; a kill is NOT credited; `The prompt hook is confirmed working` present; extended
  items still listed in shop and inventory; `python tools/scan_logs.py` exit 0 with one
  `standdown-drill` warn. PASS leg B (marker gone): fourteen ON after the first battle, no box, a
  kill credited, no new game detection on Continue. FAIL: a second box, extended items missing
  under the drill, a kills.json archive on a plain Continue. (DEV OK) (covers LW-393, LW-406)
  **[BLOCKER]**
- [x] 2.2 (PASSED 2026-10-17; legs B and D on dev 62015755 in one story battle with 30 s on the formation screen: both armed lines within 2 ms of Battle started, no battle grace line, all parts armed, no WARN, no box, Chaos Blade claims kill number 12, scan CLEAN; leg A as recorded below; REAL WORLD FALSE CLOSE 2026-10-17 in sitting 1, FIXED by LW-425 46db4966 and owner read on a cold launch the same day, tutorial waits quietly and Gariland arms; leg A PASSED 2026-10-17 on dev 62015755, owner live: the battle grace DBG line and the unit list armed line within a second of Battle started, the WARN 59 s later with detail band-region (drill: LW_FORCE_BAND_MISMATCH), three OFF and eleven ON, one box seen by the owner, scan CLEAN; the kill read was skipped since kill counting was OFF, as the roster drill already showed; legs B and D passed as above: a New Game tutorial battle closed the band door on healthy memory, detail none mirrors a party row (rows checked 1), and showed the box; LW-425 owns the fix; re run this box and the tutorial leg after it lands) **Band door drill (LW-398).** Kill counting, stat growth and the battle powers wait for
  the first battle and switch off alone under the drill. Marker `LW_FORCE_BAND_MISMATCH`, recipe
  legs A, B and D (D is mandatory). PASS leg A: before battle the three read `WAITING (waiting for
  the first battle)`; within a second of `Battle started.` the unit list armed Event and one DBG
  line ending `(battle grace)`; about twenty one seconds after the cursor lands on the field one
  WARN `Kill counting, stat growth and the weapon powers in battle are paused for this session
  because the live battle unit data is not where this mod expects it...` ending `(detail:
  band-region (drill: LW_FORCE_BAND_MISMATCH))`, three OFF and eleven ON, one box, a kill not
  credited, scan exit 0. PASS leg B: both armed Event lines within a second of `Battle started.`,
  no `(battle grace)` line, fourteen ON, kill credited. PASS leg D (a story battle, thirty seconds
  on the formation screen): no WARN, no box, both armed lines, fourteen ON. FAIL: a box inside the
  first twenty field seconds, a WARN on the formation screen, two boxes for this one closure (a
  second box here means the unit list closed too). (DEV OK) (covers LW-398) **[BLOCKER]**
- [x] 2.3 (PASSED 2026-10-17 on dev 62015755, owner live. Leg A: the palette WARN 1.6 s after the header with detail palette-clear-site (drill: LW_FORCE_TABLE_MISMATCH), one box seen, every other part ON after Continue, the Cutpurse swung plain steel, scan CLEAN; the box text expected a refusal line that cannot print while the part is OFF and now says so. Leg B: no WARN, all parts armed, Painted weapon 1 into palette 14 on each swing and the owner saw the color back, the Stoneshooter WP bump restored to 16 each turn end, The Defender's provoke takes hold with no setup WARN, scan CLEAN. The Sunderer plant under the drill was not run and rides box 9.4) **Table door drill (LW-399).** Five fixed game tables each own a check; the weapon palette
  check fails alone. Marker `LW_FORCE_TABLE_MISMATCH`, recipe legs A and B. PASS leg A: within two
  seconds of the header one WARN `Weapon colors are paused for this session because the weapon
  palette is not where this mod expects it; everything else keeps working.` ending `(detail:
  palette-clear-site (drill: LW_FORCE_TABLE_MISMATCH))` with no expected bytes printed; report
  reads weapon colors OFF and the roster parts WAITING; exactly one box naming weapon colors alone
  under Paused this session; after Continue every part ON but weapon colors; in battle a seeded
  Cutpurse (id 1) swings PLAIN steel and the file carries NO palette write line (the weapon colors phase never ticks while its part is OFF, so the latch refusal `Weapon colors were not repainted; ...` cannot print here; it only guards the race where the part is ON and the door closes underneath), the Stoneshooter (id 73) shot carries its +3 WP bump, a
  Sunderer full wait still plants; scan exit 0 with no stand down finding. PASS leg B: no table
  WARN of any kind at the title, fourteen ON after Continue, the Cutpurse REPAINTS, the Stoneshooter
  bump holds, a Defender wielder gets the Provoke grant with no `Provoke's effect was not set up`
  WARN. FAIL: a WARN naming any other door (a real closure, stop and run `python -I
  tools/probes/static_table_file_image_probe.py`), a repainted Cutpurse under the drill, provoke
  or gun growth still WAITING a minute after Continue, a second box. (DEV OK) (covers LW-399;
  LW-251 and LW-123 each get a leg B read here) **[BLOCKER]**
- [x] 2.4 (PASSED 2026-10-17 on dev 62015755, owner live. Leg A 2026-10-17 on dev 62015755, owner live: armed 13:17:31, the twin WARN 1.6 s later with detail twin-menu-flags, one box naming twin weapons alone, no second pistol on the status page, Outrider Pistol claims kill number 4, the standdown tape carries the twin lane record, scan CLEAN. Leg B: the menu flags line in the same millisecond as the armed line, A twin weapon is equipped 1.6 s later and the owner saw the second pistol, no box, scan CLEAN) **Twin weapons drill (LW-400).** The twin weapon grant owns a report line and switches
  off alone. Marker `LW_FORCE_TWIN_MISMATCH`, recipe legs A and B. PASS leg A: no twin WARN before
  Continue; after Continue the armed line, then one WARN starting `The twin weapon grant is paused
  for this session because the two menu flags it uses to stay out of the equipment screens do not
  read the way this mod expects` ending `(detail: twin-menu-flags (drill: LW_FORCE_TWIN_MISMATCH))`;
  twin weapons OFF with the rest ON; exactly one box with twin weapons alone under Paused this
  session; a tier 3 Outrider Pistol equipped on a clean unit stamps NO twin and logs no `A twin
  weapon is equipped`; a kill is credited; the newest `flight_*_standdown.jsonl` carries `twin lane
  stand-down`. PASS leg B: `The menu flags read as expected; the twin weapon grant is enabled.`
  within the same second as the armed line; Status visit stamps the twin with `A twin weapon is
  equipped in the wielder's off-hand; their original gear is remembered and returns when the weapon
  comes off.`; no box. FAIL: a twin WARN before Continue, an ERROR line, kill counting OFF, a box
  listing the roster parts, twin weapons WAITING a minute after Continue. (DEV OK) (covers LW-400)
  **[BLOCKER]**
- [x] 2.5 (PASSED 2026-10-17: prod leg on 1c7ba75f, every F key pressed in battle, nothing on screen and no dev line in the log; dev leg on 62015755, provoke-spike armed (dev) at battle start and F6 marked the first live enemy at tile (3,7), composed and inflicted) **No developer hotkey fires in a release build (LW-413).** A player can never trip a dev
  key. Prod leg (sitting 1): in battle, press F6 and F5 (the two wired dev keys). PASS:
  nothing happens on screen, no `provoke-spike` line and no Provoke mark appears in the log. Dev
  leg (sitting 2): press F6 in battle; the drill fires (the log carries `provoke-spike: armed (dev)`
  at battle start and a mark lands on the hovered enemy). FAIL: any dev key doing anything on prod,
  or the dev drill dead on dev. The offline half (no GetAsyncKeyState string in the prod DLL) is
  box B5. **[BLOCKER]**
- [x] 2.6 (PASSED 2026-10-17 on dev 62015755, owner live: launch 13:23:24, the boot grace DBG line at once, the sensing WARN with the drill sentence at 13:23:49 (25 s after the header, which the box text first tied to the armed line and now ties to the launch), no box, battle sensing OFF and only the new game detector off; a New Game logged A new game signal was seen but the battle sentinel check has not passed, so the kill tally was left untouched, no new game line, kills.json still 384, archive count still 17, scan CLEAN; the normal leg is box 6.1) **Sensing drill (LW-395).** The new game detector refuses to clear kills until the battle
  sentinel check passes. Marker `LW_FORCE_SENSING_MISMATCH`; Continue from a WORLD MAP save, never a
  mid battle autosave (the lane waits silently while a battle is live and the drill looks dead).
  PASS drill leg: about 25 seconds after the launch header (the sensing lane starts at boot, holds a mismatch through its boot grace, then debounces; the armed line is not its clock) one WARN `The new game detector is paused for this session because the game's
  battle status data does not read the way this mod expects; ...` with the drill sentence, no box;
  a New Game through the prayer logs `A new game signal was seen but the battle sentinel check has
  not passed, so the kill tally was left untouched (nothing archived, nothing cleared).`, no
  `A new game was detected` line, kills.json unchanged. PASS normal leg (prod, box 6.1): the
  sensing line right after the header and a real New Game archives. FAIL: kills archived under the
  drill, or a box. (DEV OK) (covers LW-395) **[MAJOR]**
- [x] 2.7 (PASSED 2026-10-17. NORMAL LEG 2026-10-17, prod 1c7ba75f, owner live, the attract cutscene played during the idle: launch 10:18:43, exactly one wait line at 10:21:14, no box, no WARN, no recovery line on Continue; four Parts report lines before Continue, changing only by extended inventory and banners going WAITING to ON within 1.8 s of launch, which the box text first called a fail and now allows. DRILL LEG on dev 62015755, owner live: launch 13:25:46, the cannot read WARN at 13:26:56 and the owner saw the cannot find the game box, on Continue the armed line then Living Weapons found the game after the earlier warning in the same millisecond, scan CLEAN; the box landed at 70.4 s, a hair past the 70 s the box text first allowed, so the text now allows 80) **Blind drill (LW-392).** When the mod cannot read the game for a minute it says so once,
  then recovers. Marker `LW_FORCE_GUARD_UNREADABLE`, sit on the title about 80 seconds, then
  Continue. PASS drill leg: at about 60 seconds one WARN `Living Weapons has not been able to read
  the game for about a minute, so its checks cannot run and nothing is counting yet. ...` ending
  with the drill sentence, one box headlined `Living Weapons cannot find the game yet` ending
  `Living Weapons <version>, game build unknown`; on Continue the armed line then `Living Weapons
  found the game after the earlier warning; all checks passed and it is armed now.` PASS normal leg
  (prod, 2.5 minutes on the title): exactly one wait line `Living Weapons can see the game and is
  waiting for a save to load before it runs its checks; this is normal on the title screen.` at
  about two minutes, no box, no recovery line on Continue; the only other lines after the header
  are `Parts report:` lines, at most a handful, each differing from the one before only by a part moving from WAITING to ON (banners and extended inventory flip within a second of launch when the loader hands over its hooks; the table checks arm at the title),
  no WARN. The drill box's `game build unknown` footer is correct only there. Scan exit 0 both
  legs. FAIL: a box on the normal leg, no box by 80 seconds under the drill (the stall clock runs about a minute of unreadable steps and the box lands near 70 s after the header), a recovery line with
  no prior warning, a wait line before 90 seconds or a second one, a Parts report line before Continue in which a part moves anywhere but from WAITING to ON. (DEV OK drill; normal leg prod) (covers LW-392)
  **[MAJOR]**
- [x] 2.8 (PASSED 2026-10-17 on dev 62015755, owner live. Main leg: the stamp line read not a build this mod knows, the armed line, one WARN This game build is new to the mod, its own checks passed, running normally, no box, Chaos Blade claims kill number 13, scan CLEAN. Patch day leg with both markers: the palette WARN carried This game build is new to the mod, so the game update itself is the likely cause. right after the shared hedge and its detail tail grew the new build clause, the owner saw the one box with the same sentence. The box text expected the two stamps to differ by one under the drill; they print the same because the drill flips only the comparison, so the text now says so) **Unknown build stays running (LW-397).** An unknown game build is reported, never a
  reason to stop. Marker `LW_FORCE_FINGERPRINT_MISMATCH`, launch, Continue, one kill. PASS: `Game
  build stamp: ...; not a build this mod knows (it was made for ...)` in the header, the armed
  line, exactly one WARN at the armed edge opening `This game build is new to the mod, its own
  checks passed, running normally.` naming the drill, no box, a kill counted. The prod leg is box
  1.2's stamp line. Patch day leg (the copy a player sees the day a game update lands, never shown
  live yet): drop `LW_FORCE_FINGERPRINT_MISMATCH` AND `LW_FORCE_TABLE_MISMATCH` together, launch,
  sit at the title ten seconds, read, no Continue needed. PASS: the stamp line reads `not a build
  this mod knows`; the palette WARN carries `This game build is new to the mod, so the game update
  itself is the likely cause.` right after the shared hedge and its detail tail grows `; this game
  build is new to the mod (TimeDateStamp ..., made for ...)` where under the drill the two stamps read the SAME (the drill flips only the comparison's expected stamp, LivingWeapon/Guard/LaunchGuard.cs:54, while the made for text prints the true expected constant; a real game update shows two different stamps); the one
  box carries the same new build sentence after the shared hedge. FAIL: a stand down, a box on the
  plain leg, no stamp line, or a WARN or box without the new build sentence while the stamp line
  says unknown. (DEV OK) (covers LW-397) **[MAJOR]**
- [x] 2.9 (PASSED 2026-10-17, owner live, dev deploy of the LW-431 fix (624926a9 plus the uncommitted LW-430/LW-431 tree) with bluemage enabled: leg 1 the loader applied JobCommandData at 16:49:15, armed 16:49:48.606, kit lane match 16:49:48.607, Barrage granted to the Yoichi Thief 16:50:04, no kit lane WARN; leg 2 drill ID 8 applied 16:52:03, armed 16:52:29.122, kit lane WARN 16:52:30.615 with `rec8: expected 96-97-98-99-9A-9B-9C-9D, observed 64-65-66-67-68-69-6A-6B`, one box (tape `parts box shown (2 parts off)`, owner screenshot 16.52.47), `kit-lane stand-down` on flight_20261017_165230_standdown, Barrage gone, Archer Aim shows Monk moves, Chaos Blade kill 15 at 16:55:04. Earlier PARTIAL on dev 62015755: leg 1 passed, the job command match line at 15:56:20, armed at 15:56:35, Barrage granted to the Yoichi Bow Thief at 15:58:13, zero WARNs, zero boxes, scan CLEAN. Leg 2 FAILED 2026-10-17: the drill table landed (the Archer showed Monk moves, the Reloaded log wrote JobCommandData ID 8 at 15:59:44) but the kit lane had passed at 15:59:42.951, so no WARN, no box and Barrage still on the Thief; logged as LW-431, retest after the fix) **Kit lane drill (LW-112, two legs).** Another mod rewriting the job command table pauses
  only the weapon commands, not the mod. The check runs once per session and waits until a save
  has loaded (LW-431), so the mod loader has already written the other mod's table when it looks.
  Recipe "LW-112 kit-lane drill" in
  docs/DEV_TEST_RECIPES.md (a throwaway `lwdrill.jobconflict` mod carrying a changed Aim record).
  Keep fftivc.jobs.bluemage ENABLED for both legs; that launch's Reloaded log must show an
  `Applying JobCommandData` line or the leg proves nothing.
  PASS leg 1 (bait, no conflict mod): on the title no kit lane line and no WARN, the Parts report
  shows `weapon commands WAITING (waiting for a save to load)`; after Continue the armed line, then
  within about a second the kit lane line of box 1.3, Barrage present in a tier 3 Yoichi Bow
  Thief's action list (Barrage is Thief only; an Archer in the party shows the Aim list the drill
  rewrites), zero boxes, zero WARNs. PASS leg 2 (conflict mod enabled): on the title no kit lane
  line, no WARN, weapon commands and provoke WAITING; after Continue the armed line, then within
  about a second and a half ONE WARN opening `The weapon commands (Barrage, Shadow Blade and
  Provoke) are paused for this session because another mod rewrote the job command data` with a
  `(detail: jobcommand-table (rec8: expected 96-97-98-99-9A-9B-9C-9D, observed 64-65-66-67-68-69-6A-6B))`
  tail, a Parts report with weapon commands OFF and provoke OFF, then a few seconds later exactly
  one box with weapon commands and provoke under Paused this session, kill counting, stat growth
  and weapon powers under `Still being checked (as the game loads and your first battle starts):`,
  the rest under Still working, and the footer last; Barrage ABSENT from the Yoichi wielder, the
  Archer shows Monk moves in Aim, a kill still bumps the tally, `flight_*_standdown.jsonl` carries
  `kit-lane stand-down`. FAIL: the job command match line anywhere in leg 2, two boxes for this one
  closure, no footer, Barrage still grantable under the WARN, a frozen tally, no WARN within five
  seconds of the armed line, any kit lane WARN or box in leg 1 (the loader's own writeback changed
  the checked bytes, which would hit every player with a job command mod), or (leg 2 trap) the
  Archer shows vanilla Aim with no WARN, which means the modloader dropped the drill table and the
  leg proved nothing. Cleanup: disable and delete the
  drill mod. (DEV OK) (covers LW-112, the only guard lane whose drill is a throwaway mod rather
  than a marker file) **[MAJOR]**
- [x] 2.10 (PASSED 2026-10-17, owner live on dev 181da02d: fullscreen leg, roster drill box on screen over the game without Alt Tab (owner screenshot 17.09.11, hedge, what to do, drill sentence, footer last), log kept ticking after it (stand down 17:08:47, Parts report 17:09:41), OK returned focus, keyboard and controller worked; tape flight_20261017_170956_battle-start carries one `parts box shown (11 parts off)`; marker removed, fullscreen relaunch 17:11:45 showed zero boxes and zero box records on three tapes. Earlier on dev 62015755: windowed leg passed in the 2.11 run, both boxes on screen over the game without Alt Tab, OK returned focus, keyboard and controller worked; the palette WARN and roster ERROR carry hedge, what to do, drill sentence and detail tail last, zero old wording; box records equal boxes seen. Kit lane box seen windowed 2026-10-17 in box 2.9 leg 2 on the LW-431 fix (owner screenshot 16.52.47, tape `parts box shown (2 parts off)`, hedge then `Your game and save are fine.`, two paragraph what to do, footer last)) **Every paused message reads the same way, and the box is really seen (LW-406).** Across
  every drill above, each DOOR closing WARN or ERROR has the same shape: what paused and why in
  player words, `This usually means the game just updated, or another mod changed the same data.`,
  `What to do: check the Living Weapons mod page for a newer version; ...`, the drill sentence, then
  `(detail: ...)` last. Every parts box carries `Your game and save are fine.` after the hedge,
  then the two paragraph what to do, the drill sentence, and the footer `Living Weapons <version>,
  game build 0x6A5E4A1B` where the version equals the deployed ModConfig.json's ModVersion at that
  sitting (2.3.4 before the B8 bump, 2.4.0 after). Three shapes are deliberately different and are
  NOT a fail: the two latch refusals (`Weapon colors were not repainted; the palette data is not
  where this mod expects it.` and `The gun's growth was not applied; the item data is not where
  this mod expects it.`) are one plain line each, printed once per session and only while their
  part is OFF; a message whose cause a game update cannot explain (bad shipped data, a loader
  error, box 2.12) carries no hedge at all; the blind box (2.7) says `Your game is fine and nothing
  has been changed` and has no part list. Box visibility: on the first drill box of the sitting,
  the box is on screen WITHOUT Alt Tab in the display mode you play in, the game keeps animating
  behind it and the log keeps ticking (a `Parts report:` or DBG line timestamped after the box
  appeared), OK dismisses it, focus returns to the game, keyboard and controller input work, and
  the box never reappears on a later launch with no new closure. Box count by machine: after each
  drill sitting, `grep -o "parts box shown ([0-9]* parts off)" <deployed folder>\flight\*.jsonl`
  lists one record per box seen, with the part count the drill predicts (11 roster, 3 band, 1
  table, 1 twin, 2 kit lane) and zero records on the clean legs. PASS: no parts box without a
  footer, nothing technical before the detail tail, zero boxes on the clean legs, every box seen
  in both display modes, records equal boxes. FAIL: a WARN in old wording ("update the mod",
  "wrong address", "switched itself off"), a door WARN without the detail tail, a latch refusal
  that repeats or prints while its part is ON, a parts box with no footer, `Living Weapons
  unknown` in a footer, a box found only after Alt Tab, the game minimized or input dead after OK,
  a record with no box seen or a box with no record. (DEV OK) (covers LW-406) **[MAJOR]**
- [x] 2.11 (PASSED 2026-10-17 on dev 62015755, owner live with screenshots: box one at the title with weapon colors alone under Paused, ten under Still being checked, both flag sentences and the footer; box two on the world map after Continue with eleven paused, three still working, no Still being checked group; Parts report eleven OFF three ON; scan CLEAN with one standdown drill warn; tapes 154801 `(1 parts off)` and 154846 `(11 parts off)`, battle start and end logged at 15:48:46 and 15:48:51) **Two doors down, two waves (the box rule).** When two different checks fail at
  different times the mod shows one box per failure wave, and the drill sentence names every flag
  that was on. Dev deploy, drop BOTH `LW_FORCE_TABLE_MISMATCH` and `LW_FORCE_ROSTER_MISMATCH`,
  launch, sit at the title ten seconds, Continue, wait ten seconds, one battle, flee. PASS: at the
  title the palette WARN and ONE box with weapon colors alone under Paused this session, the other ten
  roster parts under `Still being checked (as the game loads and your first battle starts):`, a
  drill paragraph carrying BOTH `This closure was forced by the LW_FORCE_TABLE_MISMATCH drill flag
  (dev builds only).` and `This stand down was forced by the LW_FORCE_ROSTER_MISMATCH drill flag
  (dev builds only).`, and the footer; after Continue the roster ERROR of box 2.1 and a SECOND box
  with all eleven roster parts (weapon colors among them) under Paused this session and battle sensing,
  banners, extended inventory under Still working, no Still being checked group; the Parts report
  after Continue reads eleven OFF and three ON; `python tools/scan_logs.py` exit 0 with one
  `standdown-drill` warn; the sitting's tapes carry exactly two `parts box shown` records
  (`(1 parts off)` then `(11 parts off)`). FAIL: one box only (the second wave never opened),
  three boxes, a drill paragraph naming one flag only, weapon colors missing from the second box,
  a second record reading anything but `(11 parts off)`.
  (DEV OK) (covers LW-406 the wave half) **[MAJOR]**
- [x] 2.12 (PASSED 2026-10-17, owner live, box retest on the dev deploy of the LW-430 fix (624926a9 plus the uncommitted LW-430/LW-431 tree), Ravager block deleted from the deployed file: WARN at 16:56:15 unchanged and plain, owner screenshot 16.56.28 shows the box body `A part of it found a problem in the files Living Weapons shipped with ... Reinstalling Living Weapons usually fixes this.`, no update hedge, no `different place`, `Your game and save are fine.` once, extended inventory alone under Paused; deployed file restored byte equal to the repo copy. Earlier PARTIAL on dev 62015755, Ravager row deleted: the WARN at 15:51:17 plain with no hedge and the detail `id 262: row without an ItemExtendedData.xml entry`, no armed line, extended inventory OFF with the shipped data reason, one box with extended inventory alone under Paused, thirteen ON after Continue, Chaos Blade kill credited at 15:54:32, no crash, scan CLEAN. FAIL on the box: it still says parts found the game data in a different place and carries the update hedge, logged as LW-430; retest after the fix) **The new items refuse bad data (the one undrillable door a hand edit can close).** If
  the mod's own item tables are damaged, the new items pause with a plain message and nothing else
  stops. Dev deploy; in the DEPLOYED folder only (the repo copy is generated), delete one
  `<ItemExtended>` block from `extended_inventory\ItemExtendedData.xml`; launch, sit at the title
  ten seconds, Continue, open the Items tab, one battle; redeploy to clean up. PASS: within seconds
  of the header one WARN `The new items are paused for this session because the item data shipped
  with this mod did not load correctly; everything else keeps working.` followed by the what to do
  and a `(detail: ...)` tail carrying `row without an ItemExtendedData.xml entry`, with NO `This
  usually means the game just updated` sentence; no `Extended inventory armed` line; the Parts
  report reads `extended inventory OFF (the new item data shipped with this build did not
  validate)`; one box (LW-430) whose body reads `A part of it found a problem in the files Living
  Weapons shipped with ... Reinstalling Living Weapons usually fixes this.` with no `different place`
  and no update hedge anywhere in it, extended inventory alone under Paused this session, battle sensing and
  banners under Still working, the roster parts under Still being checked; after Continue thirteen
  ON, ids 261 to 267 absent from every list, no crash, a kill credits. FAIL: an `Extended
  inventory armed` line (the validation let a missing row through), two boxes, the hedge sentence
  present, a crash on the Items tab. (DEV OK) (covers LW-406 the plain voice half) **[MINOR]**

---

## 3. Growth (the multi lane model, the knight sword top up, prod thresholds, mounted speed)

> Growth and signature rows are DEV OK unless noted (dev floors every tally to tier 3). Box 1.4
> does NOT cover Afterimage or Ultima; each owns its own stat lane (section 9).

- [x] 3.1 (PASSED 2026-10-17, owner live, DEV build of the fix ffd24810 on the real save: 883 of 883 on Ramza's first turn; log shows the first pour 679 -> 883 then two game refills each re poured before anyone acted, no refill after the first action, damage stuck at 653 of 883, scan CLEAN; re read on prod 1c7ba75f in sitting 1 the same day: prod tier 1 raised max 679 to 747 and the top up delivered 679 -> 747 of 747 within 0.2 s of Battle started, census 747 of 747 at mid and end of battle) **Knight sword fills the HP it adds (LW-416, the LW-327 regression).** A knight whose
  grown sword raised Max HP starts the battle with the bar FULL, even when a long opening cutscene
  makes the game rebuild him more than once. Ramza with a tier 3 knight sword (the owner's save:
  Chaos Blade or Excalibur), load from the WORLD MAP, enter a story battle with a long opening
  cutscene and watch all of it (do not skip), read HP on his first turn, take three hits, finish,
  Alt Tab out (never kill the exe), read the log and `python tools/parse_flight.py <newest
  flight>`; then one random encounter the same way. PASS: current equals max on turn one (883 of
  883 or whatever the raised max reads) in both battles; max stays pinned under hits and between
  battles; the file carries `located party slot 0 in memory; stat growth will apply`, one `HP
  top-up armed for party unit 1; max HP 679 raised to 883, current HP reads N` (the numbers are
  the owner's save), at least one `HP top-up delivered to party unit 1; current HP N -> M of 883`,
  and zero or more PAIRS of `the game refilled party unit 1 to 679 of 679 before anyone acted; max
  HP held back to 883, top-up re-armed` each followed at once by a `delivered` line (write the pair
  count next to this box); the tape shows the top up on Ramza's slot as one `event: healing`
  record (`400 -> 883`) or as two (`400 -> 679` then `679 -> 883`), the census reads `hp=883
  mhp=883`, and no `refilled` line of either kind after the first `a unit finished its turn
  (number 1 this battle)` line; the Parts report shows kill counting ON beside stat growth ON;
  `python tools\scan_logs.py --flight` CLEAN. FAIL, name which: F1, a `refilled ... after the
  first action; ... hp left alone` line and 679 of 883 (the game's last refill came after an acted
  edge, outside the window); F2, `delivered` present, no `refilled` after it, and 679 of 883 (an hp
  only refill, or one growth tick between the game's max write and its hp write; one F2 does not
  falsify the fix, two tapes with F2 and no tick split do); F3, no `armed` line at all (growth
  never located Ramza or the capture took the held path; bring the `located` line and the tape);
  F4, 883 of 883 on turn one but a top up `healing` record or a `refilled ... re-armed` line AFTER
  the first turn line (the window bound broke); F5, a Parts report line with kill counting WAITING
  or OFF beside stat growth ON plus any `refilled ... before anyone acted` line after the first
  turn line (the turn counter that closes the window runs only while kill counting is ON; no drill
  produces this, it is a log read only). NOT a failure (owner call 2026-08-25, written in
  LivingWeapon/Growth/GrowthEngine.Lanes.cs): (a) a knight sword whose tier rose BETWEEN battles
  (a kills.json edit, the archive restore, a tier crossed by the last kill of the previous battle)
  opens the very next battle with the max grown and the bar un topped, and the battle after it
  tops up, which is why the sitting plan keeps the 3.3 edit off the HP lane; (b) a transition
  churn can land one extra top up after a reset, once. A wounded bar counts only on a knight sword
  whose tier did not move since its last battle. The quoted log lines land with LW-416; re read
  them from LivingWeapon/Growth/GrowthEngine.Lanes.cs after the merge. (DEV OK) (covers LW-416,
  LW-327, LW-316 the Max HP half) **[BLOCKER]**
- [ ] 3.2 **Multi lane growth in one battle (LW-317, LW-316, LW-250, LW-249).** Each weapon class
  grows the stats its card says. Dev battle with a pole (Whale Whisker), a katana, a knight sword,
  a Stoneshooter and a Blaze Gun wielder; read each unit's stats on its turn and the roster after
  the battle. PASS: pole PA and MA up, katana PA, MA and Brave up (Brave capped 97), knight sword
  Max HP up, Stoneshooter WP bumped and `wp-table: restored <name>'s WP to <baked>` at turn end,
  Blaze Gun WP plus Faith up (Faith capped 85) with the damage forecast tracking the held value;
  every reading lands on round(natural x (1+factor)); roster values unchanged after exit. Knives,
  bows and ninja blades grow Speed; books, cloths, instruments and the Arcanum grow MA. Level up
  mid fight (chapter one levels every battle): pick the knife wielder closest to a level up, let
  them level inside this battle, read PA and Speed on their next turn and on the roster after.
  PASS adds: the in battle reading is round(new natural x (1+factor)) once the level lands, the
  roster shows the vanilla level up gain with no bonus baked in, the file carries `growth:` capture
  lines on the new level and no `restart residue` line. FAIL: a
  stat banking into the roster, a WP that stays bumped, PA rising on a knife or a pole with MA
  flat, a roster stat one below the vanilla level up value, the hold never re applied after the
  level up, or the WARN `...'s resident WP byte reads ..., matching neither the baked value`.
  (DEV OK) (covers LW-317, LW-316, LW-250, LW-249) **[MAJOR]**
- [x] 3.3 (PASSED 2026-10-17, prod 1c7ba75f, owner live, run on the second tier instead of the first because the Cutpurse could not land a kill: Chaos Blade at 9 claims kill number 10 at 10:31:04, toast delivered 1.4 s later `Chaos Blade has gained its 10th kill and has grown to Chaos Blade+2`, owner read the card and the +2 name; max HP rose mid battle as the hp lane retargets on a tier up, which is the designed hold) **Prod thresholds live (LW-161).** Tiers come at 5, 10 and 15 kills on a release build.
  Sitting 1, after the kills.json edit the sitting plan describes (one living weapon set to 4):
  score one kill with that weapon. PASS: the card reads `Kills: 5/10 to +2`, the name wears `+1`,
  the tier up banner delivers via the facing prompt (the toast positive control, LW-323's second
  read). FAIL: a card at 6 kills with no tier suffix, or a tier at any other count. (prod only)
  (covers LW-161) **[MAJOR]**
- [ ] 3.4 **Mounted speed and its log lines (LW-109, LW-110, Cavalier's Charge).** Riding a chocobo
  with a tier 3 Holy Lance (id 104) reads Speed natural plus 3 on the card (the `statBonus` of 3
  on id 104's signature block in data/items.json); dismount reverts it; the file says why. PASS:
  `timed-stat: capture natural ...`, `timed-stat: boost wrote N (natural N + bonus 3)`, `timed-stat:
  re-apply wrote ...` and `timed-stat: revert wrote ...` each present only when a write happened;
  Speed back to natural after dismount and at battle exit; no other unit's Speed moves. FAIL:
  Speed stuck at +3 after dismount, a card change with no timed-stat line, or the retry line
  `timed-stat: window closed but read ...; keeping the record to retry the revert` repeating.
  (DEV OK) (covers LW-109, LW-110) **[MINOR]**
- [ ] 3.5 **Restart does not inflate a held stat (LW-90).** Retrying a battle must not bake a held
  boost into the unit's natural. Tier 3 Ame-no-Murakumo wielder, start the battle (they act first),
  open the card after their first turn, then Retry and read the card at the first turn again. PASS:
  Speed reads natural after the first turn and after the restart; the file shows `The
  Ame-no-Murakumo wielder's Speed is held above the whole field so they act first this battle` then
  the release line, and on the restart `growth: restart residue corrected at capture`. FAIL: card
  Speed climbing by 3 each restart, or the boost outliving the first turn. (DEV OK) (covers LW-90)
  **[MINOR]**

---

## 4. The extended inventory (ids 261 to 267; recipes in docs/EXTENDED_INVENTORY_GUIDE.md)

- [ ] 4.1 **A weapon past id 260 is a real item (LW-346, LW-347, LW-365, LW-388 walk).** The
  owner's 13 step Warbrand (264) walk: list, equip on the formation screen, a shield in the off
  hand, swing at an empty tile, swing at an enemy, kill, card, save, quit, cold boot, load. PASS:
  every step; the shield stays beside the Warbrand; the empty tile swing draws the blade (not bare
  fists) and a vanilla sword's empty tile swing is unchanged; weapon damage dealt; the kill
  credits; the counter paints; the item is still there after the cold boot; log `Extended
  inventory: the <label> cap is widened (the new items now deal weapon damage).` after the save
  loads; no `swing-id fallback: ... refused`. FAIL: the item vanishing after a cold boot, hands
  drawn, no damage, a refusal line from the byte patch set. (DEV OK) (covers LW-346, LW-347,
  LW-365) **[BLOCKER]**
- [ ] 4.2 **The seven vanilla axes and flails are back, the seven designs moved (LW-351, LW-367).**
  Battle Axe, Giant's Axe, Slasher, Iron Flail, Flail of Flame, Morning Star and Scorpion Tail are
  vanilla again at their old ids; Terrastaff, Ravager, Sunderer, Warbrand, Bloodlash, Climhazzard
  and Sasori exist at 261 to 267 with their kills carried. Open a vanilla axe card and each design's
  card on the owner's long running save, then load the save twice. PASS: seven vanilla cards plain
  (no Kills line), seven design cards with Kills lines and their prior counts; the first load MAY
  print `A damaged menu order table was healed on the load: ` naming only ids past 267 and the
  second load is silent; both weapon menus list exactly what is owned with no hand sort. The two
  handed design counts once: equip the Terrastaff (261, two handed) and kill once; one `Terrastaff
  claims kill number N` line, the count up by one, no shield slot offered in the picker. FAIL: an
  axe still carrying a design's text, a design missing from the list, kills reset to 0, a heal line
  naming 261 to 267 or appearing on every load, two credit lines for one Terrastaff corpse. (DEV OK
  for names and listing; counts need prod) (covers LW-351, LW-367) **[MAJOR]**
- [ ] 4.3 **The bag holds through every list and loop (LW-371, LW-372, LW-382, LW-368).** Owning
  many weapon and shield kinds no longer damages the save and every list draws past 149 rows.
  There is NO fill rig this release (FFTHandsFree crashes the Denuvo free build and no script in
  this repo writes a bag), so the box runs on the owner's long running save as it stands: note how
  many weapon and shield kinds the Items tab lists, then walk both weapon menus, the Items tab, a
  shop Buy list, the equip picker on two fully equipped units, sell one kind, save, reload, a load
  with the mod disabled, a lost battle plus Retry, buy one item, use one; then the sell to zero
  loop: sell every Warbrand (it leaves both weapon menus), buy one back in the same shop visit,
  save, reload; sell a vanilla living weapon with kills and buy one back, open its card. PASS:
  every list draws every owned kind, the picker holds, the sold kind vanishes, Sell completes, the
  mod less load is clean, counts correct across the loop, zero warnings, none of
  `template-relocation: ... refused`, `list-relocation: ... refused`, `list-builder: ... hook
  install failed`, `something still writes the game's old item count block`; the Warbrand row
  returns in both menus with count 1 after the reload and no heal line naming 264; the vanilla
  weapon's card still reads its old `Kills: N` (the tally is per item id, by design). The "past
  149 rows" half is proven ONLY if the noted kind count is over 149; otherwise flip that half NOT
  REACHED and the LW-371 and LW-372 live proof rests on their 2026-08-31 changelog rows (the owner
  read 161 and 153 rows then). FAIL: a list stopping short of an owned kind, helmet chart
  corruption after reload, a crash on the picker or on Sell, a doubled or missing Warbrand row
  after the buy back, count 0 after the reload, a vanilla card reset to 0. (DEV OK) (covers
  LW-371, LW-372, LW-382, LW-368) **[MAJOR]**
- [ ] 4.4 **The indirect roads in and out of the bag (job change, Dismiss, Throw, an enemy's Steal
  or Rend).** Taking a new weapon off a unit the roundabout way still puts it in the bag, and
  losing a weapon mid fight stops its credit and its grant. Dev build: a unit holding a Sunderer
  (263) changes job on the party screen to one that cannot hold it (the game hands it back);
  Dismiss a unit holding a Bloodlash (265); a Ninja with two Bloodlash in the bag Throws one; open
  the Items tab and save. Leg B (when the fight offers it; chapter one and two Thieves and Knights
  carry Steal Weapon and Rend Weapon): let an enemy take a living weapon from a tier 3 wielder mid
  battle, then kill once with the remaining weapon and finish. PASS: each returned count rises by
  one in the Items tab and both still list in the weapon menu; the Throw list offers Bloodlash, the
  throw lands and the count drops to 1; the next save line `A save was written (key ...);
  extended-inventory bag counts recorded for it` shows the new counts; no `something still writes
  the game's old item count block` WARN and no `template-relocation: ... refused`; leg B: the file
  carries `<weapon> was unequipped; its granted ability is switched off.` if a support grant was
  held and the ability leaves the unit's support slot, the next kill logs ONE `claims kill number`
  line naming the remaining weapon, a stolen Sunderer's former wielder plants no Bulwark, the
  stolen item sits in neither menu after the battle, the lost weapon's count in kills.json is
  unchanged, no crash. FAIL: a count unchanged or the item gone, the tripwire WARN, Bloodlash
  missing from the Throw list, two credit lines after the loss, a grant still firing on an empty
  hand, a crash. (DEV OK) (covers LW-346 the cap set, LW-353 the count record) **[MAJOR]**
- [ ] 4.5 **Disable, save, re enable: the uninstall promise, and a missing DLL.** README.md,
  docs/COMPATIBILITY.md and docs/EXTENDED_INVENTORY_GUIDE.md promise a removed mod loses nothing
  permanent and the new weapons come back with their counts; this box finds out which half of that
  is true, because the code keys every save written to a new key. Dev sitting, the owner owns a
  Warbrand and one unit wields it. Leg 1: untick Living Weapons in Reloaded-II, load the save, read
  the wielder's hand and the Items list, quit WITHOUT saving, re enable, load, read the count. Leg
  2: the same, but SAVE once while the mod is off, re enable, load, read. Leg 3: with the mod
  enabled, rename the deployed `LivingWeapon.dll` to `LivingWeapon.dll.off`, launch, Continue,
  read the wielder, quit, rename it back (or redeploy). PASS leg 1: hand empty and item absent
  while off, no crash; on return `A save was loaded (key ...); extended-inventory bag counts placed
  from its own recorded counts` naming `Warbrand xN` with N the pre test count. PASS leg 2 (pre
  registered from LivingWeapon/Persistence/BagReplay.cs, not a hope): the log reads `placed from
  the first-copy seed (this save was never seen with the mod running)` and the Warbrand is x0; the
  three docs above are then softened to "if you did not save while the mod was off" before ship
  (box B19 carries that grep). PASS leg 3: no new `livingweapon.log` this launch (the prev log
  untouched), Reloaded's own log names the missing DLL, the game loads with the hand empty, no
  crash; the next launch with the DLL back arms and the Warbrand returns with its count. FAIL: a
  crash on a mod less load, a leg 1 count other than N, leg 2 reading `recorded counts` with a
  wrong N (a key collision, far worse than a seed), the count gone after the DLL returns. (DEV OK)
  (covers LW-348 the key design, LW-353 the per save counts) **[MAJOR]**
- [ ] 4.6 **Shops sell the seven designs where items.json says (LW-354).** Each design has a town
  list and a story milestone (`extended.shops` and `extended.shopAvailability` in data/items.json).
  On a save past the named milestone open the Dorter, Lesalia and Gariland Buy lists. PASS: Dorter
  lists Warbrand (264, after Chapter4_KillElmdor), Bloodlash (265, after Chapter3_Start),
  Climhazzard (266, after Chapter3_SaveRafa) and Sasori (267, after Chapter4_Start); Lesalia lists
  Terrastaff (261, after Chapter1_KillMiluda), Ravager (262, after Chapter4_Start) and Sunderer
  (263, after Chapter4_Bethla); Gariland lists none of the seven; a purchase raises the bag count;
  no "+" item anywhere; the file carries `Extended inventory: the shop table mirror was refreshed
  from the game's own table.` and no `shops: ... refused`. FAIL: a design missing from its town
  past its milestone, a design in Gariland, a "+" row, a purchase that does not raise the count.
  (DEV OK) (covers LW-354) **[MINOR]**

---

## 5. Kill attribution, battle edges, the attack card, toasts

- [ ] 5.1 **Credit goes to the true killer, in every lane (LW-272, LW-96, LW-137, LW-154; LW-396
  and LW-164 watch).** Manual battle against HUMAN soldiers who carry the same weapon id as one of
  the party's living weapons (chapter one and two knights and archers do), plus one auto battle run
  with the fast forward button HELD for the whole fight: kills credit the killer's weapon even when
  another unit is hovered; a dual wielder's kill credits BOTH living weapons; a unit at roster slot
  21 or later earns kills and card text like everyone else; a death caused by an enemy never flips
  credit; a bare handed unit acts and then a living weapon wielder kills with Throw or an item
  (the kill still credits the weapon); a Counter during the enemy's turn kills a near dead enemy.
  PASS: every credit prints `<weapon> claims kill number N, felling <victim> at (x,y).` naming the
  right weapon, and the tape's `latch weapons=[...] mainHand=... src=turn-flags` records carry
  `src=turn-flags` (never `src=first-kill-fallback` on a manual kill); both knives' counters up one
  on a dual wield kill (two `claims kill number` lines for one corpse); the late recruit credited
  and carded; the item or Throw kill after the unarmed turn credits (a plain credit line, or a
  `Corrected a burial` line naming the weapon); the fast forward battle's `Battle ended: N kills
  credited` equals the kills seen on screen with zero `could not be determined` lines and no `The
  provoke hold timed out and released on its own`; no `rescue=WeaponUnique` on an enemy seat in
  the tape, and an enemy's kill (or a Confused enemy's kill of its own ally) logs no credit; the
  `Battle ended` summary matches the on screen kills. NOT a failure: the Counter kill is uncredited
  BY DESIGN this release (the tape shows `no-credit slot=N reason=untracked-weapon via=EnemyTurn`
  for that corpse, no `claims kill number`, the card unchanged; LW-396 is the open row, see Open
  risks). FAIL: a kill on the wrong weapon, one knife of two credited, the late recruit uncredited,
  `slain by a player carrying no Living Weapon; the kill is deliberately left uncredited` after the
  item or Throw kill, a kill the player saw at fast forward with no credit line, a WeaponUnique
  rescue on an enemy seat, or `the kill goes uncredited` after a living weapon kill on a player
  turn. (DEV OK) (covers LW-272, LW-96, LW-137, LW-154) **[BLOCKER]**
- [ ] 5.2 **A long cast, a delayed kill and a long pause are not a battle end (LW-42).** Queue Holy
  or Flare, let it resolve, win normally; let one kill land from a DELAYED action (a rod wielder's
  Fire, a Dragoon's Jump or a Dancer's Wiznaibus that kills after another unit has acted); after
  one kill open the in battle options or system menu and leave it 30 seconds, resume, then Alt Tab
  away for 60 seconds during an ENEMY turn, come back, kill once more. PASS: exactly one `Battle
  started.` and one battle end summary per real battle, the battle end trace `battle-end sentinels
  (slot0=... ...)` only at the true end; the delayed kill prints `<weapon> claims kill number N`
  naming the COMMITTER's weapon with `delayed-credit detail` in its trace tail and the count rises
  on that card only; the post pause kill credits; scan exit 0. FAIL: a battle end mid fight (then
  a second `Battle started.`), kills reset, the delayed kill credited to the unit that acted in
  between or `the kill goes uncredited`, the post pause kill uncredited. (DEV OK) (covers LW-42)
  **[BLOCKER]**
- [ ] 5.3 **Tallies persist, battle edges stay clean, a world map re enter registers, a chained
  pair stays two fights.** Credits land in battle, the battle end saves, the launch header count
  carries over a session restart; two consecutive battles log started, ended, started with no
  double exit and no mid battle exit; leaving a battle to the world map and restarting it logs a
  fresh `Battle started.`, the Attack row renames, kills credit. Chained story pair (two fights
  with no map between them: the Riovanes gate then the roof, the Lionel gate then Queklain, the
  Limberry gate then Elmdor): if the owner's save is within reach of one, play battle one with a
  living weapon crossing a tier near its end, accept the save prompt if offered, play battle two;
  if none is within reach, flip that leg NOT REACHED and leave it for the next release. PASS: all
  three; `Battle ended` for battle one BEFORE battle two's `Battle started.`, battle two opening
  with no `has grown to` banner from battle one (5.8's drop line instead if one was pending), two
  tapes, two summaries, battle two's kills credited; closing scan CLEAN. FAIL: a lost tally across
  a restart, a double exit, silent dormancy after the re enter, one `Battle started.` spanning
  both chained fights, a banner from battle one inside battle two. (DEV OK) **[BLOCKER]**
- [ ] 5.4 **Lookalike units never swap kills (LW-252).** Two fingerprint twins (same level, brave,
  faith) plus Ramza with a living weapon; each kills once. PASS: Ramza's kill credits; a twin's kill
  with no living weapon logs `slain by a player carrying no Living Weapon; the kill is deliberately
  left uncredited` or `The killer of the enemy at battle slot N could not be determined; the kill
  goes uncredited.`; the tape shows two distinct unit ids. Opportunistic (never stage a battle for
  it): if a Charm or Berserk lands on a living weapon wielder during the pass, record which of the
  two the AI driven kill gets, `claims kill number` or `the kill goes uncredited`, and that a
  second such kill answers the same way; the answer becomes a docs/MECHANICS.md line. FAIL: a kill
  on the wrong weapon, a unit wearing another unit's grant, two AI driven kills answered
  differently. (DEV OK) (covers LW-252) **[MAJOR]**
- [ ] 5.5 **Retry does not pay twice (LW-233).** Kill one enemy, note the tally, die, Retry, re kill
  the same enemy; run that leg twice, once from the battle start retry and once from the checkpoint
  retry when the game offers both; control leg: kill, Raise the corpse, kill again. Keep the
  Kiyomori and any Brave or Faith changing effect off the field for this box. PASS: each retry
  leg's tally equals the pre retry number and the log carries `The battle was retried; kills from
  the abandoned attempt are uncounted so nothing pays out twice.`; the Raise leg's tally is up two.
  NOT a failure: one uncredited re kill across the retry legs is the recorded one in fifteen timing
  miss (a miss, never a payout); the identity swap branch on a Brave or Faith altered victim can
  double count, which is why the Kiyomori stays benched here. FAIL: a tally one higher after either
  retry, two uncredited re kills in two runs, or the Raise re kill paying once. (DEV OK) (covers
  LW-233) **[MAJOR]**
- [ ] 5.6 **The Attack row is always this unit's truth (LW-87, LW-88, LW-91, LW-98).** On a wielder's
  turn the Attack row reads the weapon name and the live count; it survives a T detour onto an ally
  and an enemy and a STATUS SCREEN detour (open an enemy's status page, read two pages, back out);
  an unarmed human reads `Fists`; an enemy turn reads stock Attack; the count climbs with each
  kill. Two living weapon wielders plus one bare handed human, alternating turns. PASS: the row
  keeps the name after every detour, hover and status alike, each unit's row names its own weapon,
  `Fists` on the bare hand, the count rises by one per kill, zero WARN (a wobble shows at most
  `attack row copy unverified (...): retained pending recovery` and recovers in about a second).
  FAIL: a stale name on another unit's menu, stock Attack or another unit's weapon on the row after
  the status page, a frozen count, a weapon name on an enemy or unarmed row. (DEV OK) (covers
  LW-87, LW-88, LW-91, LW-98) **[MAJOR]**
- [ ] 5.7 **Row and card agree, no hitches, the long battle tape (LW-257, LW-259, LW-261, LW-262).**
  A long battle (ten plus kills) with no freeze; the Attack row count and the equip card count agree
  within a second of each kill; the tape keeps reporting late in the fight. Kill one enemy with a
  charged spell so `waiting to identify the killer` prints, then leave that body and one credited
  corpse on the field until both turn to crystal or chest. End it and run
  `python tools\parse_flight.py <deployed folder>\flight\flight_*_battle-exit.jsonl`. PASS:
  identical `Kills:` readings on both surfaces (a card left open across a kill may hold the old
  snapshot until reopened, known); no multi second stall; `LW37 locate-complete: N ticks, N bytes,
  Nms` lines with many ticks; `coverage regions=N sites=N kills=N suffix=N trigger=...` with suffix
  under 1024; `locate-complete` records only from named edges; `site-evicted` records present after
  the 64 routine paint lines are spent; the pending kill still credits (its `claims kill number`
  line lands before or after the crystal, never `could not be determined`) and the crystal triggers
  no retry detection (no `The battle was retried` line, no `restart` record `latch open`). NOT a
  failure (both pre registered in docs/VERIFY_LIVE.md): settlement through the permissive branch
  (`RegionsStale` true) for most of a battle is LW-257 and LW-261 working as designed, read the
  kill to paint convergence (about a second) and the `locate-complete` ticks, never the branch;
  `sites=` may read anywhere up to 3072 (kill notes 2048 plus suffix notes 1024, separate rooms),
  only `suffix=` is capped, and `site-evicted ... reason=pruned-dead` is rare, not routine. FAIL: a
  23 against 22 disagreement after reopening, a visible stall the file blames on the mod (Read
  first), `ticks=1`, suffix pinned at cap, a tape whose last card record is a routine paint with
  no eviction after a late kill, a pending kill lost to the crystal, a retry uncredit on a crystal.
  (DEV OK) (covers LW-257, LW-259, LW-261, LW-262; the pre registered LW-259 signature was never
  signed) **[MAJOR]**
- [x] 5.8 (PASSED 2026-10-18 on prod of 855a00eb, owner live: read one, the Chaos Blade poked to 9 claimed kill number 10 at 13:17:47, the owner fled at 13:17:59, the log carried the drop line naming Chaos Blade tier 2 and the battle exit tape the record dropped weapon=37 tier=2, the next real battle at 13:18:23 showed no banner and its tape holds zero toast records; read two, the Cutpurse and Argent Dirk tier ups delivered on the facing prompt in that same first battle at 13:17:48 and 13:17:55, on top of the 3.3 control from 2026-10-17; the four second battles either side are pre battle churn) **A level up banner never shows a battle late (LW-323, two reads).** A tier crossed with
  the battle ending fast shows NO ghost toast in the next battle; a tier crossed with a Wait prompt
  still to come delivers in battle as always (box 3.3 is that positive control on prod). PASS: read
  one: the next battle opens with no stale banner and the file carries `A toast went undelivered by
  its battle's end and was dropped (<weapon>, tier N); the growth itself is saved either way` with
  a `toast` record on the dying battle's tape; read two: the banner lands on the facing prompt in
  its own battle. FAIL: a `has grown to` banner in a battle whose kill happened the battle before,
  or a dropped toast with no drop line. (DEV OK for read one) (covers LW-323, AWAITING-LIVE)
  **[MAJOR]**
- [ ] 5.9 **Guests never count, as killer or as corpse (LW-234 watch).** A guest like Delita or
  Agrias killing an enemy never grows one of your weapons (Delita's Broad Sword shares an id with
  Ramza's), and a guest dying is never a kill. Any story battle with a guest (the formation screen
  shows the guest slot; the Gariland battle of box 6.6 has two): let a guest finish one enemy, and
  if the fight allows let an enemy kill the guest (or read the tape only). PASS: the guest's kill
  has no `claims kill number` line and the tape marks that corpse `no-credit slot=N
  reason=untracked-weapon via=EnemyTurn`; the party weapon's count equals only its own kills; the
  guest's death logs `died but was not a tracked enemy; no credit given` with a `no-credit slot=N
  reason=not-tracked-enemy` record, no credit line and no banner; `python tools/parse_flight.py`
  on the battle exit tape shows no record bridging a guest seat with `rescue=OracleEnemy`; the
  summary count equals the enemy kills only. FAIL: a `claims kill number` line during a guest's
  turn, a card count one above the owner's own kills, a credit or banner on the guest's death, a
  guest seat rescued as an enemy on the tape (the LW-234 exposure live again; a FAIL promotes
  LW-234). (DEV OK) **[MAJOR]**
- [ ] 5.10 **Leaving a battle the ugly way: quit to title mid fight, and a hard close (LW-108 and
  LW-233, the quit door).** A player who quits to the title in the middle of a fight, or closes
  the game and comes back tomorrow, gets a battle that counts and no ghost from the abandoned one.
  Leg A (quit): dev build, enter a battle, kill one enemy with a living weapon (note the credit
  line and the count), open the system menu, Quit to title, Continue the same save (record whether
  the game put you back in the battle or on the world map), kill that same enemy again, finish.
  Leg B (hard close): play into a battle until the autosave has run (one full turn), kill the game
  process (the one place a hard kill IS the test; the tape ring is lost on purpose), relaunch,
  Continue. PASS leg A: a `Battle ended` line for the abandoned attempt BEFORE the resumed
  battle's `Battle started.` (or, when the exit lands only on the world map, within five seconds
  of the map and before any new battle), the re kill of the same enemy does NOT pay (tally equals
  the pre quit number plus the finished battle's kills), no `has grown to` banner from before the
  quit shows after it, `python tools/scan_logs.py --require-battle` exit 0. PASS leg B: the header,
  the armed line, then `Battle started.` within seconds of the field appearing; the Parts report
  reads kill counting, stat growth and the battle powers ON with `battle sensing WAITING` for the
  whole resumed battle (the detector arms only out of battle, by design; its reason names the
  check not yet run); a kill credits; the battle end summary prints; within ten seconds of the
  world map the sensing line `The battle sentinels read as expected; the new game detector is
  enabled.` appears and the report reaches `Living Weapons: all parts armed.`. FAIL: no `Battle
  ended` between the quit and the resumed fight, the same enemy paid twice, a banner from the
  abandoned attempt, two battles sharing one `Battle started.`, no `Battle started.` for the
  resumed battle, a kill uncredited, a box, or sensing still WAITING a minute after the world map.
  A leg A FAIL is a Backlog row beside LW-108 and LW-233 and the owner rules whether it joins R5.
  (DEV OK) **[MAJOR]**
- [ ] 5.11 **Two of the same weapon share one story.** Buying a second copy of a shop weapon is
  the most ordinary thing a player does: both copies share one kill count and both wielders grow,
  while a trick weapon's power is one per party (two main hand wielders means the trick arms for
  neither, silently). Dev build: two units of different level each with a Cutpurse (id 1) or a
  Mythril Knife, one kill each; then two Mending Staves (61) on two casters for one battle, and
  one staff removed for the next. PASS: both knife cards read the same `Kills: N/T` after both
  kills (two `claims kill number` lines, N up by two); both wielders' Speed reads round(natural x
  (1+factor)) on their turns; with two staves the file shows NO mend line for either and no WARN
  (ambiguous means no grant, LivingWeapon/Battle/Wielder.Roster.cs); with one staff removed the
  `ally was mended` line returns the next battle. FAIL: one knife card frozen while the other
  climbs, growth on one wielder only, a staff healing from both rows, the trick firing for one
  unit only, a crash on the picker. Owner ruling owed on the result: whether "two wielders, no
  grant" deserves one calm log line (a Backlog row, not a fix). (DEV OK) **[MAJOR]**
- [x] 5.12 (PASSED 2026-10-18 on prod of a38eb35b, owner live, kills.json staged at 7 for every weapon and 15 for the Outrider Pistol: the same save loaded twice read 7 of 10 on the cards, a different slot read Cleaver+ at 7 of 10 with its ring, the first save loaded again read Outrider Pistol+3 at 15, every read matching the file, no frozen or zero counter) **The fast reload loop (LW-163).** Reload means the world map's Load menu inside the same
  session, not a relaunch: load a save, open the card, load the same save again, then a different
  save, then the first again, opening the card each time. PASS: the Kills line matches kills.json
  every time. FAIL: a frozen or zero looking counter until a tier up. (DEV OK) (covers LW-163)
  **[MINOR]**

---

## 6. Save integrity and persistence

- [x] 6.1 (PASSED 2026-10-17, prod 1c7ba75f, owner live from a cold launch: Continue loaded 381 kills across 121 weapons with no detection and no archive, sensing line right after the header; New Game logged Archived the previous kills.json to <save folder>rchive\kills.14.json and A new game was detected; the previous kill tally was archived and reset.; bag counts seeded for a new game; the owner read 0 kills on the Vagabond at Gariland; the opener credited nothing; Vagabond claims kill number 1; run_flavor.txt reads prod; the archive was copied back, 381 kills restored) **Cold launch New Game archives and resets; Continue never trips it (LW-395 normal leg,
  LW-368, LW-134).** From a COLD launch on prod: Continue the real save (no reset), then New Game.
  PASS: Continue logs no new game detection and no archive; `The battle sentinels read as
  expected; the new game detector is enabled.` right after the header; New Game logs `Archived the
  previous kills.json to <full path>` (the path is `<save folder>\archive\kills.N.json`) and `A new
  game was detected; the previous kill tally was archived and reset.`; the fresh game starts with
  its potions (`Extended-inventory bag counts seeded for a new game`); cards read 0 with plain
  names; the Orbonne opener's scripted kills stay uncredited BY DESIGN; the first real battle
  credits kill number 1; `run_flavor.txt` in the save folder reads `prod`. Then, game closed, copy
  that archive back over `<save folder>\kills.json` (the sitting plan step). This box runs on the
  owner's POPULATED tally; the empty tally variant (no archive line at all, by design) is box 6.6.
  FAIL: a reset on Continue, no archive on New Game, `Failed to archive kills.json` in the log
  (stop, restore from kills.json.bak before another battle saves over it, and clean `archive\`),
  an empty starting inventory, a stamp file missing or reading `dev`. (prod) (covers LW-395 normal
  leg, LW-368 new game leg, LW-134 stamp eyeball) **[BLOCKER]**
- [x] 6.2 (PASSED 2026-10-17 on prod 62015755: the file half as recorded at 10:25, one move line then, kills.json without 48 and the .bak holding 48 = 9; the owner read the Terrastaff card at 12 of 15 to +3 with +2 in its name right after Continue; the later launches printed no move line) **A 2.3.x player's axe kills move to the new weapon on the first launch (LW-351 upgrade
  leg).** Every returning player's first 2.4.0 launch does this before anything else: the
  Terrastaff kills they grew for weeks are filed under the Battle Axe's old number (48) and must
  move to 261 once, with nothing lost. The owner's save migrated 2026-08-30, so this box stages
  it. Sitting 1, game closed, in the same kills.json edit the sitting plan describes, add the line
  `"48": 9` (a key the file no longer has). Launch, Continue, open the Terrastaff and Battle Axe
  cards, then read kills.json and kills.json.bak BEFORE fighting (the next battle's save rotates
  the .bak); launch once more. PASS: the header carries exactly
  one `Some weapons changed item id in this version; 1 weapon tally and 0 deed record(s) were
  moved over so no earned progress was lost (48 -> 261).`; the Terrastaff card reads its previous
  count plus 9 (the move sums on collision); the Battle Axe card has NO Kills line; kills.json has
  no `"48"` key and kills.json.bak still holds the `"48": 9` row (the pre move file); the second
  launch prints no move line. FAIL: no move line, a Battle Axe card wearing a Kills line, the
  Terrastaff count unchanged, or the line printing again on the second launch (the old key
  survived). Part B half: `grep -c migratedFrom LivingWeapon/meta.json` reads 7 and the
  TallyMigrationTests ride B1. (prod; a dev build floors every count to 3 and hides the move)
  (covers LW-351 upgrade half) **[BLOCKER]**
- [ ] 6.3 **Extended bag counts follow their save and survive a cold boot (LW-353, LW-348).** Two
  saves with different Warbrand counts: load A, read, load B, read, load A again; then change a
  count, quit, cold boot, Continue, read; `ls "<save folder>\extended_inventory.json"`. PASS: counts
  follow their slots; the file is present and updated; `The load-edge hook is confirmed working;
  the game's first save load this session was intercepted (load-apply 0x...)` once and `The
  save-edge hook is confirmed working; the game's first save this session was intercepted.` after
  a save; none of `save-edge hooks: install failed`, `Failed to save the extended-inventory bag
  counts to disk`, `The extended-inventory sidecar could not be read`. FAIL: slot B showing slot
  A's count, a count back to seed after the boot, no load edge line. (DEV OK) (covers LW-353,
  LW-348) **[MAJOR]**
- [ ] 6.4 (RUNNING 2026-10-17: two deploys so far, dev 09:56 and prod 10:10, each printed the restore line with the post restore check passed and parity 506 of 506; flight holds its tapes; the header tally never dropped across sitting 1; closes after the sitting 2 and last sitting deploys) **Save files are update safe and every deploy preserves them (LW-134, LW-297).** Across
  this pass's deploys: `kills.json`, `legends.json`, `gunslinger.json` sit under the SAVE folder
  (`<Reloaded root>\User\Mods\prawl.fft.livingweapons`), the restore line prints each time (post
  restore check passed), `flight\` and its 20 tapes exist under the deployed folder afterwards, the
  launch header tally never drops. A file that never existed yet (no battle fought, no deed, no
  twin minted on that install) is not a loss; a file that EXISTED before a deploy and is gone after
  it is. PASS: zero silent loss all pass. FAIL: any lost preserved item (capture the deploy
  transcript and stop deploying until understood; LW-28 is the open backlog row). (covers LW-134,
  LW-297 restore half) **[MAJOR]**
- [x] 6.5 (PASSED 2026-10-17, prod 1c7ba75f, owner launch with the save folder renamed aside: Save files live at the Reloaded User\Mods folder, one WARN No kill tally was found on disk; starting fresh., tally 0 across 0 (kills.json, fresh), legends 0 and 0 (legends.json, fresh), sidecar none (first run), 0 save(s) known, Extended inventory armed 7, zero ERROR, no box, scan CLEAN; the folder came back holding run_flavor.txt reading prod plus pool_regions.json and its .bak, which the box text first missed and now allows; Config.json is read from the deployed folder when the save folder has none) **A brand new install starts clean (the first launch ever).** A player who has never run
  the mod gets a save folder, a stamp and a working header with nothing on disk to read, and no
  error. Game closed, prod deploy in place: rename the WHOLE save folder
  `<Reloaded root>\User\Mods\prawl.fft.livingweapons` to `prawl.fft.livingweapons.aside`. Launch,
  sit on the title ten seconds, read the header (box 6.6 continues from here with the folder still
  aside). PASS: the header carries `Save files live at <Reloaded root>\User\Mods\prawl.fft.livingweapons.`
  (never a path inside the deployed Mods folder), one WARN `No kill tally was found on disk;
  starting fresh.`, `The kill tally holds 0 lifetime kills across 0 weapons (kills.json, fresh).`,
  `The legends hold deeds for 0 weapons and 0 Marks (legends.json, fresh).`, the extended seed
  line ending `(sidecar: none (first run), 0 save(s) known).`, `Extended inventory armed: 7 new
  item(s)`; the folder exists again holding `run_flavor.txt` reading `prod` plus the card painter's `pool_regions.json` and its `.bak` (written within seconds as the painter finds the game's text pools) and no other file of the mod's
  (Reloaded-II may recreate its own `ModUserConfig.json` there; that file and a `Config.json` the
  launcher writes are Reloaded's, not a defect, and both came along in the rename);
  zero ERROR lines; no box; `python tools/scan_logs.py` exit 0 (one warn counted). NOT a failure:
  the fresh start WARN, the `fresh` source words, and the absent kills.json, legends.json and
  gunslinger.json (each is born at its first event). FAIL: `Failed to stamp`, `Failed to migrate`,
  a `Save files live at` path inside `$env:RELOADEDIIMODS`, `Startup failed; Living Weapons will
  not run`, any box. (DEV OK for the header half: a dev build seeds the tally in memory only and
  writes nothing while you sit on the title) (covers LW-134 the stamp on a fresh folder)
  **[MAJOR]**
- [x] 6.6 (PASSED 2026-10-17, prod 1c7ba75f, owner live with the save folder aside: New Game on the empty tally printed no new game line and no archive; the opener ended Battle ended: no kills were credited; Gariland logged Vagabond claims kill number 1 then delivered Vagabond draws first blood! 3 s later, kills.json read {"19":1}; the guest's kill logged slain by a player carrying no Living Weapon, uncredited; the battle ended 5 kills credited (Vagabond 5), 1 tier reached; the save edge hook confirmed, the save key pt543-0a45852bfb5b was written and the relaunch Continue loaded that same key with every design at x0; zero ERROR; the real folder is back) **The new player's first hour: New Game on an empty tally, first blood, a guest's kill
  (LW-368 and LW-395, the empty tally variant).** The first hour of every new player: a New Game
  with nothing on disk, the Orbonne opener, Gariland, kill number one with Ramza's sword (every
  chapter one weapon grows), the first toast the mod ever shows, and Delita or Argath killing
  something that must not count. Prod, folder still aside from 6.5: New Game, play or skip the
  Orbonne opener, win the Gariland battle with one kill by Ramza's starting sword and let a guest
  finish one enemy, Wait on Ramza's next turn after his kill, open his equip card, save in slot 1,
  Continue that slot once, close the game. Afterwards, game closed, delete the recreated folder,
  rename `.aside` back, and delete the slot 1 save if unwanted. PASS: NO `A new game was detected`
  line and NO `Archived the previous kills.json` line (the reset returns early on an empty tally,
  expected); `Extended-inventory bag counts seeded for a new game`; the opener ends `Battle ended:
  no kills were credited` and `kills.json` appears in the save folder right after that exit
  reading `{}`; Gariland logs `<weapon> claims kill number 1, felling ...` for Ramza's sword and the
  facing prompt shows `<weapon> draws first blood!` during that battle with one `toast` record
  `enqueued weapon=<id> tier=-1` on the tape; the guest's kill has no `claims kill number` line
  (box 5.9's read, for free); the card reads `Kills: 1/5 to +1` with a plain name; `kills.json`
  reads `{"<id>": 1}` with no `.bak` yet; `legends.json` now exists; the save logs `The save-edge
  hook is confirmed working; the game's first save this session was intercepted.` and `A save was
  written (key ...); extended-inventory bag counts recorded for it` with every design at x0, and
  `extended_inventory.json` now exists; the Continue logs `A save was loaded (key ...);
  extended-inventory bag counts placed from its own recorded counts` (the new save's key round
  trips). NOT a failure: no archive and no new game line on an empty tally; the opener's scripted
  kills uncredited. FAIL: `A new game was detected` on an empty tally (an archive of nothing),
  kills.json absent after the opener's exit, a `Kills: 0/5` card after the credited kill or a
  `Kills: -/- to +` that never resolves, no first blood toast by battle end or a toast reading `has
  grown to` at kill 1, a guest's kill crediting Ramza's sword, an ERROR. (prod only: the dev seed
  writes every weapon at 3 into the newborn kills.json at the opener's exit and hides the empty
  tally path, and the seed floor sits above the first blood milestone) (covers LW-368 new game
  leg, LW-395 the empty tally leg) **[MAJOR]**
- [ ] 6.7 **A damaged tally file comes back from its backup, and a foreign paint cache is thrown
  away (LW-405 as it stands).** A torn write on a crash is the realistic way a tally dies; the
  previous copy must take over with one warning, never a silent restart at zero. Sitting 2 (dev,
  the real file is aside), game closed: confirm `kills.json.bak` exists, overwrite `kills.json`
  with the text `{ not json`, and in `<save folder>\pool_regions.json` set `peTimeDateStamp` to 1;
  launch, Continue, one battle to the end, read both tally files, then a second battle. PASS: the
  header reads `(kills.json, backup)` with the backup's total and exactly one WARN `The kill
  tally's primary file was missing or corrupt; it was restored from the backup.`; the cards read
  the backup counts (dev floors to 3); after the battle kills.json is valid JSON and
  kills.json.bak holds `{ not json` (expected for one generation; the save copies the current
  primary to .bak before the move); after the second battle both are valid; the launch log carries
  the DBG `Pool-region sidecar not usable (PE build key does not match the running build); warm
  start will seed nothing this launch.`, the cards still paint, and by battle end the file is
  rewritten with `"peTimeDateStamp":1784564251,"peSizeOfImage":66707456` (0x6A5E4A1B and
  0x03F9E000 in decimal) and a non empty `regions` list; `python tools/scan_logs.py
  --require-battle` exit 0. FAIL: `(kills.json, fresh)` with a .bak present, no WARN, a crash at
  launch, kills.json still garbage after the battle, the foreign stamp accepted (no `not usable`
  line), cards that never paint on that launch. (DEV OK) (covers LW-405 the cache stamp read)
  **[MAJOR]**

---

## 7. Equip cards, the Grows line, glow icons, galleries (data rows take effect on RESTART)

- [x] 7.1 (PASSED 2026-10-18 on prod of a38eb35b, owner live with eight screenshots, every weapon staged at 7 kills: Cutpurse Kills 7/10 to +2 then Grows: Speed., Outrider Pistol Kills: 15 then Grows: Weapon Power., Chaos Blade and Excalibur Grows: HP. with their Petrify and Holy lines, Materia Blade Grows: Physical & Magick Attack. with both Ultima scaling lines, Slumber Rod the pole lane, Murasame the katana lane with Bravery, Battle Axe a plain card with no Kills and no Grows line; each card's text matched tools/lib/flavor.assemble_desc for its row; no No Poaching line seen; the two kill counter clause rests on box 1.4 and the 13:17 fight the same day; the DLC Akademy Blade and the game's own second Materia Blade+ row are not mod rows, owner ruling) **The Grows line and the famous names (LW-332, LW-322, LW-328, LW-166).** Every living
  weapon card says in words what it grows, right under Kills. Open a knife, a pole, a katana, a
  gun, a knight sword, the Excalibur, the Materia Blade, the Cutpurse and one vanilla non growing
  item. PASS: line one `Kills: N/T to +`, line two `Grows: ...` matching that weapon's lane in
  LivingWeapon/meta.json (`Grows: Speed.` on a knife, `Grows: Physical & Magick Attack.` on a pole,
  `Grows: Physical Attack, Magick Attack & Bravery.` on a katana, `Grows: Weapon Power.` or
  `Grows: Weapon Power & Faith.` on a gun, `Grows: HP.` on a knight sword; the other lanes read
  `Grows: Physical Attack.` and `Grows: Magick Attack.`), prose below; no Grows phrase on the
  plain item; no card anywhere carries the retired `No Poaching.` line (the Cutpurse used to);
  Excalibur, Chaos Blade and Materia Blade print their hidden effects, Materia Blade keeps its
  Ultima scaling numbers; after one battle with two credited kills both counters read right. FAIL:
  Grows below the prose, a phrase on a non growing item, a `No Poaching.` line, a famous card with
  vanilla text only, a counter mispaint. (DEV OK) (covers LW-332, LW-322, LW-328, LW-166; LW-322 is
  the R5 grows text live pass) **[MAJOR]**
- [ ] 7.2 (REOPENED 2026-10-18 by LW-427, which turns the cache hide back on behind the heal hook, so the hide 0 and next relaunch wording below describes the old build; re run it with the same legs plus box 7.2a. Earlier history, PASSED 2026-10-17, owner live on prod of the LW-428 tree over 181da02d: two weapons at one tier, Chaos Blade drawn first, both poked to 10 kills live (lw381_tally_poke.py), a battle saved them, after the relaunch both card and small icons read tier 2 in the loose files and modded.pac and the launch pass judged 121 icons, wrote 0; reset leg, New Game detected 18:26:19, plain art restored the same session (wrote 3, 6 pac-patched), relaunch found all 242 surfaces plain; no blank icon anywhere, hide 0 every launch. Earlier PARTIAL after LW-423 efa0dae2: owner read both status pages with no blank row and the rim after a relaunch, every launch hide 0; the reset leg and the two same tier weapons leg still owed; FIRST RUN FAILED 2026-10-17 in sitting 1, owner live on prod 1c7ba75f: after a dev launch had seeded Chaos Blade to tier 3 and the prod launch dropped it to tier 1, Ramza's battle status page drew the Chaos Blade+ list row with no picture while the card kept its art; second sighting; LW-423 owns the fix, re run this box after it lands) **Glow rims follow the tally, no halo (LW-381, LW-383, LW-336, LW-295, LW-356, LW-287).**
  A weapon that crosses a tier in battle wears its new rim after the next relaunch, and no icon is ever blank in between (since LW-427 the new rim also shows the same session, see box 7.2a); rims come off after a tally reset; extended weapons rim like vanilla ones; enemies
  never brighten. Dev build, a weapon one kill short of a tier, win, open the inventory; two weapons
  at one tier with one drawn before the battle; a Warbrand at tier 1+ beside a below tier design;
  a battle with a player bow and an enemy bow of the same id; then a tally reset and relaunch. PASS:
  after a relaunch the new rim shows and both same tier weapons are rimmed, every list and card icon drew art before the relaunch, three rim strengths across tiers 1, 2, 3,
  Warbrand rimmed and the plain design plain, enemy bow plain, rims gone after reset; log `IconGlow:
  judged N icons, wrote N (...)` with hides above 0 only when a tier changed this session; no colored halo outside any
  weapon, shield or helm sprite (the vanilla haze stays). FAIL: a blank icon on any screen, a rim missing after the relaunch, a stale
  rim after reset, an enemy brightened, `IconGlow cannot locate modded.pac this launch`, `IconGlow
  standing down`, a tinted ring around an icon, or a blank list icon after a tier change between launches, up or down (LW-423). (DEV OK) (covers LW-381, LW-383, LW-336, LW-295,
  LW-356, LW-287) **[MAJOR]**
- [x] 7.2a (PASSED 2026-10-18, owner; the LW-442 race leg, memory leg, post battle run and shop list PASSED 07:35 to 08:36 on 6b6d320f, see the ledger row; owner live on the dev build of e8c5fbe6: Defender poked 3 to 0 then 0 to 1, the open inventory healed card and row 11 ms after the hide at 06:23:51, Equipment and Abilities healed its small icon cache at 06:24:53, the battle status page drew the ring, Chaos Blade never healed, twelve heals accepted and zero refused, and after save, load and relaunch the ring updated in place at 06:30:05; the one edge, a second change finding no texture record at 06:15:31, is LW-441) **A new glow ring shows the same session, on every screen (LW-427).** The dev build floors every tally to 3 on load and its thresholds are 1, 2 and 3, so every weapon is tier 3 at launch and setting a tally to 0 then getting one kill changes nothing. The tier change comes from the live poke `python tools/probes/lw381_tally_poke.py <id> <kills>` instead. (1) Dev deploy on the owner's go, game killed first. Launch, Continue. The file log must NOT show `The icon cache hook is not installed`, `IconCacheHeal stays off` or `IconCacheHeal could not hook the icon cache (`. (2) Open the party menu, then the inventory, once. The log shows `The icon cache hook is confirmed working; new glow rings can show in the same session.` If it is absent after both screens the hook is dead, stop. (3) Alt tab and run `python tools/probes/lw381_tally_poke.py 33 0` (Defender, tier 3 to 0). Within a few seconds the log shows the IconGlow judged line with `1 pac-patched` and `hide: 2 surface(s) hidden`, or, when the poke lands within 2.5 s of a menu change, the judged line with `hide: 0`, then `hide waits, a screen is still loading`, then the retry's `pending retry, hide: 2 surface(s) hidden.` (a ring that waits more than 10 s logs `same session glow rings have waited N seconds for the menus to go quiet` once); the line `left the drawn art on 2 icon surface(s)` instead means the healer is not armed, FAIL. (4) Open the inventory. Defender's list row and card draw the plain art within a tenth of a second, never blank. The Debug log shows `IconCacheHeal heal cache=0x... thread=N ei_033_uitx loader=accepted entry=present` and the same for `ei_s_033_uitx`; across steps 4 and 5 there are three to five heal lines in total, each cache and icon once. (5) Equipment and Abilities (small icon and card), then a battle and a unit's status page, show plain art everywhere with more heal lines and none refused. (6) Upward leg: `python tools/probes/lw381_tally_poke.py 33 1`, repeat steps 4 and 5, and the tier 1 ring shows everywhere. (7) Bait: Chaos Blade (37, unchanged at tier 3) draws unchanged on every screen and no heal line carries `ei_037` or `ei_s_037`. (8) Save, load, relaunch: the same art, no blank, `hide: 0` on the launch pass and no heal lines. (9) Optional: drop `lw427_trace.txt` into the mod folder before step 3 for `IconCacheHeal tracing on.`, lookup lines `IconCacheHeal lookup cache=0x... thread=N ei_033_uitx -> MISS` and the cost line `IconCacheHeal cost N lookups/s, mean N ns detour, mean N ns original` with the detour mean under 1000 ns; thread ids on lookup and heal lines should match. FAIL signatures, literal: `IconCacheHeal gave up on ei_033_uitx for cache 0x... after 3 tries`; `IconCacheHeal hit an internal error and left the icon alone (`; `loader=refused` or `entry=missing`; two heal lines with the same cache and icon; the kept-art line after a poke; a crash in the first menu after the poke (the loader premise is false and nothing can catch it); any blank icon; inventory stutter. Alternative flavor: `BuildLinked.ps1 -Prod` (no seeding, thresholds 5, 10, 15) with Defender at 0 in kills.json and five real kills or one poke to 5. PASS is steps 2 to 8 all as written. Race leg (LW-442), after step 8, poke two weapons so hides land, then open and close the inventory and Equipment and Abilities quickly 30 to 50 times; PASS is `heal deferred` lines during openings, every tracked icon drawn with the right ring, no fault, one entry per cache and zero attempt caps. With `lw427_trace.txt` present every lookup line on the build threads reads `key=stack` and every one on the draw thread reads `key=heap`; also finish a battle and go straight into the inventory. (DEV OK) (covers LW-427) **[MAJOR]**
- [x] 7.3 (PASSED 2026-10-18 on prod of a38eb35b, owner eye on the inventory list and the card icons with screenshots: all eleven knives, the eight ninja blades in the bag, three books and three bags each read as their own color with blade and grip, cover and strap, in two tones, the owner's yes on each family; the ninth ninja blade, the Omnilex and the fourth bag are not in the owner's bag and rest on B13's byte for byte icon gate; the books only show in the Inventory list under the Acquired sort, LW-451) **The four owed galleries (LW-198, LW-205, LW-210, LW-212).** The knives, ninja blades,
  books and bags were built and gated 2026-08-16 and have waited on the owner's eye since. Items
  tab plus the equip picker, walk each family. PASS: each of the 11 knives, 9 ninja blades, 4
  books and 4 bags reads as its own nameable color with a second material visible (hilt, page
  block, strap), no haze, no colored smoke, no speckle; the Omnilex keeps its deep vermilion. FAIL:
  two siblings one color, grey art under a tint, or an icon fuming colored smoke (the LW-236
  shape). (DEV OK, icon bytes are flavor independent) (covers LW-198, LW-205, LW-210, LW-212,
  LW-236; AWAITING-LIVE rows the owner flips) **[MAJOR]**
- [x] 7.4 (PASSED 2026-10-18 on prod of a38eb35b, owner eye on the inventory list and card icons with screenshots, the owner's yes on each family: nine katanas in the bag no two one color with Chirijiraden amber and Kiyomori cyan, two cloths dark, eight staves with the Staff of the Magi gold headed over a dark shaft, four spears, three harps with the Faerie Harp near its gold art, six poles, eight rods with the orb a lighter tone than the shaft; not in the owner's bag and resting on B13's byte for byte icon gate: Masamune and the eleventh katana, the third cloth, four spears including the Holy Lance, the Terrastaff and two more poles) **The families signed off from previews only (LW-204, LW-213, LW-209, LW-207, LW-211,
  LW-206, LW-208).** Katanas, dancer cloths, staves, spears, harps, poles and rods were committed
  but never eyeballed in game. Same walk. PASS: eleven katanas no two one color (Masamune blue
  with a gold fuller, Chirijiraden amber, Kiyomori cyan with a venom edge); three dark cloths;
  eight staves apart with Staff of the Magi gold headed over near black; eight spears with the Holy
  Lance blue bodied, gold trimmed; three harps with Faerie Harp near its gold art; nine poles with
  Terrastaff and Greenwood Pole told apart at a glance; eight rods with the orb a third tone; no
  colored smoke on any of them. FAIL: Masamune gold, Chirijiraden blue, Muramasa violet, Holy Lance
  gold bodied, a red Duskstring, a rod whose orb and shaft read one color, a smoke fuming icon.
  (DEV OK) (covers LW-204, LW-213, LW-209, LW-207, LW-211, LW-206, LW-208, LW-236) **[MAJOR]**
- [ ] 7.5 (PARTIAL 2026-10-18 on prod of a38eb35b, owner live with five screenshots: Siren's Lyre May cast Confuse on hit, Duskstring Harp May cast Blind on hit with no Absorbs HP badge, Faerie Harp May cast Sleep on hit, Glarebound Tome May cast Blind on hit, Binding Codex May cast Immobilize on hit; the two in battle proc reads are still owed) **Cast clause cards and the two retuned procs (LW-331, LW-355, LW-352).** Siren's Lyre,
  Duskstring Harp, Faerie Harp, Glarebound Tome and Binding Codex each name their on hit status
  (Confuse, Blind, Sleep, Blind, Immobilize per docs/living_weapon_grid.csv); the Duskstring Harp
  shows NO Absorbs HP badge. One hit each with Siren's Lyre and Glarebound Tome in battle. PASS:
  the five cards carry the clause, the harp badge is absent, and when the proc fires it is Confuse
  (lyre) and Blind (tome). FAIL: a card with no cast clause, Charm or Intimidate on a proc, a
  stray badge. (DEV OK) (covers LW-331, LW-355, LW-352) **[MINOR]**
- [ ] 7.6 (PARTIAL 2026-10-18 on prod of a38eb35b, owner live on the Equipment and Abilities picker with screenshots: Hushblade, Claymore, Graviton, Arcanum, Rod of Faith, Staff of the Magi and Kiku-ichimonji each draw their whole card inside the frame with no blank line under Kills, nine lines at most; the Holy Lance is not in the owner's bag and there is no give all rig on this build, so it rests on the CARD HEIGHT gate (B3) and on the Hushblade, which is one character longer; the cold boot dash read is owed on the next launch) **Tallest cards fit, dashes before the paint (LW-333, LW-311).** Open the six longest
  cards by assembled text with the color tags stripped, Hushblade (4), Holy Lance (104), Rod of
  Faith (58), Arcanum (30), Claymore (22) and Graviton (29), plus the Staff of the Magi (66, the
  most lines under a plain 33 character wrap) and the Kiku-ichimonji (45): description plus Kills
  line inside the frame, no blank line under Kills (box B15 re prints the six so a later retune is
  noticed). On a cold boot open a living weapon
  card within the first seconds. PASS: it reads `Kills: -/- to +` first and the real counter lands
  over it; never a literal `Kills: 0/5` on a weapon with kills. FAIL: text clipped at the bottom, a
  false zero, a counter that never paints. (DEV OK) (covers LW-333, LW-311) **[MINOR]**
- [x] 7.7 (PASSED 2026-10-18 on prod of a38eb35b, owner eye on the inventory lists with screenshots and the owner's yes on each family: the swords and the seven knight swords from the 7.1 and 7.6 shots with Ravager apart from Defender and Sunderer apart from Save the Queen and a gold Lightbringer absent from the bag, eight bows each their own color with the string read normal, six crossbows and six guns each nameable, sixteen shields two toned with the Genji Shield steel blue and no crimson, thirteen helms with Mendsteel the one green and Timeward the one bright metal, twelve hats with their emblem zones, haze neutral; nothing back on a one hue stamp, no colored cloud) **The wardrobe regression walk (families already passed in game).** Swords, knight
  swords, bows, crossbows, guns, shields, helmets, hats keep the look the owner approved in
  August. PASS: fifteen colored sword blades with a metal ridge and a gold Lightbringer; Ravager
  apart from Defender and Sunderer apart from Save the Queen; nine bows with the string a bright
  line (Frostarc limb rime expected); six crossbows and six guns each nameable; sixteen two tone
  shields with Genji Shield steel blue under its vanilla name; thirteen helmets with Mendsteel the
  one green and Timeward the one bright metal, crisp engraved lines kept; twelve hats with their
  emblem zones; neutral haze around shields and helmets. FAIL: any item back on the old one hue
  stamp, a colored glow cloud, a crimson Genji Shield. (DEV OK) (covers LW-189, LW-190, LW-197,
  LW-199, LW-200, LW-201, LW-202, LW-203, LW-215, LW-216, LW-230, LW-231) **[MINOR]**

---

## 8. The weapon color cut holds in release (LW-446)

> Battle sprite colors are not a 2.4.0 feature. The owner ruled them out on 2026-10-18 (LW-412
> withdrawn, redesign LW-447 for 2.5.0, docs/DESIGN.md section 8). The painter stays in dev builds
> under the dev flag, so the only thing to check on prod is that the cut holds. No sprite color
> reads belong in this document.

- [x] 8.1 (PASSED 2026-10-18 on prod of 855a00eb, owner live: every Parts report line of the launch names the thirteen parts in order with weapon colors absent, the Select-String returns only the report lines, zero Painted, Restored or Palette lines, the owner swung the Cutpurse at 12:43 and read plain steel with a screenshot, Battle ended 12:45:43, scan CLEAN; environment note from B20: FFTColorCustomizer, paxtrick.fft.colorcustomizer, is enabled and loads first in every sitting of this pass) **A prod build never mentions weapon colors and swings everything in vanilla steel
  (LW-446).** After a sitting on prod, run
  `Select-String -Path "$env:RELOADEDIIMODS\prawl.fft.livingweapons\livingweapon.log" -Pattern 'Parts report:|weapon colors|Painted weapon|Restored weapon|Palette .* bank|Weapon colors'`.
  PASS: every `Parts report:` line names exactly thirteen parts in this order, battle sensing,
  kill counting, stat growth, card paint, weapon powers in battle (Maim, Plague and the rest),
  weapon commands, banners, extended inventory, twin weapons, bulwark, gun growth, poach den,
  provoke; the search returns no other line; one swing of the Cutpurse (id 1) in battle draws the
  vanilla steel knife; the glow rings on the list and card icons are unchanged. FAIL: a fourteen
  part line, the words weapon colors anywhere in the log or a pause box, any Painted, Restored or
  Palette line, or a colored swing. (A dev build fails this box by design; `build_flavor.txt`
  must read prod first.) **[MAJOR]**

---

## 9. The thirty signature tour (LW-325) plus Provoke, Bulwark, Living Poach and the twins

> Every signature weapon at its tier once (the 30 signature blocks in data/items.json; the +3
> ability column of docs/living_weapon_grid.csv names 29 of them; Materia Blade, id 32, is the
> always on Ultima with an empty +3 cell). Watch the FILE for the once per
> battle narration. Each defect found gets its own Backlog row; LW-326 (Puppeteer reliability) is
> already open. All boxes DEV OK unless noted. Boxes 9.1, 9.3 and 9.5 to 9.11 together ARE the
> LW-325 tour; 9.2 and 9.4 are the Provoke and Bulwark edge legs.

- [ ] 9.1 **Provoke, the whole cluster (LW-123, LW-127, LW-129, LW-130, LW-131, LW-133, LW-135,
  LW-138, LW-118; LW-136 optional).** A tier 3 Defender (id 33) wielder in Knight or Squire has
  Provoke; shouting at one enemy hides the party from it until its turn ends. Full pass in
  docs/PROVOKE_AC.md. PASS: `The Defender wielder now has Provoke in Knight's action list`; `The
  Defender's provoke takes hold; the marked enemy can see nothing but the Defender.`; the marked
  enemy attacks only the bearer; other enemies still see the party; no Invisible icon over hidden
  party units; `The provoke hold ends (EnemyTurnDone); every hidden unit is visible again.` within
  seconds of that enemy's turn end with `N units were ever hidden` greater than 0; a shout at an
  ally leaves no lingering mark and a second shout is not refused; one of the shouts lands on an
  ARCHER or a MAGE and that unit's next action targets the bearer (a bolt or a spell on him, or a
  move toward him), nothing else on the party (Provoke has only ever been watched on melee
  enemies); no `Provoke's effect was not set up` WARN. Optional: two Defenders fielded, the hold
  refuses and the mark is scrubbed (`A provoke mark landed on an enemy while the shout could not
  take hold, so the runtime scrubbed it off; that enemy can be shouted at again.`). FAIL: Provoke
  missing, the hold never arms, hidden units hit, a spell or an arrow on a hidden party unit while
  the hold is up, `0 units were ever hidden`, or the watchdog release `The provoke hold timed out
  and released on its own`. (covers LW-123, LW-127, LW-129, LW-130, LW-131, LW-133, LW-135,
  LW-136, LW-138, LW-118) **[MAJOR]**
- [ ] 9.2 **Provoke survives a quit and a reload mid hold (the LW-130 open question).** A player
  who saves and quits while the shout is working never comes back to an invisible party or an ally
  wearing the mark. In the 9.1 battle: cast Provoke, let one more unit act so the game autosaves
  with the hold up, Quit to title, Continue that autosave. PASS: every party unit visible on the
  reloaded field, no Invisible or Provoked icon on any party unit, the marked enemy's mark gone or
  readable only on the enemy, a kill still credits afterwards, no `The provoke hold timed out and
  released on its own` on the reloaded battle. FAIL: a party unit invisible after the load, a mark
  on a party seat, the watchdog release firing on the reloaded battle. (DEV OK) (covers LW-130 the
  save edge half; docs/PROVOKE_AC.md's open question) **[MAJOR]**
- [ ] 9.3 **Bulwark at its new id (LW-144, LW-141, LW-145, LW-366).** A tier 3 Sunderer (id 263)
  wielder who full waits plants the blade and bars the one tile behind. The 263 build has NEVER
  been seen firing (docs/TODO.md LW-366), so this is a real leg. docs/BULWARK_AC.md holds V1 to V6.
  PASS: `The Sunderer wielder plants its blade at (x,y) facing N; the ground at its back (bx,by) is
  barred.`, the toast `The Sunderer plants its blade; none may take the ground at its back.`, the
  red no entry cursor on the back tile only, then `The Sunderer's Bulwark releases (...); the
  ground at its back settles back to normal.` on the next turn with the tile normal again. FAIL: the
  tile in FRONT barred, a tile still barred after release, a raised tile or softlock, or no plant
  at all at id 263. (covers LW-144, LW-141, LW-145, LW-366) **[MAJOR]**
- [ ] 9.4 **Bulwark's edges (docs/BULWARK_AC.md V3 to V6 and P1).** The stance never bars the wrong
  tile and never leaves one barred: on the map's edge, across a Retry, over a corpse, or into the
  next fight. In the 9.3 battle, in order: (1) stand on the map's edge row with the back to the
  void and full wait; (2) turn around, back into the map, full wait; (3) with a plant up, lose and
  Retry; (4) plant again in a different facing, then kill an enemy on the back tile and Raise it;
  (5) one more full wait plant, then win and enter the next battle. PASS: (1) no plant, no toast,
  no red cursor anywhere, the file carries the DBG `Bulwark refused to plant: the wielder's back is
  to the map edge`; (2) the real back tile bars; (3) after the Retry no tile is barred and the next
  plant works; (4) the corpse stays, the Raise lands, the barred tile is the one behind the new
  facing; (5) the release line prints at battle end and the next battle's ground is clean
  everywhere. NOT a failure: the edge refusal is a silent DBG line with no toast, by design. FAIL:
  a plant with the back to the void, a tile barred after the Retry or into the next fight, a Raise
  refused on the back tile, a raised tile or a softlock. (DEV OK) (covers LW-141, LW-145 the
  ground restore half) **[MAJOR]**
- [ ] 9.5 **Living Poach (LW-167; LW-386 watch).** A Poach unit landing a basic Attack kill with a
  dormant formula living weapon (a Cutpurse works) on a poachable monster gets a carcass in the
  Den, a toast, no crystal; an ABILITY kill does not fire it. PASS: `The weapon's spirit claims the
  carcass: <name> added to the Poacher's Den.`, the corpse vanishes with no crystal, the Den lists
  the carcass, and the Den hands out the right item for that monster. FAIL: a toast but no Den
  entry, a crystal plus a Den entry, a wrong Den item (the LW-386 player report), or `Living Poach
  is paused for this session because the Poacher's Den store is not where this mod expects it`.
  (covers LW-167) **[MAJOR]**
- [ ] 9.6 **The twins and their consent rules (LW-168, LW-171, LW-193, LW-194).** A tier 3 Outrider
  Pistol (id 71) or Arbalest (id 79) wielder with NO support picked gets Dual Wield and a twin in
  an EMPTY off hand after a Status visit; anything the player equips there is respected; the twin
  never appears or moves while gear can be edited. The twin must be stamped BEFORE the battle is
  built: equip the pistol on the world map, visit Status (the stamp line prints), then enter the
  battle. Extra legs: (a) equip the pistol on the FORMATION screen of a battle with the off hand
  empty and start it; (b) with the pistol in the main hand and the off hand empty, open the weapon
  picker on that unit and sit ten seconds, back out to the equip screen and sit ten more, then
  back out to Status; (c) enter a shop, open the Fitting Room on that unit, try a weapon, leave
  without buying, then back on the party screen press Best Fit on a Sunderer wielder. PASS: `A
  twin weapon is equipped in the wielder's off-hand; ...` on the empty hand at the Status visit;
  Attack fires twice in a battle entered with the twin already stamped; a shield equipped there
  gives `A shield is equipped, so the twin weapon steps aside ...` and the shield is never lost; a
  second gun the player puts in the off hand by hand gives `The wielder already carries a second
  weapon of their own in the off hand, so the twin weapon steps aside; ...` and both guns still
  fire on the player's own Dual Wield (LW-426); unequipping the source gives `The twin weapon is removed; the wielder's original
  off-hand gear is restored.` and the support slot is BLANK (no Toadja); the twin weapon's card
  teaches the empty hand rule; leg (a) fires ONE shot that battle with no stamp line during it and
  no warning, and the next battle entered from the map after a Status visit fires twice (this buys
  the twin-dualfire-construction-bound flip); leg (b) shows no stamp line and no off hand change
  until the Status page; leg (c) shows no `A twin weapon is equipped` line timestamped inside the
  shop, no `twin-refund` record, and Best Fit either keeps the Sunderer or returns it to the bag
  with the count up one. NOT a failure: one shot in a battle whose off hand was EMPTY when the
  battle was built (dual fire is welded at construction; a mid battle stamp reaches neither the
  shot count nor the field art). FAIL: one shot in a battle entered with the twin already stamped,
  a shield destroyed, a pistol minted into the bag, a phantom support ability, the twin stamping
  over player gear, a stamp line while the picker, the equip screen or the shop is open, a
  `twin-refund` record, a Sunderer lost on Best Fit. (covers LW-168, LW-171, LW-193, LW-194)
  **[MAJOR]**
- [ ] 9.7 **The katana trio (Iai, Kobu, Mushin).** Three katanas, three different tricks, one
  battle each or all together. PASS: Ame-no-Murakumo (42) takes the literal first turn then
  releases (`released by the turn flags`); Kiyomori (43) raises the wielder's Brave to a braver
  foe's on a strike, capped 97; Kiku-ichimonji (45) banks a charge on a full Wait (`The
  Kiku-ichimonji wielder stands perfectly still through its turn; the next strike is charged`) and
  spends it (`...charged strike lands; Mushin's boost is spent`), a move then wait banks nothing,
  two waits do not stack. FAIL: a false Iai release on a pre turn strike, Brave moving the wrong
  way, a stacked Mushin. **[MAJOR]**
- [ ] 9.8 **Knives and swords.** Each blade's passive or trick fires once and the file says so.
  PASS: Gloomfang (3) Concentration, Hushblade (4) Magick Def Boost, Sanguine Gauche (6) Defense
  Boost and Mortal Coil (8, below half HP) Attack Boost each log one grant line with
  `readback=SET`; a Zwill (10) kill grants an immediate extra turn (`The extra turn finished
  (consumed) after 1 turn ends`); Sanguine Sword (23) shows Shadow Blade castable; Wrathblade (27)
  survives a lethal hit playing dead and auto revives with no crystal; Swiftedge (28) Speed climbs
  +1 per acted turn to +5 and a hit resets it; Arcanum (30) steals a buff onto the wielder for 3 of
  its turns; Materia Blade (32) PA tracks current HP percent with no flap flood. FAIL: a
  `readback=MISS` grant, a missing extra turn, a crystal, a ramp that does not step or reset, PA
  that ignores HP. (covers LW-158 live half, LW-94 the Afterimage turn lane) **[MAJOR]**
- [ ] 9.9 **Rods and staves.** Each caster weapon's trick fires once and the file says so. PASS:
  Umbral Rod (56) restores a tenth of HP and MP on a NEW tile (`MP already full` is the correct no
  op); Rod of Faith (58) grants teleport movement below 30 percent HP (`readback=SET`, move id
  243); Warlock's Staff (60) bearer casts instantly, two bearers both do; Mending Staff (61) mends
  allies within one tile for a tenth of max HP at the wielder's turn end (`1 ally was mended for N
  HP at the wielder's turn end.`); Sanctus Staff (64) boosts ally heals by 30 percent while the
  wielder was the last player to act (the `healBoostPct` of 30 on id 64 in data/items.json; the
  file reads `ally HP rises boosted 30 percent`), a near max heal refused unboosted; Staff of the
  Magi (66) holds fallen allies at 3 hearts (`Divine intervention holds the fallen ally at tile
  (x,y) at 3 hearts`). FAIL: a silent Renewal while the wielder visibly acts (the LW-94 lane), a
  crystallized ally under Sanctuary, a charged cast under Choir. (covers LW-153 live half, LW-94
  the Renewal turn lane) **[MAJOR]**
- [ ] 9.10 **Bows, crossbows and the lance.** Each ranged weapon's trick fires once and the file
  says so. PASS: Eclipsebolt (78) snaps only its OWN Doom procs to 1 (`the bow's own Doom procs
  are hastened to one`; a Doom from another weapon logs exactly one `appeared outside the
  Eclipsebolt wielder's own action ... its countdown (3) is left alone` and ticks naturally);
  Venombolt (80) poison never cures, ticks harder, never lands the killing blow, and survives a
  victim's mid battle level up (`re-anchoring the hold`); Stormarc (86) chips up to three nearby
  foes at decaying damage (a silent write, read the unit cards); Huntress (89) strips a struck foe's
  reaction for 3 of its turns (the `crippleTurns` of 3 on id 89 in data/items.json); Yoichi Bow
  (90) shows Barrage castable with its correct name and description and it survives the learn
  screen; Holy Lance (104) is box 3.4. FAIL: a foreign Doom hastened (the LW-95 regression), a
  cured Plague, a Barrage with vanilla text. (covers LW-95, LW-97 the ability text sibling; LW-325
  with 9.1, 9.3, 9.5 to 9.9 and 9.11: every one of the 30 signature ids in data/items.json seen
  once) **[MAJOR]**
- [ ] 9.11 **Puppeteer (Galewind 9; LW-326 known).** A +3 hit turns the struck enemy into the
  player's puppet for its own next turn. PASS: the file shows `puppeteer gate: ACTIVE: the next
  struck enemy becomes your puppet` (DBG) on the wielder's act, the hit dominates the struck enemy,
  the player controls it on ITS turn, it releases on its own turn (`Puppet control ended after the
  enemy took its turn`), and the cooldown refuses a second dominate. Record how many strikes the
  arm needed; the known defect is a quick confirm missing the arm and a dominated monster showing
  nothing. FAIL: release on the wielder's clock, or two puppets at once. **[MINOR]**

---

## 10. Compatibility (docs/COMPATIBILITY.md)

- [ ] 10.1 **The WotL Restoration dual mod run (LW-408).** Both mods installed, WotL Restoration
  loading BEFORE Living Weapons. PASS: the parts report reads all ON, extended inventory included;
  WotL Restoration's own log prints `Item expansion NOT armed`; Luso and Balthier's events still
  fire; the Terrastaff card reads Terrastaff with its kill counter; Agrias's birthday hands out a
  Septième Sens. The owner flips the docs/TODO.md row to AWAITING-LIVE before the run and PROVEN
  or not after. Leg B, the WRONG order (Reloaded orders by install, so half of dual mod players
  run it first): swap the order so Living Weapons loads first, launch, open the Terrastaff card,
  one Terrastaff kill, swap back. PASS leg B: the card reads Moonblade with no Kills line (text
  damage only, as docs/COMPATIBILITY.md says), `Terrastaff claims kill number N` still in the log,
  all parts ON, `Item expansion NOT armed` still in WotL Restoration's log, no crash. FAIL: the
  Terrastaff reading as Moonblade in the RIGHT order, a kill counter lost on ids 261 to 267,
  extended inventory OFF, an event that no longer fires, a crash or an uncredited kill in the
  wrong order. (covers LW-408; docs half in B16 with LW-410, LW-169) **[MAJOR]**
- [ ] 10.2 (OPEN 2026-10-17: every launch of sitting 1 read base 0x7FF65FC60000 and each armed and credited a kill; the base only moves across a Windows restart, so one launch after a reboot closes this box) **Two launches, two bases (LW-387, LW-388, LW-132).** ASLR moves the game each launch and
  the mod follows. Compare the `Game image base` line across two launches with a Windows restart between them (Windows picks a module's random base once per boot, so every launch inside one boot reads the same base). PASS: the
  base differs at least once and both launches armed and credited a kill. FAIL: a crash at boot, a
  thunk entry landmark refusal, extended inventory not armed on either launch. (DEV OK) (covers
  LW-387, LW-388, LW-132) **[MAJOR]**
- [ ] 10.3 **The helper mod is missing and the mod copes (README's own promise).** A player whose
  Reloaded did not fetch `reloaded.sharedlib.hooks` still gets kill counting, growth and cards,
  loses only the banners and the seven new items, is told what to enable, and sees NO box (an
  absence is not a failed check, so it never boxes; LivingWeapon/Guard/Part.cs). Dev deploy: untick
  `reloaded.sharedlib.hooks` in Reloaded-II (if the launcher re enables it as a declared
  dependency, move its folder out of the Mods folder instead and accept the launcher's missing
  dependency warning), launch, Continue, one battle with a kill, quit, put it back, launch again
  and open a Warbrand card. PASS: both WARNs in the file, `Banners (the pop-ups that announce a
  weapon leveling up) are paused for this session because the game-hooks helper mod
  (reloaded.sharedlib.hooks) is not enabled. What to do: enable the reloaded.sharedlib.hooks mod in
  Reloaded-II and launch the game again.` and `The new items are paused for this session because
  the game-hooks helper mod (reloaded.sharedlib.hooks) is not enabled; everything else keeps
  working. What to do: ...`; the Parts report reads `banners OFF (the loader gave no hooks);
  extended inventory OFF (the loader gave no hooks)` with the other twelve ON after the first
  battle; `Living Weapons: all parts armed.` never prints; ZERO boxes; no `flight_*_standdown.jsonl`
  from this launch; the kill credits and the card paints; ids 261 to 267 absent from every list
  with no crash; `python tools/scan_logs.py --require-battle` exit 0; on the re enabled launch
  `Extended inventory armed: 7 new item(s)` and the Warbrand is back with its count. Optional
  worst day leg (the `Living Weapons has paused itself for this session` box has never been
  shown): helper still off, drop `LW_FORCE_ROSTER_MISMATCH` and `LW_FORCE_SENSING_MISMATCH`,
  launch, wait fifteen seconds at the title, Continue. PASS: one box headlined `Living Weapons has
  paused itself for this session` with no Still working group, all fourteen under Paused this
  session, the roster drill sentence and the footer; the Parts report fourteen OFF. FAIL: any box
  on the plain leg, a stand down, kill counting OFF, `Startup failed`, a crash at boot, the
  Warbrand count lost after re enabling, the `some features are paused` headline with an empty
  Still working group on the worst day leg, or Reloaded refusing to launch the mod at all (then
  README.md's "if the mod does not start, install it" is the only player guidance and must be re
  read against what the launcher said). (DEV OK) **[MAJOR]**
- [ ] 10.4 **Another game language does not crash on the new items.** Ids 261 to 267 have text in
  English only (the mod ships `item.en.nxd` and nothing for another language), and nobody has
  watched what a French or Japanese list does with a row its text table does not have. Steam, game
  Properties, Language, pick French (a text pack download may follow), launch, Continue a save
  holding a Warbrand, open the Items tab, the equip picker and the Warbrand card, swing it once in
  a battle, quit, switch the language back. PASS: no crash or hang on any list or card; the
  Warbrand row shows SOME label (blank or a placeholder is acceptable; write down what it showed);
  vanilla items read French; no Kills line anywhere (expected, README.md's language section); the
  log still credits `Warbrand claims kill number N`; all parts armed; `Extended inventory armed: 7
  new item(s)`. FAIL: a crash or hang opening a list or card holding an extended item, a vanilla
  item wearing the English mod text, a kill uncredited. On a FAIL the README language section and
  docs/COMPATIBILITY.md's language row need a Backlog row before ship. (DEV OK) **[MAJOR]**
- [x] 10.5 (PASSED 2026-10-17, owner: Configure Mod opens an empty pane with no error, no tile markers seen in sitting 1) **Treasure Master is gone, the config pane is empty (LW-10).** Reloaded-II launcher,
  select prawl.fft.livingweapons, Configure Mod. PASS: the pane opens empty with no error; no tile
  markers in any battle; the Scholar's Ring card reads `Boosts JP earned.` and never mentions a
  module. FAIL: a launcher error on Configure, a tile marker, a module mention. (covers LW-10)
  **[MINOR]**

---

## 11. Regression leftovers (the 2.3.0 pass condensed to what still has a surface)

> The 2.3.0 pass's blocker rows (tallies persist, battle edges, the world map re enter) now live
> in box 5.3; what remains here is the minor furniture.

- [x] 11.1 (PASSED 2026-10-17, owner live: Gariland enemies walk, Sunsteel Helm, Empyrean Robe and the Squire's Equip Axes text read as written) **The item pass holds in an early battle (LW-188, LW-97, LW-99).** Chapter 1 enemies
  walk, they do not hover; the Sunsteel Helm card ends `Jump +1.` and the Empyrean Robe card ends
  `Wards against Silence.`; the Squire's Equip Axes description explains every axe was reforged
  (intended dead JP, not a broken install); Beowulf's hand holds the Lightbringer. PASS: no floating
  generic enemies, the three cards read as written. FAIL: a parade of hovering knights, vanilla
  Equip Axes text, id 31 named Nagrarok. (covers LW-188, LW-97, LW-99) **[MINOR]**
- [x] 11.2 (PASSED 2026-10-17: across sittings 1 and 2 the logs carry zero Mark narration, zero Mark toasts and zero CharmLock, LifeSap or Wyrmblood lines; the undead half on dev 62015755 at 13:19:52, Outrider Pistol felling an undead foe, moved its legends counts fourth number from 15 to 16 with lastVictim class 3, and no deed-miss record in any afternoon tape) **Marks stay hidden while legends grow; nothing dormant speaks.** Every launch header
  reads `The legends hold deeds for N weapons and M Marks (legends.json, primary)` with N growing
  across the pass; battle ends count Marks in the FILE only; zero Mark narration or toast lines
  anywhere; zero CharmLock, LifeSap or Wyrmblood lines (their lanes were deleted in LW-159, so any
  such line is a ghost). One living weapon kill on an UNDEAD (skeleton, ghoul, revenant) during the
  pass, if one is at hand: after that battle the weapon's entry in `legends.json` shows its
  `counts` fourth number one higher and `lastVictim` reading the undead, and the tape has no
  `deed-miss` for that slot (the undead classifier has never fired live, docs/VERIFY_LIVE.md row 8);
  no undead battle at hand, flip that half NOT REACHED. PASS: nothing to see on screen, the file
  counts as described. FAIL: a Mark toast, a dormant module line, the undead count landing in the
  human or monster slot, a `deed-miss` on a real kill. (covers LW-159 live half) **[MINOR]**
- [x] 11.3 (PASSED 2026-10-17, owner live on prod with Blue And Red Mages enabled: Red Mage and Blue Mage abilities intact, the guard armed) **Red and Blue Mages still compose (LW-77 era prune).** With Blue And Red Mages enabled
  on the prod deploy, Red Mage keeps its abilities with no hand edit, Blue Mage is intact, the
  guard arms. PASS: all three. FAIL: a missing Red Mage ability, a part OFF. **[MINOR]**

---

# PART B. Scripted from the dev box, game closed (the owner does not need to be present)

> Every box names the exact command, the expected output, and that it runs from the
> worktree root with the game closed, one dotnet at a time, unpiped (read `$LASTEXITCODE`
> directly; a piped gate reads the tail's exit). The owner ticks the box on the run's report. A
> box that needs the game running is never here. Blockers first.

## B. Build and release gates, offline proofs

- [x] B1 (PASSED 2026-10-17 on main cdafa581 after all three rows merged: build 0 warnings, Passed 4134, Failed 0) **The whole suite is green, with a count.** `dotnet build LivingWeapon.Tests\LivingWeapon.Tests.csproj -c Release`
  then `dotnet test LivingWeapon.Tests\LivingWeapon.Tests.csproj -c Release`. Run it on main AFTER
  the three parallel rows (LW-416, LW-413, LW-414) and their ledger exits have landed there; main
  already carries the ProofClaim baseline fix for docs/TODO.md at 7 (committed 2026-10-17), so the
  TODO.md drift is gone and only a clean count is acceptable. PASS: 0 warnings on the build;
  `Passed! - Failed: 0, Passed: N` with N at or above 4089 (main on 2026-10-17; the parallel rows
  add tests) and the number written next to this box. FAIL: any failed or skipped test, a warning,
  or a run taken before the three rows merged (not a fail, not a pass; run it again). (covers every test
  only and refactor row: LW-147, LW-149, LW-150, LW-151, LW-152, LW-153, LW-154, LW-155, LW-157,
  LW-158, LW-160, LW-182, LW-186, LW-260, LW-265, LW-266, LW-267, LW-269, LW-375, LW-391, LW-145)
  **[BLOCKER]**
- [x] B2 (PASSED 2026-10-17 on 6016d943: DocsContractTests 24, TodoContractTests 38, 175 ledger slugs) **The docs and the ledgers pass their own gates (LW-102, LW-169, LW-183, LW-278, LW-275,
  LW-256, LW-60).** `dotnet test LivingWeapon.Tests\LivingWeapon.Tests.csproj -c Release --filter "FullyQualifiedName~DocsContractTests"`
  and the same with `TodoContractTests`. PASS: both green, which proves this file is in the top
  level allow list and carries its CONTRACT stamp, every doc path reference in the repo resolves,
  no production comment cites a ledger row that does not exist, the two deleted icon docs
  (ICON_VERDICTS and ICON_STYLE_BIBLE, LW-278) stay absent from the top level,
  docs/archive/SMOKE_TEST_2.3.0.md carries ARCHIVED, every docs/LIVE_LEDGER.md claim header carries
  a slug (`grep -c "^### \[" docs/LIVE_LEDGER.md` reads 175 or more), and no work row hides under
  Walled or Format in docs/TODO.md. FAIL: either filter red. (covers LW-102, LW-169, LW-183,
  LW-278, LW-275, LW-256, LW-60) **[BLOCKER]**
- [x] B3 (PASSED 2026-10-17 on 6016d943: analyze.py exit 0 both ways, no deferral, spriteIdOverride 0) **The dominance gate and its sibling gates, both ways (LW-363, LW-352, LW-166, LW-333,
  LW-293, LW-414).** `python tools\analyze.py` then `python tools\analyze.py --baseline`. PASS: both
  exit 0 with `PASS: no item is strictly dominated` and NO `(1 owner-ruled deferral` tail
  (`grep -n "DOMINANCE_DEFERRED = {}" tools/analyze.py` hits); the GROWS PHRASE, CLAIMS, POACH
  NOTICE RETIRED (`PASS: no item's card carries the retired "No Poaching." notice.`), CARD HEIGHT
  (`PASS: every card renders inside the box.`), grows lockstep and grid sync rows all PASS;
  `grep -c spriteIdOverride data/items.json` reads 0. FAIL: any nonzero exit, a deferral tail, a red
  gate row. (covers LW-363, LW-352, LW-166 source half, LW-333 offline half, LW-293, LW-150, LW-414
  the analyze half) **[BLOCKER]**
- [x] B4 (PASSED 2026-10-17: Dev {1,2,3}, Prod {5,10,15}, ProvokeEnabled true) **The thresholds and switches read as the release intends (LW-161, LW-133).**
  `grep -n "Thresholds = \|ProvokeEnabled" LivingWeapon/Tuning.cs`. PASS: `DevThresholds = { 1, 2,
  3 }`, `ProdThresholds = { 5, 10, 15 }`, `ProvokeEnabled = true`. FAIL: any other numbers or false.
  (covers LW-161 offline half, LW-133) **[BLOCKER]**
- [x] B5 (PASSED 2026-10-17 on 6016d943: prod publish carries every dev string 0, DrillTrigger 1, production 2) **The prod DLL carries no dev lever (LW-413 offline half, LW-155).**
  `dotnet publish LivingWeapon\LivingWeapon.csproj -c Release -o $env:TEMP\lw240dll` (the
  worktree's own output, never the Mods folder), then
  `python -I -c "import pathlib,os;b=pathlib.Path(os.environ['TEMP'],'lw240dll','LivingWeapon.dll').read_bytes();[print(s,b.count(s.encode())+b.count(s.encode('utf-16-le'))) for s in ['GetAsyncKeyState','ProvokeSpike','provoke-spike','StatusSpike','NumeralSpike','BodyDoubleSpike','DrillTrigger','development','production']]"`.
  PASS: `GetAsyncKeyState`, `ProvokeSpike`, `provoke-spike`, `StatusSpike`, `NumeralSpike`,
  `BodyDoubleSpike` and `development` each read 0; `DrillTrigger` reads exactly 1 (the type is
  always compiled, only its Mod.cs call site is dev only, LivingWeapon/Guard/DrillTrigger.cs);
  `production` reads 1 or more; the LW-413 contract test (every GetAsyncKeyState poll under `#if
  LWDEV`) green inside B1. FAIL: any dev string above 0, which means a hotkey poll shipped, or
  `production` at 0. (covers LW-413, LW-155) **[BLOCKER]**
- [ ] B6 (PARTIAL 2026-10-17: every clause passed on 6016d943 except the version, which reads 2.3.4 until the ModVersion bump; re run at release) **Publish.ps1 packages a clean prod zip into a scratch folder.**
  `.\Publish.ps1 -OutputPath $env:TEMP\lw240` (the staging folder is `Publish\prawl.fft.livingweapons`
  under the repo; the Mods folder is never touched). PASS: exit 0; package verify PASS on every
  `$RequiredModFiles` row (meta.json, glow_icons/manifest.json, the 7 tables with
  JobCommandData.xml absent, the three nxds item, ability and uistatuseffect, the extended
  inventory XML and the two 261 icon parts files, the DLL); no `*.bloodpact_parked` artifact
  inside; the zip is `FFTLivingWeapons-2.4.0.zip` with one top level wrapper folder; the packaged
  ModConfig.json reads `"ModVersion": "2.4.0"` with `fftivc.utility.modloader` and
  `reloaded.sharedlib.hooks` under ModDependencies. FAIL: any missing required file, a parked
  artifact, a 2.3.4 version in the zip. (covers the Publish half of LW-297) **[BLOCKER]**
- [x] B7 (PASSED 2026-10-17 on 6016d943 after the 402fc94d pass line fix: stamp 0x6A5E4A1B, every pinned site file backed, == neither 0) **The exe on disk still matches every pinned landmark (LW-387, LW-388, LW-391, LW-132,
  LW-393, LW-398, LW-399, LW-414).** `python -I tools/probes/sentinel_file_image_probe.py`,
  `python -I tools/probes/static_table_file_image_probe.py`,
  `python -I tools/probes/band_array_file_image_probe.py` against the installed exe, and
  `sed -n '21,22p' LivingWeapon/Guard/LaunchGuard.Landmarks.cs`. PASS: every probe exit 0 and
  reporting stamp `0x6A5E4A1B` size `0x03F9E000`; the sentinels file backed as docs/LIVE_LEDGER.md
  records (slot0 and slot9 `ff ff ff ff`, mode `ff`); the five static table sites matching their
  vanilla bytes; the band span reads zero until the game writes it (the probe line ends "0 of 25088 bytes file backed, 0 nonzero, 0 outside any section") and the static unit array span is fully file backed with exactly four nonzero bytes, ff ff ff ff at 0x14089AB98 to 0x14089AB9B (the probe line ends "15360 of 15360 bytes file backed, 4 nonzero, 0 outside any section"); the static table
  probe's item stats line ends `== neither 0` and its "rows where the file matches neither
  baseline nor proposed" list is empty (the LW-414 fix); Landmarks.cs reads
  `ExpectedTimeDateStamp = 0x6A5E4A1B` and `ExpectedSizeOfImage = 0x03F9E000`; the
  ImageLiteralContractTests green inside B1. FAIL: any probe reporting a differing byte or a new
  stamp (then docs/PATCH_REANCHOR.md is the runbook, not this pass). (covers LW-387, LW-388,
  LW-391, LW-132 offline halves, LW-414 the probe half) **[BLOCKER]**
- [ ] B8 (PARTIAL 2026-10-17: CI green on 39d25a42, action versions pinned; ModVersion 2.4.0, the v2.4.0 tag and the riders home owed at release, the riders home landed in cb9a6e85) **Version, dependencies, workflow, tag (LW-104, LW-286).**
  `grep -n ModVersion mod/ModConfig.json`; `grep -n "uses:" .github/workflows/release.yml`;
  `gh run list --workflow release.yml --limit 1` then `gh run view <id>`; `git tag --list v2.4.0`
  after the pass closes. PASS: `"ModVersion": "2.4.0"` (it reads 2.3.4 at authoring); checkout@v7,
  setup-python@v7, setup-dotnet@v6, setup-node@v7, upload-artifact@v7 twice,
  softprops/action-gh-release@v2; the latest run green with zero annotations; the v2.4.0 tag on
  the pass close commit; the deploy riders have their docs/VERIFY_LIVE.md home (LW-286). FAIL: a
  stale version, an action at v4 or v5, a deprecation annotation, no tag. (covers LW-104, LW-286;
  the LW-281 banner upload is an owner action, GO/NO-GO item 11) **[BLOCKER]**
- [x] B9 (PASSED 2026-10-17 on 6016d943: filter green, sabotage turned exactly the baseline Fact red naming Plague.cs, restored clean) **No comment claims proof it does not have (LW-107, LW-106, LW-105).**
  `dotnet test LivingWeapon.Tests\LivingWeapon.Tests.csproj -c Release --filter "FullyQualifiedName~ProofClaimContractTests"`,
  plus `grep -rn "PROVEN" LivingWeapon/Signatures/Iai.cs`. Non vacuity once per release: copy
  LivingWeapon/Signatures/Plague.cs to `%TEMP%` first, add one of the six phrases ClaimRegex pins (LivingWeapon.Tests/ProofClaimContractTests.cs line 59; the upper case word PROVEN followed by the word LIVE is the usual one) to one comment
  in the working copy, re run the filter, copy the `%TEMP%` copy back, then `git status --short
  LivingWeapon/Signatures/Plague.cs` must print nothing (never `git checkout` on the uncommitted
  tree). PASS: green on the untouched tree; the grep returns no proof claim; the sabotage turns
  exactly `Proof_claim_counts_match_the_frozen_baseline` red naming Plague.cs. FAIL: the sabotage
  stays green. (covers LW-107, LW-106, LW-105) **[MAJOR]**
- [x] B10 (PASSED 2026-10-17 on 6016d943: comment budget green, both format verifies exit 0) **Comments and formatting hold their frozen shape (LW-185, LW-276, LW-263, LW-264).**
  `dotnet test LivingWeapon.Tests\LivingWeapon.Tests.csproj -c Release --filter "FullyQualifiedName~CommentBudgetContractTests"`,
  `dotnet format LivingWeapon\LivingWeapon.csproj --verify-no-changes`,
  `dotnet format LivingWeapon.Tests\LivingWeapon.Tests.csproj --verify-no-changes`, and
  `grep -rnE "//.*[0-9]+ lines\)" LivingWeapon --include=*.cs`. PASS: the filter green, both format
  gates exit 0, the grep prints nothing (a line count quoted inside a comment is the LW-276 defect).
  FAIL: a frozen file's comment count or longest block grew, a format diff, a grep hit. (covers
  LW-185, LW-276, LW-263, LW-264) **[MAJOR]**
- [x] B11 (PASSED 2026-10-17 on 6016d943: selftest 35/35, regen leaves git clean, 121 lanes) **The bake tools agree with each other (LW-156, LW-159, LW-250, LW-308).**
  `python tools\generate.py`, `python tools\gen_living_weapon_meta.py`,
  `python tools\gen_living_weapon_meta.py --selftest`, then `git status --short LivingWeapon/meta.json
  mod/FFTIVC` and `grep -c '"lane"' LivingWeapon/meta.json`. PASS: every command exit 0, meta.json
  and the generated tables unchanged in git, the selftest's palette lanes (6 and 7 among them)
  pass, the lane count reads 121 (the 121 rows carrying `grows` in data/items.json). FAIL: any
  nonzero exit, a diff, or a lane count that is not 121. (covers LW-156, LW-159, LW-250 offline
  half, LW-308 offline half) **[MAJOR]**
- [x] B12 (PASSED 2026-10-17: scan_logs selftest 60/60, --bogus exit 2) **The scanners refuse false passes (LW-148).** `python tools\scan_logs.py --selftest` and
  `python tools\parse_flight.py --bogus`. PASS: `scan_logs selftest: 60/60 passed.`; parse_flight
  raises `unknown flag: --bogus`. FAIL: a lower count, or a bogus flag accepted. (covers LW-148)
  **[MAJOR]**
- [x] B13 (PASSED 2026-10-17 on 6016d943 after the 402fc94d pass line fix: 454 comparisons, 0 mismatched, 0 missing) **The icon gates: every shipped icon equals its approved picture (LW-247, LW-236, LW-227,
  LW-243, LW-240, LW-287).** On the dev box (game files and the texture tool present):
  `python tools\recolor_icons.py --selftest`; `python tools\icon_preview.py preview` with NO ids,
  then `python tools\icon_preview.py verify`; `python tools\icon_preview.py compare --expect` with
  no ids; `python tools\icon_preview.py anchors`; `python tools\icon_preview.py silhouettes`;
  `grep -n "SHIP_GLOW_RIM = False" tools/recolor_icons.py`. PASS: selftest exit 0 with no
  `SELFTEST FAILURES:` line and no `SELFTEST SKIP (game files absent)` line on this box; verify
  prints `454 comparisons: 0 mismatched, 0 missing` (227 recolored items, two surfaces each; the seven axes and flails back on the game's own art and the seven extended ids 261 to 267, baked by tools/bake_extended_icon_parts.py, sit outside this count); compare prints `OK: nothing moved outside the
  0 item(s) named by --expect`; anchors prints `OK: every reserved name is within 40 degrees of its
  own art`; silhouettes exit 0; the grep hits. FAIL: any mismatched count, a `FAIL:` line, a SKIP
  line on the dev box, SHIP_GLOW_RIM true. (covers LW-247, LW-236 the 454 of 454 gate, LW-227,
  LW-243, LW-240, LW-287 offline half) **[MAJOR]**
- [ ] B14 (PARTIAL 2026-10-17: DeployGuardTests 8/8; the -VerifyOnly read and the flavor refusal run at release) **Deploy parity and the flavor guard's test, at release time (LW-297, LW-134).** With the
  game closed and the prod deploy from box 1.1 in place, in the release sitting with the owner's
  go ahead: `.\BuildLinked.ps1 -VerifyOnly` (deploys nothing, hashes the install against the repo)
  and `dotnet test LivingWeapon.Tests\LivingWeapon.Tests.csproj -c Release --filter "FullyQualifiedName~DeployGuardTests"`
  (runs the real Resolve-DeployedFlavor). PASS: VerifyOnly reports parity with zero differing and
  zero missing files and `installed flavor: prod`; DeployGuardTests green. FAIL: a differing file, a
  red guard test. NOTE: the live refusal itself (a plain BuildLinked over prod) is box 1.1's last
  step, where the owner already has the shell open. (covers LW-297, LW-134 guard test half)
  **[MAJOR]**
- [x] B15 (PASSED 2026-10-17 on 6016d943 after the 402fc94d pass line fix: 30 signatures, 29 grid +3 cells plus Materia Blade) **data/items.json says what the cards and the tour expect (LW-351, LW-354, LW-355, LW-99,
  LW-363, LW-188, LW-10, LW-97, LW-96, LW-325).**
  `python -I -c "import json,sys,re;sys.path.insert(0,'tools');from lib import flavor;d=json.load(open('data/items.json',encoding='utf-8'));it=d['items'] if isinstance(d,dict) and 'items' in d else d;[print(i['id'],i['name'],i['extended'].get('shops'),i['extended'].get('shopAvailability'),i['extended'].get('seedCount')) for i in it if 261<=i['id']<=267];print('signatures',sum(1 for i in it if i.get('signature')));[print(i['id'],i['name'],i['proposed'].get('onHitAbilityId'),i['proposed'].get('evade'),i['proposed'].get('rider'),repr(flavor.assemble_desc(i))) for i in it if i['id'] in (31,92,95,108,149,206,212,222,237,260)];L=lambda i:len(re.sub('<[^>]+>','',flavor.assemble_desc(i)));print(sorted(((L(i),i['id'],i['name']) for i in it),reverse=True)[:6])"`,
  then `grep -n "460" tools\patch_ability_names.py` and `grep -n "RosterSlots" LivingWeapon\Offsets.cs`.
  PASS: ids 261 to 267 are Terrastaff, Ravager, Sunderer, Warbrand, Bloodlash, Climhazzard, Sasori;
  261 to 263 list Lesalia, Riovanes, Eagrose, Lionel, Limberry, Zeltennia and 264 to 267 list
  Dorter, Warjilis, SalGhidos, Riovanes, Eagrose, Lionel, Limberry, Zeltennia, with the milestones
  box 4.6 names and seedCount 0 on all seven; exactly 30 items carry a signature block; 29 of their ids fill the "+3 ability" column of docs/living_weapon_grid.csv and the thirtieth, Materia Blade (id 32), is the tier 0 always on Ultima whose grid row carries P3 "Ultima" and an empty "+3 ability" cell (analyze.py's P3 ABILITY GRID LOCKSTEP row is the mechanical check); id 92 Siren's Lyre onHitAbilityId 243
  and id 95 Glarebound Tome 234; id 31 named Lightbringer with identity opening `The line's only
  Holy sword`; id 108 proposed.evade 22; Sunsteel Helm (149) rider `Jump+1` and card ending `Jump
  +1.`; Empyrean Robe (206) rider `immune Silence` and card ending `Wards against Silence.`; the
  rider `innate Float` only on Featherfoot Boots (212) and Envoûtement (237, with `innate Reflect`),
  Cursed Ring (222) reading `innate Undead and Float` instead, Sunsteel Helm and Empyrean Robe
  naming Float only in a `Was innate Float until` history note; the Scholar's Ring (260) card
  ending `Boosts JP earned.` (its identity field says `Doubles JP earned`, a design note never
  baked); the six longest assembled cards (tags stripped) are Hushblade 204, Holy Lance 203, Rod of
  Faith 201, Arcanum 201, Claymore 201 and Graviton 200 (box 7.6); the 460 grep hits (the Equip Axes cell); Offsets.cs reads
  `RosterSlots = 50`. FAIL: any value off. (covers LW-351 offline half, LW-354 data half, LW-355,
  LW-99, LW-363, LW-188, LW-10, LW-97, LW-96 offline half, LW-325 the list) **[MAJOR]**
- [ ] B16 (PARTIAL 2026-10-17: greps and wording read passed on 6016d943; door pin passed on cdafa581, DoorsOf hits once in production and in the superset test, filter 45/45, the swap sabotage turned exactly that test red and the copy restore re greened; the LOGGING.md hooks quote is a paraphrase, fix before release) **The docs describe the parts model the code ships (LW-401, LW-410, LW-406, LW-169,
  LW-272).** `grep -rn "holds every write\|switches itself off\|until the landmarks match" README.md
  docs/ --exclude=CHANGELOG.md --exclude=SMOKE_TEST_2.4.0.md --exclude-dir=archive` (the changelog
  quotes the retired phrase in its exit row by design); `grep -n "Parts report" README.md docs/COMPATIBILITY.md docs/LOGGING.md
  docs/PATCH_REANCHOR.md`; `grep -n "143\|Item expansion NOT armed" docs/COMPATIBILITY.md`;
  `grep -n "Dual-wielding doubles the growth" README.md`; and a read of the fourteen part names,
  the hedge, the what to do and the footer in docs/LOGGING.md against LivingWeapon/Guard/PartsReport.cs
  and PartsCopy.cs. After LW-416 merges, also `grep -n "DoorsOf" LivingWeapon/Guard/PartsReport.cs
  LivingWeapon.Tests/PartsReportTests.cs` and `dotnet test LivingWeapon.Tests\LivingWeapon.Tests.csproj -c Release --filter "FullyQualifiedName~PartsReportTests"`
  (the pin behind box 3.1's F5: the turn counter that closes the top up window runs only while
  kill counting is ON, so kill counting's door set must contain stat growth's). PASS: zero stale
  phrase hits; at least one Parts report hit per file; the 143 count and the WotL Restoration
  section present with mod 114 absent by ruling; the README dual wield line present; every quoted
  sentence in docs/LOGGING.md matches the code byte for byte; the DoorsOf grep hits once in
  production (the seam) and at least once in a test asserting the superset, the filter green, and
  once per release the non vacuity read: swap the two Part names in that assertion through a
  `%TEMP%` copy round trip and watch it go red, then put the copy back. FAIL: a stale phrase, a doc
  without the bullet, a wording drift, no DoorsOf hit after the merge, the filter red, or a
  sabotage that stays green. (covers LW-401, LW-410, LW-406 offline half, LW-169, LW-272 offline
  half, LW-416 the door pin) **[MAJOR]**
- [x] B17 (PASSED 2026-10-17 on 6016d943: six preserved names, save side files through SaveLocation) **The preservation list and the save folder agree on where every save side file lives
  (LW-348, LW-134).** The deploy preserves only the legacy copies in the deployed folder; the real
  save side files live in the save folder a deploy never touches, so nothing is lost either way.
  `sed -n '78,85p' tools/pipeline.ps1`; `grep -n '"User", "Mods"' LivingWeapon/Persistence/SaveLocation.cs`;
  `grep -rn "extended_inventory.json\|PathFor(FileName)" LivingWeapon --include=*.cs`;
  `grep -n "Path.Combine(modDir" LivingWeapon/Logging/FlightRecorder.cs LivingWeapon/Guard/DrillTrigger.cs`;
  then the old file tolerance reads, `grep -n "MigrateLegacySupp" LivingWeapon/Signatures/GunSlinger.Store.cs LivingWeapon.Tests/GunSlingerTests.cs`
  (both files hit, the call site at the load and the policy pin; a 2.3.x one byte twin support
  key maps back on load, 255 to a blank slot),
  `grep -n "_extendedIds" LivingWeapon/Extended/SaveEdgeHooks.cs`, `grep -n "foreach (var item in items)" LivingWeapon/Persistence/BagReplay.cs`
  and `grep -n "firstStaleId" LivingWeapon/Persistence/TemplateSeat.Repair.cs` (a stale sidecar id
  is read from the armed ids only, placed from the live items only, and dropped past the armed
  range, so a sidecar from another build never places a count on the wrong weapon).
  PASS: `$PreservedSaveFiles` lists exactly `kills.json`, `legends.json`, `gunslinger.json` and
  their `.bak`; SaveLocation.cs resolves `User\Mods\<ModId>` from the Reloaded root; the extended
  bag sidecar and `run_flavor.txt` both resolve through SaveLocation (the sidecar file name and
  `save.PathFor(FileName)` in FlavorStamp.cs), never through `modDir`; only `flight` and the drill
  marker files resolve through `modDir`, matching the Two folders bullet in 0. Read first; the four
  tolerance greps hit and the GunSlingerTests and ExtendedBagSidecarTests ride B1. FAIL: a save
  side file written through `modDir`, a preserved name missing from the list, or a loop over a
  fixed id range in any of the three sidecar sites. (covers LW-348 preservation half, LW-134)
  **[MAJOR]**
- [ ] B18 **Ledger and scope hygiene at ship (LW-320 ruling included).**
  `grep -n "AWAITING-LIVE\|^## Now" docs/TODO.md`; `grep -n "LW-320\|LW-358" docs/TODO.md
  docs/RELEASE_SCOPE.md docs/CHANGELOG.md`; `git tag --list v2.4.0`. PASS: docs/RELEASE_SCOPE.md R5
  boxes ticked; docs/TODO.md Now holds only rows the owner chose to carry (LW-408 by ruling; LW-358
  was demoted to Backlog 2026-10-17 and docs/RELEASE_SCOPE.md line 506 still says it stays, a
  correction the orchestrator owes the ledger); every shipped row of this cycle exited to
  docs/CHANGELOG.md; TodoContractTests green after the exits; the AWAITING-LIVE flips (LW-323,
  LW-198, LW-205, LW-210, LW-212, LW-322, LW-112) made by the owner, never by an agent; the LW-320
  power audit ruling (88 shop weapons against 33 earned ones, the accepted riderless apexes in the
  ruling column of docs/living_weapon_grid.csv) recorded by the owner on the docs/TODO.md row
  before the pass close commit, as a tick or as the rulings he rejects, so that grep shows a dated
  owner verdict. FAIL: a Now row left behind with no ruling, an agent made flip, or an LW-320 row
  with no verdict. (covers LW-320) **[MAJOR]**
- [ ] B19 **README and the ship notes tell a 2.3.x player what the update does to their seven axes
  and flails, and what uninstalling really keeps.** A returning player must be told, before they
  load, that the Terrastaff, Ravager, Sunderer, Warbrand, Bloodlash, Climhazzard and Sasori are
  now separate purchases (which town, which chapter, box 4.6's list), that the ones in their hands
  are the vanilla axes and flails again with no Kills line, and that their kills moved and show on
  the new weapon's card (box 6.2); and the uninstall promise must match what box 4.5 found.
  `grep -n -i "development branch\|battle axe\|saved counts" README.md`; `grep -n -i "saved bag
  count" docs/COMPATIBILITY.md docs/EXTENDED_INVENTORY_GUIDE.md`; a read of README.md's install and
  compatibility sections for the upgrade paragraph; the owner's ship notes draft carries the same
  paragraph. PASS: no "development branch" hedge left in README.md (the move shipped); one
  paragraph names the seven, the Battle Axe in hand, the moved kills and where to buy them; the
  three "comes back with its saved count" sentences read "if you did not save while the mod was
  off" (or box 4.5 leg 2 disproved the code read, and they stay); the ship notes carry the
  paragraph. FAIL: the hedge survives, no upgrade paragraph, an uninstall promise box 4.5 showed
  false. (covers LW-351 the player facing half, LW-401 the docs half) **[MAJOR]**
- [ ] B20 (READ 2026-10-18 from the dev box, a read of AppConfig.json and the registry, owner ticks: Reloaded root is the game folder's Reloaded subfolder, AutoInject False so the launch method is the Reloaded-II Launch button; EnabledMods in load order 1 paxtrick.fft.colorcustomizer, 2 fftivc.jobs.bluemage, 3 fftivc.mechanics.levelscaling, 4 fft.monsters.strongmoster, 5 fftivc.qol.customfastforward, 6 fftivc.rare.treasure.only, 7 prawl.fft.livingweapons; FFTHandsFree absent; none of the thirteen will not work together mods enabled; WotL Restoration not installed, so 10.1 adds it for its run only; Living Weapons loads last; QuickEdit reads 0x0; FFTColorCustomizer enabled, noted on 8.1) **The environment the pass ran under is written down before sitting 1.** Every live box is
  unrepeatable without the mod list and order, FFTHandsFree left enabled crashes the Denuvo free
  build at launch (which would fail box 1.2 for a reason that is not this mod), and one click into
  the console suspends the mod's threads. A script reads `<Reloaded root>\Apps\fft_enhanced.exe\AppConfig.json`
  (its `EnabledMods` list; a read, never a write) and pastes the list and order under this box, or
  the owner screenshots the Reloaded-II mod list; `reg query HKCU\Console /v QuickEdit` (and any
  `HKCU\Console\<title>` subkey for the Reloaded console); the launch method from Read first is
  noted here too. PASS: FFTHandsFree absent or disabled; no mod from docs/COMPATIBILITY.md's will
  not work together table enabled; WotL Restoration, if present, above `prawl.fft.livingweapons`;
  FFTColorCustomizer's state noted beside box 8.1; the list unchanged across sittings except where
  a box says to toggle a mod (2.9, 10.1, 10.3); QuickEdit reads `0x0` (if `0x1`, the owner turns
  it off in the console Properties once before sitting 1 and the read is repeated). FAIL: HandsFree
  enabled, an item rebalance mod enabled, the list missing from the doc, QuickEdit still `0x1` at
  sitting 1. **[MAJOR]**
- [x] B21 (PASSED 2026-10-17: rescued log 794 lines, 0 errors, CLEAN, 20 of 20 tapes parse) **The previous session's rescued log and tapes read back clean.** The 2026-10-17 drill
  sitting's `livingweapon.log`, `livingweapon.prev.log` and 20 flight tapes were rescued before the
  next deploy wiped them, to a folder under `%TEMP%` (a temp folder; it may be gone by the release
  sitting, in which case this box reads the current log the same way). `python tools/scan_logs.py
  "<that folder>\livingweapon.log"` and `python tools/parse_flight.py` on each tape under
  `<that folder>\flight\`. PASS (already read 2026-10-17, offline): the main log 794 lines, 0
  errors, 1 warning, `RESULT: CLEAN`, `last parts report: Living Weapons: all parts armed.`, image
  base `0x7FF65FC60000` with a nonzero delta, stamp `0x6A5E4A1B`, `Extended inventory armed: 7 new
  item(s)`, `Battle ended: 1 kill credited (Chaos Blade 1)`; the prev log shows the LW-397 unknown
  build drill (`not a build this mod knows`) and the roster drill stand down with eleven parts OFF;
  the standdown tapes carry their guard records. If the folder is gone, the box reads NOT REACHED
  and the live evidence rests on the changelog rows named below; do not re run the drills to
  replace it. FAIL: a tape that does not parse, or an ERROR in the clean log. (covers the live
  evidence behind LW-393, LW-397, LW-398, LW-399, LW-400, LW-388) **[MINOR]**

---

## GO / NO-GO (any fail = do not release)

Owner ruling 2026-10-17: every box in both parts passes before the tag is cut, cosmetic included.
The BLOCKER, MAJOR and MINOR tags order the work, nothing more; none of them is permission to ship
with a known miss.

1. B1 full suite 0 failed; B3 analyze.py exit 0 both ways; B6 Publish.ps1 clean with a 2.4.0 zip.
2. B4 and B5: prod thresholds {5,10,15}, no seeding, no dev lever in the DLL; 1.1 the flavor guard
   refuses dev over prod; B14 parity holds.
3. Section 1: header with a nonzero image base delta and the known stamp, every part ON after the
   first battle, no box, scan exit 0 on the final session.
4. Section 2: the seven drills each pause exactly their parts with one box in the shared voice,
   and F6 is dead on prod (2.5).
5. 3.1 the knight sword HP top up reads full on turn one (the LW-416 regression closed).
6. 4.1 and 4.3: the extended inventory survives a cold boot and every list and loop.
7. 8.1 the color cut: release swings in vanilla steel and the parts report has thirteen parts.
8. Section 9: thirty signatures seen once each, Bulwark at 263 among them; defects rowed.
9. 5.1, 5.2, 5.3, 5.5, 5.8: credit to the true killer in manual and auto, no false battle end, no
   lost tally, no double pay on Retry, no stale toast.
10. 6.1 cold launch New Game archives and resets, Continue never trips it; 6.2 a 2.3.x tally's axe
    kills move to the new weapon once; 10.1 the WotL dual run answered.
11. B8 and B18: ModVersion 2.4.0, tag cut, RELEASE_SCOPE and TODO clean; the LW-281 download page
    banner re rendered after the final art state and uploaded (owner).

---

## Open risks to watch (known, accepted, or tracked elsewhere)

- **LW-326 Puppeteer reliability**: quick confirms miss the arm; a dominated monster shows nothing.
  Known; 9.11 records the strike count.
- **LW-386 Poacher's Den wrong items**: a public 2.3.3 report of katanas where dragon carcasses
  belong, not reproduced; 9.5 watches the Den item.
- **LW-366 Bulwark at id 263 never seen**: 9.3 is the real leg, not a re read.
- **LW-28 intermittent preservation loss**: BuildLinked fails red on it; eyeball every deploy.
- **No full bag rig this release**: HandsFree crashes the Denuvo free build and no repo script
  writes a bag; 4.3's "past 149 rows" half may read NOT REACHED and rest on the 2026-08-31 rows.
- **LW-405 paint cache stamp**: the card paint cache is stamped with the expected build, not the
  observed one; harmless while the stamps agree (they do, B7), and an absent or foreign cache is
  thrown away for the cold scan rather than trusted (6.7 reads that).
- **LW-396 Counter and reaction kills uncredited**: by design this release (a miss beats a mis
  credit); 5.1 reads the bury record so the owner can tell the designed refusal from a regression;
  the release notes should say so, since "my Counter kills do not count" is the likely first report.
- **LW-234 a guest's death as a kill**: the enemy oracle is the only team gate on credit; 5.9
  watches a guest die.
- **LW-164 an enemy carrying a tracked weapon id**: 5.1's human battle is the watch.
- **LW-108 and the quit door of LW-233**: 5.10 is the first time a mid battle quit is watched.
- **Untested lane: a never reloaded new game's first extended purchase**: a new game played to
  Lesalia with no reload seats the bought Terrastaff through the game's own rebuild hook alone
  (LivingWeapon/Extended/OrderRebuildHook.Seat.cs), which no save of the owner's can stand in for;
  hours of play, deferred (Appendix A). The fallback is a reload, which every player does
  eventually.
- **Reloaded installed outside the game folder**: IconGlow walks three parents up for modded.pac
  and warns `IconGlow cannot locate modded.pac this launch` when it is not there; 7.2 lists that
  WARN as a FAIL, which is right for the owner's layout and a known restart only degrade for
  players elsewhere.

### This is NOT a failure (read before flipping any box FAIL; each is designed or accepted)

- 3.1: a knight sword whose tier rose BETWEEN battles opens one battle with the max grown and the
  bar un topped, and the next battle tops up; one extra top up after a reset, once.
- 2.3 and 2.10: the two one line latch refusals under the table drill, printed once with no hedge
  and no detail tail.
- 2.7: repeated identical `Parts report:` lines while sitting at the title.
- 2.10 and 2.11: a SECOND box for a LATER closure; the footer reading 2.3.4 before the B8 bump;
  the blind box's `game build unknown` footer.
- 5.1: a Counter or reaction kill on the enemy's turn uncredited (LW-396).
- 5.5: one uncredited re kill across the retry legs (the one in fifteen miss).
- 5.7: the permissive branch most of a battle; `sites=` up to 3072; rare `pruned-dead` evictions.
- 5.10: `battle sensing WAITING` through a battle resumed from a mid battle autosave.
- 6.1 and 6.6: the Orbonne opener's scripted kills uncredited; no archive and no new game line on
  an EMPTY tally.
- 6.5: `No kill tally was found on disk; starting fresh.` and `fresh` on a brand new install; save
  side files that do not exist until their first event.
- 9.4: a plant refused with the back to the map edge, a DBG line only, no toast.
- 9.6: ONE shot in a battle whose off hand was empty when the battle was built.
- 9.7: a move then Wait banks no Mushin charge; two waits do not stack.
- 9.11: the puppet releases after ITS OWN turn, never on the wielder's clock.
- 10.3: no box at all when the hooks helper is missing (an absence never boxes).
- **LW-409 the double cast thing**: a Dual Wield living weapon may swing once, a formula 4 gun may
  cast twice; 9.6 and 3.2 are the places to notice it.
- **LW-165 Steam Deck cold boot card latency**: accepted 2.3.3, README note, untested here.
- **LW-7 auto battle turn count collapse**: counting, not credit; 5.1 auto leg is the watch.
- **LW-412 shade doubt**: withdrawn 2026-10-18 with the feature; 8.1 now reads that release
  swings in steel. LW-447 carries the redesign and LW-339 and LW-305 ride with it in 2.5.0.
- **LW-358 and docs/RELEASE_SCOPE.md disagree**: the TODO demoted LW-358 to Backlog 2026-10-17
  and the scope doc line 506 still says it stays in Now; B18 carries the correction.

---

## Appendix A: deferred by decision (NOT part of this pass)

Pushed to 2.5.0 by the 2026-10-17 ruling (docs/RELEASE_SCOPE.md R5): LW-415 the write scope
buckets; the R1 cosmetics (LW-329 the Grows line glow color, LW-335 the inner ring colors, the
hue true ups); LW-340 the extensive glow growth pass; LW-324 the kills paint speed pass; LW-339
the fuller recolor edge pass (now under LW-447); every research row. The 2.5.0 wardrobe cut
(LW-313, LW-288, LW-337, LW-338, LW-214, LW-241, LW-245, LW-277, LW-244, LW-238, LW-302, LW-318,
LW-321) and the Reliquary Phase 1 live pass (docs/VERIFY_LIVE.md deferred rows 6 to 9, backlog
LW-6) have no rows here. LW-407 the file summaries run alone after the parallel rows merge and
have no live surface.

Found by the 2026-10-17 edge hunt and deferred with a reason: the never reloaded new game's first
extended purchase (hours of play to reach Lesalia without a reload; the fallback is a reload, which
every player does eventually); BuildLinked's failed deploy restore path, a deliberate red deploy
with the DLL held open (owner tooling players never run, and LW-28 already tracks preservation
loss; a staged red deploy on a release sitting risks the install for nothing a player sees); a
zero byte `legends.json` or `gunslinger.json` loading as a silent fresh start (release hidden,
one Backlog line for a WARN like the tally's, no live leg).

## Appendix B: harness references and the ledger flips this pass can buy

- Cheats and drills: docs/DEV_TEST_RECIPES.md (the seven drill recipes, give_move and kill_all,
  the grant ACTIVE check, mod list bisection, glow_bounce). Its "Give 99" recipe needs FFTHandsFree,
  which crashes the Denuvo free build; there is no full bag rig this release.
- Log and tape tooling: `python tools/scan_logs.py --selftest`; `python tools/parse_flight.py
  <file>`; docs/LOGGING.md for the verb glossary, the parts report and the flush triggers.
- Acceptance criteria: docs/PROVOKE_AC.md (9.1), docs/BULWARK_AC.md (9.3),
  docs/EXTENDED_INVENTORY_GUIDE.md (section 4).
- docs/LIVE_LEDGER.md rows still Uncertain that this pass can hand the owner evidence for (owner
  flips only): retry-preserves-credited-identity and battle-retry-rewind-fingerprint (5.5);
  worldmap-menu-open-byte, party-browse-screen-byte and twin-dualfire-construction-bound (9.6);
  next-unit-computable and ct-paid-at-turn-start (9.1); per-turn-normalize-repaints-boosted-baseline
  (3.5); slot0-battle-phase-values (5.2); ct-hold-zero-turn-denial (no shipped surface, LW-115, an
  owner flip on the 2026-07-22 run or leave it); the two OVERTURN CANDIDATE rows
  inventory-default-order-drops-unknown-ids and weapon-palette-assignment-walled (4.2 may
  speak to the first; the second waits for LW-447); pe-key-match-kitlane-mismatch-discriminator (2.9 leg 2 is its experiment);
  mapsave-load-card-sites-verify-dead (5.12 is its named isolation);
  turn-flag-holds-through-status-detour and attack-card-reverts-hover-follower (5.6's status
  screen detour); gear-loss-commits-post-battle (4.4 leg B); the SPLIT row
  empty-tile-hand-read-bounds, whose contradicted cure half is superseded by the PROVEN
  swing-id-fallback-stub that 4.1 reads (retire the cure half, owner only).
- docs/VERIFY_LIVE.md: the LW-286 deploy riders (eviction tape 5.7, cache partition 5.7, the no
  drift check B13) get their home there before the release deploy.

## Appendix C: the denominator, every id since the 2.3.0 tag and where it lives here

Lines 11 to 2463 of docs/CHANGELOG.md hold 158 unique ids (the five cycle sections hold 49 + 1 +
52 + 37 + 19; the 2.3.0 cycle section runs past line 2463 and only its first 19 rows are dated
after the tag). The brief's count of 161 is reached by adding the three rows dated 2026-07-16, the
tag date itself, that start just past line 2463: LW-60, LW-95 and LW-94. All 161 are listed.
"No live surface" means a comment sweep, a test refactor, a doc move, tooling or a retraction
that a player and the running game cannot see; such a row still rides one Part B gate.
Of the 161, 60 rows shipped to players in 2.3.1 to 2.3.4 and are regression only here, and 101 rows have
never shipped (everything on main after the 2026-08-12 cut that v2.3.4 was built from). The regression
only ids: LW-42, LW-60, LW-80, LW-87, LW-88, LW-90, LW-91, LW-94, LW-95, LW-96, LW-97, LW-98, LW-99, LW-102, LW-104, LW-105, LW-106, LW-107, LW-109, LW-110, LW-113, LW-115, LW-118, LW-123, LW-127, LW-129, LW-130, LW-131, LW-132, LW-133, LW-134, LW-135, LW-136, LW-137, LW-138, LW-141, LW-144, LW-145, LW-147, LW-148, LW-149, LW-150, LW-151, LW-152, LW-153, LW-154, LW-155, LW-156, LW-157, LW-158, LW-159, LW-160, LW-161, LW-163, LW-166, LW-167, LW-168, LW-169, LW-171, LW-387.

| Id | Where | Note |
|---|---|---|
| LW-10 | A 10.5 | Treasure Master gone, empty config pane |
| LW-42 | A 5.2 | long cast, delayed kill and long pause are not a battle end |
| LW-60 | B2 | the 2.3.0 pass itself, archived; this document replaces it |
| LW-80 | none | RETRACTED, an upstream report sent by direct contact; no live surface |
| LW-87 | A 5.6 | Attack row survives T detours |
| LW-88 | A 5.6 | Attack row count climbs |
| LW-90 | A 3.5 | restart does not inflate a held stat |
| LW-91 | A 5.6 | no stale weapon name on another unit's menu |
| LW-94 | A 9.8, 9.9 | turn credits flags first (Afterimage, Renewal lanes) |
| LW-95 | A 9.10 | Eagle Eye hastens only its own Dooms |
| LW-96 | A 5.1, B15 | late roster slot earns kills; RosterSlots = 50 |
| LW-97 | A 11.1, B15 | Equip Axes reforged description, WONTFIX note |
| LW-98 | A 5.6 | unarmed row reads Fists |
| LW-99 | A 11.1, A 8.1, B15 | Lightbringer is the only Holy sword, WONTFIX note |
| LW-102 | B2 | TodoContractTests stray row sweep; no live surface |
| LW-104 | B8 | release.yml action majors; no live surface |
| LW-105 | B9 | Iai proof prose demoted; no live surface |
| LW-106 | B9 | proof claim ratchet; no live surface |
| LW-107 | B9 | 61 proof sentences reworded; no live surface |
| LW-109 | A 3.4 | timed stat revert retry |
| LW-110 | A 3.4 | timed stat debug lines |
| LW-113 | none | animation register mechanism, PROVEN ledger row, nothing shipped on it; no live surface |
| LW-115 | none, App B | CT zero hold mechanism, Uncertain ledger row, nothing shipped on it; no live surface |
| LW-118 | A 9.1 | next unit forecast behind Provoke's hide |
| LW-123 | A 9.1 | Provoke |
| LW-127 | A 9.1 | only the marked enemy is pulled |
| LW-129 | A 9.1 | no Invisible icon on hidden units |
| LW-130 | A 9.1, A 9.2 | ally shout leaves no mark; the mid hold reload is 9.2 |
| LW-131 | A 9.1 | release at the enemy's turn end |
| LW-132 | A 1.2, A 10.2, B7 | arms on the current build; superseded by the Denuvo free rebase |
| LW-133 | A 9.1, B4 | Provoke re armed, ProvokeEnabled = true |
| LW-134 | A 1.1, A 6.1, A 6.4, A 6.5, B14, B17 | flavor guard and run_flavor.txt, stamped on a fresh folder too |
| LW-135 | A 9.1 | units really hidden |
| LW-136 | A 9.1 optional | two Defenders refuse and scrub |
| LW-137 | A 5.1 | death edge check measured right; no code change |
| LW-138 | A 9.1 | no early release on the player's own cast |
| LW-141 | A 9.3, A 9.4 | terrain grid obstacle bit, the edges in 9.4 |
| LW-144 | A 9.3 | Bulwark |
| LW-145 | A 9.3, A 9.4, B1 | six guarded write fixes; ground restore half rides Bulwark and its edges |
| LW-147 | B1 | test fixtures; no live surface |
| LW-148 | B12 | scanner self tests |
| LW-149 | B1 | copy paste folds; no live surface |
| LW-150 | B1, B3 | structural seams; no live surface |
| LW-151 | B1 | test fake default; no live surface |
| LW-152 | B1 | kill counter corner pins; no live surface |
| LW-153 | B1, A 9.9 | HealPulse folds; heal twins live in the tour |
| LW-154 | B1, A 5.1 | ActorResolver twin filter refactor |
| LW-155 | B1, B5 | dead logging lane deleted |
| LW-156 | B11 | bake tools agree |
| LW-157 | B1 | test refactor; no live surface |
| LW-158 | B1, A 9.8 | Larceny stateful pin; live steal in the tour |
| LW-159 | B11, A 11.2 | four inert ability lanes deleted |
| LW-160 | B1 | test fixture fold; no live surface |
| LW-161 | A 3.3, B4 | prod thresholds 5, 10, 15 |
| LW-163 | A 5.12 | fast reload loop card |
| LW-166 | B3, A 7.1 | No Poaching notice retired; absence read on the cards |
| LW-167 | A 9.5 | Living Poach |
| LW-168 | A 9.6 | Outrider Pistol twin with no support picked |
| LW-169 | B2, B16 | docs/COMPATIBILITY.md exists and links resolve |
| LW-171 | A 9.6 | Arbalest Crossfire |
| LW-182 | B1 | folder layout rename; no live surface |
| LW-183 | B2 | LIVE_LEDGER reformat; no live surface |
| LW-185 | B10 | comment sweep; no live surface |
| LW-186 | B1 | tick table shape test; no live surface |
| LW-188 | A 11.1, B15 | Float trimmed, Sunsteel Jump +1, Empyrean Wards against Silence |
| LW-189 | A 7.7 | first per surface recolor, superseded by the families |
| LW-190 | A 7.7 | sixteen shields |
| LW-193 | A 9.6 | twin consent rules |
| LW-194 | A 9.6 | player keeps their off hand |
| LW-197 | A 7.7 | Genji Shield steel blue; comment correction |
| LW-199 | A 7.7 | fifteen swords |
| LW-200 | A 7.7 | seven knight swords |
| LW-201 | A 7.7 | nine bows |
| LW-202 | A 7.7 | six crossbows |
| LW-203 | A 7.7 | six guns |
| LW-204 | A 7.4 | eleven katanas, previews only |
| LW-206 | A 7.4 | nine poles, previews only |
| LW-207 | A 7.4 | eight spears, previews only |
| LW-208 | A 7.4 | eight rods, previews only |
| LW-209 | A 7.4 | eight staves, previews only |
| LW-211 | A 7.4 | three harps, previews only |
| LW-213 | A 7.4 | three cloths, previews only |
| LW-215 | A 7.7 | thirteen helmets |
| LW-216 | A 7.7 | twelve hats |
| LW-227 | B13 | byte compare, not alpha only; no live surface |
| LW-230 | A 7.7 | neutral haze on shields and helmets |
| LW-231 | A 7.7 | smooth hats and crossbows, crisp helmets |
| LW-233 | A 5.5, A 5.10 | Retry does not pay twice; the quit door is 5.10 |
| LW-236 | B13, A 7.3, A 7.4 | 58 smoke fuming icons re made by the eight family passes; B13's 454 of 454 is the gate, the knife, ninja blade, book, bag, katana, cloth, staff and pole walks are the live read |
| LW-240 | B13 | shared silhouette gate; no live surface |
| LW-243 | B13 | reserved name anchor gate; no live surface |
| LW-247 | A 1.1, B13 | ramp engine, byte identical deploy |
| LW-249 | A 3.2 | poles grow MA |
| LW-250 | A 3.2, B11 | grows lanes, grid is the authority |
| LW-251 | A 8.1 (off in release), A 2.3 leg B | weapon colors by turn, compiled out of 2.4.0 by LW-446 |
| LW-252 | A 5.4 | lookalike units never swap kills |
| LW-256 | B2 | four ledger citations resolve; no live surface |
| LW-257 | A 5.7 | row and card agree |
| LW-259 | A 5.7 | eviction lines late in a long battle |
| LW-260 | B1 | heartbeat pins; no live surface |
| LW-261 | A 5.7 | no 7 to 10 second freezes |
| LW-262 | A 5.7 | cache partition |
| LW-263 | B10 | stale line counts removed; no live surface |
| LW-264 | B10 | a comment corrected; no live surface |
| LW-265 | B1 | one chunk read per step pin; no live surface |
| LW-266 | B1 | locate complete budget pin; no live surface |
| LW-267 | B1 | PoolLocator guard pins; no live surface |
| LW-269 | B1 | CardSites admission pins; no live surface |
| LW-272 | A 5.1, B16 | dual wield credits both weapons |
| LW-275 | B2 | ghost ledger citation gate; no live surface |
| LW-276 | B10 | no line count quotes in comments; no live surface |
| LW-278 | B2 | WONTFIX, two icon docs deleted; no live surface |
| LW-287 | A 7.2, B13 | halo removed |
| LW-292 | 2.5.0 smoke with LW-447 | the game asks for the palette file fresh every battle; no override is served |
| LW-293 | B3 | sprite field comment corrected; spriteIdOverride count 0; no live surface |
| LW-294 | none | a probe docstring corrected; no live surface |
| LW-295 | A 7.2 | glow rims at three strengths, player only brightening |
| LW-297 | A 1.1, A 6.4, B6, B14 | deploy parity and VerifyOnly |
| LW-308 | A 8.1 (no palette line at all in release), B11 | palette stranger WARN never on a clean install |
| LW-311 | A 7.6 | Kills dashes before the paint |
| LW-316 | A 3.1, A 3.2 | the three growth premises |
| LW-317 | A 3.2 | multi lane growth |
| LW-322 | A 7.1 | the Grows phrase (R5 live pass) |
| LW-327 | A 3.1 | knight sword HP top up (LW-416 regression) |
| LW-328 | A 7.1 | famous names show hidden effects |
| LW-331 | A 7.5 | cast clause cards |
| LW-332 | A 7.1 | Grows line second under Kills |
| LW-333 | A 7.6, B3 | tallest cards fit |
| LW-336 | A 7.2 | the mod keeps its glow icons current |
| LW-346 | A 4.1, A 4.4 | a weapon past id 260 is a real item; the indirect bag roads are 4.4 |
| LW-347 | A 4.1 | shield stays beside an extended weapon |
| LW-348 | A 6.3, A 4.5, B17 | sidecar survives a cold boot; lives in the save folder; keyed per save written |
| LW-351 | A 4.2, A 6.2, B15, B19 | vanilla axes back, designs at 261 to 267; the 2.3.x upgrade move is 6.2 |
| LW-352 | A 7.5, B3 | CLAIMS gate, derived badge |
| LW-353 | A 6.3, A 4.4, A 4.5 | per save bag counts |
| LW-354 | A 4.6, B15 | shop rows for 261 to 267 |
| LW-355 | A 7.5, B15 | Siren's Lyre Confuse, Glarebound Tome Blind |
| LW-356 | A 7.2 | extended weapons rim |
| LW-363 | B3 | Ironreed Pole evade 22, no deferral |
| LW-365 | A 4.1 | empty tile swing draws the blade |
| LW-367 | A 4.2 | menu order table heals once |
| LW-368 | A 4.3, A 6.1, A 6.6 | bag count block relocated, new game potions |
| LW-371 | A 4.3 | 143 kinds do not damage the save |
| LW-372 | A 4.3 | lists draw past 149 |
| LW-375 | B1 | WONTFIX, 26 kind ceiling test; no live surface |
| LW-381 | A 7.2 | tier cross rims on first open |
| LW-382 | A 4.3 | party menu lists all, Sell completes |
| LW-383 | A 7.2 | two weapons at one tier both glow |
| LW-387 | A 1.2, A 10.2, B7 | Denuvo free hotfix |
| LW-388 | A 1.2, A 10.2, A 4.1, B7 | main port to the Denuvo free build |
| LW-391 | B1, B7 | ImageLiteralContractTests; no live surface |
| LW-392 | A 2.7 | blind drill |
| LW-393 | A 2.1, A 1.3 | roster drill, the parts model |
| LW-395 | A 2.6, A 6.1, A 6.6 | sensing drill; the empty tally New Game is 6.6 |
| LW-397 | A 2.8, A 1.2 | build stamp report |
| LW-398 | A 2.2 | band door drill |
| LW-399 | A 2.3 | table door drill |
| LW-400 | A 2.4 | twin weapons drill |
| LW-401 | B16, B19 | docs describe the parts model; no live surface |
| LW-406 | A 2.10, A 2.11, A 2.12, A 1.3 | one voice for every paused message; the wave rule and the plain voice |
| LW-410 | B16 | compatibility survey 143 mods; no live surface |

TODO rows that ship before or during this pass: LW-416 (A 3.1, B16 the door pin), LW-413 (A 2.5, B5), LW-414 (B3,
B7), LW-323 (A 5.8), LW-446 (A 8.1), LW-325 (A 9.1 to 9.10, B15 the list), LW-408 (A 10.1), LW-112
(A 2.9), LW-366 (A 9.3), LW-198, LW-205, LW-210, LW-212 (A 7.3), LW-322 (A 7.1), LW-320 (B18),
LW-286 (B8), LW-281 (GO/NO-GO item 11, owner).
