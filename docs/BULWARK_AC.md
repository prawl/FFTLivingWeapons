# Bulwark ("Line in the Sand") — acceptance criteria

STATUS: CONTRACT (design gate for the Bulwark signature; owner-only live sign-offs)

The Sunderer +3 signature (id 50; the earth-splitter earns its name). The knight plants the
blade and becomes part of the map:
while the stance holds, the ground behind them cannot be walked on. Enemies do not get
funneled by trickery or mind control; the tile itself refuses them. Vanilla FFT has no
zone control of any kind. This is the first.

**The mechanism** (Tech: terrain grid `0x140D8DCB0` — CORRECTED 2026-07-28 from the wrong base
`0x140D8DCC0` (16 bytes / 2 records too high, which landed every write 2 tiles east of target;
the disasm operand `[rdx + idx*8 + 0xD8DCB2]` puts record byte +2 at `0x140D8DCB2`, which only
resolves against the `0xB0` base) — 8 bytes/tile, `idx = x + y*mapWidth + layerBit*0x100`. The
walkability lever is byte **+6 bit 0x02**: the engine's OWN obstacle state (this map's five
natural trees all read byte+6 == 0x22, bit 0x02 set). OR'ing it in blocks movement AND enemy AI
pathing while the tile stays hoverable/selectable, rendering the game's native red circle-slash
"invalid destination" cursor. Bit 0x01 also blocks but additionally strips the tile from the
cursor mask (rejected — the player couldn't even hover it). HEIGHT (byte +2, the ORIGINAL
mechanism this doc first shipped with) is NOT a walkability input: raising a vacant tile's
height left it selectable and SOFTLOCKED the game when a unit stepped onto it — that design is
dead. FACING (band `+0x35` low 2 bits, packed with the same byte's terrain-layer bit, live-proven
2026-07-28 to track the Wait facing wheel) picks which neighbor is "behind": 0=South (looks
0,-1), 1=West (looks -1,0), 2=North (looks 0,+1), 3=East (looks +1,0); behind = the tile OPPOSITE
the wielder's look direction. LIVE-PASS CORRECTED 2026-07-28 06:15: the Y-axis was flipped from
the design above — a facing-North plant at (9,8) barred (9,9) and the owner watched it render IN
FRONT of the unit, not behind (the companion facing-West plant at (10,8) was correct; X-axis was
never wrong). Corroboration: at battle start, enemies at LOW y face the players at HIGH y and
read facing byte 2 (North) — so North looks toward +y, not -y as a sibling repo's "y+ = south"
note had assumed. The game's own facing LABELS are unchanged (byte 2 is still North); only the
Y-axis delta flipped: South's behind is now y+1, North's behind is now y-1. Settled live
2026-07-28, LIVE_LEDGER Contradicted-section terrain entry. GRID WRITES PERSIST THE WHOLE PROCESS
SESSION (stale walkability dirt once crashed the game), so restore is mandatory on every path
that ends a hold, including battle exit (criterion 9 / A1).)

## Premise gate (before any build)

- [ ] P0. The owner flips the terrain-grid ledger row to PROVEN. (Owner only.)
- [ ] P1. The grid base holds across at least two different maps and a battle restart
      (the address is module-static; the CONTENT must be confirmed per-battle-rebuilt so a
      restore miss cannot leak into the next fight).
- [ ] P2. One probe answers whether a cleaner NO-ENTRY lever exists than the height hack:
      the f0 flag bits (a 0x40 delta was observed between adjacent tiles) and f3 get one
      poke session each. Height is the fallback, not the requirement.

## Activation and lifetime

- 1. The stance triggers on the wielder's FULL WAIT: turn ended with no move and no action
     (the shipped Mushin turn-flag read). No granted command, no ability slot, no marker.
- 2. It holds until the wielder's next turn OPENS, then releases on its own. It also
     releases immediately if the wielder dies, is removed, or the battle ends (battle-edge
     reset, the standard `ResetBattle` seam).
- 3. Wielder = the deployed main-hand Sunderer holder, resolved the same way every
     signature resolves its wielder; two deployed copies follow the LW-136 rule (refuse
     rather than guess). No collision with Mushin's full-wait trigger: that is katana-only
     and a unit has one main hand.

