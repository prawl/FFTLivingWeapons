# Don't-Lose-The-Weapon: keep broken gear & stop crystallization

Feasibility research for two requested Living Weapon features:

1. **Keep broken equipment** — when an enemy Breaks/Rends a weapon, shield, or armor, lose it only for
   *that* battle, then return it permanently afterward (not destroyed forever).
2. **Stop crystallization** — bypass the "3 strikes → crystal/treasure" permanent unit loss; keep the KO'd
   party member.

**Bottom line:** both are feasible without any new engine capability. The Living Weapon runtime already
*detects a battle state by polling and writes a byte back* every 100ms (that's literally `GrowthEngine`).
Neither feature needs hooks/detours — only battle enter/exit bookkeeping. **Both use the same trick:** a
nameId-keyed roster snapshot taken on battle-enter, selectively restored on battle-exit.

---

## Why the runtime is the right layer (and the data tables are not)

The data/table route for Feature 1 would mean neutering the Knight's Rend/Break abilities. That's the wrong
tool twice over:

- This mod's ability tables (`ability.en.nxd` / `overrideabilityactiondata.nxd`) are **parked** because
  editing them corrupted unrelated abilities (Fire range, Red Mage wipe, new-game crash) — see
  `docs/UNIMPLEMENTED_MECHANICS.md`. Reviving them for this is high-risk.
- A table edit can only disable break *entirely* or do nothing. It cannot express "lost for this battle,
  returned after." Only a runtime restore can.

The runtime path reuses proven infrastructure: `Engine.cs` battle enter/exit detection, `Mem` (RPM/WPM-backed,
VirtualQuery-guarded — a bad/freed address is a no-op, never a crash), and the exact roster `+0x14` equip
write `GrowthEngine` already performs and that persists to save.

---

## Feature 1 — Keep broken equipment

**Verdict: likely feasible (medium confidence).** The whole bet rides on one battle-end timing question.

**Approach — snapshot-and-restore on the persistent roster** (`0x1411A18D0`, stride `0x258`):

- **Battle ENTER:** snapshot every populated player roster unit's 7 equipment `u16`s, keyed by
  **nameId (`+0x230`), not roster index** (slots reorder; nameId is the stable key `KillTracker`/
  `GrowthEngine` already use). Equip offsets:

  | Offset | Slot |
  |--------|------|
  | +0x0E | head |
  | +0x10 | body |
  | +0x12 | accessory |
  | +0x14 | right-hand weapon |
  | +0x16 | left-hand |
  | +0x18 | reserved |
  | +0x1A | shield |

- **During battle:** do nothing. Let the break play out — the unit fights without that piece, exactly as
  requested.
- **Battle EXIT:** for each snapshotted unit, re-find its slot by nameId and write back **only** slots that
  were non-empty in the snapshot **and** read empty (`0xFFFF`) now. The "non-empty-then AND empty-now" guard
  is mandatory: it avoids clobbering deliberate post-battle re-equips, and avoids forcing gear onto a unit
  that crystallized away (no longer in the roster).

**Known vs unknown:**

- *Known/mapped:* all 7 equip offsets, the nameId key, battle enter/exit edges, the guarded write primitive.
  No new offset discovery required.
- *Unknown — confirm in-game first:*
  1. Does Rend write `0xFFFF` into the **persistent** roster mid-battle, or only into the in-battle struct
     with a copy-back at battle end? (Decides where to diff and when the loss commits.)
  2. **The timing race** — the loss commits when the game copies battle equipment back to the roster at
     battle-end reconciliation. The restore must land *after* that copy-back. `Engine`'s current exit edge
     (`slot9` clear) is coarse and may fire before the reconcile, so likely delay the restore a few ticks
     past the transition (mirror the existing `FieldSettleSeconds` settle window) and verify-then-write.
  3. Does the next-battle stat resolver re-validate equipment and silently drop an item the current job can't
     wield? (Restoring the exact pre-break value mitigates this unless the player changed jobs in between.)
  4. Coordinate with `GrowthEngine` — both writers can target `+0x14` on the same exit tick; they must agree
     on the canonical id (snapshot may hold a pre-upgrade weapon).

**Risks:** the timing race is the only real one, and it is naturally fail-safe — a missed/early write just
leaves the item lost (vanilla behavior), never corruption. Scope note: break and steal empty the slot
identically, so this silently returns *stolen* gear too. Probably desired, but own it.

---

## Feature 2 — Stop crystallization

**Verdict: needs reverse-engineering for the "ideal" path; a low-risk path exists today.** The obvious move —
pinning the death counter — is the trap.

**Recommended primary: post-battle roster-restore (same snapshot infra). NOT counter-pin, NOT HP-floor.**

Why counter-pinning (hold the death timer at 3 every tick so it never hits 0) is the wrong lead:

- The death-counter offset is **unmapped in every public IC source** (FearLess Cheat Engine, OpenCheatTables,
  WeMod, dicene's FFT_Egg_Control — all checked, none expose it). FFHacktics notes the PSX original kept the
  counter in a *separate small per-unit byte array* (PSX staging `0x8019FD26`), **not** the main unit struct —
  so in IC it may not sit at a clean `0x140893C00 + slot*0x200 + k` offset at all.
- It is **unverified** whether crystallization even reads a poll-pinnable byte vs. firing inside an atomic
  turn-resolution routine a 100ms poll can't pre-empt.
- **Irreversible failure mode.** Every other thing the DLL writes is cosmetic/recoverable. One missed pin =
  a permanently deleted party member. The blast radius dwarfs anything the runtime does today. The loop is
  `Task.Delay(100)` (not frame-locked), and the `Display` ByteScan on the same thread can stall it well past
  100ms — if a stall coincides with the KO'd unit's turn, the pin misses.
- It does **nothing for Ramza** (his KO is a direct game-over, no counter).

The alternatives, ranked:

- **Post-battle roster-restore (best):** snapshot every player roster entry (nameId/level/equipment/stats) on
  battle ENTER; on EXIT re-write any roster slot the game zeroed/crystallized-away. **Zero new offset
  discovery** (all roster fields already in `Offsets.cs`), survives a missed poll (it's an end-of-battle
  reconcile, not a per-tick race), and inherently covers the party. Its one risk is the same battle-end
  timing window as Feature 1.
- **HP-floor (poll HP==0 → write HP=1):** trivial (`KillTracker` already reads HP per slot) but *over-delivers*
  — it eliminates real tactical KOs entirely and may fight the engine's own per-tick KO application. Use only
  if losing the tactical KO is acceptable.
- **Counter-pin:** only if the empirical test proves the byte exists, is poll-pinnable, and gates the event —
  and even then keep post-battle-restore as the safety net.

**Ramza caveat (unavoidable, state it plainly):** Ramza crystallizing = *instant Game Over mid-battle*, which
fires before any battle-exit restore can run. Post-battle restore cannot save a Ramza in-battle Game Over. The
only thing that covers him is a **narrow HP-floor on Ramza specifically**. So full coverage =
party post-battle-restore **+** a Ramza-only HP floor. Keep counter-pin away from Ramza entirely.

**The gating unknown (could kill Feature 2):** *when* does the game zero the persistent roster slot on
crystallize — mid-battle, or at battle-end copy-back? If mid-battle, there is nothing left to restore on exit
and the post-battle-restore path is dead.

**RE plan for the counter offset (only if you pursue counter-pin):** in a throwaway battle, KO a non-Ramza
unit and don't revive it. Each tick, snapshot that unit's full `0x200`-stride slot at
`0x140893C00 + slot*0x200` and diff across its skipped turns, watching for the byte that steps `3→2→1→0`.
Search near known fields first — `+0x25` (CT), the `+0x45` status bitfield (byte0 `0x20` = Dead,
`0x40` = Crystal). If it's not co-located in the `0x200` stride (per the PSX layout note), the search space is
larger. Then pin it to 3 across several of the unit's turns: does it crystallize anyway (→ event reads other
state, abandon counter-pin), and does it stay Phoenix-Down-revivable?

---

## Shared infrastructure & sequencing

- **Both features need one component:** a battle-enter roster snapshot keyed by nameId. Build it for Feature 1;
  Feature 2's post-battle-restore reuses it directly (just snapshot more fields — level/job/stats alongside
  equipment).
