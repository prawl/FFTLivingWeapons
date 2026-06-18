Release TODO's
- Replace the Stormbrand (it's sorta lame)
- Sanctus Staff test potions and Regen
- Ramza's Squire cannot equip Shields but normal squires can?
- Larcency keeps popped up in the logs despite not being equipped
- BUG: enemy Knights cast Shadow Blade without the Sanguine Sword. The Shadow Blade grant (Sanguine Sword id 23, ability 165) is injected into the Knight/Squire/Gallant Knight JobCommand record, which is JOB-global, so every unit of those jobs shows Shadow Blade as Learned regardless of equipped weapon (enemies included). Seen live: enemy Knight "Dyana" holding the Arcanum used Shadow Blade. Same class as the Barrage enemy-Thief leak, but Knights are a common enemy job so it is far more visible. Fix: gate the injected command to the actual wielder (per-unit), or restrict the grant to rare/unused enemy jobs, or remove the command-grant and use a different signature.
- PUPPETEER (#11) LUCAVI/BOSS CARVE-OUT: the gate is currently ALLOW-EVERYONE (`IsDominatable => true`, by user request) — so bosses/Lucavi ARE dominatable by design. We do NOT want Lucavi dominatable. The `maxHp >= 2000` latch-loop cap does NOT exclude them (a live Lucavi read 999 max HP), and it's only a garbage-read sanity cap anyway — do not lean on it. Need a real carve-out keyed to job-id band and/or name-id (the long-standing "Lucavi carve-out" — IC Lucavi/boss job ids still need mapping). Costs in-game testing time to identify the ids; deferred until we can spare it. Until then, allow-everyone ships and a Lucavi CAN be puppeted.




New Buffs Exploration
1. PROVEN: Can add two support abilities 
3. PROVEN: Add a new ability (e.g. Sanguine Sword) to a weapon.
4. PROVEN: Can change movement from Move to Teleport mid-battle for a limited duration.  On X give the unit M Teleportation for x turns. 
5. PROVEN: Adrenaline — drop below 30% HP → Attack Boost + Move+2 for 3 turns (a desperation surge).
6 PROVEN: Charm-Lock - Casting charm does not break for 3 turns  → REPLACE with Puppeteer (#11); current charm is broken
6 PROVEN: Take another turn now.  When killing a unit, immediately take another turn.
7 PROVEN the enemies Reactions
8. PROVEN Ricochet  Stormarc id 86 hosts it as "Arc Lightning" — on a damage event from the +3 wielder's action, chip the nearest other enemy within 3 tiles for 50% of the
9. PROVEN Barrage: parked on two decisions (job-wide vs per-unit, and the blank-name problem).
10 PROVEN Give Spiritual Font: Lifefont and Manafont to a single character
11. PUPPETEER (signature; victim status "Puppet") — REPLACES Charm-Lock/Galewind (#6; vanilla charm is broken, this is strictly better: real menu control vs flaky charm-AI). Enemy-control PROVEN LIVE 2026-06-18. LOCKED DESIGN: reliable on a +3 weapon hit (NO rng) → puppet the struck enemy for its NEXT turn (full move + skillset), revert to AI at the turn boundary; ONE puppet at a time + 3-turn cooldown (counts the WIELDER's own turns); target gate = NO bosses/special/monster-class (job-id gate); NO hp gate, NO level gate (silent level-fail = bad UX); +/+2 = stat growth only (only +3 carries the ability). Class Puppeteer.cs + Puppeteer.Policy.cs. Build order: START with the boss/monster job-id gate as a pure policy + tests. Also the multiplayer primitive (see Dev/FFTMultiplayer). On hit by the +3 wielder, set bit 0x08 at the struck enemy's combat struct +0x05 → full MENU control of that enemy: move + its ENTIRE skillset (verified live casting Fire on its own allies; unit stays team-1 so it can turn on its own line). One write PERSISTS across turns (authoritative struct holds itself — no per-tick fight). Build as a CharmLock/Maim clone: on latch save the original +0x05 byte, own it, then RELEASE after N of the victim's turns (CtTurns off +0x09) by writing the saved byte back → AI (permanent variant = never release; battle-exit struct-rebuild cleans up). Flag: combat +0x05 bit 0x08 (SET=human / CLEAR=AI). CombatAnchor 0x141855CE0, stride 0x200; locate the victim via the usual lvl(+0x29)/brave(+0x2A)/faith(+0x2C)/weapon(+0x20) fingerprint. Mechanism found via Dicene's `fftivc.handsfree` mod (does the INVERSE — clears 0x08 to AI-ify the player team — and SIGSCANS the struct, so it's 1.5-proof; decompiled source in Downloads/FFT_-_HandsFree1.0.0/decompiled).

Ideas:

Discipline: Compare Bra between your unit and the enemy you just hit, taking the higher of the two as your own.
Retain broken equipment

Retain the last ability used on you.

Needs Exploration
- Weapon that unlocks a job early?
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