## Blocking rules (the owner's three, plus the traps they surfaced)

- 4. While active, the ONE TILE DIRECTLY BEHIND the wielder (per its facing at the moment of
     planting) is impassable: it vanishes from every enemy's move range and enemies path
     around it. This is "the anti-Provoke, the leave-me-alone move" — the wielder's own
     mobility and every ally's movement are untouched; only the back tile, and so the
     back-attack bonus, is denied. (SUPERSEDES the original four-orthogonal-tile ring: a full
     ring walled allies too, which the owner rejected in favor of denying only the back.)
- 5. The BEHIND TILE is modified ONLY IF vacant. Holding a living unit, a corpse, a crystal,
     or a chest, it is NEVER written: occupancy already blocks entry, so the effect loses
     nothing, and the occupant keeps normal movement (walk away freely) and normal attacks
     (their tile's obstacle state never changed). The 2026-07-09 corpse-tile pathing crash
     class is avoided by the same rule.
- 6. If the behind tile's occupant LEAVES while the stance holds, it is raised after two
     consecutive vacant engine ticks, never mid-walk (the mover owns transforms during a
     walk; we never race it) — the same deferred-watch machinery the four-tile ring used,
     now watching the one tile.
- 7. MAP EDGE: if the wielder's back is to the map edge, the behind tile falls off the
     declared map and `BulwarkPolicy.BehindTile` returns null — the wielder plants NOTHING
     that turn (no substitute tile, no partial ring). PER-AXIS bounds checks run BEFORE any
     index math: an unguarded x-1/y-1 at the map edge would otherwise silently wrap to a
     valid tile on another row/column. A test pins all four facings' off-map case.
- 8. The wielder's own tile is never modified, and only the wielder's LAYER is blocked
     (bridge decks above or below keep their vanilla behavior; the layer question is v1
     scope, revisit only with live evidence).
- 9. RESTORE is MANDATORY and ACTIVE: write the exact prior byte back on release, wielder
     death, AND the battle-exit edge, and confirm the restore book is empty afterward.
     **CORRECTED 2026-07-28: the grid does NOT rebuild per map (that P1 claim is CONTRADICTED
     -- live, walkability dirt persisted across restarts and onto the world map and CRASHED the
     game). So an unrestored tile LEAKS for the whole process session and can crash the engine.
     Restore is the safety-critical path, not a nicety.** The write is in-process RAM only
     (never disk/save), so a full game relaunch is the last-resort clean slate.

## Plan-review amendments (2026-07-27 adversarial pass; each was a blocker or should-fix)

- A1. Battle-edge semantics — SETTLED 2026-07-28 (inverted after the crash, then rewritten
      for the back-tile rework). Grid writes PERSIST THE WHOLE PROCESS SESSION (proven live:
      stale walkability dirt survived battle restarts and onto the world map, and once
      crashed the game), so the battle edge is a RELEASE path, not a drop. `ResetBattle()`
      now calls the same guarded restore path `Release()` uses BEFORE clearing state: if a
      hold is active (or the restore book is non-empty), it writes the saved original back,
      then clears. Engine fires `ResetBattle()` on BOTH the enter and exit edges: the exit
      edge is what actually restores a still-active hold; the following enter-edge call is an
      idempotent no-op on an already-empty book (`Release()` itself performs zero writes and
      stays silent when there is nothing to restore, so the routine double-edge reset produces
      no log noise). The restore is safe on ANY edge because the grid is one persistent
      per-process structure and the book holds this same session's true originals — there is
      no "wrong map" to scribble onto. Belt-and-suspenders: the write is in-process RAM only,
      so a full game relaunch is always the last-resort clean slate.
- A2. Two deployed Sunderers REFUSE via the single-wielder resolver (LW-136 semantics), never
      the all-wielders variant: two planters would contest tiles with colliding restore books.
- A3. The map WxH pair (0x140C6AD6A/B) carries runtime SANITY GATES as a floor: refuse to
      plant unless 1 <= W,H <= 30, W*H <= 256, and the wielder's own (gx,gy) lies inside WxH.
      Evidence so far: the pair read 11x12 and 10x18 on the two live maps, and the pathfinder's
      own width register (r14 = 11) matched the pair on the 11-wide map.
- A4. VETO writes preserve every other bit of f6, only OR-ing in bit 0x02 (the obstacle
      state): `vetoed = orig | 0x02`. Idempotent on an already-vetoed byte (a tree's 0x22
      stays 0x22); restore is the exact original byte. SUPERSEDES the original height formula
      (`orig & 0xE0 | clamp(...)`), retired with the height mechanism.
- A5. RETIRED with the height mechanism: the original "high ground shrinks the block below
      jump reach" concern was specific to a graduated height write and does not apply to a
      binary obstacle bit. Whether Fly/Teleport ignores the byte +6 veto (as it ignores
      ordinary terrain) is UNTESTED for this mechanism specifically — treat as unverified, not
      assumed, until a live pass confirms it (folds into V6).
- A6. Tile (0,0) reads permanently occupied (the frozen-twin mirrors live there): a wielder
      whose behind tile resolves to (0,0) plants nothing that turn — the safe direction (the
      occupied-tile rule, criterion 5), documented rather than special-cased.
- A7. Wielder LOSS release is explicit machinery, not an assumed edge: while planted, a
      wielder reading dead (hp 0 or the Dead bit) or failing to resolve for N consecutive
      ticks restores and releases (a corpse never raises the turn flag).
- A8. The plant announcement rides the toast queue's normal delivery (the next facing
      prompt), one prompt late by design, on a reserved event key that cannot collide with
      the genuine tier-crossing toasts.

