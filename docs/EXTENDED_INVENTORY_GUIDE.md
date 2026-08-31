# Adding a new item to the extended inventory

STATUS: CONTRACT (the hand-off guide for authoring a brand-new item past the game's 261-item wall;
DEV_TEST_RECIPES.md holds the terse version of the same recipe, this file is the self-contained one)

## What this is, in plain language

Final Fantasy Tactics: The Ivalice Chronicles has room for exactly 261 items, and every slot is
taken. This mod broke that wall: it can add brand-new items with their own id, name, stats, icon,
shop listing and growth, living at ids 261 and up. Ids 261 to 267 are the seven designs LW-351
moved off the vanilla axe and flail slots (the Terrastaff, Ravager, Sunderer, Warbrand, Bloodlash,
Climhazzard and Sasori); the Terrastaff (id 261) is the worked example for everything below. The
Moonblade, the throwaway sword that first proved the wall could be crossed, held id 261 until
2026-08-31 and was removed once the seven real designs had proven the system.

The important mental model: a new item is **one row in `data/items.json`, one row in
`docs/living_weapon_grid.csv`, and one icon bake**. Everything else (the tables the DLL reads,
the name and description text, the glow rims, kill counting, growth, toasts, the shop listing,
the per-save bag counts) is generated or handled at runtime keyed off the id. You never touch C#
to add an item, with one exception: a per-weapon SIGNATURE ability (the +3 tier unlock) is a C#
`ISignature` module, so an item that should have one needs a code change too; an item without a
signature needs none.

## What it is NOT (read before designing)

- **Weapons only** in V1. The generator refuses any non-weapon category.
- **Ids are contiguous from 261**; the next free id is one past the highest `id` in
  `data/items.json` (267 today, so the next item is 268). A gap fails `generate.py` on purpose
  (the runtime donor tables are indexed by `id - 261`; a gap would read a neighbor's donors).
  The 511 id-mask is not the real headroom, and LW-368 round 2 (the item-count-list relocation)
  narrowed the picture to three separate numbers: **at most 121 extended items can EXIST at
  once** (`ExtendedSites.MaxExtendedCount`, enforced by `generate.py` and `ExtendedInventoryData`;
  past it, seven of the boot-patch sites plus one post-load site are `lea` instructions whose
  one-byte displacement overflows instead of widening); **owned at once** (LW-371, 2026-08-31):
  the two weapon menu order charts (the Items tab's and the equip picker's, 140 kinds plus an
  end marker in the save file, whose housekeeper overflows them silently into the neighboring
  data when a 141st kind is picked up) and the picker's third chart (every owned item of any
  category, 262 words) now live on a page the mod owns with 511 words of room each, reached
  through the three pointer slots every live reader uses; the save file keeps its 141-word
  fields (the mod writes the first 140 chart words back into them at every save and copies
  them onto the page at every load, so a mod-less load behaves exactly as before: a list may
  look short until one Sort). The binding number is now the game's menu LIST, not the chart:
  the big menus (the Items tab's combined weapons view, the All Items browser, the shops) draw
  up to **255 entries** per list (LW-372: the shared caps widened from 145/150 to 255/256, safe
  because a hook on the list builder hands the two STACK-buffer callers a mod-owned buffer and
  copies back at most 149 entries; those two menus, the equip picker among them, keep 149,
  which no real save approaches: the worst per-unit picker list today is ~142).
  LW-375 owner ruling (2026-08-31): that picker list is now the DESIGN
  CEILING for the whole catalog, 123 vanilla weapon kinds plus at most 26 extended weapon
  kinds to fill its 149 rows, enforced by ExtendedWeaponCeilingTests (the gate refuses a
  bigger extended catalog); the third relocation that would lift the big browsers past 255
  drawn rows (the static draw list 0x141811470, about 50 code fields, plus the rebuild's
  264-word stack temp) was declined.
  At the ruling's maximum (all 261 vanilla ids plus all 26 extended owned = 287 kinds) the All
  Items browser draws 255 of 287 and the combined weapons view (at most 165 kinds) never
  truncates at all; every undrawn kind still shows in its category tab, equips and saves. The chart holds every kind regardless. The picker's other three sub-charts (helmets 30 words for 28
  vanilla kinds, body 38 for 36, accessories 34 for 33) are NOT relocated: a V2 extended
  helmet, armor or accessory would overflow the picker block on day one. One residual limit
  LW-368 did NOT close: a third per-item word list (0x105 entries, not moved) still overlaps
  the Poacher's Den's own bytes for every extended id, so poaching a monster carrying one can
  read garbage there until that list is relocated too.
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

