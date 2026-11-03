# gates/pipeline/gates.ps1
# The shared gate functions every mod runs before a deploy or a package. A mod's tools/pipeline.ps1
# dot-sources this file and keeps its own gates below it. Where things are comes from rules.json at
# the repo root. Runs under Windows PowerShell 5.1 and PowerShell 7.
#
# A caller that is not a mod (this repo's own Check.ps1) sets $GatesRepoRoot before dot-sourcing;
# a mod leaves it unset and the root is the folder the gates folder sits in.

$GatesDir = Split-Path -Parent $PSScriptRoot
if (-not (Test-Path variable:GatesRepoRoot)) {
    $GatesRepoRoot = Split-Path -Parent $GatesDir
}
$GatesVersion = (Get-Content -LiteralPath (Join-Path $GatesDir 'VERSION') -Raw).Trim()

$rulesPath = Join-Path $GatesRepoRoot 'rules.json'
if (-not (Test-Path -LiteralPath $rulesPath)) {
    throw "No rules.json at $GatesRepoRoot. The shared gates need one; gates/README.md shows the shape."
}
$Rules = Get-Content -LiteralPath $rulesPath -Raw | ConvertFrom-Json
foreach ($key in 'prod', 'tests', 'ticketPrefix', 'projects', 'testProjects') {
    if (-not $Rules.PSObject.Properties[$key]) {
        throw "rules.json lacks the $key key."
    }
}

function Get-Rule {
    # A rules.json key with a default for a repo that leaves it out.
    param([string]$Name, $Default)

    $prop = $Rules.PSObject.Properties[$Name]
    if ($prop) { return $prop.Value }
    return $Default
}

function Get-Confession {
    # The row a refused gate asks for. It names the next free ledger id, so the paper trail of
    # every rule that got in the way piles up in one place.
    $prefix = $Rules.ticketPrefix
    $ids = @()
    foreach ($name in 'docs\TODO.md', 'docs\CHANGELOG.md') {
        $path = Join-Path $GatesRepoRoot $name
        if (Test-Path -LiteralPath $path) {
            $ids += [regex]::Matches((Get-Content -LiteralPath $path -Raw), "\[$prefix-(\d+)\]") | ForEach-Object { [int]$_.Groups[1].Value }
        }
    }
    $next = 1
    if ($ids.Count -gt 0) { $next = ($ids | Measure-Object -Maximum).Maximum + 1 }
    $date = Get-Date -Format 'yyyy-MM-dd'
    return "If the rule is wrong here, paste this row under Backlog in docs/TODO.md and say why: - [$prefix-$next] ${date}: Wanted to bend <rule>, because <why>."
}

function Invoke-ManifestGate {
    # The copy of the rulebook must match what was published, file for file. Runs first and names
    # the rulebook version, so the build log says which rules it ran under. There is no override; a
    # fix to the rules goes upstream, and the confession row records that it had to. Loading this
    # file prints nothing, since a mod's tests may dot-source it and read the output.
    param(
        [Parameter(Mandatory = $true)][ValidateSet('DEPLOY', 'PACKAGE', 'CHECK')]
        [string]$FailVerb
    )

    Write-Host "[gates] shared rules $GatesVersion, repo $GatesRepoRoot"
    & python (Join-Path $GatesDir 'tools\manifest.py') verify --dir $GatesDir
    if ($LASTEXITCODE -ne 0) {
        throw "REFUSING TO ${FailVerb}: the rulebook copy under $GatesDir does not match its fingerprint list (see above). $(Get-Confession)"
    }
}

function Get-RatchetBase {
    # The second ref the health ratchet judges against. CI passes the commit a push started
    # from; a local run uses the main branch on the remote when it is known.
    if ($env:GATES_BASE_SHA -and $env:GATES_BASE_SHA -notmatch '^0+$') { return $env:GATES_BASE_SHA }
    & git -C $GatesRepoRoot rev-parse --verify --quiet origin/main 2>$null | Out-Null
    if ($LASTEXITCODE -eq 0) { return 'origin/main' }
    return $null
}

function Invoke-FormatGate {
    # Style gate. dotnet format in verify mode on every project, whitespace and the .editorconfig
    # rules only. It rewrites nothing; run "dotnet format" by hand to fix a red.
    param(
        [Parameter(Mandatory = $true)][ValidateSet('DEPLOY', 'PACKAGE', 'CHECK')]
        [string]$FailVerb
    )

    foreach ($proj in @($Rules.projects)) {
        & dotnet format (Join-Path $GatesRepoRoot $proj) --verify-no-changes -v q
        if ($LASTEXITCODE -ne 0) {
            throw "REFUSING TO ${FailVerb}: $proj is not formatted (run: dotnet format $proj)."
        }
    }
}

