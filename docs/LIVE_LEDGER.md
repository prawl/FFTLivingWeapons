# Live ledger — runtime memory-mechanic claims

The single home for "is this actually true in the live game?". Every load-bearing claim the
Living Weapon runtime rests on gets a row: the mechanism, the evidence (probe / log / play
session), the date, and a status. Mirrors the grid CSV's convention: **only Patrick flips a
row to PROVEN** (he is the live-verification authority); anyone may add UNCERTAIN rows or
attach contradicting evidence. A "proven live" claim in a doc comment without a row here is
a rumor with good posture.

Statuses: **PROVEN** (live-verified, evidence linked) · **UNCERTAIN** (works-in-code, never
isolated live) · **CONTRADICTED** (evidence points both ways — probe before building on it)
· **WALLED** (investigated, blocked; do not retry without new information).

## Proven

| Claim | Mechanism | Evidence | Date |
|---|---|---|---|
| Charm survives its on-hit break | write+HOLD auth-copy `+0x49/0x20` AND `+0x54/0x20` | held through a hit live; `0x140893C00` is a cosmetic decoy | 2026-06-08 |
| Poison can be pinned + made uncurable | band `+0x48/0x80` re-OR + timer `+0x4A` re-pin (init 36) | two-healer battle at 1.75x rate; `probes/poison_probe.py` | 2026-06-09 |
| Doom bytes + hasten | bit `+0x49/0x01`, countdown `+0x59` (init 3); write-down renders LIVE on the head counter | Eclipsebolt +3 in play; doom research session | 2026-06-09 |
| Extra turn via CT slam | scheduler CT = combat `base+0x41` (band entry `+0x25`) slammed to 100 | Zwill v8 played live; `probes/ct_probe.py` found the byte | 2026-06-09 |
| Victim turn counting | band entry `+0x09` read, ≥90 then <70 = one turn | Maim + CharmLock victim windows in play | 2026-06-09 |
| Reaction suppression | hold-zero combat `+0x94` (4 bytes), one-shot restore | Counter suppressed through 5 hits; `probes/cripple_probe.py` | 2026-06-09 |
| Command grant via JobCommand inject | ability id into record slot + extend bit + held learned bit | Barrage cast end-to-end (Thief primary AND Steal-as-secondary) | 2026-06-10 |
| Support bits grantable live | OR-set combat `+0x98`; calculation-gated supports take effect | Concentration etc. in play; HP/MP Boost + Doublehand/Dual Wield are build-time-only (read-back fine, no effect) | 2026-06-08 |
| Movement is exactly-ONE-effective | both Font bits held perfectly; engine honored only Lifefont | the retired Spiritual Font bit-grant attempt | 2026-06-10 |
| Master Teleportation grantable | movement image for id 243 held | player teleported in session | 2026-06-10 |
| MP pair on the band | `+0x18/+0x1A` u16 pair | MP visibly restored on screen (gated per battle by MpLayoutOk) | 2026-06-10 |
| Mid-battle level-up drift | live structs update; roster keeps pre-battle level until battle end | Phoenix Down mis-credit chain, fixed via `LevelMatchesRoster` | 2026-06-10 |
| slot0 quit-stick | QUIT leaves slot0 at 0xFF on the world map; victory clears to 0x66 | `probes/sentinel_probe.py` | 2026-06-10 |
| Static array freezes on battle restart; band stays live | tracker reads must use the `0x14184xxxx` band | fresh corpse 0/539 visible only in band; Ramza's real position only there | 2026-06-09 |
| Frozen (0,0) twins in the band | real-position match outranks the (0,0) duplicate | slots 25+28 same identity live; Zwill locate v7→v8 | 2026-06-10 |
| Plague needs a grace window | poison-bit edge and acted window land up to ~2s apart, either order | four open windows, zero latches, chocobo cleansed the "permanent" poison | 2026-06-10 |
| inb flag (`+0x12`) pulses mid-battle | filter by sane field bounds + slot sign, never the flag | half the live enemies read 0 at any instant | 2026-06-09 |
| Cursor tile (x,y) addresses | cursor X = `0x140C64A54`, cursor Y = `0x140C6496C`, linear idx = `0x140C64E7C` (all u8) | live diff (Y tracked 5→11 on hover) + FFTHandsFree CommandWatcher reads the same | 2026-06-11 |
| **Treasure Master: the native tile mark IS writable** | The hover-+-2 mark is a per-tile FLAG (bit `0x80` of a status byte), NOT a coordinate list (that's why the earlier AoB scan missed it). Found via a differential toggle scan (mark on/off/on/off ×N, keep only addresses that flip in lockstep AND survive a live re-read): one marked tile flips bit `0x80` at THREE copies `0x140e7c3bb` / `0x14102fb23` / `0x14117fe67` (frame/buffer copies). WRITE+HOLDING `0x80` onto them with the tile UNMARKED made the engine RENDER the mark itself — no input, no button. Release → engine clears it (so the DLL holds, like stat growth). Output half of Treasure Master SOLVED. `probes/mark_probe.py togglefind/holdmany`. OPEN: per-tile addressing (these 3 are THIS tile's copies; need the stride/layout to mark an ARBITRARY tile — mark a 2nd known tile, toggle-scan, derive the base+stride). Heap addresses may rebase per battle → resolve via a pointer chain or AoB, not hardcode. | 2026-06-11 |
| Battle tile structures cluster at `0x140C6xxxx` | terrain grid `0x140C65000` (7 bytes/tile, 9×8 window, STATIC map data — marking never wrote here); move-tile list `0x140C66315` | FFTHandsFree CommandWatcher.cs + mark_probe diffs | 2026-06-11 |

