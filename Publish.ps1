<#
.SYNOPSIS
    Packages FFT Item Overhaul (data tables + Living Weapon DLL) for release.
.DESCRIPTION
    Production counterpart to BuildLinked.ps1 (which deploys straight into the
    live Reloaded Mods folder). Mirrors the sibling FFTColorCustomizer's
    BuildLinked / Publish split.

    Regenerates the modloader tables from data/items.json, GATES on the
    build-diversity check (refuses to package a design with any strictly-
    dominated item), bakes meta.json, builds the Living Weapon DLL, then stages
    the mod deliverables (ModConfig.json, preview.png, LivingWeapon.dll + deps,
    meta.json, FFTIVC table/nxd/tex tree) into a build folder named after the
    ModId and zips it with a single top-level wrapper folder so Reloaded-II /
    Nexus / Vortex extract to the expected path.

    The generate + gate + meta + DLL-build steps mirror BuildLinked.ps1 so a local
    `.\Publish.ps1` produces the same vetted artifacts a deploy would. NOTE:
    generate.py emits the 6 table XMLs only; item.en.nxd is produced separately by
    tools/patch_names.py (needs FF16Tools) and is shipped from its committed copy
    in the mod tree.

    The package is verified before it's considered shippable: any missing
    required file (including LivingWeapon.dll) makes the script exit 1 (the
    v3.0.7 silent-artifact-drift guard).
.PARAMETER Version
    Version number for the mod (e.g., "1.0.1").
    Default: reads ModVersion from mod/ModConfig.json (NOT hardcoded).
.PARAMETER OutputPath
    Where to save the final ZIP file.
    Default: "." under GitHub Actions, else "C:\Users\ptyRa\Downloads".
.PARAMETER NexusModId
    Nexus mod ID for the archive filename convention. Placeholder 0 until the
    mod is registered on Nexus; MUST be set before uploading to Nexus, otherwise
    Vortex can't parse the version/mod ID from the filename.
.PARAMETER SkipGenerate
    Skip the generate + build-diversity gate (package the committed tree as-is).
    Use only when you've already generated + gated this session.
.EXAMPLE
    .\Publish.ps1
    # Generate + gate + package; version read from mod/ModConfig.json
.EXAMPLE
    .\Publish.ps1 -Version "1.0.1"
    # Package with an explicit version
#>

[cmdletbinding()]
param (
    [string]$Version = "",
    [string]$OutputPath = "",
    [int]$NexusModId = 0,   # TODO: set to the real Nexus mod ID before Nexus upload
    [switch]$SkipGenerate
)

## => Configuration <= ##
# The build folder's NAME becomes the wrapper folder INSIDE the zip. Vortex's
# FFT IC extension treats a zip with files-at-root as malformed and falls back
# to fabricating a fake wrapper, which double-nests the install. Naming this
# folder after our ModId makes the zip extract to the expected layout.
$ModId            = "prawl.fft.itemoverhaul"
$SourceModPath    = "mod"
$BuildOutputPath  = "Publish/$ModId"
$SourceModConfig  = "$SourceModPath/ModConfig.json"
$SourcePreview    = "$SourceModPath/preview.png"
$SourceFFTIVC     = "$SourceModPath/FFTIVC"

# Set default output path based on environment
if (-not $OutputPath) {
    if ($env:GITHUB_ACTIONS) {
        # GitHub Actions - output to workspace
        $OutputPath = "."
    }
    else {
        # Local build - output to Downloads
        $OutputPath = "C:\Users\ptyRa\Downloads"
    }
}

## => Functions <= ##
function Write-Status {
    param($Message, $Color = "Green")
    Write-Host "`n==> $Message" -ForegroundColor $Color
}

function Write-ErrorMessage {
    param($Message)
    Write-Host "`n[ERROR] $Message" -ForegroundColor Red
    exit 1
}

