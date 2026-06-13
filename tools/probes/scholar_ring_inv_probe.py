"""Read or set the Scholar's Ring (id 260) INVENTORY count, to test the auto-grant.

The runtime (ScholarRing.Grant) tops the inventory up to 1 whenever it reads 0, out of
battle. count[id] is a u8 at InventoryCountBase (0x1411A17C0) + id.

Usage:
  python tools\\probes\\scholar_ring_inv_probe.py        -> read the current count
  python tools\\probes\\scholar_ring_inv_probe.py 0      -> set count to 0 (then go to the
                                                            world map to watch it re-grant)
"""
import pathlib
import sys

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
from treasure_flags import ru8, wpm, _require_game

ADDR = 0x1411A17C0 + 260   # InventoryCountBase + ScholarRingItemId


def main():
    _require_game()
    if len(sys.argv) > 1:
        n = int(sys.argv[1]) & 0xFF
        ok = wpm(ADDR, bytes([n]))
        print(f"set count[260] @ {ADDR:#x} = {n}  ({'ok' if ok else 'WRITE FAILED'})")
    cur = ru8(ADDR)
    print(f"Scholar's Ring inventory count[260] = {cur}")
    if cur == 0:
        print("  -> go to the world map (out of battle) ~1s; the runtime should grant 1.")


if __name__ == "__main__":
    main()
