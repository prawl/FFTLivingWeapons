# User Feedback

STATUS: CONTRACT (curated player feedback ledger)

Playtest feedback log -- raw observations from real sessions, often with other mods
loaded (Harder Story Battles, a randomizer, "True Swordsman"). The observations are the
player's; parenthetical scope/triage notes are added during capture and not yet verified.

## 2026-06-21

### Movement items (Move +1) feel overpowered
- Added Move on gear is very strong. Ramza with **Trailwarden** + **Wayfarer Boots** can
  cross half the battlefield in a single move.
- Trailwarden is an early item, so the "zoom all over the stage" power arrives far too soon.
- In the cadet fight (with Harder Story Battles), both the enemies and the starter units
  are all running it, so the entire field is hyper-mobile.
- Request: nerf added-Move on items.

### Early armor with situational / "useless" riders
- A lot of early armor carries riders that feel useless at that point in the game.
- Example: a Ch2 armor piece that absorbs and boosts Holy. The only Holy sources seen early
  are Priests and Excalibur, so the rider does nothing for most of the run.
- It reads like a "penalty" item -- as if you're meant to hold that early piece for a future
  Excalibur user rather than equip it now.
- (Hasn't tested end-game yet; this is an early-game pacing impression.)

### Damage scaling / build power
- "True Swordsman" grants Doublehand. A **Dragoon** that can equip swords hits **100+**
  easily, even into Knights with an innate defense boost. Two-handed swords on an
  off-class striker may be over-tuned.

### Mustadio shows up without a gun (likely NOT our mod)
- Mustadio joined as a guest (level 18) unable to equip any weapon at all until he becomes
  a permanent party member.
- That run's gear came from the randomizer mod, so this may be a randomizer / guest-unit
  interaction rather than ours. Needs confirmation before assuming it's our mod.

### Living Weapons + Harder Story Battles interaction
- Loves that enemies also get the Living Weapons system. Wants enemies to **hit harder** and
  to actually **use** the living-weapon benefits if possible.
  - _Scope note (verified against code 2026-07-04): enemies inherit only the STATIC item rebalance --
    the reworked weapon/armor stats + riders baked into the item tables, which every equipper gets on
    restart. They do NOT get the living-weapon RUNTIME: the kill tally, the `+`/`+2`/`+3` stat lift,
    the awakened +3 signatures, and the Kills card counter are all player-roster-only (KillTracker
    credits "the acting player's weapon(s)"; GrowthEngine holds only player combat structs). So
    "enemies actually use the benefits" is a net-new feature -- deferred (see docs/RELEASE_SCOPE.md),
    not a tuning fix._
- On **Tactician** difficulty, New Game+ makes the chapel fight an auto-fail: Ramza is locked
  at level 12 while the rest of the field is level 50+ with 50+ living-weapon items. Effectively
  impossible -- had to drop to **Squire** to get past it and continue NG+.
- Outside that, the **cadet fight** is the hardest in the game: enemies outscale your units even
  though both sides have the same gear, because Ramza starts at level 1 with basic equipment.

## 2026-07-21 (after downloading 2.3.0)

### Only Ramza and generics benefit from the LW system
- Special/story units appear excluded from the living-weapon runtime. (Triage: [LW-96] in
  docs/TODO.md; needs a repro to pin which surface skips them.)

### Equip-axe is back on Squires
- Reported as a regression. (Triage: [LW-97]; check the emitted tables vs another mod's stomp.)

### Fists show the previous unit's weapon name
- An unarmed unit's Attack row wears the weapon name of the unit that acted the turn before.
  (Triage: [LW-98]; likely the LW-91 stale-paint lifecycle with nothing to overwrite it.)

### Nagrarok missing (separate report)
- Nagrarok was equipped on Beowulf and "turned into another sword". (Triage: [LW-99].)

### Positive
- Loves the new changes: the weapon name replacing "Attack" in the menu "feels nice", and the
  new +3 for Outrider is "awesome".