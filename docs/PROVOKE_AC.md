# Provoke (Defender +3) -- scope + acceptance criteria

STATUS: CONTRACT (Provoke acceptance criteria)

Approved 2026-07-22. Design source: `docs/living_weapon_grid.csv` row 34 (Defender, id 33).
Post-release feature: it does not gate 2.3.1 (`docs/RELEASE_SCOPE.md`, locked "no new features").
Work ledger id: LW-123.

**SHIPPED STATUS (arc 2a, live 2026-07-22).** The FUNNEL PREMISE IS PROVEN (owner-verified through
the mod: hiding every player unit except the bearer makes the enemy AI target the bearer;
docs/LIVE_LEDGER.md Proven row). **SUPERSEDED 2026-07-27 by LW-127, and the history matters because
this document spent five days telling its reader the opposite.** The runtime used to hide for the
WHOLE enemy phase (called WINDOW, `Tuning.ProvokeSliceMode = false`), which meant every enemy that
acted while the hold was up walked past the party to hit the bearer. The clean single-enemy
alternative (called SLICE) was tried live on 2026-07-22 and LOST THE TURN-START RACE: the AI commits
its target the instant its turn opens, so a hide that reacts to the turn starting lands too late.
Both names are now retired, along with the `ProvokeSliceMode` constant, because the thing that made
the choice unnecessary arrived: the runtime can now tell WHO ACTS NEXT (LW-118, measured live
2026-07-27, ledger rows pending the owner's flip), so the hide goes up in the run-up to the marked
enemy's own turn instead of blanketing the phase. The shipped rule is one five-branch decision
(`ProvokeHold.Policy.ActionFor`) in which HIDE IS THE DEFAULT and a reveal must be earned. Wherever
the text below still says SLICE or WINDOW, it is describing that retired pair. Polish A (2026-07-22)
moved the release detection onto the proven actor pointer, and that half is unchanged. Arc 2b (the data
plumbing that arms the real granted command) SHIPPED 2026-07-22 (commit 3565363): id 33 carries the
signature, so a grown Defender grants a working Provoke command. The job-global leak that arming
exposes (criterion 0e) is RESOLVED met-by-observation 2026-07-23: the enemy AI does not cast a
zero-value command, so no usable-by-AI clear ships. See 0e.

## One line

The Defender lets its bearer shout down one enemy: point at any foe, and until that foe has taken
its turn, that foe attacks the bearer instead of your other units, whom the bearer shields by
carrying the game's best parry to survive what it just invited.

## What the player does

1. Equip a Defender that has earned its third tier. Its bearer gains a command called **Provoke**.
2. Use it and pick any enemy on the field. That is the bearer's action for the turn.
3. From that moment until the provoked enemy finishes its next turn, THAT enemy attacks the bearer
   instead of your other units. Other enemies acting in between behave normally.
4. It ends. Everyone fights normally again.

Normally one enemy besides the provoked one is affected: whoever acts IMMEDIATELY before it.
The runtime has to have the party hidden before the provoked foe's turn opens, because the AI picks
its target the instant a turn starts, and the only place to raise the hide is during the turn just
ahead. So that neighbour is redirected too. Everyone earlier in the order fights normally. It can be
more than one neighbour when several enemies are bunched within the reveal margin
(`Tuning.ProvokeRevealMarginTicks`, 2 ticks of charge time), since the party stays hidden across
that whole bunch; expect one, do not score two as a failure, and score "all of them" as one.
Criterion 19 records this as the accepted cost and what it would take to remove it. UNTIL 2026-07-27
this paragraph said the opposite, that EVERY enemy acting during the hold was redirected, which was
true of the mode that shipped then and is a FAILURE signature now.

## How it works, plainly

Two halves: a mark, and a hold.

**The mark says who was goaded.** Provoke hangs a status on the unit you point at. That status is
one the game never had: **StatusEffectData id 0**, band `+0x45` bit `0x80`, named through
UIStatusEffect Key 1. Id 0 is the single blank slot in the whole forty status decode table, which
is why it was free to take and why it took three candidates to find (it is absent from this repo's
own status map). It carries no behaviour, no pose, `CheckFlags: 0` and `Counter: 0`, so the target
keeps walking normally and nothing in the engine acts on it. The mark is simultaneously the fiction
the player reads, the receipt the runtime detects, and the record of which enemy was chosen.

Two properties of the mark are load-bearing and neither is optional engineering taste. It NEVER
EXPIRES, so nothing in the engine will tidy up after it; and it CANNOT BE RE-APPLIED while present,
so a recast on an already provoked unit reads 0%. Together those mean the runtime clearing the mark
is not hygiene, it is what makes the ability usable more than once on the same enemy in a battle.

**The hold decides who they can see.** While the mark is up, the runtime flags every other unit on
your side with the engine's own "cannot be seen" status, leaving the bearer as the only name on the
acting enemy's target list. The flags go up in the RUN-UP to the provoked enemy's turn, not across
the whole enemy phase: the runtime reads who acts next from each enemy's charge time and speed
(`TurnOrder.cs`), raises the hide once the provoked enemy is the next enemy due, and holds it through
that enemy's turn. Timing is why the run-up is mandatory rather than tidy: the AI commits its target
the instant its turn opens, so a hide that waits for the provoked enemy to start acting is always too
late. This half is subtractive because it has to be: there is no aggro in this game. A PSX dig closed
that question, enemy targeting is computed tile scoring with no holdable focus field, so the only
lever on who the AI attacks is whether a unit can be targeted at all. (Before 2026-07-27 the flags
did stay up for the whole enemy phase, which is what pulled every acting enemy onto the bearer.)

REJECTED, and recorded so the reasoning is not relitigated. **Berserk** was the first choice and is
the better fiction (the engine genuinely enrages the unit for free), but 25 of 173 jobs resist it,
including the entire boss tier, and because the mark was also the receipt an immune target meant the
hold never fired either. **`Wall`** (band `+0x48` bit `0x01`) was the second choice and this document
specified it until 2026-07-22. It is a TRAP and the most expensive lesson of the arc: it looks like
the perfect inert marker, landing at 100% with no effect, no pose, a clean icon and blank text, but
it also carries `IgnoreAttacks`, the flag KO and Crystal carry, so attacks against a unit wearing it
read 0%. A marker that makes its bearer unkillable is worse than no marker. It may yet be exactly
right for the HOLD, where untargetability with no text to leak is the goal, but never as a mark. **The system statuses** (`Evading`, `Performing`, `Critical`) look like free
labels because their text is placeholder or marked for deletion, but each one drives a real pose:
they are internal engine states, not spare flags. **Renaming a real status** such as Slow is a trap:
status text is global to the status, so every ordinary Slow in the game would read "Provoke".

## The mechanism this rests on (premise ledger)

| Fact | Where | Status |
|---|---|---|
| Holding the composed Invisible bit on every player-team unit except one funnels enemy AI onto that one | `docs/LIVE_LEDGER.md` Proven, 2026-07-22 | PROVEN |
| A raw composed status write is an orphan flag: the AI reads it, no effect is performed, and it NEVER expires | `docs/LIVE_LEDGER.md` Uncertain, 2026-07-22 | LIVE-OBSERVED, owner PROVEN flip PENDING |
| The Invisible bit survives the unit acting; it is cleared by BEING HIT | `LivingWeapon/Offsets.cs` AInvisible block | corrected 2026-07-22 |
| Turn-owner team field is reliable for turn-level gating | `docs/LIVE_LEDGER.md` Proven, 2026-06-16 | CONTRADICTED IN PLAY 2026-07-27, ledger row untouched (owner-only). The 2026-06-16 row's own experiment never hovered a unit on a different team than the turn owner, which is the only arrangement separating "tracks the turn" from "tracks the cursor" (LW-131). The Provoke pass then produced that arrangement by accident: across a whole enemy turn the field read a sane PLAYER value, which is what a cursor-tracking field does when the AI targets a player unit, and is not something a turn-owner field can do. ProvokeHold now reads this field NOWHERE, in either of its gates (LW-131 release, LW-135 hide). Other consumers still do, notably KillTracker's death-edge bury (LW-137). |
| Per-unit turn/moved/acted flags | `docs/LIVE_LEDGER.md` Proven, 2026-07-09 | PROVEN |
| Command grant via JobCommand inject | `docs/LIVE_LEDGER.md` Proven, 2026-06-10 and 2026-06-14 | PROVEN |
| A cut ability can be renamed, granted, and re-effected: Provoke exists in the game | `docs/LIVE_LEDGER.md` Uncertain, 2026-07-22 | LIVE-OBSERVED end to end, owner PROVEN flip PENDING |
| Berserk is behavioural from the flag alone (the exception among statuses) | `docs/LIVE_LEDGER.md` Uncertain, 2026-07-22 | LIVE-OBSERVED |
| Clearing the COMPOSED bit releases the engine's already-has-it refusal; clearing the inflicted registry alone does not | `docs/LIVE_LEDGER.md` Uncertain, 2026-07-22 | LIVE-OBSERVED, owner PROVEN flip PENDING |
| The in-process runtime can write the ability action and inflict tables through the ordinary guarded path | `docs/LIVE_LEDGER.md` Uncertain, 2026-07-22 | LIVE-OBSERVED |

Addresses: Invisible is band `+0x47` bit `0x10` (`Offsets.AInvisible` / `AInvisibleBit`), the byte
Feign Death already writes. Friend/foe is band `+0x1D2` bit `0x10`, allies 0, guest-complete, READ
ONLY. Out-of-play seats read combat `+0x01` == `0xFF`. Turn team is `TurnQueue + 0x02`
(`Offsets.TqTeam`): 0 player, 1 enemy, 2 ally.

Two consequences of the orphan-flag row are load-bearing and are not optional engineering taste.
First, the flag does not decay, so nothing in the engine will ever tidy up after a hold we fail to
release: the fail-safe has to be ours. Second, being hit strips the flag, so a hold has to be
re-stamped, for splash damage rather than for the unit acting.

## Scope

**This arc (the hold engine).** Everything that decides who is flagged and when, plus its release
and its fail-safes. It was scoped with the trigger stubbed behind a seam; the trigger then landed
the same session (below), and both halves ship in process today.

**The trigger: SOLVED LIVE 2026-07-22, ahead of schedule.** It is a real granted command, built from
four levers that were each proven the same session (see the LIVE_LEDGER row). The shape:

- **Host ability: id 189**, vanilla `Embrace`, referenced by no job, no monster skillset, no innate
  slot and no weapon proc. Range 5, single target, 0 MP, 0 CT, formula 56, and it lands at 100%.
- **Renamed** to Provoke through `tools/patch_ability_names.py`, which rebuilds from the pristine
  vanilla decode and refuses to deploy unless exactly the intended cells differ.
- **Granted** through the shipped JobCommand injection that already ships Barrage and Shadow Blade.
- **Re-effected** by repointing ONE byte: the action row's InflictStatus index at `+15`, pointed at
  a HAND-WRITTEN row in the inflict-status table that applies the mark alone. That byte lives in the
  LIVE action table at `0x14078B2DC` (so the byte is `0x14078C1AF`), NOT the decoy copy at
  `0x14078961C`, which accepts writes, reads them back perfectly and is ignored by the engine. The
  inflict row is ours to author: unused index 29 at `0x14080FC4E`, written `80 80 00 00 00 00`, which
  is mode `0x80` AllOrNothing FIRST and then s0 bit `0x80` for status id 0.
- **Named** by writing the blank row in `nxd/uistatuseffect.en.nxd`, whose `Key` is the status bit
  index plus one (so id 0 = Key 1).

Because the ability marks its own target, the runtime needs no cast-detection hook and no
action-record read: it polls for an enemy wearing the mark, which is a read it already does.

## Non-goals

- NO team-swap. Flipping allies to the enemy team funnels perfectly and empties the player team, so
  the bearer dying is an instant game over and no cheap guard makes that impossible. Rejected, and
  the friend/foe byte is never written for any reason.
- NO write to the inflicted status layer (`+0x1D3..+0x1D7`) FOR THE HOLD. A durable registry write
  would make a stuck hold permanent and survive our own cleanup, which is the whole reason the hold
  uses a composed-only bit. SCOPED EXCEPTION, owner decision 2026-07-22: CLEARING the mark off a
  provoked enemy may touch that layer, because clearing residue is the opposite operation from
  planting a durable flag. The exception is for clearing only, for the mark's bit only, and it is
  always a mask-scoped read-modify-write.
- Reveal-timing: the reactive approach (hide at the provoked enemy's turn-start) was written as the
  default, MEASURED live on 2026-07-22, and lost: the AI commits the instant the turn opens, so a
  hide that reacts to the turn starting lands too late. That non-goal stands, and it is why the
  shipped rule hides during the RUN-UP instead of reacting. What changed on 2026-07-27 is that the
  run-up became knowable (LW-118), so per-enemy behaviour is in v1 after all, and criterion 4 is
  written against what actually ships rather than against a deferred goal.
- NO stat change to the Defender's static profile. Its numbers stay as `data/items.json` id 33 has
  them. CORRECTED 2026-07-22: this document previously asserted the item is already gate-exempt as a
  living weapon, and it is not. Only id 32 carries the `livingWeapon` flag, and id 33 passes
  `analyze.py` on its own numbers. Its SIGNATURE BLOCK is a separate thing and it is present again
  (removed for 2.3.2 by LW-133, restored by 43de63e), which is what makes the command grantable;
  adding it was what flipped two gates red at the time (the `docs/living_weapon_grid.csv` "+3
  ability" cell ran 134 characters against a 90 cap, and the assembled description 234 against a 205
  budget), so both were shortened in the same commit and both gates are green with the block in.

## Acceptance criteria

**The command itself**

0a. Provoke appears as a real entry in the bearer's command list, named Provoke, with its own
   description and icon, and is selectable rather than greyed. It appears in Squire's or Knight's
   action set only, which is the shipped grant's job whitelist (shared with Shadow Blade); see
   criterion 1.
0b. It targets a single enemy at range 5, costs no MP, has no charge time, and lands at 100% on a
   non-immune target.
0c. It applies the id 0 mark and nothing else: no damage, no pose, no engine state. The action
   row's InflictStatus index points at our authored row 29 in the LIVE table (`0x14078B2DC`); the
   decoy copy at `0x14078961C` is never written, by anything, ever.
0d. There is no immunity gap on the boss tier. MET for the mark that actually ships, owner live
   2026-07-22: Provoke cast at Loffrey (Divine Knight, Lv 54, flagged Objective and Enemy) read
   100% on the cursor, and a band read taken straight afterwards showed him wearing status id 0 in
   BOTH status layers. This REPLACES the earlier evidence, which was gathered with `Wall` before
   the mark moved and therefore described a different status; that substitution is the reason this
   criterion was wrong for a day, and it is why the re-run happened. REMAINING SCOPE, stated so it
   is not overclaimed later: one boss on one job is enough for this criterion, because the boss
   tier was the entire worry, and it is NOT enough to assert the engine's immunity system could
   never carry this bit. So the item card needs no exception, and no prose here may go further.
   The supporting ledger row stays Uncertain until the owner flips it.
0e. The command is job-global: units of the same job, INCLUDING ENEMIES, inherit it. RESOLVED
   met-by-observation, owner live 2026-07-23: an enemy Knight carrying Provoke as a LEARNED command,
   with its usable-by-AI bit SET, never cast it across many turns of a real battle. Provoke applies a
   zero-value inert mark (0 damage, no effect the AI scores), and the enemy AI picks abilities by
   utility, so a do-nothing ability scores about zero and is never chosen. The leak is that enemies
   HOLD the command, not that they USE it, and holding an unused command has no player-visible effect.
   The worst case even if it ever fired is harmless: an enemy casting Provoke on a player unit only
   hangs the inert mark on that unit, and the hold engine reacts solely to an ENEMY wearing the mark,
   so nothing triggers. The mapped suppressor (clear the usable-by-AI bit: COMMON-data byte +7 mask
   0x80 at 0x14078856F, confirmed a real flag field, 401 of 512 rows set, single copy, no decoy twin,
   via `tools/probes/ability_grant_probe.py aiflag`) is therefore NOT shipped: it is a per-battle
   write whose behavioural effect cannot be demonstrated, because there is no baseline where the AI
   casts Provoke for the clear to suppress, and an unproven write to the ability tables is the exact
   surface that has bitten this repo before. CAVEAT kept honest: this is one enemy on one job, not
   exhaustive across boss AI; and if Provoke is ever given a real AI-attractive effect the leak could
   wake, at which point the mapped clear is ready to wire. Supporting ledger row: docs/LIVE_LEDGER.md
   dated 2026-07-23, owner PROVEN flip pending.
0f. The ability's own description says what the ability does. MET since commit 3565363:
   `tools/patch_ability_names.py` Key 189 ships "Threaten a distant foe. Until it takes its turn,
   enemy attacks are drawn onto the bearer, not your allies." The provenance comment above that row
   was corrected in the same pass and now describes the shipped repoint rather than the abandoned
   Berserk one. This criterion previously alleged the old Berserk prose ("blind rage", "forgets its
   skills") was still shipping, which was true only until that commit; the wrong text described a
   rejected design (index 53) where the enemy was enraged, whereas the shipped redirect comes from
   hiding the bearer's allies and the provoked enemy keeps its whole skill set. Any future change
   here means editing that script, rebaking `ability.en.nxd` and re-running
   `tools/audit_nxd_bakes.py`, so it is one text change, not two.

**Arming and duration**

1. Provoke is offered only when the bearer holds a Defender (id 33) in its MAIN hand at kill tier 3
   or above, is deployed, and is alive. It is additionally job-gated by the shipped grant: the
   command is injected only into Squire or Knight, whether as the bearer's primary job or as one of
   their action sets taken as its secondary command (the shared whitelist ShadowBlade already uses).
   Any other job is logged as ungrantable and gets nothing.
1b. The hold lasts `Tuning.ProvokeTurns` of the provoked enemy's turns. Ships at 1 for the first
   live pass, target 3.
2. The hold arms on the cast, during the bearer's own turn, and is up continuously from that instant
   until it releases. There is no timing window to hit. The cast's OWN resolution never counts as
   the marked enemy's turn: the engine's actor pointer parks on whatever a player action targeted,
   and Provoke targets the enemy it marks, so the park visible at arm time belongs to the bearer's
   action. Only a park that RISES after the hold armed can complete a turn (LW-138, live 2026-07-27,
   where the old rule released 28 seconds before the enemy moved). The cost is deliberate and
   narrow: if a mark somehow lands while that enemy is already mid-turn (the DEV planter can do it,
   a cast cannot), that turn does not count and the hold waits for the next one.
3. The hold releases on the FIRST of: the provoked enemy's turn ends; the provoked enemy dies; the
   provoked enemy leaves the field entirely (EnemyGone, declared only after
   `Tuning.ProvokeMarkedMissTicks` consecutive failed re-locates, so one unreadable frame never
   releases the hold); the
   provoked enemy can no longer carry out its provoked turn because it is Petrified, Confused,
   Stopped, Charmed, Slept, or set to Don't-Act (status ids 8/11/30/34/35/37, read on its composed
   layer); the bearer dies; the bearer no longer holds a Defender in its main hand; the battle ends.
   NOTE: a provoked enemy that is instead mind-controlled by Puppeteer's agency bits (not the Charm
   status) is not caught by this list and is left to the watchdog; closing that gap is LW-126.
3b. Releasing the hold CLEARS THE MARK off the provoked enemy, so the same enemy can be provoked
   again later in the battle. Owner design decision, 2026-07-22. The composed bit (`+0x45` mask
   `0x80`) is the half that does the work: it is what the engine's already-has-it refusal reads, and
   clearing the inflicted registry alone leaves the recast refused at 0%. Both layers are cleared
   anyway, composed first, because a registry bit left set is residue that may reach a save. Every
   write is mask-scoped: `+0x45` is shared with Dead, Undead, Charging and Jump, and KillTracker's
   death detection reads that byte, so a whole-byte write there is a correctness bug, not a style
   one. If the mark ever returns after a clear, the next tick clears it again.
3c. Provoke can be cast at a friendly unit, not just an enemy: the cursor allows picking your own
   side, owner confirmed live 2026-07-23. That cast is legal and DOES NOTHING to the ally, but the
   inert mark it leaves never expires (Counter 0) and blocks a recast on the same unit at 0%, so
   left alone it would strand that unit "Provoked" for the rest of the battle. LW-130. The runtime
   scrubs the mark off any PLAYER-side seat wearing it, every live tick, independent of the hold's
   own Idle/Armed state (a player can provoke an ally while a hold on some other enemy is already
   up). The bearer is included, not exempt: it is a player-side seat like any other and a mark on
   it is just as stuck. Reuses the same mask-scoped `ClearMark` criterion 3b uses, on both layers,
   for the same reason (`+0x45` is shared with Dead/Undead/Charging/Jump; KillTracker reads it).
   Whether a friendly mark could reach a SAVE before the next tick scrubs it is UNMEASURED, same as
   the open question already on record for the enemy case below; this criterion does not claim it
   cannot.

3d. A mark that lands on an enemy while the hold CANNOT arm is scrubbed off rather than left on that
   enemy for the rest of the battle. LW-136, found by desk reading on 2026-07-27, not by play. The
   way in that matters is TWO DEPLOYED DEFENDERS: the command is granted off the first roster row
   holding id 33 in a main hand, while the hold resolves its bearer through
   `Wielder.ResolveDeployedMainHand`, which returns 0 on two deployed wielders because the pair is
   genuinely ambiguous. So the command is castable while the hold refuses to arm, the mark lands,
   nothing ever releases it (there is no hold to release), and because the mark never expires and
   cannot be re-applied, every later cast on that enemy reads 0% for the rest of the battle. The
   scrub is debounced on `Tuning.ProvokeMarkedMissTicks`, the same counter the marked-enemy locate
   uses, so a bearer read that misses for a single tick cannot eat a mark the next tick would have
   armed on. Reuses `ClearMark` on both layers, mask-scoped, for criterion 3b's reasons.

**Who is flagged**

4. While the PROVOKED enemy is taking its turn, AND during the run-up to that turn, every valid,
   on-field, player-side band entry except the bearer carries the Invisible bit, re-stamped each
   tick so splash damage cannot strip it for the rest of the turn. The run-up is the load-bearing
   half and it is not optional: the AI commits its target the instant its turn opens, so a hide that
   waits for the provoked enemy to BE the actor is always too late (measured live 2026-07-22). The
   runtime therefore reads who acts next from each enemy's charge time and speed (LW-118/LW-127,
   `TurnOrder.cs`), hides once the provoked enemy is the next ENEMY due, and holds through its turn.
   Player seats are deliberately excluded from that ranking: `ExtraTurn` writes charge time and
   `Iai` and `GrowthEngine` write speed on player seats, all three ticking before this one in the
   same loop, and `Offsets.cs` warns the player-side read is untrustworthy anyway.
