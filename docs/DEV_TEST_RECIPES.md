# Dev / test recipes

Throwaway conveniences for testing this mod live. None of this ships — it's harness only.

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

## Live memory probe

`%TEMP%\fft_probes\ct_probe.py` — RPM/WPM (can't crash the game) watch/dump/hold of the battle structs. Modes:
`dump`, `watch [s] [hz]`, `hold combat|static|cond <val> <mhp> <lvl> [s]`. Used to find the scheduler CT.
