# Рисует иконку Cleaner (зелёный скруглённый квадрат + белые «искры») и собирает многоразмерный .ico.
# Запуск: powershell -NoProfile -File tools\make-icon.ps1   → src\Cleaner.App\Assets\cleaner.ico
Add-Type -AssemblyName System.Drawing
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$out = Join-Path $root 'src\Cleaner.App\Assets\cleaner.ico'
New-Item -ItemType Directory (Split-Path $out) -Force | Out-Null

function Star([System.Drawing.Graphics]$g, [float]$cx, [float]$cy, [float]$r, [System.Drawing.Brush]$brush) {
    # Четырёхлучевая «искра»: длинные лучи по осям, вогнутые стороны.
    $k = 0.22
    $pts = [System.Drawing.PointF[]]@(
        (New-Object System.Drawing.PointF $cx, ($cy - $r)),
        (New-Object System.Drawing.PointF ($cx + $r * $k), ($cy - $r * $k)),
        (New-Object System.Drawing.PointF ($cx + $r), $cy),
        (New-Object System.Drawing.PointF ($cx + $r * $k), ($cy + $r * $k)),
        (New-Object System.Drawing.PointF $cx, ($cy + $r)),
        (New-Object System.Drawing.PointF ($cx - $r * $k), ($cy + $r * $k)),
        (New-Object System.Drawing.PointF ($cx - $r), $cy),
        (New-Object System.Drawing.PointF ($cx - $r * $k), ($cy - $r * $k)))
    $g.FillPolygon($brush, $pts)
}

function Render([int]$s) {
    $bmp = New-Object System.Drawing.Bitmap $s, $s, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'; $g.PixelOffsetMode = 'HighQuality'
    $g.Clear([System.Drawing.Color]::Transparent)

    # Фон: скруглённый квадрат с диагональным градиентом.
    $m = [Math]::Max(1, $s * 0.03); $w = $s - 2 * $m; $rad = $w * 0.24
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddArc([float]$m, [float]$m, [float](2 * $rad), [float](2 * $rad), 180, 90)
    $path.AddArc([float]($m + $w - 2 * $rad), [float]$m, [float](2 * $rad), [float](2 * $rad), 270, 90)
    $path.AddArc([float]($m + $w - 2 * $rad), [float]($m + $w - 2 * $rad), [float](2 * $rad), [float](2 * $rad), 0, 90)
    $path.AddArc([float]$m, [float]($m + $w - 2 * $rad), [float](2 * $rad), [float](2 * $rad), 90, 90)
    $path.CloseFigure()
    $p0 = New-Object System.Drawing.PointF 0, 0
    $p1 = New-Object System.Drawing.PointF $s, $s
    $grad = New-Object System.Drawing.Drawing2D.LinearGradientBrush $p0, $p1,
        ([System.Drawing.Color]::FromArgb(255, 72, 199, 130)), ([System.Drawing.Color]::FromArgb(255, 22, 112, 72))
    $g.FillPath($grad, $path)

    # Белые искры: большая, средняя, маленькая.
    $white = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::White)
    Star $g ($s * 0.44) ($s * 0.56) ($s * 0.30) $white
    Star $g ($s * 0.73) ($s * 0.28) ($s * 0.14) $white
    if ($s -ge 32) { Star $g ($s * 0.76) ($s * 0.74) ($s * 0.08) $white }

    $g.Dispose()
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()
    return ,$ms.ToArray()
}

# ICO с PNG-кадрами (поддерживается с Windows Vista).
$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$images = @(); foreach ($sz in $sizes) { $images += ,(Render $sz) }
$fs = [System.IO.File]::Create($out); $bw = New-Object System.IO.BinaryWriter $fs
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $sz = $sizes[$i]; $len = $images[$i].Length
    $bw.Write([byte]($sz % 256)); $bw.Write([byte]($sz % 256)); $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([uint16]1); $bw.Write([uint16]32); $bw.Write([uint32]$len); $bw.Write([uint32]$offset)
    $offset += $len
}
foreach ($img in $images) { $bw.Write([byte[]]$img) }
$bw.Close()
# Превью для проверки глазами.
[System.IO.File]::WriteAllBytes((Join-Path $root 'tools\icon-preview-256.png'), [byte[]]$images[-1])
"OK $out ($((Get-Item $out).Length) bytes)"