5. On the player's own turns, on an ally's turn, and on any NON-provoked enemy's turn, no unit is
   flagged by Provoke, with TWO exceptions. First, the enemy acting immediately before the provoked
   one (criterion 19). Second, and this one was bought with a failed live pass on 2026-07-27: once
   the provoked enemy is the NEXT enemy due, the party stays hidden even through your own turns.
   The original ordering revealed on any player turn, and the tape shows exactly what that cost:
   `hide (why=next ... nextNameId=828 markedEta=4)` at 08:08:04, then
   `reveal (why=player-turn ... nextNameId=828 markedEta=3)` at 08:08:09 with that same enemy still
   next, then its turn opened against a fully visible party and it walked past the bearer to kill a
   mage. A player turn ends and the next turn opens with no gap to re-hide in, so revealing there is
   the same lost race in a new costume.
   The reveal was also buying nothing: a flagged ally can still be selected and targeted normally,
   CONFIRMED live 2026-07-22, so healing and buffing behave as usual either way. The one cost was
   cosmetic, the party wearing the status icon during your own turns while the shout winds up, and
   the owner rejected even that, so criterion 18's data fix removes the icon outright: with it in,
   nothing about the extended hold is player-visible at all.
   A reveal has to be EARNED, which is the opposite of how this shipped before 2026-07-27: the
   decision defaults to hiding, and only the provoked enemy reading as clearly further off than the
   leading enemy by `Tuning.ProvokeRevealMarginTicks`, or a player-side seat owning the turn while
   the provoked enemy is NOT next, opens the party back up. Every ambiguous or unusable read stays
   hidden, so a degraded build still funnels rather than silently doing nothing, which is the LW-135
   failure this ordering exists to make impossible.
