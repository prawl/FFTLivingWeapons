# Sprite / Identity Swap -- proven recipe + build gap analysis

STATUS: JOURNAL (closed research log; verify claims against LIVE_LEDGER.md before building on them)

**Status (2026-06-18):** Re-skinning a PLAYER party unit (body + portrait + name + voice, kit
untouched) is **PROVEN LIVE**. No reverse-engineering walls remain for the player / pre-battle
case. **Nothing is wired into any mod** -- this doc records the recipe for a future build.

**This spike belongs to the FFTMultiplayer project** (`Dev/FFTMultiplayer/`): it lifts the
*player-side* half of that plan's "cosmetic-locked bodies" cost. The MP-side record lives in
`Dev/FFTMultiplayer/LIVE_DRAFT_FINDINGS.md` (section 1 proof row, checklist #14, the walls section).
This doc is the detailed live-RE recipe; the probe lives here because the FFTMultiplayer live-RE
tooling (`combat_scan.py`, `roster_read.py`) already lives in this repo's `tools/probes/`.
System-of-record row: `docs/LIVE_LEDGER.md` ("Player FULL identity swap via two roster bytes").
Memory: `ic-job-id-remap`. Probe: `tools/probes/roster_sprite.py`.

---

## The recipe (proven live)

The battle unit is **built from the persistent roster blueprint at construction** (battle-entry or
Organize-screen refresh). So writing the roster BEFORE the body is built re-skins it. (The opposite
lever -- a *POST-construction* live combat-struct `+0x00` write mid-battle -- only re-skins the
status-page model preview, not the field. A *CONSTRUCTION-time* hook write DOES re-skin the field; see
the enemy note below. The dividing line is purely WHEN the write lands relative to the field build.)

**Roster blueprint:** base `0x1411A7D10`, stride `0x258`, one record per party slot. Static module
address (image base `0x140000000`, no ASLR) -- does not rebase. Full struct = Dicene `UnitData`
(clone in `%TEMP%\fftivc.editors.uniteditor\...\UnitSaveData.cs`).

Two independent levers, both leaving `Job (+0x02)` -- and therefore the command/kit -- alone:

| What it controls | Offset | Width | Example (-> Agrias) |
|---|---|---|---|
| **Body / battle model** | `+0x00` SpriteSet | u8 | `set <slot> 0x1E` |
| **Portrait + name + voice** | `+0x230` (Dicene's mislabeled "VoiceID" = the char-identity id) | u32 | `setraw <slot> 0x230 0x1E 4` |

Trigger after writing: leave/re-enter the Organize or party menu, or start a battle.

### The body menu (SpriteSetID enum)
- **Story bodies** (render independent of job): `0x01`-`0x06` Ramza/Delita chapters, `0x07` Argath,
  `0x0D` Orlandeau, `0x0F` Reis, `0x11` Gaffgarion, `0x16` Mustadio, `0x1E` Agrias, `0x1F` Beowulf,
  `0x2A` Meliadoul, `0x31` Ajora, `0x32` Cloud, `0xA2` Balthier, `0xA3` Luso, `0xA5` Argath
  Deathknight ... (full table in Dicene `Constants.cs` `SpriteSetID`).
- **Generics:** `0x80` male, `0x81` female, `0x82` monster (monster body wants a monster job).

### The identity field (`+0x230`)
Drives portrait + on-screen name + voice. Name resolves via Dicene's
`GetCharNameFromSpecialName(voiceID, +0xDC nickname)`: a unique id returns the canon name, a generic
id falls back to the nickname at `+0xDC`. Observed pattern in a live roster:
- **Uniques:** `voiceID == canonical char id` -- Agrias `0x1E`, Cloud `0x32`, Orlandeau `0x0D`,
  Beowulf `0x1F`, Meliadoul `0x2A`, Ramza `0x01`.
- **Generics:** a high name-pool id -- `0x12A`, `0x23F`, monsters `0x3xx`.

### Proof
Slot 3 = a generic male Black Mage (job `0x50`). `SpriteSet 0x80 -> 0x1E` made the BODY Agrias (kit
stayed Black Magic, portrait stayed generic). Then `+0x230 0x10F -> 0x1E` made the PORTRAIT + name
Agrias too -- a complete identity swap from two writes, no crash. Natural corroboration: slot 8 was
already shipping a Cloud body (`0x32`) on a Thief job (`0x53`).

---

## What this covers vs what it does NOT (for contrast)
This recipe is **players, pre-battle, only** -- but as of 2026-06-19 the enemy/field cases are no longer
fully walled:
- **Enemy BATTLEFIELD body: CRACKED (2026-06-19).** A *construction-time* write -- hooking
  `CopyJobEffectsToUnit @0x14EFD2F20` (fires per unit DURING construction, before the field model is built
  from `+0x00`) and writing combat `+0x00` -- re-skins the on-grid enemy model, textured (for a resident
  sprite). Done by the `prawl.fft.skinspike` mod (`Dev/FFTMultiplayer/SkinSpike/`); only the sprite swaps --
  job/class label + kit untouched, and the equipment-graphic overlay does NOT follow. See `LIVE_LEDGER.md`
  row "CONSTRUCTION-time `+0x00` write re-skins the ENEMY BATTLEFIELD model".
- **Mid-battle POST-construction live re-skin: preview-only (not the field).** A live combat-struct `+0x00`
  write AFTER the unit is built re-derives only the STATUS-PAGE model preview (texture-gated); the
  BATTLEFIELD model stays construction-welded. Job `+0x03` writes are label-only (kit welded at
  construction). Construction-time works; post-construction is too late.
- **Enemy IDENTITY (portrait/name) + a pre-construction enemy blueprint:** still walled. Enemies have no
  editable pre-construction source record (ENTD not findable in live 1.5 memory) -- the SkinSpike hook
  re-skins the body per-construction, it does not edit a blueprint. See `LIVE_LEDGER.md` Walled rows.

---

## What stands between this and a built mod

**The hard part is done.** No RE walls remain for the player cosmetic swap; the addressing is static
(no pointer-chain/AoB needed). What's left is design + a little QA + plain engineering.

### Design decisions (pick before building)
1. **Persistence / save policy -- the main knot.** The roster IS the save. A write persists once the
   player saves. For a cosmetic re-skin that is arguably *desirable* (it sticks), but it edits a real
   save (the repo has a save-pollution incident in its history). Decide: permanent save edit
   (write-once + re-assert on load) vs live RAM-only overlay (hold, never let it reach disk). Provide
   a clean revert/uninstall either way.
2. **Unit keying.** Slot index is fragile (the roster reorders). Key the re-skin by the original
   `voiceID` or by nickname, not by slot, so a config survives a party reshuffle.
3. **Which mod / where it lives -- DECIDED: FFTMultiplayer.** This is part of the `prawl.fft.multiplayer`
   coordinator, not a Living Weapon feature (a cosmetic re-skin is thematically off for weapon-growth).
   It removes the *player-side* half of the MP plan's cosmetic-lock cost; the puppeted stock-enemy side
   stays walled. Fold the re-skin into the coordinator's battle-start setup alongside the puppet +
   kit-stamp passes.

### Verification spikes (QA before trusting it broadly)
1. **Crash-safety matrix.** Only Agrias-on-BLM is proven. Sweep representative body x job combos
   (story body on each generic job, monster body on a human job, etc.) for glitch/crash before
   exposing arbitrary choices. Ship an allow-list, not a free-for-all.
2. **`voiceID` side-effects.** Setting it to a unique id duplicates that identity (we made a 2nd
   Agrias while the real one sat in slot 12). Verify no story-flag / recruitment / save-integrity
   weirdness, especially when the swap collides with an existing unique. Generic->generic (different
   pool id) is the safest variant.
3. **Palette (`+0x03`).** Unexplored. Check whether re-skins need a palette match for correct colors.
4. **Scope.** Confirmed: battle model + party menu. Unconfirmed: world-map sprite, cutscenes,
   formation preview. Decide whether those matter.

### Pure engineering
- Config schema (identity-keyed: target -> {SpriteSet, voiceID, optional palette}).
- Write path: one-shot on save-load, or a write+assert tick if the engine ever stomps it (it
  shouldn't -- the roster is the source). TDD behind `IGameMemory` like the rest of the runtime.
- Revert/uninstall restores the captured originals.

**Bottom line:** zero RE blockers for the player cosmetic swap. The gating items are one design call
(save-persistence policy), two QA spikes (crash matrix, `voiceID` side-effects), and standard mod
plumbing. Enemy / mid-battle re-skin remains a separate debugger spike and is out of scope for a
player-cosmetic mod.

---

## Probe / addresses quick ref
```
roster base 0x1411A7D10  stride 0x258   (static, no ASLR)
  +0x00 SpriteSet (u8)   body
  +0x01 UnitIndex (u8)   roster position (NOT identity)
  +0x02 Job (u8)         kit -- DO NOT TOUCH for a cosmetic swap
  +0x03 Palette (u8)     unexplored
  +0xDC Nickname (16B)   generic fallback name
  +0x230 voiceID (u32)   portrait + name + voice (the identity id)

tools/probes/roster_sprite.py
  (no args)                 dump populated slots
  ids                       dump identity fields (UnitIndex/Flags/SpriteSet/Job/voiceID/nick)
  set <slot> <spriteHex> [palHex]      write SpriteSet (+/- Palette), prints revert
  setraw <slot> <offHex> <valHex> [w]  write an arbitrary field, prints revert
```
