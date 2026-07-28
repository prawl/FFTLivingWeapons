# Bulwark ("Line in the Sand") — acceptance criteria

STATUS: CONTRACT (design gate for the Bulwark signature; owner-only live sign-offs)

The Sunderer +3 signature (id 50; the earth-splitter earns its name). The knight plants the
blade and becomes part of the map:
while the stance holds, the ground beside them cannot be walked on. Enemies do not get
funneled by trickery or mind control; the tiles themselves refuse them. Vanilla FFT has no
zone control of any kind. This is the first.

**The mechanism** (Tech: the terrain grid at `0x140D8DCC0`, 8 bytes/tile,
`idx = x + y*mapWidth + layerBit*0x100`, f2 low5 = height; the pathfinder consumes it LIVE —
holding a fake height excluded a tile from move range and panned the hover camera,
owner-witnessed 2026-07-27. LIVE_LEDGER Uncertain row of that date.)

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

- 4. While active, the FOUR ORTHOGONAL tiles around the wielder are impassable: they vanish
     from every enemy's move range and enemies path around them.
- 5. VACANT TILES ONLY are ever modified. A tile holding a living unit, a corpse, a crystal,
     or a chest is NEVER written: occupancy already blocks entry, so the effect loses
     nothing, and the occupant keeps normal movement (walk away freely) and normal attacks
     (their tile's height never changed). The 2026-07-09 corpse-tile pathing crash class is
     avoided by the same rule.
- 6. A tile whose occupant LEAVES while the stance holds is raised on the next engine tick,
     never mid-walk (the mover owns transforms during a walk; we never race it).
- 7. MAP EDGE: out-of-bounds neighbors are skipped with PER-AXIS bounds checks. Explicitly:
     x-1 at x=0 and x+1 at x=mapWidth-1 must be rejected BEFORE the index math, because the
     linear index silently wraps to a valid tile on another row. Same for y. A test pins all
     four edges and the corner case.
- 8. The wielder's own tile is never modified, and only the wielder's LAYER is blocked
     (bridge decks above or below keep their vanilla behavior; the layer question is v1
     scope, revisit only with live evidence).
- 9. RESTORE: exact prior bytes on release, wielder death, and battle edge. A crash mid-hold
     must not be able to contaminate anything durable (P1's rebuild check is what makes this
     safe; the write is in-memory only).

## Accepted costs and exclusions (v1)

- 10. NO red/painted tiles. Tile paint is walled behind LW-142 (the quad arena crashes on
      naive writes) and the mark-blob addressing (solver-refuted formula). The player-facing
      signal is the plant announcement through the shipped toast lane plus the effect itself.
- 11. The phantom-cliff cosmetics are ACCEPTED while the height lever is in use: hovering a
      blocked tile pans the camera up and reads a fake height. If P2 finds a no-entry flag,
      this cost disappears; it never blocks shipping.
- 12. AI sanity is a WATCH item, not a precondition: one live battle confirms no AI freeze
      or pathing loop when a preferred route is blocked (the engine handles real cliffs
      constantly; evidence says fine, eyes say for sure).

## Verify (owner-only live pass)

- [ ] V1. Plant via full wait; the four vacant orthogonal tiles leave enemy move ranges.
- [ ] V2. An enemy standing on one of the four tiles walks away normally and attacks
      normally while adjacent; its vacated tile then blocks.
- [ ] V3. Wielder against the map edge: no phantom blocks anywhere else on the map (the
      wrap trap), the existing neighbors still block.
- [ ] V4. A corpse tile among the four is untouched; a revive onto it still works.
- [ ] V5. Release on the wielder's next turn; tiles restore exactly; a battle restart shows
      pristine terrain.
- [ ] V6. One full battle with the stance used twice: no AI freeze, no crash, no drift in
      the flight recorder's battle-exit archive.