6. Side membership is read from the friend/foe bit, so guests are hidden alongside the party. That
   byte is never written.
7. Seats reading combat `+0x01` == `0xFF` are skipped. The engine parks staged cutscene units in
   real band seats with sane stats and real positions, and five of them once sailed through a
   position-based filter.
8. The bearer is never flagged.
9. Enemy units are never flagged, ever, by any path.

**Release and fail-safes**

10. The bearer dying while the hold is up releases it on that same tick. Every unit on your side
    being invisible at once is a state nobody has observed and this feature does not ship it.
11. Provoke clears ONLY bits it set itself. A unit already carrying the Invisible bit when the hold
    arms is left alone entirely, both on arm and on release, so Feign Death holding the same bit on
    the same byte is never disturbed in either direction.
12. On the battle-exit edge, every seat Provoke ever flagged is cleared.
13. On the battle-ENTER edge, the same sweep runs before anything else, so a hold stranded by a
    mid-hold reload or a fast battle restart, where our tracked seats survive in-process, does not
    survive into the next battle. A hard PROCESS kill loses the tracked seats with the process, but
    that case is covered instead by the engine constructing fresh units at the next battle, so no
    stranded flag reaches it either way.
14. A watchdog releases the hold if it has been up longer than a plausible WAIT for the marked
    enemy's turn (90 seconds of live battle time, in `Tuning.cs`). This exists because the flag
    never expires on its own; it is a backstop for a release condition we failed to observe, and
    firing it logs at WARNING level (louder than a normal Event release) because it means a real
    bug. RAISED from 30 on 2026-07-27: the shout is cast on YOUR turn and the goaded enemy can sit
    most of a round away in the queue, which the live pass measured at 31 seconds, so the original
    cap described a single turn's length rather than the wait the design actually asks for. Raising
    `ProvokeTurns` above 1 needs this raised again, since the clock accrues across the whole hold.

