$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$assets = Join-Path (Split-Path -Parent $root) 'assets'
New-Item -ItemType Directory -Force $assets | Out-Null

$size = 256
$bmp = New-Object System.Drawing.Bitmap $size, $size
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit
$g.Clear([System.Drawing.Color]::Transparent)

$rect = New-Object System.Drawing.Rectangle 8, 8, 240, 240
$radius = 52
$path = New-Object System.Drawing.Drawing2D.GraphicsPath
$path.AddArc($rect.X, $rect.Y, $radius, $radius, 180, 90)
$path.AddArc($rect.Right - $radius, $rect.Y, $radius, $radius, 270, 90)
$path.AddArc($rect.Right - $radius, $rect.Bottom - $radius, $radius, $radius, 0, 90)
$path.AddArc($rect.X, $rect.Bottom - $radius, $radius, $radius, 90, 90)
$path.CloseFigure()

$brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
    (New-Object System.Drawing.Point 8, 8),
    (New-Object System.Drawing.Point 248, 248),
    [System.Drawing.Color]::FromArgb(14, 165, 233),
    [System.Drawing.Color]::FromArgb(37, 99, 235))
$g.FillPath($brush, $path)

$pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(90, 255, 255, 255)), 3
$g.DrawPath($pen, $path)

$font = New-Object System.Drawing.Font 'Segoe UI', 116, ([System.Drawing.FontStyle]::Bold), ([System.Drawing.GraphicsUnit]::Pixel)
$sf = New-Object System.Drawing.StringFormat
$sf.Alignment = [System.Drawing.StringAlignment]::Center
$sf.LineAlignment = [System.Drawing.StringAlignment]::Center
$textRect = New-Object System.Drawing.RectangleF 0, -8, 256, 256
$g.DrawString('En', $font, [System.Drawing.Brushes]::White, $textRect, $sf)

$linePen = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(230, 224, 242, 254)), 10
$linePen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
$linePen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
$g.DrawLine($linePen, 84, 196, 172, 196)

$png = Join-Path $env:TEMP 'vibelish-icon.png'
$bmp.Save($png, [System.Drawing.Imaging.ImageFormat]::Png)
$g.Dispose()
$bmp.Dispose()

$magick = Get-Command magick -ErrorAction Stop
& $magick.Source $png -define icon:auto-resize=256,128,64,48,32,16 (Join-Path $assets 'app.ico')
if ($LASTEXITCODE -ne 0) { throw 'magick failed' }
Write-Host ('icon: ' + (Get-Item (Join-Path $assets 'app.ico')).Length + ' bytes')
