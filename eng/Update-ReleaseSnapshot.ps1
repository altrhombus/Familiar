#Requires -Version 7
<#
.SYNOPSIS
    Refreshes the release data bundled into Familiar.Core from DataForGeeks.

.DESCRIPTION
    Familiar downloads current release data at runtime and caches it. The bundled snapshot is only the
    offline fallback, so refresh it before a release rather than for every Windows feature update.
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

$baseUri = 'https://raw.githubusercontent.com/altrhombus/DataForGeeks/main/content/ms/win'
$destination = Join-Path $PSScriptRoot '..\src\Familiar.Core\Releases\Snapshot'
$files = 'releases', 'buildnumbers', 'lifecycle-client', 'lifecycle-ltsc', 'lifecycle-server'

foreach ($file in $files) {
    $target = Join-Path $destination "$file.json"
    Invoke-WebRequest -Uri "$baseUri/$file.json" -OutFile $target
    $metadata = (Get-Content $target -Raw | ConvertFrom-Json).metadata
    '{0,-18} {1,5} records, collected {2}' -f $file, $metadata.recordCount, $metadata.lastCollected
}
