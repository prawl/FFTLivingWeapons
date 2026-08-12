# TODO

STATUS: CONTRACT (machine-checked by TodoContractTests; format grammar at the bottom of this file)

The work ledger. "Now" holds what is actively being worked for the current release (hard cap 5,
each entry carries Done means + Verify). "Backlog" captures everything else at the cheapest
possible entry cost. Items EXIT this file only through docs/CHANGELOG.md, moved there in the
commit that ships or kills them. The full release ship gate stays in docs/RELEASE_SCOPE.md; Now
is the in-flight subset, not a mirror of that checklist.

Entries are written ELI5-first: the opening sentence is plain language anyone can follow, and
the technical detail lives in the indented lines under it.

## Now (release: 2.3.2)

- **[LW-112] Stop blaming a game update when another mod rewrote the same game data** (opened 2026-07-21) [AWAITING-LIVE]
  - Done means: a player running a custom job mod alongside Living Weapons no longer sees this mod
    switch itself off with a message blaming a game update that never happened. When the game
    program itself checks out fine and only the job-command data landmark mismatches, the mod
    STAYS ON, says the truth (another mod rewrote the same game data, named, with the observed
    bytes), and switches off only the three weapon-granted commands that write that data, so the
    two mods stop overwriting each other. The message does NOT suggest load order, although this
    row once promised that: the LW-77 proven row says load order cannot fix a whole-row writeback
    conflict, so suggesting it would be false hope; the honest options (live without the three
    commands, or remove the conflicting mod) are stated instead. (Tech: built 2026-07-28. Two
    instances of the untouched portable FingerprintGuard core: main guard = PE build key + Ramza
    roster row, full stand-down; kit-lane guard = the jobcommand-table landmark in
    LaunchGuard.KitLane.cs, stepped only after main arms, standing down only the
    Barrage/ShadowBlade/Provoke ticks in Engine. Post-arm an all-zero window counts as a blanked
    row, not a boot window. LIVE_LEDGER Uncertain row dated 2026-07-28 carries the discriminator
    claim; adversarial verify passed both non-vacuity breaks.)
  - Unexplained residue, kept so it is not papered over: the player then merged the other mod's
    table rows into a third mod's folder and reports both now work, which should NOT clear the
    memory bytes; suspect the merged copy is silently inert, meaning their custom jobs are likely
    dead and they have not noticed. Worth one question before advising anyone to copy the
    workaround.
  - Verify: the owner runs the two-leg drill in docs/DEV_TEST_RECIPES.md (LW-112 section): a bait
    leg with no conflicting mod (lane arms, Barrage present, zero boxes), then a leg with a
    throwaway job mod that really rewrites rec 8 (mod stays armed, one truthful WARN and one calm
    box, Barrage absent, kills still count). The drill mod recipe and every failure signature are
    pre-registered there. Owner only, as every AWAITING-LIVE flip is.

- **[LW-137] Measure whether kill credit's death-edge bury reads a turn or a cursor** (opened 2026-07-27) [AWAITING-LIVE]
  - Done means: a measured answer, from real battles, to whether the kill counter's "was an enemy
    acting when this unit died" check can be fooled by where the cursor happens to rest. If the
    two readings never disagree at real death edges, the worry closes; if they disagree, the
    measured rate and direction justify moving the check onto the per-unit turn flags, the same
    walk the Defender's shout already switched to. (Tech: KillTracker.Corpses.cs:232 reads
    TqTeam at the death edge, diverting on 1 or 2; the 2026-07-27 Provoke pass observed that
    field reading PLAYER through a whole enemy turn, cursor-tracking behaviour. The measurement
    instrument is built: tools/probes/killcredit_probe.py, passive and read-only, records BOTH
    readings side by side at every death edge with pre-registered verdict classes, on the
    cursor_resolve_probe base. The 20 banked flight tapes were mined first, 2026-07-28, and
    carry ZERO death edges, all recent tapes being feature-drill sessions, so live play is the
    only evidence source.)
  - Verify: the owner runs killcredit_probe.py alongside any normal battles, ideally including
    the LW-112 drill battle since it is already owed, until a handful of death edges land on
    both sides (player kills and enemy-turn deaths), then reads the census line: zero
    disagreements across a real session closes this; any A-PLAYER-B-AI or A-AI-B-PLAYER line is
    the exposure proven, with the fix direction already named. The ledger row stays untouched
    until then. Owner only, as every AWAITING-LIVE flip is.

- **[LW-165] Kill counts are slow to appear in the status menu after a cold boot on the Steam Deck** (opened 2026-08-12) [AWAITING-LIVE]
  - Done means: the felt delay is a measured number instead of a feeling, and a tune or accept
    decision is made from that number. The mod now prints one plain line the first time the kill
    counters come alive each launch, saying how many card text spots it maintains and how many
    seconds after arming that happened, so the owner's next Deck cold boot turns the complaint
    into a stopwatch reading. Already measured from the 2026-08-11 Deck log: the whole heap
    sweep never ran (pool coverage carries everything) and arming tracked the save load
    closely, so the unmeasured gap between arming and the first paint is the only suspect left.
    (Tech: the Info line fires on the false to true edge of the pool coverage latch in
    Display.PoolPaint.cs, once per launch, timed from Display's own first Tick, which Engine
    only starts after the guard arms; later re coverages after an Invalidate log at Debug to
    the file only. If the measured gap comes back under a second, the felt lag was the arming
    follows save load seconds plus menu time, and the only deeper lever is read only pre
    locating before arming, which touches the born disarmed principle and would need its own
    arc.)
  - Verify: the suite is green including the born red first coverage line test and the once per
    launch pin, and the owner's next Steam Deck cold boot log shows the new line with its
    seconds reading, which becomes the tune or accept decision. Owner only, as every live flip
    is.

- **[LW-174] Five story-battle monster jobs are invisible to Living Poach because the map skips their alias rows** (opened 2026-08-12) [AWAITING-LIVE]
  - Built and adversarially checked 2026-08-12, same session it was opened: six new pinned
    tests written red first, the extractor now emits the five alias entries (each tagged with
    its base job), the regenerated map is byte identical on a re run, and the independent
    verify broke the implementation three ways to prove the new guards really trip (suite
    3086 green, analyze exit 0, verdict SHIP at 9/10). Only the live premise beat remains.
  - Premise rescoped 2026-08-12 after a full encounter-table sweep: alias jobs appear in
    EXACTLY three battles in the whole game (384 Siedge Weald, the TIC name for PSX Sweegy
    Woods, fielding six alias units; 389 one panther; 400 one chocobo), all story battles,
    so the player impact is those battles plus NG+ replays, and a late save cannot reach any
    of them naturally. The owner's live pass instead confirmed base-job poaches end to end
    (goblin, skeleton x2, black goblin, red panther all claimed, despawned, and Den counted,
    with the Den UI cross-checked exact against the store bytes). The live settle for the
    alias byte is a staged encounter: inject one MainJob 169 panther into a reachable random
    battle through the moddable encounter table (the arena lane the sibling repo showed
    working live), restart, poach it; a toast plus key 19 rising settles it, silence means
    the engine normalizes aliases and this row gets a retraction note instead.
  - Done means: a monster the game fields on one of the Job sheet's five alias rows (jobs
    169-173, which per the sheet are exact clones of jobs 103/97/98/100/94 with the same
    species and the same carcass keys) poaches exactly like its base-job twin: the map
    resolves it, the Den store gets the right carcass, the toast fires, and nothing else
    changes. The extractor emits alias entries pointing at the same carcass pairs instead of
    folding them into a skip list, and two job ids sharing one carcass pair is explicitly
    allowed, since the store write is keyed by carcass key alone. (Tech: the confirmed major
    finding from the ac43327 adversarial verify round. PoachMap.TryGetJob membership is the
    mod's entire monster gate at LivingPoach.cs:108, so an unmapped alias job is a silent
    refusal, the same failure shape as the Black Chocobo job 95 bug that round's parent
    commit fixed. Offline evidence: the vanilla ENTD table, decoded, fields MainJob 169-172
    for all six monsters of battle 384, the Sweegy Woods chapter 1 composition, plus aliases
    in battles 389 and 400, so ordinary story play hits the gap.)
  - Verify: the suite is green with pinned tests proving jobs 169-173 resolve to the same
    carcass keys as their base jobs and that the real committed poach.json carries the alias
    entries; the existing global key-uniqueness test is deliberately relaxed to base rows
    only, with alias rows asserted equal to their base instead. Live premise watch, owner
    only: one alias-job monster poached in a story battle, since the band job byte reading
    169-173 live is the one unverified link (band equals sheet key was read live at 95 in
    the Black Chocobo falsifying case, ledger row still Uncertain; MainJob is sheet-key
    space).

