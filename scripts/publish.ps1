<#
.SYNOPSIS
    Builds a self-contained distributable package of the Epic ERP Print Service.

.DESCRIPTION
    Produces dist\EpicPrintService\ (and a .zip of it) containing:
        app\            self-contained win-x64 binaries (no .NET runtime needed on the target PC)
        install.ps1
        uninstall.ps1
    Copy the folder/zip to the target PC and run install.ps1 as Administrator.
    Requires the .NET 10 SDK on the build machine only.
#>
[CmdletBinding()]
param(
    [ValidateSet('win-x64', 'win-x86', 'win-arm64')]
    [string]$Runtime = 'win-x64',
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'src\PrintService\PrintService.csproj'
$packageDir = Join-Path $root 'dist\EpicPrintService'
$appDir = Join-Path $packageDir 'app'
$zip = Join-Path $root "dist\EpicPrintService-$Runtime.zip"

if (Test-Path $packageDir) { Remove-Item $packageDir -Recurse -Force }
if (Test-Path $zip) { Remove-Item $zip -Force }

Write-Host "Publishing $Runtime ($Configuration)..." -ForegroundColor Cyan
dotnet publish $project -c $Configuration -r $Runtime --self-contained true -o $appDir -p:DebugType=None -p:DebugSymbols=false
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE" }

Copy-Item (Join-Path $PSScriptRoot 'install.ps1') $packageDir
Copy-Item (Join-Path $PSScriptRoot 'uninstall.ps1') $packageDir

Compress-Archive -Path $packageDir -DestinationPath $zip
Write-Host "Package: $packageDir" -ForegroundColor Green
Write-Host "Zip:     $zip" -ForegroundColor Green
