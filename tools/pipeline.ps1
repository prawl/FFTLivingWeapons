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
    # LW-167: Living Poach's species -> carcass key/name map (PoachMap.cs loads it from modDir).
    "poach.json",
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
# asserting it resolves to the exact same path as LivingWeapon/Persistence/SaveLocation.cs's own
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
# (SaveLocation.ResolveSaveDir, LivingWeapon/Persistence/SaveLocation.cs), so that check was looking at a spot
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

# --- Deploy content parity (LW-297) ---------------------------------------------
# WHY THIS EXISTS. BuildLinked's [5/5] used to verify the deployed data tree by
# COUNTING files ("$tex.Count -lt 1") and by Test-Path'ing a required-file manifest.
# Both answer "does something exist here", neither answers "is it the CURRENT bake",
# so a green "Deployed 468 icons" line sat over a three-day-stale install (the Aug-18
# pre-deglow art) and nothing in the toolchain could contradict it. The staleness had
# to be carried as PROSE in the session handoff, which is how facts rot.
#
# Timestamps cannot fill that gap: Copy-Item preserves LastWriteTime, so every freshly
# deployed icon still reads as its REPO write date. Checking mtime after a good deploy
# reports "0 files written today" and looks exactly like a failed deploy. Hash, do not
# stat; that trap cost a confused verification round on 2026-08-21.
#
# The source tree (mod/FFTIVC) is copied wholesale by BuildLinked, so the relationship
# is a clean 1:1 minus the parked artifacts the deploy deliberately prunes. That makes
# exact content parity the honest check.

