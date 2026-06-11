#!/usr/bin/env python
"""Fold docs/living_weapon_rods.csv back into docs/living_weapon_grid.csv:
replace the grid's rows with ids 51-58 by the rods sheet's data rows (in id order,
at the position of the grid's first rod row). Everything else round-trips untouched."""
import io

GRID = r"C:\Users\ptyRa\Dev\FFTItemOverhaul\docs\living_weapon_grid.csv"
RODS = r"C:\Users\ptyRa\Dev\FFTItemOverhaul\docs\living_weapon_rods.csv"
ROD_IDS = {str(i) for i in range(51, 59)}


def rid(line):
    head = line.split(",", 1)[0]
    return head if head in ROD_IDS else None


with io.open(RODS, encoding="utf-8") as f:
    rod_rows = [ln for ln in f.read().splitlines() if ln.strip() and rid(ln)]
assert len(rod_rows) == 8, f"expected 8 rod rows, got {len(rod_rows)}"

with io.open(GRID, encoding="utf-8") as f:
    grid = f.read().splitlines()

out, inserted = [], False
for ln in grid:
    if rid(ln):
        if not inserted:
            out.extend(rod_rows)
            inserted = True
        continue
    out.append(ln)
assert inserted, "no rod rows found in the grid"

with io.open(GRID, "w", encoding="utf-8", newline="\n") as f:
    f.write("\n".join(out) + "\n")
print(f"merged {len(rod_rows)} rod rows into the grid ({len(out)} lines total)")
