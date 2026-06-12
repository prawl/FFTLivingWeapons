#!/bin/bash
# Battle-cheat helpers.  Source this file, then call the functions directly.
#
#   source ./fft.sh
#   give_move          # grant Master Teleportation to hovered unit
#   give_move 242      # grant plain Teleport instead
#   kill_all           # KO all enemies in the current battle
#
# These shell into tools/probes/battle_cheats.py.  The game must be running.
# See docs/DEV_TEST_RECIPES.md for full usage notes.

_FFT_REPO="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

give_move() {
  local ability="${1:-243}"
  python "$_FFT_REPO/tools/probes/battle_cheats.py" give_move "$ability"
}

kill_all() {
  python "$_FFT_REPO/tools/probes/battle_cheats.py" kill_all
}

# Treasure capture campaign (treasure_flags.py wrappers)
treasure() {
  python "$_FFT_REPO/tools/probes/treasure_flags.py" session
}

tstatus() {
  python "$_FFT_REPO/tools/probes/treasure_flags.py" status
}

tmapid() {
  python "$_FFT_REPO/tools/probes/treasure_flags.py" mapid
}

trefp() {
  python "$_FFT_REPO/tools/probes/treasure_flags.py" refp "$1"
}

# Water/lava maps: convert to map-id-only mode (no terrain fingerprint).
tnofp() {
  python "$_FFT_REPO/tools/probes/treasure_flags.py" nofp "$1"
}

# Hold the party at full HP so a boss alpha-strike can't kill them. Start BEFORE the battle.
godmode() {
  python "$_FFT_REPO/tools/probes/battle_cheats.py" godmode "${1:-999}"
}

revive() {
  python "$_FFT_REPO/tools/probes/battle_cheats.py" revive
}