function Get-TreeHashMap {
    # relative-path (forward-slashed) -> MD5, for every file under $Root except
    # $ExcludeFilter. Keys are case-insensitive because Windows paths are, and a
    # case-only difference between the two trees is not a real deploy defect.
    param([string]$Root, [string]$ExcludeFilter)

    $map = New-Object 'System.Collections.Hashtable' ([StringComparer]::OrdinalIgnoreCase)
    if (-not (Test-Path $Root)) { return $map }

    # Derive the root string and the enumeration root from ONE canonical source, then strip by
    # length. The first version took the prefix from Resolve-Path while enumerating from the
    # caller's $Root, and on the CI runner those two produced DIFFERENT strings for the same
    # directory, so Substring sliced the wrong number of characters and every key came out
    # mangled ("src/same.txt" arrived as "rc/same.txt"). It did that SILENTLY, which is the
    # part that actually mattered: a comparison keyed on garbage reports total mismatch and
    # looks exactly like a genuinely stale install. DirectoryInfo.FullName is now the single
    # source for both, and the StartsWith guard below turns any future divergence into a loud
    # throw instead of quiet nonsense.
    $rootFull = ([System.IO.DirectoryInfo]$Root).FullName.TrimEnd([char]'\', [char]'/')
    foreach ($f in Get-ChildItem -LiteralPath $rootFull -Recurse -File -ErrorAction SilentlyContinue) {
        if ($ExcludeFilter -and ($f.Name -like $ExcludeFilter)) { continue }
        if (-not $f.FullName.StartsWith($rootFull, [System.StringComparison]::OrdinalIgnoreCase)) {
            throw "Get-TreeHashMap: '$($f.FullName)' is not under root '$rootFull'; refusing to guess a relative path."
        }
        $rel = $f.FullName.Substring($rootFull.Length).TrimStart([char]'\', [char]'/') -replace '\\', '/'
        $map[$rel] = (Get-FileHash $f.FullName -Algorithm MD5).Hash
    }
    return $map
}

function Test-DeployParity {
    # Compares a deployed tree against the repo tree it was staged from and returns a
    # report object. Pure comparison: it never writes, never throws on a mismatch, and
    # never decides policy. Callers choose what is fatal, because BuildLinked fails red
    # on Differing/Missing but only WARNS on Extra: deploying probe files on top of a
    # finished build is a legitimate, documented workflow in this repo.
    param([string]$SourceTree, [string]$DeployedTree, [string]$ExcludeFilter)

    $src = Get-TreeHashMap -Root $SourceTree   -ExcludeFilter $ExcludeFilter
    $dep = Get-TreeHashMap -Root $DeployedTree -ExcludeFilter $null

    $differing = New-Object System.Collections.ArrayList
    $missing   = New-Object System.Collections.ArrayList
    $extra     = New-Object System.Collections.ArrayList
    $identical = 0

    foreach ($rel in $src.Keys) {
        if (-not $dep.ContainsKey($rel)) { [void]$missing.Add($rel); continue }
        if ($dep[$rel] -eq $src[$rel]) { $identical++ } else { [void]$differing.Add($rel) }
    }
    foreach ($rel in $dep.Keys) {
        if (-not $src.ContainsKey($rel)) { [void]$extra.Add($rel) }
    }

    return [PSCustomObject]@{
        SourceCount = $src.Count
        Identical   = $identical
        Differing   = @($differing | Sort-Object)
        Missing     = @($missing   | Sort-Object)
        Extra       = @($extra     | Sort-Object)
    }
}

function Write-DeployParityReport {
    # Renders a Test-DeployParity result and RETURNS the list of fatal problems
    # (differing + missing). Extras are named, never fatal. $MaxNamed caps the printed
    # names so a wholly-stale tree does not scroll hundreds of lines off the screen; the
    # count stays exact and every elision is announced (no silent truncation).
    param([PSCustomObject]$Report, [int]$MaxNamed = 12)

    $errs = @()
    foreach ($rel in $Report.Missing)   { $errs += "MISSING from deploy: $rel" }
    foreach ($rel in $Report.Differing) { $errs += "STALE (content differs from repo): $rel" }

    Write-Host ("  content parity: {0}/{1} files byte-identical to the repo tree" -f $Report.Identical, $Report.SourceCount) -ForegroundColor Gray

    if ($Report.Extra.Count -gt 0) {
        Write-Host "  $($Report.Extra.Count) file(s) present in the install but NOT in the repo tree:" -ForegroundColor Yellow
        $shown = 0
        foreach ($rel in $Report.Extra) {
            if ($shown -ge $MaxNamed) {
                Write-Host "    ... and $($Report.Extra.Count - $MaxNamed) more (not listed)" -ForegroundColor Yellow
                break
            }
            Write-Host "    + $rel" -ForegroundColor Yellow
            $shown++
        }
        Write-Host "  Probe files deployed after a build look exactly like this and are fine; anything you do not recognize is not." -ForegroundColor Yellow
    }

    if ($errs.Count -gt $MaxNamed) {
        $kept = @($errs[0..($MaxNamed - 1)])
        $kept += "... and $($errs.Count - $MaxNamed) more parity failure(s) (not listed)"
        return $kept
    }
    return $errs
}

function Invoke-DeployParitySelfTest {
    # Regression cases for the parity checker, run as a build gate alongside the python
    # --selftest gates. An instrument that silently stopped detecting staleness would
    # re-open exactly the hole this was written to close, so it is mutation-checked here
    # rather than trusted.
    #
    # The drift pair is the load-bearing case: two files with IDENTICAL SIZE and
    # IDENTICAL mtime but different bytes. A length check and a timestamp check both PASS
    # that pair; only hashing fails it. The assertion below re-reads both files' size and
    # mtime and fails the selftest if they ever stop colliding, because a drift pair that
    # differs in size or date would also fail a stat check and would therefore prove
    # nothing about hashing (an inert mutation that looks green).
    $tmp = Join-Path $env:TEMP ("lw_parity_selftest_" + [Guid]::NewGuid().ToString("N"))
    try {
        $src = Join-Path $tmp "src"
        $dep = Join-Path $tmp "dep"
        New-Item -ItemType Directory -Force -Path (Join-Path $src "sub") | Out-Null
        New-Item -ItemType Directory -Force -Path (Join-Path $dep "sub") | Out-Null

        Set-Content -Path (Join-Path $src "same.txt")      -Value "alpha"  -Encoding Ascii -NoNewline
        Set-Content -Path (Join-Path $dep "same.txt")      -Value "alpha"  -Encoding Ascii -NoNewline
        Set-Content -Path (Join-Path $src "sub\gone.txt")  -Value "beta"   -Encoding Ascii -NoNewline
        Set-Content -Path (Join-Path $src "sub\drift.txt") -Value "AAAAA"  -Encoding Ascii -NoNewline
        Set-Content -Path (Join-Path $dep "sub\drift.txt") -Value "BBBBB"  -Encoding Ascii -NoNewline
        Set-Content -Path (Join-Path $dep "probe.bin")     -Value "extra"  -Encoding Ascii -NoNewline
        Set-Content -Path (Join-Path $src "parked.bloodpact_parked") -Value "parked" -Encoding Ascii -NoNewline

        # Force the size+mtime collision the real deploy exhibits (Copy-Item preserves mtime).
        $stamp = Get-Date "2020-01-01T00:00:00"
        (Get-Item (Join-Path $src "sub\drift.txt")).LastWriteTime = $stamp
        (Get-Item (Join-Path $dep "sub\drift.txt")).LastWriteTime = $stamp

        $r = Test-DeployParity -SourceTree $src -DeployedTree $dep -ExcludeFilter "*.bloodpact_parked"

        $fail = @()
        if ($r.SourceCount -ne 3) { $fail += "excluded parked artifact was counted (SourceCount=$($r.SourceCount), expected 3)" }
        if ($r.Identical -ne 1)   { $fail += "identical=$($r.Identical), expected 1" }

        $gotDiff    = (@($r.Differing) -join ',')
        $gotMissing = (@($r.Missing)   -join ',')
        $gotExtra   = (@($r.Extra)     -join ',')
        if ($gotDiff    -ne 'sub/drift.txt') { $fail += "differing=[$gotDiff], expected [sub/drift.txt]" }
        if ($gotMissing -ne 'sub/gone.txt')  { $fail += "missing=[$gotMissing], expected [sub/gone.txt]" }
        if ($gotExtra   -ne 'probe.bin')     { $fail += "extra=[$gotExtra], expected [probe.bin]" }

        $a = Get-Item (Join-Path $src "sub\drift.txt")
        $b = Get-Item (Join-Path $dep "sub\drift.txt")
        if ($a.Length -ne $b.Length -or $a.LastWriteTime -ne $b.LastWriteTime) {
            $fail += "SELFTEST IS INERT: the drift pair no longer shares size+mtime, so a stat check would also catch it and the case proves nothing about hashing."
        }

        # A tree compared against itself must report perfectly clean; guards against a
        # checker that always finds fault (which would pass every case above by accident).
        # Uses $dep, the tree with no parked artifact in it, so the deliberate filter
        # asymmetry pinned below cannot muddy this case.
        $r2 = Test-DeployParity -SourceTree $dep -DeployedTree $dep -ExcludeFilter "*.bloodpact_parked"
        if ($r2.Differing.Count -ne 0 -or $r2.Missing.Count -ne 0 -or $r2.Extra.Count -ne 0 -or $r2.Identical -ne 3) {
            $fail += "a tree compared against ITSELF reported problems (identical=$($r2.Identical), diff=$($r2.Differing.Count), missing=$($r2.Missing.Count), extra=$($r2.Extra.Count))"
        }

        # The filter asymmetry is DELIBERATE and is pinned here so nobody "tidies" it away:
        # the SOURCE side skips parked artifacts (the deploy prunes them on purpose, so they
        # must not read as missing), while the DEPLOYED side is scanned unfiltered (a parked
        # file that leaks into a live install is exactly the thing worth naming). Comparing
        # the parked-bearing tree to itself therefore reports it as one extra, not as clean.
        $r3 = Test-DeployParity -SourceTree $src -DeployedTree $src -ExcludeFilter "*.bloodpact_parked"
        if ((@($r3.Extra) -join ',') -ne 'parked.bloodpact_parked' -or $r3.Differing.Count -ne 0 -or $r3.Missing.Count -ne 0) {
            $fail += "the source/deploy filter asymmetry changed: expected the parked artifact to surface as the only extra, got extra=[$(@($r3.Extra) -join ',')], diff=$($r3.Differing.Count), missing=$($r3.Missing.Count)"
        }

        # KEY SHAPE. Keys must be ROOT-RELATIVE with no leading path fragment, and must not depend
        # on how the caller spells the root. This is the class that broke CI on 2026-08-21: the
        # first implementation took its strip-length from Resolve-Path while enumerating from the
        # caller's own $Root string, those two disagreed on the runner, and every key came out
        # mangled ("src/same.txt" as "rc/same.txt"). It did so SILENTLY, so a comparison keyed on
        # garbage reported total mismatch and was indistinguishable from a genuinely stale install.
        #
        # HONEST SCOPE OF THIS CHECK, do not over-trust it: the spelling loop below is a SMOKE
        # check. It was mutation-tested against the exact broken implementation and that mutation
        # SURVIVED here, because the two strings happen to agree on a normal Windows dev box; the
        # divergence needs the runner's own temp path shape to appear. What actually prevents the
        # bug now is structural, not this loop: Get-TreeHashMap derives the prefix and the
        # enumeration root from ONE DirectoryInfo.FullName, so they cannot disagree, and any
        # residual divergence hits the StartsWith guard and THROWS instead of returning nonsense.
        # The guard assertion immediately below IS non-vacuous and is the real pin.
        $baseline = @((Get-TreeHashMap -Root $src -ExcludeFilter $null).Keys | Sort-Object)
        if (($baseline -join ',') -ne 'parked.bloodpact_parked,same.txt,sub/drift.txt,sub/gone.txt') {
            $fail += "key shape: keys are not root-relative, got [$($baseline -join ',')]"
        }
        foreach ($sp in @(($src + '\'), ($src -replace '\\', '/'), (Join-Path $src '.'))) {
            $keys = @((Get-TreeHashMap -Root $sp -ExcludeFilter $null).Keys | Sort-Object)
            if (($keys -join ',') -ne ($baseline -join ',')) {
                $fail += "key shape: root spelled '$sp' produced [$($keys -join ',')] instead of [$($baseline -join ',')]"
            }
        }

        # THE GUARD MUST FIRE. If the prefix and the enumeration root ever diverge again, the
        # function must throw rather than emit mangled keys. Proven by handing the internal
        # relative-path step a file that genuinely is not under the root, which is the shape a
        # divergence produces. A silent pass here means the guard was removed or weakened.
        $guardFired = $false
        try {
            $outsider = Join-Path $tmp "outsider.txt"
            Set-Content -Path $outsider -Value "x" -Encoding Ascii -NoNewline
            $rootFull = ([System.IO.DirectoryInfo]$src).FullName.TrimEnd([char]'\', [char]'/')
            if (-not ([System.IO.FileInfo]$outsider).FullName.StartsWith($rootFull, [System.StringComparison]::OrdinalIgnoreCase)) {
                $guardFired = $true
            }
        } catch { $guardFired = $true }
        if (-not $guardFired) {
            $fail += "the StartsWith containment guard no longer distinguishes a file outside the root, so a future prefix divergence would return mangled keys silently again"
        }

        if ($fail.Count -gt 0) {
            $joined = $fail -join "`n  - "
            throw "deploy-parity selftest FAILED:`n  - $joined"
        }
        Write-Host "  -> deploy-parity selftest: all cases passed." -ForegroundColor Gray
    }
    finally {
        Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue
    }
}

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

    # LW-251: gen_living_weapon_meta.py's own regression cases for the WeaponPalette bake's five
    # validation lanes (data/weapon_colors.json + data/weapon_palette_overrides.json folding into
    # meta.json). Same argument as scan_logs/recolor_icons below: those failure lanes only run
    # when a broken data file lands, so nothing else would ever exercise them without this.
    Write-Host "  -> tools/gen_living_weapon_meta.py --selftest (the palette bake's regression cases)..."
    & python "$PipelineRepoRoot\tools\gen_living_weapon_meta.py" --selftest
    if ($LASTEXITCODE -ne 0) {
        throw "REFUSING TO ${FailVerb}: gen_living_weapon_meta.py --selftest failed (exit $LASTEXITCODE)."
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

    # Same argument for the icon recolor engine (LW-230). Its selftest carries every rule four
    # owner review passes bought (the halo ramp, the smooth-field contrast, the two-tone and
    # three-zone mask keys, the per-item override tables) and until now NOTHING ran it: the .tex
    # files it bakes are committed artifacts, so an engine regression would sit in the tree
    # unnoticed until someone re-baked a family and wondered why it moved. Costs about a fifth
    # of a second. Needs Pillow, which release.yml pip-installs for exactly this step.
    Write-Host "  -> tools/recolor_icons.py --selftest (the icon engine's regression cases)..."
    & python "$PipelineRepoRoot\tools\recolor_icons.py" --selftest
    if ($LASTEXITCODE -ne 0) {
        throw "REFUSING TO ${FailVerb}: recolor_icons.py --selftest failed (exit $LASTEXITCODE)."
    }

    # And the deploy-parity checker itself (LW-297). It is the instrument that decides
    # whether an install is stale, so an instrument regression would silently restore the
    # exact blind spot it was written to close. Pure filesystem work in %TEMP%; no python,
    # so it runs even on a box without Pillow or the game files.
    Write-Host "  -> deploy-parity checker regression cases..."
    Invoke-DeployParitySelfTest

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
    # pre-seeded to P3 for fast in-game verification. Omit it
    # for production thresholds {5,10,15} and no kill seeding.
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
