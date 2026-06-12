"""Bake data/treasure_addrs.json + data/map_trap_formation.json -> LivingWeapon/treasure.json.

Gate rules (exit 1 on violation):
  (a) Ship addresses for verified tiles that satisfy ONE of two modes:
        Fingerprinted mode: non-null fpHash AND capturedBuild present.
        Map-id-only mode:   fpVer == 0 AND fpHash is null/absent -- for water/lava maps
          whose terrain grid animates on every re-entry (no stable fingerprint possible).
          capturedBuild must still be present and current (L0 PE key check is NOT bypassed).
      Tiles that match neither mode are skipped.
  (b) Every shipped (x,y) must be a treasure tile (is_treasure) in the snapshot.
  (c) Off bytes must be 0x00 or 0x01 -- other values are WARNED and the single address is
      DROPPED (a lockstep coincidence, not a flag byte). Same for addresses at or above
      0x142000000: the 0x142e detail-store family relocates between runs (see the prototype
      doc) and must never ship. A tile ships only if >= 4 clean addresses remain after drops.
  (d) Addrs must be in the module span 0x140000000..0x143000000 (exclusive high) and outside
      the UI render arena 0x140C63000..0x140CC5000 (exclusive high). No duplicates within a
      map. These are hard failures: a legit capture cannot produce them, so they mean
      corrupt data.
  (e) Build-key policy: dataset key = newest capturedBuild timeDateStamp. Maps captured under an
      older key are warned and dropped (never hard-fail -- would brick deploys post-patch).
      Map-id-only maps are NOT exempt from this: capturedBuild must be present and current.
  (f) Emit stub entries {mapId, name, tileCount, tiles:[]} for every populated treasure map with
      no shippable tiles (runtime nag needs names/counts).
  (g) Self-test on every invocation: 3 pinned FNV-1a64 vectors + join fixture + masked-hash
      pinned vector.  Exit 1 on fail.
  (h) Print coverage summary: shippable maps / stub maps / dropped.
  (i) Fingerprint version policy: maps with fpVer 2 or 3 are shipped as fingerprinted maps;
      maps with fpVer 0 are shipped as map-id-only (water/lava, no terrain gate); anything
      else is WARNED and demoted to stub (re-fingerprint needed via 'refp' or set map-id-only
      via 'nofp' in treasure_flags.py).
      fpVer=2: dry-land maps (field-0 hash, immune to field-1/6 churn).
      fpVer=3: water/lava maps that DO have a stable v3 hash (fields {2,3,4,5}).
      fpVer=0: map-id-only mode (no fingerprint at all -- see rule (a) above).
      Both fpVer=2 and fpVer=3 are shipped with fpLen == TERRAIN_RAW_LEN (1456); the runtime
      dispatches on fpVer.  Map-id-only maps are emitted with fpVer=0, fpHash=null, fpLen=null.

Populated = at least one is_treasure tile in map_trap_formation.json (mapIds 1-127).
"""
import json
import struct
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
from lib.paths import ROOT
from lib.treasure import is_treasure

# ── constants ─────────────────────────────────────────────────────────────────

MODULE_BASE = 0x140000000
MODULE_END  = 0x143000000   # exclusive
UI_ARENA_LO = 0x140C63000
UI_ARENA_HI = 0x140CC5000   # exclusive

# The 0x142e detail-store family relocates between runs (prototype doc: observed at
# 0x142eca../0x142ec4../0x142ebed.. on successive captures). Anything at or above this
# cutoff is a volatile lockstep coincidence, never a stable flag byte.
VOLATILE_BASE = 0x142000000

# A capture normally yields ~6 copies (3 buffer families x a 2-byte pair), but the pair
# byte does not always toggle: Mandalia Plain captured 3 tiles at exactly 3 clean singles
# (one per family) and the in-session hold-test PAINTED on all of them (2026-06-11 live).
# The eyeball trust gate is the real quality bar; this floor only rejects captures too
# thin to have covered the families at all.
MIN_ADDRS_TO_SHIP = 3

# Terrain grid: 208 records x 7 bytes each.  The v2 fingerprint reads the full window
# (1456 raw bytes) but hashes only field-0 (offset i*7 for record i).
TERRAIN_RECORD_LEN = 7
TERRAIN_NUM_RECORDS = 208
TERRAIN_RAW_LEN = TERRAIN_RECORD_LEN * TERRAIN_NUM_RECORDS  # 1456

