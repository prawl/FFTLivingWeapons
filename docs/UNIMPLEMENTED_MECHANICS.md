PARKED — "Bloodpact" (HP-cost capstone weapon). PROTOTYPED + DEPLOYED LIVE on Chaos Blade (id37):
formula=2, May-Cast host slot 219, ~19% on hit = PA*Y bonus dmg to target + PA*Y/X self-recoil on the
wielder. CONFIRMED WORKING in-game (and it live-confirmed Formula 66 / 0x42 fires self-recoil in the
override path -- a result worth noting in MAYCAST_ABILITY_RELOCATION.md, which had flagged 0x42 untested).
NOT SOLD YET -- open issues:
  (a) It's a RE-SKIN, not an original effect. "Bloodpact" is an invented name pinned to the cut "Crushing
      Blow" slot (219); the guts ARE Construct 8's Dispose (Formula 0x42), so it looks + feels exactly like
      Dispose (full Dispose animation, damage at the end). The animation rides the FORMULA, not the hostG
      slot -- can't re-skin it without dropping the self-cost. 0x42 may be the only shipped self-cost formula
      (TODO: sweep FFTHandsFree FormulaTable.md for an alternative).
  (b) Overtuned: Y=16 -> ~200+ bonus dmg (one of the strongest procs). One-number fix: drop Y to ~6-8.
