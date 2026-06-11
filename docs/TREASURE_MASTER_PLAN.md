# Treasure Master — Implementation Plan (2026-06-11)

Auto-mark treasure tiles in battle using the game's NATIVE tile mark: hold bit `0x80` on each
tile's per-map module-static render-flag bytes every tick. Mechanism proven live — see
`docs/treasure_master_prototype.md` TOP section (everything below its HISTORICAL JOURNEY marker
is superseded). This doc is the build plan; the prototype doc stays the RE record.

## Decisions (locked 2026-06-11)

- **Carrier = an accessory, not a weapon**: prod builds gate the feature on the ring being
  equipped by any deployed unit; dev (LWDEV) builds are always-on for testing. Nominated item:
  **Scholar's Ring (id 260)** — utility identity already, real slot cost. items.json prose pass
  deferred until the nomination is confirmed.
- **Treasure only.** Trap tiles are never marked (a mark reads as "free loot here").
- **Marks stay lit** while the ring is worn (the hold re-asserts within ~33ms of a manual
  un-mark; opting out = unequip the ring).
- **Claimed treasure un-marks** via the inventory-count heuristic: each tile's item ids are
  known, and `count[id] = u8 @ 0x1411A17C0 + id`; when a tile's item count ticks up while a unit
  stands on that tile → per-tile Claimed sub-state, stop holding it for the rest of the battle.
  Zero new RE.
- **Game patch = hard global disarm** (dataset PE build key vs live header) until re-capture.
- **Ship in increments**: prod can ship from the first captured maps; uncaptured maps are silent
  no-ops. Coverage is a property of the data file, never the code.

## Map detection

The FFTHandsFree live battle map-id byte: module-static **`0x14077D83C` (u8)**, current battle's
map id, valid 1..127, STALE out of battle (only read while InLive). Verified in FFTHandsFree
(GameBridge/LiveBattleMapId.cs; Dugeura=86 / Beddha=82 / Araguay=80; survives restart; 7 lockstep
backup addresses on file). Its id space IS `MapTrapFormationData.xml`'s Id space (74 = The Siedge
Weald in both) — one guarded read keys the dataset. Do NOT port `DetectMap()` fingerprinting
(documented unreliable there; mis-picks MAP074).

## Data flow

```
MapTrapFormationData.xml ──tools/extract_trap_table.py──▶ data/map_trap_formation.json (committed; CI never reads the install)
in-game capture sessions ──tools/probes/treasure_flags.py──▶ data/treasure_addrs.json (committed; the precious file)
        both ──tools/gen_treasure_db.py (pipeline gate, before the unit-test gate)──▶ LivingWeapon/treasure.json
                 (csproj None Include + $RequiredModFiles + release.yml sentinel) ──▶ ships next to the DLL
runtime: TreasureDb.Load (fail-soft) ──▶ TreasureMaster (ISignature) holds 0x80 via guarded IGameMemory writes
```

`data/trap_treasure_tiles.json` stays untouched as the historical coord validator.

## Runtime: four-layer containment (no write until ALL pass)

- **L0 build key** (startup, once): dataset PE key (TimeDateStamp+SizeOfImage) vs the live header
  read through the injected IGameMemory. Mismatch → global disarm, one loud log.
- **L1 map id**: guarded U8 @ `0x14077D83C`, valid 1..127, present in the dataset. Absent → one
  log line, silent battle.
- **L2 map identity proof**: FNV-1a64 over a FIXED-LENGTH prefix of the terrain grid @
  `0x140C65000` must equal the hash captured with the addresses. Fixed prefix — record count is
  per-map and a tail scan could hash residue of a previously loaded larger map. One pinned test
  vector shared verbatim between the Python self-test and a C# fact (cross-language hash drift =
  silently inert everywhere = the worst failure mode).
- **L3 per-address audit**: AddrState over `{0x00, 0x01, 0x80, 0x81}` — bit 0x80 is the mark, the
  low bit is engine-driven don't-care (captures park the cursor ON the tile; the runtime audits
  with it elsewhere), anything else is Foreign and is NEVER written.

ARMED: every 33ms tick, ≤4 tiles × ~6 addrs = ≤24 guarded ops. Per addr: Writable → U8 → write
`cur|0x80` only on difference (CharmLock.Force idiom through IGameMemory — NOT MemBits, which is
static over Mem and invisible to dict fakes). **OR-only by construction: the module has no Clear
path.** Release = stop writing; the engine clears marks itself (ledger-proven). Map-id re-checked
every tick; full fingerprint revalidation every ~30 ticks; transient read failures suspend writes
immediately but disarm only after K consecutive bad ticks.

**Lifecycle**: the debounced battle-exit edge is SUSPENDED by event frames, so chained story
battles may never fire ResetBattle — the module additionally treats a stable map-id CHANGE as a
battle boundary (full state reset + re-arm). Writes also suspend during real-event frames.

## Known holes the design review found (probes required before prod)

1. **Mid-battle quickload/battle-retry** (savescumming for rares) beats all four layers IF the
   flag buffers shift on the same map (same id → L1 passes, same terrain → L2 passes, 0x00/0x01
   are ubiquitous → L3 passes). Probe: hold a Sledge mark through a quickload. Ledger row 31
   (static array freezes on restart) is the worrying analog.
2. **Camera rotation/scroll/zoom**: every stability proof so far was at one camera pose. Probe:
   hold a mark while rotating (Q/E), scrolling, zooming.