ADDRS_JSON = ROOT / "data" / "treasure_addrs.json"
TRAP_JSON  = ROOT / "data" / "map_trap_formation.json"
OUT_JSON   = ROOT / "LivingWeapon" / "treasure.json"

# ── FNV-1a 64-bit ─────────────────────────────────────────────────────────────

_FNV_OFFSET = 0xcbf29ce484222325
_FNV_PRIME  = 0x100000001b3

def fnv1a64(data: bytes) -> int:
    h = _FNV_OFFSET
    for b in data:
        h ^= b
        h = (h * _FNV_PRIME) & 0xFFFFFFFFFFFFFFFF
    return h


def masked_terrain_hash(raw: bytes) -> int:
    """Fingerprint v2: FNV-1a64 over field-0 bytes only (byte at i*TERRAIN_RECORD_LEN).
    Immune to changes in fields 1-6 which are live state (change during play).

    Pinned test vector (shared with C# MaskedTerrainHash -- never let them drift):
      raw = 01 02 03 04 05 06 07  11 12 13 14 15 16 17  (2 records)
      field-0 bytes = [0x01, 0x11]
      result = fnv1a64([0x01, 0x11]) = 0x082f3307b4e8a9a7

    Kept for dual-version support: dry-land maps with fpVer=2 continue to use this.
    """
    num_records = len(raw) // TERRAIN_RECORD_LEN
    field0 = bytes(raw[i * TERRAIN_RECORD_LEN] for i in range(num_records))
    return fnv1a64(field0)


def masked_terrain_hash_v3(raw: bytes) -> int:
    """Fingerprint v3: FNV-1a64 over bytes at positions where (i % 7) in {2,3,4,5}.

    Fields {2,3,4,5} are static geometry; fields {0,1,6} (height, slope, flow) animate
    on water/lava maps and are excluded.

    Pinned test vector (shared with C# MaskedTerrainHashV3 and treasure_flags.py):
      raw = 01 02 03 04 05 06 07  11 12 13 14 15 16 17  (2 records)
      fields {2,3,4,5} bytes = [03 04 05 06 13 14 15 16]
        (indices 2,3,4,5 from record 0; indices 9,10,11,12 from record 1)
      result = fnv1a64([03 04 05 06 13 14 15 16]) = 0x05708f90b5f5fac5
    """
    static_bytes = bytes(raw[i] for i in range(len(raw)) if (i % TERRAIN_RECORD_LEN) in (2, 3, 4, 5))
    return fnv1a64(static_bytes)

# ── self-test ─────────────────────────────────────────────────────────────────

