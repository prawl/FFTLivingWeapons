# Mechanics

Consolidated mechanics ledger. Folds in the mechanic ideas/implementations from `docs/TODO.md` plus the
retired `docs/UNIMPLEMENTED_MECHANICS.md` (deleted; its content lives here now).
One bullet per mechanic. Two buckets: **Confirmed** (proven live and/or shipped) vs **Unconfirmed**
(idea / probe / moonshot / walled). Bug/chore items from TODO.md (Stormbrand swap, analyze.py gate
gap, Squire shield rule, Larceny log spam, Sanctus Staff tests) are NOT mechanics and stay in TODO.md.

---

## Confirmed (proven live and/or shipped)

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

- Soul Ledger (Knight sword): each kill this battle stacks +1 PA (+1 Speed per 3rd soul, capped) -- our own kill-tally driving a live within-battle power gauge.
- Doppelgleam (Ninja blade): each strike borrows the max of own-vs-target PA/MA/Speed (StatHold sourced from the victim's bytes).
- Beast Within (Knuckles/Godhand): after a melee act, enter Beast Form -- Brave ~97 + extra PA + Speed+2 + injected Counter/Hamedo (a werewolf with no sprite swap).
- Loaded Dice (Knife): hold Brave/Faith at a high floor so every reaction/Brave/Faith roll skews good (the legal stand-in for the walled crit roll).
- Worst Omen (execution tome / Murasame): curse a struck foe for 3 turns -- zeroed Brave + held Blind/Slow (inverse of Loaded Dice).
- Sovereign's Decree (Knight sword): on a kill, charm every enemy within 2 tiles for its next turn so the cluster turns on itself.
- Preemption (Crossbow/gun): spike CT at battle-enter so the wielder takes the literal first turn regardless of Speed.
- Twin Star Covenant (Katana pair): link to one ally -- when you act, their CT fills so they act back-to-back with you, but their Brave averages with yours.
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
- Knockback: write a victim's gx/gy one tile to shove it. PARKED (effectively walled): band gx/gy writes are engine-authoritative (AI paths from them) but the renderer never re-derives -> compounding sprite desync.

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
