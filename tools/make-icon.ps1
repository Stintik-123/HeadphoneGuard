$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

# Пути строим от расположения скрипта — чтобы скрипт работал в любом пути.
$repo = Split-Path $PSScriptRoot -Parent
$root = Join-Path $repo 'src\HeadphoneGuard\Assets'
New-Item -ItemType Directory -Force -Path $root | Out-Null

function New-BmpImage([int]$size) {
    # Рисуем иконку: тёмный скруглённый квадрат, наушники, красная батарейка.
    $bmp = New-Object System.Drawing.Bitmap($size, $size)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.Clear([System.Drawing.Color]::Transparent)

    $bg = New-Object System.Drawing.Drawing2D.GraphicsPath
    $r = $size * 0.22
    $bg.AddArc(0, 0, $r, $r, 90, 180)
    $bg.AddArc($size - $r, 0, $r, $r, 270, 180)
    $bg.AddArc(0, $size - $r, $r, $r, 180, 180)
    $bg.AddArc($size - $r, $size - $r, $r, $r, 0, 180)
    $bg.CloseFigure()
    $g.FillPath((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 22, 26, 34))), $bg)

    $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(255, 236, 240, 248)), ($size * 0.11)
    $pen.StartCap = 'Round'; $pen.EndCap = 'Round'
    $g.DrawArc($pen, $size * 0.24, $size * 0.18, $size * 0.52, $size * 0.62, 200, 140)

    $cup = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 236, 240, 248))
    $g.FillEllipse($cup, $size * 0.17, $size * 0.50, $size * 0.20, $size * 0.30)
    $g.FillEllipse($cup, $size * 0.63, $size * 0.50, $size * 0.20, $size * 0.30)

    $warn = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 255, 92, 82))
    $bx = $size * 0.36; $by = $size * 0.30; $bw = $size * 0.28; $bh = $size * 0.42
    $g.FillRectangle($warn, $bx, $by, $bw, $bh)
    $g.FillRectangle($warn, $bx + $bw * 0.3, $by - $bh * 0.12, $bw * 0.4, $bh * 0.14)
    $g.FillRectangle((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 22, 26, 34))),
        $bx, $by, $bw, $bh * 0.28)

    $g.Dispose()

    # XOR-битмап: BGRA, снизу вверх
    $xor = New-Object byte[] ($size * $size * 4)
    $stride = $size * 4
    for ($y = 0; $y -lt $size; $y++) {
        for ($x = 0; $x -lt $size; $x++) {
            $pixel = $bmp.GetPixel($x, $size - 1 - $y)
            $offset = $y * $stride + $x * 4
            $xor[$offset + 0] = $pixel.B
            $xor[$offset + 1] = $pixel.G
            $xor[$offset + 2] = $pixel.R
            $xor[$offset + 3] = $pixel.A
        }
    }
    $bmp.Dispose()

    # AND-маска: 1 бит на пиксель, всё непрозрачно, строки кратны 4 байтам
    $andRowBytes = [int]([Math]::Ceiling($size / 32.0) * 4)
    $and = New-Object byte[] ($andRowBytes * $size)

    # BITMAPINFOHEADER (биHeight = 2 * height из-за XOR + AND)
    $header = New-Object byte[] 40
    [Array]::Copy([System.BitConverter]::GetBytes([int]$size), 0, $header, 0, 4)   # biWidth
    [Array]::Copy([System.BitConverter]::GetBytes([int]($size * 2)), 0, $header, 4, 4) # biHeight
    [Array]::Copy([System.BitConverter]::GetBytes([int]1), 0, $header, 8, 4)         # biPlanes
    [Array]::Copy([System.BitConverter]::GetBytes([int]32), 0, $header, 12, 4)       # biBitCount
    [Array]::Copy([System.BitConverter]::GetBytes([int]0), 0, $header, 16, 4)        # biCompression
    [Array]::Copy([System.BitConverter]::GetBytes([int]($xor.Length + $and.Length)), 0, $header, 20, 4)

    $dib = New-Object byte[] (40 + $xor.Length + $and.Length)
    [Array]::Copy($header, 0, $dib, 0, 40)
    [Array]::Copy($xor, 0, $dib, 40, $xor.Length)
    [Array]::Copy($and, 0, $dib, 40 + $xor.Length, $and.Length)

    return $dib
}

function Write-Ico([int[]]$sizes, [string]$path) {
    $images = @()
    foreach ($size in $sizes) {
        $images += ,(New-BmpImage $size)
    }

    $total = 6 + 16 * $sizes.Count
    foreach ($img in $images) { $total += $img.Length }

    $bytes = New-Object byte[] $total
    $bytes[0] = 0; $bytes[1] = 0; $bytes[2] = 1; $bytes[3] = 0
    $bytes[4] = [byte]$sizes.Count; $bytes[5] = 0

    $offset = 6 + 16 * $sizes.Count
    for ($i = 0; $i -lt $sizes.Count; $i++) {
        $size = $sizes[$i]
        $entry = 6 + $i * 16
        $bytes[$entry + 0] = [byte]($(if ($size -ge 256) { 0 } else { $size }))
        $bytes[$entry + 1] = [byte]($(if ($size -ge 256) { 0 } else { $size }))
        $bytes[$entry + 2] = 0; $bytes[$entry + 3] = 0
        [Array]::Copy([System.BitConverter]::GetBytes([int]1), 0, $bytes, $entry + 4, 2)   # planes
        [Array]::Copy([System.BitConverter]::GetBytes([int]32), 0, $bytes, $entry + 6, 2)  # bpp
        [Array]::Copy([System.BitConverter]::GetBytes([int]$images[$i].Length), 0, $bytes, $entry + 8, 4)
        [Array]::Copy([System.BitConverter]::GetBytes([int]$offset), 0, $bytes, $entry + 12, 4)
        [Array]::Copy($images[$i], 0, $bytes, $offset, $images[$i].Length)
        $offset += $images[$i].Length
    }

    [System.IO.File]::WriteAllBytes($path, $bytes)
    Write-Host ("wrote {0} ({1} bytes, sizes: {2})" -f (Split-Path $path -Leaf), $bytes.Length, ($sizes -join ','))
}

Write-Ico @(16, 32, 48, 64) (Join-Path $root 'app.ico')
Write-Ico @(16, 32, 48) (Join-Path $root 'app-48.ico')
Write-Ico @(16, 32) (Join-Path $root 'app-32.ico')
Write-Ico @(16) (Join-Path $root 'app-16.ico')