## Backlog

- [LW-179] 2026-08-12: The battle menu's Attack row should wear the weapon's name from the
  very first menu of a battle; today the first open (and the odd mid-battle blip) shows the
  plain Attack text because the mod refuses to paint when it cannot prove whose menu is
  open, the fail-closed rule that cured the 2.3.0 wrong-name bug. Owner ideal stated
  2026-08-12: never see the plain text. Fix shape agreed in session: ride the LW-139
  turn-order helper (the proven next-actor clock) as a fallback owner at battle open, with
  fail-closed kept as the outer gate (prediction and flags disagreeing composes vanilla,
  same as today), and note the BridgeFail lane (owner resolved but the roster match failed)
  is untouched by that helper and needs its own look before "never" is honest. The owner
  signalled this may be picked up very soon; the three sightings and their log lines are in
  the LW-171 session log (two NoOwner at battle load, one BridgeFail mid-battle). Make the living poach look and sound closer to the game's own poach:
  the owner watched one live and called it eerily close but not identical, the removal
  timing is slightly off and the game's poach sound never plays. Step one when picked up is
  a read-only tape of a VANILLA poach with the existing text-hook head sampler running, to
  inventory exactly what the engine fires (banner call, timing, whatever sits next to the
  sound trigger); that ten minute tape decides whether the mimicry is a weekend or a wall.
  Sound triggering from in-process has no proven lane today, and calling engine UI routines
  cold has walled before (the numeral popup), so this is a real RE arc, not a knob. Open
  design question for the owner first: perfect mimicry may be undesirable, since a slightly
  distinct presentation is how a player and a debugger tell a mod poach from a vanilla
  poach at a glance.

- [LW-175] 2026-08-12: If a poached corpse's removal stays blocked for about 30 seconds the
  mod gives up, and the corpse can then still crystallize on top of the already-banked
  carcass, the exact double payout the fix batch set out to remove, just delayed; the code
  comments claim this cannot happen, and the give-up clock even burns while the game is
  paused. Confirmed (minor, four independent confirmations) by the ac43327 adversarial
  verify round: at PendingTickCap (~900 ticks) LivingPoach.Despawn.cs drops the pending
  entry, the crystal pin stops at the vanilla start value 3, and the engine's countdown
  simply resumes; the class doc at lines 29-31 ("never the double-payout") and the give-up
  Warn both oversell the guarantee; and the cap counts raw 33ms Engine ticks that keep
  running through pause, menus, and mid-battle dialogue while every Transient blocker is
  frozen game state, so a longer-than-30s pause defeats the watchdog outright
  (KillTracker.Corpses gates its own pending age on onField polls for exactly this reason).
  Fix shape is an owner call: gate the cap on progress-capable frames, or keep pinning with
  retries stopped until a Permanent read or battle end, or accept the bounded residual and
  make the doc and the Warn tell the truth. Rides with it, from the same round's notes: the
  new refusal log claims crystal conversion is caught by the Dead-bit check while the doc it
  replaced said crystal also sets composed bit 0x01, both cannot be right, and no ledger row
  distinguishes them; the owed live pass should watch one corpse crystallize while pending
  to settle what +0x46 and the Dead bit actually do.

- [LW-173] 2026-08-12: The game has a native message box on the WORLD MAP (clean modal, story
  text styling, F/Enter Close), and owning it would give the mod an in game voice outside of
  battle, replacing the raw OS message box the fingerprint guard uses today for stand down
  and conflict notices, and opening a home for things like the non English counter warning.
  Owner discovered a reproducible trigger 2026-08-12: traverse to a story node, start the
  battle, FLEE back to the world map (the unit now stands ON the story node), then select the
  next, not yet accessible story node; the game answers with the modal "You cannot travel to
  the selected location at this time..." (screenshot banked in the owner's Monosnap). RE
  leads when picked up: the dialog text almost certainly flows through the FnSetTextString
  family the PromptSwap hook already taps (watch the hook's head sampler while triggering
  it); the RTTI scan of the live image names candidate window classes (FFTOItemConfirmWindow
  and siblings); a show routine plus text holder pair would make this a callable surface, the
  same shape as the battle callout bubble arc.

- [LW-172] 2026-08-12: The mod's human versus monster job boundary is off by two, and every
  consumer of it needs an audit: monsters start at job 94 (Chocobo; Black Chocobo 95), not 96.
  Falsified live during the LW-167 pass (a Black Chocobo victim's job byte read 95 at the
  credit edge) and confirmed by the game's own Job sheet (Key 94 Chocobo, 95 Black Chocobo,
  96 Red Chocobo; the sheet also carries each monster's poach keys). Known consumers of the
  wrong floor: Puppeteer.Policy.cs (GenericJobHi 95, MonsterJobLo 96), VictimClass.cs
  (IsHumanJob 74 to 95; job 94 labeled "Dark Knight", which per the sheet is the Chocobo, so
  the July "Dark Knight 94 live" reading and any Reliquary victim classing built on it are
  suspect), and any band walk leaning on the 96 floor. Living Poach itself no longer cares
  (its gate is Job sheet map membership since the fix). Also unresolved and captured here so
  it is not smoothed over: a 2026-06-08 live ROSTER read recorded job 96 as "Chocobo", which
  either means that unit was actually a Red Chocobo or the roster +0x02 space differs from
  the band job byte at the boundary; one deliberate re-read settles it.

- [LW-170] 2026-08-12: Units wearing the rebalance's Float granting gear render in a strange
  half floating state: standing flush on solid ground as if not floating at all, hovering
  slightly only over water, and the float look overrides the critical kneel (the owner saw a
  critical Agrias over a water tile in an upright standing pose players never normally see,
  hovering above the water).
  Owner observed live 2026-08-12 on enemies and on Agrias. The items are the mod's own innate
  Float pieces: Sunsteel Helm (id 149, vanilla Golden Helm) and Empyrean Robe (id 206, vanilla
  Luminous Robe, EquipBonus row 46); enemies pick them up through level lists, so the state is
  common in late fights. Cosmetic only so far; nobody has verified whether the gameplay half
  of the innate Float (Earth/Quake immunity, trap immunity, terrain walk) actually works on
  these pieces, and that check should ride the diagnosis, since a broken gameplay half would
  reclassify this from cosmetic oddity to broken rider. Diagnosis leads when picked up:
  compare against a vanilla native Float source (status layer Float via the proven band bytes,
  or any vanilla float gear if one exists) to learn whether this is the game's own rendering
  of equipment granted float or something about the EquipBonus lane; the render side hover is
  pure node data (node Z = negative 12 times height, one extra height unit with FLOAT, per the
  teleport/float ledger row), so a probe can read whether the game ever re stamps Z for these
  units on land. Prior that may bite: a redefined EquipBonus row rewrites every user of that
  row (see the row override audit memory).

- [LW-178] 2026-08-12: Settle whether the mod loader merges table XML rows per PROPERTY or
  per ROW, because compatibility verdicts hinge on it when two mods edit different fields of
  the same row. The loader's own template comment claims per property tracking ("only
  properties actually included will be tracked as edited"), while the LW-77 Proven ledger
  row showed load order cannot fix the item table conflicts we tested; both can be true if
  the tested conflicts shipped the same properties, so the grain question is genuinely open.
  Concrete test case sitting ready: Ramza Overhaul (Nexus 110) and this mod both ship
  JobData row 4 with DISJOINT properties (their EquippableItems grant vs our
  CharacterEvasion 8); install both, restart, and read Ramza's chapter job in game: both
  effects landing proves per property, one missing proves per row. The answer sharpens the
  compatibility grid's caveat wording and could soften the fatal six's phrasing.

