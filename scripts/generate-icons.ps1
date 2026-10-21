<#
.SYNOPSIS
  Generates the MSIX/Command Palette PNG assets for Lexi (simple rounded-square "Lx" logo).
.NOTES
  Requires Windows PowerShell 5.1 (System.Drawing). Output: src/Lexi.Extension/Assets.
#>
param(
    [string]$OutDir = (Join-Path $PSScriptRoot '..\src\Lexi.Extension\Assets')
)

Add-Type -AssemblyName System.Drawing
New-Item -ItemType Directory -Force $OutDir | Out-Null

function New-RoundedPath([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = 2 * $r
    $p.AddArc($x, $y, $d, $d, 180, 90)
    $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    return $p
}

function Save-Logo([string]$name, [int]$width, [int]$height, [double]$markRatio = 1.0) {
    $bmp = New-Object System.Drawing.Bitmap($width, $height, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit
    $g.Clear([System.Drawing.Color]::Transparent)

    # Square mark centered in the canvas
    $side = [Math]::Min($width, $height) * $markRatio
    $x = ($width - $side) / 2
    $y = ($height - $side) / 2
    $rect = New-Object System.Drawing.RectangleF($x, $y, $side, $side)
    $c1 = [System.Drawing.Color]::FromArgb(255, 37, 99, 235)   # blue-600
    $c2 = [System.Drawing.Color]::FromArgb(255, 14, 165, 233)  # sky-500
    $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush($rect, $c1, $c2, 45.0)
    $path = New-RoundedPath $x $y $side $side ($side * 0.22)
    $g.FillPath($brush, $path)

    # "Lx" wordmark
    $fontSize = [float]($side * 0.46)
    $font = New-Object System.Drawing.Font('Segoe UI Semibold', $fontSize, [System.Drawing.FontStyle]::Regular, [System.Drawing.GraphicsUnit]::Pixel)
    $fmt = New-Object System.Drawing.StringFormat
    $fmt.Alignment = [System.Drawing.StringAlignment]::Center
    $fmt.LineAlignment = [System.Drawing.StringAlignment]::Center
    $textRect = New-Object System.Drawing.RectangleF($x, ($y + $side * 0.02), $side, $side)
    $g.DrawString('Lx', $font, [System.Drawing.Brushes]::White, $textRect, $fmt)

    $file = Join-Path $OutDir $name
    $bmp.Save($file, [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose(); $bmp.Dispose(); $font.Dispose(); $brush.Dispose(); $path.Dispose()
    Write-Host "wrote $file ($width x $height)"
}

Save-Logo 'StoreLogo.png' 50 50
Save-Logo 'LexiIcon.png' 64 64
Save-Logo 'LockScreenLogo.scale-200.png' 48 48
Save-Logo 'Square44x44Logo.scale-200.png' 88 88
Save-Logo 'Square44x44Logo.targetsize-24_altform-unplated.png' 24 24
Save-Logo 'Square150x150Logo.scale-200.png' 300 300 0.6
Save-Logo 'Wide310x150Logo.scale-200.png' 620 300 0.6
Save-Logo 'SplashScreen.scale-200.png' 1240 600 0.4