## Contradicted — probe before building on these

| Claim | The contradiction | Next step |
|---|---|---|
| CharmLock's lock-turn expiry counts turns off auth-copy `+0x25` | `Offsets.ACtSlam` (+0x25) is documented write-only ("a live watcher saw zero transitions") and SpiritualFont's notes say NEITHER +0x25 nor +0x09 band reads ever reached ≥90 across full turns — yet CharmLock reads +0x25 on the ±1MB auth copies and charm-lock behaved in play. Possibly the auth-copy family ticks where band entries don't; possibly the expiry path has simply never fired (locks released by battle end instead). | watchspan a locked enemy's `+0x25` on its auth copy across its turns (`probes/ct_probe.py watch`); if it never reaches ≥90, the N-turn expiry is dead code and the lock only ends at battle end |
| `mhp == 2000` is a valid unit | five enemy scans accept 2000 inclusive (`Band.EnemyFingerprints`), while `Band.IsValid` / `EnemyOracle` / `Plague.IsValidEnemyMhp` exclude it | check whether any IC unit can reach exactly 2000 max HP; then settle ONE bound everywhere |
| Treasure Master via the move-range HIGHLIGHT (paint tiles blue) | PARTIAL LEVER FOUND, source not yet. The move-highlight has an on/off GATE at `0x140c64c68` (u8: ~4 idle, 13 in move) — write+HOLDING it nonzero keeps the highlight rendered even out of Move mode (proven live 2026-06-11, ~3.4M holds). BUT the GATE is not the SOURCE: holding a custom tile list into `0x140C66315` (the move-PATH/validation list, 7 bytes/tile) changed nothing on screen — the engine draws the blue quads from a different buffer (float world-coords at `0x140c80000+`). One-shot count writes are reverted (engine-maintained); only a hold sticks. Next: find the buffer that FEEDS the `0x140c80000` quad render (diff in-move vs out, focus the band between the count and the quads), or learn the tile→world-coord mapping to hold the quads directly. `probes/mark_probe.py hold/holdmany`. |

## Walled — do not retry without new information

| Claim | Wall | Evidence |
|---|---|---|
| Crit-hit chance settable | no data field, no per-unit byte to hold, engine byte Denuvo-walled | full search, same wall as proc-rate 0x32 |
| Guest/Traitor allegiance flip | engine pool-relocation, not a holdable byte | +0x46/+0x02/+0x1B/+0x23/+0x1DD all dead via same-unit Entice heap diff |
| Guaranteed Knockback via gx/gy writes | engine-authoritative but renderer never re-derives → compounding sprite desync | `probes/knockback_probe.py` |
| ~~Treasure Master via the NATIVE tile mark is walled~~ **OVERTURNED 2026-06-11** | The "no writable store" verdict was WRONG — it assumed a coordinate list (3.8 GB AoB scan, 0 hits) and never tried a differential toggle scan. The mark is a per-tile FLAG bit, and write+holding it renders the mark. See the PROVEN row above. Lesson: when an AoB/coordinate scan comes up empty, toggle-scan for a FLAG before declaring a wall. (The static `trap_treasure_tiles.json` data pass + FFTHandsFree `DetectMap()` are still the INPUT half — that part of the plan stands.) |
| >261 items displayable | display wall is a boot-built registry; page-guard hook crashes | `ITEM_CAP_261_BREAK_JOURNEY.md` |
| External probes on engine code | Denuvo; module statics ARE readable externally | the DLL is the only instrument for code-adjacent experiments |
