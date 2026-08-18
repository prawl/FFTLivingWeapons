# TODO

STATUS: CONTRACT (machine-checked by TodoContractTests; format grammar at the bottom of this file)

The work ledger. "Now" holds what is actively being worked for the current release (hard cap 5,
each entry carries Done means + Verify). "Backlog" captures everything else at the cheapest
possible entry cost. Items EXIT this file only through docs/CHANGELOG.md, moved there in the
commit that ships or kills them. The full release ship gate stays in docs/RELEASE_SCOPE.md; Now
is the in-flight subset, not a mirror of that checklist.

Entries are written ELI5-first: the opening sentence is plain language anyone can follow, and
the technical detail lives in the indented lines under it.

## Now (release: 2.3.3)

- **[LW-198] The eleven knives all had a white blade, so their colour lived in a handle a few pixels across** (opened 2026-08-13) [AWAITING-LIVE]
  - BUILT and gated 2026-08-16, owner gallery pass outstanding. Coverage was never this family's
    problem, at a CARD median of 41.2 percent, the healthiest of any family in the programme.
    The problem was that four of the eleven read as the same pale sliver in a list, because the
    artist drew every knife with a white blade and only the small grip in colour, and the tints
    left the blade white. All eleven now have a coloured BLADE with a bright fuller and a
    distinct grip.
  - Two specific colours were forced rather than chosen. The Zwill Straightblade kept its vanilla
    name and was painted dream lavender over art that measures a warm 46 degrees at chroma 0.178,
    141 degrees from its own picture; the new anchors gate reported it on sight, which is the
    first time that gate has caught something before the art shipped rather than after. And the
    Mortal Coil and the Bloodlash were the SAME necrotic green, 0.05 apart in value and nothing
    else, which the palette tripwire could not see while the family was still on the old engine.
  - A knife is a SWORD in miniature and takes the sword recipe unchanged: the card art has a dark
    braided grip and a bright fuller, and the two sword keys land on exactly those. (Tech: shared
    sprite id 1 / id 68 needed grip pct 30 rather than 24, where the icon claims 2.8 percent.
    Shipped zone share: grip card 6.5 to 22.3 percent and icon 6.2 to 15.2, fuller card 12.2 to
    25.4 and icon 14.5 to 21.0. Moving the family off bright-v2 displaced four selftest pins that
    used a knife as their sample and killed one dead override, all caught by the pins themselves.)
  - Done means: all eleven carry their identity colour across their solid art with a visibly
    separate second material, no two read alike at list size, every reserved name is anchored
    against BOTH surfaces with the reasoning recorded, and no already-approved art moves.
  - Verify: all four gates green (recolor_icons selftest with the family inside every owner-rule
    pin and mutations proving those pins bite, icon_preview.py compare --expect naming exactly
    these ids, anchors, silhouettes); each knife's second material measured on the real art; the
    bake matching a FULL preview manifest pixel for pixel; and the owner's gallery pass.
  - This row also carries the LW-198 through LW-226 PROGRAM STATEMENT that the other section
    rows cite, owner-directed 2026-08-13: after the shields pass (LW-190) set the quality bar,
    the owner called the first-pass recolours hasty ("sloppy"), so every equipment section gets
    the treatment that made the shields land: per item owner review rounds judged as pictures,
    rule fixes over pixel fixes, variant picker pages when a call is contested, engines chosen
    per family on evidence, and the identity proof (preview equals production, bake matched
    pixel for pixel) at the end. The assembly line is docs/DEV_TEST_RECIPES.md ("Icon recolor
    process") plus the engine modes in tools/recolor_icons.py. Order is now decided by
    MEASUREMENT rather than by the ledger, lowest true CARD median first; eleven families have
    been through it as of 2026-08-16, and what remains after the knives is ninja blades (63.2
    percent), books (67.0) and bags (72.1).
- **[LW-205] Four of the nine ninja blades were a pale blade with the whole identity in a small coloured guard** (opened 2026-08-13) [AWAITING-LIVE]
  - BUILT and gated 2026-08-16, owner gallery pass outstanding. Like the knives before them these
    were picked for what a player sees rather than for a coverage number, which was already a
    CARD median of 63.2 percent. All nine now have a coloured blade, a metal guard and a bright
    fuller, and one of them is back on the colour its own artwork is painted in.
  - Three kept their vanilla names and they split two ways under the anchoring gate. The Sasuke's
    Blade and the Iga Blade measure near-neutral, so their colour is free, though the Iga is kept
    WARM because its art is warm and there was no reason to fight it. The Koga Blade is not free:
    at chroma 0.232 it is emphatically a gold blade with a green guard and it was wearing flat
    green, so it keeps its gold and its Dark element and Poison rider go where the artist already
    put green, into the hilt. That is the Holy Lance's resolution for the fourth time.
  - (Tech: same sword recipe as the knives and katanas. Three percentiles moved per sprite: the
    Mistedge's and Silentfang's guards are barely darker than their blades, and the Raijin
    Longblade is the family's awkward one, its CARD carrying a large dark region the hilt key
    claims 42.5 percent of at the default while its ICON guard is small, so hilt 18 with the
    levin fuller widened to 34 to keep the lightning on the card. Moving the family off bright-v2
    emptied SMALL_TWO_ZONE entirely and displaced three more pins, all caught by the pins.)
  - Done means: all nine carry their identity colour across their solid art with a visibly
    separate second material, no two read alike at list size, every reserved name is anchored
    against BOTH surfaces with the reasoning recorded, and no already-approved art moves.
  - Verify: all four gates green with the family inside every owner-rule pin and three mutations
    proving those pins bite; each blade's second material measured on the real art; the bake
    matching a FULL preview manifest pixel for pixel; and the owner's gallery pass.

- **[LW-212] The four bags, the last weapon family in the game to have its colour looked at** (opened 2026-08-13) [AWAITING-LIVE]
  - BUILT and gated 2026-08-16, owner gallery pass outstanding; seat returned 2026-08-18 after its
    loan to LW-262, which shipped. A CARD median of 72.1 percent, the highest of any family, and
    the one real fix is the one the new anchoring gate catches: the Fallingstar Bag kept its
    vanilla name and was painted gold over artwork that is green. A bag is one piece of leather
    with no furniture, so its second tone is a SHEEN in the veils' sense and a pale neutral for
    the same reason: a coloured tone on a pouch reads as a stain. The falling star is its gold
    clasp. With this family every weapon in the game has been through the re-pass: the old
    bright-v2 engine has no items left, so it is retired to a dormant branch rather than deleted,
    and CARD_OVERRIDES and SMALL_TWO_ZONE are now empty tables for the same reason.
  - Done means: each bag reads as its own item at list size with an identity colour and a visibly
    separate second material, no two alike, and the Fallingstar Bag matches its own artwork.
  - Verify: the four icon gates green with the pins proven by mutation, the bake matching the FULL
    preview manifest, reserved-name anchoring recorded, and the owner's gallery pass.

- **[LW-267] Three guards inside the new resumable search have no test holding them down** (opened 2026-08-18) [BUILDING]
  - The line that keeps the region list marked stale on two of its three paths, the number
    deciding how often a long search reports progress, and the choice to stop reading a region
    when a read fails rather than skipping past it. Each survived deliberate mutation with the
    whole suite green. None is believed wrong today; they are simply unpinned.
  - Done means: one pin per guard, each proven non-vacuous by re-applying the exact mutation
    that survived and watching the new pin go red. Tests only; any test-accessor visibility
    flip follows the established convention with zero behavior change. (Tech: the _stale
    assignment and ProgressLogEveryTicks in PoolLocator.Restart.cs, and PoolScan's read == 0
    branch.)
  - Verify: full suite green; each pin red under its target mutation and green on restore.
- **[LW-260] Six things in the card heartbeat work are correct today but have no test** (opened 2026-08-18) [BUILDING]
  - Found by mutation during the LW-257 commit 2 verify, which changed each one deliberately
    and watched the suite stay green. Worst first: a second drain check call per tick ships
    green and would pay a full paint spot copy plus an every region compare thirty times a
    second instead of once; a separate in battle clock ships green, the exact thing the design
    forbids; dropping the pending list clear on cache wipe ships green, costing wasted watch
    beats and a misleading gave up line; throwing away the located region cache after a
    targeted re scan ships green and reintroduces the 143 to 569 millisecond stall one step
    removed; the two guards stopping a double paint in one tick ship green if removed; and the
    region range check accepts a loosened end bound. Two soft spots noted by the same verify
    carry along for the exit note: the settled predicate falls back to permissive for the whole
    on field stretch after a battle starts, and the drain re latch walks the OLD key set.
  - Done means: one pin per mutation, each proven non-vacuous by re-applying its exact mutation
    and watching the new pin go red. Tests only; locate the guards by code pattern, not the
    quoted line numbers, which may have drifted. (Tech: mutations E2, M, K, F from the LW-257
    commit 2 verify; the two !countsChanged guards near Display.cs:81 and :120; the end bound
    at Display.PoolDrain.cs:67.)
  - Verify: full suite green; every pin red under its mutation and green on restore.

## Backlog

- [LW-270] 2026-08-18: One small piece of the cleanup rate limiter has no test holding it down:
  when a big cleanup pass earns an immediate repeat, the refusal counter is also supposed to
  reset to zero, and deliberately deleting that reset leaves all 3159 tests green. The effect of
  losing it is mild, the every-32-refusals cleanup rhythm would start from a stale phase after a
  later low-yield pass, but it is the same unpinned-guard class as LW-269 and was found by the
  LW-269 verify round's own extra mutations. (Tech: the `_refusalsAtCap = 0` half of the rearm
  block in CardSites.Admission.cs's PruneDeadSites; the `_pruneImmediately = true` half IS pinned
  by Prune_evicting_exactly_the_floor_rearms_immediate_retry.)

