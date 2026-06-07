param(
    [string]$BaseUrl = "https://devops.rosenvall.se",
    [string]$HomelabPath,
    [switch]$SkipRegistry
)

$ErrorActionPreference = "Stop"

$root = Resolve-Path (Join-Path $PSScriptRoot "..")
$repoRoot = $root.Path
if ([string]::IsNullOrWhiteSpace($HomelabPath)) {
    $HomelabPath = Join-Path (Split-Path $repoRoot -Parent) "Rosenvalls-Homelab\kubernetes\applications\rosenvall-devops"
}

function Get-CommandOutput {
    param(
        [string]$FileName,
        [string[]]$Arguments,
        [string]$WorkingDirectory = $repoRoot
    )

    $output = & $FileName @Arguments 2>$null
    if ($LASTEXITCODE -ne 0) {
        return $null
    }

    return ($output -join "`n").Trim()
}

function Get-RegistryDigest {
    param([string]$Image)

    if ($SkipRegistry) {
        return "skipped"
    }

    if (Get-Command docker -ErrorAction SilentlyContinue) {
        $digest = Get-CommandOutput "docker" @("buildx", "imagetools", "inspect", $Image, "--format", "{{json .Manifest.Digest}}")
        if (-not [string]::IsNullOrWhiteSpace($digest)) {
            return $digest.Trim('"')
        }
    }

    if (Get-Command crane -ErrorAction SilentlyContinue) {
        $digest = Get-CommandOutput "crane" @("digest", $Image)
        if (-not [string]::IsNullOrWhiteSpace($digest)) {
            return $digest
        }
    }

    return "unavailable"
}

function Find-HomelabDigest {
    param(
        [string]$Path,
        [string]$Image
    )

    if (-not (Test-Path -LiteralPath $Path)) {
        return "missing"
    }

    $pattern = [regex]::Escape($Image) + "@sha256:[a-fA-F0-9]{64}"
    $match = Get-ChildItem -LiteralPath $Path -Recurse -File |
        Select-String -Pattern $pattern |
        Select-Object -First 1

    if ($null -eq $match) {
        return "not found"
    }

    return ([regex]::Match($match.Line, "@sha256:[a-fA-F0-9]{64}")).Value
}

# Local git identity check: git rev-parse HEAD
$localSha = Get-CommandOutput "git" @("rev-parse", "HEAD")
$statusUrl = ($BaseUrl.TrimEnd("/")) + "/api/status"
$status = $null
try {
    $status = Invoke-RestMethod -Uri $statusUrl -TimeoutSec 10
} catch {
    Write-Warning "Could not read $statusUrl`: $($_.Exception.Message)"
}

$apiImage = "ghcr.io/carnufex/rosenvall-devops-api"
$frontendImage = "ghcr.io/carnufex/rosenvall-devops-frontend"
$apiRegistryDigest = Get-RegistryDigest "$apiImage`:main"
$frontendRegistryDigest = Get-RegistryDigest "$frontendImage`:main"
$apiHomelabDigest = Find-HomelabDigest $HomelabPath $apiImage
$frontendHomelabDigest = Find-HomelabDigest $HomelabPath $frontendImage

$rows = @(
    [pscustomobject]@{ Check = "Local git SHA"; Value = $localSha },
    [pscustomobject]@{ Check = "Live /api/status"; Value = $statusUrl },
    [pscustomobject]@{ Check = "Release commit SHA"; Value = $status.release.commitSha },
    [pscustomobject]@{ Check = "Release API image"; Value = $status.release.apiImage },
    [pscustomobject]@{ Check = "Release frontend image"; Value = $status.release.frontendImage },
    [pscustomobject]@{ Check = "Release runner image"; Value = $status.release.runnerImage },
    [pscustomobject]@{ Check = "GHCR API main digest"; Value = $apiRegistryDigest },
    [pscustomobject]@{ Check = "GHCR frontend main digest"; Value = $frontendRegistryDigest },
    [pscustomobject]@{ Check = "Homelab API digest pin"; Value = $apiHomelabDigest },
    [pscustomobject]@{ Check = "Homelab frontend digest pin"; Value = $frontendHomelabDigest },
    [pscustomobject]@{ Check = "Homelab manifest path"; Value = $HomelabPath }
)

$rows | Format-Table -AutoSize

if ($status -and $localSha -and $status.release.commitSha -and $status.release.commitSha -ne "unknown" -and $status.release.commitSha -ne $localSha) {
    Write-Warning "Live Release commit SHA does not match local git SHA."
}

if ($status -and $status.release.apiImage -and $status.release.apiImage -notmatch "@sha256:") {
    Write-Warning "Live Release API image does not include a digest pin."
}

if ($status -and $status.release.frontendImage -and $status.release.frontendImage -notmatch "@sha256:") {
    Write-Warning "Live Release frontend image does not include a digest pin."
}
