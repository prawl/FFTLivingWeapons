# Provoke (Defender +3) -- scope + acceptance criteria

STATUS: CONTRACT (Provoke acceptance criteria)

Approved 2026-07-22. Design source: `docs/living_weapon_grid.csv` row 34 (Defender, id 33).
Post-release feature: it does not gate 2.3.1 (`docs/RELEASE_SCOPE.md`, locked "no new features").
Work ledger id: LW-123.

## One line

The Defender lets its bearer shout down one enemy: point at any foe, and until that foe has taken
its turn, the enemies who act cannot see anyone on your side except the bearer, who has the game's
best parry to survive what it just invited.

## What the player does

1. Equip a Defender that has earned its third tier. Its bearer gains a command called **Provoke**.
2. Use it and pick any enemy on the field. That is the bearer's action for the turn.
3. From that moment until the provoked enemy finishes its next turn, any enemy taking a turn
   attacks the bearer instead of your other units.
4. It ends. Everyone fights normally again.

The turn order is on screen, so the player picks the price. Provoke the unit at the top of the
queue and exactly one enemy is redirected. Provoke a unit four places down and every enemy in
between is redirected too, because the shout is up the whole time. That is a tactical choice the
player makes with full information, not a limitation being hidden from them.

## How it works, plainly

Two halves: a mark, and a hold.

**The mark says who was goaded.** Provoke hangs a status on the unit you point at. That status is
`Wall` (band `+0x48` bit `0x01`), and it was chosen after the alternatives were tried live and
rejected. It is unique among the game's forty statuses in doing everything we need and nothing else:
it lands at 100%, it performs no effect, it drives no pose, the target keeps walking normally, it
renders an icon, and it ships with a BLANK name and description that we are free to write. It is
also, uniquely, absent from the engine's immunity system, so no unit in the game can resist it. The
mark is simultaneously the fiction the player reads, the receipt the runtime detects, and the record
of which enemy was chosen.

**The hold decides who they can see.** While the mark is up, the runtime flags every other unit on
your side with the engine's own "cannot be seen" status, leaving the bearer as the only name on the
enemy's target list. This half is subtractive because it has to be: there is no aggro in this game.
A PSX dig closed that question, enemy targeting is computed tile scoring with no holdable focus
field, so the only lever on who the AI attacks is whether a unit can be targeted at all.

REJECTED, and recorded so the reasoning is not relitigated. **Berserk** was the first choice and is
the better fiction (the engine genuinely enrages the unit for free), but 25 of 173 jobs resist it,
including the entire boss tier, and because the mark was also the receipt an immune target meant the
hold never fired either. **The system statuses** (`Evading`, `Performing`, `Critical`) look like free
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
  a HAND-WRITTEN row in the inflict-status table that applies `Wall` alone. That byte lives in the
  LIVE action table at `0x14078B2DC`, NOT the decoy copy at `0x14078961C`. The inflict row is ours
  to author: an unused index with the mode byte set to `0x80` first, then the five status bytes.
- **Named** by writing the blank `Wall` row in `nxd/uistatuseffect.en.nxd`, whose `Key` is the
  status bit index plus one (so `Wall` = Key 32).

Because the ability marks its own target, the runtime needs no cast-detection hook and no
action-record read: it polls for an enemy wearing the mark, which is a read it already does.

## Non-goals

- NO team-swap. Flipping allies to the enemy team funnels perfectly and empties the player team, so
  the bearer dying is an instant game over and no cheap guard makes that impossible. Rejected, and
  the friend/foe byte is never written for any reason.
- NO write to the inflicted status layer (`+0x1D3..+0x1D7`). A durable registry write would make a
  stuck hold permanent and survive our own cleanup.
- NO reveal-timing dependency. Nothing in this arc needs to arm inside the gap between two turns, so
  no unmeasured timing number appears anywhere in it.
- NO per-enemy time-slicing in v1 (see Deferred).
- NO stat change to the Defender's static profile. Its numbers stay as `data/items.json` id 33 has
  them, and the item stays gate-exempt as a living weapon.

## Acceptance criteria

**The command itself**

0a. Provoke appears as a real entry in the bearer's command list, named Provoke, with its own
   description and icon, and is selectable rather than greyed.
0b. It targets a single enemy at range 5, costs no MP, has no charge time, and lands at 100% on a
   non-immune target.
