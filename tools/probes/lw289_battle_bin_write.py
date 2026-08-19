#!/usr/bin/env python
"""LW-289 round 4: can a mod ASSIGN a weapon's battle palette by shipping its own BATTLE.BIN?

Everything proven so far is the READ half. We know which palette each weapon draws from
(lw289_battle_bin_palette_map.py: the X nibble of the two-byte item-graphics record at
0x02D3E6 + (id-1)*2 in BATTLE.BIN, four for four against owner-measured live captures, fifteen
for fifteen against the published PSX table). Nothing yet shows we can CHANGE it, and the whole
feature rests on that: 13 palettes serve 127 weapons in vanilla, so an icon-matched recolour is
only possible if the allocation is ours to redraw.

WHY IT SHOULD WORK, AND WHY THAT IS NOT ENOUGH. battle_bin.bin is FFTPack file 0. The loader's
hook excludes only file 17 and files 741-749 from mod override, so index 0 is servable, and the
game demonstrably requests it once per battle load at offset 0 (four requests in the round-3
launch log, one per battle). That is an argument, not evidence. The same shape of argument was
wrong about g2d entry 156 and cost a retracted PROVEN row.

THE EXPERIMENT. The census sheet from lw289_palette_selector.py must already be deployed, so all
sixteen palettes wear distinct labelling colours and every weapon announces its palette when
swung. Then we rewrite four nibbles and swing the same four swords.

  item 19 Broadsword     X 14 -> 8   blade should turn CYAN
                         Y  0 -> 2   its slash ARC should turn AMBER instead of red
  item 26 Sleep Blade    X 15 -> 5   blade should turn GREEN
                         Y  0         unchanged, so its arc must STAY RED. Within-item control:
                                      it separates "we moved the weapon nibble" from "we moved
                                      something that repaints the whole item".
  item 21 Iron Sword     untouched    must stay YELLOW with a RED arc
  item 22 Mythril Sword  untouched    must stay ROSE with a RED arc

THE CONTROL THAT MAKES THIS WORTH RUNNING. Items 22 and 26 BOTH sit on palette 15 today. If 26
goes green while 22 stays rose, that is per-ITEM palette assignment, not a palette-wide repaint,
and it is exactly the capability the feature needs. If both change together we have merely
recoloured a shared palette and learned nothing new.

PRE-REGISTERED READINGS
  26 green AND 22 still rose            -> per-item assignment WORKS. LW-289 is unblocked and the
                                           bake can allocate palettes freely.
  26 and 22 BOTH green                  -> we did not write per item; something palette-wide
                                           moved. Re-examine before building anything.
  nothing moves, log says modded file 0 -> BATTLE.BIN is served but the item-graphics block is
                                           not what the enhanced renderer reads. The read map
                                           still stands; only the write lever dies.
  nothing moves, log says plain file 0  -> not served. Conclude NOTHING. Fix the deploy path.
  arcs change but blades do not (or the reverse) -> the nibbles are split differently from the
                                           FFHacktics documentation. Report which half moved.

USAGE:
  python lw289_battle_bin_write.py --plan                 # owner-facing script, touches nothing
  python lw289_battle_bin_write.py --selftest             # pure checks, no game files
  python lw289_battle_bin_write.py <work_dir>             # extract, gate, forge, diff-verify
  python lw289_battle_bin_write.py <work_dir> --deploy    # ...and install
  python lw289_battle_bin_write.py --checklog             # was file 0 served from OUR copy?
  python lw289_battle_bin_write.py --undo                 # remove the deployed battle_bin.bin

Undo also happens on the next BuildLinked, which wipes the whole fftpack folder, so deploy probe
files AFTER a build and never before.
"""
import os
import re
import shutil
import subprocess
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
# Single-source the offsets, the anchor gate and the extract from the map probe, so the two can
# never disagree about where a record lives.
from lw289_battle_bin_palette_map import (  # noqa: E402
    FFTPACK_ID, LIVE_MEASURED, extract, gate, record_offset,
)

MODS_ENV = "RELOADEDIIMODS"
DEPLOY_SUB = os.path.join("prawl.fft.livingweapons", "FFTIVC", "data", "enhanced", "fftpack")
NAME = "battle_bin.bin"
LOGS = os.path.join(os.environ.get("APPDATA", ""), "Reloaded-Mod-Loader-II", "Logs")

# The census sheet's label colours after the game's measured overbright pass of about 1.232,
# quoted here only so --plan can tell the owner what to look for.
LABEL = {0: "RED (255,0,0)", 2: "AMBER (255,233,0)", 3: "YELLOW (255,255,0)",
         5: "GREEN (41,255,0)", 8: "CYAN (0,255,255)", 14: "ROSE-MAGENTA (255,0,228)",
         15: "ROSE (255,0,122)"}