function Invoke-UnitTestGate {
    param(
        [Parameter(Mandatory = $true)][ValidateSet('DEPLOY', 'PACKAGE', 'CHECK')]
        [string]$FailVerb
    )

    foreach ($proj in @($Rules.testProjects)) {
        & dotnet test (Join-Path $GatesRepoRoot $proj) --nologo -v q
        if ($LASTEXITCODE -ne 0) {
            throw "REFUSING TO ${FailVerb}: unit tests failed in $proj (see above)."
        }
    }
}

function Invoke-HealthGate {
    # The code health gate. The selftest and the ratchet are real gates: a build that makes a health
    # number worse than the best committed record is refused like a red test. The status line only
    # reminds about missing or uncommitted records.
    param(
        [Parameter(Mandatory = $true)][ValidateSet('DEPLOY', 'PACKAGE', 'CHECK')]
        [string]$FailVerb
    )

    $health = Join-Path $GatesDir 'tools\health.py'
    & python $health --selftest
    if ($LASTEXITCODE -ne 0) {
        throw "REFUSING TO ${FailVerb}: health.py --selftest failed (exit $LASTEXITCODE)."
    }
    $ratchetArgs = @('--repo', $GatesRepoRoot, 'ratchet')
    $base = Get-RatchetBase
    if ($base) { $ratchetArgs += @('--base', $base) }
    & python $health @ratchetArgs
    if ($LASTEXITCODE -ne 0) {
        throw "REFUSING TO ${FailVerb}: the code health ratchet found a measure worse than the best record (see above)."
    }
    $statusArgs = @('status')
    if ($env:GITHUB_ACTIONS) { $statusArgs += '--committed' }
    & python $health --repo $GatesRepoRoot @statusArgs
}

# The compiler server and MSBuild's kept nodes outlive a build and would hold files in the gates'
# folder, so the gates compile without them.
$GateVariables = @('TEMP', 'TMP', 'TMPDIR', 'UseSharedCompilation', 'MSBUILDDISABLENODEREUSE')

function Enter-GateTemp {
    # The gates run with a temp folder of their own, so a folder another program makes in the
    # shared one meanwhile is never counted against them. A killed run's folder goes a day later.
    $shared = [IO.Path]::GetTempPath()
    Get-ChildItem -LiteralPath $shared -Directory -Filter 'gates_*' -ErrorAction SilentlyContinue |
        Where-Object { $_.LastWriteTime -lt (Get-Date).AddDays(-1) } |
        Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
    $dir = Join-Path $shared ('gates_' + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $dir | Out-Null
    $state = @{ Dir = $dir }
    foreach ($name in $GateVariables) { $state[$name] = [Environment]::GetEnvironmentVariable($name, 'Process') }
    foreach ($name in 'TEMP', 'TMP', 'TMPDIR') { [Environment]::SetEnvironmentVariable($name, $dir, 'Process') }
    [Environment]::SetEnvironmentVariable('UseSharedCompilation', 'false', 'Process')
    [Environment]::SetEnvironmentVariable('MSBUILDDISABLENODEREUSE', '1', 'Process')
    return $state
}

function Exit-GateTemp {
    # Safe to call twice; the second call finds nothing left to undo.
    param([hashtable]$State)

    if (-not $State -or -not $State.Dir) { return }
    foreach ($name in $GateVariables) { [Environment]::SetEnvironmentVariable($name, $State[$name], 'Process') }
    Remove-Item -LiteralPath $State.Dir -Recurse -Force -ErrorAction SilentlyContinue
    if (Test-Path -LiteralPath $State.Dir) {
        Write-Warning "The gates' temp folder $($State.Dir) could not be removed; the next run sweeps it a day from now."
    }
    $State.Dir = $null
}

function Invoke-TempLeftoverGate {
    # Nothing outside the gates writes to their folder, so a folder there under one of the repo's
    # temp prefixes is a test or tool that did not clean up, refused before such folders pile up.
    # rules.json names the prefixes (tempPrefixes) and any fixed folder a test rebuilds (tempKeep).
    param(
        [Parameter(Mandatory = $true)][ValidateSet('DEPLOY', 'PACKAGE', 'CHECK')]
        [string]$FailVerb,
        [Parameter(Mandatory = $true)]
        [hashtable]$GateTemp
    )

    $prefixes = @(Get-Rule 'tempPrefixes' @())
    $keep = @(Get-Rule 'tempKeep' @())
    $left = @(Get-ChildItem -LiteralPath $GateTemp.Dir -Directory -ErrorAction SilentlyContinue | ForEach-Object { $_.Name } |
        Where-Object { $n = $_; ($keep -notcontains $n) -and @($prefixes | Where-Object { $n.StartsWith($_) }).Count -gt 0 })
    if ($left.Count -gt 0) {
        throw "REFUSING TO ${FailVerb}: the gates left $($left.Count) folder(s) in their temp folder: $($left -join ', ')."
    }
}
