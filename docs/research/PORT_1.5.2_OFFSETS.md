# FFT:IC 1.5.2: Offset Re-anchor Ledger

STATUS: JOURNAL (closed research log; verify claims against LIVE_LEDGER.md before building on them)

> Damage report for the 1.5.2 re-anchor. Companions: docs/research/PORT_1.5.1_OFFSETS.md (the
> previous point release), docs/research/PORT_1.5_OFFSETS.md (the full recompile, where nearly
> everything moved), and docs/PATCH_REANCHOR.md (the runbook). What is different about this one:
> the entire address audit was answered OFFLINE, by diffing the 1.5.1 and 1.5.2 executables on
> disk. The game was never launched to produce anything below.

## In one paragraph

The game updated and nothing the mod points at moved, with one exception. Every table, every
battle flag, every struct base sits at exactly the address it sat at on 1.5.1. One function moved
four bytes, and it happens to be the single function the mod attaches code to, so that one line
needed changing. The mod's guard also had to be told the new game version's identity, which is the
whole reason it noticed the update and switched itself off in the first place. (Tech: PE build key
re-derived; PromptSwapHook.FnSetTextString 0x1403F1098 -> 0x1403F109C; all Offsets.cs absolutes
unchanged.)

## Build identity

| build | TimeDateStamp | SizeOfImage | Steam buildid | exe stamped | exe SHA256 |
|---|---|---|---|---|---|
| 1.5.1 | 0x6A3C5497 | 0x1878E000 | 23901820 | 2026-07-13 06:38 | 841DD404... |
| 1.5.2 | 0x6A5EA53C | 0x18D78000 | 24304240 | 2026-07-20 22:46 UTC | 1FD5320D8A6CD9364A7FE149C0BE1BB6E180C82A78A2A3E2881FC8F821949631 |

Installed on this box 2026-07-24 20:52. exe size 362,226,944 (was 356,024,064). Backup:
C:\Users\ptyRa\FFT_IC_backup_1.5.2\. SizeOfImage GREW by 0x5EA000, the reverse of 1.5.1's shrink.

## Method: the offline exe diff (new this pass)

Two tracked instruments, both added here, both read-only against files:

- `tools/probes/exe_reanchor_scan.py` -- section-layout diff, plus content re-find of anything the
  file actually bakes: the JobCommand ability table, the ability-action table and its decoy mirror,
  the inflict-status table, and the code addresses the mod hooks or cold-calls.
- `tools/probes/rip_xref_reanchor.py` -- the runtime globals. These live in zero-initialized image
  space and hold nothing on disk, so content matching is impossible; but every one of them is
  reached by RIP-relative instructions whose 32-bit displacement encodes the address. Find those
  sites in the old exe, re-locate each by its surrounding bytes (with the displacement punched out,
  since that is exactly what changes), read the new displacement, recompute the target. Report only
  on consensus across independent sites.

The consensus counts matter: RosterBase was confirmed by 33 independent referencing sites agreeing
on the same address, ActorPtr by 12, SubmenuFlag by 15. That is a stronger claim than a single live
fingerprint hit, and it cost no owner time.

## Image structure: same layout, fatter tail

Section VA spans are IDENTICAL for the first eight sections; only the last four shifted, all by the
same +0x5EA000, because the big packed section grew by ~6.2MB. The section NAMES are scrambled
between builds (`.bss` -> `.code`, `.data2` -> `.data`, `.didata` -> `.xpdata`, ...): that is
protector-driven renaming per build, NOT a layout change, and matching sections by name will lie to
you. Match by index / VA span.

