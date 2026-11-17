[CmdletBinding()]
param([string]$SourceImage)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$assets = Join-Path $PSScriptRoot 'FNSyncPlus.WinUI\Assets'
$master = Join-Path $assets 'BrandMark.png'

function Convert-BrandImage([Drawing.Image]$Image, [int]$Width, [int]$Height) {
    $imageSize = [Math]::Min($Width, $Height)
    $bitmap = [Drawing.Bitmap]::new($Width, $Height, [Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [Drawing.Graphics]::FromImage($bitmap)
    try {
        $graphics.CompositingMode = [Drawing.Drawing2D.CompositingMode]::SourceCopy
        $graphics.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $graphics.PixelOffsetMode = [Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $rect = [Drawing.Rectangle]::new([int](($Width - $imageSize) / 2), [int](($Height - $imageSize) / 2), $imageSize, $imageSize)
        $graphics.DrawImage($Image, $rect)
        $stream = [IO.MemoryStream]::new()
        try {
            $bitmap.Save($stream, [Drawing.Imaging.ImageFormat]::Png)
            return ,$stream.ToArray()
        } finally { $stream.Dispose() }
    } finally {
        $graphics.Dispose()
        $bitmap.Dispose()
    }
}

if ($SourceImage) {
    Copy-Item -LiteralPath (Resolve-Path -LiteralPath $SourceImage).Path -Destination $master -Force
}

$logo = [Drawing.Image]::FromFile($master)
try {
    $sizes = @(16, 20, 24, 32, 48, 64, 128, 256)
    $frames = [System.Collections.Generic.List[byte[]]]::new()
    foreach ($size in $sizes) { $frames.Add((Convert-BrandImage $logo $size $size)) }

    $stream = [IO.MemoryStream]::new()
    $writer = [IO.BinaryWriter]::new($stream)
    try {
        $writer.Write([uint16]0)
        $writer.Write([uint16]1)
        $writer.Write([uint16]$sizes.Count)
        $offset = 6 + 16 * $sizes.Count
        for ($i = 0; $i -lt $sizes.Count; $i++) {
            $dimension = [byte]($sizes[$i] % 256)
            $writer.Write($dimension)
            $writer.Write($dimension)
            $writer.Write([uint16]0)
            $writer.Write([uint16]1)
            $writer.Write([uint16]32)
            $writer.Write([uint32]$frames[$i].Length)
            $writer.Write([uint32]$offset)
            $offset += $frames[$i].Length
        }
        foreach ($frame in $frames) { $writer.Write($frame) }
        [IO.File]::WriteAllBytes((Join-Path $assets 'AppIcon.ico'), $stream.ToArray())
    } finally { $writer.Dispose() }

    $sizesByAsset = @{
        'LockScreenLogo.scale-200.png' = @(48, 48)
        'SplashScreen.scale-200.png' = @(1240, 600)
        'Square150x150Logo.scale-200.png' = @(300, 300)
        'Square44x44Logo.scale-200.png' = @(88, 88)
        'Square44x44Logo.targetsize-24_altform-unplated.png' = @(24, 24)
        'Square44x44Logo.targetsize-48_altform-lightunplated.png' = @(48, 48)
        'StoreLogo.png' = @(50, 50)
        'Wide310x150Logo.scale-200.png' = @(620, 300)
    }
    foreach ($asset in $sizesByAsset.Keys) {
        $width, $height = $sizesByAsset[$asset]
        [IO.File]::WriteAllBytes((Join-Path $assets $asset), (Convert-BrandImage $logo $width $height))
    }
} finally { $logo.Dispose() }
