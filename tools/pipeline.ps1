# tools/pipeline.ps1 - the shared pipeline prefix for BuildLinked.ps1 (dev deploy)
# and Publish.ps1 (release zip). Dot-source it; everything here lands in the
# caller's scope.
#
# Both scripts used to carry their own copy of generate -> gate -> meta -> test
# -> dotnet publish, and the copies drifted: different step order, different
# dotnet test flags, and a python-missing soft-skip in Publish that packaged an
# ungated tree with a stale meta.json. One copy, two callers, no drift.
#
# Step order is load-bearing: gen_living_weapon_meta.py must run BEFORE the unit
# tests (tests read LivingWeapon/meta.json), so call Invoke-TablePipeline first
# and Invoke-UnitTestGate second.

# Repo root, resolved from this file's own location so everything works no
# matter what cwd the caller happens to be in when it dot-sources us.
$PipelineRepoRoot = Split-Path -Parent $PSScriptRoot

# Required-file manifest shared by BuildLinked's deploy verification and
# Publish's Verify-Package: the mod manifest, the Living Weapon runtime (DLL +
# LivingWeapon.deps.json for the Reloaded loader + Newtonsoft + baked meta),
# the 7 sparse table XMLs, and the two full-table nxds. ModConfig.json declares
# "ModDll": "LivingWeapon.dll", so the DLL is non-optional -- shipping the
# manifest without it is the bug the verifiers exist to catch. Paths are
# forward-slash relative to the mod root (zip-entry style); Test-Path and
# Join-Path both take them as-is.
#
# LW-77 (2026-07-14): JobCommandData.xml is GONE, not merely trimmed. A table-XML
# row applies as a WHOLE-ROW writeback at OnAllModsLoaded, so shipping this table
# clobbered other job mods' post-snapshot runtime edits to the same 47 records;
# its sole payload (zeroing the dead-JP Equip Axes RSM slot) now ships as one
# ability.en.nxd Description cell on key 460 instead (tools/patch_ability_names.py).
$RequiredModFiles = @(
    "ModConfig.json",
    "LivingWeapon.dll",
    "LivingWeapon.deps.json",
    "Newtonsoft.Json.dll",
    "meta.json",
    "treasure.json",
    "FFTIVC/tables/enhanced/ItemData.xml",
    "FFTIVC/tables/enhanced/ItemWeaponData.xml",
    "FFTIVC/tables/enhanced/ItemArmorData.xml",
    "FFTIVC/tables/enhanced/ItemShieldData.xml",
    "FFTIVC/tables/enhanced/ItemAccessoryData.xml",
    "FFTIVC/tables/enhanced/ItemEquipBonusData.xml",
    "FFTIVC/tables/enhanced/JobData.xml",
    "FFTIVC/data/enhanced/nxd/item.en.nxd",
    "FFTIVC/data/enhanced/nxd/ability.en.nxd",
    # LW-123: names the blank status Provoke marks its target with. Without this the mark
    # renders as an unlabelled icon. Built by tools/patch_status_names.py.
    "FFTIVC/data/enhanced/nxd/uistatuseffect.en.nxd"
)

# Save-adjacent files a deploy must round-trip through %TEMP% rather than wipe: the
# player's kill tally, the Reliquary deed ledger, and the Gun Slinger holdings snapshot
# (plus each file's .bak -- KillTally/GunSlingerStore-style saves always produce one).
# PowerShell's Remove-Item -Exclude against -Recurse is NOT reliable protection -- it
# silently wiped the flight/ archive directory despite being excluded (1bd87a1) -- so
# BuildLinked.ps1 backs every entry in this list up into ONE named temp directory before
# cleaning $dest and restores them after staging (decision 5, docs/RELIQUARY_AC.md persist
# section). flight/ is a directory, not a file, so it isn't listed here; BuildLinked
# copies it through the same temp dir alongside this list (one named mechanism, not two).
$PreservedSaveFiles = @(
    "kills.json",
    "kills.json.bak",
    "legends.json",
    "legends.json.bak",
    "gunslinger.json",
    "gunslinger.json.bak"
)

