# TODO / ideas

Stuff I want to try next. Rough order, not strict priority. Some from the Reddit
thread, some from digging through the game's data tables. Half design notes, half
"go check if this is even possible."

## Full weapon rework pass (BIG — top priority, do with the new cast knowledge)

Re-do every weapon now that the cast vector is wide open. Confirmed this session: the ENTIRE
ability pool with **id <= 255** is castable on hit (formula 2 + OptionsAbilityId). ~150
abilities — black/time/offensive-white magic, summons (single-target), draw-out, geomancy,
monk, steal, talk, Mystic, **Dark Knight Swordplay** (holy-smite hits HARD + dark HP/MP-drain),
**Rapha/Marach Truth skills** (cast as ONE collapsed elemental hit, not the multi-panel spread).
Free on PA*WP weapons (sword/spear/crossbow/rod); rescales the strike on knife/staff/pole.
Casts hit the struck enemy only; AoE + multi-hit collapse to one tile. **id >255 is hard-walled**
(byte field — that's the real reason "monster abilities" failed; generate.py now guards it).
Tooltip "May Cast: X" = the castability oracle for <= 255.

Archetypes to spread across the arsenal (keep <= 2 per gimmick): %HP executioner (Gravity 42),
stat-sunder (Rend line), MP-burn (Empowerment 47), petrify-gambit (Induration 59), holy-smite
(Judgment Blade 155+), truth-blade (Rapha, single hit), katana-cast (Draw Out) — plus the
already-confirmed formula vectors: 67 missing-HP berserker, 99 Speed-weapon, 4 Faith/magic
weapon, status procs, dispel (Cancel 55), Rush knockback. Re-balance WP/evade/element/proc and
re-pass the dominance gate per category; fold in re-pricing + shop re-timing while we're in there.

Lore / signature weapons to slot in during the pass:
- **Gaffgarion's Blade** (KnightSword) — formula 2 + cast **Sanguine Sword (45, "Absorb HP from
  the target")**, the HP-steal Gaffgarion spams every battle. Dark theme; convert a KnightSword
  slot (e.g. the crimson Ravager, id 49). id 45 <= 255, cast confirmed.

## Offensive Chemist — SHIPPED 2026-06-01 (price still pending)

Built 5 status grenades from the 5 Remedy-covered single-cures (item ids 246-250): Venom Flask
(Poison) / Smoke Bomb (Blind) / Oil Flask (Oil) / Hush Vial (Silence) / Sludge Bomb (Slow), via
ItemConsumableData Formula 56 + StatusEffectId -> an ADD-type ItemOptions row. Renamed
(patch_grenades.py), recolored (recolor_icons.py), staggered Ch1/Ch2/Ch3/Ch4, and Remedy bumped
to Ch1 (it became the only cure). Mechanic + availability confirmed in-game; Remedy still covers
all 5 ailments so curing is intact.

Remaining:
- PRICE: stuck at vanilla 50/100 g. ItemData `<Price>` is silently overridden by the nex 'Item'
  table for consumables (ShopAvailability is NOT — that one sticks). The real price lives in the
  base `item.nxd`, which is NOT extracted (only item.en.nxd is, and price isn't in it). To set
  grenade prices (Sludge 1000 etc.) we must extract + edit the base item.nxd -- the SAME table
  the "re-price everything" pass needs, so do them together.
- Try DAMAGE items too (bomb / elemental flask) -- vanilla has no offensive-damage consumable,
  so the damage-consumable formula needs a test.

## Lost-HP berserker weapon (want this one)

Damage scales off missing HP: max HP minus current, so the lower I drop the harder I
hit. A real desperation / glass-cannon weapon. Should actually work too, because it's
pure self-stat. The magic-sword style formulas are all dead on weapons (they read an
ability value that's zero on a plain swing), but this one doesn't. Plan: rig one
weapon, confirm the formula in a fight, then pick which weapon(s) carry it.

## Other weapon / formula ideas

- A no-shield, huge-WP greatsword using ForcedTwoHands (the flag we just proved on the
  Claymore) so the big damage actually costs you the off-hand.
- "Cancel" on-hit weapon that strips the enemy's buffs (Haste/Protect/Reflect). We only
  do debuffs right now, never this.
- Chaos weapon that rolls a random status on hit (Sleep/Slow/Confuse/Blind), or a
  debuff-shotgun that rolls each one independently. (ItemOptions has Random-type rows, so
  this is live — not the dead end I first guessed.)

## Cast-vector weapon concepts (hunt 2026-06-01 — full regular ability pool is castable)

Confirmed-firing, build now (formula 2 + ability id):
- Equalizer: Gravity (42) %-MaxHP on hit — tank/boss-buster, ignores defense, WP irrelevant.
- Manabane: Empowerment (47) MP-drain on hit — anti-caster; full strike + free MP denial.
- Counter+Order retune: StatusEffectData global dial (Counter = durations, Order = tick resolve
  priority — Order is completely untouched). Zero weapon edits.

One equip-screen tooltip-check away (genuinely new vectors):
- Wrecker's Maul: Crush Weapon/Armor/Helm/Accessory (160-163) = PERMANENT gear destruction on hit.
- Witherblade: Level Drain (289) = compounding permanent stat attrition; rewards a long fight.
- Gravewake: Zombie (239) = inflict Undead (recovery hurts them, Holy doubles); combos with the
  Mending Staff (heal=damage to Undead) or a Holy blade.

Bigger swings (other tables, need one test):
- Status grenades: ItemConsumableData StatusEffectId = an ItemOptions pointer -> throwable
  Sleep/Slow/Blind for any Item-command job.
- Oilskin Mail: Always:Oil cursed armor (tanky everywhere, but 2x Fire). Downside-dial gear.
- Live traps: MapTrapFormationData — arm the dead treasure tiles (Deathtrap/SleepingGas/Degenerator).

- VETOED: KO-on-hit blade (proc 41) — already ruled too OP, don't revisit.
- GATE on every "Initial:/Always: X" curse-gear: vanilla only ships Invisible/Reraise/Stone as
  StartingStatus, but Undead + Faith DO ship as InnateStatus (Always:). Each needs one
  equip-screen "Initial:/Always: X" check to confirm the field accepts that status enum.

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

- Status duration (the `Counter` field, one number per status) sets how long Sleep/Slow/Poison/
  etc. last. Global, both sides, but a clean balance dial for the whole utility lane without
  touching a single weapon. Confirmed editable via StatusEffectData.xml.
- Deeper status edits if I'm brave: cleansing rules (Haste cancels Slow), resolution order,
  no-stack groups. Higher risk, lots of opaque flags — and several of the bits do nothing
  reachable (see dead ends).

---

## Settled this session

- Innate Parry: dead end. Parry's evade only works from the reaction slot, so going innate
  would bury everyone's Counter. Shipped the class-evade dodge dial instead.
- Make reach cost something: done. ForcedTwoHands is the off-hand flag (Claymore and
  Wyrmpike are two-handed now). Reach-3 is impossible, the engine caps melee at 2.
- Bake abilities into weapons: not possible in pure data. Only job-innate grants abilities,
  and that's per-job, not per-weapon.
- Faith-scaling weapon = formula 4 (the "magic gun" elemental attack), and it already exists
  in the mod as the rod + gun lane. Confirmed in-game 2026-06-01: damage = weapon Power scaled
  by Faith (caster x target); the wielder's MA is irrelevant (a 76-Faith mage and a 75-Faith
  Ramza hit the same target for 141 vs 140 — one point of Faith, one point of damage). So the
  magic weapons ARE Faith weapons; descriptions now say "scales with Faith." It's elemental
  MAGIC (ignores armor, two-sided Faith), not a physical holy blade.
- Boost item/potion effects from gear: impossible (DLL-only). Potion heal is a statless Z*10
  in ItemConsumableData; no weapon/equip field touches the Item command. Global potion
  rebalance (bump the Z byte) is the only lever. See the consumable memory note.

## Dead ends, don't chase

- Weapon formulas that multiply by an ability value (magic-sword, multi-hit, mana-burn,
  gravity): all zero on a plain swing.
- MA-scaling melee weapon ("a mage who hits hard in melee"): impossible. No weapon formula
  scales off the user's Magick Attack — the MA*Y formulas zero out (Y=0), and formula 4 keys
  off weapon Power + Faith, not MA. Tested 2026-06-01 (high-MA mage matched lower-MA Ramza).
  Materia Blade was reverted from the f4 experiment back to a physical Dark blade.
- Physical Faith/Brave blade ("a sword that hits harder the more faithful/brave you are"):
  no formula multiplies a physical PA*WP strike by Faith or Brave. Faith only scales MAGIC
  (formula 4 / spells). The Faith fantasy only exists in the magic-attack form, not physical.
- Self-buff-on-strike ("hit an enemy, YOU get Regen/Reraise"): dead. On-hit procs ALWAYS land
  on the struck enemy — confirmed in-game 2026-06-01, a buff proc (Haste/Shell/Protect/Regen/
  Reraise) buffed the HIT ENEMY, not the wielder. Buffs on yourself come only from passive
  EquipBonus Always:/Initial: status gear, never on-strike.
- Monster abilities from a weapon ("cast a dragon's breath / Bad Breath on hit"): walled off.
  Tested 2026-06-01 — Doom (304) and Fire Breath (338) cast via formula 2 showed no "May Cast"
  tooltip and never fired (same as Tri-Attack 340). Weapon casts only reach the regular ability
  pool. Oracle: no "May Cast" tooltip on the equip screen = it won't fire, so no battle needed.
- AbilityData range/power/formula/AoE/MP: those live in the nex Ability table, a separate
  pipeline, not the XML we edit.
- No-wake Sleep: tested 2026-06-01. Copied Slow's exact CheckFlags onto Sleep (and dropped
  Sleep's own grouping bit); it still woke on the first hit. Wake-on-damage is hardcoded by
  status type in the damage routine, not a flag we can clear. Per-weapon status variants
  don't exist either — status data is one global row per status.
