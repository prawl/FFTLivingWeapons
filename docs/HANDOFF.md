# Session Handoff — Larceny shipped: live steal, Haste proven, wielder-turn expiry (2026-06-16)

The whole **Larceny / Arcanum (+3) buff-steal** arc is **COMMITTED and verified live**. This session
proved Haste functional, reworked the expiry to the wielder's own turns, and fixed the wielder-locate.

```
053735a  Land Larceny buff-steal: live steal, mapped buffs, wielder-turn expiry   (last commit)
d900bc1  Add Shadow Blade sword signature; rework Larceny expiry to global turns
```

Both gates green: `analyze.py` PASS, `dotnet test` **1094 passing**. The deployed Reloaded folder is a
**DEV build** (thresholds {1,2,3}, every weapon force-seeded to +3).

## What landed (053735a)

- **Steal works live** — strike a buffed foe with a +3 Arcanum → its highest-priority holdable buff is
  stripped and worn by the wielder, then fades. Transfer + expiry + the multi-target sweep all confirmed.
- **Haste is FUNCTIONAL** (proven this session). Haste has **no walk animation** — its whole effect is
  +50% CT. `tools/probes/haste_ct_probe.py` measured the hasted wielder gaining **+16 CT/tick vs +11**
  for same-Speed allies = the 1.5× multiplier, on both a real cast and a Larceny-stolen bit. The
  "icon-but-no-animation" fear was a phantom.
- **Expiry = 3 of the WIELDER's own completed turns** (`TurnTracker.Turns` for the wielder's
  fingerprint — the proven acted-edge counter). Replaced the global-turn clock (didn't fade in a normal
  fight) and a 60s wall-clock cap (fired during deliberation). No backstop needed: a deployed wielder
  always takes turns (you can't bench mid-battle).
- **Wielder locate** = the single DEPLOYED Arcanum main-hand holder (`Wielder.ResolveDeployedMainHand`).
- **Innate strip-proc removed** (onHit 55); card = `+3 Ability — Larceny — Any attack steals 1 buff from
  the foe for the wielder to wear 3 turns`.

### Buff functional status (THE key table)
| Buff | Bit | Functional? |
|---|---|---|
| Reraise | +0x47/0x20 | **YES** (FeignDeath-proven) |
| Regen   | +0x48/0x40 | **YES** (heals each turn — live) |
| Reflect | +0x49/0x02 | **YES** (bounces magic — live) |
| Haste   | +0x48/0x08 | **YES** (+50% CT — proven 2026-06-16) |
| Protect | +0x48/0x20 | **PENDING** functional test |
| Shell   | +0x48/0x10 | **PENDING** functional test |
| Float / Invisible | +0x47/0x40, /0x10 | **DROPPED** (cosmetic / player-only) |

Precedence (`LarcenyPolicy.Stealable`): Reraise > Haste > Protect > Shell > Reflect > Regen.

## THE OPEN ITEMS

1. **Multi-wielder (the big one, deferred by Patrick to next).** Two DEPLOYED Arcanum wielders make
   `ResolveDeployedMainHand` bail as ambiguous → **no steal at all** (diagnosed live: Ramza br97/fa75 +
   a BLM br89/fa76 both armed). Single-wielder works today (unequip duplicates). FIX = per-wielder
   ledgers attributed to the ACTING attacker (KillTracker already resolves who acted). Today Larceny is
   one `_state`/`_wielderAddr`; this needs a `Dictionary<wielderFp, state>` and pushes `Larceny.cs` over
   200 → a seam split. In `docs/TODO.md`.
2. **Functionally test Protect / Shell.** Wired + live but icon≠effect. Use
   `tools/probes/give_enemy_buffs.py`, steal each, confirm reduced damage — drop any cosmetic like Float.
3. **Before release: re-Prod** (`BuildLinked.ps1 -Prod` / `Publish.ps1`). The dev build seeds +3, and the
   `give_enemy_buffs` / `myturn` probes mutated live state. Eyeball `kills.json` for dev-seed pollution.
4. **`docs/LIVE_LEDGER.md`** — the Haste-functional finding is ready to log (Patrick owns PROVEN flips).

## Testing gotchas (cost real time this session)
- **`battle_cheats.py myturn` sets EVERY player's Speed to 99**, so turns rotate through the whole party —
  "3 turns" of mashing is NOT 3 of the wielder's turns. To test the wielder-turn expiry, act with the
  wielder **specifically** 3 times (watch the `unit (level .. brave .. faith ..) completed a turn` line
  for the wielder's fingerprint), or play without the cheat.
- **A display bit is NOT the effect** (Float lesson). Haste passed; test Protect/Shell the same way.
- **`haste_ct_probe.py`**: read the single-tick CT step ÷ Speed (~1.5 = Haste works). Player CT byte
  `+0x25` reads between menus; `+0x09` is flat 0 for players (the documented wall).

## New probes (`tools/probes/`)
`status_probe.py` (decode any band unit's statuses), `haste_ct_probe.py` (CT-rate Haste functional test),
`larceny_locate_probe.py` (roster Arcanum slots vs band entries), `give_enemy_buffs.py` (arm enemies for
steal tests).

## Load-bearing findings (baked into code/comments)
- **Expiry counts the WIELDER's own turns via `TurnTracker.Turns`** — the acted-edge per-unit counter,
  hover-resistant. NOT the active-unit/CT poll (the TurnQueue follows the cursor) and NOT wall-clock
  (fires during deliberation).
- **Larceny holds on the DEPLOYED wielder**, not "the one roster wielder."
- **Per-battle state resets on battle ENTER**, not only EXIT (a restart skips a clean Exit).

## Pre-existing open items (unchanged)
Siren's Lyre charm bug, phantom kill tallies, Treasure Master release runbook. ~13 weapon categories
still carry DRAFT signotes (only Knives, Bows, Rods, and Swords are curated/shipped).
