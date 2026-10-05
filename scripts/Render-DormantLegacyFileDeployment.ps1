[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Image,
    [Parameter(Mandatory)][string]$ApprovedImageRepository
)

$ErrorActionPreference = 'Stop'
# This renderer has no cloud, Kubernetes, image-build or resource-write adapter.
$imagePattern = '\A' + [regex]::Escape($ApprovedImageRepository) + '@sha256:[a-f0-9]{64}\z'
if ($ApprovedImageRepository -cnotmatch '\A[a-z0-9-]+-docker\.pkg\.dev/maliev-website/[a-z0-9._-]+/legacy-maliev-file-service\z' -or
    $Image -cnotmatch $imagePattern -or
    $Image.EndsWith(('0' * 64), [StringComparison]::Ordinal)) {
    throw 'An approved immutable legacy File image digest is required.'
}
$templatePath = Join-Path $PSScriptRoot '../deploy/disabled/file-deployment.template.yaml'
$template = [IO.File]::ReadAllText($templatePath)
$marker = '__REQUIRED_LEGACY_FILE_IMMUTABLE_IMAGE__'
if ([regex]::Matches($template, $marker).Count -ne 1) {
    throw 'The dormant File deployment image slot is invalid.'
}
[Console]::Out.Write($template.Replace($marker, $Image))
