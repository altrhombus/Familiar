#Requires -Version 7
<#
.SYNOPSIS
    Publishes familiar.exe as one self-contained file that runs on a clean Windows machine (no .NET install).

.EXAMPLE
    ./eng/Publish-Cli.ps1                  # artifacts\familiar-win-x64\familiar.exe
    ./eng/Publish-Cli.ps1 -Runtime win-arm64
#>
[CmdletBinding()]
param(
    [ValidateSet('win-x64', 'win-arm64')]
    [string]$Runtime = 'win-x64'
)

$ErrorActionPreference = 'Stop'

$root = Split-Path $PSScriptRoot -Parent
$output = Join-Path $root "artifacts\familiar-$Runtime"

dotnet publish (Join-Path $root 'src\Familiar.Cli') `
    --configuration Release `
    --runtime $Runtime `
    --self-contained `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:DebugType=none `
    --output $output

if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)." }

Get-Item (Join-Path $output 'familiar.exe') | Select-Object FullName, @{ n = 'SizeMB'; e = { [math]::Round($_.Length / 1MB, 1) } }
