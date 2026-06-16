# Session Handoff — Mechanics R&D: spawn walled, crystal counter cracked, staff set proposed (2026-06-16)

A **research/exploration** session. **No production `.cs` changed, no gates touched, nothing deployed.**
Four commits, all probes + docs. The deployed Reloaded folder is still the **prior DEV Larceny build**
(thresholds {1,2,3}, every weapon force-seeded to +3 — re-Prod before any release).

```
4286509  Correct the Ramza game-over assumption; record counter-pin gating GREEN   (last commit)
2c1fd44  Map the crystal countdown counter (combat +0x07)
ecf1e9a  Add roster-loss trace; resolve break-commit timing for Bait-n-Switch
442c713  Add spawn-unit probes; record the render-layer wall
```

## Thread 1 — Spawn a new unit mid-battle: **WALLED at the render layer**
- The CT scheduler **adopts** a hand-written band slot — clone a unit into an empty player-range seat
  (**16–27**, 27 = ~28-unit array cap) and it **enrolls in the Combat Timeline** (`0x140d3a04c`, 4-byte
  records, insert-shifts). Empty band slots are RESIDENT and filled IN PLACE (no realloc).
- But it **renders blank** and the timeline-detail view **AVs**. A byte-identical clone of a *rendering*
  donor still renders blank ⇒ the drawable identity is **external** — an init-built, identity-keyed
  graphic object (scene-graph + double-buffered float geometry at `0x140f8c…`/`0x141140…`/`0x142eb…`),
  not in the 0x200 slot and not a single forgeable pointer. Overwriting a corpse's identity DE-syncs it.
- **Verdict:** brand-new *rendered* unit needs a debugger (x64dbg/CE) on the render path. Feasible
  consolation = **reanimate a fallen ally** (its own graphic). Full journal in `UNIMPLEMENTED_MECHANICS.md`.
- Probes: `formation_diff.py` (region snap/diff, captures the instantiation recipe), `clone_probe.py`
  (`slots`/`dryrun`/`clone`[`--full`/`--corpse`/`--hold`]/`enrolldiff`).

## Thread 2 — Anti-loss features (`docs/NOT_LOSE_WEAPON.md`)
- **Bait-n-Switch (keep broken gear) = GREEN.** A break empties the in-battle copy live, but the
  **persistent roster** (`0x1411A18D0`) only commits the loss **out of battle** (`battleMode=0`, the
  party-menu reconcile ~13 s after exit). **Empty sentinel = `0x00FF`** (not 0xFFFF). **Quitting reverts
  the break entirely** — only a *won* battle commits. ⇒ snapshot-on-enter / restore-on-exit works.
- **Divine Intervention (no crystallization) — counter FOUND + pin works.** The **"3 hearts" death
  counter = combat-slot base `+0x07`** (steps 3→2→1→0, once per the dead unit's turn — the offset prior
  IC sources had "unmapped"). **Holding it at 3 keeps the unit at 3 hearts; it does NOT crystallize**
  (Patrick-confirmed live). **Ramza is NOT special in IC** — he counts down the same counter (no instant
  game-over), so the pin covers him too (old "Ramza caveat" was a PSX-wiki assumption — corrected).
  - **OPEN:** confirm (a) Phoenix-Down-while-pinned still revives, (b) post-battle recovery returns him —
    then Patrick flips the `LIVE_LEDGER` row to **PROVEN**.
  - Note: crystallization does **not** touch the stat-roster at all (watched every byte 6.5 min) — unit
    **membership lives in a separate, unlocated structure**, so the post-battle-restore path has no target
    there. The **counter-pin is now the cleaner Divine Intervention.**
- Probes: `roster_loss_trace.py` (dual-watch roster + in-battle band), `crystal_counter_probe.py`
  (`list`/`watch`/`pin`).

