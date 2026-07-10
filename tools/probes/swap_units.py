"""Two-unit POSITION SWAP / single-unit TELEPORT (2026-07-10, LW-65; proven live same day:
a Ramza-with-enemy swap executed flawlessly, both units hovered and acted normally after).

The complete teleport recipe = write all THREE position layers coherently, then the engine
re-adopts every layer after the unit's first real move:
  combat +0x4F/+0x50 (logic tile; +0x51 bit7 = upper-layer, low nibble = facing, kept per unit)
  node   +0x88/+0x89/+0x8A (the AI decision pipeline's tile-lookup key)
  node   +0x4C/+0x4E/+0x50 u16 (render world X/Z/Y; X=28x+14, Y=28y+14, Z=-12*(height, +1 if
  the unit has FLOAT: the hover offset is pure node data, grantable/strippable by Z alone))
The +0x2E u16 rides along (per-tile, meaning unidentified). TRAPS: never leave two units
co-tiled (slot-order target shadowing + a movement soft-lock, both proven live); swap avoids
that by construction. THROWAWAY SAVE; interactive y/n confirm; guarded writes.

    python tools\\probes\\swap_units.py <slotA> <slotB>    # swap two units' positions
    python tools\\probes\\swap_units.py table              # position/address table (no writes)
"""
import pathlib
import struct
import sys

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
from battle_cheats import rpm, ru8, ru16, wu8, wu16, _require_game

UNITS = 0x141853CE0
HEAD = 0x140D3A410

def u64(a):
    b = rpm(a, 8)
    return None if b is None else struct.unpack("<Q", b)[0]

def node_of(slot):
    cur = u64(HEAD)
    for _ in range(64):
        if not cur:
            return None
        if u64(cur + 0x148) == UNITS + slot * 0x200:
            return cur
        cur = u64(cur)
    return None

def snap(slot, node):
    c = UNITS + slot * 0x200
    return dict(gx=ru8(c + 0x4F), gy=ru8(c + 0x50), b51=ru8(c + 0x51),
                tx=ru8(node + 0x88), ty=ru8(node + 0x89), tl=ru8(node + 0x8A),
                wx=ru16(node + 0x4C), wz=ru16(node + 0x4E), wy=ru16(node + 0x50),
                h2e=ru16(node + 0x2E))

def put(slot, node, src, own51):
    c = UNITS + slot * 0x200
    wu8(c + 0x4F, src["gx"]); wu8(c + 0x50, src["gy"])
    wu8(c + 0x51, (own51 & 0x7F) | (src["b51"] & 0x80))   # own facing, source layer bit
    wu8(node + 0x88, src["tx"]); wu8(node + 0x89, src["ty"]); wu8(node + 0x8A, src["tl"])
    wu16(node + 0x4C, src["wx"]); wu16(node + 0x4E, src["wz"]); wu16(node + 0x50, src["wy"])
    wu16(node + 0x2E, src["h2e"])

def cmd_table():
    print(f"{'slot':>4} {'tile':>9} {'combat X/Y addr':>26} {'node':>12} {'world (x,z,y)':>16}")
    cur = u64(HEAD)
    for _ in range(64):
        if not cur:
            break
        c = u64(cur + 0x148)
        off = (c or 0) - UNITS
        if c and 0 <= off < 21 * 0x200 and off % 0x200 == 0:
            s = off // 0x200
            wz = ru16(cur + 0x4E)
            wz = wz - 65536 if wz > 32767 else wz
            print(f"{s:>4} ({ru8(cur + 0x88):>2},{ru8(cur + 0x89):>2})  "
                  f"0x{c + 0x4F:X}/0x{c + 0x50:X} 0x{cur:>10X} "
                  f"({ru16(cur + 0x4C)},{wz},{ru16(cur + 0x50)})")
        cur = u64(cur)

def cmd_swap(a_slot, b_slot):
    na, nb = node_of(a_slot), node_of(b_slot)
    if not na or not nb:
        print("both units must be alive and noded; aborting.")
        sys.exit(1)
    A, B = snap(a_slot, na), snap(b_slot, nb)
    for name, s, d in (("A", a_slot, A), ("B", b_slot, B)):
        print(f"{name} slot {s}: tile({d['gx']},{d['gy']}) node({d['tx']},{d['ty']},{d['tl']}) "
              f"world({d['wx']},{d['wy']})")
    if (A["gx"], A["gy"]) != (A["tx"], A["ty"]) or (B["gx"], B["gy"]) != (B["tx"], B["ty"]):
        print("layers disagree pre-swap (unit mid-move?); refusing.")
        sys.exit(1)
    if input(f"SWAP slots {a_slot} and {b_slot}? (y/n) ").strip().lower() != "y":
        print("aborted.")
        return
    put(a_slot, na, B, A["b51"])
    put(b_slot, nb, A, B["b51"])
    A2, B2 = snap(a_slot, na), snap(b_slot, nb)
    print(f"A slot {a_slot} -> tile({A2['gx']},{A2['gy']}) world({A2['wx']},{A2['wy']})")
    print(f"B slot {b_slot} -> tile({B2['gx']},{B2['gy']}) world({B2['wx']},{B2['wy']})")
    print("SWAP COMPLETE; eyeball the field.")

def main():
    _require_game()
    if len(sys.argv) >= 2 and sys.argv[1] == "table":
        cmd_table()
    elif len(sys.argv) >= 3:
        cmd_swap(int(sys.argv[1]), int(sys.argv[2]))
    else:
        print(__doc__)

if __name__ == "__main__":
    main()
