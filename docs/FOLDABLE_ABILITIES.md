# Foldable "forbidden" abilities (catalog 2026-06-02)

Abilities normally locked away (monster / boss / story-unique / cut) that we can fold into any
unit's command via `JobCommandData.xml` `AbilityIdN` (confirmed lever — see
[project_fft_skillset_fold]). `AbilityId` is wider than a byte, so even high ids are addressable.

**Caveats (read first):** all `works` ratings are DATA-DERIVED, **none in-game verified yet**.
Positive precedent = the Rend fold (138-145) worked (same `AbilityType=Normal` class, menu-usable).
- `high` = pure cast/projectile/breath/buff/passive — effect spawns at target, no monster/boss body rig needed → should just work.
- `medium` = monster-melee motion or boss AoE geometry — the *effect* likely lands, the *animation/targeting* is the gamble.
- `unknown` = cut/unused; data may be stubbed.
Every fold still shows **greyed until JP-learned** (or set innate).

## Safest bets (high — pure-effect, should work as-is)

| id | name | normally | effect |
|----|------|----------|--------|
| 256 | **Vengeance** | Beowulf | sword strike scaling off the user's OWN lost HP — formula-67 berserker, as an ability |
| 339 | **Mighty Guard** | Dragons | Protect + Shell + Haste to self + adjacent allies in ONE cast |
| 328 | **Bad Breath** | Malboro | AoE cloud, independently rolls 8 ailments (Petrify/Blind/Confuse/Silence/Oil/Frog/Poison/Sleep) |
| 282 | **Blaster** | Coeurl | ranged gaze: Petrify OR Stop on a distant unit |
| 333 | **Almagest** | Behemoth | ranged AoE, damage scales off the CASTER's HP loss (low-HP nuke) |
| 334 | **Gigaflare** | Behemoth | big non-elemental ranged AoE nuke |
| 332 | **Twister** | Dark Behemoth | wind AoE, %-of-MAX-HP damage (ignores defense) |
| 337 / 338 | **Ice / Fire Breath** | Dragons | clean ranged elemental breath nukes |
| 288 | **Mind Blast** | Mindflayer | ranged Confusion OR Berserk |
| 304 | **Doom** | Ahriman | ranged Doom (death countdown) |
| 269 | **Choco Cure** | Chocobo | heal self + adjacent allies |
| 168 | **Celestial Stasis** | Orran/Olan | hits ALL enemies on the field with Stop + Immobilize + Disable |
| 162 | **Crush Weapon** | Meliadoul | ranged — destroys target's weapon outright, NO Brave roll (unlike Rend) |
| 165 | **Shadowblade** | Gaffgarion | ranged dark strike that absorbs HP to heal the user |
| 159 | **Divine Ruination** | Agrias/Orlandeau | holy line-attack at range, chance to Confuse |
| 157 | **Northswain's Strike** | Agrias/Orlandeau | ranged holy wave, chance of instant Dead |
| 183 | **Suffocate** | Assassins (Celia/Lede) | flat instant-KO on an adjacent unit |
| 182 | **Shadowbind** | Assassins | ranged Stop |
| 215 | **Seal Evil** | Mustadio/Beowulf | instantly Petrify any Undead |
| 263/264/262 | **Cloud's Limits** (Omnislash / Cherry Blossom / Finishing Touch) | Cloud | massive multi-hit / AoE / KO-Stop-Petrify blade nukes |
| 485 | **Vehemence** | cut Support | passive: +50% attack, -50% defense (built-in Berserk trade) — no animation risk |
| 499 | **Master Teleportation** | cut Movement | Teleport that never fails the distance roll |

## Spicy gambles (medium — effect likely lands, animation/geometry may glitch)

| id | name | normally | effect |
|----|------|----------|--------|
| 340 | Tri-Attack | Hydra/Tiamat | 3-hit adjacent melee (3-head construct — may collapse on a human) |
| 341/342/343 | Tri-Breath / Tri-Thunder / Tri-Flame | Tiamat | 3-direction elemental AoE; Tri-Breath is %-max-HP |
| 344 | Dark Whisper | Tiamat | dark AoE, chance of instant-KO or Sleep per hit |
| 277 | Self-Destruct | Bomb | HP-loss-scaled self-detonation + Oil |
| 311 | Snort | Boar | Charm an adjacent foe |
| 314 | Bequeath Bacon | Boar | sacrifice self to fully restore an ally's HP/MP + level +1 |
| 230 | Divine Ultima | Altima | faith-scaled AoE almighty nuke |
| 350 | Grand Cross | Altima | AoE that rolls every major status separately |
| 166 | Unholy Darkness | Lucavi | dark-element magick nuke ("Dark Holy") |
| 75 | Zodiark | Adramelk | boss-summon radiant AoE |
| 191/194/188 | Aphony / Nightmare / Fowlheart | Lucavi | AoE mass-status debuffs |
| 253 | Dragon's Might | Reis | buffs Brave+Speed+PA+MA (wyrm-flavored targeting) |
| 210 | Bioga | Apanda | toxic AoE, chance of instant KO |

## Cut / unused (unknown — may be stubbed; novelty)

| id | name | effect |
|----|------|--------|
| 357 | Unholy Sacrifice | sacrifice HP to damage ALL in range |
| 220 | Abyssal Blade | self-cost falloff nuke |
| 219 | Crushing Blow | clean unused sword attack |
| 229 | Ultima (cut variant) | boss-flavored 2nd Ultima, placeholder name |
| 221-228 | the "-ja" debug block | Toadja/Gravija/Blindja/Confuseja/Sleepja/Dispelja/Flareja — likely stubbed |
| 184 | Infernal Strike | sword strike that absorbs MP |
| 329 | Malboro Spores | permanently turn a unit INTO a malboro (low odds it works) |

Source: Cross-reference of AbilityData.xml × JobCommandData.xml ×
MonsterJobCommandData.xml × JobData.xml × ability.sqlite (Ability-en).
