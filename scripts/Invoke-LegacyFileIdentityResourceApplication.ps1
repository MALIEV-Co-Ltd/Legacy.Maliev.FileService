# Recording-only resource protocol. No command, cloud, or default provider adapter.
. (Join-Path $PSScriptRoot 'Get-LegacyFileIdentityPlan.ps1')

function Invoke-LegacyFileIdentityResourceApplication {
    [CmdletBinding()]
    param(
        [hashtable]$PlanArguments,
        [scriptblock]$RecordResource,
        [switch]$RecordingOnly
    )
    try {
        if (-not $RecordingOnly -or $null -eq $RecordResource -or $null -eq $PlanArguments) {
            throw 'An explicit recording boundary is required.'
        }
        # Build the plan through actual identity/IAM/source/metadata admission.
        # Never accept a caller-asserted prevalidated resource plan.
        $arguments = $PlanArguments.Clone()
        $guardPath = if ($arguments.ContainsKey('SourceGuardScriptPath')) { $arguments.SourceGuardScriptPath }
            else { Join-Path $PSScriptRoot '../.dependencies/Legacy.Maliev.Workflows/scripts/Assert-OfflineReleaseSource.ps1' }
        $modulePath = if ($arguments.ContainsKey('SourceGuardModulePath')) { $arguments.SourceGuardModulePath }
            else { Join-Path $PSScriptRoot '../.dependencies/Legacy.Maliev.Workflows/scripts/offline_release_source.py' }
        $sourceArguments = @{
            RepositoryRoot = $arguments.SourceCheckoutPath
            ExpectedSourceCommit = $arguments.SourceCommit
            ApprovedSourceRepository = $arguments.ApprovedSourceRepository
            AllowFixtureOrigin = [bool]$arguments.AllowFixtureOrigin
            SourceGuardModulePath = $modulePath
        }
        $plan = New-LegacyFileIdentityPlan @arguments *>&1
        Assert-LegacyFileSourceGuardProducer -ScriptPath $guardPath -ModulePath $modulePath
        . $guardPath
        if ($plan -isnot [pscustomobject] -or $plan.Resources.Count -ne 2 -or $plan.Resources[0].kind -cne 'ServiceAccount' -or
            $plan.Resources[1].kind -cne 'Deployment' -or $plan.DeploymentAllowed -or $plan.LiveAccepted) {
            throw 'The admitted resource plan is invalid.'
        }
        $recorded = [Collections.Generic.List[string]]::new()
        foreach ($resource in $plan.Resources) {
            Assert-LegacyFileSourceGuardProducer -ScriptPath $guardPath -ModulePath $modulePath
            $null = Assert-OfflineReleaseSource @sourceArguments
            # JSON is an immutable copy; callback mutation cannot alter the plan.
            $resourceJson = $resource | ConvertTo-Json -Depth 30 -Compress
            $acknowledgement = & $RecordResource -ResourceJson $resourceJson *>&1
            if ($acknowledgement -isnot [hashtable] -or $acknowledgement.Count -ne 4 -or
                $acknowledgement.Kind -isnot [string] -or $acknowledgement.Name -isnot [string] -or
                $acknowledgement.Namespace -isnot [string] -or
                $acknowledgement.Kind -cne $resource.kind -or
                $acknowledgement.Name -cne $resource.metadata.name -or
                $acknowledgement.Namespace -cne $resource.metadata.namespace -or
                $acknowledgement.Succeeded -isnot [bool] -or -not $acknowledgement.Succeeded) {
                throw 'The resource recording was rejected or unconfirmed.'
            }
            $recorded.Add($resource.kind)
        }
        Assert-LegacyFileSourceGuardProducer -ScriptPath $guardPath -ModulePath $modulePath
        $null = Assert-OfflineReleaseSource @sourceArguments
        return [pscustomobject]@{
            SchemaVersion = 'legacy-file-resource-application-recording/v1'
            Mode = 'recording-only'
            SourceCommit = $plan.SourceCommit
            RecordedResources = $recorded.ToArray()
            Resources = $plan.Resources
            DeploymentAllowed = $false
            LiveAccepted = $false
            SnapshotAuthenticityAccepted = $false
        }
    } catch {
        throw 'Legacy File resource application recording failed; details withheld.'
    }
}