- [LW-268] 2026-08-18: The search re-reads the whole 2.8GB of game memory every time, even though at most 145MB of it has ever held what it is looking for, and the regions it found last time were still there next time. An index that remembers which regions were already checked and re-reads only new or resized ones could cut a rescan from tens of seconds to a few. The catch that must be probed FIRST: if the game pre-commits big arenas and writes card text into them later, a region can become interesting without changing its size, and the index would skip it forever with no error and no log line. Step one is a probe that logs the full region list diff alongside each locate across one battle and checks whether newly interesting regions are freshly committed or pre-existing. Sequence this after LW-262, because fixing coverage latching should make rescans rare enough to re-price the whole idea. (Tech: candidate design diffs (base,size) against the last completed PoolScan snapshot and carries not-pool verdicts for unchanged regions; hazard is that VirtualQueryEx sees commit granularity, not content.)

- [LW-263] 2026-08-18: Three of the new pool search files describe their own size wrongly, saying a
  file is 186 lines when it is 160, and another is 186 when it is 188. The numbers went stale the
  moment a method moved between those files during the fix rounds, which is the whole argument
  against quoting a figure in prose that changes every time someone edits the file. Found by the
  fourth verify round on LW-261, and that is four rounds in a row finding this same class of defect.
  Fix is either to correct them or to stop quoting line counts in prose at all. (Tech: the
  PoolLocator.Restart.cs header and the matching claims in PoolLocator.cs and PoolLocator.Log.cs.)

- [LW-264] 2026-08-18: One doc comment still says the search finishes at most a handful of times per
  restart window, which stopped being true when the empty retry lane landed. On a cold boot where no
  text pool exists yet, the search finishes and starts again roughly once a second, indefinitely. The
  twin of this exact sentence was corrected during the same arc and this copy was missed. (Tech: the
  LocateRecordBudget doc in Display.Flight.cs; the corrected twin is LogLocateComplete in
  PoolLocator.Log.cs.)

- [LW-256] 2026-08-17: Four files point to an explanation of the retry bug that this branch does
  not have yet, and nothing automated notices. The `battle-retry-rewind-fingerprint` writeup
  landed on `main` in commit 438b173, but this arc's branch (`lw233`) was cut from an earlier
  commit, so `RestartSentinel.cs`, `RestartSentinel.Policy.cs`, `KillTracker.cs`, and
  `KillTracker.Corpses.cs` all cite a ledger row that is not in this branch's copy of
  `docs/LIVE_LEDGER.md`, and the doc gate stays green anyway because it only checks links between
  docs, not whether a code comment's citation of a ledger row actually exists. Merging `lw233`
  back onto a `main` that already carries that row settles it for free; this row exists so the
  gap is not silently forgotten if that merge is delayed or done some other way. Not a fix to make
  here: no rebase, no hand-added row, that is a merge-time call for the owner. (Tech:
  `DocsContractTests` (LivingWeapon.Tests) enforces repo-wide doc-to-doc link integrity but has no
  check that a `[slug]` cited from a `.cs` comment actually resolves to a row in
  `docs/LIVE_LEDGER.md`; the four current citations are `LivingWeapon/Kills/RestartSentinel.cs:17`,
  `RestartSentinel.Policy.cs:8`, `KillTracker.cs:132`, and `KillTracker.Corpses.cs:109`, all citing
  `[battle-retry-rewind-fingerprint]`.)

- [LW-259] 2026-08-17: The card painter's new black box can fall silent partway through a long
  battle, so a problem that happens later leaves no trace at all. The budget that caps how many card
  records one window may write is spent across a whole battle, while the priority rule protecting the
  records that matter only applies inside a single paint pass, so a long fight spends the window on
  routine paint lines and every eviction record after that is dropped without a word. Roughly eleven
  kills is enough. Found by the LW-257 round 4 adversarial verify, which also noted the fix already
  exists one field over: coverage records were given their own small reserved budget in the same
  commit and never starve, so evictions want exactly that treatment. Until then the workaround is in
  the LW-257 live script, since opening the status card resets the window. Three tidy-ups ride along
  from the same review: AnnounceCoverage takes two full site snapshots on one call where one would
  do; CardVerdict.Note's bump is O(n) in the all-evictions corner and leaves Entries no longer in
  insertion order, neither of which its class doc mentions; and the targeted Paint path accrues
  strikes without evicting, which is consistent but undocumented. (Tech: _flightBudget in
  Display.Flight.cs resets only in Display.Invalidate(); mirror CoverageRecordBudget's reserve for
  the Evicted tier.)

