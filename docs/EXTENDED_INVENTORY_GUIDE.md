# Adding a new item to the extended inventory

STATUS: CONTRACT (the hand-off guide for authoring a brand-new item past the game's 261-item wall;
DEV_TEST_RECIPES.md holds the terse version of the same recipe, this file is the self-contained one)

## What this is, in plain language

Final Fantasy Tactics: The Ivalice Chronicles has room for exactly 261 items, and every slot is
taken. This mod broke that wall: it can add brand-new items with their own id, name, stats, icon,
shop listing and growth, living at ids 261 and up. The Moonblade (id 261) is the first and the
worked example for everything below.

The important mental model: a new item is **one row in `data/items.json`, one row in
`docs/living_weapon_grid.csv`, and one icon bake**. Everything else (the tables the DLL reads,
the name and description text, the glow rims, kill counting, growth, toasts, the shop listing,
the per-save bag counts) is generated or handled at runtime keyed off the id. You never touch C#
to add an item, with one exception: a per-weapon SIGNATURE ability (the +3 tier unlock) is a C#
`ISignature` module, so an item that should have one needs a code change too; an item without a
signature needs none.

## What it is NOT (read before designing)

- **Weapons only** in V1. The generator refuses any non-weapon category.
- **Ids are contiguous from 261**, ceiling 511; the next free id is one past the highest `id` in
  `data/items.json` (261 today, so the next item is 262). A gap fails `generate.py` on purpose
  (the runtime donor tables are indexed by `id - 261`; a gap would read a neighbor's donors).
- **This is internal to Living Weapons, not a public framework** (owner ruling, recorded on the
  LW-344 task row, 2026-08-30):
  partner mods are integrated by an explicit whitelist, never by an open declaration format; the
  tables under `mod/extended_inventory/` are read by LivingWeapon.dll at boot, never by the
  modloader (a 261+ row under `FFTIVC/tables` is silently dropped by the loader).
- **Enemies cannot carry an extended id yet** (the encounter table stores item ids as one byte;
  giving enemies new items is a separate planned feature).
- **Uninstalling is clean loss, not a crash**: a save holding a new item loads fine without the
  mod; the hand is emptied and the item is gone, and it comes back with its saved bag count when
  the mod returns (details in `docs/COMPATIBILITY.md`).

## The worked example

Read the Moonblade's row in `data/items.json` (id 261) first. It is a normal living-weapon row
plus one `extended` block:

```json
"extended": {
  "cloneDonor": 37,
  "artDonor": 37,
  "seedCount": 1,
  "shops": "Dorter",
  "palette": 4,
  "spriteId": 22,
  "requiredLevel": 0,
  "typeFlags": "Weapon",
  "price": 10,
  "shopAvailability": "Blank"
}
```

## Step by step

### 1. Author the row (`data/items.json`, the only hand-edited source)

Append a row with the **next free id** (contiguous from 261). Fill it like any weapon row:
`name` (a real one; "TBD" is refused because the name is also the in-game text row),
`vanillaName` (for a net-new item, set it to the item's own name; the grid gate compares it),
`category` (a weapon category), `tier`, `flavorOverride` (the card's flavor line, 90 chars max),
`identity` (why it exists, for the design record), and `baseline` plus `proposed` (the stat
block; `formula` defaults to 1 when absent).

Growth is not a flag: EVERY weapon-category item counts kills, grows, and must have a `grows`
lane and a grid row (the gates refuse a weapon missing either), unless it opts out with
`noGrowth`. `livingWeapon: true` is a separate, rarer switch: a pure exemption from the
build-diversity no-domination gate, for an item whose power is its growth rather than its
static numbers (the Moonblade and Materia Blade are the only two). An item without it is
judged like every other item, and losing to domination there is a design bug to fix, not a
warning to suppress. `grows` has a locked vocabulary:
`PA, MA, Speed, HP, PA+MA, PA+MA+Brave, WP, WP+Faith`.

The `extended` block, field by field:

| Field | What it does | How to choose |
|---|---|---|
| `cloneDonor` | The vanilla item (id 1..255, present in items.json) whose per-category engine answers it borrows: weapon type checks, equip validity, range lookups | Pick the vanilla weapon it behaves most like |
| `artDonor` | Whose swing animation and swing art/color it draws in battle (the swing-art accessor resolves through this donor) | Usually the same as cloneDonor |
| `seedCount` | Bag copies placed at every boot arm; a loaded save then replays its own recorded count, so the effect is a one-time grant per save | 1 for a unique weapon, 0 for shop-only |
| `shops` | Towns whose shop stocks it, comma-separated, or `None` | Accepted town tokens: `Gollund, Dorter, Zaland, Goug, Warjilis, Bervenia, SalGhidos, Lesalia, Riovanes, Eagrose, Lionel, Limberry, Zeltennia, Gariland, Yardrow` (plus the legacy slot name `Unused`); the generator refuses anything outside that vocabulary |
| `shopAvailability` | The story gate on the shop listing; `Blank` means no gate: the item sells in its listed towns from day one | Owner ruling (recorded on the LW-351 task row): a real item never ships `Blank`; give it an availability window matching its tier. The Moonblade ships Blank only because it existed to prove the system |
| `palette` / `spriteId` | Bytes +0/+1 of the item's 12-byte catalog record (menu-side fields) | Copy the donor's pair. These do NOT control the battle swing's art or color; that resolves through `artDonor` |
| `requiredLevel`, `typeFlags`, `price` | More catalog record fields | `typeFlags: "Weapon"`; price is the shop price |
| `iconSource` (on the row, not in `extended`) | The vanilla id whose SHIPPED, already-recolored icon it copies | Pick an icon that reads right on the card; the donor must already have baked glow variants, or the icon bake stops and tells you to run `tools/bake_glow_icons.py` first |

If the item carries a stat rider (PA+1 and kin), `proposed.equipBonusId` names its EquipBonus
row, which also feeds the catalog record; the rider prose gates check it.

Traps that have bitten before, all enforced or documented:
- `proposed.attackFlags` is **required** (there is no vanilla row to inherit flags from; the
  generator refuses without it). Ranged weapons need `Direct`/`Arc` style flags, not `Striking`.
- **Formula and effect ids are DECIMAL in this repo's data**, while every FFT reference lists
  them in hex. Convert.
- `onHitAbilityId` is a **single byte (0-255)**; a larger value makes the modloader silently
  reject the whole weapon table. The generator refuses it; do not route around the check.
- The formula must belong to the vanilla poach-capable set `{1, 2, 3, 4, 6, 7}` or the
  poach-dormant set `{45, 46, 47, 48, 67, 69, 99}` (where the game's own Poach never fires and
  the mod's runtime cure covers it); anything else fails the classification gate.

### 2. Bake the icons

```
python tools\bake_extended_icon_parts.py
```

Byte-copies the `iconSource` donor's `.tex` pair, writes the two `.utexpt` parts files (the
game's own archive has none past id 260; the card renders blank without them), copies the donor's
three glow-rim tier variants and updates `glow_icons/manifest.json`. This must run BEFORE the
table generation: `generate.py` checks the icon files exist. Extended items stay outside the
icon-recolor program (`recolor_icons.py` skips them by design); a custom look is a polish task,
not part of adding the item.

### 3. Add the grid row and keep the docs in lockstep (owner rule, 2026-08-30)

Add the item's row to `docs/living_weapon_grid.csv` BEFORE running the gate: the grid is the
design source of truth and `analyze.py` refuses a living weapon missing from it, with the row's
name, tier, WP, parry, grows lane and +3 ability cells all lockstep-checked against items.json.
One cell trap: the `obtain` column must be `TBD` for an extended item even when it genuinely
sells in a shop; the gate's shop-sold checker reads only the vanilla shop data and is blind to
the extended `shops` field, so writing `Shop` there goes red. Update every other doc that
enumerates items (DESIGN.md counts, README, recipe examples) in the same commit.

### 4. Generate and gate the tables

```
python tools\generate.py
python tools\analyze.py
```

`generate.py` validates the row loudly (contiguity, weapons-only, donor range, attackFlags, the
shops vocabulary, the real-name rule, icon presence) and writes `mod/extended_inventory/*.xml`
in the modloader's own row vocabulary. `analyze.py` is THE gate: build diversity, description
budget, claims (the card must say what the row does and do what it says), grid lockstep. A
failure prints its section header plus lines naming the offenders (DRIFT, DOMINATED, OVERFLOW
and kin) and exits nonzero. **Read its exit code directly,
never through a pipe** (a pipe reports the tail command's exit, not the gate's).

### 5. Bake the text

```
python tools\patch_names.py
```

Seeds the item's text row from the template and bakes the name, description and badge like any
weapon, into the repo's mod tree (not the live game). The decode-verify step must PASS before
the result counts.

### 6. Build, deploy, verify

```
.\BuildLinked.ps1 -Prod
```

runs the whole pipeline (generate, gate, meta bake, tests, DLL build) and then DEPLOYS into the
live Reloaded-II Mods folder, so close `fft_enhanced.exe` first. Flavor note: a plain
`.\BuildLinked.ps1` is the DEV flavor (low kill thresholds, seeded tallies) and REFUSES to
overwrite a prod-flavored install without `-Force`; use `-Prod` on a real install. On the next
game launch, the mod's own log (`livingweapon.log` in the deployed mod folder; the previous
launch's log rotates to `livingweapon.prev.log`) must contain a line starting:

```
Extended inventory armed: N new item(s) [...names...]
```

(the counts after the item list vary by build). A `NOT armed this session (<which landmark>: ...)`
warning names exactly what refused; the whole extended inventory arms or none of it does. In
game, check: the item lists on the Items tab, equips, swings its donor's art, its card shows
name/description/Kills line, the named shop stocks it (and other towns do not), and a save/load
round trip keeps its bag count (counts are keyed per save, so each save slot remembers its own).

Icon note: the game caches icons at first draw, so glow rims and icon changes show from the
SECOND launch after a deploy.

## Removing an item

Delete its row, re-run steps 2-5 (delete its icon files and glow variants by hand; the generator
only checks presence, it does not garbage-collect), and remember the contiguity rule: only the
LAST id can be removed without renumbering. Saves holding the item lose it cleanly, as in the
uninstall note above.

## Where the deep answers live

- `docs/DEV_TEST_RECIPES.md`, "Add an extended-inventory item": the terse checklist form.
- `docs/research/ITEM_CAP_261_BREAK_JOURNEY.md`: the full engineering history of the wall break,
  every address and every dead end.
- `docs/LIVE_LEDGER.md`: the runtime mechanism claims and their status (only the owner marks a
  row Proven).
- `docs/DESIGN.md` and `docs/MECHANICS.md`: the design thesis the numbers must serve.