# LW-28: the post-restore existence check's pure core. A deploy once LOST preserved files
# despite the round-trip printing success (the 17:54 launch found no kill tally; intermittent,
# cause still unfound), so BuildLinked now compares what the backup dir HOLDS against what the
# destination HAS after the restore and fails the deploy red on any loss. Pure over its three
# inputs so it is testable without a deploy; "flight" stands in for the flight/ archive
# directory, which rides the same temp dir as the file list. Callers wrap the result in @()
# (PowerShell unwraps an empty array to $null across function returns).
function Get-LostPreservedItems([string]$preserveDir, [string]$dest, [string[]]$files) {
    $lost = @()
    foreach ($f in $files) {
        if ((Test-Path (Join-Path $preserveDir $f)) -and -not (Test-Path (Join-Path $dest $f))) { $lost += $f }
    }
    if ((Test-Path (Join-Path $preserveDir "flight")) -and -not (Test-Path (Join-Path $dest "flight"))) { $lost += "flight" }
    return $lost
}

# LW-149 Stage F: the preserve round-trip loop itself (copy each $PreservedSaveFiles entry plus
# the flight/ archive directory, the same one-mechanism treatment Get-LostPreservedItems above
# already checks). BuildLinked.ps1 used to carry this loop three times: the pre-clean backup
# ($dest -> $preserveDir, unconditional overwrite, silent), the post-stage restore ($preserveDir
# -> $dest, unconditional overwrite, silent), and the catch path's re-restore, which is the one
# copy that differs in shape -- it only fills in items still MISSING at the destination (so it
# never clobbers a partial restore an earlier stage already completed) and narrates every file it
# saves, because that narration is the only evidence a failed-deploy session leaves about what
# survived. -OnlyIfMissing reproduces exactly that guard + narration (the host lines are
# byte-identical to the catch path's old inline copies, BuildLinked.ps1 pre-LW-149 lines
# 214-228); omit it for the unconditional backup/restore legs, which stay silent since their own
# [3/5]-style step header already announces the copy.
function Copy-PreservedItems([string]$from, [string]$to, [string[]]$files, [switch]$OnlyIfMissing) {
    foreach ($f in $files) {
        $src = Join-Path $from $f
        $dst = Join-Path $to $f
        if (-not (Test-Path $src)) { continue }
        if ($OnlyIfMissing -and (Test-Path $dst)) { continue }
        Copy-Item $src $dst -Force
        if ($OnlyIfMissing) { Write-Host "Restored $f after the failed deploy." -ForegroundColor Yellow }
    }
    $flightSrc = Join-Path $from "flight"
    $flightDst = Join-Path $to "flight"
    if ((Test-Path $flightSrc) -and -not ($OnlyIfMissing -and (Test-Path $flightDst))) {
        Copy-Item $flightSrc $to -Recurse -Force
        if ($OnlyIfMissing) { Write-Host "Restored flight/ after the failed deploy." -ForegroundColor Yellow }
    }
}

# LW-51 / LW-134 (LW-148 extraction): the update-safe save directory's location, shared by
# BuildLinked.ps1's deploy guard and (via the DeployGuardTests-style cross-language pin) a C# test
# asserting it resolves to the exact same path as LivingWeapon/SaveLocation.cs's own
# ResolveSaveDir. modsDir is the Mods folder itself (one level ABOVE the deployed mod dir, e.g.
# "<root>\Reloaded\Mods"), matching the C# side's own two-levels-up walk from the deployed mod dir
# (Directory.GetParent(modDir)?.Parent). Pure over its two string inputs so it is testable without
# a deploy.
function Resolve-SaveDir([string]$modsDir, [string]$modId) {
    $reloadedRoot = Split-Path $modsDir -Parent
    return Join-Path $reloadedRoot "User\Mods\$modId"
}

