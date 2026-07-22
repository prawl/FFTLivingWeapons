# Mechanics

STATUS: CONTRACT (confirmed vs unconfirmed mechanic ledger)

Consolidated mechanics ledger. Folds in the mechanic ideas/implementations from `docs/TODO.md` plus the
retired `docs/UNIMPLEMENTED_MECHANICS.md` (deleted; its content lives here now).
One bullet per mechanic. Two buckets: **Confirmed** (proven live and/or shipped) vs **Unconfirmed**
(idea / probe / moonshot / walled). Bug/chore items from TODO.md (Stormbrand swap, analyze.py gate
gap, Squire shield rule, Larceny log spam, Sanctus Staff tests) are NOT mechanics and stay in TODO.md.

---

## Confirmed (proven live and/or shipped)

### The 2026-07-10 breakthrough block (unit manipulation, all observed live that night)

> LEDGER STATUS (LW-107, 2026-07-21): every mechanism in this block was watched working by the
> owner on the night, and every one of them still sits in LIVE_LEDGER's **Uncertain** section
> (rows 100 to 104), each tagged "Ready for the PROVEN flip". Nobody flipped them. Read the
> bullets below as observed and reproducible, NOT as promoted: build on them the way you would
> build on a strong lead, and if you need one to be settled, ask for the flip rather than
> assuming it happened.

- MOVE ANY UNIT ANYWHERE mid-battle (full teleport + two-unit position swap) -- OBSERVED LIVE:
  the render position was the missing layer for a month (the Knockback wall); a coherent
  triple-write of combat +0x4F/+0x50 (logic tile, +0x51 bit7 layer), the render node's AI tile
  key +0x88/89/8A, and the node's world coords +0x4C/+0x4E/+0x50 (X=28x+14, Y=28y+14,
  Z=-12*(height +1 if Float); node via list head 0x140D3A410, +0x148 combat backref) moves a
  unit completely: it hovers, paths, and acts from the new tile, and the engine re-adopts every
  layer after its first real move. A live Ramza-with-enemy FULL SWAP (each keeping own facing)
  executed flawlessly, twice (tools/probes/swap_units.py). Only remaining guard for a shipped
  mechanic: a tile-occupancy check (co-tiled units = slot-order target shadowing + movement
  lock, observed live).
- FLOAT'S HOVER IS DATA / free visual levitation -- OBSERVED LIVE: Float's hover offset is one
  height unit (-12) in the node's world Z, not an animation; poking Z granted a hover to a
  non-Float unit and stripped it from him live, and a real Float unit rendered flat carrying a
  grounded Z (status intact). Transform ownership: idle = unowned (pokes stick), walking = the
  mover lerps per frame (pokes lose), turn-open/move-end = one-shot re-stamp from logic. A
  shipped hover should grant the real Float STATUS and let the engine draw it; Z pokes are for
  teleports and comedy. Purely visual: all combat math reads the logic layers.
- SPAWN A REAL AI FIGHTER MID-BATTLE (LW-58 Body Double, COMPLETE) -- OBSERVED LIVE 2026-07-10:
  DUPLICATE any hovered unit into a real, drawn, named, controllable combatant that DESCENDS FROM
  THE HEAVENS and FIGHTS AS A REAL AI UNIT with no crash. The data-only enroll chain (all
  observed live): copy the donor's combat struct into a vacant same-region slot at a FREE tile; clone the
  donor's battle-keyed AI registry object re-keyed to the host slot (+ count bump); cold-build the
  render node with the DESTINATION TILE as the build args (the node tile key is the AI's subject
  lookup) + scene-bind + own animB + donor identity stamps (+0x191/2 = name + control); AND THE
  ONE-BYTE COEXISTENCE KEY: the per-slot AI-ROSTER INDEX 0x141873038[hostSlot] = next free index
  (real units hold 0..7; an un-indexed clone reads 0xFF and the AI-subject arm skips it -> the
  facing code 0x150E74A5D null-derefs = the auto-battle crash). Baked as Canary 9 in the worktree
  BodyDoubleSpike. The clone is battle-scoped (does NOT persist: the desired temporary-summon
  semantic; a permanent recruit needs a save-roster entry, unbuilt). Two knowns to polish: it is
  AI-PASSIVE (steps + waits; its behavior row 0x1411A7D10+idx*0x258 is the donor's shadow -- give
  it a real AI-data row for aggression) and the decoy CT-hold must be released for it to get turns.
  Dev-spike only.
