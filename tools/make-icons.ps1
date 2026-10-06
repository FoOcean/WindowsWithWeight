# Regenerates Assets\*.png and Assets\app.ico from the Fluent UI System Icons SVGs in Assets\fluent
# (MIT, https://github.com/microsoft/fluentui-system-icons). The SVG paths are rasterised with WPF,
# fitted to a square and (for the .ico) tinted. Nothing is hand-drawn.
# Usage: powershell -ExecutionPolicy Bypass -File tools\make-icons.ps1
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationCore, WindowsBase

$assets = Join-Path $PSScriptRoot '..\Assets' | Resolve-Path
$fluent = Join-Path $assets 'fluent'
# embedded resource name -> Fluent SVG
$glyphs = [ordered]@{
    'weight'  = 'ic_fluent_dumbbell_28_filled'      # tray + app icon
    'hand'    = 'ic_fluent_hand_point_48_filled'    # demo "hand" overlay
    'balloon' = 'ic_fluent_balloon_24_filled'
    'plane'   = 'ic_fluent_send_48_filled'
    'book'    = 'ic_fluent_book_48_filled'
    'gift'    = 'ic_fluent_gift_24_filled'
    'sofa'    = 'ic_fluent_couch_48_filled'
    'fridge'  = 'ic_fluent_refrigerator_20_filled'
}

function Get-Geometry([string]$svgName) {
    $path = Join-Path $fluent "$svgName.svg"
    if (-not (Test-Path $path)) { throw "Missing glyph $path" }
    [xml]$svg = Get-Content $path -Raw
    $group = New-Object System.Windows.Media.GeometryGroup
    $group.FillRule = 'Nonzero'
    foreach ($p in $svg.svg.path) {
        $rule = if ($p.'fill-rule' -eq 'evenodd') { 'F0 ' } else { 'F1 ' }
        $group.Children.Add([System.Windows.Media.Geometry]::Parse($rule + $p.d))
    }
    $group.Freeze()
    return $group
}

# Renders the geometry centred in a transparent size x size square, scaled to fit inside the margin.
function Get-SquareGlyph($geometry, [int]$size, [double]$margin, [System.Windows.Media.Color]$color) {
    $b = $geometry.Bounds
    $inner = $size * (1.0 - 2 * $margin)
    $scale = [Math]::Min($inner / $b.Width, $inner / $b.Height)
    $tg = New-Object System.Windows.Media.TransformGroup
    $tg.Children.Add((New-Object System.Windows.Media.TranslateTransform (-$b.X - $b.Width / 2), (-$b.Y - $b.Height / 2)))
    $tg.Children.Add((New-Object System.Windows.Media.ScaleTransform $scale, $scale))
    $tg.Children.Add((New-Object System.Windows.Media.TranslateTransform ($size / 2), ($size / 2)))
    $dv = New-Object System.Windows.Media.DrawingVisual
    $dc = $dv.RenderOpen()
    $dc.PushTransform($tg)
    $dc.DrawGeometry((New-Object System.Windows.Media.SolidColorBrush $color), $null, $geometry)
    $dc.Pop()
    $dc.Close()
    $rtb = New-Object System.Windows.Media.Imaging.RenderTargetBitmap $size, $size, 96, 96, ([System.Windows.Media.PixelFormats]::Pbgra32)
    $rtb.Render($dv)
    $enc = New-Object System.Windows.Media.Imaging.PngBitmapEncoder
    $enc.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($rtb))
    $ms = New-Object System.IO.MemoryStream
    $enc.Save($ms)
    return , ([byte[]]$ms.ToArray()) # the comma stops PowerShell unrolling the array into single bytes
}

$black = [System.Windows.Media.Color]::FromRgb(0, 0, 0)
foreach ($name in $glyphs.Keys) {
    $png = Get-SquareGlyph (Get-Geometry $glyphs[$name]) 256 0.04 $black
    $out = Join-Path $assets "$name.png"
    [System.IO.File]::WriteAllBytes($out, $png)
    Write-Host "wrote $out"
}

# Multi-size PNG-compressed .ico for the executable, tinted indigo so it reads on light and dark backgrounds.
# Every size is rendered from the vector path, so small sizes stay sharp.
$tint = [System.Windows.Media.Color]::FromRgb(99, 102, 241)
$weight = Get-Geometry $glyphs['weight']
$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$pngs = @()
foreach ($s in $sizes) {
    $margin = if ($s -le 24) { 0.0 } else { 0.03 }
    $pngs += , (Get-SquareGlyph $weight $s $margin $tint)
}
$icoPath = Join-Path $assets 'app.ico'
$fs = [System.IO.File]::Create($icoPath)
$bw = New-Object System.IO.BinaryWriter $fs
$bw.Write([UInt16]0); $bw.Write([UInt16]1); $bw.Write([UInt16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]; $len = $pngs[$i].Length
    $dim = if ($s -ge 256) { 0 } else { $s }
    $bw.Write([byte]$dim); $bw.Write([byte]$dim); $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([UInt16]1); $bw.Write([UInt16]32); $bw.Write([UInt32]$len); $bw.Write([UInt32]$offset)
    $offset += $len
}
foreach ($p in $pngs) { $bw.Write([byte[]]$p) }
$bw.Dispose(); $fs.Dispose()
Write-Host "wrote $icoPath"
