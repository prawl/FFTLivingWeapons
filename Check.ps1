# Check.ps1 runs the shared gates on this repo, the same way a mod's BuildLinked.ps1 runs them on
# a mod. Green here means the package passes its own rules.
$ErrorActionPreference = 'Stop'
$GatesRepoRoot = $PSScriptRoot
. "$PSScriptRoot\pipeline\gates.ps1"

$gateTemp = Enter-GateTemp
try {
    Invoke-ManifestGate -FailVerb CHECK
    Invoke-FormatGate -FailVerb CHECK
    Invoke-UnitTestGate -FailVerb CHECK
    Invoke-HealthGate -FailVerb CHECK
    Invoke-TempLeftoverGate -FailVerb CHECK -GateTemp $gateTemp
}
finally {
    Exit-GateTemp -State $gateTemp
}
Write-Host "[check] every gate is green."
