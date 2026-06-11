# Session Handoff — the 2.0 playtest day (2026-06-10, evening)

Everything below is COMMITTED and PUSHED (`living-weapon` == `main` @ 5607c5a). The arc of the
day: the kills-card display was rewritten end to end (Display v2), the full QoL punch list
shipped, and then a long live playtest with Patrick at the controls flushed out — and fixed —
an entire bestiary of identification bugs. Suite: 464 → **766 tests**, gates: 5 → **7**.
**2.0 is NOT released**: the `v2.0.0` tag sits ~9 commits behind tip and the zip in Downloads
is stale; tomorrow's first decision is retag-vs-2.0.1, then re-cut via `Publish.ps1`.

## The live install (IMPORTANT — non-standard state)

The Reloaded mod folder holds a **PROD-flavored build** (thresholds {5,25,50}, no seeding),
NOT the usual BuildLinked dev deploy — Patrick is release-testing on his real save. It was
placed by `Publish.ps1` + copying `Publish/prawl.fft.itemoverhaul` over the mod folder
(preserving `kills.json`), with later table changes hot-copied in. **Running `BuildLinked.ps1`
will stomp it with a dev build** (dev seeds every weapon to 3 kills and POLLUTES the tally —
this happened once; the tally was repaired by dropping exact-3 entries). A `-Prod` switch for
BuildLinked is a standing offer. Current tally carries 3 phantom credits (Scoutbolt +2,
Wellspring Rod's entire 1) from since-fixed bugs; surgery offer open.

## What shipped today (compressed)

- **Display v2**: budgeted resumable heap sweep, all-flavors attribution, ownership re-verified
  at paint, per-id suffix coverage, site-cache prune + 1s maintenance. Fixes: shared counts,
  re-equip-only updates, startup pop-in, the 9s engine-loop freeze. See memory display-v2.
- **Kill credit hardened end to end**: status-deaths detected (Dead bit +0x45/0x20 — Phoenix
  Down on undead), per-down credit on alive→dead edges (undead re-kills count, frozen twins
  still credit once), resolved-but-untracked players CLEAR the actor latch (DLC-armed Ramza /
  item users no longer pay the previous actor), and **mid-battle level-ups no longer break any
  roster-keyed identification** (`Band.LevelMatchesRoster`, one-sided drift ≤9 — this was the
  adversarial review's "uncertain" item, confirmed live and closed).
- **Battle-state sentinels**: event-id band widened (401 fake exit), slot0 sticks at 0xFF after
  a battle QUIT (probe-proven), sentinel-pair enter is edge-triggered (no more 4s metronome).
- **Plague (Venombolt +3) BUILT** (was card-only) + grace-window latch (engine applies poison
  off-tick from the acted window). Main-hand rule everywhere: kills+growth both hands,
  signatures main-hand only. Barrage works from secondary Thief. Charm/Plague gated on live
  frames. Locators require level (drift-aware) + player-side-first.
- **Data/balance from the playtest**: Scoutbolt wp4/r4 (was outranged by Throw Stone), the
  cheap-Regen quartet broken up (Mendsteel→immune Poison row 17, Studded→hp36 plain,
  Penitent's→immune Blind row 25; Nocturne/Chantage keep theirs), shields off generic
  Squire/Chemist/Black Mage, Equip Axes removed from ALL 47 skillsets carrying it
  (make_jobcommand.py), grenade prices shipped via ItemData `<Price>` (the "base item.nxd"
  comment was folklore — no such table exists), Sanguine Sword reverted to innate drain
  (formula 6, wp10), Materia Blade exonerated (FFTHandsFree's cap-break auto-arm was the
  culprit — disarmed in THAT repo, verb-only now), 15 lying cards fixed (Genji set etc.).
- **Gates 6+7**: RIDER PROSE (verbatim descs must state every rider clause; numerics in exact
  house voice) and GRID SYNC (living_weapon_grid.csv ↔ items.json on id/name/type/tier/WP/
  parry; obtain column Shop-token enforced both ways). Grid restructured: sigNote column
  ARCHIVED to docs/living_weapon_signotes.csv (draft signature ideas live there), new `type`
  and `obtain` columns (32 obtain cells = TBD, Patrick's acquisition worksheet).
- **Logs humanized** ([FFTItemOverhaul] prefix, LogNames id→name); prod build byte-verified
  after the publish pipeline shipped a stale DLL once (clean-rebuild now forced in Publish).

## New trap ledger entries (memories carry detail)

1. **JobCommandData sparse overrides**: every record MUST include
   `ExtendReactionSupportMovementIdFlagBits` ordered BEFORE any `ReactionSupportMovementIdN`
   (the model's setters deref it; absence = whole table dropped, YAXPropertyCannotBeAssignedTo)
   — and NO inline XML comments inside entries. make_jobcommand.py encodes both rules.
2. **slot0 sticks at 0xFF after battle QUIT** (victory clears to 0x66); sentinel probe at
   `%TEMP%\fft_probes\sentinel_probe.py` (module statics readable externally despite Denuvo).
3. **Roster level is pre-battle**; live structs update mid-battle — always compare via
   `Band.LevelMatchesRoster`, never `==`.
4. **EquipBonus rows emit FULL rows** (generate.py fills defaults) — claiming a row replaces
   its vanilla content entirely; ledger in items.json `_meta.equipBonus` (17, 25 claimed today).

## Tomorrow / release checklist

- Decide: move `v2.0.0` tag to tip or christen 2.0.1 → `Publish.ps1` re-cut (clean compile is
  now forced; byte-verify thresholds {5,25,50} out of habit).
- Patrick's remaining verify: main-hand Bloodlust (Zwill main hand → extra turn; offhand →
  polite log line), Plague cure-test (latch line then survive an Esuna), Equip Axes gone from
  the learn screens, the Regen-pair shop check.
- TODO.md still open: Sunderer multi-break question, chemist targeting-tag stale name, icon
  colors (Later), changelog + FAQ (offer stands — half-written in session transcripts).
- Open design threads: enemy-Thief Barrage spawn roll (needs live probe), story-progress
  address probe (would unlock chapter-gating anything), DLC weapons into the P system?,
  offhand signatures (2.x: twin-slam + the 100-vs-105 queue-jump probe), tally surgery offer.

## Dev harness facts (unchanged)

Deploy dev = kill FFT_enhanced.exe, `.\BuildLinked.ps1` (BUT see live-install note above).
Tables/nxd apply on game RESTART; the DLL on next launch. Diagnostics in `livingweapon.log`
(rotates to `.prev` per launch): `kill:`, `turn:`, `plague:`, `display:`, `battle:` prefixes —
all plain-language now. Both gates + tests enforced by BuildLinked/Publish/CI.
