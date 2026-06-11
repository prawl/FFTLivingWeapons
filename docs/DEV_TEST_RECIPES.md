# Dev / test recipes

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
  bridge). Both `FFTHandsFree` and `prawl.fft.itemoverhaul` can be loaded at once.
- **Run on a safe screen** (WorldMap / TravelList / battle). NOT while PartyMenu is open — roster-adjacent
  writes get clobbered. Close + reopen the inventory afterward to refresh the menu.

**The underlying recipe** (works via plain RPM/WPM too, no bridge needed — see `give_living_blades.py`):

```
inventory count array: count[itemId] = u8 @ 0x1411A17C0 + itemId   (verified: id257 @ 0x1411A18C1)
```

The array sits just below `RosterBase` (`0x1411A18D0`); ids 0..260 stay clear of the roster.
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

## Verify a signature grant is ACTIVE

**1. The once-per-battle log is the primary check.**
The DLL logs a `GRANT` line when the bit first fires each battle:
```
GRANT Gloomfang -> Concentration (support 213) @ +0x98[1]=0x01 readback=SET
```
`readback=SET` = write landed; `readback=MISS` = write failed (VirtualQuery guard rejected the address — investigate Mem.Writable).
`WARN build-time-only support` = the ability id bakes at battle-build, so the live bit can't take effect (design bug in the signature config).

**2. Per-ability in-game oracle** (no memory tools needed):
- **Concentration** (Gloomfang) — open the attack command and preview a hit against a unit with high physical evade or a shield; the hit% should read full (100%) rather than reduced. Without Concentration, evade knocks it down.
- **Attack Boost** (Mortal Coil, below half HP) — watch the damage preview on any physical attack before and after the wielder drops under 50% HP; the number rises once the condition trips. The boost arms and stays for the rest of the battle even if HP recovers.
- **Defense Boost** (Sanguine Gauche) / **Magick Def Boost** (Hushblade) — compare incoming damage from a known attack or spell before and after the grant fires; the number drops.
- **Charm-lock** (Galewind) / **Extra Turn** (Zwill) — behavioral; both are already live-verified.

**3. Redundancy note.**
If the wielder's job already has the same support picked, the log also emits:
```
note: wielder already equips Concentration -- the weapon grant adds nothing (pick a different support)
```
The grant writes the same bit that's already set — no stacking is possible (the engine reads a flag, not a count). Switch the equipped support to get value from the weapon grant.
