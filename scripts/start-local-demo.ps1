param(
    [int]$ApiPort = 5088,
    [int]$FrontendPort = 5173,
    [ValidateSet("Local", "ClusterPortForward")]
    [string]$ApiMode = "Local",
    [switch]$SkipClusterSecrets,
    [switch]$EnableAuth
)

$ErrorActionPreference = "Stop"

$root = Resolve-Path (Join-Path $PSScriptRoot "..")
$repoRoot = $root.Path
$logDir = Join-Path $repoRoot ".codex\devops-logs"
New-Item -ItemType Directory -Force $logDir | Out-Null

$apiProject = Join-Path $repoRoot "src\Rosenvall.DevOps.Api\Rosenvall.DevOps.Api.csproj"
$frontendRoot = Join-Path $repoRoot "frontend"

$apiOut = Join-Path $logDir "api.out.log"
$apiErr = Join-Path $logDir "api.err.log"
$frontendOut = Join-Path $logDir "frontend.out.log"
$frontendErr = Join-Path $logDir "frontend.err.log"
$homelabKubeconfig = Join-Path (Split-Path $repoRoot -Parent) "Rosenvalls-Homelab\tofu\output\kubeconfig"
$previousPreviewKubeconfig = $env:Preview__KubeconfigPath
$previousPipelinesKubeconfig = $env:Pipelines__KubeconfigPath
$previousStorageSqlitePath = $env:Storage__SqlitePath
$previousGitHubToken = $env:GitHub__Token
$previousGitHubAppId = $env:GitHub__AppId
$previousGitHubAppPrivateKey = $env:GitHub__AppPrivateKey
$previousGitHubAppSlug = $env:GitHub__AppSlug
$previousGitHubTokenSecretName = $env:GitHub__TokenSecretName
$previousRepositoriesProvider = $env:Repositories__Provider
$previousRepositoriesMode = $env:Repositories__Mode
$previousViteAuthEnabled = $env:VITE_AUTH_ENABLED
$previousViteAuthAuthority = $env:VITE_AUTH_AUTHORITY
$previousViteAuthClientId = $env:VITE_AUTH_CLIENT_ID
$previousViteAuthRedirectUri = $env:VITE_AUTH_REDIRECT_URI
$previousViteAuthPostLogoutRedirectUri = $env:VITE_AUTH_POST_LOGOUT_REDIRECT_URI

function Get-KubectlBaseArgs {
    if (-not (Get-Command kubectl -ErrorAction SilentlyContinue)) {
        return @()
    }

    if (Test-Path -LiteralPath $homelabKubeconfig) {
        return @("--kubeconfig", (Resolve-Path -LiteralPath $homelabKubeconfig).Path)
    }

    return @()
}

function Get-KubernetesSecretValue {
    param(
        [string]$Name,
        [string]$Key,
        [string[]]$KubectlBaseArgs
    )

    if (-not (Get-Command kubectl -ErrorAction SilentlyContinue)) {
        return $null
    }

    $json = & kubectl @KubectlBaseArgs -n rosenvall-devops get secret $Name -o json 2>$null
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($json)) {
        return $null
    }

    $secret = $json | ConvertFrom-Json
    $property = $secret.data.PSObject.Properties[$Key]
    if ($null -eq $property -or [string]::IsNullOrWhiteSpace($property.Value)) {
        return $null
    }

    return [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($property.Value))
}

function Set-EnvIfBlank {
    param(
        [string]$Name,
        [string]$Value
    )

    if ([string]::IsNullOrWhiteSpace([Environment]::GetEnvironmentVariable($Name, "Process")) -and -not [string]::IsNullOrWhiteSpace($Value)) {
        [Environment]::SetEnvironmentVariable($Name, $Value, "Process")
    }
}

function Wait-HttpOk {
    param(
        [string]$Url,
        [int]$TimeoutSeconds = 45
    )

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        try {
            $response = Invoke-WebRequest -UseBasicParsing -Uri $Url -TimeoutSec 2
            if ($response.StatusCode -ge 200 -and $response.StatusCode -lt 500) {
                return $true
            }
        } catch {
        }

        Start-Sleep -Milliseconds 500
    }

    return $false
}

if ([string]::IsNullOrWhiteSpace($env:Preview__KubeconfigPath) -and (Test-Path -LiteralPath $homelabKubeconfig)) {
    $env:Preview__KubeconfigPath = (Resolve-Path -LiteralPath $homelabKubeconfig).Path
}

if ([string]::IsNullOrWhiteSpace($env:Pipelines__KubeconfigPath) -and (Test-Path -LiteralPath $homelabKubeconfig)) {
    $env:Pipelines__KubeconfigPath = (Resolve-Path -LiteralPath $homelabKubeconfig).Path
}

if ([string]::IsNullOrWhiteSpace($env:Storage__SqlitePath)) {
    $env:Storage__SqlitePath = Join-Path $repoRoot ".codex\devops-state.local.db"
}

$kubectlBaseArgs = Get-KubectlBaseArgs
if (-not $SkipClusterSecrets -and $ApiMode -eq "Local") {
    Set-EnvIfBlank "GitHub__Token" (Get-KubernetesSecretValue "rosenvall-devops-github" "token" $kubectlBaseArgs)
    Set-EnvIfBlank "GitHub__AppId" (Get-KubernetesSecretValue "rosenvall-devops-github-app" "app-id" $kubectlBaseArgs)
    Set-EnvIfBlank "GitHub__AppPrivateKey" (Get-KubernetesSecretValue "rosenvall-devops-github-app" "private-key" $kubectlBaseArgs)
    Set-EnvIfBlank "GitHub__AppSlug" (Get-KubernetesSecretValue "rosenvall-devops-github-app" "app-slug" $kubectlBaseArgs)
    Set-EnvIfBlank "GitHub__TokenSecretName" "rosenvall-devops-github"
    Set-EnvIfBlank "Repositories__Provider" "GitHub"
    Set-EnvIfBlank "Repositories__Mode" "GitHubApp"
}