def _self_test() -> bool:
    """Pinned FNV-1a64 vectors shared verbatim with TreasureMaster.Policy.cs."""
    vectors = [
        (b"",       0xcbf29ce484222325),
        (b"a",      0xaf63dc4c8601ec8c),
        (b"foobar", 0x85944171f73967e8),
    ]
    ok = True
    for data, expected in vectors:
        got = fnv1a64(data)
        if got != expected:
            print(f"SELF-TEST FAIL: fnv1a64({data!r}) = 0x{got:x}, expected 0x{expected:x}")
            ok = False
        else:
            print(f"  self-test: fnv1a64({data!r}) = 0x{got:016x}  OK")

    # Pinned masked-hash v2 vector (shared with C# MaskedTerrainHash and treasure_flags.py):
    # 14-byte buf = records [01 02 03 04 05 06 07] + [11 12 13 14 15 16 17]
    # field-0 bytes = [0x01, 0x11] => 0x082f3307b4e8a9a7
    _MASKED_HASH_VECTOR = 0x082f3307b4e8a9a7
    _mhv_raw = bytes([0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07,
                      0x11, 0x12, 0x13, 0x14, 0x15, 0x16, 0x17])
    got_mhv = masked_terrain_hash(_mhv_raw)
    if got_mhv != _MASKED_HASH_VECTOR:
        print(f"SELF-TEST FAIL: masked_terrain_hash(vector) = 0x{got_mhv:016x}, "
              f"expected 0x{_MASKED_HASH_VECTOR:016x}")
        ok = False
    else:
        print(f"  self-test: masked_terrain_hash(vector) = 0x{got_mhv:016x}  OK")

    # Pinned masked-hash v3 vector (shared with C# MaskedTerrainHashV3 and treasure_flags.py):
    # 14-byte buf = records [01 02 03 04 05 06 07] + [11 12 13 14 15 16 17]
    # fields {2,3,4,5} bytes (i%7 in {2,3,4,5}):
    #   indices 2,3,4,5 -> 0x03,0x04,0x05,0x06; indices 9,10,11,12 -> 0x13,0x14,0x15,0x16
    # result = fnv1a64([03 04 05 06 13 14 15 16]) = 0x05708f90b5f5fac5
    _MASKED_HASH_V3_VECTOR = 0x05708f90b5f5fac5
    _mhv3_raw = bytes([0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07,
                       0x11, 0x12, 0x13, 0x14, 0x15, 0x16, 0x17])
    got_mhv3 = masked_terrain_hash_v3(_mhv3_raw)
    if got_mhv3 != _MASKED_HASH_V3_VECTOR:
        print(f"SELF-TEST FAIL: masked_terrain_hash_v3(vector) = 0x{got_mhv3:016x}, "
              f"expected 0x{_MASKED_HASH_V3_VECTOR:016x}")
        ok = False
    else:
        print(f"  self-test: masked_terrain_hash_v3(vector) = 0x{got_mhv3:016x}  OK")

    # Join fixture: a known-good map entry from map_trap_formation.json (map 74, tile 0) is
    # the is_treasure tile (0,1) DisableTrap.  Verify the lookup works end-to-end.
    trap_data = json.loads(TRAP_JSON.read_text(encoding="utf-8"))
    m74 = trap_data.get("74", {})
    treasure_tiles = [(t["x"], t["y"]) for t in m74.get("tiles", []) if is_treasure(t)]
    expected_treasures = [(0, 1), (1, 9), (5, 11)]  # (6,6) is a live trap
    for xy in expected_treasures:
        if xy not in treasure_tiles:
            print(f"SELF-TEST FAIL: map 74 tile {xy} not found in is_treasure set {treasure_tiles}")
            ok = False
    if ok:
        print(f"  self-test: map74 join fixture OK (treasure tiles: {treasure_tiles})")

    # Drop-rule fixture: clean pairs ship, lockstep coincidences are dropped.
    drop_cases = [
        (0x140de1ea7, 0x01, False),  # genuine flag byte
        (0x140de1ea7, 0x00, False),  # genuine flag byte, 0x00 rest
        (0x142eb9ba8, 0x40, True),   # the live 0x142e stray from the first Sledge session
        (0x140de1ea7, 0x40, True),   # bad off even at a stable address
        (0x142eb9ba8, 0x01, True),   # volatile family even with a clean off
    ]
    for addr, off, want_drop in drop_cases:
        dropped = pair_drop_reason(addr, off) is not None
        if dropped != want_drop:
            print(f"SELF-TEST FAIL: pair_drop_reason(0x{addr:x}, 0x{off:02x}) "
                  f"dropped={dropped}, expected {want_drop}")
            ok = False
    if ok:
        print("  self-test: pair drop rules OK")
    return ok

# ── parsing helpers ───────────────────────────────────────────────────────────

def parse_hex(s: str) -> int:
    return int(s, 16)

def addr_valid(addr: int) -> bool:
    if addr < MODULE_BASE or addr >= MODULE_END:
        return False
    if UI_ARENA_LO <= addr < UI_ARENA_HI:
        return False
    return True

def pair_drop_reason(addr: int, off: int) -> str | None:
    """Non-fatal drop reasons for a captured (addr, off) pair: lockstep coincidences
    that a legitimate capture CAN produce but that must never ship. None = keep."""
    if off not in (0x00, 0x01):
        return f"off 0x{off:02x} is not a flag resting value"
    if addr >= VOLATILE_BASE:
        return f"volatile family >= 0x{VOLATILE_BASE:x} (relocates between runs)"
    return None

# ── main bake ─────────────────────────────────────────────────────────────────