DECISIONS TO MAKE: (1) embrace the Dispose look ("construct-shard" flavor) / hunt an alt self-cost formula /
drop the self-cost entirely; (2) final Y (power) + X (recoil ratio) tuning.
REVERT: items.json id37 was {formula:1, onHitAbilityId:17 (petrify), identity Doom-on-hit}; live nxd backups
at .../prawl.fft.itemoverhaul/FFTIVC/data/enhanced/nxd/*.prebloodpact_bak; ability slot 219 was "Crushing Blow".


 1. Living Weapons (evolve from how you fight). A plain starter blade that grows by use. The companion logs
  every kill it scores and rewrites its catalog between battles (the exact write we proved): rack up Fire kills
  → it ignites (gains Fire + a burn proc); slay casters → it learns to Silence; finish low-HP foes → it sharpens
  toward Executioner. By endgame your sword is a one-of-a-kind legend shaped by your playstyle — and no two
  players' are the same. Vanilla literally can't; we can.

  
  3. Bonded Gear (real cross-unit synergy). Twin weapons that link two units: the companion reads both their
  states and ties them — damage one and the other bleeds, or they share Haste, or a shared HP pool. Synergy the
  equipment system alone can't even describe.

  ---

## THE LIVING SET (companion-driven, FFTHandsFree-powered) -- ACTIVELY PROTOTYPING

Gear that records how a unit fought and grows between battles. A 3-piece set, each piece tracks a different
deed via the LIVE FFTHandsFree battle-memory reads (all verified in FFTHandsFree/docs/BATTLE_MEMORY_MAP.md:
current-HP array 0x141024EC8 [u32 x21], current-MP 0x14102223C, roster equip +0x14 R-hand [0x1411A18D0 stride
0x258], acting unit via Turn Region 0x14077CA60). Growth = direct memory writes (PROVEN; these are normal
<=260 items so the equip stat-resolve reads them cleanly -- no id261-style crash). Apply growth BETWEEN
battles so stats stay stable mid-fight.

  1. LIVING WEAPON  -- records KILLS  -> grows WP.  Watcher polls enemy HP->0, credits the ACTING unit's
     R-hand weapon. Threshold growth (e.g. +1 WP per 10 kills; unlock a proc at 25/50/100). [ATTRIBUTION DONE]

     PROGRESS (2026-06-04): kill->weapon ATTRIBUTION built + compiling.
       - Leveraged the existing BattleTracker (it already polls the static battle array 0x140893C00
         stride 0x200 every 100ms for HP changes). It ALREADY emitted "kill" events -- BUT its detection
         was TRANSITION-based (had to catch the single poll where hp crosses 0) and MISSED real kills:
         a victim that MOVES right before dying (or whose maxHp flickers on the death hit) re-inits the
         tracked slot (BattleTracker.cs line ~540) and swallows the hp->0 crossing. Confirmed live: a
         samurai (Chirijiraden) kill left a corpse sitting in the array at hp=0/661 (1,10) that the
         tracker never logged.
       - FIX (BattleTracker.cs): added STATE-based death detection. A KO'd corpse persists at hp<=0 for
         several turns, so credit each corpse exactly ONCE the first poll we see it dead (new
         TrackedSlot.DeadCredited flag; reset when the slot is seen alive). Robust to the move/flicker race.
       - ATTRIBUTION: each poll captures the acting unit (_lastActingTeam/Weapon/Job/NameId; weapon =
         roster R-hand +0x14 = equip slot 3 = rosterReads[8], -1 on enemy turns). The death-detector
         (runs ~100ms later, same turn) stamps those onto the kill event + logs:
         "[BattleTracker] KILL at (x,y): ->0/MAXHP  killerTeam=T killerWeapon=W killerJob=J killerNameId=N".
         BattleEvent gained KillerTeam/KillerWeapon/KillerJob/KillerNameId.
       - WATCHER (FFTHandsFree/living_weapon_watch.py): tails that log, credits team==0 (player) kills to
         the swinging weapon id, tallies per-weapon to %TEMP%/living_weapon_kills.json. No memory reads --
         the in-process tracker already resolved the killer at the death instant.
       - STATUS: code compiles clean (0 errors). NOT yet deployed/tested -- game was running so the live
         DLL is locked; new code loads on next game launch. TO DEPLOY: close game -> BuildLinked.ps1 ->
         relaunch -> run living_weapon_watch.py -> kill enemies -> verify accurate attribution.
     NEXT (growth model -- needs a design call): how does WP actually grow? Options:
       (a) catalog WP write (same item, real WP bump; needs the weapon-stat table addr + confirm damage
           reads it live; affects every copy of that id),
       (b) item-swap chain (at thresholds, rewrite roster +0x14 to a stronger "Living Blade II/III" id --
           uses the already-confirmed roster-equip write, but changes the item's name/identity),
       (c) EquipBonus rider bump. Pick the model, pick WHICH weapon id is "the" living weapon, then build.
  2. LIVING ARMOR (helm or chest) -- records HITS TAKEN -> grows HP.  Poll the WEARER's own HP for any drop,
     count it. Grow HPBonus (ItemArmorData) over thresholds.
  3. LIVING RING/ACCESSORY -- records MAGIC DAMAGE DEALT -> grows MA (+1/+2/+3/+4). Heuristic: wearer is
     acting + wearer's MP drops (a spell was cast) + a target's HP drops = magic damage. Grow MABonus (EquipBonus row).

COST/RISKS: needs FFTHandsFree running as a LIVE COMPANION (not a drop-in data mod). No hooks (Denuvo crashes
HW breakpoints) -> we POLL (poll on turn transitions, tolerate the rare miss for a flavor counter). Edge cases
to rule on: AoE multi-kills, counter-kills (enemy-turn kills via Reaction), assist credit.
PLAN: prototype the whole loop in FFTHandsFree (Python via the bridge first, for fast iteration), then xfer
the finished mechanic to FFTItemOverhaul once happy.
FIRST PROVE-OUT: a bare kill-WATCHER that just LOGS "unit X w/ weapon#Y killed enemy#Z" in a live battle. If
that log is accurate, the counter + growth are downhill.


## SET BONUSES (companion-driven) -- DESIGN

Equip a full named set (e.g. Cool Helm + Cool Armor + Cool Ring) and the wearer gains an extra bonus
(e.g. Reraise) on top of each piece's own stats -- the classic RPG set-bonus, which vanilla FFT has no
concept of.

WHY NOT PURE DATA: the engine has no "if items A+B+C all equipped" check, and we can't add one via hooks
(Denuvo crashes HW breakpoints -- same constraint as the Living Set). So set DETECTION is a live
**FFTHandsFree** companion job, NOT a drop-in data effect.

THE BUILDING BLOCKS ALREADY EXIST:
  - **The bonus EFFECTS are already shippable EquipBonus rows.** Reraise is already a rider in our tables
    (the Dragon Rod carries it). PA+1/+2 (ids 21/24), MA+1/+2, Speed+1, elements -- all exist. So "grant
    Reraise / +PA / Flame-absorb when the set is complete" needs NO new effect tech, just an existing row.
  - **The roster equip write is proven** (roster +0x14 R-hand and the other equip slots; same write the
    Living Set growth uses).
  - **Equipment polling is proven** (we already read every unit's equipped ids each turn).

ACTIVATION (the elegant path -- no status-bit poking): the companion polls the wearer's 5 equip slots each
turn / between battles. When all set pieces are present, it **swaps one piece to a "set-active" variant**
whose `equipBonusId` points at the bonus (e.g. `Cool Ring` -> `Cool Ring [Set]`, equipBonusId = Reraise
row). The engine then grants the bonus at its normal stat-resolve -- no need to write a status bit live.
Remove any piece -> companion swaps the variant back to the plain id -> bonus gone. Apply swaps BETWEEN
battles so stats stay stable mid-fight (same rule as the Living Set).
  - Cost: each set needs one extra "[Set]" item id reserved as the active variant (mind the item cap --
    see ITEM_CAP_261_BREAK_JOURNEY.md). Only ONE piece needs a variant; the other two are plain.
  - Alternative (if we ever want partial/tiered sets): a 2-piece variant + a 3-piece variant of the swap
    piece, companion picks by how many are worn.

OPEN DESIGN CALLS:
  - Define the sets: which items, what each full-set bonus is, and whether 2-of-3 gives a lesser bonus.
  - Which slot carries the swap variant (accessory/ring is cleanest -- least stat disruption from the swap).
  - Whether set bonuses and the Living Set can coexist on the same unit (they share the companion's
    between-battles equip-write pass, so yes -- just sequence the writes).

DEPENDENCY: like the Living Set, this only works with FFTHandsFree running as a live companion. Without it,
the pieces are just their individual selves (graceful degradation -- the base items still ship in the
pure-data mod).


- If a player has "Protect" effect coming from more than 1 piece of gear give additional benefits (like make armor go up by 1)
  ANSWER: same companion pattern as set bonuses. Vanilla doesn't stack a binary status (a 2nd Protect source
  is "wasted"), so the companion counts equipped pieces granting status X and, if >=2, grants an extra
  EquipBonus (e.g. +1 armor) by swapping one piece to a "[redundant-X]" variant. Generalizes to any
  duplicated rider. Companion-driven (the "how many grant X" check is the same thing the engine can't do).

- If a undead unit is on the players team, do they auto re-rez like they normally do?  If so can you apply that to other units?
  ANSWER: Undead-status units auto-revive after KO -- but it's the SAME status that makes Phoenix Down kill
  them and healing hurt them. To give a NORMAL unit clean auto-revive WITHOUT those drawbacks, use **Reraise**,
  not Undead. And Reraise is already a shippable EquipBonus rider (the Dragon Rod carries it) -> "auto-rez on
  other units" = grant Reraise via equipment, PURE DATA, no companion needed. Granting real Undead status
  would need a live status write AND imports the downsides -- not recommended.


## LIVING WEAPON -- TEST FINDINGS + OPEN QUESTIONS (2026-06-05)

ATTRIBUTION: DONE + live-verified (see [[living-weapon-status]]). Kills follow the WEAPON id; the watcher
tallies player (team-0) kills per weapon. Scope decision: ANY kill by the wielder counts (basic Attack,
Aura Blast, items -- all of it). Counters credit the enemy (enemy is the active unit) -> skipped.

### Live PA-write test (2026-06-05) -- what we learned
Goal was to validate the "per-wielder: write the unit's PA directly" growth model. Result: **that model is
a swamp; abandon it.** Findings (via pa_poke_test.py, ctypes WriteProcessMemory on FFT_enhanced):
  - External memory WRITES work on this Denuvo build (no crash). Reusable capability.
  - Battle static array (0x140893C00 stride 0x200): PA-total @+0x22, PA-raw @+0x26 are both writable BUT:
      * +0x22 is DERIVED -- the engine recomputes it (RawPA x job-mult + equip) every turn. A 99 write
        held for one readback then reset to 21.
      * Writing +0x26 (raw) did NOT move damage -- the damage formula resolves PA from the persistent
        source at swing time, ignoring the battle-array scratch copy.
      * Exiting the battle reset everything (array is rebuilt from the roster).
  - The ROSTER (0x1411A18D0 stride 0x258) does NOT store PA as a byte. PA is computed (RawPA x job-mult).
    RosterReader reads brave/faith/equip/jp/passives but NO PA. The RawPA growth value's offset is UNDECODED.
  => Direct PA writes mean decoding RawPA + replicating the job-mult math + add/remove bookkeeping. Skip it.

### Revised growth model (use the engine's EquipBonus path, don't write stats)
The bonus should be applied the way the game already applies +PA: an **EquipBonus rider** (proven --
Arcanum grants MA+2 today; PA+1=row 21, PA+2=row 24 exist). Two shapes:
  - **(A) Item-swap chain [RECOMMENDED]**: companion rewrites the wielder's equip slot (roster +0x14, a
    PROVEN write) to `Living Blade -> +1 -> +2 -> +3` variant ids at kill thresholds; each variant carries
    a larger PA EquipBonus rider. PER-WIELDER (only that slot), UNIQUE (variant ids are player-only), the
    name VISIBLY grows, engine applies PA correctly -> damage just works. Cost: ~5 reserved item ids
    (cap-break hook CatalogRedirectHook handles >261). Watcher must map all tier-ids to one logical counter.
  - **(B) EquipBonus-pointer bump + scarcity**: one id; companion rewrites its EquipBonusId (catalog write,
    needs validation). Simpler/no new ids, but id-GLOBAL -> uniqueness relies on a single-copy treasure.

### OPEN QUESTIONS (current best answer in brackets)
  1. Growth mechanism: item-swap chain (A) vs EquipBonus-pointer bump (B)?  [lean A]
  2. Which weapon / id set: reuse a starter (Ashura id38, riderless, Samurai continuity) vs a bespoke
     "Living Blade" line with reserved tier-ids?  [bespoke line if we go (A); Ashura if (B)]
  3. Uniqueness mechanism: player-only variant ids (A gives this free) vs single Move-Find treasure (B)?
  4. Acquisition: shop (unlimited stock -> breaks uniqueness), poach (farmable -> breaks uniqueness), or
     Move-Find/Treasure-Hunter (one-time -> unique; table is moddable, "Treasure Hunt" mods exist).  [Move-Find]
  5. Growth curve + magnitude: [+1/+2/+3/+4 PA at 10/25/50/100 kills, cap +4]; does a proc unlock at a tier?
  6. Description / kill counter: [static description prose + LIVE count in the FFTHandsFree overlay];
     attempt the riskier in-menu live string rewrite later?
  7. Edge cases: AoE multi-kill -> credit each corpse once (multi-credit) [yes]; assist/chip credit [no];
     confirm counters stay uncredited [yes].
  8. Kill-tally persistence: where does living_weapon_kills.json live for shipping -- tied to the save slot?
     what happens across save/load and New Game+?
  9. Growth trigger timing: apply between battles (stats stable mid-fight) -- need a reliable battle-END
     detection event in the companion.
  10. Multiple living weapons at once: watcher already tallies per-id; (A) needs a tier-id set per weapon.
      [scope to ONE for v1]
  11. Companion-off behavior: plain weapon, no growth (graceful degradation) [confirmed acceptable].