# Game-Patch Re-anchor Runbook

STATUS: CONTRACT (the operating procedure when a game update moves the ground under the mod)

The mod pins absolute addresses into a no-ASLR image (Offsets.cs and friends). Every game
recompile relocates essentially all of them. This is the numbered procedure for surviving that
event: written 2026-07-13 during the 1.5.1 re-anchor (LW-81), distilled from the 1.5 port
(docs/research/PORT_1.5.md strategy, docs/research/PORT_1.5_OFFSETS.md worked ledger). Amend it
whenever a run teaches something new; the per-patch findings themselves go in a dated
docs/research/PORT_<ver>_OFFSETS.md journal, not here.

## How you find out

- The fingerprint guard fires at launch: ONE loud log line naming the failed landmark
  (pe-build-key for a game update) plus a once-per-session message box, and the runtime stays
  disarmed. This is the guard working, not a bug (LW-50; first real-world catch: 1.5.1,
  2026-07-13).
- Ground truth before concluding anything: read the PE header off the exe ON DISK and compare
  with LaunchGuard.cs's expected constants. A mod cannot plausibly change TimeDateStamp and
  SizeOfImage; both differing means the binary changed.

## Standing facts that keep this calm

- The save is safe: the runtime is born disarmed and nothing wrote. There is no urgency beyond
  the release schedule.
- The DATA layer still works: modloader tables/nxd/tex anchor by the loader's own scans, not by
  our addresses. Item rebalance keeps applying on the new build.
- Relative struct offsets (band +0xNN, roster strides, record sizes) SURVIVED the 1.5
  recompile unchanged, validated across 13 units. Expect relocation, not reshaping; the
  fingerprint scans in Phase B prove or refute this within minutes.
- Everything AWAITING-LIVE and any smoke pass is blocked until the guard arms on the new build.

## Step 1: capture identity and back up (before anything else)

1. PE fields from disk (python: e_lfanew at 0x3C; TimeDateStamp at PE+8; SizeOfImage at
   optional header +56), exe SHA256, exe size and mtime, Steam buildid + lastupdated from
   steamapps/appmanifest_*.acf.
2. Copy the new exe to C:\Users\ptyRa\FFT_IC_backup_<ver>\ (precedent saved the 1.5 port).
3. Record a row in the build-identity registry below.

| build | TimeDateStamp | SizeOfImage | Steam buildid | exe SHA256 (prefix) | backup |
|---|---|---|---|---|---|
| pre-1.5 | 0x690C1269 | 0x156C8000 | 20688883 | 6DEDDA92 | C:\Users\ptyRa\FFT_IC_backup_pre1.5\ |
| 1.5 | 0x6A0F86A9 | 0x190EB000 | 23353019 | 3625FD9B | none (replaced in place) |
| 1.5.1 | 0x6A3C5497 | 0x1878E000 | 23901820 | 841DD404 | C:\Users\ptyRa\FFT_IC_backup_1.5.1\ |

## Step 2: ledger it

Open a TODO Now row (the LW-81 pattern): the re-anchor is release-blocking by definition. Note
in the row that every AWAITING-LIVE flip and the smoke pass are blocked behind it.

## Step 3: Phase A, the offline damage inventory (no game needed)

