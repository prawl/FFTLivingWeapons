# Dev / test recipes

STATUS: CONTRACT (dev harness cheats and probe recipes)

Throwaway conveniences for testing this mod live. None of this ships — it's harness only.

## Step zero for unexplainable game weirdness: bisect the mod list FIRST

Before any code archaeology on a "this mod corrupted X" report: disable the OTHER Reloaded
mods and re-test. The Materia Blade+ gun-range corruption cost a day of staring at this
repo's tables — the culprit was FFTHandsFree auto-arming its parked 261-cap hooks on every
boot. If the weirdness involves item stats/visuals that this mod's tables don't even touch,
the prior is another mod, not us. Toggle in Reloaded-II's UI; one launch per suspect; THEN
open the hood.

## Give 99 of every item (inventory cheat)

**Canonical script lives in the sibling repo:** [`FFTHandsFree/lib/fft/shop.sh`](../../FFTHandsFree/lib/fft/shop.sh)
→ `give_all_items [count]`. Don't reinvent it.

```bash
cd /c/Users/ptyRa/Dev/FFTHandsFree && source ./fft.sh
give_all_items 99            # 99 of every safe item
FFT_GIVE_ITEMS_DELAY=200 give_all_items 99   # slower if writes drift
```

- Requires the **FFTHandsFree mod loaded** in the running game (it drives the `fft` command→`command.json`
  bridge). Both `FFTHandsFree` and `prawl.fft.livingweapons` can be loaded at once.
- **Run on a safe screen** (WorldMap / TravelList / battle). NOT while PartyMenu is open — roster-adjacent
  writes get clobbered. Close + reopen the inventory afterward to refresh the menu.

**The underlying recipe** (works via plain RPM/WPM too, no bridge needed — see `give_living_blades.py`):

```
inventory count array: count[itemId] = u8 @ 0x1411A7C00 + itemId   (1.5.x; == Offsets.InventoryCountBase)
```

**1.5 moved this array +0x6440** (pre-1.5 it was `0x1411A17C0`; that old region now READS PLAUSIBLE
GARBAGE and accepts writes that go nowhere, then get zeroed by the game — it burned a session on
2026-07-15; verify against `Offsets.InventoryCountBase` before hand-writing). The array sits 0x110
below `RosterBase` (`0x1411A7D10` on 1.5); ids 0..260 stay clear of the roster.
**Skip the crashy / IC-unused ids:** `262` (Onion Sword crashes on equip-render), `261, 263–277`
(IC stripped these slots), and `254, 255` (the engine's "random item" placeholder and the never-used
slot: no name, no catalog record; 99 of 254 rendered as a nameless black-icon row on the Items tab,
2026-08-27). `give_all_items` skips all of them since HandsFree c3fa8e4; replicate the skip if you
write your own. Clearing a stray one is a single-byte zero of `count[id]` from outside on a safe screen.
Caveat: only ids whose IC layout matches FFTPatcher-canonical render in the menu (~80–85%); the rest write
into RAM but don't show.

## Glow tier bounce (see every icon at one glow tier)

No manual deploy step exists any more (LW-336 retired deploy_glow_tex.py, the LW-334
interim path): `BuildLinked.ps1`/`Publish.ps1` deploy every equip icon as its plain base,
and the RUNTIME keeps each weapon's deployed icon file matched to its own kill tier while
the game plays (out of battle, a few seconds after a save loads). Because icon textures
cache at first draw for the whole process, a file the runtime rewrites mid-session never
shows THAT session -- icons show their correct rims starting the SECOND launch after a
deploy (the first launch re-tiers the files, the second draws them).

To tour the tiers: quit the game, then

    python tools\glow_bounce.py 7      # 0 = plain, 7 = tier 1, 12 = tier 2, 15 = tier 3

`glow_bounce.py` only sets EVERY living weapon's kills.json tally to that value (cards
read it too); the same two-launch rhythm applies -- launch once to let the runtime
re-tier the files (tier 0 included, restored from its plain-base snapshot), quit, then
launch again to see it. It refuses while the game runs and never touches the
kills.json.*.bak real-tally backups;
restore one of those by hand when the tour ends.

## Bump a weapon's WP for one-hit-kill testing

