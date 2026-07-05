# Speeding up the Kills counter paint -- research + proposal

STATUS: JOURNAL (closed research log; verify claims against LIVE_LEDGER.md before building on them)

> Research-only findings (2026-06-26). No code changed. Question posed: the per-weapon "Kills: NNNN"
> counter takes a noticeable beat to appear on the equip/Status card; can we write it somewhere faster --
> e.g. the in-battle command menu (the "current unit's card" with the unit name + Move/Abilities/Wait/
> Status/Auto-battle)? Short answer: the command menu does NOT remove the delay and re-opens an already-
> scrapped surface; the fix is to keep the card and kill the SEARCH cost. Details below.

## Root cause: the delay is a SEARCH, not a paint

The "Kills: NNNN" string is baked into each weapon's item description by `tools/patch_names.py` (a fixed-
width placeholder, anchored to the weapon's unique flavor line). The runtime never creates that text -- it
finds the description buffer on the heap and overwrites the 4 digits in place (`CardSites.PaintSiteWithResult`,
`LivingWeapon/CardSites.cs:138-161`; slot validator `LivingWeapon/ByteScan.cs:50-74`).

That description buffer lives on the dynamic UE heap with **no stable address**, so every paint must first
*find* it via a budget-throttled generational heap walk (`LivingWeapon/DisplaySweep.cs`). The throttle:

- 16 MB/tick out of battle, 8 MB/tick in battle, one tick per 33 ms (`LivingWeapon/Display.cs:33-34`,
  `LivingWeapon/Engine.cs:19`). A cold full pass over an `H`-byte private heap costs ~`(H/budget) * 33 ms`
  -- realistically ~1-4 s out of battle, ~2-8 s in battle, scaling with the (unmeasured) heap size `H`.
- `GenerationMinGapMs = 5000` (`DisplaySweep.cs:30`, gated `:92-103`): a card-open-triggered re-find can
  stall up to ~5 s before the new walk even starts.
- `GenerationRestMs = 90000` (`DisplaySweep.cs:29`): after a completed pass, no restart for 90 s unless an
  Invalidate is pending.

The scan CPU itself is NOT the bottleneck anymore (common-case chunk = two cheap `IndexOf` passes,
`LivingWeapon/CardScanner.cs`); the old design did a synchronous ~9 s full-heap freeze on the engine loop
(`DisplaySweep.cs:7-8`) and was traded for this bounded coverage-latency. Compounding it: the menu
reallocates the buffer on every open / unit-change / battle re-enter, and the Status-card edge **deliberately
wipes the exact-address cache** (`Display.Invalidate` -> `_sites.Clear()`, `Display.cs:84-89`, called at
`Engine.cs:176`), forcing a re-find even when the buffer did not move.

Contrast a stable image-resident address: `WpScratchPainter.Paint` (`LivingWeapon/WpScratchPainter.cs:30-45`)
writes the boosted WP to the fixed `0x141876E96` every tick with zero sweep, ownership-guarded. No search,
one-tick write. Anything we want "instant" needs that property: a stable address (or a cheap-to-locate +
cacheable one), not a fresh heap string we have to hunt down each time.

## Verdict on the command menu ("current unit's card")

**No -- it does not remove the delay, and it re-opens a surface that was already proven a dead end.**
Confidence: high that it is not a quick win; medium that it is viable at all without a fresh RE spike.

Three independent reasons:

1. **The mod can only overwrite glyphs the engine already drew; it cannot make new text appear.** This is the
   hard-won lesson from the level-up banner (`memory/living-weapon-levelup-banner.md`: "our Display/Mem code
   only overwrites already-rendered text -- it cannot make new text appear"). The command menu has **no
   pre-baked "Kills:" slot** to hijack. The screenshot's red "Kills: 100" element is *new* text -- the same
   wall that parked the banner and the floating heal/damage numeral (`docs/LIVE_LEDGER.md`, Walled rows).

2. **No stable address there either, so no escape from the search.** Everything fixed-address that identifies
   the active unit holds the *nameId integer*, never a string: the turn-queue/condensed struct
   (`Offsets.TurnQueue 0x1407832A0`, `+0x04` nameId -- itself a flagged trap, a battle index that once
   mis-credited all kills to Ramza) and the UI mirror buffer (FFTHandsFree `0x1407AC7C0 +0x04 NameId`). The
   visible "Reis" is rendered from that number through a glyph/FName-FText pipeline; FFTHandsFree cannot read
   it from memory at all (it rebuilds via `UnitNameLookup.GetName(nameId)` and falls back to OCR in
   `InfoPanelParser.cs`). Enemy names return zero byte-pattern hits in writable memory. The only writable
   name string (the heap Roster Name Display Table, 0x280 stride, name at +0x10, AoB-anchored) is also heap-
   located (no speed win) and shared across the party/status screens (overwriting it corrupts the name
   everywhere, not just the menu).

3. **This exact surface was mapped and SCRAPPED on 2026-06-23 (`docs/research/UI_GREY_HOLD.md`).** The command-menu
   widgets are `0x170`-byte records in a launch-stable heap arena (documented base `0x436BFDE4F0`), each with
   a grey flag at `+0x1C` and a u64 pointer at `+0x20`. Two findings that gut the proposal:
   - The `+0x20` pointer targets the widget's UE4 identity/debug name ("GrayOut#3", "TextTitle#4",
     "RadioButtonBattleMenu", "CommandBg") -- NOT the displayed unit name or menu label. The actual rendered-
     text buffer is unmapped. Finding it is fresh RE.
   - The grey flag on this same arena proved to be **engine-re-derived every ~16 ms render frame** -- a
     downstream render output, not a holdable byte. Forcing all 123 correlated UI-arena bytes "changed
     nothing." Even an 8 ms FastHold thread won the write race byte-wise but the screen did not change. If the
     menu's text strings behave the same way, a paint there will not hold. The equip/Status card does not have
     this problem because it is painted while **paused** (`PauseFlag==1`, `battleMode==3`, `SubmenuFlag==1`),
     so one write sticks.

