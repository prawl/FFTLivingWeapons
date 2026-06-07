# Deploy the FFT Item Overhaul mod to Reloaded-II.
# Regenerates tables from data/items.json, GATES on the build-diversity check
# (refuses to deploy a design with any strictly-dominated item), then copies the
# mod tree + builds the Living Weapon DLL into the Reloaded Mods folder. Takes effect on restart.

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$modId = "prawl.fft.itemoverhaul"
$modsDir = $env:RELOADEDIIMODS
if (-not $modsDir) {
    $modsDir = "C:\program files (x86)\steam\steamapps\common\FINAL FANTASY TACTICS - The Ivalice Chronicles\Reloaded\Mods"
}

Write-Host "[1/3] Generating tables from data/items.json..." -ForegroundColor Yellow
python "$root\tools\generate.py"
if ($LASTEXITCODE -ne 0) { Write-Host "generate.py failed" -ForegroundColor Red; exit 1 }

Write-Host "[2/3] Build-diversity gate (analyze.py)..." -ForegroundColor Yellow
python "$root\tools\analyze.py"
if ($LASTEXITCODE -ne 0) {
    Write-Host "REFUSING TO DEPLOY: at least one item is strictly dominated (see above)." -ForegroundColor Red
    exit 1
}

Write-Host "[3/4] Copying data tables + config -> $modsDir\$modId ..." -ForegroundColor Yellow
$dest = Join-Path $modsDir $modId
New-Item -ItemType Directory -Force -Path $dest | Out-Null
Copy-Item "$root\mod\ModConfig.json" $dest -Force
Copy-Item "$root\mod\FFTIVC" $dest -Recurse -Force
if (Test-Path "$root\mod\preview.png") { Copy-Item "$root\mod\preview.png" $dest -Force }

Write-Host "[4/4] Building the Living Weapon runtime into the mod..." -ForegroundColor Yellow
python "$root\tools\gen_living_weapon_meta.py"
if ($LASTEXITCODE -ne 0) { Write-Host "meta-gen failed" -ForegroundColor Red; exit 1 }
$pub = "$root\LivingWeapon\bin\publish"
dotnet publish "$root\LivingWeapon\LivingWeapon.csproj" -c Release -o $pub
if ($LASTEXITCODE -ne 0) { Write-Host "DLL build failed" -ForegroundColor Red; exit 1 }
Copy-Item "$pub\*" $dest -Recurse -Force
Copy-Item "$root\mod\ModConfig.json" $dest -Force   # our config wins over any published one

$xmls = (Get-ChildItem "$dest\FFTIVC\tables\enhanced\*.xml" | Measure-Object).Count
Write-Host "Deployed: $xmls tables + LivingWeapon.dll under $dest. Restart the game to apply." -ForegroundColor Green
