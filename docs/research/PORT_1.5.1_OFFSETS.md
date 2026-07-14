# FFT:IC 1.5.1: Offset Re-anchor Ledger

STATUS: JOURNAL (closed research log; verify claims against LIVE_LEDGER.md before building on them)

> Damage report for the 1.5.1 point-release re-anchor (LW-81). Companion to
> docs/research/PORT_1.5_OFFSETS.md (the 1.5 recompile, where nearly everything moved) and
> docs/PATCH_REANCHOR.md (the reusable runbook this arc validated). A live read-only probe
> session ran 2026-07-13 on pid 2980, owner at the controls.

## Build identity (the 1.5.1 target)

| build | TimeDateStamp | SizeOfImage | Steam buildid | exe stamped | exe SHA256 |
|---|---|---|---|---|---|
| 1.5 | 0x6A0F86A9 | 0x190EB000 | 23353019 | (prior) | 3625FD9B... |
| 1.5.1 | 0x6A3C5497 | 0x1878E000 | 23901820 | 2026-07-13 06:38 | 841DD4048C9C33958156422CD96EE8D064F5BEB3C5F8A0E23A68AAF2BB87B282 |

SizeOfImage SHRANK (0x190EB000 -> 0x1878E000): the image got smaller, not bigger, unlike the 1.0 to
1.5 jump. That is the tell that a point release, not a full recompile, was in play.

## Headline: the layout survived

Every landmark probed at its OLD (1.5) address either matched outright or was the boring
"stale-but-plausible read, confirm behaviorally" case. Exactly ONE address moved. The cheapest
possible probe (read the old address, check the known content signature) answered almost the
entire audit before any scanning was needed.

## Verified at UNCHANGED addresses (live, 2026-07-13)

- JobCommand table: content-signature-verified UNIQUE at 0x14067E213 (rec 8 Aim + rec 9 Martial
  Arts + whole-table coherence; jobcommand_find_probe.py "find").
- RosterBase 0x1411A7D10 (slot0 nameId=1 Ramza, rhand=42, real party rows).
- InventoryCountBase 0x1411A7C00 (give-all 99s pattern, ids 1-35).
- Slot0=0x10 / Slot9=0xFFFFFFFF sentinels; BattleMode 0x1409069A0 read 3 in battle, 0 on the world
  map post-battle; LiveBattleMapId 0x140784478 read 76 (Zeklaus) in battle, 119 on the world map.
  Both statics read 0xFF-uninitialized before the session's first battle (new observation, not a
  contradiction: nobody had probed that window before).
- CombatAnchor 0x141855CE0 (Ramza weapon=42 pa=25 ma=13 spd=11 brave=97 faith=75, twin at +0x800
  identical); TurnQueue 0x1407832A0 structured correctly; ActorPtr 0x14186AF68 pointed into the
  band; Acted 0x140782A8C flipped 0 to 1 on an action; EventId 0x140782A94 read 401 (behavior
  confirm deferred to a future cutscene).
- Display mirrors with a card open: MirrorWeapon 0x141876EB4 = 42, MirrorOffHand 0x141876EB6 = 138
  (Vanguard, confirmed on-screen), WpScratch 0x141876E96 = 10 (Ame-no-Murakumo WP).
- PauseFlag 0x140C6B1C8 (+ synced copy 0x140C6B307) is alive at the old address.

## The one mover: SubmenuFlag

Old address 0x140D4085E reads 0 in every state on 1.5.1 (dead byte, not merely stale). New address
0x140D4080C (delta -0x52, a struct-local reshuffle), found by a consistency-sampled 3-state solve:
12 samples per state at 0.4s intervals (card=1 constant, command-menu=0, field=0), plus two
discriminators that separated it from decoys: it reads 1 in the card via BOTH paths (own-turn
Status command AND the pause-menu Units > Status route), and it reads 0 in the abilities list,
which is what rejected the generic-panel candidate 0x140D40554 (that one also read 1 in the card
but did not distinguish the abilities list). Synced sibling behaves identically at 0x140D407BA.

## Pause semantics changed, address did not

PauseFlag stayed at 0x140C6B1C8 but its MEANING narrowed. On 1.5 it held 1 across the whole player
turn (idle + command menu + abilities list + card). On 1.5.1 it reads 1 ONLY while the unit status
card is open; it reads 0 during the command menu and the abilities list. Any caller that treated
"paused" as a broad player-turn signal now sees a narrower true window. Offsets.cs's PauseFlag
comment and the file header note both carry this; BattleState.cs's own predicates were checked and
none assert the old breadth as fact, so no logic there needed correcting, only the constant's own
provenance comment.

## Provisional (behavior confirm owed on the post-deploy live pass)

- ArrayBase 0x140899F50: structurally plausible (a small battle showed 2 sane slots); definitive
  proof is a credited kill.
- EventId 0x140782A94: read 401 with no visible dialogue on THIS session too (same open question
  the 1.5 ledger flagged); a future cutscene is the confirm.
- PromptSwapHook.FnSetTextString 0x14028F79C: prologue bytes read plausible
  (4584c074 0f488d15 f8a74600 488bc8e8); behavioral confirm is a toast actually delivering.
- Dev spikes (#if LWDEV BodyDoubleSpike/StatusSpike constants): deliberately NOT re-verified this
  pass, stale-flagged in Offsets.cs's header note.
- Treasure Master: not touched this commit. It auto-disarmed on its baked build key exactly as
  designed; reviving it is a separate owner decision (docs/PATCH_REANCHOR.md Step 7).

## The lesson

A point release can preserve an entire address layout down to the byte. Before scanning anything,
run the cheapest content-signature probe AT the old addresses first: it either confirms the
landscape is unchanged (as here, in one session) or tells you exactly where to start scanning. The
1.5 recompile taught "expect a non-monotonic gradient of large deltas"; 1.5.1 taught the opposite
lesson just as hard: don't assume a patch moved anything until you have actually read the old
address and found it wrong.
