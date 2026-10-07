<#
.SYNOPSIS
    Renders assets/*.svg into the multi-size familiar.ico used by the app and CLI, and the app's title bar PNG.

.DESCRIPTION
    Small sizes use the plain kitty face (familiar-small.svg); larger sizes use the cat in the witch hat
    (familiar.svg). Microsoft Edge renders every size directly from the SVG, so each one is drawn at its
    real pixel size rather than scaled down from a large bitmap. Run it again after editing either SVG.
#>
[CmdletBinding()]
param(
    [string] $Edge = "${env:ProgramFiles(x86)}\Microsoft\Edge\Application\msedge.exe",
    [string] $OutFile = "$PSScriptRoot\..\assets\familiar.ico",
    [string] $TitleBarFile = "$PSScriptRoot\..\src\Familiar.App\Assets\TitleBarIcon.png"
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$assets = Resolve-Path "$PSScriptRoot\..\assets"
$sizes = @(
    @{ Size = 16; Svg = 'familiar-small.svg' }
    @{ Size = 20; Svg = 'familiar-small.svg' }
    @{ Size = 24; Svg = 'familiar-small.svg' }
    @{ Size = 32; Svg = 'familiar-small.svg' }
    @{ Size = 40; Svg = 'familiar.svg' }
    @{ Size = 48; Svg = 'familiar.svg' }
    @{ Size = 64; Svg = 'familiar.svg' }
    @{ Size = 96; Svg = 'familiar.svg' }
    @{ Size = 128; Svg = 'familiar.svg' }
    @{ Size = 256; Svg = 'familiar.svg' }
    # Shown at 16 DIPs in the title bar; 64 pixels keeps it sharp up to 400% scaling.
    @{ Size = 64; Svg = 'familiar-small.svg'; TitleBar = $true }
)

# Lay every size out side by side on one transparent page, screenshot it once, then crop.
$work = New-Item -ItemType Directory -Path (Join-Path ([IO.Path]::GetTempPath()) "familiar-icon-$([guid]::NewGuid())")
try {
    $x = 0
    $images = foreach ($entry in $sizes) {
        $entry.X = $x
        $src = ([Uri](Join-Path $assets $entry.Svg)).AbsoluteUri
        "<img src='$src' style='position:absolute;left:${x}px;top:0;width:$($entry.Size)px;height:$($entry.Size)px'>"
        $x += $entry.Size + 8
    }
    $page = Join-Path $work 'icon.html'
    Set-Content $page "<html><body style='margin:0;background:transparent'>$($images -join '')</body></html>"

    $shot = Join-Path $work 'icon.png'
    & $Edge --headless=new --disable-gpu --hide-scrollbars --force-device-scale-factor=1 `
        --default-background-color=00000000 --allow-file-access-from-files `
        "--window-size=$x,256" "--user-data-dir=$work\profile" "--screenshot=$shot" ([Uri]$page).AbsoluteUri 2>$null | Out-Null
    for ($i = 0; -not (Test-Path $shot) -and $i -lt 100; $i++) { Start-Sleep -Milliseconds 100 }
    if (-not (Test-Path $shot)) { throw "Edge did not produce a screenshot." }

    $sheet = [Drawing.Bitmap]::new($shot)
    $frames = foreach ($entry in $sizes) {
        $s = $entry.Size
        $frame = $sheet.Clone([Drawing.Rectangle]::new($entry.X, 0, $s, $s), [Drawing.Imaging.PixelFormat]::Format32bppArgb)
        $stream = [IO.MemoryStream]::new()
        $frame.Save($stream, [Drawing.Imaging.ImageFormat]::Png)
        $frame.Dispose()
        [pscustomobject]@{ Size = $s; Bytes = $stream.ToArray(); TitleBar = [bool]$entry.TitleBar }
    }
    $sheet.Dispose()

    $titleBar = $frames | Where-Object TitleBar
    New-Item -ItemType Directory -Force -Path (Split-Path $TitleBarFile) | Out-Null
    [IO.File]::WriteAllBytes($TitleBarFile, $titleBar.Bytes)
    $frames = @($frames | Where-Object { -not $_.TitleBar })

    # ICO: ICONDIR, one ICONDIRENTRY per frame, then the PNG payloads.
    $out = [IO.MemoryStream]::new()
    $writer = [IO.BinaryWriter]::new($out)
    $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$frames.Count)
    $offset = 6 + 16 * $frames.Count
    foreach ($frame in $frames) {
        $dimension = [byte]($frame.Size % 256)
        $writer.Write($dimension); $writer.Write($dimension)
        $writer.Write([byte]0); $writer.Write([byte]0)
        $writer.Write([uint16]1); $writer.Write([uint16]32)
        $writer.Write([uint32]$frame.Bytes.Length); $writer.Write([uint32]$offset)
        $offset += $frame.Bytes.Length
    }
    foreach ($frame in $frames) { $writer.Write($frame.Bytes) }
    $writer.Flush()

    New-Item -ItemType Directory -Force -Path (Split-Path $OutFile) | Out-Null
    [IO.File]::WriteAllBytes($OutFile, $out.ToArray())
    Write-Host "Wrote $((Resolve-Path $OutFile).Path) ($($frames.Count) sizes)"
}
finally {
    Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue
}
