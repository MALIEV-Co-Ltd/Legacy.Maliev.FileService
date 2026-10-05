param([Parameter(Mandatory)][string]$Scenario, [string]$ImageOnlyPlanScriptPath = (Join-Path $PSScriptRoot "../../.dependencies/Legacy.Maliev.Workflows/scripts/New-OfflineImageOnlyDeploymentPlan.ps1"))
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
$plan = $Scenario.StartsWith('plan-', [StringComparison]::Ordinal)
$intent = $Scenario.StartsWith('intent-', [StringComparison]::Ordinal)
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
            if ($Scenario -eq 'plan-reader-fails') { throw 'private reader fault' }
            $json = $snapshot | ConvertTo-Json -Depth 30 -Compress
            if ($Scenario -eq 'plan-duplicate-iam-json') { $json = $json.Replace('"project":"maliev-website"', '"project":"maliev-website","project":"maliev-website"') }
            return $json
        }
        $builder = {
            $calls.Add('image-build')
            if ($Scenario -eq 'plan-builder-fails') { throw 'private builder fault' }
            return @{
                image = 'asia-southeast1-docker.pkg.dev/maliev-website/recording/legacy-maliev-file-service@sha256:' + ('b' * 64)
                sourceCommit = if ($Scenario -eq 'plan-source-invalid') { 'c' * 40 } else { 'a' * 40 }
                approved = $Scenario -ne 'plan-unapproved'
            }
        }
        if ($Scenario -eq 'plan-image-invalid') {
            $builder = { $calls.Add('image-build'); return @{ image = 'example.invalid/image:latest'; sourceCommit = 'a' * 40; approved = $true } }
        }
        $originalDeploymentJson = $deployment | ConvertTo-Json -Depth 30 -Compress
        $originalAccountJson = $account | ConvertTo-Json -Depth 30 -Compress
        $requestDeploymentJson = if ($Scenario -eq 'plan-duplicate-json') { $originalDeploymentJson.Replace('"kind":"Deployment"', '"kind":"Deployment","kind":"Deployment"') } else { $originalDeploymentJson }
        $value = New-LegacyFileIdentityPlan -DeploymentJson $requestDeploymentJson -ServiceAccountJson $originalAccountJson -ExpectedBucketUri 'gs://recording-file-bucket' -SourceCommit ('a' * 40) -ApprovedImageRepository 'asia-southeast1-docker.pkg.dev/maliev-website/recording/legacy-maliev-file-service' -ReadIamSnapshot $reader -BuildImage $builder -ImageOnlyPlanScriptPath $ImageOnlyPlanScriptPath
        if ($value.Resources.Count -ne 2 -or $value.Resources[0].kind -cne 'ServiceAccount' -or $value.Resources[1].kind -cne 'Deployment' -or $value.SnapshotAuthenticityAccepted) { throw 'Offline resource ordering or acceptance is invalid.' }
        $expectedDeployment = ConvertFrom-Json $originalDeploymentJson -AsHashtable -DateKind String
        $expectedDeployment.spec.template.spec.containers[0].image = 'asia-southeast1-docker.pkg.dev/maliev-website/recording/legacy-maliev-file-service@sha256:' + ('b' * 64)
        if (($value.Resources[1] | ConvertTo-Json -Depth 30 -Compress) -cne ($expectedDeployment | ConvertTo-Json -Depth 30 -Compress) -or
            ($value.Resources[0] | ConvertTo-Json -Depth 30 -Compress) -cne $originalAccountJson -or
            ($deployment | ConvertTo-Json -Depth 30 -Compress) -cne $originalDeploymentJson) { throw 'Offline image plan modified non-image settings or its input.' }
        $value
    } elseif ($intent) {
        Assert-LegacyFileIdentityIntent -DeploymentJson ($deployment | ConvertTo-Json -Depth 30 -Compress) -ServiceAccountJson ($account | ConvertTo-Json -Depth 30 -Compress)
    } else {
        Assert-LegacyFileIamSnapshot -SnapshotJson ($snapshot | ConvertTo-Json -Depth 30 -Compress) -ExpectedBucketUri 'gs://recording-file-bucket'
    }
    if ($result.DeploymentAllowed -or $result.LiveAccepted) { throw 'Offline contract activated a runtime boundary.' }
} catch { $failed = $true; $failureMessage = $_.Exception.Message; if ($_.Exception.Message.Contains('private', [StringComparison]::Ordinal)) { throw 'Private callback details escaped the controlled boundary.' } } finally { if ($tempHelperPath) { [IO.File]::Delete($tempHelperPath) } }
if ($accepted -eq $failed) { throw ('Contract scenario returned an unexpected outcome: ' + $failureMessage) }
if ($plan) {
    $expected = if ($Scenario -in @('plan-identity-invalid', 'plan-helper-missing', 'plan-helper-changed', 'plan-duplicate-json')) { '' } elseif ($Scenario -in @('plan-reader-fails', 'plan-policy-invalid', 'plan-duplicate-iam-json')) { 'iam-read' } else { 'iam-read,image-build' }
    if (($calls -join ',') -cne $expected) { throw 'IAM preflight ordering did not stop image construction.' }
}
Write-Output ('PASS:' + $Scenario)
