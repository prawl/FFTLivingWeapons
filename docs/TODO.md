2.0 Release TODO's
- Thorough sweep of other mods
- When using the new chemist items there's a tag that still says the old items name when you're targeting a tile to use.


TODO's
- Ramza's Squire cannot equip Shields but normal squires can?
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


Needs Exploration
- When reviving an ally heal % amount of health back on revival
- When hit with an element, gain resistance to that element for x turns
- Health gain from healing spells increased by X%
- When health is below 10% become immune to physical or magical attacks for 3 turn
- Buffed Regen, it heals the unit and others around them
- Damaging enemies with Wands will restore mana
- Defeating enemies with Magic will restore some life
- Makes Spell Casting Instant for X turns
- Swap Mana with a Target
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
- Master Treasure Hunter: Use the in game tile marking feature to mark all of the traps and treasure automatically

2.X feature: Treasure Master (auto-mark trap/treasure tiles in battle)
- Native-mark hijack is WALLED (see docs/LIVE_LEDGER.md) -- the mark has no writable store.
- Buildable architecture: (1) author trap_treasure_tiles.json per map (122 maps, fixed
  placements, guide-documented -- a data pass like the obtain column); (2) detect the live
  map (FFTHandsFree DetectMap fingerprinting is done); (3) display via the DLL's own paint
  (the kills-card painter) keyed off cursor/tile-index addresses in the ledger.
- Probe + findings: tools/probes/mark_probe.py; cursor (x,y)=0x140C64A54/0x140C6496C proven.

Open offers (standing, dated — so they stop rotting in handoff prose)
- (2026-06-10) Tally surgery: kills.json on the live install carries 3 phantom credits from
  since-fixed bugs (Scoutbolt +2, Wellspring Rod's entire 1). Say the word and they're removed.
- (2026-06-10) Changelog + FAQ for the 2.0 release — half-written in session transcripts already.
- (2026-06-11) CharmLock CT-expiry probe: watchspan a locked enemy's auth-copy +0x25 across its
  turns to settle the contradiction in docs/LIVE_LEDGER.md (is the N-turn unlock dead code?).

Scrapped
2. PROVEN: Give two counter abilities we know work together.
3. Knockback probe (same session): write a victim's gx/gy one tile, see if the engine accepts.