# item id -> (new weapon palette X or None to leave, new effect palette Y or None to leave)
TREATMENT = {
    19: (8, 2),      # blade 14 -> 8 cyan, arc 0 -> 2 amber
    26: (5, None),   # blade 15 -> 5 green, arc left at 0 so it must stay red
}
CONTROLS = {21: "must stay YELLOW blade / RED arc",
            22: "must stay ROSE blade / RED arc, and it shares palette 15 with item 26 today"}

# Owner-facing output must use the names the player actually sees in game. The modloader's
# template dump carries VANILLA names, and this mod renames most weapons, so a plan printed from
# the template sends the owner hunting for an item that does not exist in his inventory.
# Sourced from data/items.json, the single hand-edited item source.
MOD_NAMES = {19: "Vagabond", 21: "Riposte", 22: "Claymore", 26: "Flamberge"}
VANILLA_NAMES = {19: "Broadsword", 21: "Iron Sword", 22: "Mythril Sword", 26: "Sleep Blade"}


def label_for(iid):
    return f"{MOD_NAMES.get(iid, '?')} (vanilla {VANILLA_NAMES.get(iid, '?')})"


def forge(raw):
    """Rewrite only the nibbles named in TREATMENT. Returns the new bytes and the exact diff, so
    the caller can prove nothing else moved."""
    buf = bytearray(raw)
    diff = []
    for iid, (newx, newy) in sorted(TREATMENT.items()):
        off = record_offset(iid)
        old = buf[off]
        x = old >> 4 if newx is None else newx
        y = old & 0xF if newy is None else newy
        if not (0 <= x <= 15 and 0 <= y <= 15):
            sys.exit(f"item {iid}: palette nibble out of range")
        buf[off] = (x << 4) | y
        if buf[off] != old:
            diff.append((off, old, buf[off], iid))
    return bytes(buf), diff


def verify(vanilla, forged, diff):
    """A byte-level diff gate. Anything changed that we did not name is a bug, and shipping a
    1.4 MB file with a stray edit in it is exactly how you poison an experiment."""
    if len(vanilla) != len(forged):
        sys.exit(f"length changed: {len(vanilla)} -> {len(forged)}")
    moved = [i for i in range(len(vanilla)) if vanilla[i] != forged[i]]
    named = {d[0] for d in diff}
    stray = [i for i in moved if i not in named]
    if stray:
        sys.exit(f"{len(stray)} byte(s) changed that we did not name, first at 0x{stray[0]:X}")
    print(f"diff gate: exactly {len(moved)} byte(s) changed, all of them named")
    for off, old, new, iid in diff:
        print(f"   item {iid:3} @0x{off:06X}: {old:02X} -> {new:02X}   "
              f"X {old >> 4:2} -> {new >> 4:2}   Y {old & 0xF} -> {new & 0xF}")


def plan():
    print("LW-289 round 4: does shipping our own BATTLE.BIN reassign a weapon's palette?\n")
    print("PRECONDITION: the census sheet must still be deployed, so every palette wears a")
    print("distinct label colour. Run  lw289_palette_selector.py --checklog  if unsure; it should")
    print("report reads served from OUR override.\n")
    print("Same four swords, DAYLIGHT battle, one attack each. Retry-from-formation between")
    print("swings is fine; three rounds of it have shown the assignment is stable per item.\n")
    for iid, (nx, ny) in sorted(TREATMENT.items()):
        was = LIVE_MEASURED.get(iid)
        print(f"  {label_for(iid)}: blade {was} -> {nx}, expect {LABEL.get(nx, nx)}")
        if ny is not None:
            print(f"           arc   0 -> {ny}, expect {LABEL.get(ny, ny)} instead of red")
        else:
            print(f"           arc   unchanged, must STAY {LABEL[0]}")
    for iid, why in sorted(CONTROLS.items()):
        print(f"  {label_for(iid)}: UNTOUCHED CONTROL, {why}")
    print()
    print(f"THE ONE THAT MATTERS: {MOD_NAMES[22]} and {MOD_NAMES[26]} share palette 15 right")
    print(f"now. {MOD_NAMES[26]} going green while {MOD_NAMES[22]} stays rose is per-item")
    print("assignment, which is the whole feature. Both going green")
    print("means we only repainted a shared palette and have proven nothing new.")
    print()
    print("Then run --checklog. If the game did not read OUR battle_bin, the pictures are void.")


