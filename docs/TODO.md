# TODO

STATUS: CONTRACT (machine-checked by TodoContractTests; format grammar at the bottom of this file)

The work ledger. "Now" holds what is actively being worked for the current release (hard cap 5,
each entry carries Done means + Verify). "Backlog" captures everything else at the cheapest
possible entry cost. Items EXIT this file only through docs/CHANGELOG.md, moved there in the
commit that ships or kills them. The full release ship gate stays in docs/RELEASE_SCOPE.md; Now
is the in-flight subset, not a mirror of that checklist.

Entries are written ELI5-first: the opening sentence is plain language anyone can follow, and
the technical detail lives in the indented lines under it.

## Now (release: 2.3.1)

- **[LW-100] A restarted battle can keep a leftover Speed boost while the rider starts on foot** (opened 2026-07-21) [BLOCKED(needs instrumentation and a slow enough restart)]
  - Done means: a rider who restarts a battle and opens it dismounted no longer carries the
    previous run's leftover mounted Speed until they climb back on a chocobo; the mod
    recognises its own leftover boost even when the signature that wrote it is not currently
    active. (Tech: HoldTimedStat's LW-90 correction fires only at an ACTIVE capture, so a
    dismounted battle 2 open skips capture entirely; candidate is an inactive first sight
    NaturalLedger consult for mount gated signatures. Rides with it: a clean remount capture
    currently drops the post revert corrective sentinel for the rest of the battle.)
  - Verify: the owner ran the live pass 2026-07-21 and it came back INCONCLUSIVE, not clean, so
    the ticket stays open. What happened: the reload took 3.469 seconds and the mod only counts a
    battle as ended after 4.0 seconds out of battle, so it never saw an end or a start, never
    cleared its notes, and simply wrote the natural it still remembered. A reading of natural is
    what the mod produces in BOTH worlds, so that seat cannot tell them apart. Two things did
    change: the premise this ticket rests on is no longer unverified, because the same session
    caught the mod's own boosted value surviving a battle rebuild on the PA lane (read 27 against
    natural 21, exactly the 1.30 hold target) and the previous log caught it on the SPEED byte
    itself (iai read 18 against natural 11). RE TEST RECIPE, in order: (1) land LW-110 first so
    the mount lane actually says what it did, because today it logs nothing when it works and this
    had to be reconstructed from flight tapes; (2) make the restart cross the 4 second debounce,
    or lower ExitDebounceSeconds in a dev build, and CONFIRM a real battle-end plus battle-start
    pair in livingweapon.log before trusting the read; (3) then read Speed at the restarted open
    while on foot, BEFORE remounting. Only then the unit tests: a recorded leftover target is
    refused as a natural even with no active hold, plus the remount sentinel. (Tech: the code hole
    is confirmed, GrowthEngine.TimedStat.cs gates the only FilterCapture call on active first, so
    a dismounted open misses all three arms. The 2026-07-21 pass never entered it.)

- **[LW-123] Give the Defender a shout that pulls one enemy's attention onto the person holding it** (opened 2026-07-22) [BUILDING]
  - Done means: a player holding a grown Defender can point at any enemy on the field, and until
    that enemy has taken its turn, the enemies who act cannot see anyone on your side except the
    bearer, who carries the best parry in the game to survive what it just invited. The shout is a
    real command in the bearer's list, and the mark it leaves comes off when the shout ends, so the
    same enemy can be shouted at more than once in a battle. None of that survives closing the game
    today: everything working is held up by two Python programs, and teaching the mod to do it
    itself is the first half of the job. Acceptance criteria are docs/PROVOKE_AC.md. (Tech: two arcs
    behind a seam. Arc 1, the trigger, is solved and needs moving out of the probes into the
    runtime: the JobCommand injection of ability 189 on Barrage's proven lane, plus two guarded
    idempotent table writes, the authored inflict row 29 at 0x14080FC4E and ability 189's
    InflictStatus byte at 0x14078C1AF in the LIVE action table at 0x14078B2DC, never the decoy copy
    at 0x14078961C. Page protections on all four addresses already pass Mem.Writable, measured
    2026-07-22, so no new memory capability is needed. Arc 2, the hold, holds the composed Invisible
    bit, band +0x47 bit 0x10, on every player side seat except the bearer while an enemy holds the
    turn. Data plumbing: id 33 has no signature block, and adding one turns two description budget
    gates red until the living_weapon_grid.csv cell and the new p3Desc are shortened byte
    identically.)
  - Verify: the owner runs the live pass already written into docs/PROVOKE_AC.md, which starts with
    a bait step so a bearer who was going to be attacked anyway cannot look like a success. Two
    things must be settled BEFORE that pass is worth running, and neither is code. First, cast the
    shipped mark at a boss and confirm it lands at 100%: the only boss evidence on record was
    gathered with Wall, the status this design abandoned, so criterion 0d is currently inherited
    rather than measured and the item card may not claim there is no immunity gap until it is.
    Second, read what the probe reports when its hold on the two table bytes ends, because the
    handoff says one write sticks and the ledger says that is unmeasured; the runtime is being
    built idempotent so it is correct either way, but the ledger row should stop contradicting
    itself. Owner only, as every AWAITING-LIVE flip is.

