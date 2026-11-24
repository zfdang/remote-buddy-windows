[CmdletBinding()]
param(
    [string]$SourceDir = (Join-Path (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)) "remote-buddy\assets")
)

$ErrorActionPreference = "Stop"
$RepoRoot = Split-Path -Parent $PSScriptRoot
$destDir = Join-Path $RepoRoot "src\RemoteBuddy.App\Resources"

if (-not (Test-Path $SourceDir)) {
    throw "Source asset directory not found: $SourceDir"
}

foreach ($asset in @("AppIcon.png", "ChromecastG9N9NPhoto.png", "T1RemoteIllustration.png", "BuddyRemoteIllustration.png")) {
    $srcPath = Join-Path $SourceDir $asset
    if (-not (Test-Path $srcPath)) {
        throw "Required source asset not found: $srcPath"
    }
}

Copy-Item (Join-Path $SourceDir "AppIcon.png") (Join-Path $destDir "app.png") -Force
Copy-Item (Join-Path $SourceDir "ChromecastG9N9NPhoto.png") (Join-Path $destDir "ChromecastG9N9NPhoto.png") -Force
Copy-Item (Join-Path $SourceDir "T1RemoteIllustration.png") (Join-Path $destDir "T1RemoteIllustration.png") -Force
Copy-Item (Join-Path $SourceDir "BuddyRemoteIllustration.png") (Join-Path $destDir "BuddyRemoteIllustration.png") -Force

Add-Type -AssemblyName System.Drawing

$pngPath = "$destDir\app.png"
$icoPath = "$destDir\app.ico"

$origImg = [System.Drawing.Image]::FromFile($pngPath)
$sizes = @(16, 32, 48, 64, 128, 256)

$pngBytesList = [System.Collections.Generic.List[byte[]]]::new()

foreach ($s in $sizes) {
    $bmp = [System.Drawing.Bitmap]::new($s, $s)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.DrawImage($origImg, 0, 0, $s, $s)
    $g.Dispose()

    $ms = [System.IO.MemoryStream]::new()
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $pngBytesList.Add($ms.ToArray())
    $ms.Dispose()
    $bmp.Dispose()
}
$origImg.Dispose()

$fs = [System.IO.File]::Create($icoPath)
$bw = [System.IO.BinaryWriter]::new($fs)

# Header
$bw.Write([System.UInt16]0) # Reserved
$bw.Write([System.UInt16]1) # Type (1=Icon)
$bw.Write([System.UInt16]$sizes.Length) # Image count

$offset = 6 + ($sizes.Length * 16)
for ($i = 0; $i -lt $sizes.Length; $i++) {
    $s = $sizes[$i]
    $bytes = $pngBytesList[$i]
    $w = if ($s -ge 256) { [byte]0 } else { [byte]$s }
    $h = if ($s -ge 256) { [byte]0 } else { [byte]$s }
    $bw.Write($w)
    $bw.Write($h)
    $bw.Write([byte]0) # Color palette
    $bw.Write([byte]0) # Reserved
    $bw.Write([System.UInt16]1) # Color planes
    $bw.Write([System.UInt16]32) # Bits per pixel
    $bw.Write([System.UInt32]$bytes.Length) # Data length
    $bw.Write([System.UInt32]$offset) # Data offset
    $offset += $bytes.Length
}

foreach ($bytes in $pngBytesList) {
    $bw.Write($bytes)
}

$bw.Flush()
$bw.Close()
$fs.Dispose()

Write-Host "Generated app.ico successfully. Size: $((Get-Item $icoPath).Length)"
