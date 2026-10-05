param(
    [Parameter(Mandatory)][string]$Scenario,
    [string]$ImageOnlyPlanScriptPath = (Join-Path $PSScriptRoot "../../.dependencies/Legacy.Maliev.Workflows/scripts/New-OfflineImageOnlyDeploymentPlan.ps1"),
    [string]$SourceGuardScriptPath = (Join-Path $PSScriptRoot "../../.dependencies/Legacy.Maliev.Workflows/scripts/Assert-OfflineReleaseSource.ps1"),
    [string]$SourceGuardModulePath = (Join-Path $PSScriptRoot "../../.dependencies/Legacy.Maliev.Workflows/scripts/offline_release_source.py")
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '../../scripts/Get-LegacyFileIdentityPlan.ps1')
$deployment = @{
    apiVersion = 'apps/v1'; kind = 'Deployment'; metadata = @{ name = 'legacy-maliev-file'; namespace = 'maliev-legacy'; annotations = @{ recording = 'preserve'; timestamp = '2026-10-05T01:02:03.0000+07:00' }; resourceVersion = '17' }
    spec = @{ replicas = 1; selector = @{ matchLabels = @{ 'app.kubernetes.io/name' = 'legacy-maliev-file' } }; template = @{ metadata = @{ labels = @{ 'app.kubernetes.io/name' = 'legacy-maliev-file' } }; spec = @{ serviceAccountName = 'legacy-maliev-file'; containers = @(@{ name = 'legacy-maliev-file-service'; image = 'example.invalid/legacy-maliev-file-service@sha256:' + ('a' * 64); env = @(@{ name = 'RECORDING_SETTING'; value = 'preserve' }); resources = @{ requests = @{ cpu = '100m'; memory = '128Mi' } } }, @{ name = 'recording-sidecar'; image = 'example.invalid/recording-sidecar@sha256:' + ('c' * 64) }) } } }
}
$account = @{
    apiVersion = 'v1'; kind = 'ServiceAccount'; metadata = @{ name = 'legacy-maliev-file'; namespace = 'maliev-legacy'; annotations = @{ 'iam.gke.io/gcp-service-account' = 'legacy-maliev-file@maliev-website.iam.gserviceaccount.com'; timestamp = '2026-10-05T01:02:03.0000+07:00' } }
    automountServiceAccountToken = $true
}
$gsa = 'serviceAccount:legacy-maliev-file@maliev-website.iam.gserviceaccount.com'
$ksa = 'serviceAccount:maliev-website.svc.id.goog[maliev-legacy/legacy-maliev-file]'
$snapshot = @{
    project = 'maliev-website'; bucketUri = 'gs://recording-file-bucket'; googleServiceAccount = 'legacy-maliev-file@maliev-website.iam.gserviceaccount.com'
    bucketPolicy = @{ bindings = @(@{ role = 'roles/storage.objectUser'; members = @($gsa) }) }
    serviceAccountPolicy = @{ bindings = @(@{ role = 'roles/iam.workloadIdentityUser'; members = @($ksa) }, @{ role = 'roles/iam.serviceAccountTokenCreator'; members = @($gsa) }) }
}
. (Join-Path $PSScriptRoot 'FileReleaseSourceFixture.ps1')
$plan = $Scenario.StartsWith('plan-', [StringComparison]::Ordinal)
$intent = $Scenario.StartsWith('intent-', [StringComparison]::Ordinal)
$fixture = if ($plan) { New-FileReleaseSourceFixture } else { $null }
$expectedSourceCommit = if ($fixture) { $fixture.SourceCommit } else { $null }
$expectedOrigin = if ($fixture) { $fixture.OriginUri } else { $null }
$fixtureOptIn = $true
$accepted = $Scenario -in @('intent-valid', 'iam-valid', 'plan-valid')
switch -CaseSensitive ($Scenario) {
    'intent-valid' { }
    'intent-namespace' { $deployment.metadata.namespace = 'maliev' }
    'intent-deployment' { $deployment.metadata.name = 'maliev-file-service' }
    'intent-container' { $deployment.spec.template.spec.containers[0].name = 'maliev-file-service' }
    'intent-ksa' { $deployment.spec.template.spec.serviceAccountName = 'default' }
    'intent-account-namespace' { $account.metadata.namespace = 'maliev' }
    'intent-account-name' { $account.metadata.name = 'maliev-file-service' }
    'intent-gsa' { $account.metadata.annotations['iam.gke.io/gcp-service-account'] = 'other@example.invalid' }
    'intent-token-false' { $account.automountServiceAccountToken = $false }
    'intent-token-string' { $account.automountServiceAccountToken = 'true' }
    'iam-valid' { }
    'iam-project' { $snapshot.project = 'other-project' }
    'iam-bucket' { $snapshot.bucketUri = 'gs://other-bucket' }
    'iam-gsa' { $snapshot.googleServiceAccount = 'other@example.invalid' }
    'iam-missing-object' { $snapshot.bucketPolicy.bindings = @() }
    'iam-missing-wif' { $snapshot.serviceAccountPolicy.bindings = @($snapshot.serviceAccountPolicy.bindings[1]) }
    'iam-missing-signing' { $snapshot.serviceAccountPolicy.bindings = @($snapshot.serviceAccountPolicy.bindings[0]) }
    'iam-conditional-object' { $snapshot.bucketPolicy.bindings[0].condition = @{ expression = 'true' } }
    'iam-conditional-wif' { $snapshot.serviceAccountPolicy.bindings[0].condition = @{ expression = 'true' } }
    'iam-conditional-signing' { $snapshot.serviceAccountPolicy.bindings[1].condition = @{ expression = 'true' } }
    'iam-storage-admin' { $snapshot.bucketPolicy.bindings += @{ role = 'roles/storage.admin'; members = @($gsa) } }
    'iam-owner' { $snapshot.serviceAccountPolicy.bindings += @{ role = 'roles/owner'; members = @($gsa) } }
    'iam-editor' { $snapshot.serviceAccountPolicy.bindings += @{ role = 'roles/editor'; members = @($ksa) } }
    'iam-member-case' { $snapshot.bucketPolicy.bindings[0].members = @($gsa.ToUpperInvariant()) }
    'iam-duplicate-binding' { $snapshot.bucketPolicy.bindings += $snapshot.bucketPolicy.bindings[0] }
    'iam-public' { $snapshot.bucketPolicy.bindings[0].members += 'allUsers' }
    'iam-cross-bucket' { $snapshot.bucketPolicy.bindings[0].members += $ksa }
    'iam-cross-signing' { $snapshot.serviceAccountPolicy.bindings[1].members += $ksa }
    'iam-cross-wif' { $snapshot.serviceAccountPolicy.bindings[0].members += $gsa }
    'plan-valid' { }
    'plan-fixture-without-opt-in' { $fixtureOptIn = $false }
    'plan-production-origin-mismatch' { $fixtureOptIn = $false; $expectedOrigin = 'https://github.com/MALIEV-Co-Ltd/Legacy.Maliev.DocumentService.git' }
    'plan-source-dirty-tracked' { [IO.File]::WriteAllText((Join-Path $fixture.Checkout 'source.txt'), 'changed') }
    'plan-source-dirty-untracked' { [IO.File]::WriteAllText((Join-Path $fixture.Checkout 'new.txt'), 'untracked') }
    'plan-source-dirty-staged' { [IO.File]::WriteAllText((Join-Path $fixture.Checkout 'source.txt'), 'changed'); $null = Invoke-FileFixtureGit -Root $fixture.Checkout -Arguments @('add', '--', 'source.txt') }
    'plan-source-wrong-head' { $expectedSourceCommit = 'a' * 40 }
    'plan-source-origin' { $null = Invoke-FileFixtureGit -Root $fixture.Checkout -Arguments @('remote', 'set-url', 'origin', 'https://example.invalid/private-origin') }
    'plan-source-stale-remote' { Move-FileFixtureRemote -Fixture $fixture }
    'plan-reader-source-dirty' { }
    'plan-reader-source-remote' { }
    'plan-builder-source-dirty' { }
    'plan-builder-source-remote' { }
    'plan-source-helper-missing' { $SourceGuardScriptPath = Join-Path $fixture.Root 'missing.ps1' }
    'plan-source-helper-changed' { $tempSourceGuard = [IO.Path]::GetTempFileName(); [IO.File]::WriteAllText($tempSourceGuard, "throw 'private guard ran'"); $SourceGuardScriptPath = $tempSourceGuard }
    'plan-source-module-changed' { $tempSourceModule = [IO.Path]::GetTempFileName(); [IO.File]::WriteAllText($tempSourceModule, "raise RuntimeError('private module ran')"); $SourceGuardModulePath = $tempSourceModule }

    'plan-duplicate-json' { }
    'plan-duplicate-iam-json' { }
    'plan-helper-missing' { $ImageOnlyPlanScriptPath = Join-Path ([IO.Path]::GetTempPath()) ([Guid]::NewGuid().ToString('N') + '.ps1') }
    'plan-helper-changed' { $tempHelperPath = [IO.Path]::GetTempFileName(); [IO.File]::WriteAllText($tempHelperPath, "throw 'private producer ran'"); $ImageOnlyPlanScriptPath = $tempHelperPath }
    'plan-identity-invalid' { $deployment.spec.template.spec.serviceAccountName = 'default' }
    'plan-reader-fails' { }
    'plan-policy-invalid' { $snapshot.bucketPolicy.bindings = @() }
    'plan-builder-fails' { }
    'plan-image-invalid' { }
    'plan-source-invalid' { }
    'plan-unapproved' { }
    default { throw 'Unknown contract scenario.' }
}
$calls = [System.Collections.Generic.List[string]]::new()
$failed = $false
try {
    $result = if ($plan) {
        $reader = {
            $calls.Add('iam-read')
            if ($Scenario -eq 'plan-reader-source-dirty') { [IO.File]::WriteAllText((Join-Path $fixture.Checkout 'source.txt'), 'reader change') }
            if ($Scenario -eq 'plan-reader-source-remote') { Move-FileFixtureRemote -Fixture $fixture }
            if ($Scenario -eq 'plan-reader-fails') { throw 'private reader fault' }
            $json = $snapshot | ConvertTo-Json -Depth 30 -Compress
            if ($Scenario -eq 'plan-duplicate-iam-json') { $json = $json.Replace('"project":"maliev-website"', '"project":"maliev-website","project":"maliev-website"') }
            return $json
        }
        $builder = {
            $calls.Add('image-build')
            if ($Scenario -eq 'plan-builder-source-dirty') { [IO.File]::WriteAllText((Join-Path $fixture.Checkout 'source.txt'), 'builder change') }
            if ($Scenario -eq 'plan-builder-source-remote') { Move-FileFixtureRemote -Fixture $fixture }
            if ($Scenario -eq 'plan-builder-fails') { throw 'private builder fault' }
            return @{
                image = 'asia-southeast1-docker.pkg.dev/maliev-website/recording/legacy-maliev-file-service@sha256:' + ('b' * 64)
                sourceCommit = if ($Scenario -eq 'plan-source-invalid') { 'c' * 40 } else { $fixture.SourceCommit }
                approved = $Scenario -ne 'plan-unapproved'
            }
        }
        if ($Scenario -eq 'plan-image-invalid') {
            $builder = { $calls.Add('image-build'); return @{ image = 'example.invalid/image:latest'; sourceCommit = $fixture.SourceCommit; approved = $true } }
        }
        $originalDeploymentJson = $deployment | ConvertTo-Json -Depth 30 -Compress
        $originalAccountJson = $account | ConvertTo-Json -Depth 30 -Compress
        $requestDeploymentJson = if ($Scenario -eq 'plan-duplicate-json') { $originalDeploymentJson.Replace('"kind":"Deployment"', '"kind":"Deployment","kind":"Deployment"') } else { $originalDeploymentJson }
        $value = New-LegacyFileIdentityPlan -DeploymentJson $requestDeploymentJson -ServiceAccountJson $originalAccountJson -ExpectedBucketUri 'gs://recording-file-bucket' -SourceCommit $expectedSourceCommit -SourceCheckoutPath $fixture.Checkout -ApprovedSourceRepository $expectedOrigin -AllowFixtureOrigin:$fixtureOptIn -SourceGuardScriptPath $SourceGuardScriptPath -SourceGuardModulePath $SourceGuardModulePath -ApprovedImageRepository 'asia-southeast1-docker.pkg.dev/maliev-website/recording/legacy-maliev-file-service' -ReadIamSnapshot $reader -BuildImage $builder -ImageOnlyPlanScriptPath $ImageOnlyPlanScriptPath
        if ($value.Resources.Count -ne 2 -or $value.Resources[0].kind -cne 'ServiceAccount' -or $value.Resources[1].kind -cne 'Deployment' -or $value.SnapshotAuthenticityAccepted) { throw 'Offline resource ordering or acceptance is invalid.' }
        $expectedDeployment = ConvertFrom-Json $originalDeploymentJson -AsHashtable -DateKind String
        $expectedDeployment.spec.template.spec.containers[0].image = 'asia-southeast1-docker.pkg.dev/maliev-website/recording/legacy-maliev-file-service@sha256:' + ('b' * 64)
        if (($value.Resources[1] | ConvertTo-Json -Depth 30 -Compress) -cne ($expectedDeployment | ConvertTo-Json -Depth 30 -Compress) -or
            ($value.Resources[0] | ConvertTo-Json -Depth 30 -Compress) -cne $originalAccountJson -or
            ($deployment | ConvertTo-Json -Depth 30 -Compress) -cne $originalDeploymentJson) { throw 'Offline image plan modified non-image settings or its input.' }
        if ($value.SourceObservation.sourceCommit -cne $fixture.SourceCommit -or $value.SourceObservation.repositoryIdentity -cne 'isolated-fixture-origin' -or $value.SourceObservation.deploymentAllowed -or $value.SourceObservation.liveAccepted) { throw 'Source observation crossed its fixture boundary.' }
        $value
    } elseif ($intent) {
        Assert-LegacyFileIdentityIntent -DeploymentJson ($deployment | ConvertTo-Json -Depth 30 -Compress) -ServiceAccountJson ($account | ConvertTo-Json -Depth 30 -Compress)
    } else {
        Assert-LegacyFileIamSnapshot -SnapshotJson ($snapshot | ConvertTo-Json -Depth 30 -Compress) -ExpectedBucketUri 'gs://recording-file-bucket'
    }
    if ($result.DeploymentAllowed -or $result.LiveAccepted) { throw 'Offline contract activated a runtime boundary.' }
} catch { $failed = $true; $failureMessage = $_.Exception.Message; if ($_.Exception.Message.Contains('private', [StringComparison]::Ordinal)) { throw 'Private callback details escaped the controlled boundary.' } } finally { if ($tempHelperPath) { [IO.File]::Delete($tempHelperPath) }; if ($tempSourceGuard) { [IO.File]::Delete($tempSourceGuard) }; if ($tempSourceModule) { [IO.File]::Delete($tempSourceModule) }; if ($fixture) { Remove-FileReleaseSourceFixture -Root $fixture.Root } }
if ($accepted -eq $failed) { throw ('Contract scenario returned an unexpected outcome: ' + $failureMessage) }
if ($plan) {
    $expected = if ($Scenario -in @('plan-identity-invalid', 'plan-helper-missing', 'plan-helper-changed', 'plan-duplicate-json', 'plan-source-dirty-tracked', 'plan-source-dirty-untracked', 'plan-source-dirty-staged', 'plan-source-wrong-head', 'plan-source-origin', 'plan-source-stale-remote', 'plan-source-helper-missing', 'plan-source-helper-changed', 'plan-source-module-changed', 'plan-fixture-without-opt-in', 'plan-production-origin-mismatch')) { '' } elseif ($Scenario -in @('plan-reader-fails', 'plan-policy-invalid', 'plan-duplicate-iam-json', 'plan-reader-source-dirty', 'plan-reader-source-remote')) { 'iam-read' } else { 'iam-read,image-build' }
    if (($calls -join ',') -cne $expected) { throw 'IAM preflight ordering did not stop image construction.' }
}
Write-Output ('PASS:' + $Scenario)