- **[LW-130] Provoking your own teammate no longer leaves them stuck wearing the Provoked mark forever** (opened 2026-07-23) [AWAITING-LIVE]
  - Done means: casting the Defender's shout at your own side no longer leaves a mark that never
    comes off, so that teammate stops reading Provoked in its status list and can be shouted at
    again later without the game refusing at 0 percent. (Tech: ProvokeHold.ScrubPlayerSideMarks
    walks every player-side band seat on every live tick, independent of the hold's own Idle or
    Armed state, and mask-scoped ClearMark clears the id-0 mark off both status layers, composed +0x45
    and inflicted +0x1D3, on any seat found wearing it, the bearer included. Covered by
    LivingWeapon.Tests\ProvokeHoldTests.cs.)
  - Verify: the owner runs a live pass, casting Provoke at an ally and confirming the status clears
    within a tick and a second cast on that same ally lands rather than reading 0 percent, plus the
    same check aimed at the bearer itself. (Tech: docs/PROVOKE_AC.md criterion 3c. Whether a mark
    could reach a save before the scrub runs stays an open, unmeasured question this pass does not
    have to answer.) Owner only, as every AWAITING-LIVE flip is.

## Backlog

- [LW-131] 2026-07-23: Provoke keeps hanging on for its full thirty second safety timer even when
  the enemy you shouted at HAS taken its turn, so the shout runs far longer than it should and
  every enemy acting in that window gets pulled onto the bearer.
  Why it matters: the shout is supposed to let go the moment the goaded enemy finishes its turn.
  The safety timer is a backstop for a release we failed to notice, and it firing means a real bug,
  which is why it logs as a warning. The handoff assumed the enemy simply never got a turn in
  thirty seconds. The tape says otherwise, so the assumption in that note is wrong and should not
  be carried forward.
  Evidence, and it is unambiguous: flight tape flight_20260723_003332_battle-exit.jsonl. The mod
  armed on nameId 623 at -66.640s. The engine actor pointer then named 623 as the acting unit at
  -48.984s and released it at -47.234s, and did the same again at -40.609s to -37.078s. Two clean
  rising and falling edges, both inside the hold, and the turn counter never moved. The watchdog
  fired at -35.828s, about 3 seconds before that enemy took yet another turn.
  Desk pass 2026-07-23 narrowed it to ONE suspect. TickArmed only counts a turn when BOTH halves
  agree: the team read (TurnQueue +0x02 equals 1) AND the actor pointer naming a unit matching the
  marked identity. The identity half is CLEARED, and the tape proves it rather than argues it: had
  the marked identity stopped resolving, the miss tick counter would have released with reason
  EnemyGone, and it never did, so LocateByIdentity matched that same tuple on every tick of the
  hold. The actor pointer half resolves through the same band entry base the flight tap prints, and
  that tap read nameId 623 off it, the very nameId the mark was armed on. That leaves the TEAM READ.
  What makes the team read genuinely doubtful, and it is not just the standing hover memory: the
  Proven LIVE_LEDGER row this code cites (dated 2026-06-16) is CONFOUNDED. Its own evidence line
  says player turns read 0 including under hover, and enemy turns read 1 across whole turns. Both
  observations are equally consistent with the field describing the CURSOR, because the units
  hovered during player turns were player units, and during an enemy turn the camera follows the
  acting enemy anyway. The experiment never once hovered a unit belonging to a DIFFERENT team than
  the current turn owner, which is the only arrangement that separates the two explanations. The
  LW-87 hover follower row (2026-07-21) then showed the same struct's identity fields moving with
  the cursor, and the whole codebase was re-anchored off it for turn owner questions
  (ActorResolver.Cursor.cs, AttackCard.cs) about a day before ProvokeHold was written against it.
  There is a second, cheaper possibility worth ruling out in the same run: the 2026-06-16 row was
  measured PRE-1.5 at 0x14077D2A2, and the 1.5 re-anchor comment in Offsets.cs records confirming
  team equals 0 only, so team equals 1 during an enemy turn has never been re-confirmed on this
  build at all.
  CORROBORATION FOUND THE SAME PASS, and it is close to decisive. The sibling FFTHandsFree repo's
  own BATTLE_COORDINATES note (that repo's docs folder, not this one) describes this exact struct
  as showing the unit under the cursor
  and says in as many words that the +0x02 team field is the HOVERED unit's team, going stale on an
  empty tile. The same note carries the mechanism that would explain this bug exactly: during an
  enemy's action the AI cursor sits on the PLAYER unit it is targeting, so the team field reads 0
  for the player, the enemy turn gate goes false, and the goaded enemy's turn is never counted. That
  is not proof on this build and it is not an owner flip, but it means the 2026-06-16 row and a
  sibling repo's documentation now flatly disagree, and the row is the one whose evidence has the
  hole in it.
  The discriminating probe is about a minute of play and settles both at once: during an ENEMY
  unit's turn, move the cursor onto one of YOUR units and read TurnQueue +0x02. Reads 1, the field
  is the turn owner and the Proven row survives (look elsewhere, and suspect the actor pointer
  timing). Reads 0, the field follows the cursor, the Proven row is wrong and must be corrected,
  and this bug is explained: a player idly hovering their own units during the enemy phase silently
  switches the shout's turn detection off.
  Do NOT simply delete the team gate as the fix. It is there because the actor pointer PARKS on
  struck victims, so without it a marked enemy that merely gets HIT during your own turn would
  count as having taken its turn. The replacement has to be a signal that is per turn and not per
  cursor, most likely Band.FlagOwner, the PSX turn flags walk the rest of the runtime re-anchored
  onto for exactly this reason.
  Note for whoever picks this up: LW-127 (the single enemy slice) is gated on the same turn
  detection question, so answering this answers part of that too.

- [LW-129] 2026-07-22: When Provoke hides your units, they wear a visible invisible status icon over
  their heads, which tips off the player that the mod is doing something; suppress that icon so the
  trick stays hidden.
  Why it matters: the AI ignore flag and its icon are the same bit (band +0x47 bit 0x10), so we
  cannot set the behaviour without the icon; suppressing it needs a separate rendering side lever.
  Owner confirmed the icon still shows in the 2026-07-22 live test and wants it hidden.
  State: candidate lever is the global overhead UI toggle (u32 at 0x436A367BF8, write 2 = UI off)
  found via CE 2026-07-09, but it sits in the DYNAMIC 0x43xx region so its launch to launch
  stability is UNCONFIRMED, and it is unproven whether value 2 hides the status ICON versus only the
  HP bar. Gated on a read only probe first: does the address hold a sane 0/1/2 each launch, and does
  writing 2 take the icon with it. Alternatives: a per unit icon visibility field, or setting the AI
  layer and leaving the icon layer clear if the two split. ProvokeHold already carries a SuppressIcon
  no op seam waiting for the proven lever. Hiding the reddened HP bar of a hidden ally is separate.

- [LW-128] 2026-07-22: Provoke pops an empty speech bubble over the caster (Ramza's portrait, no
  words) on cast; fill it with a taunt, since Provoke is literally a taunt.
  Why it matters: that bubble is the game's own callout for the ability, Embrace's vanilla quote
  slot gone blank after we renamed and repointed the ability, and it fires exactly on cast, so it is
  free thematic flavour going to waste. Owner asked for a jeer in there 2026-07-22.
  State: the mechanism is a Proven ledger row (a callout bubble carries mod supplied text via the
  show flag hijack: poll the callout text holder show flag, inject our line on the rising edge).
  Candidate lines drafted (Eyes on me curs / Come break upon my shield / Your mother swung truer);
  consider rotating a few at random. Polish; rides after the core arc and the usable by AI fix.

- [LW-127] 2026-07-22: Provoke ships redirecting EVERY enemy that acts while the shout is up (window
  mode), not just the one you point at; the clean single-enemy version needs the game's turn order
  read so the mod can hide the party just before that one foe acts.
  Why it matters: the owner wanted only the provoked enemy redirected, but the slice approach (hide
  only during that foe's own turn) loses the turn-start race live. The AI picks its target the
  instant its turn opens, so a hide that reacts to the turn starting lands too late (flight tape
  2026-07-22: the marked enemy moved on the same tick it became the actor). Window mode (hide the
  whole enemy phase) avoids the race but pulls every acting enemy onto the bearer.
  State: Tuning.ProvokeSliceMode is the switch (ships false = window). The fix is turn-queue
  lookahead: read the turn order (LW-118), detect the provoked enemy is next, and pre-hide in the
  gap before its turn opens. Gated on LW-118 proving the turn queue is readable. Polish A already
  moved the turn detection onto the proven actor pointer, so the release half is done.

- [LW-126] 2026-07-22: When an enemy you shouted at with Provoke gets mind-controlled onto your
  side by Galewind's Puppeteer, the shout does not notice the takeover and hangs on until a
  failsafe fires; it should recognise the takeover and let go right away.
  Why it matters: a Puppeteered enemy is now driven by the player, so it never takes the AI turn
  Provoke is waiting on. In the shipping slice mode this is harmless, since a takeover means that
  enemy is never the acting AI unit and nobody is hidden for it, and the thirty-second watchdog
  clears the stranded armed state on its own; but a direct release is tidier and becomes REQUIRED
  if the window fallback mode is ever the shipped one.
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
- [LW-118] 2026-07-22: Find out whether we can read, and eventually reorder, the game's turn
  order array; if it is writable, time control is complete and Quick, Delay and Haste become
  mechanics instead of metaphors.
  The ledger's Combat Timeline row is Uncertain, dated 2026-06-16, describes a 4 byte record
  array with byte0 = CT and byte1 = a tile X locator, and writes its own address with a tilde,
  approximate. It also predates the 1.5.1 re-anchor, and the re-anchor rule is to verify at the
  old address before scanning. tools/probes/turn_queue_probe.py is therefore READ ONLY on
  purpose: dump correlates candidate records against every live unit's real CT and tile X
  (five or more matches is proof by construction, one is coincidence), find fingerprint scans a
  bounded window if the address moved, and watch samples while the clock runs to confirm byte0s
  march with CT accrual. No write verb exists until a read proves the array is real, current and
  understood. Protocol: sit on an open menu for dump and find so the clock is frozen and the
  cross check cannot disagree by timing; close it for watch. Whatever the answer, the ledger row
  gets updated, including a clean negative.
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
- [LW-112] 2026-07-21: A popular kind of mod (custom jobs/items) makes Living Weapons switch
  itself off with a message blaming a game update that never happened; the guard is
  misdiagnosing a mod conflict as a game patch.
  Player report 2026-07-21 (the same player as LW-101): loading CustomJOB_ITEM alongside us
  popped the stand-down box ("it does not look like the version the mod was built for").
  Desk-confirmed root cause: startup landmark 2 is the JobCommand rec 8/rec 9 ability-id bytes
  (LaunchGuard.Landmarks.cs Rec8Sig/Rec9Sig = Archer's Aim ids 150..157 and Monk's Martial Arts
  ids 100..107). A custom-job mod rewrites exactly those bytes (whole-row table writeback,
  modloader-merge-semantics), so the signature legitimately vanishes, ANY single landmark
  mismatch held 30 ticks stands the whole mod down (FingerprintGuard.cs anyMismatch path), and
  the message asserts a game update. The PE build key MATCHES in this scenario, which is the
  discriminator we already hold but ignore. Fix direction: when the PE key matches and only a
  DATA landmark mismatches, say the truth (another mod rewrote the same game data, name the
  landmark, suggest load order/compat) and consider degrading only what rests on that anchor
  (Barrage kit injection) instead of the whole mod; rec8/rec9 is Barrage's anchor, not a game
  version check, and job mods will always touch it. Verify with the player's livingweapon.log
  stand-down line, which names the landmark (LW-83). Unexplained residue, do not paper over:
  the player then merged the other mod's table rows into a third mod's folder and reports both
  now work, which should NOT clear the memory bytes; suspect the merged copy is silently inert
  (wrong folder shape, or a bad value making the modloader reject the whole file), meaning
  their custom jobs are likely dead and they have not noticed. Their in-game state is worth a
  question before advising anyone else to copy the workaround.
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
- [LW-109] 2026-07-21: When a timed stat bonus ends, one unexpected reading throws the bonus away
  for the rest of the battle with no way back.
  GrowthEngine.TimedStat.cs's window-closed branch removes the record unconditionally, including
  when the revert write was skipped because the byte read neither the boosted nor the baked value.
  With a clean capture no corrective sentinel is armed (that arm is gated on a baked residue), so
  the bonus is abandoned silently. Asymmetric with the ordinary Hold path, which keeps its record
  and can re-apply. Found by desk review during the LW-100 evidence pass, not observed live.
- [LW-110] 2026-07-21: The mounted Speed bonus never says anything in the log when it works, which
  is why a simple question about it needed a forensic pass over flight tapes to answer.
  HoldTimedStat logs only its two correction paths; the capture, the boost write, the re-apply and
  the revert are all silent at every level, and growth is not tapped by the flight recorder at
  all. Consequence: absence of a log line proves nothing about this lane, which is exactly the
  trap the LW-100 pass fell into. Add a Debug line at capture, boost and revert (file sink gets
  everything, so console noise is not a concern), and consider tapping stat holds in the recorder.
  Blocks a trustworthy LW-100 re test. Related trap for triagers: GrowthEngine.cs and
  GrowthEngine.TimedStat.cs emit nearly identical "restart residue corrected at capture" strings,
  distinguishable only by the lane token the first one carries, so grepping that phrase can look
  like every lane was checked when only one was.
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
