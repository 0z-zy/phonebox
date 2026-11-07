# Renders the PhoneBox icon (same design as the Android launcher icon) as a multi-size .ico.
# Usage: pwsh tools/make-icon.ps1  ->  agent/PhoneBox.Agent/phonebox.ico
Add-Type -AssemblyName System.Drawing
$outDir = Join-Path $PSScriptRoot '..\agent\PhoneBox.Agent'
$out = Join-Path (Resolve-Path -LiteralPath $outDir).Path 'phonebox.ico'

function New-RoundRect([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $r * 2
    $p.AddArc($x, $y, $d, $d, 180, 90); $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90); $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    return $p
}

function New-Brush([string]$hex) { New-Object System.Drawing.SolidBrush ([System.Drawing.ColorTranslator]::FromHtml($hex)) }

function New-IconPng([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.Clear([System.Drawing.Color]::Transparent)
    $s = $size / 256.0

    # background
    $g.FillPath((New-Brush '#0F1318'), (New-RoundRect 0 0 ($size - 1) ($size - 1) (56 * $s)))
    # screen frame
    $pen = New-Object System.Drawing.Pen ([System.Drawing.ColorTranslator]::FromHtml('#E8ECF1')), ([Math]::Max(1.5, 11 * $s))
    $g.DrawPath($pen, (New-RoundRect (44 * $s) (62 * $s) (168 * $s) (116 * $s) (16 * $s)))
    # bars
    $g.FillRectangle((New-Brush '#2DD4BF'), 70 * $s, 136 * $s, 20 * $s, 24 * $s)
    $g.FillRectangle((New-Brush '#76B900'), 100 * $s, 108 * $s, 20 * $s, 52 * $s)
    $g.FillRectangle((New-Brush '#A78BFA'), 130 * $s, 124 * $s, 20 * $s, 36 * $s)
    # clock dot
    $g.FillEllipse((New-Brush '#F59E0B'), 164 * $s, 92 * $s, 28 * $s, 28 * $s)
    # stand
    $g.FillRectangle((New-Brush '#E8ECF1'), 118 * $s, 178 * $s, 20 * $s, 18 * $s)
    $g.FillRectangle((New-Brush '#E8ECF1'), 88 * $s, 196 * $s, 80 * $s, 12 * $s)

    $g.Dispose()
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    return , $ms.ToArray()
}

$sizes = 16, 24, 32, 48, 64, 128, 256
$images = foreach ($sz in $sizes) { , (New-IconPng $sz) }

$fs = [System.IO.File]::Create($out)
$bw = New-Object System.IO.BinaryWriter $fs
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $sz = $sizes[$i]; $len = $images[$i].Length
    $dim = if ($sz -ge 256) { 0 } else { $sz }
    $bw.Write([byte]$dim); $bw.Write([byte]$dim)
    $bw.Write([byte]0); $bw.Write([byte]0); $bw.Write([uint16]1); $bw.Write([uint16]32)
    $bw.Write([uint32]$len); $bw.Write([uint32]$offset)
    $offset += $len
}
foreach ($img in $images) { $bw.Write([byte[]]$img) }
$bw.Close()
"icon written: $out ($((Get-Item $out).Length) bytes)"
