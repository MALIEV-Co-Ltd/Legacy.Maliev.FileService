# Offline validation only. No cloud, Kubernetes, process, or resource-write adapter.
function ConvertFrom-LegacyFilePlanJson {
    param([Parameter(Mandatory)][string]$Json)
    function Test-UniqueNames([System.Text.Json.JsonElement]$Element) {
        if ($Element.ValueKind -eq [System.Text.Json.JsonValueKind]::Object) {
            $names = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
            foreach ($property in $Element.EnumerateObject()) {
                if (-not $names.Add($property.Name)) { throw 'Ambiguous JSON.' }
                Test-UniqueNames $property.Value
            }
        } elseif ($Element.ValueKind -eq [System.Text.Json.JsonValueKind]::Array) {
            foreach ($item in $Element.EnumerateArray()) { Test-UniqueNames $item }
        }
    }
    $document = $null
    try {
        if ($Json.Length -gt 1048576) { throw 'Oversized JSON.' }
        $document = [System.Text.Json.JsonDocument]::Parse($Json)
        Test-UniqueNames $document.RootElement
        $parsed = ConvertFrom-Json -InputObject $Json -AsHashtable -DateKind String -ErrorAction Stop
        if ($parsed -isnot [hashtable]) { throw 'Expected an object.' }
        return $parsed
    } catch { throw 'Legacy File plan JSON is invalid or ambiguous.' }
    finally { if ($null -ne $document) { $document.Dispose() } }
}

function Assert-LegacyFileIdentityIntent {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$DeploymentJson, [Parameter(Mandatory)][string]$ServiceAccountJson)
    try {
        $deployment = ConvertFrom-LegacyFilePlanJson -Json $DeploymentJson
        $account = ConvertFrom-LegacyFilePlanJson -Json $ServiceAccountJson
    } catch { throw 'Legacy File identity documents are invalid.' }
    $namespace = 'maliev-legacy'
    $ksa = 'legacy-maliev-file'
    $gsa = 'legacy-maliev-file@maliev-website.iam.gserviceaccount.com'
    if ($deployment.apiVersion -cne 'apps/v1' -or $deployment.kind -cne 'Deployment' -or
        $deployment.metadata.name -cne 'legacy-maliev-file' -or $deployment.metadata.namespace -cne $namespace -or
        $deployment.spec.template.spec.serviceAccountName -cne $ksa) { throw 'Legacy File Deployment identity is invalid.' }
    $containers = @($deployment.spec.template.spec.containers | Where-Object { $_.name -ceq 'legacy-maliev-file-service' })
    if ($containers.Count -ne 1) { throw 'Legacy File Deployment container identity is invalid.' }
    if ($account.apiVersion -cne 'v1' -or $account.kind -cne 'ServiceAccount' -or
        $account.metadata.name -cne $ksa -or $account.metadata.namespace -cne $namespace -or
        $account.metadata.annotations['iam.gke.io/gcp-service-account'] -cne $gsa -or
        $account.automountServiceAccountToken -isnot [bool] -or -not $account.automountServiceAccountToken) {
        throw 'Legacy File service-account identity or token intent is invalid.'
    }
    return [pscustomobject]@{ Deployment = $deployment; ServiceAccount = $account; DeploymentAllowed = $false; LiveAccepted = $false }
}

function Assert-LegacyFileIamSnapshot {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$SnapshotJson, [Parameter(Mandatory)][string]$ExpectedBucketUri)
    # The caller supplies an approved bucket and a recording-reader snapshot.
    # This checks parsed policy semantics, not cloud authenticity or freshness.
    if ($ExpectedBucketUri -cnotmatch '^gs://[a-z0-9][a-z0-9._-]{1,61}[a-z0-9]$') { throw 'Legacy File bucket scope is invalid.' }
    try { $snapshot = ConvertFrom-LegacyFilePlanJson -Json $SnapshotJson }
    catch { throw 'Legacy File IAM snapshot is invalid.' }
    $gsa = 'legacy-maliev-file@maliev-website.iam.gserviceaccount.com'
    $gsaMember = 'serviceAccount:' + $gsa
    $ksaMember = 'serviceAccount:maliev-website.svc.id.goog[maliev-legacy/legacy-maliev-file]'
    if ($snapshot.project -cne 'maliev-website' -or $snapshot.bucketUri -cne $ExpectedBucketUri -or
        $snapshot.googleServiceAccount -cne $gsa) { throw 'Legacy File IAM snapshot scope is invalid.' }
    $requirements = @(
        @{ Policy = $snapshot.bucketPolicy; Role = 'roles/storage.objectUser'; Member = $gsaMember },
        @{ Policy = $snapshot.serviceAccountPolicy; Role = 'roles/iam.workloadIdentityUser'; Member = $ksaMember },
        @{ Policy = $snapshot.serviceAccountPolicy; Role = 'roles/iam.serviceAccountTokenCreator'; Member = $gsaMember }
    )
    foreach ($requirement in $requirements) {
        $matching = @($requirement.Policy.bindings | Where-Object {
            $_.role -ceq $requirement.Role -and @($_.members) -ccontains $requirement.Member
        })
        if ($matching.Count -ne 1 -or $matching[0].ContainsKey('condition')) { throw 'Legacy File required unconditional IAM binding is missing or ambiguous.' }
    }
    foreach ($scope in @(@{ Policy = $snapshot.bucketPolicy; Allowed = @('roles/storage.objectUser') },
        @{ Policy = $snapshot.serviceAccountPolicy; Allowed = @('roles/iam.workloadIdentityUser', 'roles/iam.serviceAccountTokenCreator') })) {
        foreach ($binding in $scope.Policy.bindings) {
            if (@($binding.members) -ccontains 'allUsers' -or @($binding.members) -ccontains 'allAuthenticatedUsers') { throw 'Legacy File IAM snapshot grants public access.' }
            if ((@($binding.members) -ccontains $gsaMember -or @($binding.members) -ccontains $ksaMember) -and
                $scope.Allowed -cnotcontains $binding.role) { throw 'Legacy File identity has a non-approved IAM role.' }
        }
    }
    foreach ($binding in $snapshot.bucketPolicy.bindings) {
        if (@($binding.members) -ccontains $ksaMember) { throw 'Legacy File bucket role must use the dedicated Google identity.' }
    }
    foreach ($binding in $snapshot.serviceAccountPolicy.bindings) {
        if ((@($binding.members) -ccontains $ksaMember -and $binding.role -cne 'roles/iam.workloadIdentityUser') -or
            (@($binding.members) -ccontains $gsaMember -and $binding.role -cne 'roles/iam.serviceAccountTokenCreator')) {
            throw 'Legacy File impersonation and signing identities are mismatched.'
        }
    }
    return [pscustomobject]@{ SchemaVersion = 'legacy-file-identity-plan/v1'; PolicySemanticsAccepted = $true; DeploymentAllowed = $false; LiveAccepted = $false }
}

