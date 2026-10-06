param([Parameter(Mandatory)][string]$Scenario)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '../../scripts/Invoke-LegacyFileIdentityResourceApplication.ps1')
. (Join-Path $PSScriptRoot 'FileReleaseSourceFixture.ps1')
$fixture = New-FileReleaseSourceFixture
$calls = [Collections.Generic.List[string]]::new()
$payloads = [Collections.Generic.List[string]]::new()
$deployment = @{
    apiVersion = 'apps/v1'; kind = 'Deployment'; metadata = @{ name = 'legacy-maliev-file'; namespace = 'maliev-legacy' }
    spec = @{ replicas = 1; template = @{ spec = @{ serviceAccountName = 'legacy-maliev-file'; automountServiceAccountToken = $true
        containers = @(@{ name = 'legacy-maliev-file-service'; image = 'example.invalid/file@sha256:' + ('a' * 64); env = @(@{ name = 'RECORDING_SETTING'; value = 'preserve' }) })
    } } }
}
$account = @{
    apiVersion = 'v1'; kind = 'ServiceAccount'; automountServiceAccountToken = $true
    metadata = @{ name = 'legacy-maliev-file'; namespace = 'maliev-legacy'; annotations = @{ 'iam.gke.io/gcp-service-account' = 'legacy-maliev-file@maliev-website.iam.gserviceaccount.com' } }
}
$gsa = 'serviceAccount:legacy-maliev-file@maliev-website.iam.gserviceaccount.com'
$ksa = 'serviceAccount:maliev-website.svc.id.goog[maliev-legacy/legacy-maliev-file]'
$snapshot = @{
    project = 'maliev-website'; bucketUri = 'gs://recording-file-bucket'; googleServiceAccount = 'legacy-maliev-file@maliev-website.iam.gserviceaccount.com'
    bucketPolicy = @{ bindings = @(@{ role = 'roles/storage.objectUser'; members = @($gsa) }) }
    serviceAccountPolicy = @{ bindings = @(@{ role = 'roles/iam.workloadIdentityUser'; members = @($ksa) }, @{ role = 'roles/iam.serviceAccountTokenCreator'; members = @($gsa) }) }
}
$recordingOnly = $true
switch -CaseSensitive ($Scenario) {
    'success-explicit' { }
    'success-inherited' { $deployment.spec.template.spec.Remove('automountServiceAccountToken') }
    'callback-copy' { }
    'no-recording' { $recordingOnly = $false }
    'no-adapter' { }
    'pod-false' { $deployment.spec.template.spec.automountServiceAccountToken = $false }
    'account-false' { $account.automountServiceAccountToken = $false }
    'iam-rejected' { $snapshot.bucketPolicy.bindings = @() }
    'iam-warning' { }
    'image-fails' { }
    'image-warning' { }
    'source-before-dirty' { [IO.File]::WriteAllText((Join-Path $fixture.Checkout 'source.txt'), 'private fixture source') }
    'ksa-rejected' { }
    'ksa-throws' { }
    'ksa-unknown' { }
    'ksa-wrong-identity' { }
    'ksa-string-status' { }
    'ksa-multiple' { }
    'ksa-warning' { }
    'ksa-error' { }
    'deployment-rejected' { }
    'deployment-throws' { }
    'source-after-ksa-dirty' { }
    'source-after-ksa-remote' { }
    'producer-after-ksa-drift' { }
    'module-after-ksa-drift' { }
    'source-after-deployment-dirty' { }
    'plan-arguments-invalid' { }
    default { throw 'Unknown resource application scenario.' }
}
$reader = { $calls.Add('iam-read'); if ($Scenario -eq 'iam-warning') { Write-Warning 'private fixture IAM warning' }; $snapshot | ConvertTo-Json -Depth 30 -Compress }
$builder = {
    $calls.Add('image-build')
    if ($Scenario -eq 'image-fails') { throw 'private fixture image fault' }
    if ($Scenario -eq 'image-warning') { Write-Warning 'private fixture image warning' }
    return @{ image = 'asia-southeast1-docker.pkg.dev/maliev-website/recording/legacy-maliev-file-service@sha256:' + ('b' * 64); sourceCommit = $fixture.SourceCommit; approved = $true }
}
$originalDeployment = $deployment | ConvertTo-Json -Depth 30 -Compress
$originalAccount = $account | ConvertTo-Json -Depth 30 -Compress
$arguments = @{
    DeploymentJson = $originalDeployment; ServiceAccountJson = $originalAccount; ExpectedBucketUri = 'gs://recording-file-bucket'
    SourceCommit = $fixture.SourceCommit; SourceCheckoutPath = $fixture.Checkout; ApprovedSourceRepository = $fixture.OriginUri; AllowFixtureOrigin = $true
    ApprovedImageRepository = 'asia-southeast1-docker.pkg.dev/maliev-website/recording/legacy-maliev-file-service'; ReadIamSnapshot = $reader; BuildImage = $builder
}
if ($Scenario -eq 'plan-arguments-invalid') { $arguments.UnapprovedParameter = 'private fixture parameter' }
$tempProducer = $null
if ($Scenario -in @('producer-after-ksa-drift', 'module-after-ksa-drift')) {
    $extension = if ($Scenario -eq 'producer-after-ksa-drift') { '.ps1' } else { '.py' }
    $tempProducer = Join-Path ([IO.Path]::GetTempPath()) ([Guid]::NewGuid().ToString('N') + $extension)
    $sourceFile = if ($Scenario -eq 'producer-after-ksa-drift') { '../../.dependencies/Legacy.Maliev.Workflows/scripts/Assert-OfflineReleaseSource.ps1' }
        else { '../../.dependencies/Legacy.Maliev.Workflows/scripts/offline_release_source.py' }
    [IO.File]::Copy((Join-Path $PSScriptRoot $sourceFile), $tempProducer, $true)
    if ($Scenario -eq 'producer-after-ksa-drift') { $arguments.SourceGuardScriptPath = $tempProducer }
    else { $arguments.SourceGuardModulePath = $tempProducer }
}
$adapter = {
    param([string]$ResourceJson)
    $resource = ConvertFrom-LegacyFilePlanJson -Json $ResourceJson
    $kind = $resource.kind
    $calls.Add($kind)
    $payloads.Add($ResourceJson)
    $acknowledgement = @{ Kind = $kind; Name = $resource.metadata.name; Namespace = $resource.metadata.namespace; Succeeded = $true }
    if ($kind -ceq 'ServiceAccount') {
        switch -CaseSensitive ($Scenario) {
            'ksa-rejected' { $acknowledgement.Succeeded = $false }
            'ksa-throws' { throw 'private fixture KSA fault' }
            'ksa-unknown' { return @{ Status = 'Unknown'; Detail = 'private fixture KSA detail' } }
            'ksa-wrong-identity' { $acknowledgement.Name = 'other' }
            'ksa-string-status' { $acknowledgement.Succeeded = 'true' }
            'ksa-multiple' { return @($acknowledgement, $acknowledgement) }
            'ksa-warning' { Write-Warning 'private fixture KSA warning' }
            'ksa-error' { Write-Error 'private fixture KSA error' -ErrorAction Continue }
            'source-after-ksa-dirty' { [IO.File]::WriteAllText((Join-Path $fixture.Checkout 'source.txt'), 'private fixture KSA source') }
            'source-after-ksa-remote' { Move-FileFixtureRemote -Fixture $fixture }
            'producer-after-ksa-drift' { [IO.File]::WriteAllText($tempProducer, "throw 'private fixture producer ran'") }
            'module-after-ksa-drift' { [IO.File]::WriteAllText($tempProducer, "raise RuntimeError('private fixture module ran')") }
        }
    } else {
        if ($Scenario -eq 'deployment-rejected') { $acknowledgement.Succeeded = $false }
        if ($Scenario -eq 'deployment-throws') { throw 'private fixture Deployment fault' }
        if ($Scenario -eq 'source-after-deployment-dirty') { [IO.File]::WriteAllText((Join-Path $fixture.Checkout 'source.txt'), 'private fixture Deployment source') }
    }
    if ($Scenario -eq 'callback-copy') {
        $resource.metadata.name = 'callback-local-change'
        $arguments.DeploymentJson = '{}'
        $arguments.SourceCommit = 'c' * 40
    }
    return $acknowledgement
}
if ($Scenario -eq 'no-adapter') { $adapter = $null }
$failed = $false
try {
    $result = Invoke-LegacyFileIdentityResourceApplication -PlanArguments $arguments -RecordResource $adapter -RecordingOnly:$recordingOnly
    if ($result.SchemaVersion -cne 'legacy-file-resource-application-recording/v1' -or $result.Mode -cne 'recording-only' -or
        $result.DeploymentAllowed -or $result.LiveAccepted -or $result.SnapshotAuthenticityAccepted -or
        ($result.RecordedResources -join ',') -cne 'ServiceAccount,Deployment' -or $result.SourceCommit -cne $fixture.SourceCommit) {
        throw 'Recording protocol activated or misreported its boundary.'
    }
    $expectedDeployment = ConvertFrom-LegacyFilePlanJson -Json $originalDeployment
    $expectedDeployment.spec.template.spec.containers[0].image = 'asia-southeast1-docker.pkg.dev/maliev-website/recording/legacy-maliev-file-service@sha256:' + ('b' * 64)
    if (($result.Resources[1] | ConvertTo-Json -Depth 30 -Compress) -cne ($expectedDeployment | ConvertTo-Json -Depth 30 -Compress) -or
        ($result.Resources[0] | ConvertTo-Json -Depth 30 -Compress) -cne $originalAccount -or
        ($deployment | ConvertTo-Json -Depth 30 -Compress) -cne $originalDeployment -or
        $payloads.Count -ne 2 -or $payloads[0] -cne $originalAccount -or
        $payloads[1] -cne ($expectedDeployment | ConvertTo-Json -Depth 30 -Compress)) { throw 'Resource input or recorded payload mutated.' }
} catch {
    $failed = $true
    if ($_.Exception.Message -cne 'Legacy File resource application recording failed; details withheld.') { throw 'The resource boundary leaked or misclassified a failure.' }
} finally { if ($tempProducer) { [IO.File]::Delete($tempProducer) }; Remove-FileReleaseSourceFixture -Root $fixture.Root }
$accepted = $Scenario -in @('success-explicit', 'success-inherited', 'callback-copy')
if ($accepted -eq $failed) { throw 'Resource application scenario returned an unexpected outcome.' }
$expected = if ($Scenario -in @('no-recording', 'no-adapter', 'pod-false', 'account-false', 'source-before-dirty', 'plan-arguments-invalid')) { '' }
    elseif ($Scenario -in @('iam-rejected', 'iam-warning')) { 'iam-read' }
    elseif ($Scenario -in @('image-fails', 'image-warning')) { 'iam-read,image-build' }
    elseif ($Scenario.StartsWith('ksa-', [StringComparison]::Ordinal) -or $Scenario -in @('source-after-ksa-dirty', 'source-after-ksa-remote', 'producer-after-ksa-drift', 'module-after-ksa-drift')) { 'iam-read,image-build,ServiceAccount' }
    else { 'iam-read,image-build,ServiceAccount,Deployment' }
if (($calls -join ',') -cne $expected) { throw 'Resource failure did not stop ordered callbacks.' }
Write-Output ('PASS:' + $Scenario)
