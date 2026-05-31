$ErrorActionPreference = "Stop"

$root = Resolve-Path (Join-Path $PSScriptRoot "..")
$repoRoot = $root.Path
$logDir = Join-Path $repoRoot ".codex\devops-logs"
$pidFile = Join-Path $logDir "local-demo.pids.json"

function Stop-RecordedProcess {
    param(
        [string]$Label,
        [object]$ProcessId
    )

    if ($null -eq $ProcessId) {
        return
    }

    $parsed = 0
    if (-not [int]::TryParse([string]$ProcessId, [ref]$parsed) -or $parsed -le 0) {
        return
    }

    $process = Get-Process -Id $parsed -ErrorAction SilentlyContinue
    if ($null -eq $process) {
        return
    }

    try {
        Stop-Process -Id $parsed -Force -ErrorAction Stop
        Write-Host "Stopped $Label process $parsed from $pidFile."
    } catch {
        Write-Warning "Could not stop $Label process $parsed from $pidFile`: $($_.Exception.Message)"
    }
}

function Get-LocalDemoProcess {
    Get-CimInstance Win32_Process |
        Where-Object {
            ($_.Name -eq "dotnet.exe" -and $_.CommandLine -match "Rosenvall.DevOps.Api") -or
            ($_.Name -eq "node.exe" -and $_.CommandLine -match "vite") -or
            ($_.Name -eq "kubectl.exe" -and $_.CommandLine -match "port-forward" -and $_.CommandLine -match "rosenvall-devops-api") -or
            ($_.Name -eq "kubectl.exe" -and $_.CommandLine -match "port-forward" -and $_.CommandLine -match "rosenvall-devops-forgejo")
        }
}

if (Test-Path -LiteralPath $pidFile) {
    try {
        $recorded = Get-Content -LiteralPath $pidFile -Raw | ConvertFrom-Json
        Stop-RecordedProcess "API" $recorded.apiPid
        Stop-RecordedProcess "frontend" $recorded.frontendPid
        Stop-RecordedProcess "Forgejo port-forward" $recorded.forgejoPid
    } catch {
        Write-Warning "Could not read $pidFile`: $($_.Exception.Message)"
    }
}

Get-LocalDemoProcess |
    ForEach-Object {
        try {
            Stop-Process -Id $_.ProcessId -Force -ErrorAction Stop
            Write-Host "Stopped matching local demo process $($_.ProcessId) ($($_.Name))."
        } catch {
            Write-Warning "Could not stop matching local demo process $($_.ProcessId) ($($_.Name)): $($_.Exception.Message)"
        }
    }

if (Test-Path -LiteralPath $pidFile) {
    Remove-Item -LiteralPath $pidFile -Force
}

$remaining = @(Get-LocalDemoProcess)
if ($remaining.Count -gt 0) {
    Write-Warning "Some Rosenvall DevOps local demo processes are still running:"
    $remaining |
        ForEach-Object {
            Write-Warning "PID $($_.ProcessId) $($_.Name): $($_.CommandLine)"
        }
} else {
    Write-Host "No matching Rosenvall DevOps local demo processes remain."
}

Write-Host "Rosenvall DevOps local demo processes stopped."