- [LW-177] 2026-08-12: Teach the mod to detect the conflict classes the compatibility
  survey exposed and say the truth in game, the guard extension half of what LW-169 carried
  before the grid narrowed it. Candidates from the survey session: an item lane guard (we
  know every row we ship; detect foreign item rows at arm time and name the loss instead of
  silently fighting, covering the only FATAL class), a text anchor census message when
  another mod's cells cover ours (pool coverage already counts sites), and a grant time
  record verify for command rewrites beyond recs 8 and 9 (Knight Overhaul rewrites rec 7,
  exactly where ShadowBlade injects for a Knight wielder). Same guard family and born
  disarmed discipline as LW-112's kit lane. Survey provenance: all 97 published mods, owner
  session 2026-08-12: 6 fatal item table mods (ids 115, 107, 84, 75, 72, 64), roughly 25
  with potential conflicts (job row overlaps, command record rewrites, text cell overlaps,
  non English counters per LW-101), 66 clean; GenericJobs (id 34) source audited clean; the
  per mod verdicts for the middle bucket live only as classes, the durable record is
  docs/COMPATIBILITY.md.

- [LW-164] 2026-08-12: An enemy carrying the same weapon type as a player's living weapon can be
  briefly mistaken for a player unit, which could someday hand an enemy's kill to the player's
  weapon.
  Seen once on the 2026-08-12 player tape (flight_20260812_023631): a band enemy wielding weapon
  id 19 was bridged Player with rescue=WeaponUnique even though FOUR roster rows also carry id
  19 and the enemy's level/brave/faith fingerprint matched no roster row; no kill landed on that
  hit, so nothing mis-credited, but the WeaponUnique rescue demonstrably fired on a non-unique
  id. Check ActorResolver's WeaponUnique lane: it should refuse when the weapon id is not unique
  across the roster, and probably verify the fingerprint against the roster row it names. The
  same tape shows vanilla chapter 1 human enemies routinely carrying tracked ids 19 and 20, so
  the collision population is normal play, not an exotic setup.

- [LW-162] 2026-08-12: The build script's warning about leftover dev kill tallies points at the
  wrong folder, so anyone following it looks where the tally no longer lives.
  Found during the LW-161 live pass: BuildLinked's DEV-flavored-install warning tells the owner
  to delete kills.json from the deploy folder (Reloaded\Mods\prawl.fft.livingweapons), but since
  the LW-134 save-dir split the tally lives in the User save folder
  (Reloaded\User\Mods\prawl.fft.livingweapons), and the deploy-folder path misdirected this
  session's live-pass staging on the first try. Re-point the warning text at the real save dir
  and check whether the preserve/restore list still names the old location anywhere else in
  tools/pipeline.ps1.

- [LW-146] 2026-07-28: A batch of comments and docs that lied about the code they sit on is fixed;
  one item is left, and it needs the owner's own sign-off.
  Fixed this commit: docs/LOGGING.md's claim that Puppeteer is not flight-tapped (it is, and the
  "pup" record shapes are now documented), StatusApply.cs's dead v1 apply-engine address,
  NumeralSpike.cs/BodyDoubleSpike.cs/Engine.cs's stale F6-spike comments (LW-67 deleted those
  spikes; the current map is F2/F4 StatusSpike, F5/Shift+F5 BodyDoubleSpike, F6 ProvokeSpike, F8
  NumeralSpike), the TurnOwnerSpike/TurnOwnerProbe/TurnOwnerProbeTests headers (LW-31 shipped
  2026-07-07, docs/CHANGELOG.md:932; the recorder stays wired only as a passive correlation
  instrument for LW-139), the dead Log.cs shim (deleted, its test allowlist updated), and
  Puppeteer.Policy.cs's dangling reference to a class TODO that no longer exists. tools/probes/
  tile_cal.py's stale terrain grid base was already fixed earlier the same day, commit df8b779, so
  there was nothing left to do there.
  Still open, owner territory: docs/LIVE_LEDGER.md line 138's "TILE SYSTEM SOLVED" paragraph still
  names the wrong terrain grid base two lines under its own correction banner and wants a
  supersession stamp; that flip is owner sign-off only, same as every LIVE_LEDGER row.

- [LW-100] 2026-07-21: A rider who restarts a battle and opens it on foot can keep the previous
  run's leftover mounted Speed until they climb back on a chocobo.
  Demoted from Now on 2026-07-27 to make room for LW-127, which is being actively worked. Nothing
  about it changed: it has been BLOCKED since 2026-07-21 on a live pass nobody can currently run,
  and parking it in Now while five other things move is how a ledger starts lying about what is
  being worked.
  State: the 2026-07-21 live pass came back INCONCLUSIVE rather than clean. The reload took 3.469
  seconds and the mod only counts a battle as ended after 4.0 seconds out of battle, so it never saw
  an end or a start, never cleared its notes, and wrote the natural it still remembered, which is
  what it would produce in BOTH worlds. That seat cannot tell them apart. The premise itself is no
  longer unverified: the same session caught the mod's own boosted value surviving a battle rebuild
  on the PA lane (read 27 against natural 21, exactly the 1.30 hold target) and an earlier log caught
  it on the Speed byte itself (18 against natural 11).
  RE TEST RECIPE, in order: (1) DONE 2026-07-23, commit 3dccbf7, the mount lane now logs its
  capture, boost, re-apply and revert, so a line appearing means a write really happened and the
  absence of one is finally evidence; this step used to be the blocker and no longer is. (2) Make
  the restart cross the 4 second debounce, or lower ExitDebounceSeconds in a dev build, and CONFIRM
  a real battle-end plus battle-start pair in livingweapon.log before trusting the read. (3) Then
  read Speed at the restarted open while on foot, BEFORE remounting. Only then the unit tests: a
  recorded leftover target is refused as a natural even with no active hold, plus the remount
  sentinel. (Tech: the code hole is confirmed, GrowthEngine.TimedStat.cs gates the only
  FilterCapture call on active first, so a dismounted open misses all three arms; the 2026-07-21
  pass never entered it. Rides with it: a clean remount capture currently drops the post revert
  corrective sentinel for the rest of the battle.)

- [LW-143] 2026-07-27: While holding the fast-forward button, the owner may have seen the Defender's
  shout keep going past the one enemy turn it is supposed to last; parked at the owner's request
  ("keep that in your back pocket, don't act on it yet"), a single possible sighting, not a
  confirmed bug.
  Why it is plausible rather than dismissed: the mod samples the battle every 33 milliseconds of
  real time, but fast-forward speeds the GAME clock, not ours. The release needs to SEE the marked
  enemy become the acting unit and then stop being it (a rise then a fall, LW-138's fresh-rise
  rule). Under fast-forward a short enemy turn could open and close entirely between two of our
  samples, so the rise is never observed, the turn is never counted, and the hold lingers until the
  90 second watchdog force-releases it with a WARN. That WARN line, or an EnemyTurnDone arriving
  only after a SECOND turn of the marked enemy, is exactly what the tape would show if this is
  real; the flight tapes survive deploys, so the evidence keeps.
  If confirmed, the likely fix direction already exists in this session's findings: detect the
  marked enemy's turn by its CT PAYMENT (a drop of about 100, readable long after the fact) instead
  of, or as well as, the sub-sample actor-pointer dwell. The payment persists between samples; the
  pointer does not.
  Probable corroborating tape, banked same day without acting on it:
  flight_20260727_101904_battle-exit.jsonl cast 3 (mark nameId 795, second cast on that enemy)
  held 43 seconds between its why=next hide (-110.312s) and its EnemyTurnDone release (-67.703s),
  where the battle's other holds released within about 14 seconds. Matches the predicted
  "EnemyTurnDone only after a later turn" signature; the release reason was NOT Watchdog, so the
  fall was eventually observed rather than timed out.

- [LW-139] 2026-07-27: The mod can now know who acts NEXT, and several features that currently
  guess at turn state from present tense signals could be rebuilt on it.
  Why it matters: every turn related mechanic in the runtime today asks "who is acting right now"
  and answers with either the engine's actor pointer (which parks on units that were merely struck,
  the LW-138 bug) or the per unit menu open flag (which is blank during action resolution, the hole
  LW-138 fell through). Neither can answer "who is up after this one", so anything wanting to act
  BEFORE a turn opens has been impossible. The CT and Speed clock model proved live on 2026-07-27
  reproduces the game's own Combat Timeline, so that answer is now available for the cost of a band
  walk, with no new write surface and no new address.
  Candidate consumers, each its own card when picked up: Provoke's hide (LW-118, the reason this was
  proven at all), Iai and Kobu and Mushin's turn detection, the Attack card's turn owner resolve
  (LW-87 anchors it on the menu open flag), and ExtraTurn, which already WRITES CT and could read
  the same field it slams.
  Not free, and the limit is sharper than the headline: only the NEXT unit to act is trustworthy.
  That part scored 15 of 15 across two sessions and a game restart. The DEEP forecast is not
  trustworthy and the two runs disagree about how far it holds, one staying exact for six turns and
  the other going wrong at three, so any consumer wanting more than one turn of lookahead has to
  earn it with its own measurement rather than inheriting this row. Shared code would want one turn
  order helper rather than five copies. (Tech: band +0x25 CT and
  +0x24 Speed; ledger rows dated 2026-07-27; harness tools/probes/provoke_lookahead_probe.py.)