## Accepted costs and exclusions (v1)

- 10. NO PERSISTENT red/painted tile marker (a highlight visible without hovering). Tile
      paint is walled behind LW-142 (the quad arena crashes on naive writes) and the
      mark-blob addressing (solver-refuted formula). The player-facing signal is the plant
      announcement through the shipped toast lane, the effect itself, AND — new, free, a
      byproduct of the mechanism itself — the game's own red circle-slash cursor the moment
      a player hovers the barred tile: a native affordance Bulwark never has to draw.
- 12. AI sanity is a WATCH item, not a precondition: one live battle confirms no AI freeze
      or pathing loop when a preferred route is blocked (the engine handles real cliffs
      constantly; evidence says fine, eyes say for sure).
- 13. BACK TO THE WALL: a wielder whose behind tile falls off the map plants nothing that
      turn (criterion 7). No substitute tile is chosen; the stance simply does not activate.
- 14. An OCCUPIED behind tile defers rather than fails: the stance still exists (toast fires,
      `_planted` is true) but nothing is barred until the occupant leaves and two consecutive
      vacant ticks confirm it (criterion 6). An occupant that never leaves means the tile is
      never barred that hold.

(Item 11, the "phantom-cliff" cosmetic accepted while the height lever was in use, is DELETED:
the height mechanism it excused is retired, and its replacement — the native red circle-slash
cursor over the barred tile — is a FEATURE, not a cost. See item 10.)

## Verify (owner-only live pass)

- [ ] V1. Plant via full wait; the ONE tile behind the wielder's facing leaves enemy move
      ranges, and the other three orthogonal tiles are untouched.
- [ ] V2. An enemy standing on the behind tile walks away normally and attacks normally
      while adjacent; its vacated tile then blocks.
- [ ] V3. Two waits from the same spot on the map's edge row, one per orientation. First:
      wait FACING INTO the map, so the wielder's back is to the void — nothing plants (the
      stance simply does not activate; the file-only Debug refusal "the wielder's back is
      to the map edge" is the expected receipt) and no phantom block appears anywhere else
      on the map. Then turn around and wait FACING THE VOID, so the wielder's back points
      into the map — the real tile behind him bars normally.
- [ ] V4. A corpse on the behind tile is untouched; a revive onto it still works.
- [ ] V5. Release on the wielder's next turn; the tile restores exactly; a battle restart
      (and a battle END) shows pristine terrain — no leaked obstacle bit onto the next fight
      or the world map.
- [ ] V6. One full battle with the stance used twice, across at least one facing change: no
      AI freeze, no crash, no drift in the flight recorder's battle-exit archive, and an
      AI unit paths AROUND the barred tile rather than through it (A5's untested claim).
