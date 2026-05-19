param(
    [int]$ApiPort = 5088,
    [int]$FrontendPort = 5173
)

$ErrorActionPreference = "Stop"

$root = Resolve-Path (Join-Path $PSScriptRoot "..")
$repoRoot = Resolve-Path (Join-Path $root "..\..")
$logDir = Join-Path $repoRoot ".codex\devops-logs"
New-Item -ItemType Directory -Force $logDir | Out-Null

$apiProject = Join-Path $root "src\Rosenvall.DevOps.Api\Rosenvall.DevOps.Api.csproj"
$frontendRoot = Join-Path $root "frontend"

$apiOut = Join-Path $logDir "api.out.log"
$apiErr = Join-Path $logDir "api.err.log"
$frontendOut = Join-Path $logDir "frontend.out.log"
$frontendErr = Join-Path $logDir "frontend.err.log"
$homelabKubeconfig = Join-Path (Split-Path $root -Parent) "Rosenvalls-Homelab\tofu\output\kubeconfig"
$previousPreviewKubeconfig = $env:Preview__KubeconfigPath
$previousPipelinesKubeconfig = $env:Pipelines__KubeconfigPath

if ([string]::IsNullOrWhiteSpace($env:Preview__KubeconfigPath) -and (Test-Path -LiteralPath $homelabKubeconfig)) {
    $env:Preview__KubeconfigPath = (Resolve-Path -LiteralPath $homelabKubeconfig).Path
}

if ([string]::IsNullOrWhiteSpace($env:Pipelines__KubeconfigPath) -and (Test-Path -LiteralPath $homelabKubeconfig)) {
    $env:Pipelines__KubeconfigPath = (Resolve-Path -LiteralPath $homelabKubeconfig).Path
}

Get-CimInstance Win32_Process |
    Where-Object {
        ($_.Name -eq "dotnet.exe" -and $_.CommandLine -match "Rosenvall.DevOps.Api") -or
        ($_.Name -eq "node.exe" -and $_.CommandLine -match "vite")
    } |
    ForEach-Object { Stop-Process -Id $_.ProcessId -Force }

$api = Start-Process -FilePath "dotnet.exe" `
    -ArgumentList "run", "--project", $apiProject, "--urls", "http://localhost:$ApiPort" `
    -WorkingDirectory $repoRoot `
    -WindowStyle Hidden `
    -RedirectStandardOutput $apiOut `
    -RedirectStandardError $apiErr `
    -PassThru

$env:Preview__KubeconfigPath = $previousPreviewKubeconfig
$env:Pipelines__KubeconfigPath = $previousPipelinesKubeconfig

Start-Sleep -Seconds 3

$frontend = Start-Process -FilePath "npm.cmd" `
    -ArgumentList "run", "dev", "--", "--port", "$FrontendPort" `
    -WorkingDirectory $frontendRoot `
    -WindowStyle Hidden `
    -RedirectStandardOutput $frontendOut `
    -RedirectStandardError $frontendErr `
    -PassThru

Start-Sleep -Seconds 2

Write-Host "Rosenvall DevOps demo is running."
Write-Host "Frontend: http://localhost:$FrontendPort"
Write-Host "API:      http://localhost:$ApiPort/healthz"
Write-Host "API PID:  $($api.Id)"
Write-Host "UI PID:   $($frontend.Id)"
Write-Host "Logs:     $logDir"
if (Test-Path -LiteralPath $homelabKubeconfig) {
    Write-Host "Kubeconfig: $homelabKubeconfig"
}