# LW-134 (2026-07-25 near-miss): teaches the deploy guard where the player's kill counts
# actually live now. A DEV build pre-seeds every weapon's tally (LWDEV), so deploying a dev build
# over a real player's install would wipe their real progress -- the guard's whole job is to catch
# that BEFORE it happens. The old guard's fallback checked for kills.json next to the mod folder,
# but LW-51 moved save files into the update-safe Reloaded/User/Mods/<ModId> dir
# (SaveLocation.ResolveSaveDir, LivingWeapon/SaveLocation.cs), so that check was looking at a spot
# the file can never be in anymore -- it always came back "nothing to worry about" and would have
# waved a real installed save through. On 2026-07-25 a plain dev BuildLinked run got all the way
# to the edge of overwriting a production install with a live 384-kill tally and no marker file at
# all; only a human noticing stopped it.
#
# Pure over its three inputs (a marker path, a stamp path, and a list of tally-probe paths) so it
# is testable without a deploy. Precedence is FAIL CLOSED, checked in this order:
#   1. build_flavor.txt ($markerPath): BuildLinked's own last-DEPLOY marker, next to the mod.
#   2. run_flavor.txt ($stampPath): the mod's own last-RUN marker (FlavorStamp.cs), in the save
#      dir. Deliberately OUTRANKS a stale "dev" marker: a dev marker surviving a hand-extracted
#      prod zip (which BuildLinked never touched) is exactly the miss class this guard exists for,
#      while the stamp is written by whatever build actually ran most recently.
#   3. Any $tallyProbePaths path that exists: player data with no flavour evidence at all gets
#      protected as if it were production -- this is the 2026-07-25 incident shape.
#   4. Otherwise: no evidence either way, nothing to protect.
# A marker/stamp file is valid ONLY if its first line, trimmed, is exactly "dev" or "prod";
# anything else (missing, empty, garbage) counts as absent rather than trusted.
function Resolve-DeployedFlavor([string]$markerPath, [string]$stampPath, [string[]]$tallyProbePaths) {
    function Read-FlavorToken([string]$path) {
        if (-not (Test-Path $path)) { return "" }
        try { $line = ([string](Get-Content $path -TotalCount 1)).Trim() } catch { return "" }
        if ($line -eq "dev" -or $line -eq "prod") { return $line }
        return ""
    }

    $marker = Read-FlavorToken $markerPath
    $stamp  = Read-FlavorToken $stampPath

    if ($marker -eq "prod") { return [pscustomobject]@{ Flavor = "prod"; Source = "marker" } }
    if ($stamp  -eq "prod") { return [pscustomobject]@{ Flavor = "prod"; Source = "stamp" } }
    if ($marker -eq "dev")  { return [pscustomobject]@{ Flavor = "dev";  Source = "marker" } }
    if ($stamp  -eq "dev")  { return [pscustomobject]@{ Flavor = "dev";  Source = "stamp" } }
    foreach ($p in $tallyProbePaths) {
        if (Test-Path $p) { return [pscustomobject]@{ Flavor = "prod"; Source = "tally" } }
    }
    return [pscustomobject]@{ Flavor = ""; Source = "none" }
}

# Parked repo artifacts that must never ship. The two bloodpact nxd tables stay in the repo
# tree for provenance (renamed *.bloodpact_parked so the game never loads them), but the
# modloader scans every file under FFTIVC and logs a per-file "edits nex table ... which is
# unrecognized" warning on launch (owner-observed 2026-07-07). Both ship paths exclude this
# pattern (BuildLinked prunes after its stage copy; Publish excludes via robocopy /XF), and
# both verification steps fail red if one slips through, so the exclusion cannot drift.
$ParkedArtifactFilter = "*.bloodpact_parked"