if ($EnableAuth -or $ApiMode -eq "ClusterPortForward") {
    Set-EnvIfBlank "VITE_AUTH_ENABLED" "true"
    Set-EnvIfBlank "VITE_AUTH_AUTHORITY" "https://authentik.rosenvall.se/application/o/rosenvall-devops/"
    Set-EnvIfBlank "VITE_AUTH_CLIENT_ID" "rosenvall-devops"
    Set-EnvIfBlank "VITE_AUTH_REDIRECT_URI" "http://localhost:$FrontendPort/auth/callback"
    Set-EnvIfBlank "VITE_AUTH_POST_LOGOUT_REDIRECT_URI" "http://localhost:$FrontendPort/"
}

Get-CimInstance Win32_Process |
    Where-Object {
        ($_.Name -eq "dotnet.exe" -and $_.CommandLine -match "Rosenvall.DevOps.Api") -or
        ($_.Name -eq "node.exe" -and $_.CommandLine -match "vite") -or
        ($_.Name -eq "kubectl.exe" -and $_.CommandLine -match "rosenvall-devops-api" -and $_.CommandLine -match "port-forward")
    } |
    ForEach-Object { Stop-Process -Id $_.ProcessId -Force }

if ($ApiMode -eq "ClusterPortForward") {
    if (-not (Get-Command kubectl -ErrorAction SilentlyContinue)) {
        throw "kubectl is required for -ApiMode ClusterPortForward."
    }

    $apiArgs = @($kubectlBaseArgs + @("-n", "rosenvall-devops", "port-forward", "svc/rosenvall-devops-api", "$ApiPort`:8080"))
    $api = Start-Process -FilePath "kubectl" `
        -ArgumentList $apiArgs `
        -WorkingDirectory $repoRoot `
        -WindowStyle Hidden `
        -RedirectStandardOutput $apiOut `
        -RedirectStandardError $apiErr `
        -PassThru
} else {
    $api = Start-Process -FilePath "dotnet.exe" `
        -ArgumentList "run", "--project", $apiProject, "--urls", "http://localhost:$ApiPort" `
        -WorkingDirectory $repoRoot `
        -WindowStyle Hidden `
        -RedirectStandardOutput $apiOut `
        -RedirectStandardError $apiErr `
        -PassThru
}

$env:Preview__KubeconfigPath = $previousPreviewKubeconfig
$env:Pipelines__KubeconfigPath = $previousPipelinesKubeconfig
$env:Storage__SqlitePath = $previousStorageSqlitePath
$env:GitHub__Token = $previousGitHubToken
$env:GitHub__AppId = $previousGitHubAppId
$env:GitHub__AppPrivateKey = $previousGitHubAppPrivateKey
$env:GitHub__AppSlug = $previousGitHubAppSlug
$env:GitHub__TokenSecretName = $previousGitHubTokenSecretName
$env:Repositories__Provider = $previousRepositoriesProvider
$env:Repositories__Mode = $previousRepositoriesMode

if (-not (Wait-HttpOk "http://localhost:$ApiPort/healthz")) {
    Write-Warning "API did not become healthy on http://localhost:$ApiPort/healthz. Check $apiOut and $apiErr."
}

$frontend = Start-Process -FilePath "npm.cmd" `
    -ArgumentList "run", "dev", "--", "--host", "localhost", "--port", "$FrontendPort" `
    -WorkingDirectory $frontendRoot `
    -WindowStyle Hidden `
    -RedirectStandardOutput $frontendOut `
    -RedirectStandardError $frontendErr `
    -PassThru

if (-not (Wait-HttpOk "http://localhost:$FrontendPort")) {
    Write-Warning "Frontend did not start on http://localhost:$FrontendPort. Check $frontendOut and $frontendErr."
}

$env:VITE_AUTH_ENABLED = $previousViteAuthEnabled
$env:VITE_AUTH_AUTHORITY = $previousViteAuthAuthority
$env:VITE_AUTH_CLIENT_ID = $previousViteAuthClientId
$env:VITE_AUTH_REDIRECT_URI = $previousViteAuthRedirectUri
$env:VITE_AUTH_POST_LOGOUT_REDIRECT_URI = $previousViteAuthPostLogoutRedirectUri

Write-Host "Rosenvall DevOps demo is running."
Write-Host "Frontend: http://localhost:$FrontendPort"
Write-Host "API:      http://localhost:$ApiPort/healthz"
Write-Host "API mode: $ApiMode"
Write-Host "API PID:  $($api.Id)"
Write-Host "UI PID:   $($frontend.Id)"
Write-Host "Logs:     $logDir"
if (Test-Path -LiteralPath $homelabKubeconfig) {
    Write-Host "Kubeconfig: $homelabKubeconfig"
}
if (-not $SkipClusterSecrets -and $ApiMode -eq "Local") {
    Write-Host "Cluster secrets: GitHub App values loaded into the local API process when available."
}
if ($ApiMode -eq "ClusterPortForward") {
    Write-Host "Auth: local frontend is started with Authentik settings and localhost callback."
}