- [LW-128] 2026-07-22: Provoke pops an empty speech bubble over the caster (Ramza's portrait, no
  words) on cast; fill it with a taunt, since Provoke is literally a taunt.
  Why it matters: that bubble is the game's own callout for the ability, Embrace's vanilla quote
  slot gone blank after we renamed and repointed the ability, and it fires exactly on cast, so it is
  free thematic flavour going to waste. Owner asked for a jeer in there 2026-07-22.
  State, CORRECTED 2026-07-23: this ticket previously said the mechanism rests on a Proven ledger
  row. IT DOES NOT, and the correction matters more than the feature, because acting on the old
  wording would have meant building on a premise nobody has signed off. Both callout rows in
  docs/LIVE_LEDGER.md (the show flag hijack, and the injected call with piggyback timing) sit in the
  UNCERTAIN section, dated 2026-07-02. They are strong: the owner eyewitnessed custom text rendering
  on screen twice, and roughly ten in process call runs went by without a crash. They are still not
  a Proven row, and only the owner moves one.
  What that costs: this is a full build arc with its own live premise proof, not a small change
  riding an established mechanism. The evidence is also OLDER THAN THE 1.5.1 RE-ANCHOR, and the
  addresses involved (a 0x436B07D058 holder, an in process call to 0x14028F720) were never
  re-verified after it, so step one is simply reading whether that holder still validates on this
  build. The spike that proved it (ShowSpike) was DELETED from the tree by LW-67, so the code has to
  be written again rather than revived, and commit b6a1ffe is where the original lives.
  Candidate lines drafted (Eyes on me curs / Come break upon my shield / Your mother swung truer);
  consider rotating a few at random. Polish; rides after the core arc and the usable by AI fix.


- [LW-126] 2026-07-22: When an enemy you shouted at with Provoke gets mind-controlled onto your
  side by Galewind's Puppeteer, the shout does not notice the takeover and hangs on until a
  failsafe fires; it should recognise the takeover and let go right away.
  Why it matters: a Puppeteered enemy is now driven by the player, so it never takes the AI turn
  Provoke is waiting on. RE-READ 2026-07-27, because this row's reasoning was written against a
  shipped behaviour that no longer exists and its conclusion flipped: it used to argue the gap was
  harmless under the then-hypothetical single enemy mode and would only become REQUIRED if the
  whole phase fallback shipped. LW-127 shipped the single enemy rule, so the harmless case is the
  live one: a dominated enemy is not the acting AI unit, so the ranking stops naming it as next and
  the party is revealed rather than held hidden. What remains is a stranded ARMED hold that sits
  there until the ninety second watchdog clears it, which logs a WARN meaning "a real bug" and so
  produces a false bug report rather than a broken battle. A direct release is still the right fix.
  State: LW-123's disabling-status release catches engine Charm (status id 34), but Puppeteer
  dominates through the agency bits (combat +0x05 and its shadow +0x1EE), not the Charm status, so
  a domination slips past. The agency read mask is a LIVE_LEDGER Uncertain row (inferred from
  scouting Dicene's fftivc.unitcontrol, never live-proven as a readable signal), so this is gated
  on proving that mask first; once proven, add the agency bit to Provoke's disabling check (Tuning
  holds the id/mask set). The same case is a watch item on the LW-123 live pass.

- [LW-125] 2026-07-22: Three weapons now grant a command to their wielder, and all three do it with
  the same hundred and thirty lines copied out three times. The next one makes it four.
  Why it matters: the duplicated part is not the interesting part. It is the roster walk that finds
  the wielder, the release path, and the learned bit hold, and every copy is a place a fix has to
  be applied again and can be forgotten. ShadowBlade.cs has carried a FOLLOW UP SEAM comment since
  it shipped saying the shared core should be extracted once it is live verified, and Provoke has
  now made it a third copy rather than a second.
  Deliberately deferred, not overlooked: extracting the core means editing two shipped modules that
  players are running, and doing that inside a commit that also adds a feature is the exact diff a
  reviewer should distrust. It is its own stage. The pure decisions were already shared rather than
  copied (ProvokePolicy delegates to ShadowBladePolicy for record resolution), so what is left is
  the stateful half only.
  Watch for when it happens: Barrage.Policy.InjectSlot and ReleaseSlot call
  ShadowBladePolicy.NeedsInject, so the dependency between those two files already points the wrong
  way, and the extraction is the natural moment to straighten it. Do not fold the table write half
  of Provoke into any shared core: it is keyed on a different arming condition on purpose, and
  fusing the two lifecycles is the bug LW-123's plan review was written to prevent.

- [LW-124] 2026-07-22: Audit every band walk in the runtime for staged cutscene units. The engine
  parks them in real seats with sane stats and real map positions, so a walk that only checks for
  sane values counts them as party members.
  Why it matters: five of them sailed through a position-based filter on 2026-07-22 and would have
  been treated as live party members. Band.IsValid does not test the engine's own hide gate, so
  every existing caller is exposed, not just the new one.
  State: LW-123 adds a Band.IsOnField predicate (IsValid plus combat +0x01 not equal to 0xFF) and
  uses it in the new code only. Folding it into IsValid touches about ten call sites and each one
  needs a think about whether excluding an off-field seat changes its answer, so it is deliberately
  a separate pass.

- [LW-122] 2026-07-22: Make the game apply a status for us. The door is found, resolvable and
  safe to knock on; we are not yet speaking its dialect.
  Why it matters: three independent systems said the same thing this session (the mover ignores
  written state, the animation register wanted an input not an output, a raw status bit sets a
  flag while the engine does the work), so "ask the engine" is the general key, and this is the
  first tool that turns it. It also unlocks two walls directly, the enemy model rebuild and the
  Guest/Traitor allegiance flip, both of which were declared dead for reasons that only apply to
  writing data ourselves.
  State: the pinned v1 apply engine is dead on this build. The fixed image thunk at 0x1401FB064
  resolves the live routine every launch and its prologue is verified before every call, which is
  a permanent fix rather than a re-pin. Eleven cold calls landed safely and applied nothing,
  covering all three modes against all three candidate subjects at the decoded argument order.
  Next, cheapest first: sweep the four remaining argument permutations (one loop, no deploy, the
  knob already ships); read the global flag the routine tests early, since a wrong global sends it
  down a different path before it touches anything; disassemble past the bail branches; and
  reconsider the premise, because the claim that this dispatch means id, mode and slot came from
  a v1 header note that was never verified and the function may not be what we think it is.
  Instruments in tree: tools/probes/apply_engine_find.py (peek, spring, dump, scan) and
  battle_toolbag.py engine with its order and subj knobs.
