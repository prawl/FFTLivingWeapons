# BuildLinked.ps1 - local build + deploy of prawl.fft.livingweapons into Reloaded-II.
#
# Local-dev counterpart to Publish.ps1 (which builds the production release zip).
# Mirrors the sibling FFTColorCustomizer's BuildLinked / Publish split:
#   BuildLinked.ps1 -> deploy straight into the live Reloaded Mods folder (this file)
#   Publish.ps1     -> stage + zip a distributable package
#
# The shared pipeline prefix (generate -> dominance gate -> meta -> unit tests ->
# DLL publish) lives in tools/pipeline.ps1; this file keeps the deploy-specific
# half: mods-folder resolution, the build-flavor guard, the kills.json round-trip,
# the Vortex marker exclusion, and deploy verification. Table/nxd/tex changes take
# effect on game RESTART; the DLL loads on next game launch.
#
#   -Prod   build with production kill thresholds {5,25,50} and no kill seeding
#           (omits -p:LwDev=true) -- for release-testing on a real save.
#   -Force  let a plain DEV deploy overwrite a prod-flavored install (see the
#           guard below for why that needs an explicit opt-in).

param(
    [switch]$Prod,
    [switch]$Force
)

$ErrorActionPreference = "Stop"
Split-Path $MyInvocation.MyCommand.Path | Push-Location
[Environment]::CurrentDirectory = $PWD

. "$PSScriptRoot\tools\pipeline.ps1"

$flavor = "DEV"
if ($Prod) { $flavor = "PROD" }

Write-Host "============================================" -ForegroundColor Cyan
Write-Host "   FFT Living Weapons - $flavor BUILD (linked)" -ForegroundColor Cyan
if ($Prod) {
    Write-Host "   (production thresholds, no kill seeding)" -ForegroundColor Cyan
}
Write-Host "============================================" -ForegroundColor Cyan

