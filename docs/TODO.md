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

- **[LW-100] A restarted battle can keep a leftover Speed boost while the rider starts on foot** (opened 2026-07-21) [BLOCKED(premise unverified, may be a phantom)]
  - Done means: a rider who restarts a battle and opens it dismounted no longer carries the
    previous run's leftover mounted Speed until they climb back on a chocobo; the mod
    recognises its own leftover boost even when the signature that wrote it is not currently
    active. (Tech: HoldTimedStat's LW-90 correction fires only at an ACTIVE capture, so a
    dismounted battle 2 open skips capture entirely; candidate is an inactive first sight
    NaturalLedger consult for mount gated signatures. Rides with it: a clean remount capture
    currently drops the post revert corrective sentinel for the rest of the battle.)
  - Verify: FIRST, prove the bug is real at all, because it may not be. Owner live pass on a dev
    build: get a Holy Lance (id 104, main hand, tier 3) wielder mounted mid battle so the card
    climbs 11 to 14, restart the battle while still mounted, and read Speed on the card at the
    restarted open while on foot. Reads natural, the bug does not exist and this exits RETRACTED.
    Reads natural plus 3, the bug is real and the same reading settles the ledger question below.
    ONLY THEN: unit tests pin the dismounted restart open (a recorded leftover target is refused
    as a natural even with no active hold) and the remount sentinel. (Tech: the whole ticket rests
    on a battle restart carrying a MID battle boost, and the only evidence a restart carries
    anything comes from the Iai opening hold, which is armed inside the game's battle open
    snapshot and therefore cannot tell "restart replays the polluted snapshot" apart from "restart
    keeps the live byte". A mount happens long after that snapshot, so under the replay reading
    there is no residue to correct and the code hole is unreachable. The code hole itself is
    confirmed: GrowthEngine.TimedStat.cs:63 gates the only FilterCapture call on active first, so
    a dismounted open misses all three arms. Confirmed code is not a confirmed bug.)

## Backlog

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
