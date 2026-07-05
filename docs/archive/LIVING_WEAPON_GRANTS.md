# Living Weapon Tier-Grant Design

STATUS: ARCHIVED (superseded by the curated grant model; the master grid is docs/living_weapon_grid.csv)

_Generated proposal grid (Hybrid model: stat growth every tier + ability unlocks at milestones). Grid data: living_weapon_grid.csv._

> **Excluded:** the **Throwing** (Shuriken / Fuma Shuriken / Yagyu Darkrood) and **Bomb** (Flameburst / Snowmelt / Spark) categories are **Throw-command consumables**, not persistently wielded weapons — the grow-the-wielded-weapon system has nothing to attach to, so they get no grants. 121 weapons remain.

## Mechanics Catalog (the dropdown of buildable grants)

## Living Weapon — Mechanics Catalog (grant menu)

Every weapon gets **scalar stat growth on every tier** (the smooth backbone) plus **binary ability unlocks at tier milestones** (+/+2/+3). Below is the full menu of grants we can actually build, with the lever, stacking rule, and confidence.

### 0. Baseline stat growth (the backbone — always on)
| Grant | What it does | Lever | Stacking | Type | Confidence |
|---|---|---|---|---|---|
| PA growth | Holds wielder's PA at `round(natural × (1+factor))` for physical weapons | GrowthEngine over combat-struct, routed by category/formula in Tuning.cs | Single per weapon | **Scalar** | **Proven** (built) |
| MA growth | Same, for caster / magic-formula weapons (Rod/Staff/Book/some Instrument/Gun) | GrowthEngine + Tuning formula routing | Single per weapon | **Scalar** | **Proven** (built) |
| Speed growth | Same, for speed-formula weapons (Knife/Throwing/some Cloth/Ninja) | GrowthEngine + Tuning formula routing | Single per weapon | **Scalar** | **Proven** (built) |

### 1. REACTION grants (ability ids 167–197)
- **What:** a passive that fires when the wielder is hit/acts. **Only ONE reaction fires per hit.**
- **Lever:** reaction-ability slot on the combat struct (same inject path as the JobCommand-table proof).
- **Stacking rule:** **1 effective.** A weapon may *list* a sequential pair only if it arbitrates cleanly (Mana Shield → Speed Surge). **Parry (191) is greedy/solo — NEVER pair it with another reaction.**
- **Type:** Binary. **Confidence:** Proven for the grant mechanism; specific arbitration is the design contract.
- Menu: Counter 186, Mana Shield 189, Parry 191, Auto-Potion 185, Regenerate 172, Magick Counter 179, Speed Surge 168, First Strike 197, Shirahadori 195, Reflexes 193, Crit:Quick 177, Dragonheart 171, Soulbind 190.

### 2. SUPPORT grants (ability ids 198–229)
- **What:** an always-on passive (damage/defense/utility multiplier or rule-breaker).
- **Lever:** support-ability slot on the combat struct.
- **Stacking rule:** **SUPPORTS STACK — up to 2 at once.** This is the only grant family that legitimately doubles.
- **Type:** Binary. **Confidence:** Proven.
- Menu: Attack Boost 209, Defense Boost 210, Magick Boost 211, Magick Def Boost 212, Concentration 213 (never miss), Doublehand 220, Dual Wield 221, Brawler 216, Halve MP 206, Swiftspell 226, HP Boost 228, Beastmaster 222.

### 3. MOVEMENT grants (ability ids 230–251)
- **What:** map mobility / terrain / on-move font.
- **Lever:** movement-ability slot on the combat struct.
- **Stacking rule:** **EXACTLY ONE effective.** Movement does NOT stack, and a **+Move bonus blocks a font** (Move+N and Lifefont/Manafont are mutually exclusive — pick one). Across +/+2/+3, deepen the SAME movement line (Move+1→Move+2), never list two movement grants simultaneously.
- **Type:** Binary. **Confidence:** Proven.
- Menu: Teleport 242, Move+1/2/3 (230/231/232), Jump+N, Levitate 250, Fly 251, Ignore Terrain 245, Ignore Elevation 236, Waterwalking 246, Lifefont 237, Manafont 238.
- **Tier gate:** no early Teleport/Fly on a tier-1 weapon (breaks level-gating).

### 4. CONDITIONAL grant — the "Adrenaline" pattern
- **What:** below X% HP → grant [support and/or ONE movement and/or stat] for N turns. Fires <33ms, held while the condition holds.
- **Lever:** HP-watch in the engine loop; applies the same support/movement/stat slots, gated by an HP threshold + turn counter.
- **Stacking rule:** the *granted* bundle still obeys family rules (≤2 supports, ≤1 movement). Great risk/reward — only desperation buffs.
- **Type:** Binary (gated). **Confidence:** **Proven** (Adrenaline fires, held).

### 5. REVIVE heal-back
- **What:** on reviving an ally, heal a % of HP. **Lever:** revive-event hook. **Stacking:** standalone. **Type:** Binary. **Confidence:** Doable (feasibility-cleared).

### 6. TIMED grant
- **What:** for N turns after event X, grant a buff. **Lever:** turn-counter gate. **Stacking:** obeys family rules. **Type:** Binary (gated). **Confidence:** Doable.

### 7. NEW PRIMARY ABILITY inject — CAVEATED
- **What:** add a brand-new castable ability to the wielder's command tree.
- **Lever:** in-memory JobCommand-table inject (~0x140679436, 25-byte records) **or** the JobCommandData.xml data path.
- **Caveat:** the data path is **per-command, not per-wielder-clean**, and the live inject still needs the "auto-learned/purchased" question solved. Needs manual JP or the XML route.
- **Type:** Binary. **Confidence:** **Requires-proof / flag it.** Not used in this grid — every entry above sticks to the clean families. Reserve for a one-off signature only if a weapon truly demands it.