Measured shift by region (correlation of 4KB windows; docs/PATCH_REANCHOR.md's "never interpolate
across regions" still applies):

| region | shift |
|---|---|
| code 0x140001000 .. ~0x140280000 | +0 |
| code ~0x1402A8000 .. ~0x140420000 | +4 |
| code ~0x14042C000 .. end of section | +16 |
| data 0x140611000 .. 0x14077C000 | +0 |
| data 0x14077C000 .. 0x14091FA00 | +0 |

A caution for whoever reads this next: a naive same-offset byte comparison called the code section
"59% changed", which is nearly meaningless. Most of that is the +4/+16 shift making every byte
mismatch its neighbour, and much of the rest is RIP displacements adjusting to the shift. Correlate
at a candidate shift before believing a churn number.

## Verified UNCHANGED (offline, 2026-07-24)

Data / baked tables, by exact content match in the new image:

- `Barrage.AbilityBase` 0x14067E213, unique hit. The LaunchGuard JobCommand landmark bytes were
  additionally checked in place: rec 8 at 0x14067E2DB reads `96 97 98 99 9A 9B 9C 9D` and rec 9 at
  0x14067E2F4 reads `64 65 66 67 68 69 6A 6B`, both exactly as expected, so the guard's signature
  constants need no change.
- `Offsets.InflictTable` 0x14080FBA0, `ProvokePolicy.DecoyActionTable` 0x14078961C, unique hits.
- `Offsets.LiveActionTable` 0x14078B2DC: first hit is at the pinned address; the "ambiguity" the
  tool reports is four hits exactly 0x14 apart, i.e. neighbouring 20-byte rows of the same table
  carrying identical content. Intra-table row repetition, not a relocation.

Runtime globals, by RIP cross-reference consensus (sites agreeing / sites resolved):

| anchor | address | sites |
|---|---|---|
| Offsets.RosterBase | 0x1411A7D10 | 33/33 |
| Offsets.SubmenuFlag | 0x140D4080C | 15/15 |
| Offsets.EventId | 0x140782A94 | 14/14 |
| Offsets.ActorPtr | 0x14186AF68 | 12/12 |
| Offsets.InventoryCountBase | 0x1411A7C00 | 10/10 |
| Offsets.LiveBattleMapId | 0x140784478 | 9/9 |
| Offsets.PauseFlag | 0x140C6B1C8 | 6/6 |
| Offsets.CombatAnchor | 0x141855CE0 | 3/3 |
| Offsets.TurnQueue | 0x1407832A0 | 2/2 |
| Offsets.BattleMode | 0x1409069A0 | 2/2 |
| Offsets.Slot9 | 0x140782A54 | 1/1 |
| Offsets.Acted | 0x140782A8C | 1/1 |

`StatusApply.BattleUnitsBase` is derived from CombatAnchor in code, so it rides along.

Runtime globals with no direct cross-reference, resolved by NEIGHBOURHOOD evidence (every
RIP-referenced address within a window either side, all shifting by the same amount):

| anchor | address | evidence |
|---|---|---|
| Offsets.Slot0 | 0x140782A30 | 599 neighbours within +-0x400, all delta +0 |
| Offsets.TerrainGrid | 0x140C6B440 | 226 neighbours within +-0x400, all delta +0 |
| Offsets.MenuCursor | 0x1407FC620 | 31 neighbours within +-0x8000, all delta +0 (unused by any gate anyway) |
| Offsets.ArrayBase | 0x140899F50 | 4 neighbours within +-0x8000, all delta +0 (thinnest evidence here) |
| MirrorWeapon / MirrorOffHand / WpScratch | 0x141876EB4 / EB6 / E96 | 481 of 492 neighbours delta +0; the 11 outliers are short-context false matches |

The mirror trio and ArrayBase are the two weakest rows and both are cheap to settle live: open an
equip card (mirrors) and credit one kill (ArrayBase).

## The one casualty: FnSetTextString, +4

`PromptSwapHook.FnSetTextString` 0x1403F1098 -> **0x1403F109C**. Same story as 1.5.1, smaller
delta, and this time caught before it could do harm rather than after two crashes.

Evidence, all byte-level and reproducible from the two files:

- At 0x1403F109C the new image carries the same 11-byte prologue the mod pins
  (`40 55 53 57 48 8B EC 48 83 EC 50`).
- Immediately before it sits the same `20 5F E9 01 00 00 00 CC` tail-and-padding that precedes the
  1.5.1 entry: the runbook's required ret/CC padding landmark, present identically in both builds.
- That prologue occurs EXACTLY ONCE in 0x1403F0000..0x1403F3000 in each build (0x1403F1098 old,
  0x1403F109C new), so the re-find is unique, not a plausible-looking neighbour.
- The function body is 505/512 bytes identical at shift +4. All seven differing bytes are RIP
  displacement adjustments that reconcile exactly: five change by -4 (the instruction moved +4 while
  its target did not), and two change by +12 because they point at 0x14041FAD0 -> 0x14041FAE0, a
  target in the region that shifted +16. Nothing in the function's logic changed.
- The wrapper that converges on it moved the same +4 (0x1403F1068 -> 0x1403F106C) and is
  byte-identical.
- The OLD address on 1.5.2 now reads `00 00 00 CC`, i.e. padding, not a function entry.

That last point is the reassuring one: `HookLandmark.Verify` would have read those padding bytes,
failed the prologue compare, and refused to install with one logged warning. The 1.5.1 incident
(detour on a mid-function address, two crashes on auto-battle) cannot repeat; the cost of missing
this would have been silently dead tier-up toasts, not a crash.

## Data layer: green

`python tools/audit_nxd_bakes.py` exits 0 against the freshly installed 1.5.2 pac: Item-en 1130
intended / 5 allowed / 0 drift / 0 unintended, Ability-en 7/0/0/0, UIStatusEffect-en 4/0/0/0. So
1.5.2 changed no text cell our full-table bakes touch, and the LW-78 rebase path is not needed this
patch. (The item rebalance never depended on our addresses anyway; it anchors by the modloader's
own scans.)

## Not re-verified this pass, deliberately

- Dev spikes (`#if LWDEV`: BodyDoubleSpike, StatusSpike, and friends). Three BodyDoubleSpike code
  addresses happened to verify unchanged in passing (FnNodeBuild 0x14026EBEC, FnEnroll 0x140274F30,
  FnObjPopulate 0x140284A80, all in the +0 region), but the rest were not audited: runbook Step 7
  says re-anchor spikes lazily, when next used.
- Treasure Master: auto-disarms on its own baked build key, as designed. Removal is planned
  (docs/TODO.md LW-10), so stay-disarmed remains the recommendation.
- SEMANTICS of anything. This whole pass is an address audit. The 1.5.1 pause-flag lesson (address
  survived, meaning narrowed to card-open only) is exactly the class of change this method cannot
  see, which is why the live pass still owns the verdict.

## Live pass (owner, 2026-07-25) and what it did NOT cover

Session 01:52 launch, 01:54 battle exit. `scan_logs.py --require-battle --flight` exit 0, zero
warnings, zero errors across 196 log lines.

- ARMED clean: "The game build matches all memory landmarks; Living Weapons is armed". No
  stand-down. Settles the re-derived PE build key.
- The MOVED HOOK is confirmed, and by the strongest available signal rather than by a toast: the
  prompt hook's own canary fired ("the game's first prompt this session was intercepted") and the
  sampled prompt heads read as real text ("Objective", "Guest", "Special", "Camera Controls"). A
  wrong entry yields caller garbage in rdx, which is exactly the LW-89 failure shape, so readable
  text at 0x1403F109C is a direct refutation of it. A tier-up toast also ENQUEUED ("Chaos Blade has
  gained its 5th kill and has grown to Chaos Blade+"); delivery needs a facing prompt, which this
  battle never reached, so enqueue is as far as the tape goes.
- ArrayBase, the thinnest offline row (4 neighbours), is settled by a credited kill carrying victim
  identity: "Chaos Blade claims kill number 4", victim nameId 833, job 100, battle slot 14.
- The display mirrors (MirrorWeapon / MirrorOffHand / WpScratch), the OTHER thin row, were NOT
  settled by the log. The battle exercised the Attack-menu row, which resolves by cursor, not
  through these mirrors; no equip card was opened during the recorded session. The owner confirmed
  the card by eye afterwards, so these three rest on offline evidence (481/492 neighbours at delta
  +0) plus an owner eyeball, NOT on a log line. Recorded here deliberately: if a display bug ever
  surfaces on 1.5.2, this is the row to re-check first.
- Semantics were not systematically re-checked (the 1.5.1 pause-flag class of change). Nothing in
  the session suggested drift, but absence of a symptom in one battle is not a semantic audit.

## The lesson

1.5.1 taught "read the old address before assuming anything moved". 1.5.2 extends it: you can read
the old addresses without the game running at all. Keep the previous build's exe (the runbook has
said to back it up since the 1.5 port, and this is the payoff), and a patch's address audit becomes
a file diff measured in minutes. The technique that generalises is the RIP cross-reference: when the
data is invisible because it only exists at runtime, the CODE that reaches it is still sitting there
in the file, stating the address in plain sight, in as many independent places as the game happens
to touch that global.