Read the Terrastaff's row in `data/items.json` (id 261) first. It is a normal living-weapon row
plus one `extended` block (and, because the design moved here from the Battle Axe's slot, a
`migratedFrom: 48` on the row):

```json
"extended": {
  "cloneDonor": 108,
  "artDonor": 108,
  "seedCount": 0,
  "shops": "Lesalia, Riovanes, Eagrose, Lionel, Limberry, Zeltennia",
  "palette": 7,
  "spriteId": 62,
  "requiredLevel": 0,
  "typeFlags": "Weapon",
  "price": 1500,
  "shopAvailability": "Chapter1_KillMiluda"
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
static numbers (the Materia Blade is the only one today). An item without it is
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
| `shopAvailability` | The story gate on the shop listing; `Blank` means no gate: the item sells in its listed towns from day one | Owner ruling (recorded on the LW-351 task row): a real item never ships `Blank`; give it an availability window matching its tier. The removed Moonblade proof item was the only row that ever shipped Blank |
| `palette` / `spriteId` | Bytes +0/+1 of the item's 12-byte catalog record (menu-side fields) | Copy the donor's pair. These do NOT control the battle swing's art or color; that resolves through `artDonor` |
| `requiredLevel`, `typeFlags`, `price` | More catalog record fields | `typeFlags: "Weapon"`; price is the shop price |
| `iconSource` (on the row, not in `extended`) | The vanilla id whose SHIPPED, already-recolored icon it copies | Pick an icon that reads right on the card; the donor must already have baked glow variants, or the icon bake stops and tells you to run `tools/bake_glow_icons.py` first |
| `migratedFrom` (on the row, not in `extended`) | Only for a design that MOVED here from another id: the id it used to live at. The bake copies it to `meta.json` and the runtime carries a save's earned kills and deeds across the move, once | Set it to the old id (Terrastaff carries `48`). Omit it on a genuinely net-new item. The move only fires while the old id has no living-weapon row of its own, so it is safe to redesign that slot later |

**Trap, `iconSource` on a moved design:** it names a SHIPPED picture, so it copies whatever that
id ships at bake time. The Terrastaff's `iconSource` is 48, and the id-48 icon was its own art on
the day it was baked but is the vanilla Battle Axe's art now (the same holds for every moved
design: 262 to 267 name 49, 50 and 67 to 70). Re-running `tools/bake_extended_icon_parts.py`
with no ids would therefore repaint the moved designs with axes and flails. Since stage 2 the
bake takes the ids to touch on its command line (`python tools/bake_extended_icon_parts.py 268`)
and refuses an id that is not an extended row; bake only the row you are adding, check the
result, and if a design ever has to diverge from its old picture for good, give it its own
recolor rather than a donor.

If the item carries a stat rider (PA+1 and kin), `proposed.equipBonusId` names its EquipBonus
row, which also feeds the catalog record; the rider prose gates check it.

Traps that have bitten before, all enforced or documented:
- `proposed.attackFlags` is **required** (there is no vanilla row to inherit flags from; the
  generator refuses without it), and the flags must speak the item's NEW category's grammar.
  Plainly: the flags say how the weapon reaches its target, and the game groups every category
  around one delivery word, so a pole that says `Striking` (the axe's word) is wrong even
  when the card looks fine. `generate.py` reads the delivery word out of your flags
  (`Striking`, `Lunging`, `Direct` or `Arc`) and refuses the row unless it is the one every
  vanilla row of that category carries (`CATEGORY_DELIVERY` in the script: swords, knight swords,
  ninja blades, knives, katana, axes, flails, rods, staves and bags strike; poles, polearms
  and cloths lunge; guns, crossbows, instruments, books, bombs and thrown items are direct;
  bows arc). The grip words
  (`Throwable`, `TwoHands`, `TwoSwords`, `ForcedTwoHands`) are yours to choose. A design that
  MOVES categories (the Terrastaff went from the Battle Axe's Axe slot to a Pole) authors the
  destination's grammar, not the flags its old slot lent it: copy them from a vanilla row of
  the new category (the Terrastaff took the Ironreed Pole's `Throwable, TwoHands, Lunging`).
  A deliberate exception needs the owner's ruling in `EXTENDED_DELIVERY_EXCEPTIONS` (empty).
- `proposed.range` must be the **clone donor's** range, because that is the reach the game
  plays. Plainly: you can type any range you like on an extended row, and the game ignores it;
  the weapon strikes as far as the donor it borrows its engine from. The owner watched this on
  the Terrastaff on 2026-08-30 (its row said 1, it hit two tiles away, its donor the Ironreed
  Pole is a range-2 pole). Where the game really reads the reach from is not settled (the
  donor's own row through the sibling accessors, or the Lunging delivery class itself; every
  vanilla Lunging category is range 2, so that one sighting cannot tell them apart; LW-364 owns
  the answer). What IS settled: the row records the donor's range so the dominance math and the
  reader see the truth, `generate.py` refuses otherwise (`check_extended_range`), and a design
  that needs a different reach picks a donor that has it. An owner-ruled `EXTENDED_RANGE_EXCEPTIONS`
  entry (empty today) is the only way past the check.
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

Removing one from the MIDDLE slides every later id down one (the Moonblade's removal on
2026-08-31 is the worked example, commit history under that date): renumber the rows in
`data/items.json` and `docs/living_weapon_grid.csv`; byte-copy each later id's `.tex` pair and
its six glow variants down one id and re-point its two `.utexpt` parts files (the picture path
inside them names the id; `bake_extended_icon_parts.patch_parts` does it without touching the
donor pac), then delete the old last id's files and let `bake_glow_icons.update_manifest` prune
its manifest entry; DELETE the old last id's row from `working/pilot_item.sqlite` before the name
bake, or the audit refuses the stray row; move every constant that names an id
(`Bulwark.SundererId`, `analyze.py`'s `DOMINANCE_DEFERRED` and `THIN_NICHE_EXCEPTIONS` keys, the
shipped-roster tests: `ExtendedInventoryDataTests`, `TallyMigrationTests`, `BulwarkTests`,
`MetaSchemaTests`); re-read every doc and note that names an id (README counts, DESIGN,
COMPATIBILITY, DEV_TEST_RECIPES, the restored rows' `identity` prose in `data/items.json`, the
`recolor_icons.py` ruling prose, the TODO rows); and on a dev rig shift the keys in `kills.json`,
`legends.json` and `extended_inventory.json` by hand (keep a timestamped `.bak` of each), because
the runtime's tally migration keys on the OLD vanilla id (`migratedFrom`) and knows nothing about
a shift between extended ids. A player's save keeps its hand words as numbers, so EVERY worn
extended item becomes whatever now holds its number (the next design down) and a worn old last
id empties. The saved menu charts also still carry the old last id, and that word is exactly
what the game's chart housekeeper stops at (the LW-351 round-8 defect shape: a doubled neighbor
and a lost end marker on every new acquisition), so Sort BOTH weapon lists once BEFORE buying or
looting anything; LW-367 teaches the mod's repair to drop such a word itself.

## Where the deep answers live

- `docs/DEV_TEST_RECIPES.md`, "Add an extended-inventory item": the terse checklist form.
- `docs/research/ITEM_CAP_261_BREAK_JOURNEY.md`: the full engineering history of the wall break,
  every address and every dead end.
- `docs/LIVE_LEDGER.md`: the runtime mechanism claims and their status (only the owner marks a
  row Proven).
- `docs/DESIGN.md` and `docs/MECHANICS.md`: the design thesis the numbers must serve.