- [LW-258] 2026-08-17: Stage 3 of the card-disagreement fix, parked deliberately until the new tapes
  say whether it is needed. If the LW-257 commits land and a card copy is STILL going dark (rather
  than merely being drawn before the paint), the answer already designed is a standing re-offer: walk
  one chunk of one already-located pool region per maintenance beat, round-robin, through the existing
  OnChunk. Never a whole-process region walk, never the retired whole-heap sweep. What makes it
  affordable is a second piece worth having on its own: split CardScanner.FindKills so the literal hit
  and the meter-slot check happen BEFORE the 121-flavor attribution search, and skip attribution
  entirely for a slot address CardSites already owns, which cuts the dominant per-chunk cost by
  roughly ten times in steady state. Promote this only on evidence from the LW-257 taps (a site that
  keeps getting evicted, or a region whose count keeps draining), never on suspicion. (Tech: full
  design in the LW-257 round 1 proposal, section 4 items 10 and 11; the new `card` flight records
  named `site-evicted` and `coverage` are the evidence to read.)

- [LW-253] 2026-08-17: The twin weapon takes up to ten seconds to visibly appear on the status
  page when the player bounces in and out of menus, and the owner asked for about two if it
  comes cheap. The wait is polling arithmetic, not a bug: the twin pass runs once a second and
  the safety rule wants two consecutive safe-screen readings before writing, so every dip back
  into the equip screen resets the count. (Tech: raise the gunslinger phase cadence from 30
  ticks toward 10 with an in-battle modulo guard so the in-battle re-assert keeps its
  established one second rhythm, or sample PartyBrowseFlag per tick and decide per pass; the owner accepted
  the current feel for the shipped cut, so this rides a normal round with its own verify.)

- [LW-254] 2026-08-17: The phantom-pistol refund detector shipped watching but not acting: it
  names every refund the game issues for the conjured twin and records the full evidence, and
  the correction that would take the phantom back out of the inventory stays unbuilt until
  those recordings prove the detector fires on phantoms and nothing else. The adversarial
  review rejected arming it because the inventory count is one global number per item, so a
  teammate's lawful pistol return inside the same second could be blamed on the twin and a REAL
  pistol destroyed. (Tech: watch-mode lane in GunSlinger.Reconcile.cs, flight record type
  twin-refund; arming needs the hardened rule from the plan-v3 review verdict: menu context
  required, rise exactly one, both sack reads valid, no other row's gear transition touching
  the id in the window, plus one round of watch tapes showing the fire-set equals the phantom
  set. The interim cost is accepted phantom inflation, benign direction only.)

- [LW-255] 2026-08-17: The black box records what the mod SAW but not what it DECIDED, so any
  check that quietly says no leaves no trace at all and the next person has to re-derive the
  refusal by hand. Proven the expensive way on the LW-233 live drill: the retry detector declined
  to fire, and because it only ever writes a record on SUCCESS, settling which of its gates
  refused took a code read plus stopwatch arithmetic against the tape instead of a single line
  saying so. Three gaps, worst first. One, a detector that can decline records nothing when it
  does; it should name the gate that refused. Two, values the code DERIVES are absent, so a
  reader has to recompute them from the raw inputs and can get it wrong. Three, records are
  written only on change, which makes a quiet stretch and a stalled tick loop look identical on
  the tape, and the LW-233 drill carried an 8.6 second hole of exactly that kind across the
  game-over screen. The generalisation worth keeping is that a silent refusal is the expensive
  kind of silence, because the moment anyone cares about it the evidence is already gone.
  (Tech: RestartSentinel.LogOpenEdge is the only recorder call in the class, so the miss paths
  in Tick / PresentRevive / ProcessStash and the ShouldOpenLatch grace refusal are all invisible;
  onField, the derived gate input feeding the sentinel's inLiveish, is absent from the tape and
  has to be re-derived from the mode records through BattleState.OnField; a sparse heartbeat
  record would separate a starved tick loop from a quiet one. Evidence tape
  tools/probes/tapes/lw233_death_retry_live_20260817.jsonl. Vocabulary is FROZEN per the
  log-facelift note, so new record types need that constraint respected.)

- [LW-246] 2026-08-16: Players did not like the recoloured icons, and the reason is now known
  and measured instead of guessed. A spriter player named it and a five lens diagnostic confirmed
  it: the old engine threw away the artist's own colour choices pixel by pixel, so highlights
  stopped describing light and every sprite became one colour made lighter or darker. A
  replacement approach was prototyped and iterated live with the owner across twelve review
  rounds until all sixteen shields passed at game size: keep every brightness the artist chose,
  move only which colour family each material wears, leave outlines, glints and gold fittings
  alone, and add an identity rim glow. The finished shields went straight into the live install
  on 2026-08-16 for the owner's in game look; the repo mod tree is deliberately untouched until
  LW-247 makes the pipeline able to reproduce them. (Tech: prototype is
  tools/probes/ramp_engine_prototype.py; diagnostic evidence: legacy hue entropy 0.129 bits vs
  vanilla 2.569 on shield cards; all 224 invented brightness edges sat on the zone percentile
  seam; outline ring saturation was repainted 0.29 to 0.60. Prototype metrics on all 32 renders:
  zero invented edges, ring delta 0.000, mean saturation within about 1.2x of vanilla. The
  earlier palette census in this row's history reproduces via
  `python tools/probes/vanilla_palette_sample.py <out.json>`.)
