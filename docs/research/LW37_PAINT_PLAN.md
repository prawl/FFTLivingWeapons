# LW-37 build plan v2: pool-anchored in-place Kills paint

STATUS: JOURNAL (build plan v2, code-map grounded 2026-07-07; supersedes v1). Mechanism proven live 2026-07-07.
Memory `lw37-equip-card-redirect-walled`. Probe `tools/probes/item_text_census.py`.

SHIPPED + LIVE-VERIFIED 2026-07-08. Two live-only refinements past this plan (see the memory for
detail): the region discriminator requires the owner weapon's NAME adjacent to its "Kills:" hit
(a name-less render copy is not the pool), and PoolLocator paints EVERY name-bearing baked region
(`LocateAll`), not a single "best" one, because the process holds several baked copies with no
static signature for which the card materializes from.

## Goal
Retire the per-paint whole-heap `DisplaySweep` for the equip-card Kills meter. The equip card
re-materializes its description from a STABLE UE string pool on every open; write the pool's
space-padded `Kills:` field in place and the card shows the current count on open WITHOUT chasing
transient widget copies each paint.

## What the code map SETTLES (v1 "recon unknowns" that are already answered in-repo)
- FIELD WIDTH is a baked, gated constant, not a live unknown. The pool field is `"Kills: 0/5 to +   "`
  = 18 bytes (7-char `"Kills: "` literal + 11-char body). `Signatures.KillsMeterSlotChars = 11`
  (Signatures.cs:144), lockstep-pinned to `flavor.py` `KILLS_SLOT_BODY_CHARS` by `analyze.py:177`.
- COMPOSE exists: `Signatures.KillsMeterSlot(kills)` -> the 11-char body; `AttackCardTail.ComposeHead`
  -> the full `"Kills: N/T to +X"` line. No new compose.
- SCAN + ATTRIBUTE + WRITE-DISCIPLINE exist and are tested. `CardScanner.FindKills` + nearest-flavor
  attribution (`CardScanner.NearestAnchorPos`, `FlavorWindow` 2048). `CardSites.PaintSiteWithResult`
  (CardSites.cs:177) already does the exact in-place, same-width, foreign-refused (`ByteScan.
  MeterSlotDigits`), `Writable`-gated, skip-if-equal body write. A `Site` holds `AnchorAddr` (flavor)
  + `SlotAddr` (body). The `CardSites` cache already persists located sites and re-verifies each
  `PaintAll`; transient widget sites evict (`AnchorIsLive` fails), a stable pool site persists.

=> The ONE genuinely new piece is reaching + caching the STABLE POOL region so the sweep can retire,
instead of re-walking the heap every paint to re-find transient widget copies.

## The live fact that CALIBRATES (owner ~10-min recon, item_text_census.py) -- does NOT block the build
Does the current `DisplaySweep` region source (`Mem.Regions()`: committed/PRIVATE/writable) already
include the pool region (proof addr ~0x004CDBxxx)?
- `find "<a distinctive baked flavor substring>"` -> note the pool hit address; `dump <addr>` to
  confirm the contiguous baked layout (name+tier, keys, `"Kills: N   \n\n"`, flavor). Check the
  address against the `Mem.Regions()` PRIVATE+writable filter.
- Browse weapon A, then WITHOUT opening B, `find "B"` -> is B's pool entry present? (complete vs
  on-demand -> the per-open verify below covers on-demand regardless).
The "reach + cache the pool, retire the sweep to a gated fallback" design below is correct in BOTH
outcomes; the recon just confirms the anchor/region live and whether the sweep can be dropped fully
or kept as a rare fallback.

## Build (pure-first, reuse-heavy; 200-line seam split)
1. **PoolLocator** (new; pure matching + cache; mirror `GrowthEngine.Locate`): given a distinctive
   baked anchor, scan committed readable regions, identify the pool entry span, cache the region
   base, re-find lazily on a read miss (per-launch relocation). Pure -> `FakeHeap`-testable.
2. **PoolPaint** (new; thin orchestrator): on trigger, ensure the viewed/changed weapons' pool Kills
   sites are registered into `CardSites` via `PoolLocator`, then `PaintAll`. Reuses `CardScanner`
   attribution + `CardSites` write discipline verbatim.
3. **Trigger:** mirror `Display.CheckAndSnapshotCounts` (self-snapshot per meta id) on the
   tally-change edge (Engine.cs:386/403) + a per-out-of-battle-tick verify of the viewed weapon
   (`Offsets.MirrorWeapon`/`MirrorOffHand` 0x141876EB4/6). No per-frame heap crawl.
4. **Retire the sweep behind a flag:** gate `DisplaySweep`'s full walk; keep it as the fallback
   discovery path until live-verified; `PoolPaint` + `CardSites.PaintAll` is the fast path.
5. **File split:** `PoolPaint.cs` (memory/wiring) + `PoolPaint.Policy.cs` (pure locate/attribution/
   guard), <=200 lines each. Add an `analyze.py` lockstep if any new constant is introduced.

## Tests
- Unit (xUnit + `FakeHeap`): PoolLocator cache seed/hit/miss/stale-drop + pool-region identify;
  pool-entry span math; reuse the `CardScanner`/`ByteScan` foreign-refusal + same-width guards
  (mirror `CardScannerFindKillsTests` / `CardSitesPaintTests`).
- Live (owner + probe): the recon above; pool write lands + re-materializes on reopen; a battle-exit
  kill shows updated on reopen; another item's flavor untouched; Attack card unaffected; the sweep
  quiesces (no per-paint gigabyte crawl); no latency.

## Risks (all already enforced by the reused `CardSites` path)
Per-launch relocation (re-find, never hardcode); pool-on-demand timing (the per-open verify covers
it); same-width ceiling (compose fits the gated 11-char body); anchor discipline (foreign refusal so
name/flavor/other surfaces are never disturbed).
