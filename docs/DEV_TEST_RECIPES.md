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
**Skip the crashy / IC-unused ids:** `262` (Onion Sword crashes on equip-render) and `261, 263–277`
(IC stripped these slots). `give_all_items` already skips them; replicate the skip if you write your own.
Caveat: only ids whose IC layout matches FFTPatcher-canonical render in the menu (~80–85%); the rest write
into RAM but don't show.

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
- **Charm-lock** (Galewind) / **Extra Turn** (Zwill) — behavioral; both are already live-verified.

**3. Redundancy note.**
If the wielder's job already has the same support picked, the log also emits:
```
[Living Weapons] [12:34:56.789] [INFO] [grant] The wielder already chose Concentration as their support; the weapon's grant adds nothing (pick a different support to benefit).
```
The grant writes the same bit that's already set — no stacking is possible (the engine reads a flag, not a count). Switch the equipped support to get value from the weapon grant.