function Invoke-TablePipeline {
    # generate -> dominance gate -> meta, with uniform exit-code checks. Throws
    # on any red step; the caller's catch turns that into a nonzero exit.
    # Missing python is a hard failure, not a skip: quietly packaging the
    # committed tree with no gate and no fresh meta.json is exactly the silent
    # ungated-package path this used to allow. The intentional skip is
    # Publish.ps1's -SkipGenerate, and only after a gated run this session.
    param(
        [Parameter(Mandatory = $true)][ValidateSet('DEPLOY', 'PACKAGE')]
        [string]$FailVerb
    )

    if (-not (Get-Command python -ErrorAction SilentlyContinue)) {
        throw "REFUSING TO ${FailVerb}: python not found on PATH (table generation + the dominance gate cannot run)."
    }

    Write-Host "  -> tools/generate.py (items.json -> table XMLs)..."
    & python "$PipelineRepoRoot\tools\generate.py"
    if ($LASTEXITCODE -ne 0) {
        throw "REFUSING TO ${FailVerb}: generate.py failed (exit $LASTEXITCODE)."
    }

    Write-Host "  -> tools/analyze.py (no item may be strictly dominated)..."
    & python "$PipelineRepoRoot\tools\analyze.py"
    if ($LASTEXITCODE -ne 0) {
        throw "REFUSING TO ${FailVerb}: at least one item is strictly dominated (see above)."
    }

    # Bake the runtime's per-weapon facts so the DLL build (and the unit tests,
    # which read LivingWeapon/meta.json) pick up a fresh copy.
    Write-Host "  -> tools/gen_living_weapon_meta.py (items.json -> meta.json)..."
    & python "$PipelineRepoRoot\tools\gen_living_weapon_meta.py"
    if ($LASTEXITCODE -ne 0) {
        throw "REFUSING TO ${FailVerb}: meta-gen failed (exit $LASTEXITCODE)."
    }

    # Bake the treasure tile address dataset.  Exit 1 from the gate (bad addr, coord
    # mismatch, off-byte violation) refuses deploy/package like analyze.py does.
    Write-Host "  -> tools/gen_treasure_db.py (treasure_addrs.json + map_trap_formation.json -> treasure.json)..."
    & python "$PipelineRepoRoot\tools\gen_treasure_db.py"
    if ($LASTEXITCODE -ne 0) {
        throw "REFUSING TO ${FailVerb}: treasure-db gen failed (exit $LASTEXITCODE)."
    }

    # Run the log scanner's own built-in regression cases (LW-148 F2): scan_logs.py is a
    # verify-time tool, not a build gate, but its PARSING LOGIC (line_level, the recognized-line
    # tripwire, --allow, flight-trigger parsing, ...) is exactly the kind of pure code this
    # pipeline's other gates exist to protect, and it had no build-time gate of its own. Running
    # its selftest here catches a regression in the scanner itself at build time instead of the
    # next time someone actually needs it to catch a bad live-verify log.
    Write-Host "  -> tools/scan_logs.py --selftest (the log scanner's own regression cases)..."
    & python "$PipelineRepoRoot\tools\scan_logs.py" --selftest
    if ($LASTEXITCODE -ne 0) {
        throw "REFUSING TO ${FailVerb}: scan_logs.py --selftest failed (exit $LASTEXITCODE)."
    }

    Write-Host "  -> Generated + gated + meta baked OK." -ForegroundColor Green
}

function Invoke-UnitTestGate {
    # The TDD gate. ONE canonical flag set, so a test that passes locally passed
    # under the same conditions everywhere.
    param(
        [Parameter(Mandatory = $true)][ValidateSet('DEPLOY', 'PACKAGE')]
        [string]$FailVerb
    )

    & dotnet test "$PipelineRepoRoot\LivingWeapon.Tests\LivingWeapon.Tests.csproj" --nologo -v q
    if ($LASTEXITCODE -ne 0) {
        throw "REFUSING TO ${FailVerb}: unit tests failed (see above)."
    }
}

function Invoke-LivingWeaponPublish {
    # Build the Living Weapon runtime into $OutDir. The framework-dependent
    # publish emits LivingWeapon.dll, Newtonsoft.Json.dll, LivingWeapon.deps.json
    # (the Reloaded loader reads it), and meta.json (copied via the csproj).
    #
    # -Dev defines LWDEV (-p:LwDev=true): kill thresholds {1,2,3} + every weapon
    # pre-seeded to P2 (one kill from P3) for fast in-game verification. Omit it
    # for production thresholds {5,25,50} and no kill seeding.
    #
    # -CleanFirst forces a FULL recompile: MSBuild's incremental up-to-date check
    # shipped a stale Release DLL with a fresh timestamp on the first 2.0.0 cut
    # (the copy step re-dates the file even when CoreCompile is skipped; caught
    # by byte-verifying the packaged DLL). The clean costs seconds and deletes
    # the failure class.
    param(
        [Parameter(Mandatory = $true)][string]$OutDir,
        [switch]$Dev,
        [switch]$CleanFirst
    )

    if ($CleanFirst) {
        Remove-Item -Recurse -Force "$PipelineRepoRoot\LivingWeapon\obj\Release", "$PipelineRepoRoot\LivingWeapon\bin\Release" -ErrorAction SilentlyContinue
    }

    $publishArgs = @("publish", "$PipelineRepoRoot\LivingWeapon\LivingWeapon.csproj", "-c", "Release", "-o", $OutDir)
    if ($Dev) { $publishArgs += "-p:LwDev=true" }

    & dotnet @publishArgs
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed (exit $LASTEXITCODE)."
    }
}