function Invoke-GenerateAndGate {
    # Mirror deploy.ps1: regenerate tables from items.json, then refuse to
    # package if any item is strictly dominated. Skipped under -SkipGenerate or
    # when python is unavailable (then we package the committed tree as-is).
    Write-Status "Regenerating tables + build-diversity gate..." "Cyan"

    $python = (Get-Command python -ErrorAction SilentlyContinue)
    if (-not $python) {
        Write-Host "  -> python not found; packaging committed tables as-is (skipping generate + gate)." -ForegroundColor Yellow
        return
    }

    Write-Host "  -> tools/generate.py (items.json -> table XMLs)..."
    & python "tools/generate.py"
    if ($LASTEXITCODE -ne 0) {
        Write-ErrorMessage "generate.py failed (exit $LASTEXITCODE)."
    }

    Write-Host "  -> tools/analyze.py (no item may be strictly dominated)..."
    & python "tools/analyze.py"
    if ($LASTEXITCODE -ne 0) {
        Write-ErrorMessage "REFUSING TO PACKAGE: at least one item is strictly dominated (see above)."
    }

    # Bake the runtime's per-weapon facts so the DLL build picks up a fresh
    # meta.json (copied into the package via the csproj).
    Write-Host "  -> tools/gen_living_weapon_meta.py (items.json -> meta.json)..."
    & python "tools/gen_living_weapon_meta.py"
    if ($LASTEXITCODE -ne 0) {
        Write-ErrorMessage "meta-gen failed (exit $LASTEXITCODE)."
    }

    Write-Host "  -> Generated + gated + meta baked OK." -ForegroundColor Green
}

function Invoke-BuildDll {
    # Build the Living Weapon runtime straight into the package folder so the
    # release zip contains the DLL + its deps + meta.json. ModConfig.json declares
    # "ModDll": "LivingWeapon.dll", so a package without it is a broken mod (this
    # was the gap: the old data-only Publish shipped a manifest pointing at a DLL
    # it never included). The framework-dependent publish emits LivingWeapon.dll,
    # Newtonsoft.Json.dll, the *.deps.json / *.runtimeconfig.json the loader needs,
    # and meta.json (copied via the csproj).
    Write-Status "Building Living Weapon DLL into the package..." "Cyan"

    # NO -p:LwDev here: production ships the real escalating thresholds {5,20,50} and seeds no kills.
    # (BuildLinked.ps1 passes -p:LwDev=true for the dev {1,2,3} + auto-P3 testing build.)
    & dotnet publish "LivingWeapon/LivingWeapon.csproj" -c Release -o $BuildOutputPath
    if ($LASTEXITCODE -ne 0) {
        Write-ErrorMessage "dotnet publish failed (exit $LASTEXITCODE)."
    }

    # Drop debug symbols from the package root (users don't need them; keep the
    # deps.json / runtimeconfig.json the Reloaded loader relies on).
    Get-ChildItem $BuildOutputPath -Filter *.pdb -File -ErrorAction SilentlyContinue |
        Remove-Item -Force -ErrorAction SilentlyContinue

    Write-Host "  -> DLL build complete." -ForegroundColor Green
}

function Get-ModVersion {
    param([string]$RequestedVersion)

    if (-not [string]::IsNullOrEmpty($RequestedVersion)) {
        Write-Host "  -> Using version from -Version parameter: $RequestedVersion"
        return $RequestedVersion
    }

    # Derive from the SOURCE ModConfig.json, never hardcoded.
    if (-not (Test-Path $SourceModConfig)) {
        Write-ErrorMessage "No version specified and ModConfig.json not found at: $SourceModConfig"
    }

    $config = Get-Content $SourceModConfig -Raw | ConvertFrom-Json
    $modVersion = $config.ModVersion
    if ([string]::IsNullOrEmpty($modVersion)) {
        Write-ErrorMessage "ModConfig.json has no ModVersion field at: $SourceModConfig"
    }

    Write-Host "  -> Using version from ModConfig.json: $modVersion"
    return $modVersion
}

function Clean-BuildDirectories {
    Write-Status "Cleaning build directory..." "Yellow"

    if (Test-Path $BuildOutputPath) {
        Remove-Item "$BuildOutputPath\*" -Recurse -Force -ErrorAction SilentlyContinue | Out-Null
    } else {
        New-Item $BuildOutputPath -ItemType Directory -Force | Out-Null
    }
}