def checklog():
    if not os.path.isdir(LOGS):
        sys.exit(f"no Reloaded logs under {LOGS}")
    logs = [os.path.join(LOGS, f) for f in os.listdir(LOGS) if f.endswith(".txt")]
    log = max(logs, key=os.path.getmtime)
    print(f"log: {os.path.basename(log)}")
    modded = plain = 0
    times = []
    for line in open(log, encoding="utf-8", errors="ignore"):
        if "Accessing" not in line or f"file {FFTPACK_ID} -> {NAME}" not in line:
            continue
        m = re.match(r"\[(\d\d:\d\d:\d\d)\]", line)
        times.append(m.group(1) if m else "??")
        if "modded file" in line:
            modded += 1
        else:
            plain += 1
    print(f"  battle_bin served from OUR override : {modded}")
    print(f"  battle_bin served from the GAME copy: {plain}")
    print(f"  read at                             : {', '.join(times) or '(never)'}")
    if modded and not plain:
        print("LIVE: the game read our BATTLE.BIN. An unchanged weapon is now a REAL negative.")
    elif plain:
        print("NOT SERVED (or only partly): conclude NOTHING about the write lever.")
    else:
        print("NOT READ: no battle was loaded this launch.")


def selftest():
    fake = bytearray(0x40000)
    for iid in list(TREATMENT) + list(CONTROLS):
        fake[record_offset(iid)] = 0xF0
        fake[record_offset(iid) + 1] = 0x42
    out, diff = forge(bytes(fake))
    assert len(out) == len(fake), "forge changed the file length"
    changed = {i for i in range(len(fake)) if fake[i] != out[i]}
    assert changed == {d[0] for d in diff}, "diff list does not match the real byte changes"
    for iid, (nx, ny) in TREATMENT.items():
        off = record_offset(iid)
        assert out[off] >> 4 == (nx if nx is not None else 0xF), f"item {iid} X not applied"
        assert out[off] & 0xF == (ny if ny is not None else 0x0), f"item {iid} Y not applied"
        assert out[off + 1] == 0x42, f"item {iid} graphic byte ZZ was touched, it must not be"
    for iid in CONTROLS:
        off = record_offset(iid)
        assert out[off] == 0xF0 and out[off + 1] == 0x42, f"control item {iid} was modified"
    # The deciding pair must actually end up on different palettes, or the round proves nothing.
    assert 22 in CONTROLS and 26 in TREATMENT, "the shared-palette control pair is not set up"
    assert LIVE_MEASURED[22] == LIVE_MEASURED[26], \
        "items 22 and 26 no longer share a palette; pick a new control pair"
    assert TREATMENT[26][0] != LIVE_MEASURED[22], \
        "item 26 is being moved onto item 22's palette, which makes the control unreadable"
    # And the labels have to be tellable apart on screen.
    for a, b in ((TREATMENT[19][0], TREATMENT[26][0]), (TREATMENT[26][0], LIVE_MEASURED[22])):
        assert a != b, f"palettes {a} and {b} collide in this plan"
    for iid in list(TREATMENT) + list(CONTROLS):
        assert iid in MOD_NAMES and iid in VANILLA_NAMES,             f"item {iid} has no in-game name; the owner-facing plan would print a vanilla name"
    print("selftest OK")


def deploy(work_dir):
    if "fft_enhanced.exe" in subprocess.run(
            ["tasklist", "/FI", "IMAGENAME eq fft_enhanced.exe"],
            capture_output=True, text=True).stdout:
        sys.exit("fft_enhanced.exe is RUNNING; close it and rerun")
    mods = os.environ.get(MODS_ENV) or sys.exit(f"{MODS_ENV} not set")
    dst = os.path.join(mods, DEPLOY_SUB)
    os.makedirs(dst, exist_ok=True)
    shutil.copy2(os.path.join(work_dir, NAME), os.path.join(dst, NAME))
    print(f"deployed {NAME} -> {dst}")
    sheet = os.path.join(dst, "unit", "battle_wep_spr.bin")
    print(f"census sheet still in place: {os.path.isfile(sheet)}   <- must be True or the labels "
          f"are gone and the round is unreadable")


def undo():
    mods = os.environ.get(MODS_ENV) or sys.exit(f"{MODS_ENV} not set")
    p = os.path.join(mods, DEPLOY_SUB, NAME)
    if os.path.isfile(p):
        os.remove(p)
        print(f"removed {p}")
    else:
        print(f"nothing to remove at {p}")


def main():
    if "--plan" in sys.argv:
        plan()
        return
    if "--selftest" in sys.argv:
        selftest()
        return
    if "--checklog" in sys.argv:
        checklog()
        return
    if "--undo" in sys.argv:
        undo()
        return
    selftest()
    if len(sys.argv) < 2 or sys.argv[1].startswith("--"):
        sys.exit(__doc__.split("USAGE:")[1])
    work_dir = sys.argv[1]
    os.makedirs(work_dir, exist_ok=True)
    raw = extract(work_dir)
    print(f"battle_bin: {len(raw)} bytes")
    gate(raw)
    out, diff = forge(raw)
    verify(raw, out, diff)
    open(os.path.join(work_dir, NAME), "wb").write(out)
    print(f"wrote {os.path.join(work_dir, NAME)}")
    if "--deploy" not in sys.argv:
        print("dry run only; rerun with --deploy to install")
        return
    deploy(work_dir)


if __name__ == "__main__":
    main()