- [LW-121] 2026-07-22: A weapon that plants its wielder somewhere nothing can reach: proven to
  work, and degenerate unless it costs something.
  Found by deliberately breaking a guard (battle_toolbag.py warp onto a treetop). The engine
  accepts placement on terrain it would never path a unit onto: correct perched render, real
  height readout, turn marker, health bar, valid selection diamond. The unit is then STRANDED,
  since the pathfinder refuses to route out of a tile it would not route into ("At present, can't
  move to any other tiles"), but it acts normally and its ranged attacks connect, while the enemy
  AI degrades gracefully, backing off and milling about without engaging or crashing. So the
  mechanic writes itself: trade all mobility for an unassailable firing position, which is a
  genuine tactical bargain rather than a cheat, PROVIDED it is bounded. Unbounded it is a
  degenerate strategy, since melee simply cannot answer it. Design levers to price it: a duration
  after which the wielder is returned to the ground, a one per battle limit, an accuracy or damage
  penalty while perched, or making the descent the wielder's whole next turn. Height matters
  mechanically in this game (damage, accuracy, range), so the perch is worth more than the
  novelty suggests. Open before any build: whether melee genuinely cannot reach (the AI's retreat
  implies it but was not directly tested), and whether an ability that moves a target could shove
  the wielder off.
- [LW-120] 2026-07-22: Play an animation at a dramatic moment, so a weapon coming alive looks
  like something instead of only printing text.
  Now legal: the animation register is Proven (owner flip 2026-07-21). Candidate first moment is
  a tier up, which the mod already detects (BannerToast) and which the owner's catalog already
  has a page for (0x1c, the level up leap). Work needed: the render node walk currently lives
  ONLY in BodyDoubleSpike.cs behind #if LWDEV, so production has never touched a node; moving it
  into shipped code through the guarded Mem layer and the IGameMemory seam is the actual task,
  after which the write is one guarded u16 (page + 1 into node +0x10). Theater only, so the risk
  is a wrong pose for a few seconds and the engine re-stamps at the unit's next event. THE REAL
  GATE: page ids are per sprite class and only one class is swept, so either finish LW-114 first
  and use pages that agree across classes, or fire only for mapped classes and skip the rest
  (fail closed, house style). Rides /build-lite plus an owner live pass.
- [LW-119] 2026-07-22: The status map is extracted and the hazards are known, but the probe
  verb that would use it is not written yet, and two thirds of the map has never been exercised
  in this game.
  tools/probes/status_map.py holds all 40 ids with their band offsets, the three layer model
  (write inflicted AND composed, re-assert on a loop; composed alone is the same wasted-write
  mistake the animation output block taught us), the two proven timers (poison band +0x4A init
  36, doom band +0x59 init 3), and an evidence tier per status. Only 13 bits are anchored by
  shipped code or a Proven row; the other 27 are map-only, meaning the ported decode table plus
  id arithmetic that checks out, which is good evidence for the TABLE and no evidence for any
  individual bit. Two ids are refused outright because the repo has crash tapes for them:
  crystal is permanent unit loss and treasure crashed the game when an enemy pathed onto the
  tile. Six more need an explicit yes. Three open disagreements are recorded in the file rather
  than smoothed over: charm's companion byte (band +0x54 versus +0x38 versus a third source
  calling +0x38 a node pool index, so write the status bit only), composed-rebuilt-every-frame
  versus the proven composed-only poison hold, and Larceny's one-shot strip which has no ledger
  row and should be undone by the next compose. Next step is the verb plus a live pass that
  promotes map-only bits to observed, cheapest first: haste, regen, protect, shell.
- [LW-117] 2026-07-22: The battle toolbag: one plain verb per already-proven mechanic, so a
  design conversation can say "what if the weapon benched them a turn" and we can just do it.
  tools/probes/battle_toolbag.py wraps Proven-section mechanisms only, no new reverse
  engineering: quick (CT slam, act now), bench (CT held at zero, turn denial), hide and show
  (the gate byte, with the model id saved to a temp state file because each probe run is its
  own process), float (render Z hover), reserve and deploy (park below the floor, return with
  the sky descent), and state (every field the bag touches, for every unit). Constants were
  re-read out of the tree rather than recalled. Hazards are printed by the commands that carry
  them: a hidden unit gets no turns and cannot un-hide itself, a mid-hide autosave persists the
  hidden state into the resume, and hide and bench both refuse the current actor. Owner eyeball
  wanted per verb before any of it informs a signature design; quick and bench together also
  settle the LW-115 AT-list question in one battle.
- [LW-116] 2026-07-22: Knockback: we can already teleport a unit, but a real shove (the Rush
  effect) is three lanes of work and none has run yet; the probe is armed for two of them.
  Lane 1, in the bag pending an eyeball: shove = the owner's cataloged flinch-with-displacement
  page (0x37/0x38) plus the proven teleport triple-write, occupancy refused, Z left to the
  engine's own re-stamp (knockback_probe.py v2; v1 from June predates the render crack and rode
  the dead ct_probe harness). Lane 2, a pure table experiment nobody has tried: assign a Dash
  family formula id to a test weapon and see whether hits push natively; v1's durable note
  applies, proc rates are Denuvo locked so the native rate is the rate. Lane 3, the discovery:
  run knockback_probe.py watch on a victim while the owner Rushes them for real, hunting any
  field that changes BEFORE the world coords start marching; that would be the engine's own
  shove ORDER, the animation register shape again, and lane 1 retires. Wanted on the same tape:
  one plain walk and one non knockback hit for contrast.
  LANE 3 SUCCEEDED FIRST RUN 2026-07-22 and outgrew the ticket: the tape found the engine
  ordering the move 18ms before anything visibly moved, and the same machinery drives ordinary
  walking, so this is the MOVEMENT api and knockback is one mode of it. Read banked as a
  LIVE_LEDGER Uncertain row, tape preserved at tools/probes/tapes_knockback_20260722.jsonl.
  THE WRITE LANE IS DEAD by this method, settled the same night in THREE rounds (one field, then
  three, then the engine's whole fourteen field burst with the is-moving flag last; every value
  stuck untouched for two seconds and nothing moved, and the engine did not even reset them, so
  nothing reads that block at rest). Earlier note, kept because the correction matters: the destination
  alone is inert but sticky, and replaying the engine's entire order in its own sequence
  (destination, counter, then mode last) also stuck in every byte and still moved nothing. These
  fields are the mover's bookkeeping, not its inputs, the same wall shape as the LW-58 pending
  field. So lane 1's composed imitation STANDS as the shipping path for a knockback effect, and
  the read half keeps its value: we can now watch any move and tell a shove from a walk by the
  mode byte. Whatever actually drives the mover is in-process territory (a call, not a poke), so
  it belongs with the deep levers rather than here. Lane 2, the Dash formula table experiment, is
  untouched and still worth ten minutes.
- [LW-114] 2026-07-21: Finish mapping the animation flipbooks: one sweep per sprite class so
  every signature can pick its pages by fact instead of folklore.
  The time mage sweep (tools/probes/anim_catalog.jsonl, all 128 pages owner labeled) proved the
  ids are per sprite class and killed the old decode labels (its "crouch 0x34" is the full
  death animation). Wanted next, about ten minutes each with anim_poke_probe.py sweep: a KNIGHT
  or other weapon carrier (the same-as-previous runs at 0x4b-0x55 and 0x5d-0x63 are suspected
  per weapon category swing variants that a staff collapses; a sword should fan them out), a
  FEMALE sprite, and a MONSTER (chocobo first, since summons and mounts care). Protocol notes
  that earned their keep: sit on your own unit's open menu so CT freezes and the guinea pig
  stays idle; labels append per entry so a freeze loses nothing; the book ends near 0x79.
- [LW-108] 2026-07-21: Restarting a battle quickly is completely invisible to the mod, so it
  keeps believing the old battle never ended; the worst case is a kill being counted twice.
  Found while checking LW-100 (flight tape flight_20260721_211423): battleMode fell to 0 for
  3.469 seconds and the mod's exit debounce is 4.0 seconds (BattleState.ExitDebounceSeconds), so
  no battle-end and no battle-start fired, the turn counter ran straight through (the closing
  line reported 8 turns across BOTH attempts), and Engine.ResetBattleState never ran. Everything
  per battle survives into the replayed battle: kill tracker corpse latches and coverage, growth
  captures, the struct location cache, the LW-42 marker arm, and every signature's state. The
  sharp edge is credit: enemies killed before the restart are alive again while the mod still
  holds their corpses, so re-killing them can credit twice. Seen twice now (3.469s here, 2.860s
  in the 14:04 tape), so a fast reload is the normal case, not an outlier. Candidates: detect the
  board snapping back to spawn tiles (five units moved in one 4ms tick) as a restart edge, or key
  the reset on a battle identity rather than a wall clock gap. Do NOT just shorten the debounce
  without re-checking the LW-42 post battle marker stick that the 4 seconds exists to absorb.
- [LW-111] 2026-07-21: Stepping off a chocobo mid turn keeps the Speed bonus until the turn ends,
  and the item text promises it drops immediately.
  Owner observed it live 2026-07-21 (step 5 of the LW-100 pass): dismounting and moving away held
  Speed at 15 for about 23 seconds, until the turn was committed. Most likely game side rather
  than ours: the mod re-reads the ride bit about ten times a second and reverts on the first tick
  it sees clear (the same session showed an instant revert when the move was undone), so a hold
  that long means the game itself keeps combat +0x1B4 bit 0x80 set until turn commit. Bounded to
  one turn, capped at natural plus 3, self healing, non compounding. Two cheap outcomes: a
  LIVE_LEDGER Uncertain row for when the game clears the ride bit, and a wording fix in
  data/items.json, which currently claims the bonus reverts on dismount.
