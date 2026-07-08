# LW-37 build plan: pool-anchored in-place Kills paint

STATUS: PLAN (mechanism proven live 2026-07-07; build not started). See memory
`lw37-equip-card-redirect-walled` and the probe `tools/probes/item_text_census.py`.

## Goal
Retire the `Display.cs` whole-heap sweep for the equip-card Kills meter. Replace "crawl gigabytes
each paint to find widget copies" with "write the `Kills:` field in the stable string pool; the card
re-materializes it on open."

## Mechanism (proven live, owner-verified)
The equip card renders `pool -> transient FString descriptors -> materialized widget`. The pointer
REDIRECT is walled (descriptors are throwaway scratch, rebuilt from the pool each open, so a repoint
loses the materialize race). But the POOL is stable across reopen, and overwriting its space-padded
`Kills: N          ` field IN PLACE (same-length) shows on the next open. Confirmed by
`item_text_census.py write` at the pool "Kills:" address: "LOKI WAS HERE" replaced Gloomfang's Kills
line, flavor + Concentration block intact, survived reopen.

Pool layout per item (contiguous NUL-terminated strings): `displayName+tier`, lowercase key, plural
key, `Kills: N          \n\n`, flavor+mechanic. The `Kills:` line is its own space-padded field.

## Components
1. **PoolLocator (cache-once).** Find the pool arena via a cheap stable anchor (a distinctive baked
   flavor/name substring from `WeaponMeta`), NOT a whole-heap sweep. Cache the region per session;
   re-find lazily on a read miss (per-launch relocation).
2. **KillsFieldLocator (per weapon).** Within the cached pool, anchor on the weapon's flavor/name,
   then find its `Kills:` field at the fixed offset before the flavor. Return field address + padded
   width.
3. **KillsLine compose (pure).** `"Kills: N/T to +"` from the tally + tier thresholds (reuse the
   existing compose). Must fit the measured padded width.
4. **PoolWrite (in-place, guarded).** Same-length-or-shorter overwrite (space-pad remainder), only if
   the field currently reads as a `Kills:` line (anchor discipline, foreign refused), the AttackRow
   fail-closed rule.
5. **Trigger.** Update every tracked weapon's pool `Kills:` field on tally-change (battle-exit / kill
   credit) so any card open re-materializes the current count. Fallback if the pool is on-demand: a
   per-open write of the viewed weapon.
6. **Retire the sweep** for this surface behind a flag until live-verified.

## Recon to lock first (via item_text_census.py, ~10 min live)
- Pool copy count (`find` a weapon: how many pool-shaped hits vs widget hits).
- Complete vs on-demand: browse weapon A, then WITHOUT opening B, `find "B"`; is B's entry present?
  (Decides the trigger model.)
- Exact padded field width of `Kills: N          ` before `\n\n` (caps compose length).
- Stablest cheap anchor (flavor substring vs name+tier) and the narrowest arena to search.

## Test split
- Unit (xUnit + IGameMemory fake): compose (width-bounded), field-locate offset math, the
  same-length / anchor-discipline write guard, foreign refusal. All pure.
- Live (probe + in-game): pool locate on a real session, write lands + re-materializes, a
  battle-exit kill shows updated on reopen, another item's flavor untouched, Attack card unaffected,
  no latency.

## Risks
Per-launch relocation (re-find, never hardcode addresses); pool-on-demand timing (trigger model);
same-length ceiling (compose must fit the padded width); tight field bounds + anchor discipline so
name / flavor / other surfaces are never disturbed.
