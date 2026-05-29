param(
    [int]$ApiPort = 5088,
    [int]$FrontendPort = 5173,
    [int]$ForgejoPort = 3001,
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
$forgejoOut = Join-Path $logDir "forgejo.out.log"
$forgejoErr = Join-Path $logDir "forgejo.err.log"
$homelabKubeconfig = Join-Path (Split-Path $repoRoot -Parent) "Rosenvalls-Homelab\tofu\output\kubeconfig"
$previousPreviewKubeconfig = $env:Preview__KubeconfigPath
$previousPipelinesKubeconfig = $env:Pipelines__KubeconfigPath
$previousPreviewSourceMode = $env:Ai__Codex__PreviewSourceMode
$previousPreviewSourceJobTimeoutSeconds = $env:Ai__Codex__PreviewSourceJobTimeoutSeconds
$previousCodexKubernetesRunnerImage = $env:Ai__Codex__KubernetesRunnerImage
$previousStorageSqlitePath = $env:Storage__SqlitePath
$previousGitHubToken = $env:GitHub__Token
$previousGitHubAppId = $env:GitHub__AppId
$previousGitHubAppPrivateKey = $env:GitHub__AppPrivateKey
$previousGitHubAppSlug = $env:GitHub__AppSlug
$previousGitHubAppClientId = $env:GitHub__AppClientId
$previousGitHubAppClientSecret = $env:GitHub__AppClientSecret
$previousGitHubTokenSecretName = $env:GitHub__TokenSecretName
$previousRepositoriesProvider = $env:Repositories__Provider
$previousRepositoriesMode = $env:Repositories__Mode
$previousLocalGitEnabled = $env:LocalGit__Enabled
$previousLocalGitApiBaseUrl = $env:LocalGit__ApiBaseUrl
$previousLocalGitRunnerApiBaseUrl = $env:LocalGit__RunnerApiBaseUrl
$previousLocalGitCloneBaseUrl = $env:LocalGit__CloneBaseUrl
$previousLocalGitOwner = $env:LocalGit__Owner
$previousLocalGitUsername = $env:LocalGit__Username
$previousLocalGitPassword = $env:LocalGit__Password
$previousLocalGitUnavailableReason = $env:LocalGit__UnavailableReason
$previousViteAuthEnabled = $env:VITE_AUTH_ENABLED
$previousViteAuthAuthority = $env:VITE_AUTH_AUTHORITY
$previousViteAuthClientId = $env:VITE_AUTH_CLIENT_ID
$previousViteAuthRedirectUri = $env:VITE_AUTH_REDIRECT_URI
$previousViteAuthPostLogoutRedirectUri = $env:VITE_AUTH_POST_LOGOUT_REDIRECT_URI
$previousViteAuthProxyPrefix = $env:VITE_AUTH_PROXY_PREFIX
$previousAuthenticationAuthority = $env:Authentication__Authority
$previousAuthenticationAudience = $env:Authentication__Audience

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

    try {
        $json = & kubectl @KubectlBaseArgs -n rosenvall-devops get secret $Name -o json 2>$null
        if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($json)) {
            return $null
        }
    } catch {
        return $null
    }

    $secret = $json | ConvertFrom-Json
    $property = $secret.data.PSObject.Properties[$Key]
    if ($null -eq $property -or [string]::IsNullOrWhiteSpace($property.Value)) {
        return $null
    }

    return [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($property.Value))
}

function Get-KubernetesConfigMapValue {
    param(
        [string]$Name,
        [string]$Key,
        [string[]]$KubectlBaseArgs
    )

    if (-not (Get-Command kubectl -ErrorAction SilentlyContinue)) {
        return $null
    }

    try {
        $json = & kubectl @KubectlBaseArgs -n rosenvall-devops get configmap $Name -o json 2>$null
        if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($json)) {
            return $null
        }
    } catch {
        return $null
    }

    $configMap = $json | ConvertFrom-Json
    $property = $configMap.data.PSObject.Properties[$Key]
    if ($null -eq $property -or [string]::IsNullOrWhiteSpace($property.Value)) {
        return $null
    }

    return $property.Value
}

