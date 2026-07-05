# New-Weapons Expansion -- Proposal (slices)

STATUS: ARCHIVED (proposals landed in data/items.json or were dropped)

Draft 2026-06-26, off the back of the cap-break win (id261 "Moonblade" equips/displays/swings/hits live).
This is a PLANNING doc, not a build. Specifics (exact ids, stats, names) get nailed per-slice in
`data/items.json` (the single source) and gated by `analyze.py`.

## Foundation (what we can now lean on)
- **~250 new item ids reachable (261-511)** -- the catalog mask is `0x1FF`=511. ~120 are trivial (the equip
  clamp's disp8 tops out ~382 before re-encoding); the full 511 needs a couple more cap widenings.
- **261+ weapons EQUIP / DISPLAY / RESOLVE STATS / DEAL DAMAGE today** via the rig (catalog relocation +
  weapon thunk + validity thunk + clamp). Proven live.
- **Battle ART for 261+ needs the model slot-swap** (construction-time id swap) -- proven viable, NOT yet
  built. Until it ships, a 261+ weapon swings INVISIBLE (everything else works).
- **Vanilla ids 0-260 have NATIVE art** -- they work 100% today, no cap-break, no slot-swap.
- **The gate (`analyze.py`)**: no item may be strictly dominated within/across an equip slot. New weapons
  that share a slot must be DIFFERENTIATED (rider, element, evade, tier, availability). **Living-weapon items
  are gate-EXEMPT** (power comes from growth).
- **128 stat profiles** (ItemWeaponData rows, indexed by SecondTableId). New weapons REUSE a row (Moonblade
  -> 67) for free; DISTINCT stats past 128 need the table-extension (Slice 5).
- Names/descriptions: `item.en.nxd` is unlimited (Moonblade proved it). Naming trend + the
  uniqueness/<=90-char gate per `[[item-description-uniqueness]]`. ASCII only, no em dashes.

## Phase 0 (PREREQUISITE for all net-new 261+ content) -- productionize + the model slot-swap
1. Move the cap-break rig from the FFTHandsFree research bridge into the shipping mod
   `prawl.fft.livingweapons` (it currently lives as off-by-default verbs).
2. Build the **model slot-swap loop**: on battle entry, swap a 261+ weapon's roster slot to its clone-art id
   for the construction window, restore to the real id in menus (so saves/names stay correct). Same
   background-loop pattern the project already runs (puppet-hold / stat-hold). This is the universal unlock
   for 261+ battle art.
3. (Deferred to Slice 5) extend ItemWeaponData past 128 rows when a slice needs more distinct stats.

Everything below that lands at 261+ is blocked on Phase 0 for ART (not for equip/stats). Plan accordingly.

---

## Slice 1 -- Axes & Flails (restore the category)
The 7 retyped ids and their current (wrong-art) state:

| id | current name | retyped to | originally |
|---|---|---|---|
| 48 | Terrastaff | Pole | axe/flail |
| 49 | Ravager | KnightSword | axe |
| 50 | Sunderer | KnightSword | axe |
| 67 | Warbrand | Sword | axe/flail (Moonblade clones this row) |
| 68 | Bloodlash | Knife | flail/whip |
| 69 | Climhazzard | NinjaBlade | -- |
| 70 | Sasori | Katana | flail (scorpion) |

### Your question: revert vs add -- RECOMMENDATION: **REVERT** (for the axes/flails)
Change these back to **Axe/Flail** categories with new names + axe/flail-appropriate stats. Why revert wins:
1. **It FIXES the existing cosmetic bug for free** -- these swing axe/flail art *today* despite being
   sword-typed (the `[[weapon-blade-art-walled]]` blemish). As axes/flails, the native art is CORRECT and
   needs ZERO slot-swap.
2. **Axes/flails ship immediately** -- native art means Slice 1 lands BEFORE Phase 0 is done. Pure data +
   gate.
3. **The displaced sword identities** (Warbrand etc.) **re-home in the 261+ band** -- that's exactly the
   "new content" the cap-break was built for, and sword models are abundant for the slot-swap clone.

The ONE cost: those sword identities swing INVISIBLE in battle from when they move to 261+ until Phase 0
ships (they still equip/display/hit). Mitigate by doing Phase 0 right after, or keeping the swords at their
vanilla ids until Phase 0 is ready and moving them in one motion.

Alternative (ADD): keep 48-70 as swords, add brand-new axes/flails at 261+. Rejected -- it carries the
axe-art-on-sword bug forever AND the new axes still need Phase 0 for art. Strictly worse than revert.

### Work for Slice 1
- Re-type the chosen ids to Axe/Flail in `items.json`; re-tune to axe/flail identity: **axes** = high WP /
  lower evade / a crit or random-damage rider (vanilla axe flavor); **flails** = ignore-evade or an on-hit
  status. These differentiators keep them gate-legal vs the sword family sharing the slot.
- New names "to keep the trend going" -- evocative, unique-flavor, <=90 chars.
- Re-run `analyze.py` (category access changes -- axes/flails are equippable by a different job set than
  swords; confirm no new dominance).
- Decide per-id: which sword identities to preserve (re-home at 261+, post-Phase-0) vs retire.
- Net: the game GAINS the axe/flail category (native art) + keeps the swords (at 261+). True expansion.

---

## Slice 2 -- WOTL-missing weapons
War of the Lions (PSP) added content over the PSX original; IC is based on WOTL but Patrick wants to backfill
any weapons IC dropped. **Google the WOTL weapon list, diff against `items.json`, add the gaps at 261+.**
- Per weapon: name + stat profile (reuse a SecondTableId or a new row) + an art-clone target (slot-swap) +
  availability tier + a rider to pass the gate.
- Blocked on Phase 0 for art (lands at 261+). Slice size = the WOTL delta (likely a handful to ~a dozen).
- Candidates to confirm: the WOTL rare/multiplayer drops and any WOTL-exclusive weapons not in IC.

---

## Slice 3 (proposed) -- Living-Weapon Signature weapons  ★ highest-value for the mod's identity
The mod's headline is the Living Weapon: growth + per-weapon P3 signatures (the `ISignature` system -- one
constructor line + one array entry per signature). The cap-break hands us ~250 ids of headroom to add NEW
signature weapons **without disturbing the carefully-gated existing 261**.
- Design a set of brand-new weapons each built around a FRESH P3 signature (a new on-kill effect, a
  growth-scaled aura, a turn-edge proc -- the project already has CharmLock/Plague/Barrage/ExtraTurn/Renewal
  etc. as the pattern).