function Copy-ModAssets {
    Write-Status "Staging mod deliverables..." "Cyan"

    # ModConfig.json (required)
    if (-not (Test-Path $SourceModConfig)) {
        Write-ErrorMessage "ModConfig.json not found at: $SourceModConfig"
    }
    Write-Host "  -> Copying ModConfig.json..."
    Copy-Item $SourceModConfig -Destination $BuildOutputPath -Force

    # preview.png (required; it's the ModIcon)
    if (-not (Test-Path $SourcePreview)) {
        Write-ErrorMessage "preview.png not found at: $SourcePreview"
    }
    Write-Host "  -> Copying preview.png..."
    Copy-Item $SourcePreview -Destination $BuildOutputPath -Force

    # FFTIVC tree: tables/enhanced/*.xml + data/enhanced/nxd/item.en.nxd + ui .tex (required)
    if (-not (Test-Path $SourceFFTIVC)) {
        Write-ErrorMessage "FFTIVC folder not found at: $SourceFFTIVC"
    }
    Write-Host "  -> Copying FFTIVC folder (tables + nxd + icons)..."
    $destFFTIVC = "$BuildOutputPath/FFTIVC"

    # robocopy for efficient recursive copy (handles the 468 .tex icon files)
    $robocopyArgs = @(
        $SourceFFTIVC,
        $destFFTIVC,
        "/E",     # Copy subdirectories including empty ones
        "/NFL",   # No file list
        "/NDL",   # No directory list
        "/NJH",   # No job header
        "/NJS",   # No job summary
        "/NC",    # No class
        "/NS"     # No size
    )
    robocopy @robocopyArgs | Out-Null
    # robocopy exit codes < 8 are success (0-7 = files copied / nothing to do / extras)
    if ($LASTEXITCODE -ge 8) {
        Write-ErrorMessage "Failed to copy FFTIVC folder (robocopy exit $LASTEXITCODE)!"
    }

    $xmlCount = (Get-ChildItem "$destFFTIVC/tables/enhanced" -Filter "*.xml" -ErrorAction SilentlyContinue | Measure-Object).Count
    $texCount = (Get-ChildItem "$destFFTIVC" -Filter "*.tex" -Recurse -ErrorAction SilentlyContinue | Measure-Object).Count
    Write-Host "  -> Staged $xmlCount table XML(s) and $texCount icon .tex file(s)" -ForegroundColor Green
}

function Create-Package {
    param([string]$ModVersion)

    Write-Status "Creating ZIP package..." "Green"

    # Default: a STABLE name (FFTItemOverhaul-<version>.zip) so re-cutting a tag overwrites the
    # release asset in place instead of piling up timestamped copies. With -NexusModId set, switch to
    # the Nexus convention (<ModName>-<NexusModId>-<version-dashed>-<unix-timestamp>.zip) that Vortex
    # parses for version + mod ID (without which the mod shows a "!" with no version when hand-installed).
    $versionDashed = $ModVersion -replace '\.', '-'
    if ($NexusModId -gt 0) {
        $unixTimestamp = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
        $packageName = "FFTItemOverhaul-$NexusModId-$versionDashed-$unixTimestamp.zip"
    } else {
        Write-Host "  -> Stable name (no -NexusModId); pass -NexusModId for the Vortex-parseable name before a Nexus upload." -ForegroundColor Yellow
        $packageName = "FFTItemOverhaul-$ModVersion.zip"
    }
    $packagePath = Join-Path $OutputPath $packageName

    # Remove existing package if it exists
    if (Test-Path $packagePath) {
        Write-Host "  -> Removing existing package..."
        Remove-Item $packagePath -Force
    }

    # Ensure output directory exists
    if (-not (Test-Path $OutputPath)) {
        Write-Host "  -> Creating output directory: $OutputPath"
        New-Item -ItemType Directory -Force -Path $OutputPath | Out-Null
    }

    # Ensure build directory exists
    if (-not (Test-Path $BuildOutputPath)) {
        Write-ErrorMessage "Build output directory not found: $BuildOutputPath"
        return $null
    }

    # Load assembly properly
    try {
        Add-Type -Assembly System.IO.Compression.FileSystem -ErrorAction Stop
    }
    catch {
        Write-Host "  -> Loading compression assembly using alternative method..."
        [Reflection.Assembly]::LoadWithPartialName("System.IO.Compression.FileSystem") | Out-Null
    }

    try {
        # Convert paths to absolute
        $absoluteBuildPath   = (Get-Item $BuildOutputPath).FullName
        $absolutePackagePath = [System.IO.Path]::GetFullPath($packagePath)

        Write-Host "  -> Source: $absoluteBuildPath"
        Write-Host "  -> Target: $absolutePackagePath"

        # includeBaseDirectory: $true wraps zip contents in a folder named after
        # the build folder (prawl.fft.itemoverhaul). Vortex's FFT IC extension
        # installer treats this as the proper structure and avoids creating a
        # fake wrapper folder (which would double-nest the install).
        [System.IO.Compression.ZipFile]::CreateFromDirectory(
            $absoluteBuildPath,
            $absolutePackagePath,
            [System.IO.Compression.CompressionLevel]::Optimal,
            $true
        )

        if (Test-Path $absolutePackagePath) {
            $packageInfo = Get-Item $absolutePackagePath
            $sizeMB = [math]::Round($packageInfo.Length / 1MB, 2)

            Write-Host "  -> Package created successfully!" -ForegroundColor Green
            Write-Host "  -> Size: $sizeMB MB" -ForegroundColor Cyan
            Write-Host "  -> Location: $absolutePackagePath" -ForegroundColor Cyan
            return $absolutePackagePath
        }
        else {
            Write-ErrorMessage "Package was not created at: $absolutePackagePath"
            return $null
        }
    }
    catch {
        Write-Host "`n[ERROR] Failed to create ZIP package: $_" -ForegroundColor Red
        Write-Host "  -> Error details: $($_.Exception.Message)" -ForegroundColor Red
        return $null
    }
}