3. **Fingerprint stability**: hash identical across restart on the same map AND different between
   Sledge and Zeklaus, regardless of which map loaded previously (tail residue).
4. **Resting-byte semantics** away from the capture pose (cursor elsewhere / off-screen / units
   standing on the tile).
5. Accepted limitation: a mod overriding MapTrapFormationData shifts the real treasure while
   every layer passes — marks would lie. Debug path stays "bisect mods first".

## Capture campaign (~90 populated maps, as-you-play)

`treasure_flags.py session`: reads the map-id byte itself, joins the committed snapshot, prints
pending tiles, human name-confirm ("you are on Zeklaus Desert [y/n]" — validates byte↔XML id
alignment per map for free). Per tile: **cursor lock** (refuses until the cursor sits on the
target (x,y), re-checked at every snapshot, abort on drift), the proven cursor-still toggle scan
(clean filter `(off&0x80)==0 && on==off|0x80`, <4 hits → partial + re-capture nag), then the
**trust gate** — a 3s hold-test, "did the mark paint? [y/n]" — only eyeball-confirmed `verified`
tiles ship. Atomic save per tile; resumable; `status` dashboard; `verify <mapId>` = read-only
post-patch re-audit. Post-patch bake policy: **warn-and-drop stale-key maps** (a hard fail would
brick every deploy after the first post-patch capture). LWDEV nags on battle enter for uncaptured
maps (the bake emits name/tile-count stubs for them; VERIFIED-only applies to addresses).

## Files

| File | Job |
|---|---|
| `LivingWeapon/TreasureMaster.cs` | ISignature module: state machine + hold loop + L0 read (mem injected). |
| `LivingWeapon/TreasureMaster.Policy.cs` | Pure statics: MapIdValid, Fnv1a64, AddrState, ArmDecision, BuildKeyMatches. |
| `LivingWeapon/ArmAudit.cs` | Separate stateless gate evaluator (injected IGameMemory) — the 200-line seam; a Gates.cs partial would be same-state-machine evasion. |
| `LivingWeapon/TreasureDb.cs` | Pure file loader, MetaLoader-style fail-soft; validates addrs into 0x140000000..0x143000000 and outside the UI arena 0x140C63000–0x140CC5000. |
| `LivingWeapon/treasure.json` | The baked artifact (generated — never hand-edit). |
| `Offsets.cs` +2 (LiveBattleMapId, TerrainGrid) · `Tuning.cs` +knobs (incl. TreasureRingItemId, RequireRing) · `Engine.cs` +ctor line, tail-append to BOTH ordered arrays | Flag addresses never go in Offsets — they are data. |
| `tools/lib/treasure.py` | Shared XML parse (map names come from XML comments — pinned fixture). |
| `tools/extract_trap_table.py` → `data/map_trap_formation.json` | Once per game version. |
| `tools/probes/treasure_flags.py` → `data/treasure_addrs.json` | Capture sessions + `mapid`/`status`/`verify` verbs. |
| `tools/gen_treasure_db.py` | The bake + gate (exit 1 refuses deploy/package); runs its self-test every invocation. |
| `LivingWeapon.Tests/Treasure*Tests.cs` | Policy table tests, FakeSparseMemory state-machine matrix (zero writes when !inLive; OR-only proven via the Written map), one PinnedBuf fact through LiveMemory, loader fail-soft trio, schema lockstep with MissingMemberHandling.Error vs the fresh bake. |

Ring gate v1: prod `RequireRing=true` with the roster accessory-slot reader behind a seam — until
the accessory offset is probe-confirmed, prod stays disarmed (safe default), LWDEV is always-on.

## Build order (branch `treasure-master`; every stage green on both gates)

- **0a — probes + ledger** (live, Patrick at the controls): `mapid` spot-check (Sledge=74,
  Zeklaus=76, across restart); flag-addr restart + QUICKLOAD + camera-rotation probes;
  fingerprint stability/distinctness; resting-byte characterization. Then the ledger work:
  amend row 36's stale "may rebase" caveat, flip the new rows PROVEN (only Patrick).
- **0b — lib + snapshot**: tools/lib/treasure.py (+pinned fixture), extract_trap_table.py,
  committed data/map_trap_formation.json, Id-74 sanity assert vs hand-hovered ground truth.
- **1 — capture tool**: treasure_flags.py (session/cursor-lock/auto-advance/hold-test/atomic
  saves, mapid, status, verify) + data/treasure_addrs.json seeded from the existing Sledge capture.
- **2 — data layer (TDD)**: TreasureDbTests + TreasureSchemaTests failing → TreasureDb.cs,
  gen_treasure_db.py + pipeline wiring + csproj (Condition="Exists") + $RequiredModFiles +
  release.yml sentinels (meta.json is not one today — add both).
- **3 — policy (TDD)**: PolicyTests failing → TreasureMaster.Policy.cs (pinned FNV vector).
- **4 — runtime (TDD)**: stateful tests + PinnedBuf fact failing → ArmAudit.cs + TreasureMaster.cs
  + Offsets/Tuning + Engine wiring.
- **5 — live proof**: capture Sledge's remaining tiles; all four paint; an uncaptured map logs
  the nag with zero writes; the deliberate mis-keyed-dataset experiment proves the fingerprint
  refuses. Ledger flips.
- **6 — campaign + ship**: data-only commits through the full pipeline; ring prose pass in
  items.json; Publish when coverage feels right.
