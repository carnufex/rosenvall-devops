param(
    [int]$ApiPort = 5088,
    [int]$FrontendPort = 5173,
    [int]$ForgejoPort = 3001,
    [string]$BoardId,
    [string]$WorkItemId,
    [string]$KubeconfigPath
)

$ErrorActionPreference = "Stop"

$root = Resolve-Path (Join-Path $PSScriptRoot "..")
$repoRoot = $root.Path
$defaultKubeconfig = Join-Path (Split-Path $repoRoot -Parent) "Rosenvalls-Homelab\tofu\output\kubeconfig"
if ([string]::IsNullOrWhiteSpace($KubeconfigPath)) {
    $KubeconfigPath = $defaultKubeconfig
}

$results = New-Object System.Collections.Generic.List[object]

function Add-DoctorResult {
    param(
        [string]$Name,
        [string]$Status,
        [string]$Message
    )

    $results.Add([pscustomobject]@{
        Name = $Name
        Status = $Status
        Message = $Message
    }) | Out-Null
}

function Invoke-DoctorJson {
    param(
        [string]$Url,
        [int]$TimeoutSeconds = 5
    )

    try {
        return Invoke-RestMethod -Uri $Url -TimeoutSec $TimeoutSeconds
    } catch {
        return $null
    }
}

function Test-DoctorHttp {
    param(
        [string]$Url,
        [int]$TimeoutSeconds = 5
    )

    try {
        $response = Invoke-WebRequest -UseBasicParsing -Uri $Url -TimeoutSec $TimeoutSeconds
        return [pscustomobject]@{
            Succeeded = $response.StatusCode -ge 200 -and $response.StatusCode -lt 500
            StatusCode = $response.StatusCode
            Message = "HTTP $($response.StatusCode)"
        }
    } catch {
        return [pscustomobject]@{
            Succeeded = $false
            StatusCode = $null
            Message = $_.Exception.Message
        }
    }
}

$apiHealthUrl = "http://localhost:$ApiPort/healthz"
$apiStatusUrl = "http://localhost:$ApiPort/api/status"
$frontendUrl = "http://localhost:$FrontendPort"

$apiHealth = Test-DoctorHttp $apiHealthUrl
if ($apiHealth.Succeeded) {
    Add-DoctorResult "API health" "OK" "$apiHealthUrl - $($apiHealth.Message)"
} else {
    Add-DoctorResult "API health" "FAIL" "$apiHealthUrl - $($apiHealth.Message)"
}

$frontendHealth = Test-DoctorHttp $frontendUrl
if ($frontendHealth.Succeeded) {
    Add-DoctorResult "Frontend health" "OK" "$frontendUrl - $($frontendHealth.Message)"
} else {
    Add-DoctorResult "Frontend health" "FAIL" "$frontendUrl - $($frontendHealth.Message)"
}

$status = Invoke-DoctorJson $apiStatusUrl
if ($null -eq $status) {
    Add-DoctorResult "API status" "WARN" "$apiStatusUrl did not return JSON. If auth is enabled, log in through the browser and use Settings for release diagnostics."
    Add-DoctorResult "Authentication mode" "WARN" "Unavailable because /api/status could not be read."
    Add-DoctorResult "runner image" "WARN" "Unavailable because /api/status could not be read."
} else {
    Add-DoctorResult "API status" "OK" "$apiStatusUrl returned release diagnostics."
    Add-DoctorResult "Authentication mode" "OK" "$($status.authMode)"
    if ($status.release.runnerImage -and $status.release.runnerImage -ne "unknown") {
        Add-DoctorResult "runner image" "OK" "$($status.release.runnerImage)"
    } else {
        Add-DoctorResult "runner image" "WARN" "$($status.release.runnerImage)"
    }
}

$forgejoSocket = Test-NetConnection -ComputerName "localhost" -Port $ForgejoPort -InformationLevel Quiet -WarningAction SilentlyContinue
if ($forgejoSocket) {
    Add-DoctorResult "Forgejo port-forward" "OK" "localhost:$ForgejoPort accepts TCP connections"
} else {
    Add-DoctorResult "Forgejo port-forward" "WARN" "localhost:$ForgejoPort is not accepting TCP connections"
}

if (Test-Path -LiteralPath $KubeconfigPath) {
    Add-DoctorResult "Kubeconfig" "OK" $KubeconfigPath
} else {
    Add-DoctorResult "Kubeconfig" "WARN" "Missing at $KubeconfigPath"
}

if (-not [string]::IsNullOrWhiteSpace($BoardId)) {
    $boardUrl = "http://localhost:$ApiPort/api/boards/$BoardId"
    $sourceUrl = "http://localhost:$ApiPort/api/boards/$BoardId/source/repositories"
    $boardCheck = Test-DoctorHttp $boardUrl
    $sourceCheck = Test-DoctorHttp $sourceUrl
    if ($boardCheck.Succeeded) {
        Add-DoctorResult "Board endpoint" "OK" "$boardUrl - $($boardCheck.Message)"
    } else {
        Add-DoctorResult "Board endpoint" "WARN" "$boardUrl - $($boardCheck.Message)"
    }

    if ($sourceCheck.Succeeded) {
        Add-DoctorResult "Source endpoint availability" "OK" "$sourceUrl - $($sourceCheck.Message)"
    } else {
        Add-DoctorResult "Source endpoint availability" "WARN" "$sourceUrl - $($sourceCheck.Message)"
    }
} else {
    Add-DoctorResult "Source endpoint availability" "WARN" "Pass -BoardId to check /api/boards/{id}/source/repositories."
}

if (-not [string]::IsNullOrWhiteSpace($WorkItemId)) {
    $diffUrl = "http://localhost:$ApiPort/api/work-items/$WorkItemId/pull-request/diff"
    $diffCheck = Test-DoctorHttp $diffUrl
    if ($diffCheck.Succeeded) {
        Add-DoctorResult "Local PR diff endpoint availability" "OK" "$diffUrl - $($diffCheck.Message)"
    } else {
        Add-DoctorResult "Local PR diff endpoint availability" "WARN" "$diffUrl - $($diffCheck.Message)"
    }
} else {
    Add-DoctorResult "Local PR diff endpoint availability" "WARN" "Pass -WorkItemId to check /api/work-items/{id}/pull-request/diff."
}

$results | Format-Table -AutoSize

if ($results.Status -contains "FAIL") {
    exit 1
}
