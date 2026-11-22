<#
Builds the bundled 1280x720 JPEG music genre artwork from TuneIn genre logos.
Run on Windows PowerShell; the output is committed under MediaBrowser.Plugins.TuneIn\Images\genres.
Genres without a TuneIn genre logo are skipped and use the Music category artwork at runtime.
#>
param([string]$Output = (Join-Path $PSScriptRoot '..\MediaBrowser.Plugins.TuneIn\Images\genres'))
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

function Get-Opml([string]$url) { [xml](Invoke-WebRequest -UseBasicParsing $url).Content }

$width = 1280; $height = 720; $tile = 290; $radius = 36; $shadow = 10
function New-RoundedTile([float]$left, [float]$top) {
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $radius * 2
    $path.AddArc($left, $top, $d, $d, 180, 90); $path.AddArc($left + $tile - $d, $top, $d, $d, 270, 90)
    $path.AddArc($left + $tile - $d, $top + $tile - $d, $d, $d, 0, 90); $path.AddArc($left, $top + $tile - $d, $d, $d, 90, 90)
    $path.CloseFigure()
    $path
}

$ids = New-Object System.Collections.Generic.List[string]
foreach ($top in (Get-Opml 'http://opml.radiotime.com/Browse.ashx?c=music').SelectNodes('//outline[@type="link"]')) {
    $ids.Add($top.guide_id)
    foreach ($sub in (Get-Opml "http://opml.radiotime.com/Browse.ashx?id=$($top.guide_id)").SelectNodes('//outline[@type="link"]')) {
        if ($sub.guide_id -match '^g\d+$') { $ids.Add($sub.guide_id) }
    }
    Start-Sleep -Milliseconds 500
}

New-Item -ItemType Directory -Force $Output | Out-Null
$jpeg = [System.Drawing.Imaging.ImageCodecInfo]::GetImageEncoders() | Where-Object { $_.MimeType -eq 'image/jpeg' }
$quality = New-Object System.Drawing.Imaging.EncoderParameters(1)
$quality.Param[0] = New-Object System.Drawing.Imaging.EncoderParameter([System.Drawing.Imaging.Encoder]::Quality, [long]90)
foreach ($id in ($ids | Sort-Object -Unique)) {
    # Only genre profile logos are used; genres without one return HTTP 403.
    try { $bytes = (Invoke-WebRequest -UseBasicParsing "https://cdn-profiles.tunein.com/$id/images/logoq.png").Content }
    catch { continue }
    $logo = [System.Drawing.Image]::FromStream((New-Object System.IO.MemoryStream(, $bytes)))
    $canvas = New-Object System.Drawing.Bitmap($width, $height, [System.Drawing.Imaging.PixelFormat]::Format24bppRgb)
    $g = [System.Drawing.Graphics]::FromImage($canvas)
    $g.SmoothingMode = 'AntiAlias'; $g.InterpolationMode = 'HighQualityBicubic'; $g.PixelOffsetMode = 'HighQuality'
    $bounds = New-Object System.Drawing.Rectangle(0, 0, $width, $height)
    $gradient = New-Object System.Drawing.Drawing2D.LinearGradientBrush($bounds,
        [System.Drawing.Color]::FromArgb(20, 27, 43), [System.Drawing.Color]::FromArgb(112, 66, 180), 30.0)
    $g.FillRectangle($gradient, $bounds)
    $x = ($width - $tile) / 2; $y = ($height - $tile) / 2
    $shade = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(70, 0, 0, 0))
    $g.FillPath($shade, (New-RoundedTile ($x + $shadow) ($y + $shadow)))
    $clip = New-RoundedTile $x $y
    $g.FillPath([System.Drawing.Brushes]::White, $clip)
    $g.SetClip($clip)
    $g.DrawImage($logo, $x, $y, $tile, $tile)
    $g.ResetClip()
    $canvas.Save((Join-Path $Output "$id.jpg"), $jpeg, $quality)
    $g.Dispose(); $canvas.Dispose(); $logo.Dispose(); $gradient.Dispose(); $shade.Dispose()
    Write-Host "Built $id"
}
