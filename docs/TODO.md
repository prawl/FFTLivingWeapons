2.0 Release TODO's
- Dual Wielding the Zwill Straightblade and a Rod did not proc Bloodlust
- Fix the Materia Blade
- Fix the Chemist learning the new offensive items
- Does the Sunderer destroy multiple pieces of gear?
- Remove Atheist from Reposte
- Cursed Ring needs the desc to literally say +2 AP
- Fix the Kill count fluctuating wildly
- Determine which weapons need the shop treatment, poaching treatment, etc
- Thorough sweep of other mods
- Create a changelog of all the new mechanic changes
- Fix the P3 descriptions
- When using the new chemist items there's a tag that still says the old items name when you're targeting a tile to use.


Later TODO's
- Fix the coloring on all icons



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


- When reviving an ally heal % amount of health back on revival
- When hit with an element, gain resistance to that element for x turns
- Health gain from healing spells increased by X%
- When health is below 10% become immune to physical attacks for 3 turn
- Buffed Regen, it heals the unit and others around them
- Damaging enemies with Wands will restore mana
- Defeating enemies with Magic will restore some life
- Makes Spell Casting Instant for X turns
- Swap Mana with a Target

Needs Exploration
- Steal a buff from an enemy
- On Successful Parry gain X (mana/health)
- Reduce the targets level on hit
- Doing something temporarily increases evasion by % for X turns
- Turns the user into a Chocobo the ChocoBow
- Summon a friendly companion (I really want this to work)
- While "wet" or the map is raining gain strength
- Attacking an enemy from behind does X at %
- While Standing Next to a Friendly Unit Gain X
- Soul Link: Taking Damage also hurts X enemy for Y turns
- Soul Link: Healing also heals X player for Y turns.
- Increases throwing damage by X for Y turns
- Swap HP with a Target
- Make the target flee in terror (disable all abilities?)

Scrapped
2. PROVEN: Give two counter abilities we know work together.
3. Knockback probe (same session): write a victim's gx/gy one tile, see if the engine accepts.





  Rod: Wellspring (T1)
  +3 Signature: EssenseFont — Lifefont + Manafont at once
  From your list: EssenseFont
  Tech reality: A wellspring granting two fonts is name-destiny. One probe needed: OR both movement bits (+0x9C field). Docs say the menu
  honors
     only ONE movement ability, so if the engine ignores the second bit, fallback is Manafont via the slot + emulating Lifefont with  an HP
    write on move (HP writes are proven).
  ────────────────────────────────────────
  Rod: Hushward (T2)
  +3 Signature: Below 30% HP → Master Teleport for 3 turns
  From your list: your new idea
  Tech reality: Cheapest ship on the board — every piece is already proven (Adrenaline's HP gate + the Teleport movement hold + the turn
    window). The panic-blink mage.
  ────────────────────────────────────────
  Rod: Umbral (T3)
  +3 Signature: Magic kills restore HP
  From your list: defeat-with-magic
  Tech reality: Second cheapest — it's literally KillTracker + a self-HP write, both shipped. Dark mage feeding on souls; perfect identity
    match.
  ────────────────────────────────────────
  Rod: Dragon (T4)
  +3 Signature: Buffed Regen — wielder gains Regen, and its ticks also heal adjacent allies
  From your list: buffed Regen
  Tech reality: Medium: needs the Regen status-bit probe (the poison/doom watchspan recipe makes that routine) + Ricochet's tile math with the
    sign flipped. Completes the "survivable battle-mage" kit with the Reraise base.
  ────────────────────────────────────────
  Rod: Rod of Faith (T5)
  +3 Signature: Revive an ally → they return with +X% extra HP
  From your list: revive heal-back
  Tech reality: Already feasibility-cleared in LIVING_WEAPON_GRANTS.md, and the runtime already detects revival edges in Corpses.cs. "The
    faithful rise restored."
  ────────────────────────────────────────
  Rod: Spark/Ember/Frost
  +3 Signature: No runtime signature — their buff is today's data pass (below)
  From your list: —
  Tech reality: Growth-only keeps the curation tight; they're now ranged artillery, which is its own survivability (not standing next to the
    thing you're nuking).

- Solve how to add more items than the hard-limit will allow.  See C:\Users\ptyRa\Dev\FFTItemOverhaul\docs\ITEM_CAP_261_BREAK_JOURNEY.md
- When Items are broken from Rend attacks, just put them back into the inventory, you don't lose them forever 