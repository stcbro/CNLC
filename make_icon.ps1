# make_icon.ps1 - Generate app.ico (blue circle + white WiFi symbol) via GDI+
# ASCII-only on purpose; run: powershell -NoProfile -ExecutionPolicy Bypass -File make_icon.ps1

Add-Type -AssemblyName System.Drawing

$size = 256
$bmp = New-Object System.Drawing.Bitmap($size, $size)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.SmoothingMode = 'AntiAlias'

# blue gradient circle background
$rect  = New-Object System.Drawing.Rectangle(12, 12, 232, 232)
$c1    = [System.Drawing.Color]::FromArgb(33, 150, 243)
$c2    = [System.Drawing.Color]::FromArgb(21, 101, 192)
$brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush($rect, $c1, $c2, 90)
$g.FillEllipse($brush, $rect)

# white wifi arcs (two) + dot
$pen1 = New-Object System.Drawing.Pen([System.Drawing.Color]::White, 20)
$pen1.StartCap = 'Round'
$pen1.EndCap   = 'Round'
$g.DrawArc($pen1, 53, 62, 150, 150, 200, 140)

$pen2 = New-Object System.Drawing.Pen([System.Drawing.Color]::White, 20)
$pen2.StartCap = 'Round'
$pen2.EndCap   = 'Round'
$g.DrawArc($pen2, 85, 94, 86, 86, 200, 140)

$white = [System.Drawing.Brushes]::White
$g.FillEllipse($white, 111, 122, 34, 34)

$g.Dispose()

# save PNG into memory, then wrap into ICO container (256x256 entry, PNG-compressed)
$ms  = New-Object System.IO.MemoryStream
$bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
$png = $ms.ToArray()

$ico = New-Object System.IO.MemoryStream
$w   = New-Object System.IO.BinaryWriter($ico)
$w.Write([uint16]0)        # reserved
$w.Write([uint16]1)        # type: icon
$w.Write([uint16]1)        # count: 1 image
$w.Write([byte]0)          # width 256 -> 0
$w.Write([byte]0)          # height 256 -> 0
$w.Write([byte]0)          # color count
$w.Write([byte]0)          # reserved
$w.Write([uint16]1)        # planes
$w.Write([uint16]32)       # bit count
$w.Write([uint32]$png.Length)
$w.Write([uint32]22)       # data offset (6 + 16)
$w.Write($png)
$w.Flush()

$outPath = Join-Path $PSScriptRoot 'app.ico'
[System.IO.File]::WriteAllBytes($outPath, $ico.ToArray())
$bmp.Dispose()
Write-Output ("app.ico generated: " + $outPath + " (" + [Math]::Round((Get-Item $outPath).Length / 1KB, 1) + " KB)")