function New-LegacyFileIdentityPlan {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$DeploymentJson,
        [Parameter(Mandatory)][string]$ServiceAccountJson,
        [Parameter(Mandatory)][string]$ExpectedBucketUri,
        [Parameter(Mandatory)][string]$SourceCommit,
        [Parameter(Mandatory)][string]$ApprovedImageRepository,
        [Parameter(Mandatory)][scriptblock]$ReadIamSnapshot,
        [Parameter(Mandatory)][scriptblock]$BuildImage,
        [string]$ImageOnlyPlanScriptPath = (Join-Path $PSScriptRoot "../.dependencies/Legacy.Maliev.Workflows/scripts/New-OfflineImageOnlyDeploymentPlan.ps1")
    )
    if ($SourceCommit -cnotmatch '^[a-f0-9]{40}$' -or
        $ApprovedImageRepository -cnotmatch '^[a-z0-9-]+-docker\.pkg\.dev/maliev-website/[a-z0-9._-]+/legacy-maliev-file-service$') {
        throw 'Legacy File build provenance scope is invalid.'
    }
    # Canonical LF hash binds the imported producer bytes across Git checkouts.
    # Its immutable repository commit is separately pinned by the consumer workflow.
    try {
        if ((Get-Item -LiteralPath $ImageOnlyPlanScriptPath -ErrorAction Stop).Length -gt 131072) { throw 'Oversized producer.' }
        $helperText = [IO.File]::ReadAllText($ImageOnlyPlanScriptPath).Replace("`r`n", "`n")
        $helperHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($helperText)))
    } catch { throw 'Legacy File image-plan producer is unavailable.' }
    if ($helperHash -cne 'BFF22600979E72EB63958EAE1C5F86EC9BB000DD6AB24EA9D529D9CAC92DF515') {
        throw 'Legacy File image-plan producer bytes are not approved.'
    }
    . $ImageOnlyPlanScriptPath
    $intent = Assert-LegacyFileIdentityIntent -DeploymentJson $DeploymentJson -ServiceAccountJson $ServiceAccountJson
    try { $snapshot = & $ReadIamSnapshot }
    catch { throw 'Legacy File IAM policy read failed before image construction.' }
    if ($snapshot -isnot [string]) { throw 'Legacy File IAM policy reader returned an invalid snapshot.' }
    $null = Assert-LegacyFileIamSnapshot -SnapshotJson $snapshot -ExpectedBucketUri $ExpectedBucketUri
    # The only image construction boundary follows successful policy validation.
    # No default adapter exists; callers must explicitly supply a controlled one.
    try { $built = & $BuildImage }
    catch { throw 'Legacy File controlled image construction failed.' }
    if ($built -isnot [hashtable] -or $built.Count -ne 3 -or $built.sourceCommit -cne $SourceCommit -or $built.approved -isnot [bool] -or -not $built.approved -or
        $built.image -isnot [string] -or -not $built.image.StartsWith($ApprovedImageRepository + '@sha256:', [StringComparison]::Ordinal) -or
        $built.image.Substring($ApprovedImageRepository.Length) -cnotmatch '^@sha256:[a-f0-9]{64}$') {
        throw 'Legacy File immutable image provenance is invalid.'
    }
    $imagePlan = New-OfflineImageOnlyDeploymentPlan -DeploymentJson $DeploymentJson -SourceCommit $SourceCommit -ApprovedImageRepository $ApprovedImageRepository -ImageProof $built -ContractVersion 'legacy-file-image-only/v1'
    if ($imagePlan.DeploymentAllowed -or $imagePlan.LiveAccepted -or $imagePlan.SchemaVersion -cne 'legacy-file-image-only/v1') {
        throw 'Legacy File image plan crossed its offline contract.'
    }
    return [pscustomobject]@{
        SchemaVersion = 'legacy-file-identity-plan/v1'
        Mode = 'offline-identity-preparation'
        SourceCommit = $SourceCommit
        Resources = @($intent.ServiceAccount, $imagePlan.Deployment)
        DeploymentAllowed = $false
        LiveAccepted = $false
        SnapshotAuthenticityAccepted = $false
    }
}