function Test-KubernetesResource {
    param(
        [string[]]$KubectlBaseArgs,
        [string[]]$Arguments
    )

    if (-not (Get-Command kubectl -ErrorAction SilentlyContinue)) {
        return $false
    }

    try {
        & kubectl @KubectlBaseArgs @Arguments *> $null
        return $LASTEXITCODE -eq 0
    } catch {
        return $false
    }
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

if (Test-Path -LiteralPath $homelabKubeconfig) {
    $resolvedHomelabKubeconfig = (Resolve-Path -LiteralPath $homelabKubeconfig).Path
    Set-EnvIfBlank "Preview__KubeconfigPath" $resolvedHomelabKubeconfig
    Set-EnvIfBlank "Pipelines__KubeconfigPath" $resolvedHomelabKubeconfig
    Set-EnvIfBlank "Ai__Codex__PreviewSourceMode" "kubernetes-job"
    Set-EnvIfBlank "Ai__Codex__PreviewSourceJobTimeoutSeconds" "600"
} else {
    Set-EnvIfBlank "Ai__Codex__PreviewSourceMode" "in-process"
    Write-Warning "Homelab kubeconfig was not found at $homelabKubeconfig. Kubernetes preview-source jobs are disabled for this local run."
}

if ([string]::IsNullOrWhiteSpace($env:Storage__SqlitePath)) {
    $env:Storage__SqlitePath = Join-Path $repoRoot ".codex\devops-state.local.db"
}

$kubectlBaseArgs = Get-KubectlBaseArgs
if (Test-Path -LiteralPath $homelabKubeconfig) {
    Set-EnvIfBlank "Ai__Codex__KubernetesRunnerImage" (Get-KubernetesConfigMapValue "rosenvall-devops-config" "Ai__Codex__KubernetesRunnerImage" $kubectlBaseArgs)
}

if (-not $SkipClusterSecrets -and $ApiMode -eq "Local") {
    Set-EnvIfBlank "GitHub__Token" (Get-KubernetesSecretValue "rosenvall-devops-github" "token" $kubectlBaseArgs)
    Set-EnvIfBlank "GitHub__AppId" (Get-KubernetesSecretValue "rosenvall-devops-github-app" "app-id" $kubectlBaseArgs)
    Set-EnvIfBlank "GitHub__AppPrivateKey" (Get-KubernetesSecretValue "rosenvall-devops-github-app" "private-key" $kubectlBaseArgs)
    Set-EnvIfBlank "GitHub__AppSlug" (Get-KubernetesSecretValue "rosenvall-devops-github-app" "app-slug" $kubectlBaseArgs)
    Set-EnvIfBlank "GitHub__AppClientId" (Get-KubernetesSecretValue "rosenvall-devops-github-app" "client-id" $kubectlBaseArgs)
    Set-EnvIfBlank "GitHub__AppClientSecret" (Get-KubernetesSecretValue "rosenvall-devops-github-app" "client-secret" $kubectlBaseArgs)
    Set-EnvIfBlank "GitHub__TokenSecretName" "rosenvall-devops-github"
    Set-EnvIfBlank "Repositories__Provider" "GitHub"
    Set-EnvIfBlank "Repositories__Mode" "GitHubApp"
}

if ($EnableAuth -or $ApiMode -eq "ClusterPortForward") {
    Set-EnvIfBlank "Authentication__Authority" "https://authentik.rosenvall.se/application/o/rosenvall-devops/"
    Set-EnvIfBlank "Authentication__Audience" "rosenvall-devops"
    Set-EnvIfBlank "VITE_AUTH_ENABLED" "true"
    Set-EnvIfBlank "VITE_AUTH_AUTHORITY" "https://authentik.rosenvall.se/application/o/rosenvall-devops/"
    Set-EnvIfBlank "VITE_AUTH_CLIENT_ID" "rosenvall-devops"
    Set-EnvIfBlank "VITE_AUTH_REDIRECT_URI" "http://localhost:$FrontendPort/auth/callback"
    Set-EnvIfBlank "VITE_AUTH_POST_LOGOUT_REDIRECT_URI" "http://localhost:$FrontendPort/"
    Set-EnvIfBlank "VITE_AUTH_PROXY_PREFIX" "/authentik"
}

Get-CimInstance Win32_Process |
    Where-Object {
        ($_.Name -eq "dotnet.exe" -and $_.CommandLine -match "Rosenvall.DevOps.Api") -or
        ($_.Name -eq "node.exe" -and $_.CommandLine -match "vite") -or
        ($_.Name -eq "kubectl.exe" -and $_.CommandLine -match "rosenvall-devops-api" -and $_.CommandLine -match "port-forward") -or
        ($_.Name -eq "kubectl.exe" -and $_.CommandLine -match "rosenvall-devops-forgejo" -and $_.CommandLine -match "port-forward")
    } |
    ForEach-Object { Stop-Process -Id $_.ProcessId -Force }

$forgejo = $null
if ($ApiMode -eq "Local" -and (Test-Path -LiteralPath $homelabKubeconfig) -and -not $SkipClusterSecrets) {
    $forgejoServiceReady = Test-KubernetesResource -KubectlBaseArgs $kubectlBaseArgs -Arguments @("-n", "rosenvall-devops", "get", "svc", "rosenvall-devops-forgejo")
    $forgejoSecretReady = Test-KubernetesResource -KubectlBaseArgs $kubectlBaseArgs -Arguments @("-n", "rosenvall-devops", "get", "secret", "rosenvall-devops-forgejo-admin")
    $forgejoUsername = Get-KubernetesSecretValue "rosenvall-devops-forgejo-admin" "username" $kubectlBaseArgs
    $forgejoPassword = Get-KubernetesSecretValue "rosenvall-devops-forgejo-admin" "password" $kubectlBaseArgs

    Set-EnvIfBlank "LocalGit__Enabled" "true"
    Set-EnvIfBlank "LocalGit__ApiBaseUrl" "http://localhost:$ForgejoPort/api/v1"
    Set-EnvIfBlank "LocalGit__RunnerApiBaseUrl" "http://rosenvall-devops-forgejo.rosenvall-devops.svc.cluster.local:3000/api/v1"
    Set-EnvIfBlank "LocalGit__CloneBaseUrl" "http://rosenvall-devops-forgejo.rosenvall-devops.svc.cluster.local:3000"
    Set-EnvIfBlank "LocalGit__Owner" "rdo"
    Set-EnvIfBlank "LocalGit__Username" $(if ([string]::IsNullOrWhiteSpace($forgejoUsername)) { "rdo" } else { $forgejoUsername })

    if ($forgejoServiceReady -and $forgejoSecretReady -and -not [string]::IsNullOrWhiteSpace($forgejoPassword)) {
        Set-EnvIfBlank "LocalGit__Password" $forgejoPassword
        $env:LocalGit__UnavailableReason = $null
        $forgejoArgs = @($kubectlBaseArgs + @("-n", "rosenvall-devops", "port-forward", "svc/rosenvall-devops-forgejo", "$ForgejoPort`:3000"))
        $forgejo = Start-Process -FilePath "kubectl" `
            -ArgumentList $forgejoArgs `
            -WorkingDirectory $repoRoot `
            -WindowStyle Hidden `
            -RedirectStandardOutput $forgejoOut `
            -RedirectStandardError $forgejoErr `
            -PassThru

        if (-not (Wait-HttpOk "http://localhost:$ForgejoPort/api/healthz" 20)) {
            $env:LocalGit__UnavailableReason = "Local Git is configured, but Forgejo did not become reachable through localhost port-forward. Check $forgejoOut and $forgejoErr."
            Write-Warning $env:LocalGit__UnavailableReason
        }
    } else {
        $env:LocalGit__UnavailableReason = "Local Git is enabled, but Forgejo is not deployed or the rosenvall-devops-forgejo-admin secret is missing. Sync the rosenvall-devops Homelab app and restart local demo."
        Write-Warning $env:LocalGit__UnavailableReason
    }
}

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
$env:Ai__Codex__PreviewSourceMode = $previousPreviewSourceMode
$env:Ai__Codex__PreviewSourceJobTimeoutSeconds = $previousPreviewSourceJobTimeoutSeconds
$env:Ai__Codex__KubernetesRunnerImage = $previousCodexKubernetesRunnerImage
$env:Storage__SqlitePath = $previousStorageSqlitePath
$env:GitHub__Token = $previousGitHubToken
$env:GitHub__AppId = $previousGitHubAppId
$env:GitHub__AppPrivateKey = $previousGitHubAppPrivateKey
$env:GitHub__AppSlug = $previousGitHubAppSlug
$env:GitHub__AppClientId = $previousGitHubAppClientId
$env:GitHub__AppClientSecret = $previousGitHubAppClientSecret
$env:GitHub__TokenSecretName = $previousGitHubTokenSecretName
$env:Repositories__Provider = $previousRepositoriesProvider
$env:Repositories__Mode = $previousRepositoriesMode
$env:LocalGit__Enabled = $previousLocalGitEnabled
$env:LocalGit__ApiBaseUrl = $previousLocalGitApiBaseUrl
$env:LocalGit__RunnerApiBaseUrl = $previousLocalGitRunnerApiBaseUrl
$env:LocalGit__CloneBaseUrl = $previousLocalGitCloneBaseUrl
$env:LocalGit__Owner = $previousLocalGitOwner
$env:LocalGit__Username = $previousLocalGitUsername
$env:LocalGit__Password = $previousLocalGitPassword
$env:LocalGit__UnavailableReason = $previousLocalGitUnavailableReason
$env:Authentication__Authority = $previousAuthenticationAuthority
$env:Authentication__Audience = $previousAuthenticationAudience

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
$env:VITE_AUTH_PROXY_PREFIX = $previousViteAuthProxyPrefix

Write-Host "Rosenvall DevOps demo is running."
Write-Host "Frontend: http://localhost:$FrontendPort"
Write-Host "API:      http://localhost:$ApiPort/healthz"
Write-Host "API mode: $ApiMode"
Write-Host "API PID:  $($api.Id)"
Write-Host "UI PID:   $($frontend.Id)"
if ($null -ne $forgejo) {
    Write-Host "Forgejo: http://localhost:$ForgejoPort/api/v1"
    Write-Host "Forgejo PID: $($forgejo.Id)"
}
Write-Host "Logs:     $logDir"
if (Test-Path -LiteralPath $homelabKubeconfig) {
    Write-Host "Kubeconfig: $homelabKubeconfig"
}
if (-not $SkipClusterSecrets -and $ApiMode -eq "Local") {
    Write-Host "Cluster secrets: GitHub App and LocalGit values loaded into the local API process when available."
}
if ($EnableAuth -or $ApiMode -eq "ClusterPortForward") {
    Write-Host "Auth: enabled with Authentik, localhost callback."
}
