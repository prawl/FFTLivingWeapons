# TODO / ideas

Stuff I want to try next. Rough order, not strict priority. Some from the Reddit
thread, some from digging through the game's data tables. Half design notes, half
"go check if this is even possible."

## Lost-HP berserker weapon (want this one)

Damage scales off missing HP: max HP minus current, so the lower I drop the harder I
hit. A real desperation / glass-cannon weapon. Should actually work too, because it's
pure self-stat. The magic-sword style formulas are all dead on weapons (they read an
ability value that's zero on a plain swing), but this one doesn't. Plan: rig one
weapon, confirm the formula in a fight, then pick which weapon(s) carry it.

## Other weapon / formula ideas

- MA-scaling melee weapon so a mage hits hard without switching job (only if the slot
  is MA times WP, not MA times an ability value, which is the dead kind).
- Faith or Brave scaling blade (a holy sword that hits harder the more faithful you are).
- A no-shield, huge-WP greatsword using ForcedTwoHands (the flag we just proved on the
  Claymore) so the big damage actually costs you the off-hand.
- "Cancel" on-hit weapon that strips the enemy's buffs (Haste/Protect/Reflect). We only
  do debuffs right now, never this.
- Chaos weapon that rolls a random status on hit (Sleep/Slow/Confuse/Blind), or a
  debuff-shotgun that rolls each one independently.
- Weapon that buffs the wielder on strike (Regen/Reraise) instead of debuffing the enemy.

## Gear: status and elements

- Phoenix-charm accessory: starts the battle with Reraise, auto-revives once. One row
  to write, vanilla already has it. Check whether it re-applies every battle.
- Status-immunity accessories: anti-Charm, anti-Stone, anti-Doom, anti-Don't-Move.
  Counter-picks against enemy status spam. None of these exist in the mod yet.
- Innate-status gear: a sword with permanent Haste (glass cannon), a ring with Reflect
  (mage-killer), Float boots (immune to Earth and ground traps). Reraise/Regen/Faith on
  the table too.
- Ambush cloak: start every fight Invisible (assassin opener).
- More elemental armor: per-element absorb ("drink Fire" demon mail) plus more
  halve/nullify lanes. Can also use Weak-to-X as a cost to pay for an over-strong item.

## Mobility

- Move+1/+2 and Jump+2 accessories as a real axis (kite builds, vertical maps).
- Bake Move/Jump into the job itself (skirmisher moves far, heavy moves short) so it
  doesn't cost the movement slot.

## Class identity

- Free innate passives on jobs: every job has 4 innate slots and we ship zero. Drop a
  passive or two on the flat generics (Counter, Move+1, Defense) so they have a reason to
  exist, without taxing the player's own slots. Biggest bang for the effort.
- Stat multipliers per job (PA/MA/Speed/HP) for real archetype separation: Knight high PA,
  Mage low PA, fast thief, etc. Powerful, so tune carefully.
- Multi-reaction builds: we already proved you can move a reaction into the Support slot
  and it still fires. Could ship it as a real feature (two reactions at once).
- Maybe: gate signature skills to learn-on-hit (blue-mage style), or reshape the job-tree
  prerequisites.

## Progression and economy

- Re-price everything. We changed power but kept vanilla prices, so cost-per-power is
  broken. Re-pricing adds opportunity cost and gates power behind gold. Low risk.
- Re-time shop stock so the raw / agile / utility weapon lanes unlock in step per chapter.
  Maybe regional shop identities (Goug = tech, Limberry = dark).
- RequiredLevel to stop a poached endgame blade equipping at level 5 (confirm IVC actually
  enforces it).
- Rebalance consumables. The whole potion / Phoenix Down / throw economy is untouched, and
  the X-Potion blanket heal is probably too good.
- Re-seed map treasure at the rebalanced items so exploring is a loot path.

## Environment

- Map traps: arm tiles with Deathtrap, Sleeping Gas, etc. Positioning starts to cost
  something, and suddenly Float boots have a reason. Needs testing.

## Status system tuning

- Proc duration (one number) sets how long our Sleep/Charm/Don't-Move last. Tunes the whole
  utility-knife lane without touching a single weapon.
- Deeper status edits if I'm brave: a Sleep that doesn't wake on damage, cleansing rules
  (Haste cancels Slow), resolution order. Higher risk, lots of opaque flags.

---

## Settled this session

- Innate Parry: dead end. Parry's evade only works from the reaction slot, so going innate
  would bury everyone's Counter. Shipped the class-evade dodge dial instead.
- Make reach cost something: done. ForcedTwoHands is the off-hand flag (Claymore and
  Wyrmpike are two-handed now). Reach-3 is impossible, the engine caps melee at 2.
- Bake abilities into weapons: not possible in pure data. Only job-innate grants abilities,
  and that's per-job, not per-weapon.

## Dead ends, don't chase

- Weapon formulas that multiply by an ability value (magic-sword, multi-hit, mana-burn,
  gravity): all zero on a plain swing.
- AbilityData range/power/formula/AoE/MP: those live in the nex Ability table, a separate
  pipeline, not the XML we edit.
