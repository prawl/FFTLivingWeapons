# Battle command-menu un-grey hold (BattleMenuGrey) — SCRAPPED

> ⚠ **The `BattleMenuGrey` module described here was SCRAPPED 2026-06-23** — proven a dead end (the UI
> grey is unholdable; see **RESOLUTION + PARKING-LOT** at the bottom). This file is a findings record
> only; the code (module, `Config.BattleMenuUngray`, `Offsets.MenuSubState`, `Tuning.BattleMenu*`) no
> longer exists in the tree. The forward path is the combat-struct "acted" state (bottom section).

Provenance + reproduction for the (now-removed) `BattleMenuGrey` runtime module. The original live session is
recorded in the sibling driver repo (`Dev/FFTHandsFree/handoff.md`, 2026-06-23, "FFT UI grey-flag
tick-hold"); this file is the in-repo home so the code/ledger citations resolve here. Live status is
**UNCERTAIN** until Patrick flips it (see `LIVE_LEDGER.md`).

## What it does

Hold the battle command menu's grey-out overlay flag(s) at `0` every render frame so menu entries
(headline: **Abilities**) never disable after a unit has acted.

## The widget arena (self-labeling)

The command-menu widgets live in a **launch-stable HEAP arena** (NOT the module at `0x140000000` --
so the base lives in `BattleMenuGrey.Policy.cs`, not `Offsets.cs`). Each widget is a `0x170`-byte
record:

| field | offset in record | meaning |
|---|---|---|
| flag byte | `+0x1C` | the disabled/grey overlay state (hold at `0` to un-grey) |
| name pointer | `+0x20` (flag+4) | u64 -> the widget's UTF-8, null-terminated name |

Documented cluster base `0x436BFDE4F0`; record `i` flag = `base + 0x1C + i*0x170`. The resolved names
(live, 2026-06-23):

- record 0 `0x436BFDE50C` -> `GrayOut#3`
- record 1 `0x436BFDE67C` -> `TextTitle#4`
- record 2 `0x436BFDE7EC` -> `RadioButtonBattleMenu#6`
- record 3 `0x436BFDE95C` -> `CommandBg#6`
- records 4-7 -> `RadioButtonBattleMenu#7/#8`, `CommandBg#7/#8`

`RadioButtonBattleMenu` / `CommandBg` / `TextTitle` are the **anchor** widgets (corroborate we scanned
the real arena); `GrayOut` is the hold target. The module name-validates the arena each scan (must see
an anchor) before writing anything, so a drifted base is a safe no-op.

Menu-shown gate: enum `Offsets.MenuSubState` (`0x140C6B1CC`) reads `4` while the command menu is up,
with `BattleMode` (`0x1409069A0`) in `{2,3,4}`.

## The write-race (why the 33ms tick is not enough)

The engine **re-derives the grey flag every render frame (~16ms)**. So:

- An out-of-process Cheat Engine freeze loses (its timer write is unsynchronized + slower).
  Live 2026-06-23: wrote `0x436BFDE7EC = 0`, re-read `1` ~0.5s later -- the engine reverted it.
- The mod's **33ms** engine tick ALSO loses: live 2026-06-23 the hold logged
  `battle-menu un-grey ACTIVE -- holding 3 GrayOut flag(s)` (3 found, anchor validated) yet CE still
  read the bytes `1` -- the engine is the more-recent writer almost every frame.

**Fix: a dedicated ~8ms FastHold thread** (~2x per frame) re-stamps the cached flags so the held `0`
is the last write before most frames draw. This is the exact pattern that beat the Treasure-mark
"running-water wipe" (`FastHold.cs` / `Tuning.TreasureFastHoldMs`). The 33ms tick still does the
locate/name-validate (arming); the fast thread does the cheap re-stamp.

## Open questions (NOT yet proven)

1. **Which `GrayOut#N` is Abilities vs Auto-Battle** is unmapped (the widget name doesn't say which
   menu item it overlays). v1 holds EVERY GrayOut in the validated arena. To split (un-grey Abilities,
   force-grey Auto-Battle) correlate per-item live: toggle one + screenshot which entry changed.
2. **Is the flag the VISUAL gate, and does holding `0` re-enable INPUT?** Until the hold wins the race
   this was untestable. If holding `0` re-enables a spent action it could allow a double-act -- verify
   the on-screen + input behavior once the byte demonstrably holds at `0`.

## Reproduce

```
# command menu must be up: Offsets.MenuSubState (0x140C6B1CC) == 4, BattleMode (0x1409069A0) in {2,3,4}
# read the cluster: record i flag @ 0x436BFDE4F0 + 0x1C + i*0x170, name ptr @ flag+4 (follow -> UTF-8)
# enable the hold: Config.BattleMenuUngray = true ; then watch a GrayOut byte in CE while acting
```

## RESOLUTION + PARKING-LOT (2026-06-23)

**The UI grey is UNHOLDABLE — this whole module is a dead end for the goal.** Live-proven:

1. The 8ms FastHold WINS the write race (CE shows the GrayOut byte forced to `0`) but Abilities stays
   gray AND `Enter` does nothing. So the byte is a **downstream render output**, not the gate.
2. `GrayOut#3` (`0x436BFDE50C`) is not even Abilities — a 24KB diff of its cluster was byte-identical
   between gray/not-gray. A 2MB diff of the whole UI-widget arena found 123 bytes that correlate with
   the grey; **forcing all 123 to their not-grayed values changed nothing.** The grey (look + input
   gate) is engine-derived from the unit's acted state every frame, not stored as a holdable UI byte.

**The real gate = the active unit's COMBAT-STRUCT "acted" state** (this is the path forward, if revived):
- Method: a **same-unit preact/postact differential** of the combat band `0x141850000–0x141862000`
  (near `Offsets.CombatAnchor 0x141855CE0`). Cross-unit diffs are useless (per-unit fields live at
  different addresses) — must diff ONE unit before vs after it acts. Gave only **12 candidates**.
- Holding **10 at pre-act values** (flags `0x141855E9B`/`0x14185609E` → `1`; action-record cluster
  `0x141856080/081/08A/08C/090/09A/09B/0A0` → `0`) un-grays Abilities AND makes it **usable** —
  **multi-act live-confirmed** (continual attack/act). The two flags alone did nothing; clearing the
  cluster was the key.
- **BUG (why parked):** the attacker swings the WRONG DIRECTION — the cluster includes facing/attack-
  direction bytes that zeroing corrupts. **NEXT: bisect the cluster to the minimal acted byte(s),
  excluding facing; then resolve as a per-unit OFFSET** (the absolutes above are one unit's slot).
- **Clean alternative (untested):** the proven `ExtraTurn` CT-slam-to-100 grants a fresh turn →
  Abilities available, engine-honored, no facing bug. Likely the better multi-act route.

**Also ruled out:** writing `AddrBattleSubState 0x140C6B1CC = 24` dives into tile-select past the
keypress gate but with no ability bound (menu action-setup skipped) → breaks ability use; a clean
bypass = reimplementing the menu. Global `Acted 0x140782A8C` is not the per-unit gate (read 0 on a
grayed unit).

Probe tool: `tools/probes/menu_gate_diff.ps1` (RPM differential: `cap`/`diff`/`isect`). Full
cross-repo notes in the driver memory `project_fft_multiact_acted_gate_2026_06_23`.