Plus a UX narrowing: the command menu only exists on the active unit's own turn in live battle (gate
`MenuSubState 0x140C6B1CC == 4`, `BattleMode 0x1409069A0` in {2,3,4}), whereas the card is inspectable any
time in menus.

Net: the command menu trades "expensive to find" for "must find a new (unmapped) text buffer + must fight the
renderer every frame + must safely write variable-length new text," on the one surface whose render bytes
already proved unholdable.

## Ranked options

### Option A -- Keep the card; stop wiping the address cache (cached-address fix)
- **How:** On the Status-card edge (`Engine.cs:176`), call `_sweep.Invalidate()` but do NOT `_sites.Clear()`
  (`Display.cs:84-89`). Let the next `PaintAll` re-verify cached sites; `AnchorIsLive` (`CardSites.cs:123-135`)
  already evicts moved/freed/reused buffers safely (anchor-byte mismatch or RPM miss -> evict, never
  mis-paint). Optionally raise `HotChunkSet.HotTtlMs` from 10 s (`LivingWeapon/HotChunkSet.cs:18`).
- **Latency:** repeat opens within a session -> ~instant (<=33-100 ms), IF the buffer address persists. Does
  nothing for the genuinely-cold first open of a session.
- **Effort:** low (a few lines + a test). **Risk:** low -- the eviction path is the same one maintenance runs
  every tick; RPM/WPM keep it crash-safe.
- **Prove live first:** that the found "Kills:" slot address repeats across opens (same unit, then different
  units). If it jumps every open, the benefit collapses to 4 MB hot-chunk granularity.

### Option B -- Keep the card; fat paused budget + bypass the 5 s min-gap (cold-open fix)
- **How:** thread a "paused/card-open" signal into `Display.Tick`; when set, use a much larger budget (e.g.
  32-64 MB) instead of the 8 MB in-battle floor (`Display.cs:33-34,129`). Justification: the 8 MB throttle
  exists only to avoid stretching the 33 ms tick and missing a death's `hp==0` window during a live fight
  (`Display.cs:91-92`) -- but when the card is open the game is PAUSED, so nothing is dying and the throttle's
  reason does not apply. Pair with an "invalidate-now" path that bypasses `GenerationMinGapMs`
  (`DisplaySweep.cs:92-103`) for an explicit card-open (keep a >=500 ms floor to preserve the thrash guard).
- **Latency:** attacks the cold full-heap find. At 64 MB/tick a ~1 GB heap completes a pass in ~16 ticks
  ~= 530 ms; at 32 MB/tick ~= 1.0 s. Sub-second is achievable but not guaranteed -- depends on real `H`.
- **Effort:** low-medium. **Risk:** low for the paused-budget piece; medium for the min-gap bypass (the 5 s
  floor is a thrash guard -- mitigate by keying restart to a distinct new target + a >=500 ms floor).
- **Prove live first:** real private-heap size `H` (unmeasured anywhere in repo), and that a 32-64 MB
  paused-tick read does not visibly stall.

### Option C -- Command-menu stable write (the original proposal)
- **How:** one-shot-per-launch signature scan to cache the widget arena (`0x436BFDE4F0`, 0x170 stride, anchor
  on the "TextTitle"/"RadioButtonBattleMenu"/"CommandBg" identity strings via the `+0x20` pointers), then find
  the *displayed-text* buffer (NOT yet mapped), then write -- likely needing an ~8 ms FastHold thread to
  out-race the ~16 ms re-derive. Model to emulate if a text-content pointer exists: FFTHandsFree
  `GameBridge/DialogueSpeakerReader.cs` (follow a u64 widget field -> heap string).
- **Latency:** locate is cheap once per launch (beats the sweep). But unknown whether the write holds at all.
- **Effort:** high -- a fresh RE spike, not a retarget. **Risk:** high -- variable-length new-text write into
  an unknown-capacity buffer (silent corruption), plus the burned grey-flag precedent (may be unholdable),
  plus in-battle-only visibility.
