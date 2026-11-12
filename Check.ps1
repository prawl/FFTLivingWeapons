# Check.ps1 runs every gate BuildLinked.ps1 and Publish.ps1 run, with one exit code, for before
# every commit. It deploys nothing, packages nothing and never looks at the game, so it is safe
# while another program has the game. A hand built command chain can hide a red step behind a pipe.

$ErrorActionPreference = 'Stop'
. "$PSScriptRoot\tools\pipeline.ps1"

# Red until the last gate passes, so a throw or a stray exit can never read as green.
$code = 1
Push-Location $PSScriptRoot
try {
    Write-Host "[check] tables and their gates" -ForegroundColor Yellow
    Invoke-TablePipeline -FailVerb CHECK

    Write-Host "[check] shared gates" -ForegroundColor Yellow
    $gateTemp = Enter-GateTemp
    try {
        Invoke-ManifestGate -FailVerb CHECK
        Invoke-FormatGate -FailVerb CHECK
        Invoke-UnitTestGate -FailVerb CHECK
        Invoke-HealthGate -FailVerb CHECK
        Invoke-TempLeftoverGate -FailVerb CHECK -GateTemp $gateTemp
    }
    finally { Exit-GateTemp -State $gateTemp }

    $code = 0
    Write-Host "`n[check] every gate is green. Nothing was deployed." -ForegroundColor Green
}
catch {
    Write-Host "`n[check] RED. $($_.Exception.Message)" -ForegroundColor Red
}
finally {
    Pop-Location
}
exit $code
