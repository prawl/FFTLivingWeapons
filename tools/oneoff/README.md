# tools/oneoff — parked one-shot scripts

Not part of any pipeline; kept for provenance. Two of these deploy straight into the live
Reloaded Mods folder — do not run them casually.

- `add_id261_name.py` — parked 261-cap experiment (seeds a Key=261 Moonblade Item-en row); **executes on import** and deploys live. The wall is a boot-built registry: docs/ITEM_CAP_261_BREAK_JOURNEY.md.
- `patch_grenade_abilities.py` — RETIRED, superseded by patch_ability_names.py; running it mutates the pristine ability sqlite and breaks that script's self-verify.
- `patch_grenades.py` — RETIRED; the ids 246-250 grenade renames are folded into patch_names.py (`EXTRA_NAMES`), which re-asserts them on every rename run.