- DESPAWN ANY UNIT, SPRITE AND ALL (the reverse door) -- OBSERVED LIVE: ONE guarded byte
  (node +0x12C = mode 2) and the engine's own sweeper (0x14026E20C) removed a live enemy
  completely on its next unpaused frame, every predicted side effect byte-perfect (combat
  +0x01=FF, present +0x1B5=0x80, node done-marked 0x30, pool slot freed, list unlinked); the
  same primitive vanilla crystallization uses. Dev spike Ctrl+F5 wraps it (hover-fingerprint or
  ghost-orphan resolve, current-actor refused, timeout auto-revert); the per-id byte at
  0x140C6CFE0+id*9 is the "engine engaged" (hover/menu) marker, NOT a busy gate.
  Open: does the victory check stay sane after a removal (owner test pending).
- RESURRECT A REMOVED UNIT (the re-add; the arc's grail via the reverse door) -- OBSERVED LIVE,
  DATA-ONLY, same night: the despawned Knight was brought back mid-battle by (1) re-enrolling
  him in the AI registry (clone a living same-team object, re-key +0x2C to his slot, append at
  table[count], bump counts last), (2) reviving his intact node (element in-use dword = 1,
  clear the +0x12C done-mark, re-splice at the list head), (3) present = 1 then gate LAST, with
  a sky-descent flourish (world Z stepped -600 -> ground). The removal drops AI enrollment, so
  step 1 MUST precede visibility or the LW-58 freeze fires. Full byte recipe in the
  unit-despawn-resurrect memory. Together with the despawn this is MID-BATTLE REMOVE + RESTORE
  = the summon/reinforcement mechanic family (park a reserve, materialize on command).
- HIDE / REVEAL a unit's LOGIC live (the ghost-statue toggle) -- OBSERVED LIVE repeatedly: gate
  combat +0x01 = 0xFF removes the unit from every logic walk (untargetable, unhoverable, no
  turns, AI ignores it) while the render weld leaves its sprite standing; writing the model id
  back restores it whole. Reversible, instant, and the substrate of the Mirror Image idea. TRAP:
  a mid-hide autosave persists the hidden state into resumes.
- PLAY ANY ANIMATION ON ANY UNIT (the request register) -- DECODED byte-for-byte, one live poke
  from proven: node+0x10 u16 is the game's own animation-request API; one write plays any
  sequence from the enumerated vocabulary (idle, flinch, chant, crouch 0x35, stand-up, weapon
  swings, die) and LATCHES with no hold. The earlier failed pokes were all decoder OUTPUTS
  (node+0x420 block re-stamps per frame); this is the INPUT. Recipe + vocabulary in the
  anim-request-register memory.
- DOUBLE A UNIT'S IDENTITY (name + control) -- OBSERVED LIVE: the roster-identity backref pair
  combat +0x191/+0x192 routes field NAME resolution and controller ownership; copying a donor's
  pair makes another unit a literal double of it (a second "Ramza" on the field, owner-witnessed).
  The defeat check is NOT keyed on it (falsified live).

### Proven levers and buffs

- Add two support abilities to one unit -- PROVEN.
- Add a new castable ability to a weapon via JobCommand injection -- PROVEN/shipped (Sanguine Sword -> Shadow Blade, ability 165); the record is JOB-GLOBAL, so same-job enemies cast it too (known leak; fixes = turn-team gate at TurnQueue+0x02, restrict the grant to Ramza's Mettle records, or drop the command-grant entirely for a new per-unit write+hold +3 signature since the sword keeps its innate HP-drain Formula 6).
- Two counter (reaction) abilities can coexist on one unit -- PROVEN (the "give two counters that work together" idea; scrapped as a standalone signature).
- Change movement from Move to Teleport mid-battle for a limited duration -- PROVEN.
- Adrenaline: drop below 30% HP -> Attack Boost + Move+2 for 3 turns (desperation surge) -- PROVEN.
- Charm-Lock: cast Charm holds for 3 turns without breaking -- PROVEN (hold combat +0x49 status 0x20 AND +0x54 allegiance 0x20 on the authoritative struct; vanilla charm is flaky, superseded by Puppeteer).
- Extra turn: on a kill, immediately take another turn -- PROVEN/shipped (Zwill; scheduler CT = combat +0x41).
- Grant or suppress a unit's Reactions -- PROVEN (hold-zero combat +0x94 at trigger time suppresses; reaction grant via the passive bitfield +0x74).
- Ricochet (Stormarc "Arc Lightning"): on a +3 wielder damage event, chip the nearest other enemy within 3 tiles for 50% -- PROVEN.
- Barrage via JobCommand injection (Yoichi, Thief-only) -- PROVEN/shipped (parked decisions: job-wide vs per-unit, blank-name enemy-Thief spawn roll).
- Spiritual Font: Lifefont + Manafont on one character -- PROVEN.
- Puppeteer: a +3 hit sets combat +0x05 bit 0x08 -> full MENU control of the struck enemy for its next turn, revert to AI at the turn boundary -- PROVEN LIVE; one puppet at a time + 3-turn cooldown, boss/monster job-id gate (Lucavi carve-out still TODO; currently allow-everyone, so a Lucavi CAN be puppeted).
- Gun Slinger: write a 2nd gun to the roster off-hand slot +0x18 -> dual-wield twin-fire (each gun rolls its own shot/element) -- PROVEN LIVE; needs Dual Wield; write between battles, never while the PartyMenu is open.

### Shipped signatures and systems

- Living Weapon core: a weapon logs its kills, grows PA/MA/Speed by tier, awakens per-weapon signatures, and paints the tally on the equip card -- SHIPPED (the mod).
- Growth model = the engine's EquipBonus path (item-swap chain / rider at kill thresholds), NOT direct stat writes -- chosen; direct PA writes abandoned (battle-array +0x22 is recomputed each turn, raw +0x26 is ignored by the damage formula, exiting battle rebuilds the array).
- Choir (Warlock's Staff +3): bearer-only instant-cast via the Non-charge bit (band +0x7F mask 0x04) -- SHIPPED + LIVE-VERIFIED (dialed back from the original adjacent-ally aura).
- Kill attribution: cross-turn charged-summon kills and non-player-turn counterattack kills no longer mis-credit the stale player latch -- SHIPPED (still open: actually CREDITING the third-party counter-attacker, who never enters its own acted-period).
- Auto-revive via a Reraise EquipBonus rider (pure data; the Dragon Rod already carries it) -- shippable; granting real Undead status would import the Phoenix-Down-kills / healing-hurts downsides (not recommended).
- Bloodpact (HP-cost capstone, May-Cast host slot 219, Formula 0x42 self-recoil) -- PROTOTYPED + CONFIRMED WORKING LIVE, but PARKED: it is a Dispose re-skin (the animation rides the formula), overtuned (Y=16 -> ~200+ bonus damage), and the ability-nxd delivery was reverted for corrupting unrelated abilities (Fire range went map-wide, Red Mage lost abilities, new-game crashed).

---

## Unconfirmed (ideas, probes, moonshots, walled)

### Buildable now (levers proven; the signature itself is not yet built or verified)

- TRANSPOSITION / the displacement family (owner priority 2026-07-10; built on the proven
  full-teleport triple-write above): swap self with the target (Transposition strike), guaranteed
  Knockback/pull (shove the victim a real tile, sprite and all), reposition an ally (Rescue
  Throw). Cast wrapper = the JobCommand-injection lane + an action-record watch; the occupancy
  check is the one guard to build first.
- Mirror Image (owner concept, ledger LW-64; core premise PROVEN 2026-07-10): flip the unit's
  hide gate (+0x01 = FF) so a locked-on action WHIFFS at resolution (observed live: a mid-cast Slow
  resolved into nothing) while the render weld leaves the sprite standing = an untargetable
  after-image for a turn. Hazards mapped: restore-tile occupancy (solvable with the teleport
  primitive), autosave persists the hidden state (needs a battle-enter un-strand sweep), hidden
  units get no turns (external restore trigger); one side effect to chase (the whiff displaced
  the hidden unit one tile).
- Give Monks' Poles and make Claw weapons
- Turn the JP points into HP or a shield buff for HP
- Soul Ledger (Knight sword): each kill this battle stacks +1 PA (+1 Speed per 3rd soul, capped) -- our own kill-tally driving a live within-battle power gauge.
- Doppelgleam (Ninja blade): each strike borrows the max of own-vs-target PA/MA/Speed (StatHold sourced from the victim's bytes).
- Loaded Dice (Knife): hold Brave/Faith at a high floor so every reaction/Brave/Faith roll skews good (the legal stand-in for the walled crit roll).
- Sovereign's Decree (Knight sword): on a kill, charm every enemy within 2 tiles for its next turn so the cluster turns on itself.
- Preemption (Crossbow/gun): spike CT at battle-enter so the wielder takes the literal first turn regardless of Speed.
- Guardian's Oath (bodyguard blade): redirect an adjacent ally's lethal hit to the wielder (hold the ally's HP up, drop yours) -- HP-holds + position reads + death detection are all proven.
- Kobu (Katana, "rousing courage"): on a melee hit against a braver foe, raise the wielder's CURRENT Brave (+0x2B) to match (only climbs, never lowers; battle-scoped). Alt names Funki/Yuuki/Buyu; supersedes the old "Discipline" idea.
- Monk accessory-in-hand: give the Monk a 2nd accessory slot via the hand slot -- the engine ACCEPTS an accessory in the hand and the unit punches bare-fisted (confirmed live: Cursed Ring on Ramza), but stat-stacking with the off-hand accessory is NOT yet confirmed (harmless ROAR-sound quirk on the swing).
- Retain broken equipment (keep-broken-gear): snapshot/restore on the roster so a Broken/Stolen item survives the battle -- snapshot/restore timing proven, feature UNBUILT.
- Reanimate the fallen: raise a downed ALLY using its own already-built graphic (clear the Dead bit + hold HP>0 + held Reraise, the proven FeignDeath/Reraise path) -- the shippable answer to "summon a companion".
- Adaptive Living Weapon (evolve by kill TYPE, not just count): Fire kills -> gains Fire + a burn proc, caster kills -> learns Silence, low-HP finishes -> sharpens toward Executioner, so each player's blade ends up unique. Reuses the proven between-battles catalog rewrite; the SHIPPED growth is fixed per-weapon signatures grown by kill count/tier, so this playstyle-adaptive identity is the unbuilt original pitch.

### Probe (one byte / field to confirm)

- Frozen Cadence (time rod): hold Stop + pin the victim's CT at zero every tick = single-target time-stop. Probe: can CT stay pinned so the scheduler never ticks past it?
- Exile From the Hour (Ninja blade): hold the victim's CT massively negative -- alive and targetable but never seated again (non-lethal on-hit banish). Probe: does sustained-negative CT durably bench with no clamp?
- Reaper's Tithe (scythe-pole): brand a target; below a HP threshold stamp Dead + HP-zero to execute, then the mark hops to a new victim. Probe: does forcing Dead+HP0 mid-frame kill cleanly (no re-raise)?
- Tithe of Wards (rod/stick): strip one buff (Haste/Protect/Regen/Reraise) off a foe and WEAR it yourself (theft, not dispel). Probe: map the positive-status bits and confirm clear-here / set-there holds.
- Last Word ("retain the last ability used on you", gun): record the last ability an enemy used on you and re-cast it back at them (reflects physical skills true Reflect can't). Probe: does the recent-action field capture the attacker's ABILITY id?
- Bloodpact Tether (Blood Sword): soul-link to an ally; damage to either splits across both HP pools; if either hits 0, both fall. Probe: per-tick read-both / split / write+hold racing the engine's damage write.
- Phylactery Oath (Chaos Blade/Ragnarok): the first KO seeds a 3-turn timer, then snap back at full HP (later kills grow at half rate). Probe: can a unit be held KO'd-but-in-roster N turns without engine eviction?
- Knockback: write a victim's gx/gy one tile to shove it. UN-PARKED 2026-07-10: the old wall
  (renderer never re-derives from gx/gy) is beaten by the full-teleport triple-write (see the
  Confirmed lever at the top); absorbed into the Transposition/displacement family above.

### Moonshot (needs tech we do not have yet; each names its wall)

- Scorched Earth (Axe): the killing blow ignites your tile + 4 neighbors for 3 turns and the blaze creeps toward the nearest enemy. Wall: persistent terrain-hazard / live ground state (FFT has none).
- Tidewarden (Pole): low tiles flood one step higher each turn (drown + Slow grounded foes; your team gains Waterwalk). Wall: the static height map moving as a live clock.
- Glacier's Verdict (Knight sword): the kill ices every tile -- movers SLIDE until they hit a wall (collision damage), reactions off, CT halved for all but the wielder. Wall: terrain transform + sliding physics.
- Grave Conscript (bone staff): slain enemies rise as undead thralls on your side. Agency bit + Undead proven; the ALLEGIANCE FLIP is the walled grail (engine pool-relocation).
- Banner of the Slain (Spear, Holy Lance/Gungnir): plant the spear as a conjured totem that taunts adjacent foes (Berserk) and radiates Protect/Shell. Wall: the blank-render spawn (enroll already works).
- The Long Game (grimoire): every kill is a hidden move; at a secret threshold the tome resolves a board-wide cataclysm matching HOW you fought (magick kills -> Silence-storm; melee -> halve all PA). Wall: a playstyle read layered over kill-tracking + mass holds.
- World Ender (Flail): on a kill, a 3-turn Judgment -- enemy tiles crack to impassable damage, gravity reels their line toward you, sky-chip on anyone not adjacent, foes dying spawn spectral allies. Wall: terrain destruction + forced movement + field damage + visible summons (all walled).
- Summon / spawn a brand-new unit mid-battle: the scheduler ADOPTS a hand-written unit (enrolls in the Combat Timeline, seats 16-27) but it renders BLANK and the detail view AVs -- WALLED at the render layer (the drawable identity is an external init-built graphic object, not a forgeable pointer); needs a debugger. Ship "Reanimate the fallen" instead.

### Companion-driven set systems (need FFTHandsFree running as a live companion; design-only)

- Living Set (3-piece): Living Weapon (kills -> WP) is SHIPPED; Living Armor (hits taken -> HP) and Living Ring (magic damage dealt -> MA) are still design-only.
- Bonded Gear: twin weapons that link two units' states -- damage one and the other bleeds, share Haste, or run a shared HP pool (cross-unit synergy the equipment system cannot even describe).
- Set bonuses: equip a full named set -> an extra bonus (e.g. Reraise) by swapping one piece to a "[Set]" variant whose equipBonusId points at the bonus; remove a piece -> swap back. Needs one reserved item id per set (mind the 261 cap).
- Redundant-status bonus: if 2+ equipped pieces grant the same status (e.g. Protect), grant an extra EquipBonus (e.g. +1 armor) by swapping one piece to a "[redundant-X]" variant -- generalizes to any duplicated rider.

### Wishlist (loose ideas, unexplored)

- +3 becomes a super-unit only while mounted: stack bonuses (Attack/Def/Mag-Def boost) or double its stats (it takes 2 unit slots) -- Attack near Warbrand (~16+), Speed bumped too.
- Weapon that unlocks a job early.
- Steal Identity: copy the enemy's stats in battle.
- Unlock Potential: add a random ability to an allied neighbor.
- Increase damage by how far the character is in the game.
- The next attack is buffed for one turn.
- On reviving an ally, heal a % of HP back on revival.
- On an elemental hit, gain resistance to that element for X turns.
- Health gained from healing spells increased by X%.
- Below 10% HP -> immune to physical OR magical attacks for 3 turns.
- Buffed Regen that heals the unit and the units around them.
- Damaging enemies with Wands restores mana.
- Defeating enemies with Magic restores some life.
- Swap Mana with a target.
- On a successful parry, gain X (mana/health).
- Reduce the target's level on hit.
- An action temporarily increases evasion by % for X turns.
- Turns the user into a Chocobo (the ChocoBow).
- While "wet" or while the map is raining, gain strength.
- Attacking an enemy from behind does X at %.
- While standing next to a friendly unit, gain (bonus TBD).
- Soul Link: taking damage also hurts X enemy for Y turns.
- Soul Link: healing also heals X player for Y turns.
- Increase throwing damage by X for Y turns.
- Swap HP with a target.
- Make the target flee in terror (disable all abilities?).

Ideas:

MONK ACCESSORY-IN-HAND (new equip mechanic, discovered live 2026-06-26). Writing an accessory id into a
  unit's HAND slot is ACCEPTED by the engine: the unit attacks bare-fisted (a normal Monk punch), so an
  accessory in hand does NOT break the basic attack -- it just rides along, presumably granting its
  bonuses with no weapon. Observed live: Cursed Ring (id 222) placed in Ramza's main hand -> he punched
  normally. (Quirk: a monster ROAR sound fired on the swing -- the accessory's nonexistent "weapon graphic"
  indexes junk audio; cosmetic only, harmless.) DESIGN: give the Monk job a SECOND accessory slot by way of
  the hand slot -- a Monk-only perk (they don't use weapons anyway), trading the unused weapon slot for a
  2nd ring/armlet. Likely via JobData equip flags (let the Monk hand slot accept the Accessory class) or a
  runtime equip-write. TODO: confirm the accessory's stats actually STACK with the off-hand accessory (not
  just cosmetically equipped), decide the slot-rule implementation, and check the ROAR sound isn't tied to
  anything load-bearing. Pairs with the dual-gun off-hand equip-write recipe (item 12 above).

Kobu (Samurai sword / Katana signature) -- "rousing courage." On the wielder's melee hit, compare Brave
  between the wielder and the struck enemy; if the enemy's is higher, raise the wielder's Brave to match
  (only ever climbs, never lowers). Thematic AND mechanical bullseye on a Katana: katana damage =
  PA x Br/100 x WP, so every clash with a braver foe (battle-scoped) permanently sharpens the blade's own
  bite. Feasible as write+hold per-unit state: on the hit event read victim Brave and own Brave, write
  max() to the wielder's CURRENT Brave (combat struct +0x2B -- the displayed/effective copy; +0x2A re-
  normalizes and never displays, charm-style decoy one byte over). Battle-scoped climb (resets on battle-
  exit struct rebuild); an across-saves permanent variant would need the save-side Brave, not this byte.
  Name alternatives: Funki (rousing oneself to action), Yuuki (courage), Buyu (valor). Supersedes the old
  unnamed "Discipline" idea.
Retain broken equipment

Retain the last ability used on you.

Needs Exploration
+3 can turn you into a super unit but only while mounted. While mounted only you either get mutiple skills such as, att boost, def boost and mag def boost, or doubling some of its stats such as its att and def to act as 2 units since it will be taking 2 unit spots. Att could be near warbrands at 16 or higher?
speed could be increased alongside it too
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
- Swap Mana with a Target
- On Successful Parry gain X (mana/health)
- Reduce the targets level on hit
- Doing something temporarily increases evasion by % for X turns
- Turns the user into a Chocobo the ChocoBow
- Summon a friendly companion (I really want this to work) — INVESTIGATED 2026-06-16: the scheduler
  ADOPTS a hand-written unit (it enrolls in the Combat Timeline, seats 16–27) but it renders FACELESS —
  the drawable identity is an external init-built graphic object, not a forgeable pointer. WALLED without
  a debugger (see MECHANICS.md + LIVE_LEDGER.md). Feasible alt: reanimate a fallen ALLY
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


Signature Idea Bank -- creativity pass drafted 2026-06-22 (tagged by tech-distance). BUILDABLE = proven
write+hold / CT levers today; PROBE = needs one byte confirmed; MOONSHOT = needs tech we don't have yet
(persistent terrain state, blank-render spawn, allegiance flip). See memory dual-gun-equip-write-proven +
the proven-lever menu before building.

BUILDABLE NOW:
- Soul Ledger (Knight sword) -- each kill this battle stacks +1 PA (+1 Speed per 3rd soul, capped); the
  blade snowballs off corpses. Wires our own kill-tally into a live within-battle power gauge.
- Doppelgleam (Ninja blade) -- each strike borrows the max of own-vs-target PA/MA/Speed; you fight the
  strong foe with the strong foe's stats. StatHold reuse, sourced from the victim's bytes.
- Beast Within (Knuckles/Godhand) -- after a melee act, Beast Form: Brave ~97 + extra PA + Speed +2 +
  injected Counter/Hamedo. A werewolf with no sprite swap (pure write+hold + JobCommand reaction grant).
- Loaded Dice (Knife) -- hold current Brave/Faith at a high floor so every reaction/Brave/Faith roll skews
  good. The legal way to load the dice the walled crit-roll won't let us touch.
- Sovereign's Decree (Knight sword) -- on a kill, charm every enemy within 2 tiles for its next turn so the
  cluster turns on itself. Charm bit + agency hold, area'd by adjacency reads.
- Preemption (Crossbow/gun) -- spike CT at battle-enter so the wielder takes the literal first turn
  regardless of Speed. Declares initiative (distinct from kill-gated Bloodthirst).

PROBE (one byte to confirm):
- Frozen Cadence (time rod) -- hold Stop + pin the victim's CT at zero every tick = true single-target
  time-stop. Probe: can we keep CT pinned so the scheduler never ticks past it?
- Exile From the Hour (Ninja blade) -- hold victim CT massively negative; alive + targetable but never
  seated again. Non-lethal on-hit banish. Probe: does sustained-negative CT durably bench (no clamp)?
- Reaper's Tithe (scythe-pole) -- brand a target; below a HP threshold stamp Dead + HP-zero to execute,
  then the mark hops to a new victim. Probe: does forcing Dead+HP0 mid-frame kill cleanly (no re-raise)?
- Tithe of Wards (rod/stick) -- strip one buff (Haste/Protect/Regen/Reraise) off a foe and WEAR it
  yourself. Buff theft, not dispel. Probe: map positive-status bits + confirm clear-here/set-there holds.
- Last Word (gun) -- record the last ability an enemy used on you, re-cast it back at them. Probe: does the
  recent-action field capture the attacker's ABILITY id? Reflects physical skills true Reflect can't.
- Bloodpact Tether (Blood Sword) -- soul-link to an ally; damage to either splits across both HP pools; if
  either hits 0, both fall. Probe: per-tick read-both / split / write+hold racing the engine's damage write.
- Phylactery Oath (Chaos Blade/Ragnarok) -- first KO seeds a 3-turn timer, then snap back at full HP (later
  kills grow at half rate). Probe: can a unit be held KO'd-but-in-roster N turns without engine eviction?

MOONSHOT (needs new tech; each names the wall to break):
- Scorched Earth (Axe) -- the killing blow ignites your tile + 4 neighbors 3 turns, and the blaze creeps
  toward the nearest enemy. Needs persistent terrain-hazard / live ground state (FFT has none).
- Tidewarden (Pole) -- low tiles flood one step higher each turn, drowning + Slowing grounded foes while
  your team gains Waterwalk. Needs the static height map to move as a live clock.
- Glacier's Verdict (Knight sword) -- the kill ices every tile: movers SLIDE until they hit a wall
  (collision damage), reactions off, CT halved for all but the wielder. Terrain transform + sliding physics.
- Grave Conscript (bone staff) -- slain enemies rise as undead thralls on your side. Agency bit + Undead
  proven; the ALLEGIANCE FLIP is the walled grail (engine pool-relocation) this names. Holy grail of the lens.
- Banner of the Slain (Spear, Holy Lance/Gungnir) -- plant the spear as a conjured totem that taunts
  adjacent foes (Berserk) + radiates Protect/Shell. Needs the blank-render wall broken (enroll already works).
- The Long Game (grimoire) -- every kill is a hidden move; at a secret threshold the tome resolves a board-
  wide cataclysm whose flavor matches HOW you fought (magick kills -> Silence-storm; melee -> halve all PA).
  Needs a playstyle read layered over kill-tracking + mass holds.
- World Ender (Flail) -- on a kill, 3-turn Judgment: enemy tiles crack to impassable damage, gravity reels
  their line toward you, sky-chip on anyone not adjacent, foes dying spawn spectral allies. Kitchen-sink
  north star (terrain destruction + forced movement + field damage + visible summons, all walled).



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
12. PROVEN LIVE 2026-06-21: GUN SLINGER (+3 signature) -- force-equips a SECOND gun in the off-hand so the
   wielder dual-wields and a basic Attack FIRES TWICE (two ranged shots, each rolls its own hit/damage; mix
   elements, e.g. Stoneshooter Earth + Glacial Ice in one action). Verified live on Ramza (Stoneshooter 73
   main + Glacial Gun 74 off-hand -- both fired). Mechanism: write a gun id into the roster OFF-HAND slot
   +0x18 (base 0x1411A7D10, locate the unit by nameId +0x230; equip block = +0x14 main / +0x16 lhand /
   +0x18 off-hand / +0x1A shield; IDS ARE items.json ids -- guns 71-76, 73=Stoneshooter, 67=Warbrand,
   NOT vanilla FFTPatcher). REQUIRES the wielder to have Dual Wield (Two Swords) support equipped -- that
   gate is what makes the engine render + fire the off-hand gun. Equip is construction-bound (no proven
   mid-battle weapon swap), so ship as a between-battles roster-prep: +3 awakened -> write the off-hand gun
   -> materializes on next battle entry. Do NOT write while the PartyMenu is open (it clobbers the slot --
   bit us live, off-hand read back garbage). Probe: tools/probes/dualgun_probe.py (find_pid now targets the
   largest-working-set process; a duplicate FFT_enhanced instance silently ate every write for ~10 turns).
   See memory dual-gun-equip-write-proven. OVERTURNS the earlier "guns dual-wield-ineligible / construction-
   welded" pessimism.