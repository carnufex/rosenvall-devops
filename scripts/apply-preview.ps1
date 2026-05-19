param(
    [Parameter(Mandatory = $true)]
    [string]$WorkItemId,

    [string]$ApiBaseUrl = "http://localhost:5088",
    [string]$Kubeconfig = ""
)

$ErrorActionPreference = "Stop"

$manifestUrl = "$ApiBaseUrl/api/previews/$WorkItemId/manifest"
$manifest = Invoke-RestMethod $manifestUrl

if ([string]::IsNullOrWhiteSpace($manifest)) {
    throw "No preview manifest returned for work item $WorkItemId."
}

$tempManifest = Join-Path ([System.IO.Path]::GetTempPath()) "rosenvall-devops-preview-$WorkItemId.yaml"
Set-Content -LiteralPath $tempManifest -Value $manifest -Encoding utf8

$kubectlArgs = @("apply", "-f", $tempManifest)
if (-not [string]::IsNullOrWhiteSpace($Kubeconfig)) {
    $kubectlArgs = @("--kubeconfig", $Kubeconfig) + $kubectlArgs
}

kubectl @kubectlArgs

Write-Host "Applied preview manifest for work item $WorkItemId."
Write-Host "Manifest: $tempManifest"