**Observability**

15. Arm, release, and the reason for the release each log one line naming the provoked enemy's tile
    and the number of units flagged (arm and a normal release at Event level, the watchdog release
    at Warning, per criterion 14). MET in code: the release lines carried only the reason and the
    count until the tile was added, which meant a log with more than one arm in it could not say
    which enemy a release belonged to. The tile printed on release is the LAST KNOWN one, refreshed
    every armed tick the enemy can still be located, because the two releases that most need it,
    EnemyGone and EnemyDead, are exactly the ones with no readable seat left. Covered by
    `LivingWeapon.Tests/ProvokeHoldTests.cs` (the four tile tests, one per release shape).
16. Arm and release are recorded to the flight recorder, so a battle that goes wrong can be read
    after the fact rather than reproduced.
17. A watchdog release, or any tick where a write is refused by the guarded write path, logs
    distinctly from a normal release.

**Accepted costs, stated so they are not read as defects**

18. SOLVED IN DATA 2026-07-27 (LW-129), by a different lever than this criterion spent its life
    waiting for. The AI-ignore flag and its icon are the SAME bit, so the flag could never be set
    without the renderer wanting to draw the icon; and once criterion 5 started holding the flag
    through YOUR OWN turns (the branch reorder), that icon would have sat over the party's heads
    while you gave orders, which the owner rejected as a deviation from the design. The fix does not
    touch the flag or any dynamic address: `uistatuseffect.en.nxd` Key 20 (Invisibility) is baked to
    display category 0, the never-rendered gate (`Unknown14 = 0`), via `tools/patch_status_names.py`
    with the cell-level audit green. TWO ITERATIONS, recorded because the first is a lesson: the
    first bake set only the icon-index cell to -1 and FAILED LIVE the same day (the owner still saw
    the icon); that -1 semantic was correlation read off never-rendered rows, never a tested lever.
    `U14 = 0` is the one cell this table has ever proven causally (it made the mark render nothing,
    2026-07-22). COST, wider than first aimed for: Invisibility disappears from the status LIST as
    well as the overhead rotation. For the hidden party that is nothing; a vanilla Vanish Mantle
    wearer loses the list entry, and their unit is rendered transparent by the real apply path
    anyway, so the state stays self-evident. Feign Death (our own hold on the same bit) sheds its
    icon too. The old lever (global overhead-UI toggle at a dynamic `0x43xx` address,
    owner-probe-gated) is RETIRED UNPROBED. AWAITING the owner's eyes: nobody has yet seen a battle
    with the icon gone. Restart-only data, so it needs the redeploy. If THIS iteration also shows
    the icon, this table does not drive the overhead icon for statuses with real art, the data lane
    is dead, and the next step is a what-renders probe, not a third cell guess. Hiding the reddened
    HP bar of a flagged ally stays deferred polish (already hidden on the default HUD).
