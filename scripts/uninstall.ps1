<#
.SYNOPSIS
    Removes the Epic ERP Print Service.

.DESCRIPTION
    Stops and deletes the "EpicPrintService" Windows service, removes the install folder and the
    Event Log source.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\uninstall.ps1
#>
#Requires -RunAsAdministrator
[CmdletBinding()]
param(
    [string]$InstallDir = (Join-Path $env:ProgramFiles 'EpicErp\PrintService')
)

$ErrorActionPreference = 'Stop'

$ServiceName = 'EpicPrintService'
$EventSource = 'Epic Print Service'

$service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($service) {
    if ($service.Status -ne 'Stopped') {
        Write-Host "Stopping $ServiceName..." -ForegroundColor Cyan
        Stop-Service -Name $ServiceName -Force
        $service.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30))
    }
    Write-Host "Deleting service $ServiceName" -ForegroundColor Cyan
    & sc.exe delete $ServiceName | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "sc.exe delete failed with exit code $LASTEXITCODE" }

    # The service is only fully removed once nothing holds a handle to it (e.g. an open services.msc).
    $deadline = (Get-Date).AddSeconds(15)
    while ((Get-Service -Name $ServiceName -ErrorAction SilentlyContinue) -and (Get-Date) -lt $deadline) {
        Start-Sleep -Milliseconds 500
    }
} else {
    Write-Host "Service $ServiceName is not installed." -ForegroundColor Yellow
}

if (Test-Path $InstallDir) {
    Write-Host "Removing $InstallDir" -ForegroundColor Cyan
    Remove-Item $InstallDir -Recurse -Force
    $parent = Split-Path -Parent $InstallDir
    if ((Test-Path $parent) -and -not (Get-ChildItem $parent -Force)) { Remove-Item $parent -Force }
}

# Earlier versions kept a config file here.
$legacyConfig = Join-Path $env:ProgramData 'EpicErp\PrintService'
if (Test-Path $legacyConfig) {
    Remove-Item $legacyConfig -Recurse -Force
    $parent = Split-Path -Parent $legacyConfig
    if ((Test-Path $parent) -and -not (Get-ChildItem $parent -Force)) { Remove-Item $parent -Force }
}

if ([System.Diagnostics.EventLog]::SourceExists($EventSource)) {
    [System.Diagnostics.EventLog]::DeleteEventSource($EventSource)
}

Write-Host 'Epic ERP Print Service uninstalled.' -ForegroundColor Green
