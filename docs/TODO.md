Release TODO's
- Fix the battle with Oran (user rported it was too difficult)
- Replace the Stormbrand (it's sorta lame)
- Sanctus Staff test potions and Regen
- Ramza's Squire cannot equip Shields but normal squires can?
- Larcency keeps popped up in the logs despite not being equipped




New Buffs Exploration
1. PROVEN: Can add two support abilities 
3. PROVEN: Add a new ability (e.g. Sanguine Sword) to a weapon.
4. PROVEN: Can change movement from Move to Teleport mid-battle for a limited duration.  On X give the unit M Teleportation for x turns. 
5. PROVEN: Adrenaline — drop below 30% HP → Attack Boost + Move+2 for 3 turns (a desperation surge).
6 PROVEN: Charm-Lock - Casting charm does not break for 3 turns
6 PROVEN: Take another turn now.  When killing a unit, immediately take another turn.
7 PROVEN the enemies Reactions
8. PROVEN Ricochet  Stormarc id 86 hosts it as "Arc Lightning" — on a damage event from the +3 wielder's action, chip the nearest other enemy within 3 tiles for 50% of the
9. PROVEN Barrage: parked on two decisions (job-wide vs per-unit, and the blank-name problem).
10 PROVEN Give Spiritual Font: Lifefont and Manafont to a single character

Ideas:

Discipline: Compare Bra between your unit and the enemy you just hit, taking the higher of the two as your own.
Retain broken equipment

Needs Exploration
- Steal Identity: Copy the enemies stats in battle
Guardian's Oath 🛡️ — when an ally next to the wielder takes a lethal hit, redirect it to the wielder (hold the ally's HP up, drop yours). HP-holds + position reads + death detection, all proven. The bodyguard blade.
Unlock Potental: Add a random ability to an allied neighbor
Increase damage by how high a character is in the game
- The next attack is buffed for one turn
- When reviving an ally heal % amount of health back on revival
- When hit with an element, gain resistance to that element for x turns
- Health gain from healing spells increased by X%
- When health is below 10% become immune to physical or magical attacks for 3 turn
- Buffed Regen, it heals the unit and others around them
- Damaging enemies with Wands will restore mana
- Defeating enemies with Magic will restore some life
- Makes Spell Casting Instant for X turns
- Swap Mana with a Target
- On Successful Parry gain X (mana/health)
- Reduce the targets level on hit
- Doing something temporarily increases evasion by % for X turns
- Turns the user into a Chocobo the ChocoBow
- Summon a friendly companion (I really want this to work) — INVESTIGATED 2026-06-16: the scheduler
  ADOPTS a hand-written unit (it enrolls in the Combat Timeline, seats 16–27) but it renders FACELESS —
  the drawable identity is an external init-built graphic object, not a forgeable pointer. WALLED without
  a debugger (see UNIMPLEMENTED_MECHANICS.md + LIVE_LEDGER.md). Feasible alt: reanimate a fallen ALLY
  (its own face), proven FeignDeath/Reraise path.
- While "wet" or the map is raining gain strength
- Attacking an enemy from behind does X at %
- While Standing Next to a Friendly Unit Gain 
- Soul Link: Taking Damage also hurts X enemy for Y turns
- Soul Link: Healing also heals X player for Y turns.
- Increases throwing damage by X for Y turns
- Swap HP with a Target
- Make the target flee in terror (disable all abilities?)

Scrapped
2. PROVEN: Give two counter abilities we know work together.
3. Knockback probe (same session): write a victim's gx/gy one tile, see if the engine accepts.