- [LW-103] 2026-07-21: After a battle ends, the party list and the leftover battle data disagree
  about which weapon a unit is holding, and they stay disagreeing until the next battle; nothing
  visible breaks, but nobody has explained it yet.
  Seen post-deploy 2026-07-21 (LW-87 live pass): from the 14:06:31 battle end to the end of a
  10 minute recording, the roster read weapon 42 for Ramza while his frozen band entry still
  read 37, about 5.4 minutes of steady disagreement (5439 probe ticks, not a blip; the tape
  prints only changes, so the single line at battle end was the ONSET). Harmless today on every
  known surface: the Attack card never composes out of battle (Engine.Tick returns early), so
  the mod logged zero warnings all session, and CursorGate would refuse the mismatch anyway.
  Worth an explanation before anything new trusts a roster read taken out of battle: the likely
  cause is the post-battle equipment reconcile moving the roster while the band stays frozen
  (the LIVE_LEDGER row on broken and stolen gear committing at battleMode 0 is the neighbouring
  mechanism), and the owner may simply have re-equipped in the menu. Check GunSlinger.PrepRoster
  first if it is ever picked up: that lane DOES read roster hands out of battle, though it reads
  the roster (the live side) rather than the stale band. Instrument already in tree:
  tools/probes/cursor_resolve_probe.py.
- [LW-101] 2026-07-21: Players whose game language is not English see no kill counts at all,
  and the only way to get them back is switching the game to English and restarting (player
  report 2026-07-21, native language Chinese; the same player confirmed the switch works).
  The failure is silent and graceful: growth, signatures, and tallies all keep working, only
  the on card text is missing, so a player has no way to tell the mod is fine. What is NEW in
  this report: the wall is not French specific (it reaches Chinese too), and the English plus
  restart workaround is player confirmed. Known cause and wall: the card painter anchors on
  the literal English "Kills: " string baked into our English item descriptions
  (CardPatterns.cs), and a non English game loads its own language item table, so no anchor
  exists to paint into; shipping our text into a per language slot was WALLED live 2026-06-30
  on two independent counts (the game resolves the item table once under English at boot and
  never reloads it when another language activates, and FF16Tools cannot parse the real non
  English tables anyway). Cheap candidates in ascending cost: (a) say it plainly in README
  and on the Nexus page, since the workaround is real and free; (b) detect a non English item
  table at startup and log one clear line (and consider the same message box lane the
  fingerprint guard already owns) so the player learns the mod is healthy and how to see the
  counter; (c) the real cure, DLL live painting of the counter into the loaded language table
  (a genuine RE arc, the painter would need a language agnostic anchor), or upstream
  modloader support for per language text overrides (ask Nenkai; the parser bug report is
  worth sending either way). See the walled French investigation in the memory ledger and
  docs/MECHANICS.md before reopening the data lane; do not retry the item.<lang>.nxd approach
  without new information.
- [LW-6] 2026-07-04: Slayer's Reliquary, the post-release headline bet: weapons remember WHO
  they killed.
  Design: docs/RELIQUARY_DESIGN.md; acceptance: docs/RELIQUARY_AC.md. Phase 0 probes COMPLETE
  2026-07-05 (boss key = per-encounter canonical nameId; same-form minions collide; withdrawal
  bosses like Zirekile Gafgarion produce no death edge, exclude or special-case; a retried
  boss kill must dedup by key). Phase 1 (Marks + card story) SHIPPED 061e36c, awaiting live.
- [LW-7] 2026-07-05: Turn counting breaks under auto-battle: several different units' turns
  all get counted as one unit's.
  Observed turns #2-#6 credited to one fingerprint (log 07:58). The kill-credit half already
  shipped (KillerStamp death-edge stamp, f4bf5df); the turn-count half is still live.
  Candidate: close the acted period on ActorRegister OWNER CHANGE in addition to the
  byte-fall debounce. Must not regress reaction-kill credit (the pointer may name the
  REACTOR during a reaction, unverified per the ledger caveat).
- [LW-8] 2026-07-05: Clicking inside the console window can freeze the whole mod for minutes
  (Windows QuickEdit suspends the thread).
  About 3 minutes observed mid-battle; kills, growth, and toasts all stall (the census
  "hang" was this). Candidate: async/queued console sink in FileConsoleLogger (the FILE sink
  stays synchronous, it is the evidence chain). Until then read livingweapon.log, not the
  console.
- [LW-9] 2026-07-05: The Warbrand (id 67) shows up too early for how strong it is
  (owner-noted).
  Candidates when picked up: later availability tier, price bump, or stat trim (re-run the
  analyze.py dominance gate after any change). Independent of the release-scope
  spriteIdOverride cleanup.
- [LW-10] 2026-07-04: Remove the Treasure Master module (owner-paused until after the 2.3.0
  tag).
  2.3.0 ships the module disarmed (smoke row 7.22). Stage 1 committed 0f842f5 on branch feature/lw10-remove-treasure-master (worktree
  C:\Users\ptyRa\Dev\FFTLivingWeapons-lw10, plan at the worktree root lw10_plan.md, stage 2
  first half uncommitted there); merges to main only after the tag. The production Scholar's
  Ring grant was killed separately in 2.3.0 (LW-86); demoted from Now 2026-07-14 for LW-86.
- [LW-11] 2026-07-04: Give Squires and Geomancers their axe-style weapons back, the cheap
  way only (equip access on existing sword-typed items).
  The rest is walled research: type-welded formula, id-welded art, no known flail formula id.
- [LW-12] 2026-07-04: Three weapon abilities (Maim, Larceny, Ricochet) watch the battle for
  their trigger moment in an older way that can blink and miss it; upgrade them to the newer,
  reliable watching style when those files are next touched.
  (Tech: migrate the lossy-detection siblings to the cache-plus-rearm pattern, the same
  upgrade the Kobu raise detection already got.)
- [LW-13] 2026-07-04: Show milestone marks on the weapon card beyond the kill counter.
  Gated on an untested glyph-render probe; largely redundant with the shipped milestone
  toasts.
- [LW-14] 2026-07-04: Replace the Stormbrand: its on-hit effects are too rare to feel, and
  the real cure is a custom living-weapon ability (a runtime signature).
  Pick the theme AFTER the Samurai signatures lock, to avoid a Slow/element dupe.
- [LW-15] 2026-07-04: Make enemies actually USE living-weapon growth (an extra-large
  undesigned feature; the static rebalance already lands most of the real player want).
- [LW-23] 2026-07-05: When one kill earns two popups (a deed and a tier-up), only the deed
  shows and the tier-up popup is lost (owner observed).
  Ramza's gun earned Beastbane and the deed toast delivered, but the same blow was kill 2
  (tier-up to +2) and no tier-up toast appeared. Investigate contention on the single
  delivery slot (queued and dropped? overwritten?) and make both deliver in order.
- [LW-24] 2026-07-05: The tier-up banner can appear a turn late, while the NEXT unit is
  already acting; the locked policy is deliver on the earner's own turn or not at all.
  Owner screenshot: the Stormbrand wielder's 3rd-kill banner appeared during White Mage
  Collys's turn. POLICY LOCKED (owner, 2026-07-05): fire the UI text only during the earning
  unit's own wait turn; if credit resolves after that window, SWALLOW the message (the card
  and tally still record the growth, so nothing durable is lost). Implementation: compare
  the toast's earner to the current turn owner at delivery time and drop on mismatch;
  turn-owner detection has known traps (the hover-follower struct is NOT the turn owner), so
  use the durable turn/register state. Interacts with LW-23: within the correct window,
  deed and tier-up toasts still need ordered delivery, not mutual starvation.
- [LW-32] 2026-07-05: Rebuild Marks in two waves: first a chronicle that RECORDS what every
  weapon does, then an interpreter that turns those records into titles, so new titles can
  be awarded retroactively from history already collected (owner architecture direction).
  Wave 1 = aggregate counters per weapon and victim class plus a notable-events log (first
  blood, first of each class, boss keys, battle-enders, milestones); the victim snapshot
  already captured at every credit edge makes collection nearly free; KillTally-pattern
  persistence, deploy-preserved, raises LW-29's stakes. Wave 2 = a pure interpreter (policy
  only, fully unit-testable, no live risk). The killer property is RETROACTIVITY:
  interpretation can iterate forever without wronging a save. Owner is on the fence about
  Mark titles doubling the +N system; record-first defers that question (candidate rule: a
  Mark requires PLURALITY of kills, not raw count). Supersedes/absorbs the Phase 1
  legends.json shape when picked up; ties to LW-6.