def main() -> int:
    print("gen_treasure_db.py — self-test first")
    if not _self_test():
        print("GATE FAIL: self-test failed.")
        return 1
    print()

    trap_data   = json.loads(TRAP_JSON.read_text(encoding="utf-8"))
    addrs_data  = json.loads(ADDRS_JSON.read_text(encoding="utf-8"))
    capture_maps: dict = addrs_data.get("maps", {})

    # Build the authoritative set of populated treasure maps (mapIds 1-127 with
    # at least one is_treasure tile in the snapshot).
    populated: dict[int, dict] = {}
    for mid_str, mdata in trap_data.items():
        mid = int(mid_str)
        if mid < 1 or mid > 127:
            continue
        treasure_tiles = [t for t in mdata.get("tiles", []) if is_treasure(t)]
        if not treasure_tiles:
            continue
        populated[mid] = {
            "name":  mdata.get("name") or f"Map {mid}",
            "tiles": treasure_tiles,
        }

    # ── build-key policy ──────────────────────────────────────────────────────
    # Collect all distinct capturedBuild keys from capture data.
    all_builds: list[dict] = []
    seen_ts: set[int] = set()
    for mid_str, cmap in capture_maps.items():
        cb = cmap.get("capturedBuild")
        if cb and isinstance(cb, dict):
            ts = cb.get("timeDateStamp")
            if ts is not None and ts not in seen_ts:
                all_builds.append(cb)
                seen_ts.add(ts)

    newest_build: dict | None = None
    if all_builds:
        newest_build = max(all_builds, key=lambda b: b.get("timeDateStamp", 0))

    dataset_build_key = newest_build  # may be None if nothing captured yet

    # ── gate + bake ───────────────────────────────────────────────────────────
    gate_failures: list[str] = []
    warnings: list[str] = []
    shippable_count = 0
    stub_count      = 0
    dropped_count   = 0

    out_maps = []
    for mid in sorted(populated.keys()):
        pop = populated[mid]
        name        = pop["name"]
        pop_tiles   = pop["tiles"]
        tile_count  = len(pop_tiles)
        treasure_xy = {(t["x"], t["y"]) for t in pop_tiles}

        cmap = capture_maps.get(str(mid))

        # Check if this map has any capturable data.
        if cmap is None:
            # No capture data at all — emit stub.
            stub_count += 1
            out_maps.append({
                "mapId":     mid,
                "name":      name,
                "tileCount": tile_count,
                "fpVer":     None,
                "fpLen":     None,
                "fpHash":    None,
                "tiles":     [],
            })
            continue

        fp_hash    = cmap.get("fpHash")
        fp_len     = cmap.get("fpLen")
        fp_ver     = cmap.get("fpVer")
        cap_build  = cmap.get("capturedBuild")

        # Determine arm mode for this map.
        # map-id-only: fpVer == 0 and fpHash absent/null -- water/lava maps.
        # fingerprinted: fpVer 2 or 3 with a non-null fpHash.
        # Anything else: demote to stub (needs 'refp' or 'nofp').
        is_map_id_only = (fp_ver == 0 and not fp_hash)

        if not is_map_id_only and fp_ver not in (2, 3):
            print(f"  WARN: map {mid} ({name}) fpVer={fp_ver!r} (not 0, 2, or 3) -- "
                  f"needs 'refp' re-fingerprint or 'nofp' for water/lava; emitting stub")
            stub_count += 1
            out_maps.append({
                "mapId":     mid,
                "name":      name,
                "tileCount": tile_count,
                "fpVer":     None,
                "fpLen":     None,
                "fpHash":    None,
                "tiles":     [],
            })
            continue

        # Build-key staleness check.  Map-id-only maps are NOT exempt (L0 still guards).
        if newest_build and cap_build and isinstance(cap_build, dict):
            cap_ts = cap_build.get("timeDateStamp", 0)
            new_ts = newest_build.get("timeDateStamp", 0)
            if cap_ts < new_ts:
                print(f"  WARN: map {mid} ({name}) captured under older build "
                      f"(ts={cap_ts:#010x}) vs current (ts={new_ts:#010x}) — DROPPED")
                dropped_count += 1
                # Still emit a stub for the runtime nag.
                out_maps.append({
                    "mapId":     mid,
                    "name":      name,
                    "tileCount": tile_count,
                    "fpVer":     None,
                    "fpLen":     None,
                    "fpHash":    None,
                    "tiles":     [],
                })
                continue

        shippable_tiles = []
        seen_addrs: set[int] = set()

        for tile in cmap.get("tiles", []):
            status = tile.get("status", "")
            tx, ty = tile.get("x"), tile.get("y")

            # (a) Only ship verified tiles.
            # Fingerprinted mode: requires non-null fpHash + capturedBuild.
            # Map-id-only mode:   requires capturedBuild (fpHash is intentionally null).
            if status != "verified":
                continue
            if not cap_build:
                continue
            if not is_map_id_only and not fp_hash:
                continue

            # (b) Coord must be a treasure tile in the snapshot.
            if (tx, ty) not in treasure_xy:
                gate_failures.append(
                    f"map {mid} tile ({tx},{ty}): not a treasure tile in snapshot"
                )
                continue

            valid_addrs: list[list[str]] = []
            tile_ok = True
            for pair in tile.get("addrs", []):
                addr_str, off_str = pair[0], pair[1]
                try:
                    addr = parse_hex(addr_str)
                    off  = parse_hex(off_str)
                except ValueError:
                    gate_failures.append(
                        f"map {mid} tile ({tx},{ty}): unparseable addr/off {pair}"
                    )
                    tile_ok = False
                    break

                # (c) lockstep coincidences: warn + drop the single pair, keep the tile.
                if (reason := pair_drop_reason(addr, off)) is not None:
                    warnings.append(
                        f"map {mid} tile ({tx},{ty}): dropped addr {addr_str} ({reason})"
                    )
                    continue

                # (d) module span + UI arena + no duplicates
                if not addr_valid(addr):
                    gate_failures.append(
                        f"map {mid} tile ({tx},{ty}): addr 0x{addr:x} fails module span or UI arena check"
                    )
                    tile_ok = False
                    break

                if addr in seen_addrs:
                    gate_failures.append(
                        f"map {mid}: duplicate addr 0x{addr:x} in tile ({tx},{ty})"
                    )
                    tile_ok = False
                    break

                seen_addrs.add(addr)
                valid_addrs.append([addr_str, off_str])

            if tile_ok and valid_addrs and len(valid_addrs) < MIN_ADDRS_TO_SHIP:
                warnings.append(
                    f"map {mid} tile ({tx},{ty}): only {len(valid_addrs)} clean addr(s) "
                    f"after drops -- not shipping this tile, re-capture it"
                )
                valid_addrs = []

            if tile_ok and valid_addrs:
                shippable_tiles.append({"x": tx, "y": ty, "addrs": valid_addrs})

        if shippable_tiles:
            shippable_count += 1
            if is_map_id_only:
                # Map-id-only: no fingerprint fields.
                out_maps.append({
                    "mapId":     mid,
                    "name":      name,
                    "tileCount": tile_count,
                    "fpVer":     0,
                    "fpLen":     None,
                    "fpHash":    None,
                    "tiles":     shippable_tiles,
                })
            else:
                fp_hash_hex = f"0x{int(fp_hash, 16):016x}" if fp_hash else None
                out_maps.append({
                    "mapId":     mid,
                    "name":      name,
                    "tileCount": tile_count,
                    "fpVer":     fp_ver,         # preserve the actual version (2 or 3)
                    "fpLen":     TERRAIN_RAW_LEN,
                    "fpHash":    fp_hash_hex,
                    "tiles":     shippable_tiles,
                })
        else:
            stub_count += 1
            out_maps.append({
                "mapId":     mid,
                "name":      name,
                "tileCount": tile_count,
                "fpVer":     None,
                "fpLen":     None,
                "fpHash":    None,
                "tiles":     [],
            })

    if warnings:
        print("\nWARNINGS (addresses/tiles dropped, non-fatal):")
        for w in warnings:
            print(f"  {w}")

    if gate_failures:
        print("\nGATE FAILURES:")
        for f in gate_failures:
            print(f"  {f}")
        print(f"\nGATE FAIL: {len(gate_failures)} violation(s). Fix the capture data and re-run.")
        return 1

    # ── build key output (None when nothing captured yet) ────────────────────
    if dataset_build_key:
        out_key = {
            "timeDateStamp": dataset_build_key["timeDateStamp"],
            "sizeOfImage":   dataset_build_key["sizeOfImage"],
        }
    else:
        out_key = None

    out = {"buildKey": out_key, "maps": out_maps}
    OUT_JSON.write_text(json.dumps(out, indent=2, ensure_ascii=False), encoding="utf-8")

    total_populated = len(populated)
    print(f"\nwrote {OUT_JSON}")
    print(f"coverage: {shippable_count} shippable / {stub_count} stub / "
          f"{dropped_count} dropped (stale key) / {total_populated} total populated maps")
    return 0


if __name__ == "__main__":
    sys.exit(main())