- **Both share the same battle-end restore-timing window.** Solve it once (delay past the settle window,
  verify-then-write) and both unblock.
- **Ship Feature 1 first** — every byte it touches is already mapped, no per-tick writes (snapshot on enter,
  restore on exit), fail-safe failure mode. Rough effort: a focused day once the timing test passes.

## The one test that unblocks both

In a throwaway battle, log roster `+0x14` for a unit through (a) an enemy Rend and (b) a KO-to-crystal, and
record *when* the roster slot goes `0xFFFF`/zero relative to `Engine`'s exit edge. That single trace answers
the timing race for Feature 1 and the gating unknown for Feature 2.

## Implementation notes

- Thread new logic through `IGameMemory` (`LivingWeapon/GameMemory.cs`) rather than calling `Mem` statically,
  so it stays unit-testable; write the failing `LivingWeapon.Tests` (xUnit) case first, then the code (the
  test gate is enforced by `BuildLinked`/`Publish`/CI).
- Snapshot/restore is pure logic over memory reads/writes — a fake `IGameMemory` can simulate a break
  (slot → `0xFFFF`) and assert the restore writes back the original id only when the slot is empty.

## References

- `LivingWeapon/Engine.cs` — battle enter/exit edges, `FieldSettleSeconds` settle window, exit flush.
- `LivingWeapon/GrowthEngine.cs` — the proven roster `+0x14` write lever, nameId/fingerprint matching.
- `LivingWeapon/Offsets.cs` — roster + static battle array offsets.
- FFTHandsFree `docs/UNIT_DATA_STRUCTURE.md`, `docs/BATTLE_MEMORY_MAP.md` — roster equip offsets, static
  battle array fields (`+0x45` status bitfield), inventory-not-located status.
- FFTHandsFree `docs/Wiki/StatusEffects.md` — KO/death counter (starts at 3) → crystal/treasure rules,
  Safeguard prevents break/steal, Ramza death = Game Over.
- FFHacktics Wiki (World Stats / Miscellaneous Unit Data) — PSX counter stored in a separate per-unit array.