function Verify-Package {
    param([string]$PackagePath)

    # Returns $true if the package contains every required file and directory,
    # $false otherwise. Caller MUST honor the return value; this is the gate
    # that catches "the zip exists but is empty / wrong" bugs before they ship
    # to users (see the v3.0.7 incident on the color mod, where a source archive
    # shipped as the release because nothing checked the artifact contents).
    Write-Status "Verifying package contents..." "Cyan"

    if (-not $PackagePath -or -not (Test-Path $PackagePath)) {
        Write-Host "  -> Package not found for verification" -ForegroundColor Red
        return $false
    }

    Add-Type -Assembly System.IO.Compression.FileSystem -ErrorAction SilentlyContinue

    $missingCount = 0

    try {
        $zip = [System.IO.Compression.ZipFile]::OpenRead($PackagePath)

        # Normalize all entry paths to forward-slash and strip the single wrapper
        # folder added by includeBaseDirectory=true, so the required-file list
        # below stays clean regardless of the wrapper's name.
        $entryPaths = @($zip.Entries | ForEach-Object { $_.FullName -replace '\\', '/' })
        $firstSegments = @($entryPaths | ForEach-Object { ($_ -split '/')[0] } | Sort-Object -Unique)
        $wrapper = ""
        if ($firstSegments.Count -eq 1 -and $firstSegments[0]) {
            $wrapper = $firstSegments[0]
            $entryPaths = @($entryPaths | ForEach-Object {
                if ($_.StartsWith("$wrapper/")) { $_.Substring($wrapper.Length + 1) } else { $_ }
            })
            Write-Host "  -> Wrapper folder: $wrapper" -ForegroundColor Gray
        }

        # Required individual files: mod manifest, icon, the Living Weapon runtime
        # (DLL + its JSON dep + baked meta), all 6 sparse table XMLs, and the
        # full-table item nxd. ModConfig.json declares "ModDll": "LivingWeapon.dll",
        # so the DLL is non-optional — shipping the manifest without it is the bug
        # this guard exists to catch.
        $requiredFiles = @(
            "ModConfig.json",
            "preview.png",
            "LivingWeapon.dll",
            "Newtonsoft.Json.dll",
            "meta.json",
            "FFTIVC/tables/enhanced/ItemData.xml",
            "FFTIVC/tables/enhanced/ItemWeaponData.xml",
            "FFTIVC/tables/enhanced/ItemArmorData.xml",
            "FFTIVC/tables/enhanced/ItemShieldData.xml",
            "FFTIVC/tables/enhanced/ItemAccessoryData.xml",
            "FFTIVC/tables/enhanced/ItemEquipBonusData.xml",
            "FFTIVC/data/enhanced/nxd/item.en.nxd",
            "FFTIVC/data/enhanced/nxd/ability.en.nxd"
        )

        foreach ($file in $requiredFiles) {
            if ($entryPaths -contains $file) {
                Write-Host "  [OK] $file" -ForegroundColor Green
            } else {
                Write-Host "  [MISSING] $file" -ForegroundColor Red
                $missingCount++
            }
        }

        # Required icon tree: must contain at least one .tex under the ui texture path.
        $iconRoot = "FFTIVC/data/enhanced/ui/ffto/icon"
        $texEntries = @($entryPaths | Where-Object { $_.StartsWith("$iconRoot/") -and $_.EndsWith('.tex') })
        if ($texEntries.Count -gt 0) {
            Write-Host "  [OK] $iconRoot (with $($texEntries.Count) .tex files)" -ForegroundColor Green
        } else {
            Write-Host "  [MISSING] $iconRoot (expected .tex icon files, found 0)" -ForegroundColor Red
            $missingCount++
        }

        $zip.Dispose()
    }
    catch {
        Write-Host "`n[ERROR] Failed to verify package: $_" -ForegroundColor Red
        return $false
    }

    if ($missingCount -gt 0) {
        Write-Host "`n[FAIL] Verification failed: $missingCount required entries missing." -ForegroundColor Red
        return $false
    }

    Write-Host "`n[PASS] All required entries present." -ForegroundColor Green
    return $true
}