0c. It applies the `Wall` mark and nothing else: no damage, no pose, no engine state. The action
   row's InflictStatus index points at our authored row in the LIVE table (`0x14078B2DC`); the decoy
   copy is never written.
0d. There is no immunity gap. `Wall` is absent from the engine's immunity system, observed live
   2026-07-22 against a Netherseer immune to 37 of the 38 statuses that system knows, which landed
   at 100%. Every unit in the game can be provoked, so no exception is stated on the item card.
   The supporting ledger row is Uncertain until the owner flips it.
0e. The command is job-global: units of the same job, INCLUDING ENEMIES, inherit it. Accepted for
   v1, same as Sanguine Sword's shipped leak, and called out on the card.

**Arming and duration**

1. Provoke is offered only when the bearer holds a Defender (id 33) in its MAIN hand at kill tier 3
   or above, is deployed, and is alive.
1b. The hold lasts `Tuning.ProvokeTurns` of the provoked enemy's turns. Ships at 1 for the first
   live pass, target 3.
2. The hold arms on the cast, during the bearer's own turn, and is up continuously from that instant
   until it releases. There is no timing window to hit.
3. The hold releases on the FIRST of: the provoked enemy's turn ends; the provoked enemy dies; the
   bearer dies; the bearer no longer holds a Defender in its main hand; the battle ends.

**Who is flagged**

4. While the hold is up AND an enemy holds the turn, every valid, on-field, player-side band entry
   except the bearer carries the Invisible bit, re-stamped each tick so splash damage cannot strip
   it for the rest of the turn.
5. While the hold is up and a PLAYER or ALLY holds the turn, no unit is flagged by Provoke. Your own
   units are normally targetable on your own turns, so healing and buffing behave as usual.
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
    crash, a hard process kill, or a mid-hold reload does not survive into the next battle.
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

18. Flagged units visibly wear a status icon for the duration. They do not turn transparent, because
    a raw composed bit renders the icon and performs no visible effect. Accepted for v1.
19. Enemies that act between the cast and the provoked enemy's turn are also redirected. This is the
    design, not a leak (see What the player does).

## Live verification (pre-registered)

The hold engine ships before the trigger, so the DEV build arms it through the marker-file lane in
the mod directory rather than a command, matching the existing dev request-file instruments.
Environment variables do not survive this game's launch chain, so they are not an option.

**Setup.** A battle with at least three player units and two enemies. Bearer holds a Defender at
tier 3. Formation matters less than it did for the reveal experiment, but the bearer should not be
the closest unit to the enemy, so that a redirect is visibly a redirect.

**Bait step (makes a clean result meaningful).** Run one enemy turn with no hold and record who each
enemy attacks. Without this, a bearer who was going to be attacked anyway proves nothing.

**PASS.** With the hold armed, enemies taking turns attack the bearer, including from positions
where a different unit is closer. On release, the next enemy turn goes back to attacking whoever it
prefers. No unit carries the status icon after the release, after the battle ends, or at the start
of the next battle.

**Failure signatures and what each means.**
- Enemies still attack other units with the hold up: the flag is not landing (check the log for
  refused writes) or the funnel does not hold for this enemy composition, which contradicts the
  premise row and stops the arc.
- Enemies attack the bearer but a unit stays flagged after release: the release path is incomplete.
  Capture the log and the flight tape before doing anything else.
- A unit is flagged at the start of the next battle: the enter sweep did not run.
- The watchdog line appears: a release condition was missed. The tape names which.

Status stays AWAITING-LIVE until the owner runs this. Only the owner flips it.

## Deferred to v2

- **Per-enemy time-slicing**: arming at the start of the provoked enemy's turn only, so intervening
  enemies are never redirected. Needs the reveal-deadline measurement (whether the AI latches its
  target at turn open) and turn-queue lookahead, and it is a strict upgrade to this contract rather
  than a change to it.
- Hiding the reddened HP bar a flagged ally shows, and suppressing the status icon.
- A per-battle use cap, if uncapped play proves degenerate.

## Open questions (do not block this arc)

- Can the player still select and target their own units while flagged? Not exercised. Criterion 5
  makes it moot in v1 by never flagging during your own turns, but the answer is needed before any
  design that holds the flag across a player turn.
- Do caster and archer enemies funnel the same way? The premise row observed melee only.
- Does a mid-hold autosave persist the composed bit into the save?