19. REWRITTEN 2026-07-27, and the cost it accepts is now much smaller. This criterion used to licence
    the whole-phase behaviour ("every enemy that acts while the hold is up is redirected onto the
    bearer, not just the provoked one... the accepted v1 behaviour"), which is exactly what the owner
    rejected after playing it. Anyone scoring a live pass against the old wording would have marked
    the removed behaviour as a PASS. WHAT IS ACCEPTED NOW: the enemy acting IMMEDIATELY BEFORE the
    provoked one is also hidden from, so it redirects too. One extra, not the whole side. The cause
    is timing rather than sloppiness: that enemy commits its target at its own turn-open, and the
    hide has to be up before the provoked enemy's turn opens, so the two windows touch. It can be
    more than one when enemies are bunched inside `Tuning.ProvokeRevealMarginTicks` of each other,
    because the reveal needs daylight and a bunched queue never provides it; one is the expectation,
    two is not a failure, all of them is. Closing the gap means letting the preceding enemy commit
    first and only then hiding, which needs a delay measured from a live pass rather than guessed;
    that measurement is the follow-up, not this pass.

## Live verification (pre-registered)

One battle, played normally, decides whether the Defender's shout works. Cast it, watch who the
enemies swing at, then read one line in the log to see the hold let go for the right reason.

**What counts as correct, since it changed on 2026-07-27.** The shout should affect the enemy it was
aimed at, plus a small tail of the enemies acting immediately before it, normally one and more when
several are bunched close together in the turn order (criterion 19). Every enemy further back should
fight normally, with the party plainly visible. Two neighbours redirecting is not a failure; the
whole enemy side redirecting is. THE FAILURE THAT
LOOKS LIKE A SUCCESS: if the lookahead never fires, the party stays hidden the whole phase and every
enemy beelines for the bearer, which still ends with the bearer being attacked and can easily be
scored as a pass. So the deciding observation is NOT "did the bearer get hit" but "did the enemies
acting EARLIER hit somebody else". Both halves of the trigger are in the runtime (arc 2b, commit
3565363): the command is granted and its table repoint is performed in process, with no probe script
anywhere near it.