- [LW-28] 2026-07-05: One deploy LOST the kill tally and legends files even though the
  deploy script preserves them; it is intermittent, a loud failure check now ships, but the
  two underlying causes are still unfound.
  The 17:54 launch logged "No kill tally was found on disk"; the 82-kill tally and the
  Beastbane Mark were gone; the %TEMP% livingweapon_preserve dir no longer existed; the
  17:0x deploy preserved the same files fine. Second anomaly on the same evidence: the
  17:1x session flushed exit tapes at 17:37/17:41 but kills.json kept its 13:45 timestamp,
  so exit-edge tally saves may not have written that session. Investigate both. The loud
  post-restore existence check SHIPPED 2026-07-11 (Get-LostPreservedItems in
  tools/pipeline.ps1; BuildLinked fails red before deleting the backup dir, and the catch
  path re-restores from it). Owner declined tally reconstruction for now (tapes and
  prev.log carry the counts if ever wanted).
- [LW-30] 2026-07-05: Show the weapon's name and title in the attack-targeting text, e.g.
  "Select the target for Beastbane Longsword +2." (demoted from Now when LW-31 took the
  slot; the Abilities-menu funnel covers the in-battle identity job).
  If revived, the locked wording is "Select the target for {Mark}{Name}{suffix}." via a
  PromptSwap prefix match on "Select a target"; unstoried weapons keep vanilla text. Every
  technical unknown was answered live 2026-07-05: writable, render-call-time swap
  (fragment-length unbound), pill auto-sizes to viewport width, markup tokens supported
  ("<keyicon=ok>").
- [LW-39] 2026-07-06: Two party units with identical stats (twins) look the same to the
  mod, so it refuses to guess and their card shows plain vanilla text; give it more
  identifying fields so twins tell apart.
  Owner hit it live: two units at identical level and hp/maxHp made the resolve refuse, and
  the register fallback then dressed Ramza's Attack row in the Spark Rod wielder's dossier;
  the fallback is now removed, so twins simply show vanilla (fail closed by design). Fix
  direction: extend the condensed turn-queue fingerprint with more struct fields; the probe
  dump shows brave/faith-like u16 candidates in the cursor struct needing offset
  verification (turn-owner-probe lines, livingweapon.log 04:0x).
  LW-87's flag-owner resolve (2026-07-21) already gives this surface partial relief: the
  nameId bridge tells identical-stat twins apart on the Attack card now, though the growth
  and locate surfaces still need the fingerprint extension planned here.
- [LW-43] 2026-07-07: The Outrider pistol's twin-gun perk is slow to kick in for a SECOND
  wielder when someone else already has it running (it does apply eventually; owner saw
  the lag live 2026-07-07, not a correctness bug). (Tech: Gun Slinger, Outrider Pistol
  id 71.)
  Suspect the per-wielder locate/write cadence serializes or throttles with multiple
  carriers: check the Gun Slinger signature's tick loop and whether its locate stops at
  the first wielder per tick.
- [LW-47] 2026-07-07: Murasame (id 41) has no living-weapon signature: it was cut from
  2.3.0 when Kiku-ichimonji took the one samurai slot (Mushin); design a new one when
  revived, built on a mechanism already proven live.
- [LW-48] 2026-07-07: Vanity touch: make the in-battle "View Battlefield" label read
  "View Battlefield - Modded by prawl".
  Likely mechanism: a SetTextString-family tap/prefix-match swap (PromptSwap precedent) or
  the text-catalog offset redirect (AttackCard/AttackRow precedent); find the "View
  Battlefield" string source first.
- [LW-58] 2026-07-09: Research arc, RESOLVED: a mid-battle summoned COPY of a live unit is
  fully possible (drawn, named, controllable, AI-fighting, descends from the sky); the
  shippable slices are tracked as LW-64/LW-65/LW-66.
  The road there, kept for provenance: the raw-flag activation path is CONFIRMED DEAD (the
  chest-revert test re-enrolled timeline/hearts/revival but the model stayed a chest and
  the unit's turn soft-locked; treasure-pop signature decoded, +0x45/+0x46 plus the +0x18E
  mirrors). The status system was decoded (three 5-byte layers; apply engine 0x150BF66DC;
  dispatch 0x1401FB064; treasure = status id 15). External pending-field writes are
  consumed but ignored (3 tapes), closing ALL external-write spawn/model lanes. The
  breakthrough: node builder 0x14026EBEC + a data-only AI enroll whose one-byte key is the
  AI-roster index 0x141873038[slot] (0xFF = un-enrolled leads to the null AI-subject
  crash). Battle-scoped (temporary summon; a permanent recruit = a save-roster entry,
  unbuilt). Also cracked in this arc: full unit TELEPORT/SWAP, visual FLOAT, DESPAWN (node
  +0x12C mode-2 + engine sweeper), RESURRECT, and the animation request register (node
  +0x10). Full records: MECHANICS.md breakthrough block, five LIVE_LEDGER Uncertain rows
  plus two overturned walls, memories body-double-spawn-arc / position-write-desync /
  unit-despawn-resurrect-recipe / anim-request-register. BodyDoubleSpike Canary 1-9
  (dev-only, worktree feature/body-double-spawn). Open polish: AI-passivity (behavior
  row), decoy-hold default. Dead-branch extras for the record: the original probe plan
  (plan.md) and tools/probes/spawn_probe.py (built on battle_cheats.py), the band
  +0x17A..0x181 presence-byte candidate, the SpriteSet +0x00 model swap being
  scene-graph-side, the then-next CE step (what-writes on band +0x46 at a pop), and the
  untested frog-cast-in-the-revert-window variant.
- [LW-61] 2026-07-10: Two ALTERNATING playthroughs still share one kill-tally file; key the
  tally to a save identity if cross-contamination proves a real problem in play.
  The shipped Tier-1 reset only archives on a detected NEW GAME (bf351db); this Tier-2
  isolation was deliberately deferred out of LW-51.
- [LW-64] 2026-07-10: Mirror Image ability concept (owner): briefly phase a unit out so a
  locked-on spell whiffs while its sprite stays standing; the decisive test PASSED live.
  Mechanism: flip the hide gate (combat +0x01 to 0xFF); every primitive live-proven in the
  LW-58 gate-toggle session. THE DECISIVE TEST (2026-07-10, owner live): a mid-cast Slow
  whiffed entirely when the target was gate-hidden during the cast animation, so
  hide-at-resolution defeats locked-on actions and the core fantasy is proven. Known
  hazards to guard: restoring onto an occupied tile co-tiles into the movement soft-lock
  (proven live); a mid-hide autosave persists the hidden state into resumes (proven live,
  needs a battle-enter un-strand sweep); hidden units get no scheduler turns, so the
  restore trigger must be external (other units' acted edges, or the dodged action
  resolving). Castable wrapper when built: JobCommand injection plus an action-record
  watch (the Barrage lane). New side effect to chase before any build: the whiffed
  resolution DISPLACED the hidden unit one tile (unexplained; possibly target-snap
  bookkeeping applying to a unit the effect could not find).
- [LW-65] 2026-07-10: Unit TELEPORT is proven live (real units moved, two units swapped
  mid-battle, both acted normally after); it needs a tile-occupancy check and a ledger row
  before it can ship as a mechanic.
  The missing layer was render position: render node +0x4C/+0x50 u16 world X/Y = 28*tile
  + 14 (node via list head 0x140D3A410, +0x148 combat backref). A coherent triple-write
  (combat +0x4F/+0x50 logic, node +0x88/+0x89 AI tile key, node world) moved a real enemy
  who then hovered correctly and took a normal AI turn from the new tile, after which the
  engine re-stamps every layer itself. The Z formula is solved (node +0x4E = -12 x height,
  +1 height unit with FLOAT: the hover offset is pure node data, owner-witnessed granted
  and stripped by Z pokes alone). Un-parks the Knockback family (position-write-desync
  memory updated) and gives Mirror Image its restore-displacement primitive. Open before
  any shipped mechanic: the tile-occupancy check (co-tile = target shadowing + movement
  lock) and a LIVE_LEDGER row (owner flip).