Produce the checklist the live session will execute:
1. Every absolute address: Offsets.cs in full, plus literals living elsewhere (Barrage.cs
   AbilityBase, LaunchGuard.cs constants, dev spikes under #if LWDEV, Treasure Master anchors).
   Classify each: state flag / struct base / table base / display mirror / TM; and whether it
   GATES WRITES (stale-but-valid write anchors are the dangerous class, see Traps).
2. Every pin that must move in the same commit: tests that hard-code addresses or build keys
   (BarrageTests table-base pin, ScholarRingTests write tripwire, LaunchGuardTests /
   StandDownFlightArchiveTests staging), docs/recipes stating addresses as current.
3. Probe readiness: which tools/probes/*.py cover which anchors, what game state each needs,
   which have stale hardcoded addresses to parameterize first.
4. Aux-module scope calls (see Step 7) priced for the owner.

## Step 4: Phase B, the live re-find (one co-op session, external read-only RPM)

Method by anchor class (the 1.5-proven playbook; details in PORT_1.5_OFFSETS.md "Method"):
- State flags (battleMode, pause, submenu): two-state DIFFERENTIAL across transitions; for
  flags the action menu already holds (pause), a HIGH-FREQUENCY CONSISTENCY SAMPLE
  (constant-1 across one state, constant-0 across the other) beats any open/closed diff.
- Struct bases (roster, combat band, turn queue): FINGERPRINT scan on a known unit's stats
  (level, brave, faith, HP, weapon id) across the image; verify neighbors (party slots, twin
  at the known stride).
- Content-anchored tables (JobCommand): SIGNATURE scan on invariant CONTENT (rec 8 Aim bytes +
  rec 9 Martial Arts bytes exactly one RecSize apart); tools/probes/jobcommand_find_probe.py.
- Region siblings: predict by the measured neighborhood delta, then VERIFY by read/behavior.
- NEVER interpolate across regions: deltas are a non-monotonic gradient (1.5 measured +0x5080
  to +0x676C depending on region; 1.5.1 shrank the image, so expect negative-leaning deltas).
Group the probe run order by game state (title, save loaded, world map, in battle, card open)
so the owner switches states once per group, not once per probe. Pre-register expected
outcomes per probe before running (found / ambiguous / not-found and what each means).

## Step 5: wire it, one commit

- New addresses into Offsets.cs, WITH provenance comments (probe + date + evidence), and the
  LaunchGuard expected constants (PE key; re-derived JobCommand signature bytes if the content
  moved, they are game-design data and usually survive) IN THE SAME COMMIT. Never flip the
  guard constants alone: the guard exists precisely to stop old offsets running on a new build.
- Bump every pin from Step 3.2 in the same commit. Both gates green.

## Step 6: deploy and live-verify (owner go-ahead, owner eyes)

1. Kill fft_enhanced.exe, BuildLinked (dev first is fine; the flavor guard rules apply).
2. Guard arms ("Living Weapons is armed") seconds after a save loads; NO stand-down.
3. One battle round-trip: a kill credits with victim identity, battle exits clean, card paints.
4. python tools/scan_logs.py --require-battle --flight exits 0.
5. Bank the per-patch ledger to docs/research/PORT_<ver>_OFFSETS.md (STATUS: JOURNAL), flip
   the TODO row, and amend THIS doc with anything the run taught.

## Step 7: aux-module scope calls (price them, ask the owner)

- Treasure Master: auto-disarms on its own build key; its flag data is ~1500 rebased absolute
  addresses. Re-anchor cost is a rebase run (tools/treasure_rebase.py, two ground-truth
  recaptures) vs leaving it disarmed. With removal planned (LW-10), stay-disarmed is the
  default recommendation.
- Dormant modules (CharmLock, LifeSap, Wyrmblood) ship no live data wiring: nothing to do.
- Dev spikes are LWDEV-only: re-anchor lazily, when next used.

## Traps (each cost real time once)

- STALE-BUT-VALID is the killer class: an old address usually still maps on the new build and
  reads as coherent unrelated data. A write-gating anchor left stale CORRUPTS silently (the
  1.5 JobCommand base read as other ability records; a stale DLL would have overwritten them
  the moment a +3 Yoichi wielder fielded). Re-find write anchors FIRST; never ship a build
  where any write anchor is unverified.
- Marker SEMANTICS can change, not just addresses: 1.5 changed slot0's in-battle value from
  0xFF to 0x10 (docs/TODO.md LW-42 is the standing audit). Re-check meanings, not just homes.
- battleMode==3 is not "menu open" (it holds for the whole player turn and flickers during
  enemy turns); pause+submenu are the real discriminators.
- The guard's JobCommand landmark stays Unreadable (not Mismatch) until a save populates the
  roster: a boot-window non-arm is normal, not a failure.
- Alt-tabbed game pauses the sim (polls read nothing); console QuickEdit freezes mod threads;
  read the log FILE.
- Restarting the game invalidates all heap addresses mid-hunt; finish a probe batch within a
  session or re-locate cheaply.
- Before any scanning, run the cheapest content-signature probe AT the old addresses first. The
  1.5.1 point release preserved the entire layout except one UI byte (docs/research/
  PORT_1.5.1_OFFSETS.md): the full re-find script collapsed to a verification pass that answered
  the whole audit in one session. Do not assume a patch moved anything until an old-address read
  actually comes back wrong.
- UI flags need a consistency-sampled multi-state solve, not a simple diff: sample each candidate
  many times per state (not once) and require constant-value-per-state across all of them; a
  synced sibling copy is normal (expect one), and a generic-panel decoy is separated from the real
  flag by finding a DIFFERENT submenu (e.g. the abilities list) where the decoy still reads true
  but the real flag reads false.
- A flag's ADDRESS can survive a patch while its SEMANTICS narrow: 1.5.1's PauseFlag stayed at the
  same address but went from holding 1 across the whole player turn (1.5) to holding 1 only while
  the status card itself is open. Re-check meaning even when a landmark verifies at its old home.