**Setup.** A Defender (item id 33) at kill tier 3 or above in a unit's MAIN hand, that unit deployed
and alive, plus at least two OTHER party members deployed, since they are the ones that get hidden.
The bearer should not be the closest unit to the enemy, so a redirect is visibly a redirect. For the
command to appear at all, the bearer's job must be Squire or Knight, or carry one of their action
sets as its secondary command (criteria 0a/1); any other job logs "cannot receive Provoke" and
grants nothing.

**Arming (primary): cast the real command.** Pick Provoke from the bearer's action list and point at
any enemy within range 5. That is the bearer's action for the turn, and the cast should read 100% on
the cursor.

**Arming (fallback, DEV builds only).** If the command itself is not reachable on the save at hand
(wrong job, no free slot in the action list), hover the target enemy and press F6, or drop any file
named `provoke_request.txt` into the mod directory (polled about twice a second, deleted on read).
F3 is eaten on this box. Environment variables do not survive this game's launch chain, so they are
not an option. The dev planter writes exactly the same id-0 mark a real cast writes, and the hold
gates on that mark rather than on how it got there, so the two lanes exercise identical production
code. What this lane does NOT substitute for is the bearer: the hold still needs a deployed, living,
tier-3 main-hand Defender, and exactly one of them (see criterion 3d), or it will not arm at all.

**Bait step (makes a clean result meaningful).** Run one enemy turn with no hold and record who each
enemy attacks. Without this, a bearer who was going to be attacked anyway proves nothing.

**Results of the first two runs, 2026-07-27 (read this before running it again).** Two separate
bugs, one per run, both found in a single battle each and both fixed.

RUN 1 hid nobody. The hold armed, tracked the goblin, and released as `EnemyTurnDone`, so the
release gate worked; the log named the failure in one number, `0 units were ever hidden`. Cause:
WindowAction read the cursor/team field, which reads PLAYER while an enemy acts because the cursor
sits on the unit being attacked, so every tick chose Reveal. That is LW-135, fixed by moving the
call onto the same player-side turn-flag walk the release gate uses.

RUN 2 hid 2 units, and released 28 seconds too early. Arm 04:30:40.222, `provoke hide: 2 unit(s)`
5ms later, release `EnemyTurnDone` at 04:30:43.467 (3.2 seconds), and the marked enemy was still
standing on its arm tile 3,11 for another 31 seconds before it moved. Cause: the engine's actor
pointer PARKS on the unit a player action targeted, and Provoke targets the enemy it marks, so
during the cast's own resolution the pointer named the marked enemy while no menu was open for the
turn-flag gate to veto. The end of that park counted as a turn that had not happened. That is
LW-138, fixed by an edge-origin rule: a park already underway when the hold arms is ignored, and
only a park that RISES after arming can complete a turn. `Tuning.ProvokeWatchdogSeconds` went 30 to
90 in the same change, because run 2 measured a healthy 31-second wait from cast to the enemy's own
turn and the old cap would have force-released a working hold with a WARN that means "a real bug".