## => Main Script <= ##

Write-Host "`n=====================================" -ForegroundColor Magenta
Write-Host "    FFT Item Overhaul - Publisher    " -ForegroundColor Magenta
Write-Host "=====================================" -ForegroundColor Magenta

# Save current directory and change to script directory
$originalLocation = Get-Location
Split-Path $MyInvocation.MyCommand.Path | Push-Location
[Environment]::CurrentDirectory = $PWD

try {
    # Step 1: Resolve version (from -Version, else ModConfig.json, never hardcoded)
    $finalVersion = Get-ModVersion -RequestedVersion $Version

    # Step 2: Regenerate tables + build-diversity gate (unless -SkipGenerate)
    if (-not $SkipGenerate) {
        Invoke-GenerateAndGate
    } else {
        Write-Host "  -> -SkipGenerate set; packaging committed tables as-is." -ForegroundColor Yellow
    }

    # Step 3: Unit tests (TDD gate; refuse to package on a red test)
    Write-Status "Running unit tests (LivingWeapon.Tests)..." "Cyan"
    & dotnet test "LivingWeapon.Tests/LivingWeapon.Tests.csproj" --nologo -v q
    if ($LASTEXITCODE -ne 0) { Write-ErrorMessage "REFUSING TO PACKAGE: unit tests failed (see above)." }

    # Step 4: Clean build directory
    Clean-BuildDirectories

    # Step 5: Build the Living Weapon DLL into the package folder
    Invoke-BuildDll

    # Step 6: Stage deliverables into Publish/<ModId>/ (ModConfig copied here wins)
    Copy-ModAssets

    # Step 7: Create Package
    $packagePath = Create-Package -ModVersion $finalVersion

    if ($packagePath) {
        # Step 8: Verify, fail loudly if anything's missing. This is the gate
        # that stops a broken/empty/wrong zip from shipping to users.
        $verifyOk = Verify-Package -PackagePath $packagePath
        if (-not $verifyOk) {
            Write-Status "Publishing failed - package verification failed" "Red"
            $exitCode = 1
        }
        else {
            # Emit the produced zip filename as a GitHub Actions output so the
            # workflow can reference it explicitly (no glob, no drift risk).
            # The workflow consumes `steps.publish.outputs.zip`.
            if ($env:GITHUB_OUTPUT) {
                $zipFilename = Split-Path $packagePath -Leaf
                Add-Content -Path $env:GITHUB_OUTPUT -Value "zip=$zipFilename"
                Write-Host "  -> Set GHA output: zip=$zipFilename" -ForegroundColor Cyan
            }

            Write-Status "Publishing completed successfully!" "Green"
            Write-Host "`n=====================================" -ForegroundColor Magenta
            Write-Host "Package ready at: $packagePath" -ForegroundColor Yellow
            Write-Host "Version: $finalVersion" -ForegroundColor Yellow
            Write-Host "=====================================" -ForegroundColor Magenta
            $exitCode = 0
        }
    }
    else {
        Write-Status "Publishing failed - package creation unsuccessful" "Red"
        $exitCode = 1
    }
}
catch {
    Write-Host "`n[ERROR] An unexpected error occurred: $_" -ForegroundColor Red
    Write-Host "Stack Trace: $($_.ScriptStackTrace)" -ForegroundColor Red
    $exitCode = 1
}
finally {
    # Restore original directory
    Pop-Location
    Set-Location $originalLocation
    exit $exitCode
}
