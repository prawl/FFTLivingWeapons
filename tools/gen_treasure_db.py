"""Bake data/treasure_addrs.json + data/map_trap_formation.json -> LivingWeapon/treasure.json.

Gate rules (exit 1 on violation):
  (a) Only ship addresses for tiles with status "verified" AND non-null fpHash + capturedBuild.
  (b) Every shipped (x,y) must be a treasure tile (is_treasure) in the snapshot.
  (c) Off bytes must be 0x00 or 0x01.
  (d) Addrs must be in the module span 0x140000000..0x143000000 (exclusive high) and outside
      the UI render arena 0x140C63000..0x140CC5000 (exclusive high). No duplicates within a map.
  (e) Build-key policy: dataset key = newest capturedBuild timeDateStamp. Maps captured under an
      older key are warned and dropped (never hard-fail -- would brick deploys post-patch).
  (f) Emit stub entries {mapId, name, tileCount, tiles:[]} for every populated treasure map with
      no shippable tiles (runtime nag needs names/counts).
  (g) Self-test on every invocation: 3 pinned FNV-1a64 vectors + join fixture.  Exit 1 on fail.
  (h) Print coverage summary: shippable maps / stub maps / dropped.

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
                "fpLen":     None,
                "fpHash":    None,
                "tiles":     [],
            })
            continue

        fp_hash    = cmap.get("fpHash")
        fp_len     = cmap.get("fpLen")
        cap_build  = cmap.get("capturedBuild")

        # Build-key staleness check.
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

            # (a) Only ship verified tiles with a non-null fpHash + capturedBuild.
            if status != "verified":
                continue
            if not fp_hash or not cap_build:
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

                # (c) off must be 0x00 or 0x01
                if off not in (0x00, 0x01):
                    gate_failures.append(
                        f"map {mid} tile ({tx},{ty}): off byte 0x{off:02x} not 0x00 or 0x01"
                    )
                    tile_ok = False
                    break

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

            if tile_ok and valid_addrs:
                shippable_tiles.append({"x": tx, "y": ty, "addrs": valid_addrs})

        if shippable_tiles:
            shippable_count += 1
            fp_hash_hex = f"0x{int(fp_hash, 16):016x}" if fp_hash else None
            out_maps.append({
                "mapId":     mid,
                "name":      name,
                "tileCount": tile_count,
                "fpLen":     fp_len,
                "fpHash":    fp_hash_hex,
                "tiles":     shippable_tiles,
            })
        else:
            stub_count += 1
            out_maps.append({
                "mapId":     mid,
                "name":      name,
                "tileCount": tile_count,
                "fpLen":     None,
                "fpHash":    None,
                "tiles":     [],
            })

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
