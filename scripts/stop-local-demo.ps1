$ErrorActionPreference = "Stop"

Get-CimInstance Win32_Process |
    Where-Object {
        ($_.Name -eq "dotnet.exe" -and $_.CommandLine -match "Rosenvall.DevOps.Api") -or
        ($_.Name -eq "node.exe" -and $_.CommandLine -match "vite")
    } |
    ForEach-Object { Stop-Process -Id $_.ProcessId -Force }

Write-Host "Rosenvall DevOps local demo processes stopped."
