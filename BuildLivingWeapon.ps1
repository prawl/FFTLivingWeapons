# Build + deploy the FFT Living Weapon code mod (prawl.fft.livingweapon) to Reloaded-II.
# In-process C# runtime: counts kills and grows the wielder's stat. Restart the game to load.
$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$modId = "prawl.fft.livingweapon"
$modsDir = $env:RELOADEDIIMODS
if (-not $modsDir) {
    $modsDir = "C:\program files (x86)\steam\steamapps\common\FINAL FANTASY TACTICS - The Ivalice Chronicles\Reloaded\Mods"
}
$modPath = Join-Path $modsDir $modId

Write-Host "[1/3] Generating meta.json from items.json..." -ForegroundColor Yellow
python "$root\tools\gen_living_weapon_meta.py"
if ($LASTEXITCODE -ne 0) { Write-Host "meta-gen failed" -ForegroundColor Red; exit 1 }

Write-Host "[2/3] Publishing LivingWeapon.dll -> $modPath ..." -ForegroundColor Yellow
dotnet publish "$root\LivingWeapon\LivingWeapon.csproj" -c Release -o "$modPath"
if ($LASTEXITCODE -ne 0) { Write-Host "build failed" -ForegroundColor Red; exit 1 }

Write-Host "[3/3] Copying ModConfig.json + meta.json..." -ForegroundColor Yellow
Copy-Item "$root\LivingWeapon\ModConfig.json" "$modPath\ModConfig.json" -Force
Copy-Item "$root\LivingWeapon\meta.json" "$modPath\meta.json" -Force

Write-Host "Deployed to $modPath. Restart the game to load." -ForegroundColor Green
