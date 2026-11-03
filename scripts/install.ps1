<#
.SYNOPSIS
    Installs (or updates) the Epic ERP Print Service as an auto-start Windows service.

.DESCRIPTION
    - Copies the binaries to the install folder (default: C:\Program Files\EpicErp\PrintService).
    - Registers the "EpicPrintService" service: starts automatically at boot, runs as LocalSystem,
      and restarts itself if it crashes.
    - Starts it and checks that http://127.0.0.1:9555/api/v1/printers answers.

    Binaries come from (first match):
      1. -SourceDir
      2. .\app next to this script (the package built by publish.ps1)
      3. dotnet publish of ..\src\PrintService (needs the .NET 10 SDK)

    Running it again updates an existing installation.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\install.ps1
#>
#Requires -RunAsAdministrator
[CmdletBinding()]
param(
    [string]$InstallDir = (Join-Path $env:ProgramFiles 'EpicErp\PrintService'),
    [string]$SourceDir
)

$ErrorActionPreference = 'Stop'

$ServiceName = 'EpicPrintService'
$DisplayName = 'Epic ERP Print Service'
$Description = 'Local print API for Epic ERP (loopback only). Lists printers and prints PDFs on the default printer.'
$EventSource = 'Epic Print Service'
$ExeName = 'EpicErp.PrintService.exe'
$Port = 9555
$ApiKey = 'epichealthcare@2026printserer'

# ---------------------------------------------------------------- locate / build binaries
$tempPublish = $null
if (-not $SourceDir) {
    $packaged = Join-Path $PSScriptRoot 'app'
    if (Test-Path (Join-Path $packaged $ExeName)) {
        $SourceDir = $packaged
    } else {
        $project = Join-Path $PSScriptRoot '..\src\PrintService\PrintService.csproj'
        if (-not (Test-Path $project)) { throw 'No binaries found. Pass -SourceDir or run publish.ps1 first.' }
        $tempPublish = Join-Path ([System.IO.Path]::GetTempPath()) ('EpicPrintService-' + [guid]::NewGuid().ToString('N'))
        Write-Host 'Publishing from source...' -ForegroundColor Cyan
        dotnet publish $project -c Release -r win-x64 --self-contained true -o $tempPublish -p:DebugType=None -p:DebugSymbols=false
        if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE" }
        $SourceDir = $tempPublish
    }
}
if (-not (Test-Path (Join-Path $SourceDir $ExeName))) { throw "$ExeName not found in $SourceDir" }

# ---------------------------------------------------------------- stop existing service
$existingService = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($existingService -and $existingService.Status -ne 'Stopped') {
    Write-Host "Stopping $ServiceName..." -ForegroundColor Cyan
    Stop-Service -Name $ServiceName -Force
    $existingService.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30))
}

# ---------------------------------------------------------------- copy binaries
Write-Host "Installing to $InstallDir" -ForegroundColor Cyan
if (-not (Test-Path $InstallDir)) { New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null }
Copy-Item -Path (Join-Path $SourceDir '*') -Destination $InstallDir -Recurse -Force
if ($tempPublish) { Remove-Item $tempPublish -Recurse -Force -ErrorAction SilentlyContinue }

# Earlier versions kept a config file (API key hash etc.) here. It's no longer used.
$legacyConfig = Join-Path $env:ProgramData 'EpicErp\PrintService'
if (Test-Path $legacyConfig) {
    Write-Host "Removing old config $legacyConfig" -ForegroundColor Cyan
    Remove-Item $legacyConfig -Recurse -Force
    $parent = Split-Path -Parent $legacyConfig
    if ((Test-Path $parent) -and -not (Get-ChildItem $parent -Force)) { Remove-Item $parent -Force }
}

# ---------------------------------------------------------------- event log source
if (-not [System.Diagnostics.EventLog]::SourceExists($EventSource)) {
    [System.Diagnostics.EventLog]::CreateEventSource($EventSource, 'Application')
}

# ---------------------------------------------------------------- register service
$exePath = Join-Path $InstallDir $ExeName
if (-not $existingService) {
    Write-Host "Registering service $ServiceName" -ForegroundColor Cyan
    New-Service -Name $ServiceName -BinaryPathName "`"$exePath`"" -DisplayName $DisplayName `
        -Description $Description -StartupType Automatic | Out-Null
} else {
    Set-Service -Name $ServiceName -StartupType Automatic -DisplayName $DisplayName -Description $Description
}

# Restart on crash: after 5s, 5s, then 30s; reset the failure counter after a day.
& sc.exe failure $ServiceName reset= 86400 actions= restart/5000/restart/5000/restart/30000 | Out-Null
& sc.exe failureflag $ServiceName 1 | Out-Null

Write-Host "Starting $ServiceName..." -ForegroundColor Cyan
Start-Service -Name $ServiceName
(Get-Service -Name $ServiceName).WaitForStatus('Running', [TimeSpan]::FromSeconds(30))

# ---------------------------------------------------------------- self-check
$ok = $false
$deadline = (Get-Date).AddSeconds(20)
while (-not $ok -and (Get-Date) -lt $deadline) {
    try {
        $r = Invoke-WebRequest -UseBasicParsing -TimeoutSec 5 -Uri "http://127.0.0.1:$Port/api/v1/printers" -Headers @{ 'X-Api-Key' = $ApiKey }
        $ok = $r.StatusCode -eq 200
    } catch { Start-Sleep -Milliseconds 500 }
}

Write-Host ''
Write-Host "Installed. Service '$ServiceName' is running and starts automatically at boot." -ForegroundColor Green
Write-Host "API: http://127.0.0.1:$Port/api/v1/" -ForegroundColor Green
if ($ok) {
    Write-Host 'Self-check: GET /api/v1/printers returned 200.' -ForegroundColor Green
} else {
    Write-Warning "Self-check failed: the API did not answer on port $Port. Check Event Viewer > Windows Logs > Application (source '$EventSource')."
}
