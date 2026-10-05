[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$DeploymentJson,
    [Parameter(Mandatory)][string]$ServiceAccountJson
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '../../scripts/Get-LegacyFileIdentityPlan.ps1')
try {
    $null = Assert-LegacyFileIdentityIntent -DeploymentJson $DeploymentJson -ServiceAccountJson $ServiceAccountJson
} catch {
    if ($_.Exception.Message -cne 'Legacy File service-account identity or token intent is invalid.') { throw }
    [Console]::Out.Write('BLOCKED:existing-token-intent')
    exit 0
}
throw 'The existing token intent unexpectedly passed identity admission.'
