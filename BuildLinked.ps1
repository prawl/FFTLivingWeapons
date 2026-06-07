# BuildLinked.ps1 - DEV build + deploy of prawl.fft.itemoverhaul into Reloaded-II.
#
# Local-dev counterpart to Publish.ps1 (which builds the production release zip).
# Mirrors the sibling FFTColorCustomizer's BuildLinked / Publish split:
#   BuildLinked.ps1 -> deploy straight into the live Reloaded Mods folder (this file)
#   Publish.ps1     -> stage + zip a distributable package
#
# Flow: regenerate tables from data/items.json -> GATE on the build-diversity check
# (refuse to deploy a strictly-dominated design) -> bake meta.json -> build the
# Living Weapon DLL -> deploy the whole mod (data tree + DLL) into the mod folder.
# The player's kills.json tally is preserved across the clean. Table/nxd/tex
# changes take effect on game RESTART; the DLL loads on next game launch.

$ErrorActionPreference = "Stop"
Split-Path $MyInvocation.MyCommand.Path | Push-Location
[Environment]::CurrentDirectory = $PWD

Write-Host "============================================" -ForegroundColor Cyan
Write-Host "   FFT Item Overhaul - DEV BUILD (linked)   " -ForegroundColor Cyan
Write-Host "============================================" -ForegroundColor Cyan

try {
    $root    = $PSScriptRoot
    $modId   = "prawl.fft.itemoverhaul"
    $modsDir = $env:RELOADEDIIMODS
    if (-not $modsDir) {
        $modsDir = "C:\program files (x86)\steam\steamapps\common\FINAL FANTASY TACTICS - The Ivalice Chronicles\Reloaded\Mods"
    }
    $dest = Join-Path $modsDir $modId

    # --- [1/6] Generate tables from the single source of truth ---
    Write-Host "`n[1/6] Generating tables from data/items.json..." -ForegroundColor Yellow
    python "$root\tools\generate.py"
    if ($LASTEXITCODE -ne 0) { Write-Host "generate.py failed" -ForegroundColor Red; exit 1 }

    # --- [2/6] Build-diversity gate (THE gate; refuse a dominated design) ---
    Write-Host "[2/6] Build-diversity gate (analyze.py)..." -ForegroundColor Yellow
    python "$root\tools\analyze.py"
    if ($LASTEXITCODE -ne 0) {
        Write-Host "REFUSING TO DEPLOY: at least one item is strictly dominated (see above)." -ForegroundColor Red
        exit 1
    }

    # --- [3/6] Unit tests (TDD gate; refuse to deploy on a red test) ---
    Write-Host "[3/6] Running unit tests (LivingWeapon.Tests)..." -ForegroundColor Yellow
    dotnet test "$root\LivingWeapon.Tests\LivingWeapon.Tests.csproj" --nologo -v q
    if ($LASTEXITCODE -ne 0) { Write-Host "REFUSING TO DEPLOY: unit tests failed (see above)." -ForegroundColor Red; exit 1 }

    # --- [4/6] Bake the runtime's per-weapon facts (meta.json) ---
    Write-Host "[4/6] Baking LivingWeapon meta.json..." -ForegroundColor Yellow
    python "$root\tools\gen_living_weapon_meta.py"
    if ($LASTEXITCODE -ne 0) { Write-Host "meta-gen failed" -ForegroundColor Red; exit 1 }

    # --- [5/6] Clean the live mod folder, preserving the player's kill tally ---
    # kills.json is the wielder's per-weapon kill count (their progress). Treat it
    # like ColorCustomizer treats UserThemes: back it up, clean, restore.
    Write-Host "[5/6] Cleaning $dest (preserving kills.json)..." -ForegroundColor Yellow
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

    # --- [6/6] Build the DLL into the mod folder + stage the data tree ---
    Write-Host "[6/6] Publishing Living Weapon DLL + staging data..." -ForegroundColor Yellow
    dotnet publish "$root\LivingWeapon\LivingWeapon.csproj" -c Release -o $dest
    if ($LASTEXITCODE -ne 0) { Write-Host "DLL build failed" -ForegroundColor Red; exit 1 }

    Copy-Item "$root\mod\FFTIVC" $dest -Recurse -Force
    Copy-Item "$root\mod\ModConfig.json" $dest -Force   # our config wins over any published one
    if (Test-Path "$root\mod\preview.png") { Copy-Item "$root\mod\preview.png" $dest -Force }
    if ($killsBak) {
        Copy-Item $killsBak "$dest\kills.json" -Force
        Remove-Item $killsBak -Force -ErrorAction SilentlyContinue
        Write-Host "  -> Restored player's kills.json tally" -ForegroundColor Green
    }

    # --- Verify the deployment (fail loud on missing pieces; no silent drift) ---
    Write-Host "`nVerifying deployment..." -ForegroundColor Cyan
    $errs = @()
    if (-not (Test-Path "$dest\LivingWeapon.dll")) { $errs += "LivingWeapon.dll missing" }
    if (-not (Test-Path "$dest\meta.json"))        { $errs += "meta.json missing (runtime per-weapon facts)" }
    if (-not (Test-Path "$dest\ModConfig.json"))   { $errs += "ModConfig.json missing" }
    if (-not (Test-Path "$dest\FFTIVC\data\enhanced\nxd\item.en.nxd")) { $errs += "item.en.nxd missing" }
    $xmls = @(Get-ChildItem "$dest\FFTIVC\tables\enhanced\*.xml" -ErrorAction SilentlyContinue)
    if ($xmls.Count -lt 6) { $errs += "expected 6 table XMLs, found $($xmls.Count)" }
    $tex = @(Get-ChildItem "$dest\FFTIVC\data\enhanced\ui\ffto\icon" -Filter *.tex -Recurse -ErrorAction SilentlyContinue)
    if ($tex.Count -lt 1) { $errs += "no .tex icon files deployed" }

    if ($errs.Count -gt 0) {
        Write-Host "`nDEPLOY VERIFICATION FAILED:" -ForegroundColor Red
        $errs | ForEach-Object { Write-Host "  X $_" -ForegroundColor Red }
        exit 1
    }

    Write-Host "`nDeployed $($xmls.Count) tables + $($tex.Count) icons + LivingWeapon.dll -> $dest" -ForegroundColor Green
    Write-Host "Restart the game to apply (tables on restart; DLL on next launch)." -ForegroundColor Green
}
finally {
    Pop-Location
}