- [LW-66] 2026-07-10: Mid-battle unit REMOVE and RESTORE are both proven live with pure
  data writes (sky-descent flourish included); this unlocks the summon/reinforcement
  mechanic family.
  Despawn = one mode-2 byte on the render node (the engine sweeper tears down unit +
  sprite, byte-perfect); resurrect = AI-registry re-enroll (clone + re-key a living
  object) + node revival (in-use flag, done-mark clear, list re-splice) + present/gate
  reopen. The removal drops AI enrollment, so re-enroll MUST precede visibility (else the
  LW-58 freeze). Full byte recipe in the unit-despawn-resurrect memory; MECHANICS.md has
  the summary. The park-and-summon variant needs no despawn at all (gate FF + render Z
  below floor = invisible reserve). Open: victory-check sanity after a removal; whether a
  legitimate registry rebuild evicts the hand-cloned object; the Ctrl+F5 despawn spike fix
  (hover-marker refusal removed) awaits its next deploy.
- [LW-67] 2026-07-10: Strip every service bound to the F6 test key (owner directive, about
  six F6 users); this repo is DONE, the sibling FFTHandsFree repo still needs its sweep.
  Done here: the four dev spikes (AttackCardSpike, HeaderSpike, FlavorSpike, ShowSpike
  deleted whole) plus their Engine wiring, the spike-only feeders (HeaderProbeText,
  FlavorProbeText) and their tests. AttackCardProbeText and ScanCursor/RegionCursor were
  KEPT: the production Attack-card painter (AttackCard / AttackCard.Census) consumes them.
- [LW-73] 2026-07-11: The flight recorder's unit snapshots do not include health, position,
  or turn charge, so a recording alone cannot prove whether a seat held a real unit or a
  ghost; add those fields next time the recording format is touched.
  (Tech: widen the census band record with hp/position/CT; the LW-34 over-count mining
  needed the raw file log alongside the tapes for exactly this reason.)
- [LW-76] 2026-07-11: A console-noise audit left a triage list of log lines that repeat,
  over-warn, or fire in healthy sessions; walk it with the owner and demote, dedup, or
  keep each. None are urgent (console dedup masks most). The audit yardstick was
  LOGGING.md's match-report contract.
  (a) Repeat-spam risks with no per-event dedup beyond the console's per-battle key:
  Sanctuary.cs:116 re-fires per crystal-counter dip on the same ally,
  GrowthEngine.Ultima.cs:66 re-logs on HP-percent flap, SpiritualFont.cs:164's per-copy
  WARN loop, the Barrage/ShadowBlade grant/release pairs on equip flapping. (b) Info lines
  stretching the match-report definition: SpiritualFont.cs:167 narrates EVERY wielder
  move, PromptSwap.cs:161 doubles every on-screen toast, EagleEye.cs:93 prints per enemy,
  BattleCensus.cs:144 is a WARNING under the [trace] verb (tier/verb mismatch). (c) WARNs
  that fire in healthy sessions: the one-tick locate and readback misses in
  LifeSap/Renewal/Wyrmblood/Rapture/GrowthEngine.Signatures, the revive-and-rekill
  repeat-credit WARN, TreasureMaster.cs:305's self-described-benign weather mismatch,
  AttackCard.Resolve.cs:87 on known stale-cursor hovers.
- [LW-85] 2026-07-14: Finish the after-a-game-patch self-rescue (AnchorScan): teach it to
  re-find the remaining addresses it cannot yet recover on its own, then copy the pattern
  into the sibling mods.
  This is the rest of the LW-82 arc; the v1 slice shipped e77b9d7. Remaining: battle-state
  anchors (CombatAnchor/TurnQueue
  via chained fingerprint scans seeded from the found roster base; needs a live battle,
  and on a patched build the scout cannot trust any pinned battle-state flag to know one
  is running) and the no-content residue (the SubmenuFlag class: boot-time state-solve or
  anchoring relative to signed neighbors; 1.5.1's only data casualty). Sibling-mod
  adoption (copy AnchorScan.cs, the FingerprintGuard pattern, into
  FFTHandsFree/FFTColorCustomizer/FFTMultiplayer) rides this row too
  (hardening-must-be-portable).
- [LW-93] 2026-07-14: The external probe scripts can no longer find battle units on game
  version 1.5.1 (the mod itself can); fix the scripts' outdated assumptions, or rebuild
  them on the pattern that still works, before the next probe is needed.
  During the LW-92 diagnosis, poison_probe.py watch spun on "unit not located" and survey
  reported "0 units, 0 band-located" while the DLL census read the same battle fine: the
  ct_probe-family slot-marker filters (the pre-1.5 slot0==0xFF semantics, the LW-42
  class) filter every unit out. The workaround that worked: an ad-hoc watcher on the
  DLL's own Offsets constants (scratchpad plague_watch.py pattern: BandReadBase =
  CombatAnchor + BandEntry - 24 * stride, guarded rpm, fingerprint scan). Lift that into
  tools/probes as the new base.
  2026-07-21: tools/probes/cursor_resolve_probe.py (shipped with LW-87) is that base, now
  tracked and live-exercised across four sessions: it walks the band from the DLL's own
  constants with guarded reads, replays Band.IsValid and the roster bridge faithfully (a
  plan reviewer re-verified it field by field against the C#), and carries no slot-marker
  filter at all. Copy its read_band plus bridge_count helpers when rebuilding the
  ct_probe family, and keep its two column habit (replay the SHIPPED logic beside the
  PROPOSED one) whenever a probe exists to judge a change.

- [LW-140] 2026-07-27: Silent effects like Renewal's heal still show no number. A probe day
  found the engine routine that builds the floating number popup and a dev trigger fired it
  four times live (paused, unpaused, with and without the unit pointer pre-set): it returns
  cleanly and draws nothing, so the routine is only the last step of a pipeline and the
  caller's preparation work is what actually spawns the number. Verdict recorded in the
  LIVE_LEDGER numeral wall row with the two next levers named (call one level up, or detour
  the natural call site). Reopen only with one of those levers; the easy paths are spent.
- [LW-142] 2026-07-27: The blue move-range tiles render from a record buffer that was decoded
  this session and survived the 1.5 update at its old address. The first held write CRASHED
  THE GAME (owner-run, same day): the renderer consumes this buffer live and tolerates no
  incoherent value, which proves the buffer is real and raises the bar. Any paint attempt
  must write whole coherent records, paused, likely with the record count; treat it as a
  small arc now, not a one-poke test.


## Walled (blocked by engine / Denuvo / modloader)

- Swords cannot get new swing visuals: the art is welded to the weapon id inside the
  engine, and the same render node also drives DAMAGE, so touching it breaks combat.
- Item text cannot ship in French: game + modloader parser walls; the only path is the
  DLL painting text live (or upstream modloader support).

## Format (enforced by TodoContractTests)

- Sections, in this order and no others: Now (with the release name in the header), Backlog,
  Walled, Format.
- Now: at most 5 entries. Entry first line: `- **[LW-<n>] <title>** (opened YYYY-MM-DD) [STATUS]`
  where STATUS is QUEUED, BUILDING, AWAITING-LIVE, or BLOCKED(reason). Every entry carries a
  `- Done means:` and a `- Verify:` sub-bullet. Promote from Backlog by filling those in; if Now
  is at cap, demote something first.
- Backlog: entry first line `- [LW-<n>] YYYY-MM-DD: <one sentence>`; indented continuation lines
  are free. Capture new items here in the session they surface.
- ELI5-first prose (owner rule, 2026-07-21): the first sentence of every entry, and the opening
  of every Done means / Verify, is plain language a non-programmer follows: what is broken or
  wanted, for whom, what done looks like. Technical detail (offsets, hashes, file and memory
  names) comes AFTER that opening, in continuation lines or a "(Tech: ...)" tail, never
  instead of it.
- IDs are unique across this file and docs/CHANGELOG.md; never reuse a retired ID.
- Items exit ONLY by moving to docs/CHANGELOG.md when they ship or die: in the shipping commit
  itself, or in the immediately following commit when the exit row cites that commit's own hash.
- No em dashes and no double-dash separators anywhere in this file or the changelog.
- AWAITING-LIVE flips and VERIFY_LIVE checkboxes are owner-only.