## Thread 3 — Mechanic feasibility verdicts (for `docs/TODO.md` "Needs Exploration")
| Idea | Verdict | Note |
|---|---|---|
| **Support Aura** (share your support w/ adjacent allies) | FEASIBLE — 1 probe | read roster `+0x0A`, OR-hold support bit `+0x98` on adjacent allies. Needs **team-ID probe**. |
| **Unlock Potential** (random ability to adjacent ally) | FEASIBLE — 1 probe | **passives only** (commands are job-global → would buff enemy Thieves). Needs **team-ID probe**. |
| **Steal Identity** (copy enemy gear) | FEASIBLE — build as STAT transplant | hold wielder CPa/CMa/CSpeed to the foe's; literal weapon-swap likely cached (probe via `give_enemy_buffs.py`). |
| **Damage by "height"** | elevation needs 1 probe | **elevation** (high-ground) = flavorful, needs a `heightfind` decode of terrain grid `0x140C65000`. **level** ships now but is a weak snowball. |
| **Spells can't hit friendlies** | **WALLED** | ability-targeting logic (parked table); no holdable state. Premise also off — IC Summon already friendly-fires. |
| **Always rain** | **WALLED** | weather chosen at map-load, baked into the scene graph (same wall as spawn-render). Debugger only. |

## Thread 4 — Staff P3 "guardian set" (proposed, NOT yet written to grid/items.json)
Staves currently run generic **MA +10/20/30%**. Proposed curated P3 signatures (iconic picks; rest stay generic):
- **Mending Staff (T3) → "Wellspring"** — Regen aura to adjacent allies. *Probe: does the Regen bit heal live (Float lesson)?*
- **Warding Staff (T3) → "Communion"** — the **Support Aura** (share your support). *Probe: team-ID.*
- **Sanctus Staff (T4) → "Martyr's Vow"** — Guardian's Oath (redirect an adjacent ally's lethal hit). Proven levers, but **reactive** (ally drops then snaps back, wielder bleeds — no pre-emptive intercept).
- **Staff of the Magi (T6) → "Sanctuary"** — Divine Intervention (nearby allies can't crystallize). Uses the counter-pin.
- Left generic (own identities): Birchwood (starter), Warlock's (Reflect), Blazing (Fire bolt), Zeus Mace (Lightning).

## NEXT-SESSION PRIORITIES
1. **Confirm the Divine Intervention revive loop** (Phoenix-Down-while-pinned + post-battle recovery) → flip the counter `LIVE_LEDGER` row to PROVEN. Closest thing to "done."
2. **Build the team-ID probe** (extend `status_probe.py`: dump every band slot's gx/gy + team-candidate bytes + fingerprint to find a clean ally/enemy filter). **Unblocks Communion, Unlock Potential, and Wellspring targeting at once.**
3. If those green: **lock the staff guardian set into `living_weapon_grid.csv` (sigNote/P3) and build the DLL signatures** (one ISignature + one route entry each, TDD).
4. Optional probes: `heightfind` (elevation damage), the weapon-swap test (Steal Identity literal path).

## New probes this session (`tools/probes/`)
`formation_diff.py`, `clone_probe.py`, `roster_loss_trace.py`, `crystal_counter_probe.py` — all READ-only
except `clone_probe`'s clone/enroll writes and `crystal_counter`'s `pin` (single guarded byte, reverts).

## Load-bearing addresses found this session
- Combat Timeline list `0x140d3a04c` (4-byte records). Player-injectable band seats **16–27**.
- Persistent-roster break commit: `battleMode=0`, empty sentinel `0x00FF`.
- **Death/crystal counter = combat-slot base `+0x07`** (band entry −0x15).

---

## Carried over — Larceny arc (committed `053735a`, still OPEN)
1. **Multi-wielder Larceny** (the big one): two DEPLOYED Arcanum wielders make `ResolveDeployedMainHand`
   bail ambiguous → no steal. FIX = per-wielder ledgers keyed by the ACTING attacker (KillTracker resolves
   who acted); pushes `Larceny.cs` over 200 → seam split.
2. **Functionally test Protect / Shell** (`+0x48/0x20`, `+0x48/0x10`) — wired + live but icon≠effect; steal
   via `give_enemy_buffs.py`, confirm reduced damage (Float lesson). Reraise/Regen/Reflect/Haste = proven.
3. **Re-Prod before release** (`BuildLinked.ps1 -Prod` / `Publish.ps1`) — dev build seeds +3; probes mutated
   live state. Eyeball `kills.json` for dev-seed pollution.

## Pre-existing open items (unchanged)
Siren's Lyre charm bug, phantom kill tallies, Treasure Master release runbook. ~13 weapon categories still
carry DRAFT signotes (Knives, Bows, Rods, Swords curated/shipped; **Staves now have the proposed guardian
set above, pending the team-ID probe**).