### Quick rule card
- Movement: **ONE** only (and Move+N blocks a font).
- Support: **up to 2** stack.
- Reaction: **1 effective** — sequential pairs only if clean; **Parry never pairs**.
- No Teleport/Fly on tier-1.
- Each weapon = **ONE signature deepened** across +/+2/+3, not three unrelated toys.

## Balance Review

## Balance Review

### Rule-violation fixes applied (these were corrected from the raw proposals)
1. **Terrastaff (48, Pole, T1) — Teleport at tier 1 REMOVED.** Two agents proposed it; one even flagged it as dangerous. Replaced the whole line with the clean Pole version: Move+1 → Move+1+Defense Boost → **Move+2+Defense Boost**. Single deepening movement line, tier-appropriate, no early Teleport. This was the single worst violation in the batch.
2. **Ivory Pole (112, Pole, T4) — Parry pairing fixed.** Both agent versions sinned: one paired Parry with Speed Surge, the other with Counter (both reactions). Parry is greedy/solo. Final line keeps the support backbone (Brawler → Brawler+Attack Boost) and isolates **Parry as a solo, unpaired capstone**.
3. **Pole duplicate resolution.** Items 48 + 107–114 each had two competing designs. I kept the "one-signature-deepened" version per weapon (the hybrid model's whole point) and discarded the alternates. Sage's Pole (111) kept as an **MA** caster pole (the magic version), not the second agent's PA reskin — its identity is "clean MA power."
4. **Movement-stack audit — clean.** No surviving row lists two movement grants at once. Lines that deepen movement (Terrastaff Move+1→Move+2, Skirmisher's Move sits in +/+2 only) stay within one font/move line. Adrenaline bundles that include Move+2 (Muramasa, Wrathblade, Ravager, Chaos Blade) each grant exactly one movement inside the conditional — legal.
5. **Reaction-pair sanity.** Sequential pairs surviving are the clean ones: Mana Shield→Speed Surge (Ashura, Sanguine Gauche), Mana Shield→Reflexes (Hushfan). No non-Parry greedy collisions remain.

### Tier-power escalation: SANE
Capstones scale with availability. Tier-1 starters get modest grants — Move+1, HP Boost, single Attack/Defense Boost, basic Reflexes/Counter — never Teleport, Fly, Concentration+Attack stacks, or Crit:Quick. The variance/“delete” capstones (Crit:Quick, Soulbind, Doublehand, Teleport, Brawler combos, Adrenaline desperation bundles) live at **T4–T6**, where they belong. The high-`opRisk` flags cluster correctly at T6 (Sasuke's Blade, Iga Blade, Koga Blade, Chaos Blade, Excalibur, Siegebolt, Perseus Bow).

### plus3 grants that look OP for their tier — watch these
- **Zwill Straightblade (10, Knife T6) — Concentration + Doublehand THEN Teleport.** Guaranteed Sleep every turn + two-hand burst + free repositioning is a soft-lock engine. It's T6 and `high` risk, so it's *allowed*, but it's the strongest control loop in the set. Ship it last; consider gating Teleport behind the highest kill-tier so it isn't online immediately.
- **Sasuke's Blade (16, NinjaBlade T6) — Dual Wield = two Sleep procs/turn.** Two independent Sleep rolls per turn is a genuine soft-lock. Fine at T6, but the single most likely "this trivializes the fight" candidate. Pair the kill-threshold high.
- **Defender (33, KnightSword T4) — Parry + Defense Boost + Concentration + Regenerate.** This is an *intentionally* unkillable wall and it earns the name, but Concentration (never-miss) on a T4 defensive piece nudges it toward "tank that also never whiffs." Acceptable because it grows PA slowly and has zero burst — flag for playtest, not a blocker.
- **Stoneshooter (73, Gun T4) — Move+3.** Move+3 is a lot of mobility for T4. It's a single movement grant (legal) on a slow heavy gun, so it's a *repositioning* tool not a kite tool — leave it, but if it feels oppressive, drop to Move+2.

### Standout designs worth building FIRST (ship these to prove the system)
1. **Cutpurse (1, Knife T1)** — Dual Wield → +Speed Surge → +Attack Boost. The cleanest possible demo of the hybrid model: Speed growth backbone, one signature (dual-wield aggression) deepened tier by tier. Low risk, instantly legible. **Build #1.**
2. **Wellspring Rod (51, Rod T1)** — Halve MP → +Regenerate → +Magick Boost. The MA-growth showcase: a single "endless casting" fantasy that compounds. Proves the caster lane end-to-end at tier 1.
3. **Muramasa (44, Katana T4)** — the Adrenaline desperation bundle (under 30% HP → Doublehand + Move+2 for 3 turns) is the marquee conditional grant and the best risk/reward story in the set. Proves the gated-bundle pipeline.
4. **Ashura (38, Katana T1)** — the textbook clean sequential reaction pair (Mana Shield → Speed Surge). Build it to validate reaction arbitration works before shipping the spicier T6 chains.
5. **Sanguine Sword (23, Sword T3)** — HP Boost → Mana Shield → Regenerate is a self-contained lifeleech/sustain loop that reinforces exactly the role the weapon's drain formula wants. A perfect "non-WP longevity" exemplar for the design thesis.

### One thematic nit (non-blocking)
Several PA melee weapons grant **Magick Boost** to scale an elemental proc (Stormbrand, Flamberge, Lightbringer, Graviton, Arcanum, Frost Kodachi). That's correct *if* those procs scale off MA/Faith — confirm the formula actually reads MA before locking, or the rider does nothing. Flagged, not fixed, since it depends on the proc's formula id.
