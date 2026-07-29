# BuildLinked.ps1 - local build + deploy of prawl.fft.livingweapons into Reloaded-II.
#
# Local-dev counterpart to Publish.ps1 (which builds the production release zip).
# Mirrors the sibling FFTColorCustomizer's BuildLinked / Publish split:
#   BuildLinked.ps1 -> deploy straight into the live Reloaded Mods folder (this file)
#   Publish.ps1     -> stage + zip a distributable package
#
# The shared pipeline prefix (generate -> dominance gate -> meta -> unit tests ->
# DLL publish) lives in tools/pipeline.ps1; this file keeps the deploy-specific
# half: mods-folder resolution, the build-flavor guard, the save-file + flight/ archive
# round-trip (tools/pipeline.ps1's $PreservedSaveFiles), the Vortex marker exclusion, and
# deploy verification. Table/nxd/tex changes take effect on game RESTART; the DLL loads
# on next game launch.
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
    # One named temp directory for the save-adjacent round-trip (decision 5):
    # $PreservedSaveFiles (tools/pipeline.ps1) plus the flight/ archive directory.
    # Defined here -- before the backup step runs -- so the catch path below can always
    # Test-Path it, even if the failure happens before stage [3/5] backs anything up.
    $preserveDir = Join-Path $env:TEMP "livingweapon_preserve"

    # --- Pre-deploy: scan the OUTGOING session's log (LW-54, docs/VERIFY_LIVE.md) ---
    # This is the one moment in the dev loop where livingweapon.log is both COMPLETE (you killed
    # the game before deploying, and the file sink appends per line, so a kill loses nothing from
    # the FILE) and about to be DESTROYED (the [3/5] clean wipes it). CAPTURE the verdict here,
    # before the wipe; the finally block REPORTS it LAST so a dirty prior session cannot scroll
    # away under the deploy output and get missed (the failure lines are captured now because the
    # log is gone by the time we report). Deliberately a REPORT, NOT A GATE (LW-54): a dirty log
    # never blocks the deploy (you are usually deploying the fix for the very error it caught), and
    # a native nonzero exit does not trip $ErrorActionPreference='Stop'.
    $scanExit = $null
    $scanFindings = @()
    if (Get-Command python -ErrorAction SilentlyContinue) {
        Write-Host "`nPre-deploy: scanning the previous session's livingweapon.log..." -ForegroundColor Yellow
        # --quiet: stdout carries only the [FAIL] lines (captured), nothing on a clean scan.
        $scanFindings = @(& python "$root\tools\scan_logs.py" --mod-dir $dest --quiet)
        $scanExit = $LASTEXITCODE
    }

    # --- Guard: a plain (dev) run must not stomp a prod-flavored install ---
    # ELI5: this deploy is about to overwrite the mod folder, and there might be a real player's
    # save sitting behind it. A DEV build pre-fills every weapon's kill tally for fast testing, so
    # deploying one over a real save would quietly wipe that player's actual progress. This check
    # gathers every clue available before that happens, and refuses to guess wrong.
    #
    # (Tech, LW-134, 2026-07-25 near-miss): build_flavor.txt ($marker) is BuildLinked's own
    # last-DEPLOY marker, written next to the mod. LW-51 moved the actual save files -- kills.json
    # included -- out to the update-safe Reloaded/User/Mods/<ModId> dir, so the OLD fallback here
    # (Test-Path next to the mod) was checking a spot kills.json can no longer be in; it failed
    # open, and a plain dev BuildLinked run nearly stomped a real production save that same day,
    # caught only by a human noticing. $saveDir below MUST mirror
    # SaveLocation.ResolveSaveDir (LivingWeapon/SaveLocation.cs) exactly: the two are one
    # resolution rule kept in two languages, and they move together. run_flavor.txt ($stampPath)
    # is stamped by the RUNNING mod itself on every launch (FlavorStamp.cs), so it is last-RUN
    # truth, a second and independent witness from build_flavor.txt's last-DEPLOY truth.
    # Resolve-DeployedFlavor (tools/pipeline.ps1) is the shared, unit-tested precedence over both
    # plus a raw kills.json probe; see its own comment for the exact rule order. -Prod never needs
    # the guard. Resolve-SaveDir (tools/pipeline.ps1, LW-148) is the extracted, cross-language-pinned
    # form of the derivation that used to live inline here only.
    $saveDir      = Resolve-SaveDir $modsDir $modId
    $stampPath    = Join-Path $saveDir "run_flavor.txt"
    $flavorVerdict = Resolve-DeployedFlavor $marker $stampPath @((Join-Path $saveDir "kills.json"), (Join-Path $dest "kills.json"))
    $deployed = $flavorVerdict.Flavor
    if (-not $Prod) {
        if ($deployed -eq "prod" -and -not $Force) {
            Write-Host "`nREFUSING TO DEPLOY: evidence says $dest holds a PROD-flavored install (real save data)." -ForegroundColor Red
            Write-Host "Evidence source: $($flavorVerdict.Source) (see Resolve-DeployedFlavor in tools/pipeline.ps1)." -ForegroundColor Red
            Write-Host "A DEV deploy seeds every weapon's kill tally and would pollute the player's kills.json." -ForegroundColor Red
            Write-Host "Use -Prod to deploy a production build, or -Force if you really want the dev build." -ForegroundColor Red
            if ($flavorVerdict.Source -eq "stamp") {
                Write-Host "The escape hatch: the last RUN of this mod was a production build. If you already" -ForegroundColor Red
                Write-Host "-Force deployed a dev build over it, launch the game once (the dev build re-stamps" -ForegroundColor Red
                Write-Host "run_flavor.txt) or pass -Force again." -ForegroundColor Red
            }
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

    # --- [3/5] Clean the live mod folder, preserving save files + flight/ archives ---
    # kills.json/legends.json/gunslinger.json are the player's progress; flight/ is the
    # flight recorder's black-box archive directory. Round-trip all of them through
    # $preserveDir: -Exclude against -Recurse looked like protection but silently wiped
    # flight/ anyway (1bd87a1) -- a real backup/clean/restore cycle is the fix.
    Write-Host "[3/5] Cleaning $dest (preserving save files + flight/ archives)..." -ForegroundColor Yellow
    if (Test-Path $preserveDir) { Remove-Item $preserveDir -Recurse -Force -ErrorAction SilentlyContinue }
    New-Item -ItemType Directory -Force -Path $preserveDir | Out-Null
    foreach ($f in $PreservedSaveFiles) {
        $src = Join-Path $dest $f
        if (Test-Path $src) {
            Copy-Item $src (Join-Path $preserveDir $f) -Force
        }
    }
    if (Test-Path (Join-Path $dest "flight")) {
        Copy-Item (Join-Path $dest "flight") $preserveDir -Recurse -Force
    }
    if (Test-Path $dest) {
        # Keep the Vortex marker so Vortex doesn't treat the folder as orphaned. Everything
        # else (incl. flight/) is wiped here and restored from $preserveDir below.
        Remove-Item "$dest\*" -Exclude "__folder_managed_by_vortex" -Recurse -Force -ErrorAction SilentlyContinue
    } else {
        New-Item -ItemType Directory -Force -Path $dest | Out-Null
    }

    # --- [4/5] Build the DLL into the mod folder + stage the data tree ---
    Write-Host "[4/5] Publishing Living Weapon DLL ($flavor) + staging data..." -ForegroundColor Yellow
    Invoke-LivingWeaponPublish -OutDir $dest -Dev:(-not $Prod) -CleanFirst

    Copy-Item "$root\mod\FFTIVC" $dest -Recurse -Force
    # Prune parked repo artifacts from the staged tree ($ParkedArtifactFilter, tools/pipeline.ps1):
    # Copy-Item -Exclude is unreliable against -Recurse, so stage everything and delete the parked
    # files deterministically. The [5/5] verification below fails red if any survive.
    Get-ChildItem "$dest\FFTIVC" -Recurse -Filter $ParkedArtifactFilter -ErrorAction SilentlyContinue | Remove-Item -Force
    Copy-Item "$root\mod\ModConfig.json" $dest -Force   # our config wins over any published one
    if (Test-Path "$root\mod\preview.png") { Copy-Item "$root\mod\preview.png" $dest -Force }
    foreach ($f in $PreservedSaveFiles) {
        $src = Join-Path $preserveDir $f
        if (Test-Path $src) {
            Copy-Item $src (Join-Path $dest $f) -Force
        }
    }
    if (Test-Path (Join-Path $preserveDir "flight")) {
        Copy-Item (Join-Path $preserveDir "flight") $dest -Recurse -Force
    }
    # LW-28: fail RED if the round-trip lost anything, BEFORE deleting the backup dir, so the
    # surviving copies stay recoverable in %TEMP% (and the catch path below re-restores the
    # missing items from that same dir on its way to exit 1).
    $lostItems = @(Get-LostPreservedItems $preserveDir $dest $PreservedSaveFiles)
    if ($lostItems.Count -gt 0) {
        throw "POST-RESTORE CHECK FAILED: preserved item(s) missing from ${dest}: $($lostItems -join ', '). Backup copies remain in $preserveDir."
    }
    Remove-Item $preserveDir -Recurse -Force -ErrorAction SilentlyContinue
    Write-Host "  -> Restored preserved save files + flight/ archives (post-restore check passed)" -ForegroundColor Green
    # Record the deployed flavor so the guard above can protect the NEXT run.
    Set-Content -Path $marker -Value $flavor.ToLower() -Encoding Ascii

    # --- [5/5] Verify the deployment (fail loud on missing pieces; no silent drift) ---
    # Same required-file manifest Publish's Verify-Package checks (pipeline.ps1).
    Write-Host "`n[5/5] Verifying deployment..." -ForegroundColor Cyan
    $errs = @()
    foreach ($file in $RequiredModFiles) {
        if (-not (Test-Path (Join-Path $dest $file))) { $errs += "$file missing" }
    }
    $parkedDeployed = @(Get-ChildItem "$dest\FFTIVC" -Recurse -Filter $ParkedArtifactFilter -ErrorAction SilentlyContinue)
    if ($parkedDeployed.Count -gt 0) { $errs += "$($parkedDeployed.Count) parked artifact(s) ($ParkedArtifactFilter) deployed" }
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
    # already wiped $dest's save files + flight/ and build_flavor.txt -- without these
    # restores the player's tally/legends/flight evidence would be stranded in %TEMP%
    # (and overwritten by the next run's backup) and the prod guard would fail open on
    # the next plain deploy. Restore each preserved item ONLY if it's still missing, so
    # this never clobbers a partial restore a later stage already completed.
    if ((Test-Path $preserveDir) -and (Test-Path $dest)) {
        foreach ($f in $PreservedSaveFiles) {
            $src = Join-Path $preserveDir $f
            $dst = Join-Path $dest $f
            if ((Test-Path $src) -and -not (Test-Path $dst)) {
                Copy-Item $src $dst -Force
                Write-Host "Restored $f after the failed deploy." -ForegroundColor Yellow
            }
        }
        $flightSrc = Join-Path $preserveDir "flight"
        if ((Test-Path $flightSrc) -and -not (Test-Path (Join-Path $dest "flight"))) {
            Copy-Item $flightSrc $dest -Recurse -Force
            Write-Host "Restored flight/ after the failed deploy." -ForegroundColor Yellow
        }
    }
    if ($deployed -and (Test-Path $dest) -and -not (Test-Path $marker)) {
        Set-Content -Path $marker -Value $deployed -Encoding Ascii
        Write-Host "Re-stamped build_flavor.txt ($deployed) so the guard still protects the next run." -ForegroundColor Yellow
    }
    exit 1
}
finally {
    # Report the pre-deploy log scan LAST (captured before the [3/5] wipe), so a dirty prior
    # session is the final thing on screen instead of buried under the deploy output. Prints on
    # both the success and the failure path. Non-blocking by design (LW-54): a report, not a gate.
    if ($null -ne $scanExit) {
        Write-Host "`n---- Previous session's log scan (LW-54) ----" -ForegroundColor Cyan
        switch ($scanExit) {
            0 { Write-Host "  CLEAN: no runtime errors in the session you just played." -ForegroundColor Green }
            2 { Write-Host "  No previous session log to scan (game not launched since the last deploy)." -ForegroundColor DarkGray }
            default {
                if ($scanFindings.Count -eq 0) {
                    # scan_logs.py --quiet prints nothing on a clean scan and ONLY [FAIL] lines on a
                    # real finding, so a nonzero exit with an EMPTY capture means the scanner itself
                    # crashed (a Python exception, a bad flag) rather than found a runtime error --
                    # its own message went to stderr, above this block, not into $scanFindings. LW-148:
                    # this used to get mislabeled as "RUNTIME ERRORS in the session you just played",
                    # which sends you hunting through gameplay for a bug that was actually in the
                    # scanner's own process.
                    Write-Host "  The log scanner itself failed (exit $scanExit); its error went to the console above." -ForegroundColor Red
                } else {
                    Write-Host "  RUNTIME ERRORS in the session you just played (its log is now wiped by this deploy):" -ForegroundColor Red
                    foreach ($ln in $scanFindings) { Write-Host "    $ln" -ForegroundColor Red }
                    Write-Host "  Report only, not a gate (LW-54). Investigate before you flip a VERIFY_LIVE row." -ForegroundColor Red
                }
            }
        }
    }
    Pop-Location
}