- [LW-247] 2026-08-16: The new shield look lives only in a probe script, so the repo cannot
  rebuild what is now deployed. Port the ramp engine into tools/recolor_icons.py as a real
  engine, regenerate the sixteen shields through the pipeline, and take the four icon gates
  green, so the deployed look and the repo agree again. Waits on the owner's in game pass of
  the deployed shields. (Tech: rules to port from the probe: hue rotation with one per material
  delta and value never touched; donor ramps from the vanilla cache with hue discipline; twin
  groups keyed by shared drawings with consensus frame seeds; ink neutralisation; the shadow
  saturation ceiling 0.30 plus 0.55 times value; per surface identity placement; the POP boost
  on ids 134, 136 and 138 and every per item call from the 2026-08-16 session ride in the
  probe's tables.)
- [LW-244] 2026-08-14: Ragnarok kept its original name and renders lilac over artwork that is
  warm orange, 115 degrees away, and nobody ever measured it. It is the same shape of problem as
  the Whale Whisker (LW-238): a knight sword you passed by eye, whose violet was chosen as "the
  dark arriving as fire" on its fuller, which is a good reason for a colour and not a reason
  that survives the rule that an item keeping its name keeps its look. Found by the new
  reserved-name gate the moment that gate existed, which is the point of building it. Owner
  call: keep the violet and let the reason be written down, or bring it back toward its own
  amber. (Tech: id 36, icon chroma 0.138 at hue 18 degrees against a rendered 263;
  `python tools/icon_preview.py anchors` lists it, and it sits in recolor_icons.ANCHOR_RULINGS
  as OPEN so the gate reports rather than blocks.)

- [LW-245] 2026-08-14: The two gates that judge artwork have to be run by hand, and one of them
  now matters enough that forgetting it is a real risk. The reserved-name check and the
  shared-picture check both need the game files and the texture tool, which the automated build
  on the server does not have, so they live beside compare as things a person runs. That is the
  same gap LW-241 describes for compare itself, and the answer is probably the same one: a
  single local pre-commit or pipeline step that runs all three when the icon tools or the item
  colours change. (Tech: tools/icon_preview.py anchors / silhouettes / compare --expect; the
  recolor selftest is the only one wired into tools/pipeline.ps1 today.)

- [LW-242] 2026-08-14: Nine weapons wear a second colour so dark that the measurement says it is
  barely there, and the file's own rule predicted exactly that. The rule, written during the
  sword pass, is that a dark tone laid on the art's dark share is invisible by construction,
  because the body already renders those pixels dark. The file then said the one dark metal was
  used on one sword as a deliberate exception. It is on nine items, and re-measuring on the
  pixels each zone actually claims puts every one of them at 27 to 44 out of 255 from the same
  render without its second colour, where every bright metal on the same key sits at 109 to 159.
  The owner passed all nine by eye and that is the higher authority, which is why nothing was
  re-tinted; this row exists so the choice is a choice. Affected: Flamberge, Swiftedge,
  Lightbringer, Defender, Excalibur, Chaos Blade, Wellspring Rod, Ember Rod, Rod of Faith.
  (Tech: BLACK_IRON (0.615, 0.10, 0.45) on a shade-keyed zone, ids 26/28/31/33/35/37/51/53/58.
  Measure with zone_recolor over one zone against the same recipe with zones removed, median
  max-channel delta over pixels at mask weight >= 0.5. A p90 over the whole sprite, which is how
  the original rule was stated, reads 0 for any zone under a tenth of the art and is the reason
  this went unnoticed.)

- [LW-237] 2026-08-14: The Wellspring Rod's orb, the one part of it that is supposed to glow
  green, never gets painted on the big equip card. Its recipe asks for the brightest slice of
  the picture and on that particular sprite the slice survives smoothing as a SINGLE pixel, so
  the card ships the rod's body colour over the artist's green orb and reads as one flat colour.
  The single surviving pixel is not even on the orb: it sits halfway down the shaft.
  The same recipe finds a 30 pixel orb on the small list icon, so the item looks right in the
  list and wrong on the card. Confirmed by the LW-208 audit; no gate can see it, because the
  no-single-colour pin measures a fixture, not the real art. (Tech: id 51, card. cover mask at
  pct 18 is 104 raw pixels, 14 after two smoothing passes and 1 solid pixel after the whole
  chain, sitting at 0.68 along the sprite's long axis where the other seven rods' orb zones sit
  at 0.03 to 0.28; min_blob makes no difference, pct 26 recovers 33. Total second-material share
  9.5% against a rod median of 32.2%, the minimum of every reviewed weapon card. Fixing it changes art the owner already passed, so it
  needs his eye on a before-and-after.)

- [LW-238] 2026-08-14: The Whale Whisker kept its vanilla name, which by the owner's own rule
  means its colour should start from its own picture, and it was painted cyan over art that is
  visibly red. Its list icon reads warm red at a chroma HIGHER than the Perseus Bow's, and that
  bow got a measurement, a written note and an owner ruling one commit earlier; this pole got
  none of the three, and its hue barely moved from the pre-pass value. The counter-argument is
  real (it is the family's only Water pole, and cyan says water), but it is unrecorded, so the
  next pass will read the file and learn the wrong lesson. Owner call: re-anchor to the art, or
  write the exception down the way the Perseus Bow's is. (Tech: id 114. Icon 1.7 deg at chroma
  0.148 against the tint's 196.2 deg; the shipped SILVER zone takes the blue end caps, so the
  body the tint actually paints reads 6.9 deg at chroma 0.177.)

- [LW-239] 2026-08-14: Three pairs of items sit close enough in colour to be mistaken for each
  other in the same equip list, and two of them clear the look-alike tripwire by a hundredth.
  The tripwire only ever reads the three colour numbers, so it cannot know that a pair also
  shares one silhouette, which is when colour is the ONLY thing telling them apart. Pole 109
  against 113 is byte-identical in outline and 0.010 of hue from a red gate; 108 against 113 is
  inside the saturation floor by a float hair; rods 55 and 57 are the family's two green rods on
  a 99.5 percent identical outline. Found by the LW-206/LW-208 audit. (Tech: RACK_MIN_HUE_GAP
  0.05, dhue 0.060 on both pole pairs; mean-Lab dE 6.45 for 109/113 against 16.04 for the
  runner-up. Fix is a design call plus, separately, LW-240.)

- [LW-241] 2026-08-14: Nothing runs the check that proves an icon pass left every other item
  alone; a person has to remember. The build pipeline runs the recolor selftest and refuses a
  red one, but the compare gate is invoked by hand, which is how two holes in it survived three
  passes. It cannot simply be added to the pipeline as-is, because during a pass the family
  being worked moves on purpose, so it needs an expected-movers list that lives somewhere the
  build can read. (Tech: tools/pipeline.ps1 runs recolor_icons.py --selftest and throws on
  failure; grep finds no icon_preview call in any script or workflow.)

- [LW-174] 2026-08-12: Five story-battle monster jobs are invisible to Living Poach because the
  map skips their alias rows. Demoted from Now 2026-08-14 to make room for the icon re-pass
  program the owner is actively working; nothing about it changed and nothing is blocked on me.
  The build and the adversarial verify both finished on 2026-08-12 (six pinned tests written red
  first, the regenerated map byte identical on a re-run, verdict SHIP), and the only step left is
  a live premise beat that is OWNER-ONLY: alias jobs appear in exactly three battles in the whole
  game, all story battles a late save cannot reach, so settling it needs a staged encounter with
  one MainJob 169 panther injected into a reachable random battle through the moddable encounter
  table. Promote it again when that drill is on the table. (Tech: full detail in the entry this
  replaces, commit ac43327 and the rows around it; PoachMap.TryGetJob membership at
  LivingPoach.cs:108 is the mod's entire monster gate, so an unmapped alias job is a silent
  refusal.)

- [LW-210] 2026-08-13: Books re-pass: all four books BUILT and gated 2026-08-16, demoted from
  Now 2026-08-17 to make room for LW-233, which is being actively worked; nothing about the
  books changed and nothing is blocked on this repo, the owner gallery pass is the only step
  left and it waits on the same future session as the other art rows. State preserved: the
  healthiest family by coverage (CARD median 67.0 percent), one zone suffices (a book is a
  coloured cover and a pale page block the brightness key finds on all four), and the Omnilex,
  the game's most strongly coloured sprite at icon chroma 0.388 deep vermilion, keeps its
  vermilion cover with the holy in gilt-edged pages (the Holy Lance's resolution, fifth use).
  Promote it back when the owner gallery pass is on the table; the Done means and Verify are
  the standard art-row set (identity colour with a visibly separate second material, no two
  alike at list size, reserved-name anchoring recorded, four gates green with pins proven by
  mutation, bake matching the FULL preview manifest, owner gallery pass).

- [LW-234] 2026-08-14: The mod files the player's own guests as enemies, so a guest dying could
  hand the player's weapon a kill it did not earn, complete with a growth toast. Found in the
  same user's tape. Two player-side units (nameId 4 and nameId 7, band seats 8 and 9) were
  bridged Enemy fourteen times with rescue=OracleEnemy, meaning the enemy oracle had captured
  them. That they are on the player's side is visible in the tape rather than assumed: seat 8
  takes 18 damage at t=12782890 and never dies, while every unit that does die is one of the
  job 169 to 172 monsters. Nothing was mis-credited this session because neither guest died, so
  this is a live exposure and not an observed loss. What makes it sharp is that oracle
  membership is the ONLY team gate on kill credit, and the class doc for EnemyOracle states that
  guests are "structurally excluded", which this tape falsifies. (Tech:
  Downloads/flight_20260812_023631_battle-exit.jsonl; the gate is _oracle.Contains(slot.Id) in
  KillTracker.Corpses.cs. Either add a real side-membership read, which the Provoke arc proved
  must come from the band's own bit and never from seat position, or stop claiming exclusion in
  the doc and gate credit on something that actually holds. Rides naturally with LW-233 since
  both are corpse-credit correctness.)

- [LW-235] 2026-08-14: The mod has no way to learn which weapons players actually use, and the
  one time real user logs arrived they answered nothing. Owner question 2026-08-14: which
  weapons are used and which are ignored. The six submitted Reloaded console logs contain ZERO
  lines from this mod, across two different users and including two logs that run more than
  twenty minutes into play, while five other mods log to that console freely, so support
  requests built on those logs have never carried our evidence. The only weapon data that
  existed came from flight tapes one user happened to send, and it is a chapter 1 party holding
  the gear the game gives you (four Vagabonds, two Cutpurses, one Quicksilver), which is not a
  preference signal at all. Worth noting the mod already persists exactly the right data:
  kills.json in the save dir is a per-weapon tally across a whole playthrough. The question is
  whether an opt-in way exists for a player to send it, what it must not contain, and whether
  the console silence is itself a bug worth fixing first since it costs support time today.

- [LW-228] 2026-08-13: Harp signature ideas from the owner, banked for when the instruments
  arc opens. One harp grants Ramza's Tailwind so the wielder can hand a unit extra Speed. The
  blood-string harp keeps its long range HP steal, which is unique, and its awakened tier
  could cast Elmdore's Blood Suck, turning the bitten enemy into a vampire. A third harp
  takes the MP restoring ability the tree monsters use and widens it into an area effect.
  (Tech: the granted-command trio (Barrage, Shadow Blade, Provoke via JobCommand injection)
  is the proven candidate mechanism for the Tailwind grant; the Blood Suck vampire conversion
  and the AOE widening are both unverified and need their own live RE before any of this is
  promised in a design doc.)

- [LW-229] 2026-08-13: Dancers should be able to equip harps, owner call, banked alongside the
  LW-228 harp signature ideas. Vanilla locks instruments to Bards, so a Dancer today cannot
  touch any of the harp designs at all; opening the slot makes the harp arc serve both of the
  performance jobs instead of half of them. (Tech: which job may wear which equipment category
  is job table data, not item table data, and whether the modloader reaches that table has not
  been checked; find the equip category mask in the job data first, and if the modloader cannot
  write it this becomes a runtime write behind the guarded Mem layer like every other live
  mechanism.)

- [LW-214] 2026-08-13: Throwing weapons and Bombs first pass: the 6 shuriken and bomb icons were never tinted at all (they sit outside the 121-weapon set), so this is a first coloring, not a re-pass; process per LW-198.

- [LW-232] 2026-08-14: On a lot of weapon CARDS the identity colour was never on the weapon at
  all, it was on the glow around it, so those items have been shipping as vanilla art wearing a
  coloured haze. Found while extending the LW-230 halo ramp: the card engine splits a sprite
  into groups by brightness and paints the brightest one, which is right for a chunky item and
  wrong for line art, because on a thin staff or bow the brightest population IS the sprite's
  own semi-transparent haze. Measured over all 115 weapons: 104 of the card masks are more than
  half haze, 13 have ZERO solid pixels in the tint zone, and once the haze is left alone the
  median card keeps 57 percent of its identity colour with 37 items under 30 percent and 14
  under 10 percent. The list icons are fine (median 97 percent, worst 91) because they take a
  whole glyph ramp with no mask in it, so this is a card-only defect. SCOPED by the owner
  2026-08-14 after the review gallery: weapons keep their current look until each family's own
  queued re-pass comes up, and that pass is where the engine gets chosen from the art. Nothing
  bakes for weapons before then, so this row is a standing brief for those passes rather than a
  decision waiting to be made. The fix is already proven in this
  repo: it is exactly what LW-202 found on the crossbows ("bright-v2 splits a picture into two
  clusters and a crossbow is line art with no second cluster in it"), and the answer there was
  to route the family to the zone engine and pick its materials by saturation. Swords are the
  first family through this brief (LW-199, 2026-08-14) and they add the piece the crossbows
  could not show: which mask finds a family's second material is a property of the ART, not of
  the engine, and it flips between families. Saturation found the crossbow's stock; on a sword
  the same key lands on the blade and it is DARKNESS that finds the hilt, on 15 of 15 sprites.
  Measure all three keys on the real art before choosing, and prefer a bright second tone,
  since a dark one on a darkness-keyed mask is invisible by construction. The queued per
  family re-passes (LW-198 daggers, LW-199 swords, LW-201 bows, LW-206 poles, LW-207 polearms,
  LW-209 staves and the rest) are where that judgement belongs, one family and one owner gallery
  at a time. Two alternatives were offered and declined: restricting the card engine's
  clustering to solid pixels, which puts the tint on the object but re-renders all 115 cards
  (28.1 percent of solid pixels change zone, on 115 of 115 sprites) and would need one very
  large review round, or baking the honest de-tint now and letting each family's pass restore
  the colour later, which is the only option that looks worse before it looks better. (Tech: reproduce the whole
  measurement with `python tools/icon_preview.py compare`; the bake now prints a WARN naming any
  item whose tint reaches under 2 percent of its solid art, so this can never ship silently
  again.)

- [LW-165] 2026-08-12: Kill counts are slow to appear in the status menu after a cold boot on
  the Steam Deck; demoted from Now 2026-08-14 to make room for LW-230 and LW-231, which are
  being actively worked. Nothing about it changed and nothing is blocked on it: the owner
  ACCEPTED it for the 2.3.3 cut on 2026-08-12, the README carries the player note, and it has
  been waiting since then on a Deck cold boot that has not been run, so holding a Now seat for
  it is how the ledger starts lying about what is being worked (the same argument LW-100 and
  LW-112 were demoted under). State, preserved in full: the mod prints one plain line the first
  time the kill counters come alive each launch, saying how many card text spots it maintains
  and how many seconds after arming that happened, so the next Deck cold boot turns the
  complaint into a stopwatch reading. Desktop halves already measured 2026-08-12: two launches
  read 10.5s and 10.9s between arming and the first paint, and the owner reproduced the felt
  symptom on desktop (a party menu opened right after a save load shows the baked zero counts
  until the menu is re-entered). Promote it back the moment a Deck cold boot log exists; the
  likely tune is an unthrottled first pool locate at arming. (Tech: the Info line fires on the
  false to true edge of the pool coverage latch in Display.PoolPaint.cs, once per launch, timed
  from Display's own first Tick, which Engine only starts after the guard arms. The deeper lever
  is read only pre locating before arming, which touches the born disarmed principle and would
  need its own arc.)
- [LW-217] 2026-08-13: Hair Adornments re-pass: the 3 hair adornment icons still wear the legacy one-hue stamp; process per LW-198.

- [LW-218] 2026-08-13: Heavy Armor re-pass: the 14 armor icons still wear the legacy one-hue stamp; process per LW-198.

- [LW-219] 2026-08-13: Clothing re-pass: the 14 clothing icons still wear the legacy one-hue stamp; process per LW-198.

- [LW-220] 2026-08-13: Robes re-pass: the 8 robe icons still wear the legacy one-hue stamp; process per LW-198.

- [LW-221] 2026-08-13: Shoes re-pass: the 7 shoe icons still wear the legacy one-hue stamp; process per LW-198.

- [LW-222] 2026-08-13: Armguards re-pass: the 4 armguard icons still wear the legacy one-hue stamp; process per LW-198.

- [LW-223] 2026-08-13: Rings re-pass: the 6 ring icons still wear the legacy one-hue stamp; process per LW-198.

- [LW-224] 2026-08-13: Armlets re-pass: the 5 armlet icons still wear the legacy one-hue stamp; process per LW-198.

- [LW-225] 2026-08-13: Cloaks re-pass: the 7 cloak icons still wear the legacy one-hue stamp; process per LW-198.

- [LW-226] 2026-08-13: Perfumes re-pass: the 4 perfume icons still wear the legacy one-hue stamp; process per LW-198.

- [LW-196] 2026-08-13: The item icon shown when picking up a Move-Find Treasure appears NOT to
  carry the mod's recolor; it looks like the vanilla icon even for items whose menu icons are
  recolored. Owner sighting 2026-08-13 (softly held, "pretty sure"), logged with a concrete
  suspect already in hand: the vanilla icon tree has 26 icon FAMILIES and the recolor pipeline
  ships exactly two of them (equip_item, the 100px card art, and equip_item_s, the 48px list
  icon), so any UI surface drawing from a third family shows vanilla art. The treasure pickup
  popup is exactly such a surface, and a "treasure" family exists right beside the two we
  cover (Pac Files 0008 ui/ffto/icon/treasure, 90 files as t_NNN plus t_NNN_l pairs, so about
  45 ids, which is NOT one per item and means an id mapping question, not just a recolor
  question). First probe when picked up: pick up one recolored item via Move-Find, screenshot
  the popup, and match its art against equip_item versus treasure to learn which family that
  popup reads and how its t-ids map to item ids; then the fix is the settled recolor assembly
  line pointed at one more family, plus the same check for other uncovered surfaces (shop,
  battle spoils, Poachers Den) which may each read yet another family.

- [LW-195] 2026-08-13: Equip a weapon in the OFF hand and a shield in the MAIN hand and the
  battle menu's Attack row reads as bare fists, as if the unit were unarmed. Owner hit it live
  2026-08-13. First question when picked up, before any fix: is the fists text the GAME's own
  behaviour for that hand arrangement or this mod's paint? One battle with the mod disabled (or
  an untracked vanilla weapon in the same arrangement) settles it. If it is ours, the likely
  lane is that everything in the runtime treats the roster MAIN hand slot as "the weapon":
  the attack-row painter resolves the acting unit's weapon from that slot, so a shield there
  reads as no weapon even though a real weapon sits in the off hand. If that read is right, the
  same blind spot silently breaks more than text: kill credit and every signature also key on
  the main hand, so an off-hand-weapon build would earn no kills, no growth, and no granted
  commands, making this a coverage hole for an entire legal equipment arrangement, with the
  fists text just its visible corner. (Tech: unverified beyond the sighting; the main-hand
  reads to audit are the RRHand roster reads in Wielder/ActorResolver/Display and the
  mainHandWeapon field the kobu/credit log lines already print. The flight tape from the
  sighting battle, if one flushed, shows what the credit lane resolved for that unit.)

- [LW-192] 2026-08-13: Signature idea from Patrick, "Scholar": the weapon teaches its wielder any
  spell an enemy casts on them, so getting hit with something new is how you learn it. A book or
  rod fits the fantasy. The appeal is that it turns being targeted into progress, which no current
  signature does, and it rewards the player for walking into a caster's range instead of avoiding
  it. Design questions before any build, none answered yet: does it learn only spells the
  wielder's CURRENT job could legally know, or bank them for whenever that job is worn; does it
  fire on a resisted or missed cast; is it capped per battle so a long fight does not hand over a
  whole spell list; and does it announce itself, since a silent learn is the invisible-feedback
  failure the signature audit already flagged on Chain Lightning and Font. (Tech: the WRITE half
  is likely cheap and already proven in tree, since Barrage and Shadow Blade both set the roster
  learned flag for an ability slot, see HoldLearnedBit in Barrage.cs and ShadowBlade.cs. The
  unproven half is the READ: learning which ability was just cast AT the wielder. The mod
  currently infers actions from turn flags and damage events, not from an ability id on an
  incoming cast, so this needs its own probe to find where the engine names the ability being
  resolved against a target. Check whether the game already ships a vanilla learn on being hit
  support ability before inventing a mechanism, since poaching a working one is the pattern that
  made Living Poach and the granted commands cheap.)

- [LW-191] 2026-08-13: Equipping a living weapon in the middle of a battle can take its granted
  command AWAY instead of giving it, and the mod then says there is no eligible wielder while that
  wielder is standing on the field. Seen live 2026-08-13: Patrick stole the Sanguine Sword from
  Gaffgarion and used Re-equip during the same battle. Shadow Blade had been granted to Ramza at
  11:51:49 (party slot 0, job 2, record 26, the Squire story variant) and was released at 11:55:45
  with the line "no eligible Sanguine Sword wielder remains", so the command disappeared from the
  menu mid fight. One second after that release the battle side event lines still read weapon 23,
  meaning the battle still held the sword while the roster scan no longer found it. (Tech:
  ShadowBlade.cs walks RosterBase and requires the roster right hand to read id 23; Barrage and
  Provoke are the same roster keyed shape, so all three granted commands share the exposure. This
  is LW-103's roster versus band disagreement reaching a surface where it costs the player an
  ability instead of being harmless. Cheapest first question, one probe: does a mid battle Re-equip
  write the roster row at all, or only the band? If only the band, the grant lane wants a band
  fallback keyed the way kill credit already bridges a battle actor to its roster slot. Second,
  independent of the fix: the release message should not assert there is no eligible wielder when
  the truth is that the mod lost sight of the weapon.)

- [LW-112] 2026-07-21: Stop blaming a game update when another mod rewrote the same game data;
  demoted from Now 2026-08-13 to make room for LW-190, which is being actively worked.
  Nothing about it changed and nothing is blocked on this repo: it has been AWAITING-LIVE since
  2026-07-28 on a two leg owner drill that has not been run in over two weeks, and holding a Now
  seat for a row nobody is moving is how the ledger starts lying about what is being worked (the
  same argument LW-100 was demoted under). The build, the tests and the adversarial verify all
  passed at the time; only the live drill is owed. Full done means, the unexplained player residue,
  and the drill's pre registered failure signatures are preserved verbatim in the CHANGELOG entry
  this row will cite when it exits, and the drill recipe itself lives in docs/DEV_TEST_RECIPES.md
  (LW-112 section). Promote it back the moment the owner is ready to run the two legs.

- [LW-187] 2026-08-13: The build pipeline's test gate never exercises the dev-only code paths,
  so a broken dev-only phase row (or any future dev-only logic) would sail through the gate
  that guards deploys and only fail in a hand-typed dev test run. Surfaced by the LW-186
  adversarial verify: tools/pipeline.ps1 runs plain dotnet test with no dev define, while the
  five dev spike rows in the engine's phase table only compile under it. Blocker to fixing it
  cheaply: 77 tests fail under the dev define today because their tier-threshold expectations
  are written against the production numbers {5,10,15}, not the dev numbers {1,2,3}, so a
  dev-defined gate leg needs those expectations parameterized by flavor first.

- [LW-185] 2026-08-13: Thin the codebase's comments to the owner's rule: a comment must say
  something the code cannot, capped at roughly two lines per reason, with the long history
  moved to the right durable doc and cited by its row slug. Runs AFTER LW-183 gives ledger
  rows their greppable slugs (the citations need somewhere precise to point) and after
  LW-184 rewrites the engine tick (no point thinning comments that rewrite deletes). The
  deletion test keeps the fence case: a line whose absence would invite a wrong refactor is
  a keeper even though it repeats no fact.

- [LW-184] 2026-08-13: Rewrite Engine.Tick's hand-written recipe as a declarative phase
  table (owner directed). BUILT AND COMMITTED 27eb98f, owner regression watch OWED before
  this row exits: each subsystem tick is now a data row with a name, a named gate, a
  cadence, and an "after" annotation naming its ordering reason, so the tick order is a
  value tests inspect instead of physical line positions. Tests pin the exact table, every
  gate and cadence, and every ordering reason, each sabotage-proven non-vacuous; a regime
  test proves the in-battle rows run in battle and stay silent outside it. The prologue and
  both battle-edge transactions stayed sequential (bodies moved verbatim, mechanically
  diffed), the kit-lane trio's slot-claim order and the locked second PauseFlag read are
  preserved, and the independent verify rated it SHIP 9/10 on suite 3099. Exit gate, owner
  only: the seven-step DEV live regression watch (guard arms; twin equips out of battle;
  battle enter line; kill plus toast; growth on the status card; exit summary plus flight
  flush; post-battle equip-card paint), one battle, all signatures read from the log.

- [LW-181] 2026-08-12: Redesign Bulwark as "Upheaval" (pivoted 2026-08-13, supersedes the
  earlier enemy-turns-only toggle sketch): instead of a wait stance that quietly bars the
  tile behind the knight, the player aims Bulwark at the ground like Provoke's sibling,
  anyone standing in the zone is visibly thrown clear, and the vacated ground then refuses
  entry. The wall stays always-on and side-blind (vanilla tree semantics), so the game's
  own red no-entry cursor and move previews keep telling the truth and the old turn-toggle
  premise beat is not needed. Built the Living Poach way: recreate the game's knockback
  from pieces already proven one by one (flinch-with-displacement animation page plus the
  Proven teleport triple-write), delivered through the granted-command lane (the Provoke
  hijack recipe), whose donor resolution also supplies the flash and sound poach had to
  skip. Owner-directed due diligence before any build: one last shove probe, the
  acceptance run of knockback_probe.py shove, judged poach-style for convincingness, and
  it must settle four unknowns: which flinch page (0x37/0x38) maps to which push
  direction, whether those page ids transfer to a second sprite class (the anim catalog is
  time mage male only, LW-114), whether the post-flinch freeze needs an idle re-request,
  and how the stale-Z float transient reads on a height-changing shove. Stretch probe if
  the imitation reads cheap: the global differential diff during a real engine move
  hunting the mover's trigger (the LW-58 one-byte enrollment shape); from rest the mover's
  block is settled dead across three rounds, in flight the destination rewrite is honored.
  The other unproven premise is tile-cast detection (how the mod learns which tile the
  player aimed at; Provoke's mark rides the target unit, an empty tile has none), which
  needs its own probe. The two comms fixes from the original capture (the silent plant
  refusal and the "full wait" card wording) stay owed only if this redesign walls and the
  shipped full-wait Bulwark survives; until the arc runs, the shipped Bulwark stays as-is.
  SHOVE ACCEPTANCE RUN 2026-08-13, owner live, three of the four unknowns settled GREEN in
  one battle (the fourth still owed): the victim was a Red Panther, so the second sprite
  class question answered itself, both pages 0x37 and 0x38 played as a plausible flinch on
  a monster class ("works like a charm"); the direction map question resolved as MOOT, the
  flinch is too brief to read a direction at game speed so page choice is not load bearing;
  the freeze tail self-heals exactly as documented, the pose locked after each shove and
  the victim's own turn-open re-stamp fixed it, the panther then took a normal turn with NO
  rubberbanding, meaning the engine fully adopted the shoved tile as the real position.
  Still owed before the build arc: one shove onto a height-changing tile (the stale-Z float
  transient read) and the tile-cast detection probe, which remains the one wall risk.

- [LW-180] 2026-08-12: The kill-credit census warned twice in one battle that a fielded enemy
  vanished from the battle band ("its kills may go uncredited"), both times in an undead
  fight, and nobody knows which seat or why; the one kill actually taken that battle
  credited fine, so this is a caution light, not an observed loss. Suspect: undead final
  death or dissolution removing or hiding the seat, which would make this a false-alarm
  class; a real coverage hole if not. Evidence banked from the 2026-08-12 Provoke
  regression session: tapes flight_20260812_211943_battle-exit.jsonl (first WARN 21:20:42)
  and flight_20260812_213229_battle-exit.jsonl (second WARN 21:32:13; the flush census
  still shows the killed undead 851 seated at s8, so corpses staying seated is not the
  vanish). Fold the verdict into LW-76's healthy-session WARN triage if it proves benign.

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
  OWNER SIGHTING 2026-08-17, the player-visible cost is now on record: during the LW-233 retry
  drill the mod announced "Stoneshooter claims kill number 25, felling a human" twice over a
  chocobo, once at 18:08:16 and once at 18:09:07 after the bird was raised and killed again.
  The victim probe read job 94 both times (nameId 861, battle slot 12), and job 94 sits inside
  the generic human band because Puppeteer.Policy.cs:25 sets GenericJobHi to 95 while
  Puppeteer.Policy.cs:31 sets MonsterJobLo to 96, so every chocobo in the game is announced as a
  person. The toast is the harmless half; VictimClass feeds Reliquary victim classing, so fix the
  boundary rather than the wording.
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
  Owner observed live 2026-08-12 on enemies and on Agrias. The items were the mod's own innate
  Float pieces: Sunsteel Helm (id 149, vanilla Golden Helm) and Empyrean Robe (id 206, vanilla
  Luminous Robe, EquipBonus row 46); enemies picked them up through level lists, so the state
  was common in late fights. UPDATE 2026-08-13 (LW-188): both named pieces lost Float in the
  cutback, so they no longer reproduce this; the remaining repro gear is the vanilla Float pair
  (Featherfoot Boots row 46, Envoutement row 66) plus the Cursed Ring unique (row 56), making
  exposure rare instead of common. The render question itself is unchanged and this row stays
  open on it. Cosmetic only so far; nobody has verified whether the gameplay half
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
  CONFIRMED SIGNATURE 2026-08-12, one instance, from the Provoke regression night: the owner's
  deliberate fast-forward hold produced the exact predicted shape, a markedEta=0 arm at
  21:24:14 with no turn ever observed and the watchdog WARN release at 21:25:44 (tape
  flight_20260812_213229_battle-exit.jsonl); the same enemy then accepted a re-cast without
  fast-forward and released cleanly on EnemyTurnDone. Honesty rider: the markedEta=0 arm means
  a cast landing while the enemy's turn was already open (the fresh-rise refusal) cannot be
  fully excluded as the cause of this one instance, so the confirmation is strong but not
  airtight. Still parked for the 2.3.3 release at the owner's original direction; the CT
  payment fix direction stands.

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

- [LW-248] 2026-08-16: The mod's shipping look is DECIDED for colour and OPEN for glow: every
  item gets the new recolour, and no item gets a glow rim unless a later decision adds one.
  The owner settled the colour half on 2026-08-16 after seeing the whole catalogue in game;
  the glow half stays open because it is a separate layer that can be added or removed later
  without touching a single body pixel, which was proven repeatedly the same day by re-rimming
  the sixteen shields four times in seconds. Remaining colour work is hats, armour and
  accessories, on the same set by set rhythm the weapons and shields used. Anything that
  depends on the glow decision waits for the owner, and nothing about that decision blocks the
  colour pass from shipping. (Tech: the four glow candidates and their prices are laid out in
  the decision brief artifact f9835932; the deployed install currently DOES carry glow on
  shields, helms, weapons and bags, so shipping colour-only means a final de-glow bake, not a
  revert of anything the owner approved.)

- [LW-249] 2026-08-16: Nine poles grow the wrong stat, so a mage who levels a Whale Whisker
  gets thirty percent more muscle and not one point of extra spell power. Their damage runs on
  Magick Attack, but the growth code only recognises rods and staves as caster gear, so every
  pole climbs Physical Attack instead, which does nothing for the weapon the player is holding.
  Three guns have the same shape of problem from the other direction: their damage ignores
  stats entirely, so their growth cannot help them at all and is silently wasted. Confirm the
  poles really are magick scaled in the shipped data before changing anything, because the
  claim comes from the design grid rather than from a live reading. (Tech: Tuning.IsCaster
  tests category Rod or Staff only; poles carry category Pole and route to the PA lane in
  GrowthEngine.cs's stat picker. Affected ids 48, 107 through 114. The stat-independent guns
  are 71, 72 and 73, formula 3, WP times WP.)

- [LW-250] 2026-08-16: An idea worth keeping: let each weapon grow the stat its own damage
  actually uses, which fixes several families being under rewarded and, as a side effect, makes
  a colour coded glow possible later. Today seventy eight percent of weapons grow Physical
  Attack, so growth feels the same on nearly everything and a colour coded rim would be almost
  entirely one colour. Tying growth to the weapon's own formula gives knives speed, katanas and
  knight swords courage, books and instruments and veils both attack stats, and leaves the
  stat-independent guns honestly ungrown. Books, instruments and veils are the clearest fix:
  their damage averages the two attack stats, so growing only one of them hands them half the
  reward a sword gets for the same kills. NOT DECIDED, and deliberately parked until the owner
  is ready. (Tech: proposed lanes by damage formula, with counts, are in the
  growth-stat-recolour-design memory. Brave is capped at 97 so high-Brave wielders gain less
  than the full thirty percent, and the Brave write mechanism is already proven in production by
  Kobu, holding the CURRENT copy at combat +0x2B. Guns are held back because WP lives in the
  item table, so growing it would buff every copy in the game including enemy-held ones, and a
  per-wielder hold would need its own probe.)

- [LW-251] 2026-08-16: The weapon a unit swings in battle still wears its vanilla colours, so a
  recoloured icon and the sprite on the field disagree. The art is a 2D texture inside an
  eleven megabyte container of roughly fourteen hundred images, and the two easy colour levers
  were already tested in game and both failed, so the work is to find the right image in that
  container and repaint it with the same engine the icons use. Two limits to plan around before
  promising players anything: that channel is cached for the whole session, so battle art can
  never update live the way icons now can, and the art is likely shared per weapon class, which
  would mean every sword recolours together rather than one at a time. (Tech: the container is
  FFTIVC/data/enhanced/system/ffto/g2d.dat, magic YOX, header suggests 0x592 entries. Find the
  weapon sheet by decoding entries or by shipping garish overrides and bisecting. The modloader
  serves this channel from a per-index cache read once per process.)


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