Edit `data/items.json` → the item's `proposed.wp` (NOT `baseline`) → `BuildLinked.ps1` → restart the game
(table changes are restart-only). The dominance gate exempts earlier-tier items, so bumping a tier-6 weapon
(e.g. Zwill id 10) usually passes. **Revert before any real release.**

## Live memory probes

The RE instruments live in **`tools/probes/`** (rescued from `%TEMP%\fft_probes\`, which the
OS may clean — see its README for the curated index). The workhorse is `ct_probe.py` — RPM/WPM
(can't crash the game) watch/dump/hold of the battle structs. Modes: `dump`, `watch [s] [hz]`,
`hold combat|static|cond <val> <mhp> <lvl> [s]`. Used to find the scheduler CT. A probe result
that settles a mechanism claim belongs in `docs/LIVE_LEDGER.md`.

## Battle cheats (give_move / kill_all)

Two external probes in `tools/probes/battle_cheats.py`.  The game must be running; no
in-process mod required (pure RPM/WPM).

```bash
# Shell helpers (mirror FFTHandsFree/fft.sh structure)
source ./fft.sh
give_move          # grant Master Teleportation (ability 243) to the hovered unit
give_move 242      # plain Teleport instead
kill_all           # KO all enemies in the current battle

# Or call the probe directly
python tools\probes\battle_cheats.py give_move [abilityId]
python tools\probes\battle_cheats.py kill_all
python tools\probes\battle_cheats.py --selftest   # no game required
```

**give_move**: hover the target unit in-game (the condensed struct at `0x14077D2A0` mirrors
whoever the cursor is on), press Enter, and the probe fingerprint-matches via HP/MaxHP/Level
into the authoritative band (`BandReadBase`, same walk as `Wielder.Locate`), then writes the
3-byte movement bitfield at `band+0x80` (`AMovement = CMovement - BandEntry = 0x9C - 0x1C`).
Holds the grant every ~200ms until Ctrl+C (same as the DLL — engine can normalize passives
per turn), then restores the original bytes.  Default ability 243 = Master Teleportation
(proven live by Rapture, 2026-06-10).

**kill_all**: enumerates the 49-slot band (slots 0–23 = enemy-side n < 0; slots 24–48 = player-
side n ≥ 0), skips player slots and already-dead entries, and writes `HP=0` + `dead-bit (0x20)`
at `band+0x45` for each live enemy.  This is the external port of FFTHandsFree's
`CheatKillEnemiesHandler` / `KillEnemiesPlanner.Plan`.  **Porting difference**: the original
in-process handler also clears Reraise (battle-array `+0x47` bit `0x20`), which requires
cross-referencing the static battle array by HP fingerprint — not ported here because the band
alone is enough for the quick-clear use case.  If enemies revive (Reraise/undead), run twice.

## Verify a signature grant is ACTIVE

**1. The once-per-battle log is the primary check.**
The DLL logs a `[grant]` line when the bit first fires each battle. In `livingweapon.log` (the
console shows the same Info sentence without the `[grant]` bracket, and never the `[trace]`
companion since the console is Info-only):
```
[Living Weapons] [12:34:56.789] [INFO] [grant] Gloomfang bestows Concentration on its wielder.
[Living Weapons] [12:34:56.789] [DEBUG] [trace] grant detail (support ability 213, readback=SET, +0x98[1]=0x01)
```
`readback=SET` = write landed; `readback=MISS` = write failed (VirtualQuery guard rejected the address — investigate Mem.Writable) and a `[WARN] [grant]` line says the grant may not take effect.
A `build-time-only support` Warning = the ability id bakes at battle-build, so the live bit can't take effect (design bug in the signature config).

**2. Per-ability in-game oracle** (no memory tools needed):
- **Concentration** (Gloomfang) — open the attack command and preview a hit against a unit with high physical evade or a shield; the hit% should read full (100%) rather than reduced. Without Concentration, evade knocks it down.
- **Attack Boost** (Mortal Coil, below half HP) — watch the damage preview on any physical attack before and after the wielder drops under 50% HP; the number rises once the condition trips. The boost arms and stays for the rest of the battle even if HP recovers.
- **Defense Boost** (Sanguine Gauche) / **Magick Def Boost** (Hushblade) — compare incoming damage from a known attack or spell before and after the grant fires; the number drops.
- **Extra Turn** (Zwill) — behavioral; already live-verified.

**3. Redundancy note.**
If the wielder's job already has the same support picked, the log also emits:
```
[Living Weapons] [12:34:56.789] [INFO] [grant] The wielder already chose Concentration as their support; the weapon's grant adds nothing (pick a different support to benefit).
```
The grant writes the same bit that's already set — no stacking is possible (the engine reads a flag, not a count). Switch the equipped support to get value from the weapon grant.

## LW-112 kit-lane drill: prove the mod-conflict verdict live (owner, two legs)

The guard split (LW-112) claims a custom job mod no longer switches the whole mod off. Proving it
needs a real conflicting mod, so this recipe builds a throwaway one. Table changes are
RESTART-ONLY; kill fft_enhanced.exe before deploys.

**Leg 1, BAIT (no conflict mod).** Dev deploy (plain BuildLinked, NOT -Prod: Barrage needs the bow
at tier +3, and only the dev flavour seeds every weapon there), launch, load a save, put a Yoichi
Bow (id 90) on an Archer. Expect in livingweapon.log: the "Living Weapons is armed" line AND the kit-lane line "The
JobCommand table matches" with Barrage present in the Archer's Aim list, zero boxes, zero WARNs.
Without this leg a broken lane that never arms would look identical to a working conflict verdict.

**Leg 2, CONFLICT.** Create `$env:RELOADEDIIMODS\lwdrill.jobconflict\` with a minimal
ModConfig.json (ModId lwdrill.jobconflict, ModDependencies ["fftivc.utility.modloader"],
SupportedAppId ["fft_enhanced.exe"], no DLL) and
`FFTIVC\tables\enhanced\JobCommandData.xml` carrying the vanilla record whose AbilityId1..8 read
150..157 (Aim, record id 8: identify it by CONTENT, since the mod anchors on those bytes, not on
an index) with ONLY AbilityId1..8 changed to 100..107 (a real, non-zero, non-vanilla delta; the
Archer's Aim list will visibly show Monk's Martial Arts). TWO XML TRAPS, both previously live-observed: the ExtendAbilityIdFlagBits /
ExtendReactionSupportMovementIdFlagBits elements must be present BEFORE their id elements, and no
"--" inside any XML comment; either mistake makes the modloader silently drop the whole table and
the leg proves nothing (failure signature: Archer shows vanilla Aim and NO WARN appears).
Enable it in Reloaded-II, relaunch, load the same save. PASS = ALL of:
1. The "armed" line still appears (mod ON), then ~1s later ONE WARN naming jobcommand-table with
   expected 96-97-... observed 64-65-... bytes.
2. Exactly one box, headline "Living Weapons found another mod editing the same game data". NO
   "switched itself off" box; no "standing down to protect your save" anywhere in the log.
3. Barrage ABSENT from the Yoichi wielder's commands; a kill still bumps the tally (growth alive).
4. flight_*_standdown.jsonl carries a guard record containing "kit-lane stand-down".
FAILURE MAP: game-update box = discriminator failed; two boxes = ordering broke; WARN but Barrage
still grantable = the Engine lane gate is not wired; frozen tally = the lane verdict leaked into
Mem.WritesEnabled.
**Cleanup:** disable + delete lwdrill.jobconflict, relaunch, confirm the lane-armed line and
Barrage return.

## Add an extended-inventory item (a brand-new weapon past id 260, LW-346)

Plain language: a new weapon is one row in `data/items.json` plus four generated icon files;
everything else (tables, name row, glow rims, the runtime) follows from the build. The Moonblade
(id 261) is the worked example; read its row first.

1. **The row.** Append to `data/items.json` with the next free id (contiguous from 261; a gap fails
   `generate.py`). Weapons only in V1. Fill it like any living weapon (`name`, `category`, `tier`,
   `grows`, `flavorOverride`, `baseline`, `proposed` incl. `attackFlags`, `livingWeapon: true` if it
   should be gate-exempt) plus an `extended` block: `cloneDonor` (the vanilla id whose per-category
   answers it borrows: type, validity, range), `artDonor` (whose swing art it draws), `seedCount`
   (bag copies on a first install / new game), `shops` (`"Dorter, Gariland"`, the modloader's
   ItemShopsData names; `None` = nowhere), `shopAvailability` (the chapter gate; `Blank` = always),
   `palette`/`spriteId`/`requiredLevel`/`typeFlags`/`price` (the catalog record's own fields), and
   `iconSource` (the vanilla id whose SHIPPED, already-recolored icon it copies).
2. **Icons.** `python tools/bake_extended_icon_parts.py`: byte-copies the donor's `.tex` pair, writes
   the two `.utexpt` parts files (the game's pac has none past 260; the card is blank without them),
   copies the donor's three glow-rim variants and upserts `glow_icons/manifest.json`. Extended rows
   stay OUTSIDE the tint programme (`recolor_icons.py` skips them; their own look is item 11 polish).
3. **Tables.** `python tools/generate.py` (validates the row, writes `mod/extended_inventory/*.xml`;
   these are read by the DLL at boot, never by the modloader) then `python tools/analyze.py`
   (the claims gate included; read its exit code DIRECTLY, never through a pipe).
4. **Text.** `python tools/patch_names.py` seeds the Item-en row from the template (257) and bakes
   the name/description/badge like any weapon; the decode-verify must PASS.
5. **Grid.** Add the row to `docs/living_weapon_grid.csv` (the gate refuses a living weapon that is
   missing from it); obtain stays `TBD` for an extended item even when it is shop-sold (the
   gate's shop-sold checker reads only vanilla shop data and is blind to the extended `shops`
   field, so `Shop` there goes red).
6. **Build.** `.\BuildLinked.ps1 -Prod` (or the dev flavor). The boot line must read
   `Extended inventory armed: N new item(s) [...]`; a `NOT armed this session (...)` warning names
   the refusal. The runtime needs nothing per item: growth, kills, toasts, the card, the shop
   mirror and the per-save bag counts all key off the id.

Removing an item: delete its row, re-run 2-5 (delete its icon files and glow variants by hand;
`generate.py` only checks presence), and note that saves holding it lose it cleanly (the
uninstall note in docs/COMPATIBILITY.md).

## Icon recolor process (the LW-189 pipeline, reusable for every equipment pass)

Recoloring a family of equipment sprites is a settled assembly line, proven on the 121
weapons (2026-08-13). In plain terms: preview everything as pictures first, let reviewers
and then the owner judge the pictures, fix rules rather than pixels, and only bake real
game files once the pictures are signed off, with a mechanical proof that the baked files
match the approved pictures exactly.

1. Tints live in data/items.json iconTint (the single hand-edited source, all 234 items);
   the recolor ENGINES live in recolor_icons.py and are the single source of truth for how
   a tint becomes pixels. Three engines are dormant now (bright-v2, shield-bright,
   helm-two-tone; see the ramp bullet below) and one, legacy, still covers every unreviewed
   armor/accessory/hair-adornment item on its original whole-icon tint.
2. `python tools/icon_preview.py preview` decodes vanilla art and renders the engine's
   output as PNGs only (no game files). It imports the engine from recolor_icons.py, so
   preview equals production by construction.
3. `sheets` builds contact sheets for a visual QA sweep (the LW-189 pattern: fan out
   reviewers over the sheets, collapse their flags into RULE fixes or per-item overrides
   in recolor_icons.py, never hand-edit an icon; then re-preview).
4. `gallery` builds the owner's before/after HTML (flags.json in the out dir annotates
   rows amber); the owner's sign-off is the gate.
5. `python tools/recolor_icons.py [ids...]` bakes the real tex files into the mod tree.
6. `python tools/icon_preview.py verify` proves the bake's intermediate images are
   pixel-identical to the approved previews (the LW-189 weapon pass closed at 242/242).
   **Run `preview` with NO ids first.** verify reads the manifest of the LAST preview run,
   so a preview scoped to the family you just worked makes verify silently certify only
   those items. Running it at full scope for the first time (2026-08-14) is what found 58
   icons shipping art one engine version behind (docs/TODO.md LW-236).
7. Commit, deploy, owner eyeballs the cards in-game.

**THE RAMP ENGINE (LW-247, 2026-08-18).** The 150 ids that used to split across bright-v2 /
shield-bright / helm-two-tone / a chunk of three-zone (weapons 1-121, shields 128-143, helms
144-156) now render through ONE ramp engine, ported verbatim from the probe that produced the
owner-approved live-install look. Two things are new for anyone extending this family:

- **The tint stays in items.json iconTint**, same as always, but the engine's OWN per-id
  config (punch/rotate/reserved/Mode-B donor pins/etc.) lives in
  data/icon_ramp/treatments.json, and glow rims live in data/icon_ramp/rims.json. Both are
  GENERATED by `python tools/probes/lw247_emit_tables.py` (replays the exact census
  configuration that shipped, never hand-typed) and `python tools/probes/lw247_extract_bodies.py`
  (the 16 vendored body PNGs a fresh render cannot reproduce, data/icon_ramp/bodies/). Re-run
  both after any tint or config change that could move which items enter Mode B or which
  bodies need vendoring; do not hand-edit the generated JSON or PNGs.
- **Glow is an explicit knob.** `route(im, item_id, tint, surface, glow=True)` -- pass
  `glow=False` to render the body alone, no identity rim. This is how the arc gate's
  --no-glow smoke render works (there is no CLI flag for it; call `route()` or
  `recolor_icons.ramp_render(item_id, tint, surface, glow=False)` directly from a scratch
  script). LW-248 owns whether the glow-off look ever ships; do not commit its output.
- **THE ARC GATE** (run once per ramp engine change, in addition to the four gates below): a
  full bake, then a SHA-256 compare of the repo mod tree against the live install, 468/468,
  run TWICE from the already-rebaked tree (the second run is what catches a hidden mod-tree
  read the first run's own output would otherwise mask -- LW-247's B1 fix exists because of
  exactly that failure class). `compare --expect` gets the exact 150 ramp ids named, not a
  blanket "nothing moved" claim, since routing 150 ids onto a new engine legitimately moves
  every one of them once.

**THE FOUR GATES, and what each one is for.** The first runs in CI, including on the GitHub
runner that has neither the game files nor the texture tool: its two game-file-dependent
sites -- the ramp haze check that renders a real id (19) through route(), and pin 3 plus its
wiring extension pin 3b (id 130's fresh render and vendored-body/rims wiring) -- detect that
absence and SKIP loudly (`SELFTEST SKIP (game files absent): <check name>`, two lines on a
CI box, counted in the closing summary line) instead of crashing; on a dev box with the game
files present, nothing skips. The other three gates below need the game files and the
texture tool outright (no skip path), so they are run here, by hand, before the commit:

| gate | what it refuses |
| --- | --- |
| `python tools/recolor_icons.py --selftest` | a recipe that paints nothing or everything, two items in one family wearing one colour, a second material that is really a shadow, dead config for an engine an item has left, a comment naming the wrong item |
| `python tools/icon_preview.py compare --expect <ids>` | any already-approved item moving. Name the ids this pass may move; with NO ids it means "nothing anywhere may move", which is what a tooling-only or comment-only commit should prove. It refuses positional ids, because a gate that judged only what you scoped it to is not a gate |
| `python tools/icon_preview.py anchors` | an item that kept its vanilla name rendering more than 40 degrees from its own artwork without a written ruling in recolor_icons.ANCHOR_RULINGS. Ramp ids are in scope since LW-247 |
| `python tools/icon_preview.py silhouettes` | two items the artist drew with the same 48px picture wearing colours too close to tell apart. Ramp ids are judged on recolor_icons.ramp_separation_signal (body tint escaped by a distinct rim; rim alone for a reserved name) since LW-247, with known collisions the owner already approved grandfathered in recolor_icons.RAMP_SEPARATION_RULINGS |

Mutation-test any pin you add. Every gate above has been walked past at least once by an
adversarial audit, and each fix is proved by replaying the escape and watching it go red.

`tools/icon_preview.py compare --rev <rev>` re-anchors data/icon_ramp/treatments.json and
rims.json to the same revision as the engine it loads (LW-247 S9), so a historical ramp engine
judges Mode-B/reserved/punch membership the way IT shipped rather than against today's tables.
The vendored body PNGs (data/icon_ramp/bodies/) are NOT re-anchored per revision (a full binary
tree checkout was judged not worth it for a diagnostic verb); a loud warning prints when that
could matter. Comparing between the ramp arc's own commits is unaffected, since the bodies are
identical bytes on both sides of any revision pair that has them at all.

Extending to a NEW equipment family (armor, accessories): those categories still route through
the legacy whole-tint on purpose (unreviewed under the new rules). The work is choosing their
zone semantics in recolor_icons.py (what is "the metal" on a robe?), then running this exact
line. Changing an engine invalidates the approved-gallery identity for already-shipped
families; any engine change means re-previewing them too.
