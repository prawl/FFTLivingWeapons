# Session Handoff — Larceny expiry reworked to a global-turn clock + multi-target tests (2026-06-15)

This session reworked **Arcanum's "Larceny" (+3) expiry** from the wall-clock stopgap to a proper
**global-turn clock**, and added the multi-target test coverage that was missing. Everything is on
**`main`** and **uncommitted** (held until Larceny's expiry is finally watched live — see below). Both
gates are green: `analyze.py` PASS, `dotnet test` **1085 passing**.

The full Sword +3 slate (unchanged from last session except Larceny's expiry guts):

| Sword | +3 Signature | State |
|---|---|---|
| Wrathblade (27) | **Feign Death** | shipped + verified live (committed) |
| Swiftedge (28) | **Afterimage** | built + VERIFIED LIVE (doc flips uncommitted) |
| Sanguine Sword (23) | **Night Sword** | built + VERIFIED LIVE on a Knight (doc flips uncommitted) |
| Arcanum (30) | **Larceny** | built; steal **VERIFIED LIVE**; **expiry now global-turn, logic-verified, NOT yet watched fade live** ← the one open item |

T1/T2 swords deliberately have no signature.

## What changed this session — Larceny expiry: wall-clock → global turns

The earlier turn-count expiry counted the **wielder's own** turns, which never advance if the player
parks that unit (a stolen buff held through 6 sat-out turns, observed live 2026-06-14). The stopgap fix
was wall-clock (`LarcenySeconds=30`), but that bled down in menus and ignored battle pace. This session
swapped it for an **attribution-free global-turn clock** — it can't be frozen by parking the wielder,
and it tracks battle tempo:

- **`TurnTracker.GlobalTurns`** (NEW): a count of turns taken by *anyone*, bumped on every rising edge
  of the acted flag (`0x14077CA8C`) **before** the per-unit fingerprinting — so it advances even when
  the actor is unresolvable or isn't the wielder. Reset on the battle boundary. Per-unit `Turns(...)`
  is unchanged.
- **`Tuning.LarcenyHoldTurns = 12`** replaces `LarcenySeconds`. ~12 global turns ≈ a round and a half in
  a typical fight; the clock runs *faster* the more units are on the field (it counts enemy turns too).
  **This is THE live-tune knob** — one int.
- **`LarcenyPolicy.IsExpired(currentTurn, stolenTurn, turns)`** is now `currentTurn - stolenTurn >= turns`;
  `LarcenyState` stamps the **global-turn index** at steal time (was a `DateTime`).
- **`Larceny.cs`** takes a `TurnTracker` again (the arg dropped at the wall-clock swap) and reads
  `_turns.GlobalTurns` for both the steal stamp and expiry. Off-field it still ages on the (then-frozen)
  clock, so nothing fades during menus.
- `items.json` `larcenyTurns: 3` stays a pure **on/off gate** (as the wall-clock version already treated
  it); the duration is the compiled `Tuning.LarcenyHoldTurns`. **No meta regen needed for the mechanic.**

### Multi-target seam + tests (NEW)
The per-struck-foe decision was inline + untestable, so the multi-enemy behavior (one action splashing
several buffed foes) had zero coverage. Extracted it to a pure **`LarcenyPolicy.Decide(alreadyHeld,
wielderHasBuff) → Skip | Dispel | Steal`** (zero behavior change) and added tests at the Maim/Ricochet
altitude (policy + ledger + guarded writes; the live-band scan stays a live-verify, not a unit test):

- the `Decide` 4-way matrix;
- **same buff on N foes** → steal from one, **Skip** the rest (only one copy lifted per term);
- **different buffs on N foes** → **steal both** (independent ledger keys; wielder wears both);
- **wielder already owns the buff** → every duplicate **Dispelled** (stripped, never latched, so expiry
  can't clear the wielder's own enchantment);
- byte-level: a skipped duplicate's bit genuinely **survives** (foe #1 stripped, foe #2 retains it).

## Git state — all on `main`, UNCOMMITTED

```
5011667 Build Larceny + Night Sword runtimes, wire and card them      (last commit)
961772f Add Night Sword + Larceny signature logic and tests
058d95a Build Afterimage signature (Swiftedge +3): logic + tests
```

Uncommitted working tree, in two groups:

**A. This session — Larceny global-turn expiry + multi-target seam** (commit once expiry is live-confirmed):
`LivingWeapon/TurnTracker.cs` (GlobalTurns), `Larceny.cs`, `Larceny.Policy.cs` (IsExpired turns, the
`LarcenyState` int stamp, `Decide`/`LarcenyAction`), `Tuning.cs` (`LarcenyHoldTurns`), `Engine.cs`
(passes `_turns`), `WeaponMeta.cs` (doc), `LivingWeapon.Tests/LarcenyTests.cs` +
`TurnTrackerTests.cs`.

**B. Carried from last session** (verified-live doc flips + card text, still uncommitted):
`docs/LIVE_LEDGER.md` (Afterimage + Night Sword PROVEN rows), `docs/living_weapon_grid.csv` +
`living_weapon_signotes.csv` (`Verified Live? = Yes` for 23 + 28), `data/items.json` (Larceny p3Desc
*"Strikes steal 1 enchantment from a foe for the wielder to wear a few turns."* + curator note),
`GrowthEngine.Afterimage.cs` (a keeper ramp log line). `docs/TODO.md`, `docs/NEXUS_PAGE.md` (untracked)
were dirty before this work.

**Before committing group B:** regenerate `item.en.nxd` (`python tools/patch_names.py`) — the committed
nxd still carries Larceny's OLD card text; the new wording is only in items.json.

## THE ONE OPEN ITEM — Larceny expiry, watch it fade live

Larceny's **steal is proven live** ("Boom it works"); the **global-turn expiry is logic-verified (tests)
but never watched fade in-game.** It uses the same guarded `ClearBit` the steal already exercises live, so
it's low-risk — but it's the last unverified link.

**Why this is now *easier* to verify than the wall-clock version was:** GlobalTurns counts *everyone's*
turns, including enemy AI turns. So you no longer need to cycle the **wielder's** turn (the thing the
FFTHandsFree bridge couldn't drive — `battle_wait` hung last session). Just let the battle run.

**To finish:** in a battle with an Arcanum wielder, give an enemy a holdable buff it lacks (Reraise via
the probe), have **that wielder attack** the buffed enemy (watch `larceny: stole Reraise ... wears it for
~12 turns` + the wielder's band `+0x47` flip on), then let **~12 turns of combat pass** (any units' — the
wielder can sit out) and watch for `larceny: a stolen buff faded from the wielder after ~12 turns` with
`+0x47` dropping to 0. **Tune `Tuning.LarcenyHoldTurns` if 12 feels off.**

## Load-bearing Larceny findings (baked into the code/comments)

1. **Pre-hit snapshot.** Arcanum's BASE "may strip buffs" proc (onHit 55) clears the foe's buff *during*
   the hit, a tick before Larceny's per-tick scan — so a post-hit read finds nothing. Fix: snapshot each
   enemy's holdable buff every tick and steal the **pre-hit** snapshot (proven live 2026-06-14).
2. **Global turns, not wielder turns, not wall-clock** (above). The expiry clock must keep ticking no
   matter what the held unit does.
3. **Buff-bit map — still the big gate.** Only the PROVEN-holdable bits are wired: **Reraise `+0x47/0x20`**
   and **Invisible `+0x47/0x10`** (the FeignDeath pair; Reraise confirmed live on enemy and wielder). The
   **marquee buffs (Haste/Protect/Shell/Reflect/Regen/Float) are UNMAPPED** — map each with
   `tools/probes/poison_probe.py diff <mhp> <lvl>` (apply the buff, watch the bit flip in `+0x44..+0x4C`)
   + `holdbit` (confirm a held bit takes effect, not cosmetic), then add a row to
   `LarcenyPolicy.Stealable`. The transfer mechanism + the multi-target `Decide` already work for any row.
   NOTE: equipment "Always: X" buffs aren't cleanly stealable (gear re-asserts them); cast buffs are.
   Invisible makes a unit untargetable, so it's a poor test vehicle.

## Night Sword caveat

Verified live granting Shadowblade (ability 165) on a **Knight** (job 76) — renders + casts. The grant
only resolves for the generic-job band (74-92) minus special-executor jobs; **story-unique jobs (Ramza's
Squire) can't receive it** (logs "cannot receive Shadowblade"). Reuses every Barrage primitive; the
shipped Barrage module is untouched (its table consts were just widened to `internal`).

## Live install + release

The Reloaded folder holds a **DEV build** (BuildLinked, thresholds {1,2,3}, every weapon force-seeded to
+3). **Re-Prod before release** (`BuildLinked.ps1 -Prod` or `Publish.ps1`); eyeball `kills.json` for
dev-seed pollution. The deployed DLL still has the OLD wall-clock Larceny — **redeploy** to test the
global-turn expiry.

## Test recipes

- **Drive the game**: `source FFTHandsFree/fft.sh`; `screen`, `execute_action <validPath>`, `battle_attack
  <x> <y>`, `scan`/`fft_full`. Screenshot: `FFTHandsFree/screenshot_crop.ps1` → `~/Downloads/fftwin_*.png`.
  Launch: `reloaded-ii.exe --launch FFT_enhanced.exe`. The bridge's turn-cycle is flaky (`battle_wait`
  hung last session) — but the global-turn expiry no longer needs it (let enemy AI turns tick the clock).
- **Probe — give enemies a buff (clean locate, NOT a broad band scan — that hit garbage once)**: use
  `poison_probe.scan_static_union` (slot<1 = enemy) + `scan_auth` for band addrs, OR set `+0x47 |= 0x20`.
  Roster offsets: base `0x1411A18D0`, stride `0x258`, RRHand `+0x14`, RLevel `+0x1D`, RBrave `+0x1E`,
  RFaith `+0x1F`. Band: gx/gy `+0x33/+0x34`, status `+0x47`. Active unit: TurnQueue `0x14077D2A0`
  (maxHp `+0x10`, level `+0x00`). Acted flag (drives GlobalTurns): `0x14077CA8C`.

## Pre-existing open items (unchanged)

Siren's Lyre charm bug (`sirens-lyre-charm-bug`), phantom kill tallies, Treasure Master release runbook
(`docs/2.0_RELEASE_CHECKLIST.md`). The other 13 weapon categories still carry DRAFT signotes (only Knives,
Bows, Rods, and Swords are curated/shipped).
