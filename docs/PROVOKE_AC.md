# Provoke (Defender +3) -- scope + acceptance criteria

STATUS: CONTRACT (Provoke acceptance criteria)

Approved 2026-07-22. Design source: `docs/living_weapon_grid.csv` row 34 (Defender, id 33).
Post-release feature: it does not gate 2.3.1 (`docs/RELEASE_SCOPE.md`, locked "no new features").
Work ledger id: LW-123.

**SHIPPED STATUS (arc 2a, live 2026-07-22).** The FUNNEL PREMISE IS PROVEN (owner-verified through
the mod: hiding every player unit except the bearer makes the enemy AI target the bearer;
docs/LIVE_LEDGER.md Proven row). The runtime SHIPS IN **WINDOW** MODE (`Tuning.ProvokeSliceMode =
false`): it hides for the WHOLE enemy phase, so units are hidden before any enemy commits a target.
SLICE (hide only during the provoked enemy's own turn, the clean single-enemy facade) was tried live
and LOSES THE TURN-START RACE: the AI commits its target the instant its turn opens, so a hide that
reacts to the turn starting lands too late. Clean single-enemy slice is DEFERRED and needs turn-queue
LOOKAHEAD (LW-118). Below, wherever a criterion says "SLICE is the shipped default," read it as the
DEFERRED goal; WINDOW is what ships. Polish A (2026-07-22) moved the release detection onto the proven
actor pointer so WINDOW releases promptly when the provoked foe's turn ends. Arc 2b (the data
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

Under the shipped WINDOW mode, every enemy that acts until the provoked foe's turn ends is redirected
onto the bearer, so provoking a foe further down the queue pulls the ones ahead of it too. The turn
order is on screen, so that is a visible tactical price the player reads with full information, not a
hidden limitation. The clean single-enemy version (only the foe you point at, wherever it sits) is
the DEFERRED slice goal, which lost the turn-start race live.

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

**The hold decides who they can see.** While the mark is up and an enemy holds the turn (WINDOW, the
shipped mode), the runtime flags every other unit on your side with the engine's own "cannot be seen"
status, leaving the bearer as the only name on the acting enemy's target list; the flags stay up
across the whole enemy phase, so units are hidden before any enemy commits a target. This half is
subtractive because it has to be: there is no aggro in this game. A PSX dig closed that question,
enemy targeting is computed tile scoring with no holdable focus field, so the only lever on who the
AI attacks is whether a unit can be targeted at all. (The deferred SLICE mode would hide only during
the provoked enemy's own turn, but it loses the turn-start race; see the acceptance criteria.)

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
| Holding the composed Invisible bit on every player-team unit except one funnels enemy AI onto that one | `docs/LIVE_LEDGER.md` Uncertain, 2026-07-22 | LIVE-OBSERVED, owner PROVEN flip PENDING |
| A raw composed status write is an orphan flag: the AI reads it, no effect is performed, and it NEVER expires | `docs/LIVE_LEDGER.md` Uncertain, 2026-07-22 | LIVE-OBSERVED, owner PROVEN flip PENDING |
| The Invisible bit survives the unit acting; it is cleared by BEING HIT | `LivingWeapon/Offsets.cs` AInvisible block | corrected 2026-07-22 |
| Turn-owner team field is reliable for turn-level gating | `docs/LIVE_LEDGER.md` Proven, 2026-06-16 | PROVEN |
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
and its fail-safes. The trigger is stubbed behind a seam.

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
- Reveal-timing: SLICE (the default) does depend on the hide landing at the provoked enemy's
  turn-start before its AI commits a target. That is a variable MEASURED at the live pass, not an
  unmeasured number baked into the code, and the WINDOW fallback (which cannot lose the race) is the
  safety net if the measurement goes against us. The v2 hardening is turn-queue LOOKAHEAD.
- Per-enemy time-slicing IS in v1 (the slice default, criterion 4): the hide fires only during the
  provoked enemy's own turn. Only the lookahead half remains v2 (see Deferred).
- NO stat change to the Defender's static profile. Its numbers stay as `data/items.json` id 33 has
  them. CORRECTED 2026-07-22: this document previously asserted the item is already gate-exempt as a
  living weapon, and it is not. Only id 32 carries the `livingWeapon` flag; id 33 has no flag and no
  signature block at all today, and it passes `analyze.py` on its own numbers. Adding a signature
  block flips two gates red on the spot (its `docs/living_weapon_grid.csv` "+3 ability" cell is 134
  characters against a 90 cap, and the assembled description is 234 against a 205 budget), so the
  grid cell and the new `p3Desc` have to be shortened byte-identically in the same commit.

## Acceptance criteria

**The command itself**

0a. Provoke appears as a real entry in the bearer's command list, named Provoke, with its own
   description and icon, and is selectable rather than greyed.
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
0f. The ability's own description says what the ability does. IT DOES NOT TODAY, and the wrong text
   is already committed: `tools/patch_ability_names.py` Key 189 ships "Goad a distant foe into a
   blind rage. It forgets its skills and charges, seeing only the one who called it out." That is
   Berserk prose, left from the abandoned index 53 design. The provoked enemy keeps its entire
   skill set and nothing makes it charge; the redirect comes from hiding the bearer's allies, not
   from enraging the target. Harmless while nothing can reach ability 189, and player-visible the
   moment the command is granted, so the fix rides the same commit that first grants it. Changing
   it means editing that script, rebaking `ability.en.nxd` and re-running
   `tools/audit_nxd_bakes.py`, so it is one text change, not two. The provenance comment above the
   row is stale in the same way: it still describes the byte being repointed to 53 (Berserk).

**Arming and duration**

1. Provoke is offered only when the bearer holds a Defender (id 33) in its MAIN hand at kill tier 3
   or above, is deployed, and is alive.
1b. The hold lasts `Tuning.ProvokeTurns` of the provoked enemy's turns. Ships at 1 for the first
   live pass, target 3.
2. The hold arms on the cast, during the bearer's own turn, and is up continuously from that instant
   until it releases. There is no timing window to hit.
3. The hold releases on the FIRST of: the provoked enemy's turn ends; the provoked enemy dies; the
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

**Who is flagged**

4. While the PROVOKED enemy is taking its turn, every valid, on-field, player-side band entry
   except the bearer carries the Invisible bit, re-stamped each tick so splash damage cannot strip
   it for the rest of the turn. NOTE: this criterion describes SLICE mode
   (`Tuning.ProvokeSliceMode = true`), which is DEFERRED (WINDOW ships; see the shipped-status note).
   Under the deferred slice only the goaded enemy is redirected, so the fiction "I called out THAT
   one" holds. A WINDOW
   fallback (`ProvokeSliceMode=false`) instead flags during any enemy's turn, redirecting every
   enemy that acts while the hold is up; it exists because it cannot lose the turn-start timing race
   slice trades in (see Live verification and Deferred).
5. On the player's own turns, on an ally's turn, and on any NON-provoked enemy's turn, no unit is
   flagged by Provoke: under slice we only hide during the provoked enemy's own turn. Your own units
   are normally targetable on your own turns, so healing and buffing behave as usual, and it is
   CONFIRMED live 2026-07-22 that a flagged ally can still be targeted anyway.
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
14. A watchdog releases the hold if it has been up longer than a plausible single turn (initial
    value: 30 seconds of live battle time, in `Tuning.cs`). This exists because the flag never
    expires on its own; it is a backstop for a release condition we failed to observe, and firing it
    logs at Event level because it means a real bug.

**Observability**

15. Arm, release, and the reason for the release each log one line at Event level naming the
    provoked enemy's tile and the number of units flagged.
16. Arm and release are recorded to the flight recorder, so a battle that goes wrong can be read
    after the fact rather than reproduced.
17. A watchdog release, or any tick where a write is refused by the guarded write path, logs
    distinctly from a normal release.

**Accepted costs, stated so they are not read as defects**

18. Flagged units would visibly wear a status icon, because the AI-ignore flag and its icon are the
    SAME bit: a raw composed bit renders the icon and performs no visible effect, so the unit never
    turns transparent. We SUPPRESS that over-head UI so the trick stays hidden, but the lever is a
    global toggle on a DYNAMIC address (`0x436A367BF8`) whose launch stability, and whether it hides
    the status ICON versus only the HP bar, are unconfirmed, so it is gated on an owner-run probe.
    Until that probe passes, a build is allowed to show the icon and that is not a failure of the
    hold. Hiding the reddened HP bar of a flagged ally stays deferred polish.
19. Under the SHIPPED WINDOW mode, every enemy that acts while the hold is up is redirected onto the
    bearer, not just the provoked one. This is the accepted v1 behaviour: the funnel is proven, and
    SLICE (which would redirect only the provoked enemy) lost the turn-start race live and is deferred
    with lookahead. Polish A's prompt release keeps the window tight, so provoking the next-to-act
    enemy still reads as a clean single redirect.

## Live verification (pre-registered)

The hold engine ships before the trigger, so the DEV build arms it through the marker-file lane in
the mod directory rather than a command, matching the existing dev request-file instruments.
Environment variables do not survive this game's launch chain, so they are not an option.

**Setup.** A battle with at least three player units and two enemies. Bearer holds a Defender at
tier 3. Formation matters less than it did for the reveal experiment, but the bearer should not be
the closest unit to the enemy, so that a redirect is visibly a redirect.

**Arming (DEV).** Hover the target enemy and press F6, or drop `provoke_request.txt` into the mod
directory; the dev planter writes the id-0 mark on that enemy and the production hold polls for it.

**Bait step (makes a clean result meaningful).** Run one enemy turn with no hold and record who each
enemy attacks. Without this, a bearer who was going to be attacked anyway proves nothing.

**PASS (slice).** With the hold armed, the PROVOKED enemy attacks the bearer on its turn, including
from a position where a different unit is closer; enemies that act BEFORE it attack whoever they
prefer, so only the goaded one is redirected. On the provoked enemy's release the field returns to
normal. No unit carries the status icon after release, after the battle ends, or at the start of the
next battle, subject to the icon-suppression probe below (until that lever is proven, a lingering
icon is not a failure of the hold).

**Failure signatures and what each means.**
- The provoked enemy attacks someone else at its turn-start (a closer visible ally): the AI latched
  its target before our turn-start hide landed, so the slice race is lost. Flip
  `Tuning.ProvokeSliceMode` to false (WINDOW) for the ship and add turn-queue lookahead.
- EVERY enemy in the window redirects, not just the provoked one: you are seeing WINDOW behaviour;
  confirm `ProvokeSliceMode` is true.
- The hold never fires at all (no units hidden): the marked enemy's own turn flag is not reading 1
  for an enemy; fall back to the actor-pointer identity signal.
- The provoked enemy attacks the bearer but a unit stays flagged after release: the release path is
  incomplete. Capture the log and the flight tape before doing anything else.
- A unit is flagged at the start of the next battle: the enter sweep did not run.
- The watchdog line appears: a release condition was missed. The tape names which.

**Also run the icon-suppression probe in this pass.** With a unit wearing our flag, test whether the
global overhead-UI toggle hides the status ICON (not just the HP bar) and whether its dynamic
address is stable launch to launch. The result decides whether criterion 18's suppression ships
surgically, ships bluntly (all overhead UI off during the provoked turn), or is deferred.

Status stays AWAITING-LIVE until the owner runs this. Only the owner flips it.

## Deferred to v2

- **Turn-queue LOOKAHEAD**: pre-hiding just before the provoked enemy's turn opens, so the hide is
  guaranteed up before the AI commits even if it latches its target at turn-open. Per-enemy
  time-slicing itself SHIPPED in v1 (the slice default, criterion 4); lookahead is the hardening
  that only matters if the live pass shows the turn-start race is lost, and until then the WINDOW
  fallback is the safety net.
- Hiding the reddened HP bar a flagged ally shows, and suppressing the status icon.
- A per-battle use cap, if uncapped play proves degenerate.

## Open questions (do not block this arc)

- Can the player still select and target their own units while flagged? ANSWERED live 2026-07-22:
  yes, a flagged ally can be targeted normally. Criterion 5 makes it moot in v1 anyway (slice never
  flags during your own turns), but the confirmed answer means a future continuous-hide design would
  not break healing.
- Do caster and archer enemies funnel the same way? The premise row observed melee only.
- Does a mid-hold autosave persist the composed bit into the save?
