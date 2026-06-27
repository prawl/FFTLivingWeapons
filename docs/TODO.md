Release TODO's
- Replace the Stormbrand (it's sorta lame)
- Sanctus Staff test potions and Regen
- Gate gap (low pri): analyze.py `check_rider_payload` only cross-checks NUMERIC rider stats
  (PA/MA/Speed/Move/Jump) against the emitted EquipBonus row. ELEMENT/STATUS mismaps (e.g. rider
  "absorb Fire" but equipBonusId points at an absorb-Holy row) are NOT row-checked -- only that the
  desc text mentions the tokens (check_rider_desc). Extend the payload gate to element/status if a
  mismap surfaces (needs data/vanilla_equipbonus.json to also carry those fields). Same family as the
  Blazing Staff id63 numeric bug fixed 2026-06-26.
- Ramza's Squire cannot equip Shields but normal squires can?
- Larcency keeps popped up in the logs despite not being equipped
  DEFERRED 2026-06-27 (needs a live livingweapon.log to confirm which line is firing -- none on disk; the
  log is written into the Reloaded Mods deploy dir only during a play session). Code analysis narrowed it
  to two candidate sources, both in Larceny: (1) the `larceny gate:` line (Larceny.cs:95) -- gated on
  `active || Wielder.AnyDeployedMainHand(Arcanum id 30)`, and the "inactive [...]" reason string embeds
  actorFp + actedFlag, which CHURN every turn, so a DEPLOYED-but-not-acting Arcanum spams a fresh line each
  turn (note: this path still requires a roster main-hand Arcanum that LOCATES live -- so "not equipped" may
  mean equipped on a unit Patrick forgot, or a dev give-all reserve that got deployed); (2) the
  `larceny: a stolen buff FADED` line (LarcenyHoldings.cs:71) from `Expire`, which runs UNCONDITIONALLY each
  tick incl. the off-field branch (Larceny.cs:69) -- a stale `_holdings` ledger would log a phantom fade,
  but ResetBattleState clears it on both battle ENTER and EXIT (Engine.cs:170,182), so a stale ledger
  should not survive. NEXT: capture livingweapon.log during a session where the line appears, read the exact
  text + surrounding battle/gate lines, then it is likely a one-line gate-throttle tightening (a /build-lite).
- BUG (DEFERRED 2026-06-27 -- not worth the lift now; it IS fixable, see below): enemy Knights cast Shadow
  Blade without the Sanguine Sword. The Shadow Blade grant (Sanguine Sword id 23, ability 165) is injected
  into the Knight/Squire/Gallant Knight JobCommand record, which is JOB-global, so every unit of those jobs
  shows Shadow Blade as Learned regardless of equipped weapon (enemies included). Seen live: enemy Knight
  "Dyana" holding the Arcanum used Shadow Blade. Same class as the Barrage enemy-Thief leak, but Knights are
  a common enemy job so it is far more visible.
  INVESTIGATED 2026-06-27 (no code written): the record is provably JOB-GLOBAL, NOT per-unit (LIVE_LEDGER
  row 46 "Per-record (job-global), not per-unit"), so there is NO per-unit gate at the record level -- a
  same-job enemy reads the exact cell the player's wielder does. The per-unit learned bit is held only on
  the wielder, but enemies do not need it (they cast from the job command list directly). Two viable fixes,
  each a real gameplay tradeoff (why it was deferred to Patrick's call):
    (A) TURN-TEAM GATE -- keep Shadow Blade for ALL eligible sword wielders, but REMOVE it from the record
        whenever it is an enemy/ally AI turn so only the player's turn ever sees it. Field = turn-team u16
        at TurnQueue+0x02 (Offsets.TurnQueue 0x1407832A0 -> 0x1407832A2; 0=player,1=enemy,2=ally, STABLE
        across a whole turn, does NOT follow cursor-hover; fail-safe read team!=1&&team!=2 => treat as
        player so a garbage frame never strips the player's ability). Reuses ShadowBlade.cs's existing
        Restore + BarrageState save/slot machinery (Restore writes saved bytes back WITHOUT clearing
        _state; re-inject on the next player turn reuses the saved slot). NOTE: LIVE_LEDGER row 45 already
        says the turn-team field is "Used by the Shadow Blade turn-gate" (validated 2026-06-16) but the
        gate was NEVER wired into the shipped code (commit d900bc1). Risk: relies on the 33ms poll
        restoring before the enemy AI enumerates its abilities -- the enemy turn-start sequence (camera
        pan, highlight, pathing) gives many poll cycles of slack, but it MUST be live-verified (deploy,
        watch an enemy Knight no longer cast it while the player still can).
    (B) RAMZA-ONLY (Mettle records) -- restrict TryResolveGrant to Ramza's story-unique Mettle records
        (ShadowBladePolicy.RamzaSwordJobs, recs 25-28), which no enemy or generic unit ever uses ->
        STRUCTURALLY zero leak, no timing risk, already proven live (Shadow Blade casts from Ramza's
        Mettle). Cost: a generic Knight/Squire wielding the Sanguine Sword gets stat growth but no Shadow
        Blade signature -- narrows the feature to Ramza. Thematically tight (Gaffgarion's blade).
  Lower-value third option: drop the command-grant and design a new per-unit write+hold +3 signature (the
  sword already has its innate HP-drain formula 6 at baseline).
- RESOLVED 2026-06-26 (summoner kill mis-attribution): a summoner with no living weapon casts a CHARGED
  summon that lands CROSS-TURN (after its acted-period ends) and the kill leaked to the next armed unit to
  latch (live: Chaos Blade id 37, surfacing as the sticky `[w:37]` BattleLog tag). The original
  "adjacent armed mage" triage (a (level,brave,faith) resolver collision) turned out NOT to be the live
  mechanism -- live verification showed the cross-turn charged-summon leak. SHIPPED three COMPOSING layers,
  all live-proven: (1) ActorResolver band-confirmed-UNARMED guard (`actorUnarmed && emptyMatch` -> Empty,
  never borrow a colliding armed neighbor); (2) in-period `_lethalUntracked` stamp (summon kills DURING the
  caster's own turn -> no credit); (3) cross-turn untracked-delayed arm (Charging bit `0x08` snapshot at
  cast + arm on the landing edge -> `_lethalUntracked` no-credit, `Tuning.UntrackedDelayedWindow`).
  Tracked-delayed (Jump) still wins via the `delayed == null` guard. Live: summon kills no-credited on band
  slots 10 (x2) and 8; Chaos Blade's own Phoenix-Down kill still credited (no over-suppression). See
  LIVE_LEDGER. Tests: ActorResolverUnarmedTests, SummonerAttributionTests, CrossTurnSummonTests.
  KNOWN V1 residuals: a concurrent tracked Jump overlapping a summon keeps the Jump credit even if the
  summon dealt the blow; an unrelated armed kill maturing within the ~45-tick window can be a rare
  no-credit MISS (never a mis-credit). The counterattack-kill bug above is a SEPARATE third-party-latch
  problem, still open.
- RESOLVED 2026-06-27 (Choir dial-back to HOLDER-ONLY): the Warlock's Staff (id 60) +3 "Choir" signature
  no longer grants the adjacent-ally instant-cast aura -- ONLY the deployed +3 bearer(s) get the Non-charge
  bit (band +0x7F mask 0x04). Implemented as an ADDRESS-DIRECT set on each bearer's resolved live band
  entry (Wielder.ResolveDeployedMainHandAll), recording the BAND-read fingerprint so the clear/revert path
  survives mid-battle level drift; the whole positional-aura branch was deleted (Chebyshev/InAura/
  SelectNearest in Choir.Policy.cs + Tuning.ChoirMaxBeneficiaries). NOTE: instantCastRadius stays 1 (the
  "enabled" sentinel) -- 0 would DISABLE the signature, so the handoff's "instantCastRadius 0" hint was
  wrong. Adversarial review caught two bugs that bite-tests now guard: a roster-keyed-fingerprint stuck-bit
  under level drift, and a fingerprint-collision SET that leaked the bit to an enemy. p3Desc updated ("the
  bearer casts magick instantly") + item.en.nxd regenerated. 1320 tests green; analyze.py green.
  LIVE-VERIFIED 2026-06-27 (log: "choir ACTIVE -- ...the bearer casts magick instantly"; bearer instant-
  cast, adjacent ally kept normal charge). Tests: ChoirTests holder-only set + LEVEL-DRIFT + FP-COLLISION.
- PUPPETEER (#11) LUCAVI/BOSS CARVE-OUT: the gate is currently ALLOW-EVERYONE (`IsDominatable => true`, by user request) — so bosses/Lucavi ARE dominatable by design. We do NOT want Lucavi dominatable. The `maxHp >= 2000` latch-loop cap does NOT exclude them (a live Lucavi read 999 max HP), and it's only a garbage-read sanity cap anyway — do not lean on it. Need a real carve-out keyed to job-id band and/or name-id (the long-standing "Lucavi carve-out" — IC Lucavi/boss job ids still need mapping). Costs in-game testing time to identify the ids; deferred until we can spare it. Until then, allow-everyone ships and a Lucavi CAN be puppeted.
- PARTIAL FIX SHIPPED 2026-06-27 (v2.2.2): the counterattack MIS-CREDIT is suppressed -- a kill whose
  alive->dead edge falls during a confirmed non-player turn (TqTeam 1=enemy / 2=ally) now NO-CREDITS the
  stale player latch instead of paying it out (KillTracker.Corpses.cs deadStreak==1 stamp gated on the
  proven TqTeam field, fail-safe toward crediting on a 0/garbage read; tracked Jump/charge still wins via
  the delayed `ConsumeDelayedCulprit` check). The counter-attacker (Mel) goes honestly UNCREDITED -- "miss
  beats mis-credit". Tests: CounterAttributionTests (headline non-vacuous + team==0 control + delayed-wins +
  garbage-team fail-safe); 1324 green; analyze.py green. NOT live-verified (Patrick trusted the logic gate).
  STILL OPEN (deferred probe-first RE spike): actually CREDITING the counter-attacker. Witnessed live
  2026-06-26: Reis (Hexweave Bag id 118) Jumped/acted and wounded an enemy; enemy took its turn and hit
  Melioudoul; Mel COUNTERED and killed it during the enemy's turn -- credit went to Reis (log:
  `kill: Hexweave Bag earns kill #12` at 4,10). The turn-queue struct points at the enemy turn-owner, not
  Mel, and a counterattacker never enters its own acted-period so it never latches; `KillTracker.Delayed.cs`
  only re-arms the latched actor's OWN delayed action (Jump/Charge), so ConsumeDelayedCulprit returns null
  for a THIRD-party counter. There is NO mapped field that names the counter-dealer -- crediting Mel needs a
  live probe to find a "who dealt the last/counter damage" field (may not exist holdably). BattleLog `[w:N]`
  tags are just the sticky latch (BattleLog.cs), so the log cannot show who really dealt each blow.



Ideas:

MONK ACCESSORY-IN-HAND (new equip mechanic, discovered live 2026-06-26). Writing an accessory id into a
  unit's HAND slot is ACCEPTED by the engine: the unit attacks bare-fisted (a normal Monk punch), so an
  accessory in hand does NOT break the basic attack -- it just rides along, presumably granting its
  bonuses with no weapon. Verified live: Cursed Ring (id 222) placed in Ramza's main hand -> he punched
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
- Worst Omen (execution tome / Murasame) -- curse a struck foe to roll worst-case 3 turns: zeroed Brave +
  held Blind/Slow. Inverse of Loaded Dice; composes Cripple's Brave-hold with a status-bit hold.
- Sovereign's Decree (Knight sword) -- on a kill, charm every enemy within 2 tiles for its next turn so the
  cluster turns on itself. Charm bit + agency hold, area'd by adjacency reads.
- Preemption (Crossbow/gun) -- spike CT at battle-enter so the wielder takes the literal first turn
  regardless of Speed. Declares initiative (distinct from kill-gated Bloodthirst).
- Twin Star Covenant (Katana pair) -- link to one ally: when you act, their CT fills so they act back-to-
  back with you, but their Brave averages with yours. First signature to drive a SECOND unit's tempo.

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
