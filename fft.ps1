# Battle-cheat helpers (PowerShell twin of fft.sh).  Dot-source, then call directly:
#
#   . .\fft.ps1
#   give_move          # grant Master Teleportation to the hovered unit
#   give_move 242      # grant plain Teleport instead
#   kill_all           # KO all enemies in the current battle
#
# These shell into tools/probes/battle_cheats.py.  The game must be running.
# See docs/DEV_TEST_RECIPES.md for full usage notes.

$script:FftRepo = $PSScriptRoot

function give_move([int]$ability = 243) {
    python (Join-Path $script:FftRepo "tools\probes\battle_cheats.py") give_move $ability
}

function kill_all {
    python (Join-Path $script:FftRepo "tools\probes\battle_cheats.py") kill_all
}

# Treasure capture campaign (treasure_flags.py wrappers)
function treasure {
    python (Join-Path $script:FftRepo "tools\probes\treasure_flags.py") session
}

function tstatus {
    python (Join-Path $script:FftRepo "tools\probes\treasure_flags.py") status
}

function tmapid {
    python (Join-Path $script:FftRepo "tools\probes\treasure_flags.py") mapid
}

function trefp([int]$mapId) {
    python (Join-Path $script:FftRepo "tools\probes\treasure_flags.py") refp $mapId
}

# Water/lava maps: convert to map-id-only mode (no terrain fingerprint).
function tnofp([int]$mapId) {
    python (Join-Path $script:FftRepo "tools\probes\treasure_flags.py") nofp $mapId
}