try {
    $root    = $PSScriptRoot
    $modId   = "prawl.fft.livingweapons"
    $modsDir = $env:RELOADEDIIMODS
    if (-not $modsDir) {
        $modsDir = "C:\program files (x86)\steam\steamapps\common\FINAL FANTASY TACTICS - The Ivalice Chronicles\Reloaded\Mods"
    }
    $dest   = Join-Path $modsDir $modId
    $marker = Join-Path $dest "build_flavor.txt"

    # --- Guard: a plain (dev) run must not stomp a prod-flavored install ---
    # A DEV build pre-seeds every weapon's tally (LWDEV); deployed over the real
    # prod install once and polluted the player's kills.json. build_flavor.txt
    # records what's deployed; kills.json with NO marker is the hand-copied
    # prod-era state and gets the same protection. -Prod never needs the guard.
    $deployed = ""
    if (Test-Path $marker) {
        $deployed = ([string](Get-Content $marker -TotalCount 1)).Trim()
        # An unreadable/garbage marker proves nothing -- treat it like NO marker and
        # fall through to the kills.json heuristic rather than silently failing open.
        if ($deployed -ne "dev" -and $deployed -ne "prod") { $deployed = "" }
    }
    if (-not $deployed -and (Test-Path "$dest\kills.json")) {
        $deployed = "prod"   # kills.json with no readable marker = the hand-copied prod-era state
    }
    if (-not $Prod) {
        if ($deployed -eq "prod" -and -not $Force) {
            Write-Host "`nREFUSING TO DEPLOY: $dest holds a PROD-flavored install (real save data)." -ForegroundColor Red
            Write-Host "A DEV deploy seeds every weapon's kill tally and would pollute the player's kills.json." -ForegroundColor Red
            Write-Host "Use -Prod to deploy a production build, or -Force if you really want the dev build." -ForegroundColor Red
            exit 1
        }
    } elseif ($deployed -eq "dev") {
        # Crossing dev -> prod: the preserved kills.json carries DEV-SEEDED counts (every
        # weapon floored by LWDEV). Restoring it into a release-test install plants phantom
        # kills and then the guard would defend that garbage. Say so loudly.
        Write-Host "`nWARNING: the existing install is DEV-flavored -- its kills.json carries seeded counts." -ForegroundColor Yellow
        Write-Host "The tally will be preserved as-is; delete $dest\kills.json first for a clean prod baseline." -ForegroundColor Yellow
    }

    # --- [1/5] Tables: generate -> dominance gate -> bake meta.json ---
    Write-Host "`n[1/5] Generating + gating tables, baking meta.json..." -ForegroundColor Yellow
    Invoke-TablePipeline -FailVerb DEPLOY

    # --- [2/5] Unit tests (TDD gate; after meta-gen, which the tests read) ---
    Write-Host "[2/5] Running unit tests (LivingWeapon.Tests)..." -ForegroundColor Yellow
    Invoke-UnitTestGate -FailVerb DEPLOY

    # --- [3/5] Clean the live mod folder, preserving the player's kill tally ---
    # kills.json is the wielder's per-weapon kill count (their progress). Treat it
    # like ColorCustomizer treats UserThemes: back it up, clean, restore.
    Write-Host "[3/5] Cleaning $dest (preserving kills.json)..." -ForegroundColor Yellow
    $killsBak = $null
    if (Test-Path "$dest\kills.json") {
        $killsBak = Join-Path $env:TEMP "livingweapon_kills.json.bak"
        Copy-Item "$dest\kills.json" $killsBak -Force
    }
    if (Test-Path $dest) {
        # Keep the Vortex marker so Vortex doesn't treat the folder as orphaned.
        Remove-Item "$dest\*" -Exclude "__folder_managed_by_vortex" -Recurse -Force -ErrorAction SilentlyContinue
    } else {
        New-Item -ItemType Directory -Force -Path $dest | Out-Null
    }

    # --- [4/5] Build the DLL into the mod folder + stage the data tree ---
    Write-Host "[4/5] Publishing Living Weapon DLL ($flavor) + staging data..." -ForegroundColor Yellow
    Invoke-LivingWeaponPublish -OutDir $dest -Dev:(-not $Prod) -CleanFirst

    Copy-Item "$root\mod\FFTIVC" $dest -Recurse -Force
    Copy-Item "$root\mod\ModConfig.json" $dest -Force   # our config wins over any published one
    if (Test-Path "$root\mod\preview.png") { Copy-Item "$root\mod\preview.png" $dest -Force }
    if ($killsBak) {
        Copy-Item $killsBak "$dest\kills.json" -Force
        Remove-Item $killsBak -Force -ErrorAction SilentlyContinue
        Write-Host "  -> Restored player's kills.json tally" -ForegroundColor Green
    }
    # Record the deployed flavor so the guard above can protect the NEXT run.
    Set-Content -Path $marker -Value $flavor.ToLower() -Encoding Ascii

    # --- [5/5] Verify the deployment (fail loud on missing pieces; no silent drift) ---
    # Same required-file manifest Publish's Verify-Package checks (pipeline.ps1).
    Write-Host "`n[5/5] Verifying deployment..." -ForegroundColor Cyan
    $errs = @()
    foreach ($file in $RequiredModFiles) {
        if (-not (Test-Path (Join-Path $dest $file))) { $errs += "$file missing" }
    }
    $xmls = @(Get-ChildItem "$dest\FFTIVC\tables\enhanced\*.xml" -ErrorAction SilentlyContinue)
    $tex  = @(Get-ChildItem "$dest\FFTIVC\data\enhanced\ui\ffto\icon" -Filter *.tex -Recurse -ErrorAction SilentlyContinue)
    if ($tex.Count -lt 1) { $errs += "no .tex icon files deployed" }

    if ($errs.Count -gt 0) {
        Write-Host "`nDEPLOY VERIFICATION FAILED:" -ForegroundColor Red
        $errs | ForEach-Object { Write-Host "  X $_" -ForegroundColor Red }
        exit 1
    }

    Write-Host "`nDeployed $($xmls.Count) tables + $($tex.Count) icons + LivingWeapon.dll ($flavor) -> $dest" -ForegroundColor Green
    Write-Host "Restart the game to apply (tables on restart; DLL on next launch)." -ForegroundColor Green
}
catch {
    Write-Host "`n$_" -ForegroundColor Red
    # A failure AFTER the clean step (e.g. dotnet publish hitting a game-locked DLL) has
    # already deleted the deployed kills.json and build_flavor.txt -- without these
    # restores the player's tally would be stranded in %TEMP% (and overwritten by the
    # next run's backup) and the prod guard would fail open on the next plain deploy.
    if ($killsBak -and (Test-Path $killsBak) -and -not (Test-Path "$dest\kills.json")) {
        Copy-Item $killsBak "$dest\kills.json" -Force
        Write-Host "Restored the player's kills.json after the failed deploy." -ForegroundColor Yellow
    }
    if ($deployed -and (Test-Path $dest) -and -not (Test-Path $marker)) {
        Set-Content -Path $marker -Value $deployed -Encoding Ascii
        Write-Host "Re-stamped build_flavor.txt ($deployed) so the guard still protects the next run." -ForegroundColor Yellow
    }
    exit 1
}
finally {
    Pop-Location
}