- **Gate-EXEMPT** (living-weapon items) -- pure creative headroom, no dominance fight.
- This is where the cap-break pays off most: more of exactly what makes the mod special, with no opportunity
  cost against the existing roster. Blocked on Phase 0 for art (clone a fitting model per weapon).

## Slice 4 (proposed) -- a capstone "Legendary" tier
The cap-break uniquely enables adding a WHOLE NEW top tier (tier 6+) that a fixed 261 cap could never fit
without cutting something. Propose an endgame "legendary" set -- one standout per weapon category, highest
availability tier, strong riders -- as a chase-item layer. The headroom is the whole point.

## Slice 5 (proposed, enabler) -- distinct-stats unlock (ItemWeaponData extension)
Slices 1-4 reuse the 128 stat rows. When a slice wants MORE than ~128 distinct stat profiles, extend
ItemWeaponData via the same relocation trick that cracked the catalog (SecondTableId is a full byte -> up to
255 rows). Do it on-demand; it's the "scale" enabler, not a slice users see.

---

## Suggested order
1. **Slice 1a (now, no cap-break):** revert the 7 axe-art retypes -> axes/flails (native art, fixes the bug,
   restores the category). Ships on data + gate alone.
2. **Phase 0:** productionize the rig + build the model slot-swap. Gates all net-new 261+ art.
3. **Slice 1b / 2 / 3 / 4** at 261+ (ride Phase 0): re-home the displaced swords, WOTL backfill, signature
   weapons, legendary tier.
4. **Slice 5** whenever distinct stats run past 128.

## Cross-cutting checklist (every slice)
- `data/items.json` is the only hand-edited source -> `generate.py` -> tables; `gen_living_weapon_meta.py`
  for the runtime.
- 261+ weapons need: a relocated catalog entry, a stat row (reuse/new), an `item.en.nxd` name row, an
  art-clone target.
- `analyze.py` green (no dominance) -- except living-weapon items.
- Names: trend-consistent, unique flavor, <=90 chars, ASCII, no em dashes.
- Verify-live-before-commit for any runtime piece (the slot-swap); /build pipeline for substantive code.