The run below is the retest of both. What must be true this time: units are hidden (run 1's
failure), AND the hold is still up when the marked enemy actually takes its turn (run 2's).

**PASS.** With the hold armed, all four hold:

1. The MARKED enemy attacks the bearer, including from a position where a different party member is
   closer. The enemies that act EARLIER attack whoever they like, with the party plainly visible.
   A SMALL number of exceptions is expected and accepted, normally just the enemy acting immediately
   before the marked one, and more when several enemies are bunched close together in the turn order
   (criterion 19). "Every enemy attacked the bearer" is a FAILURE, not a pass, and it is the failure
   that most resembles success, so this item is the one to read slowly.
2. `livingweapon.log` in the Mods folder (read the file, not the console) shows
   `The provoke hold ends (EnemyTurnDone)` landing a second or two after the marked enemy finishes
   its turn.
3. On release the field returns to normal: nobody stays hidden, and the mark comes off the marked
   enemy, so that same enemy can be shouted at again later in the same battle (criterion 3b).
4. The tape carries at least one hide edge that PRINTS ETA FIELDS (`leaderEta=`), rather than every
   hide edge reading `lookahead=fallback`. That is the runtime saying in its own words that the
   turn-order ranking actually ran. Do NOT require the word `why=next` specifically: the edge line is
   written only when the hide/reveal ACTION changes, so if the party is already hidden for the
   neighbour when the marked enemy becomes next, the reason flips with no new line, and a perfectly
   correct battle can contain no `why=next` at all. Presence of the ETA fields is the guaranteed
   signal; `why=next` is a bonus when you get it. See the token table below.

**Three things to watch with your eyes, because they close three tickets in one battle.**

1. **LW-127.** During the marked enemy's turn, do the hidden units STAY hidden, or flicker visible
   partway through its attack? A flicker now means the hide/reveal decision crossed the reveal margin
   mid-turn, i.e. branch 4 fired because the ranking stopped naming the marked enemy as next
   (`Tuning.ProvokeRevealMarginTicks`, `ProvokeHold.Scan.cs`). Capture the tape: the `why=` token on
   each edge names the branch that decided. NOTE, because this bullet said something else until
   2026-07-27: a flicker is NOT evidence of the old cursor-field bug. `ProvokeHold` reads no cursor
   field anywhere any more, so that diagnosis is dead and following it wastes a battle.
2. **LW-130 / criterion 3c.** Cast Provoke at one of your OWN units, and again at the bearer itself.
   The mark should clear within a tick ("A stray provoke mark landed on one of your own units"), and
   a second cast on that same unit should land at 100% rather than being refused at 0%.
3. **LW-123 / criterion 3b.** After the release, the same enemy accepts a second Provoke.

**Failure signatures and what each means.**
- The WARN line `The provoke hold timed out and released on its own`, roughly
  `Tuning.ProvokeWatchdogSeconds` (90 seconds of unpaused battle time) after the arm line: a release
  condition was missed, which is exactly the signature of the bug LW-131 set out to fix. Capture the
  log and the flight tape before doing anything else. Do NOT read a long wait as this failure: a
  healthy hold routinely runs half a minute or more, because the shout is cast on your turn and the
  goaded enemy can sit most of a round away. The 2026-07-27 pass measured 31 seconds, which is why
  the cap is 90 and not the 30 this line used to name.
- An `EnemyTurnDone` release that fires BEFORE the marked enemy ever acts: the release gate is
  counting a parked actor pointer as an actor. OBSERVED 2026-07-27 and fixed (LW-138), so it is here
  as a regression signature now, not a hypothetical. The tell is the gap: a release landing a few
  seconds after the arm, with the marked enemy still on the tile both lines name. Cross-check the
  arm and release timestamps against when the enemy actually moved.
- No arm line in the log at all: either the mark never landed (did the cast read 100%?) or the
  bearer failed its tier-3, main-hand, deployed and alive check.
- An arm line but `0 units were ever hidden` on release: this is the 2026-07-27 failure above. The
  log settles which half broke without another battle, because every hide/reveal TRANSITION logs one
  line: `provoke hide: N unit(s) now hidden` or `provoke reveal: ...`. No hide line at all means the
  hold chose Reveal throughout (a turn-detection problem); a hide line reading 0 units means the
  enumeration found no targets (check that other party members really are deployed and on field).
- EVERY enemy redirecting onto the bearer: the lookahead never fired and the build fell back to
  hiding for the whole phase. The bearer still gets attacked, so this reads as a success unless you
  watch who the EARLIER enemies hit. DO NOT try to settle this by looking for the presence or absence
  of `provoke reveal:` lines. Reveals fire on every one of YOUR units' turns too (branch 1), and the
  2026-07-27 tape had 31 seconds and several player turns between the cast and the marked enemy's
  turn, so reveal lines appear in the broken world and the working world alike. This document told
  you to use that test until it was measured and found useless; use the token table instead.
- The marked enemy attacks somebody else while the party WAS hidden and the hold WAS up: the hide
  landed too late, or the ranking named the wrong enemy as next. Distinguish on the last hide edge
  before that turn by whether ETA FIELDS are present. ETA fields present (any `why=`, including
  `fallback`) means the ranking was running, so suspect timing or a mispredict; `lookahead=fallback`
  means the ranking never ran at all and the hide you saw was the blanket one. Capture the tape and
  note `markedEta` on the last few edges. READ THE TIMESTAMPS WITH CARE: an edge line is written only
  when the hide/reveal action CHANGES, so the `why=` on it describes the tick the hide went up, not
  the tick the enemy's turn opened. It cannot by itself prove the hide was late; the flight tape
  timestamps against when the enemy visibly moved are what settle that.
- A `reveal (why=player-turn ...)` whose `nextNameId` is still the provoked enemy, at any point
  before that enemy acts: the 2026-07-27 REGRESSION. It means the party was uncovered while the
  provoked enemy was already next in line, and its turn will open with everyone visible. This is the
  failure the branch reorder was made to eliminate, so seeing it again means the ordering in
  `ProvokeHold.Policy.ActionForWithReason` has drifted back.
- A unit stays hidden after the release line: the release path is incomplete.
- A unit is hidden at the start of the NEXT battle: the enter sweep did not run (criterion 13).

**The token table: what the hide and reveal lines actually say.** A line is written only when the
hide/reveal ACTION changes, never per tick and NOT when only the reason changes, so one hide edge can
cover a long hidden span and its `why=` describes the moment the hide went up. The single most useful
distinction is not any particular `why=` value: it is whether the line carries ETA FIELDS at all.
ETA fields present means the turn-order ranking ran; `lookahead=fallback` means it did not.

| Token on the edge line | What it means |
|---|---|
| `why=next leaderEta=N nextNameId=N markedEta=N` | The ranking RAN and named the marked enemy as the next enemy due. The feature working, plainly. Welcome but NOT required: see the note above about reason changes that write no line. |
| `why=fallback leaderEta=N nextNameId=N markedEta=N` | The ranking RAN, and the marked enemy is inside the reveal margin of the leader, so the party stays hidden. This is a NORMAL working-build line and it is what produces criterion 19's accepted neighbour redirect. Do not read the word fallback here as failure; the ETA fields prove the ranking ran. |
| `why=actor ...` | The marked enemy is currently acting. Expected during its own turn. |
| `why=player-turn` | One of your units holds the turn AND the provoked enemy is not next and not acting, so the party is revealed on purpose (criterion 5). Normal and common. BUT a `why=player-turn` reveal arriving while `nextNameId` is still the provoked enemy is the 2026-07-27 live failure and must not reappear: that ordering let the enemy's turn open against a visible party. Branch order now puts "is next" above "player turn" precisely to make that line impossible. |
| `why=far-off ...` | The ranking ran and put the marked enemy clearly behind the leader, so the party was revealed. This is what SHOULD happen while earlier enemies act. |
| `lookahead=fallback` (anywhere on the line) | The ranking could not read the turn order at all, so no ETA fields are printed. `why=fallback lookahead=fallback` on a HIDE edge is the whole-phase blanket. If every hide edge in the battle carries `lookahead=fallback`, the feature never engaged: that is failure (ii), whatever the bearer's HP bar suggests. |
| `markedEta=?` | The marked enemy's own seat was refused by the candidate filter (off field, dead, garbage read). Not a genuine ETA of zero. |

**Retry, do not sign off.** Any release reading `EnemyDead`, `EnemyGone`, `EnemyDisabled`,
`BearerGone` or `BearerDead`. Each of those exits for its own reason and never exercises the
turn-end gate this pass exists to test, so the battle proves nothing either way. `EnemyDisabled` is
worth one extra note: the disable arm (Petrify, Confuse, Stop, Charm, Sleep, Don't-Act) has no
behavioural test behind it, only a policy test fed a literal bool, so a wrong layer, mask or id
would show up not as a wrong release but as the 90-second watchdog WARN above.

**Not a failure, do not report either as one.**
- The enemy or two acting immediately before the provoked one redirecting onto the bearer. That is
  criterion 19's accepted cost. Every enemy redirecting is a DIFFERENT thing and IS a failure now
  (see the failure table): it means the lookahead never fired and the build reverted to hiding for
  the whole phase. Until 2026-07-27 this line said the opposite, so read it carefully.
- SINCE 2026-07-27 the hidden units should show NO icon at all (criterion 18's data fix). Seeing
  the Invisibility icon over their heads is therefore no longer neutral: it is a soft signal the
  baked `uistatuseffect.en.nxd` did not load (a table change, so it needs the game RESTARTED, not
  just relaunched from the Reloaded menu). It does not fail the funnel half of the pass on its own.

**Reading the flight tape.** Do NOT hard kill the game to grab one: the recorder flushes on the
battle EXIT edge, so END the battle (win, lose or flee) and Alt-Tab out, at which point the file is
already on disk. Read it with `python tools/parse_flight.py <file>`; arm, release and player-side
scrub are all recorded under the `provoke` tag.

**The icon-suppression probe is CANCELLED, not merely optional.** Icon suppression shipped in data
on 2026-07-27 (criterion 18: `uistatuseffect.en.nxd` Key 20 to the never-rendered category,
iteration 2 after the icon-cell-only attempt failed live), so the global overhead-UI toggle and its
dynamic-address stability question no longer gate anything. What replaces the probe is two glances
during this pass: hidden units should wear NO icon, and a provoked ENEMY's status list should still
read "Provoked" (which proves the baked table loaded at all; both edits ride the same file). Icon
still showing plus "Provoked" still present would mean the table loads and this lane cannot reach
the overhead icon; see criterion 18 for what happens then.

Status stays AWAITING-LIVE until the owner runs this. Only the owner flips it.

## Deferred to v2

- **DELIVERED 2026-07-27, no longer deferred: the clean single-enemy facade.** It was parked here
  because the reactive version lost the turn-start race, and it became buildable once LW-118 measured
  live that the next enemy to act is computable from charge time and speed. Shipped as LW-127, the
  five-branch rule in `ProvokeHold.Policy.ActionFor`, with `TurnOrder.cs` supplying the run-up. What
  is still outstanding is the LAST enemy: the one acting immediately before the provoked one is also
  hidden from (criterion 19), and closing that needs a commit delay measured from a live pass rather
  than guessed.
- Hiding the reddened HP bar a flagged ally shows (already hidden on the default HUD). The status
  icon half of this bullet shipped 2026-07-27 in data (criterion 18) and is no longer deferred.
- A per-battle use cap, if uncapped play proves degenerate.

## Open questions (do not block this arc)

- Can the player still select and target their own units while flagged? ANSWERED live 2026-07-22:
  yes, a flagged ally can be targeted normally. Criterion 5 makes it moot in v1 anyway (branch 1
  reveals on a positive player-side turn-flag read, so nobody is flagged while your own menu is
  open), but the confirmed answer means a future continuous-hide design would not break healing.
- Do caster and archer enemies funnel the same way? The premise row observed melee only.
- Does a mid-hold autosave persist the composed bit into the save?