- **Prove live first (three separate things):** (1) locate the displayed-text buffer inside a record;
  (2) a written change renders AND persists WITHOUT a fast-hold (if it reverts every frame -> grey-flag trap,
  dead); (3) the write does not corrupt the shared name. Only after all three is it even a candidate.

### Option D -- Banner / toast -- WALLED, do not repropose
The level-up red-error banner was parked after deep RE: visibility is per-frame-derived state, no single
pinnable flag/timer, needs a main-thread hook into a std::string pipeline; the `0x1401FD9CC` "arm routine" is
a phantom stack frame, do not chase it (`memory/living-weapon-levelup-banner.md`). Same class
(engine-spawned transient text) walls the floating heal/damage numeral (`docs/LIVE_LEDGER.md`). Prior art's
own conclusion: the card-paint is the cheap surface, the banner is the expensive one.

## Recommendation

**Do A + B on the equip card (independent and complementary); do NOT chase the command menu first.** A fixes
repeat-opens to near-instant at near-zero risk; B attacks the cold first-open with a paused fat budget whose
throttle justification provably does not apply while the card is up. Both keep the proven, crash-safe,
same-width overwrite into the pre-baked "Kills:" slot -- none of Option C's new-text corruption risk,
FastHold race, or burned-surface gamble.

## The one probe that decides it (run before any code)

Extend `tools/probes/display_probe.py` (already resolves PauseFlag/SubmenuFlag/MirrorWeapon and reads the heap
without crashing). On each Status-card open: AoB-search the private heap for the on-screen weapon's flavor line
+ "Kills: " literal and **log the found absolute slot address**; close and reopen the card several times on the
same unit, then on different units, and compare. While there, log total committed private-writable bytes to get
`H` for B's budget math.

- Slot address **repeats across opens** -> Option A alone makes repeat-opens instant; conservative path
  validated.
- Slot address **jumps every open** -> A's benefit drops to 4 MB hot-chunk granularity and B (cold-walk
  levers) carries the load -- still a win, just smaller.

Optional, lower priority: the 10-minute kill-or-keep test for Option C. At `0x436BFDE4F0`, walk the 0x170-stride
records and overwrite a "TextTitle" widget's pointed-to string mid-turn; watch whether the change renders and
persists. If it reverts every frame, it is the grey-flag trap again and Option C is dead.

## Open questions / unknowns (need a live probe; not settleable statically)

1. Does the equip-card buffer address persist across opens within a session? The code assumes NOT (`CardSites`
   re-verify exists precisely for movability). This single fact decides how much of Option A is real.
2. What is the real private-writable heap size `H`? Unmeasured anywhere; the "~9 s freeze"
   (`DisplaySweep.cs:8`) conflates read + CPU and cannot be cleanly converted. Determines whether B clears
   sub-second.
3. Where is the command menu's DISPLAYED-text buffer? Unmapped. Only the grey flag (`+0x1C`) and the
   identity-name pointer (`+0x20`, debug strings, not unit text) are known.
4. If found, does a write to that buffer HOLD, or is it re-derived every ~16 ms like the grey flag was? The
   central risk -- the same arena's render bytes already proved unholdable once.
5. Does the in-battle command menu render the name from the heap Roster Name Display Table (0x280 stride, name
   at +0x10) or from the nameId -> CharaName-en -> glyph path? Unconfirmed; if the former, overwriting corrupts
   the name in the party/status screens too.
6. `Offsets.TurnQueue 0x1407832A0` is 1.5-confirmed, but the UI mirror `0x1407AC7C0` is a pre-1.5 address not
   re-anchored for 1.5; would need re-verification before any reliance.

## Source map

- Paint pipeline: `LivingWeapon/Display.cs`, `DisplaySweep.cs`, `CardSites.cs`, `CardScanner.cs`,
  `CardPatterns.cs`, `ByteScan.cs`, `HotChunkSet.cs`, `ChunkReader.cs`; cadence `Engine.cs:19,201-224`,
  predicates `BattleState.cs:64-79`.
- Instant stable-address exemplar: `LivingWeapon/WpScratchPainter.cs:30-45`; addresses `Offsets.cs:184-218`.
- Command-menu surface (scrapped): `docs/research/UI_GREY_HOLD.md`; `docs/LIVE_LEDGER.md` (command-menu Uncertain row,
  Floating-numeral Walled row).
- Why the sweep exists / history: `docs/research/LIVING_WEAPON_JOURNEY.md`; `memory/display-v2-architecture.md`,
  `memory/living-weapon-kills-display.md`, `memory/living-weapon-levelup-banner.md`,
  `memory/item-description-uniqueness.md`.
- Name-render evidence: FFTHandsFree `docs/UNIT_DATA_STRUCTURE.md`, `docs/BATTLE_MEMORY_MAP.md` (sec 7, 16),
  `GameBridge/Lookup/NameTableLookup.cs`, `GameBridge/UnitDisplayName.cs`, `GameBridge/DialogueSpeakerReader.cs`,
  `GameBridge/Perception/InfoPanelParser.cs`.
