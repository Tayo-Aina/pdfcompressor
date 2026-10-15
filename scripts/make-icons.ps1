<#
    Generates the PdfCompressor application icons.

    Two variants are produced, matching the app's light/dark themes:
      Assets\app-light.ico  - light tile, blue document, white chevrons
      Assets\app-dark.ico   - dark tile, white document, blue chevrons

    Style: flat colours, no gradients, small corner radius.
    Each .ico contains 16/24/32/48/64 (BMP) and 128/256 (PNG) frames.
#>

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$assetsDir = Join-Path $PSScriptRoot '..\PdfCompressor\Assets'
$assetsDir = [System.IO.Path]::GetFullPath($assetsDir)

$sizes = @(16, 24, 32, 48, 64, 128, 256)

# ---- palette per variant -------------------------------------------------
$variants = @(
    @{ Name = 'app-dark';  Tile = '#22252C'; Doc = '#F2F5F9'; Fold = '#C9D2E0'; Chevron = '#4C8DF6' },
    @{ Name = 'app-light'; Tile = '#E8EDF5'; Doc = '#2F6FEB'; Fold = '#6E9BF1'; Chevron = '#FFFFFF' }
)

function New-RoundedRectPath([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $r * 2
    $p.AddArc($x, $y, $d, $d, 180, 90)
    $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    return $p
}

function New-DocPath([float]$x, [float]$y, [float]$w, [float]$h, [float]$r, [float]$fold) {
    # Page with the top-right corner clipped (fold mark).
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $r * 2
    $p.AddArc($x, $y, $d, $d, 180, 90)                                   # top-left
    $p.AddLine($x + $r, $y, $x + $w - $fold, $y)                         # top edge
    $p.AddLine($x + $w - $fold, $y, $x + $w, $y + $fold)                 # diagonal cut
    $p.AddLine($x + $w, $y + $fold, $x + $w, $y + $h - $r)               # right edge
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)                 # bottom-right
    $p.AddLine($x + $w - $r, $y + $h, $x + $r, $y + $h)                  # bottom edge
    $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)                          # bottom-left
    $p.CloseFigure()
    return $p
}

function New-IconBitmap([string]$tileHex, [string]$docHex, [string]$foldHex, [string]$chevronHex) {
    $S = 1024
    $bmp = New-Object System.Drawing.Bitmap($S, $S, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)

    function C([string]$hex) { [System.Drawing.ColorTranslator]::FromHtml($hex) }

    # Tile: flat rounded square, small radius.
    $margin = 0.02 * $S
    $tile = New-RoundedRectPath $margin $margin ($S - 2 * $margin) ($S - 2 * $margin) (0.09 * $S)
    $g.FillPath((New-Object System.Drawing.SolidBrush((C $tileHex))), $tile)

    # Document page with a clipped top-right corner.
    $dw = 0.40 * $S
    $dh = 0.56 * $S
    $dx = ($S - $dw) / 2
    $dy = 0.22 * $S
    $fold = 0.14 * $S
    $doc = New-DocPath $dx $dy $dw $dh (0.05 * $S) $fold
    $g.FillPath((New-Object System.Drawing.SolidBrush((C $docHex))), $doc)

    # Fold triangle in a slightly muted shade.
    $foldPath = New-Object System.Drawing.Drawing2D.GraphicsPath
    $foldPath.AddPolygon(@(
        (New-Object System.Drawing.PointF(($dx + $dw - $fold), $dy)),
        (New-Object System.Drawing.PointF(($dx + $dw), ($dy + $fold))),
        (New-Object System.Drawing.PointF(($dx + $dw - $fold), ($dy + $fold)))
    ))
    $foldPath.CloseFigure()
    $g.FillPath((New-Object System.Drawing.SolidBrush((C $foldHex))), $foldPath)

    # Compress chevrons: two flat arrows pointing at each other, clear gap between the tips.
    $cx = $S / 2
    $w = 0.10 * $S
    $h = 0.062 * $S
    $pen = New-Object System.Drawing.Pen((C $chevronHex), (0.045 * $S))
    $pen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $pen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $pen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round

    # top chevron: points down toward the centre
    $g.DrawLines($pen, @(
        (New-Object System.Drawing.PointF(($cx - $w), (0.34 * $S))),
        (New-Object System.Drawing.PointF($cx, (0.34 * $S + $h))),
        (New-Object System.Drawing.PointF(($cx + $w), (0.34 * $S)))
    ))
    # bottom chevron: points up toward the centre
    $g.DrawLines($pen, @(
        (New-Object System.Drawing.PointF(($cx - $w), (0.66 * $S))),
        (New-Object System.Drawing.PointF($cx, (0.66 * $S - $h))),
        (New-Object System.Drawing.PointF(($cx + $w), (0.66 * $S)))
    ))

    $pen.Dispose()
    $g.Dispose()
    return $bmp
}

function Get-IconFrame([System.Drawing.Bitmap]$src, [int]$size) {
    $dst = New-Object System.Drawing.Bitmap($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($dst)
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.DrawImage($src, (New-Object System.Drawing.Rectangle(0, 0, $size, $size)))
    $g.Dispose()
    return $dst
}

function Get-FrameBytes([System.Drawing.Bitmap]$frame, [int]$size) {
    # Small frames as classic BMP entries, large ones as PNG.
    if ($size -ge 128) {
        $ms = New-Object System.IO.MemoryStream
        $frame.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
        return , $ms.ToArray()
    }

    $w = $size
    $h = $size
    $rowBytes = $w * 4
    $maskRow = [Math]::Ceiling($w / 32.0) * 4
    $xorSize = $rowBytes * $h
    $andSize = $maskRow * $h

    $ms = New-Object System.IO.MemoryStream
    $bw = New-Object System.IO.BinaryWriter($ms)
    # BITMAPINFOHEADER
    $bw.Write([uint32]40)
    $bw.Write([int32]$w)
    $bw.Write([int32]($h * 2))      # XOR + AND
    $bw.Write([uint16]1)
    $bw.Write([uint16]32)
    $bw.Write([uint32]0)            # BI_RGB
    $bw.Write([uint32]($xorSize + $andSize))
    $bw.Write([int32]0); $bw.Write([int32]0); $bw.Write([uint32]0); $bw.Write([uint32]0)

    $rect = New-Object System.Drawing.Rectangle(0, 0, $w, $h)
    $data = $frame.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::ReadOnly, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    try {
        $stride = $data.Stride
        $buffer = New-Object byte[] ($stride * $h)
        [System.Runtime.InteropServices.Marshal]::Copy($data.Scan0, $buffer, 0, $buffer.Length)
        # BMP stores rows bottom-up.
        for ($y = $h - 1; $y -ge 0; $y--) {
            $bw.Write($buffer, $y * $stride, $rowBytes)
        }
    }
    finally {
        $frame.UnlockBits($data)
    }

    $bw.Write((New-Object byte[] $andSize))   # AND mask: all zero (alpha channel used)
    $bw.Flush()
    return , $ms.ToArray()
}

function Write-Ico([string]$path, [System.Drawing.Bitmap]$base) {
    $frames = @()
    foreach ($size in $sizes) {
        $frame = Get-IconFrame $base $size
        $bytes = Get-FrameBytes $frame $size
        $frames += [pscustomobject]@{ Size = $size; Bytes = $bytes }
        $frame.Dispose()
    }

    $ms = New-Object System.IO.MemoryStream
    $bw = New-Object System.IO.BinaryWriter($ms)
    $bw.Write([uint16]0)                 # reserved
    $bw.Write([uint16]1)                 # type: icon
    $bw.Write([uint16]$frames.Count)

    $offset = 6 + 16 * $frames.Count
    foreach ($f in $frames) {
        $dim = if ($f.Size -ge 256) { 0 } else { $f.Size }
        $bw.Write([byte]$dim)
        $bw.Write([byte]$dim)
        $bw.Write([byte]0)               # palette colours
        $bw.Write([byte]0)               # reserved
        $bw.Write([uint16]1)             # planes
        $bw.Write([uint16]32)            # bit depth
        $bw.Write([uint32]$f.Bytes.Length)
        $bw.Write([uint32]$offset)
        $offset += $f.Bytes.Length
    }
    foreach ($f in $frames) {
        $bw.Write($f.Bytes)
    }
    $bw.Flush()
    [System.IO.File]::WriteAllBytes($path, $ms.ToArray())
}

foreach ($v in $variants) {
    $base = New-IconBitmap $v.Tile $v.Doc $v.Fold $v.Chevron
    $iconPath = Join-Path $assetsDir ($v.Name + '.ico')
    Write-Ico $iconPath $base

    # Preview strip for eyeballing the result (not part of the app).
    $previewPath = Join-Path $env:TEMP ($v.Name + '-preview.png')
    $strip = New-Object System.Drawing.Bitmap(680, 320, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $sg = [System.Drawing.Graphics]::FromImage($strip)
    $sg.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $x = 16
    $y = 16
    $rowHeight = 0
    foreach ($size in @(16, 24, 32, 48, 64, 128, 256)) {
        if ($x + $size -gt 664) { $x = 16; $y += $rowHeight + 12; $rowHeight = 0 }
        $frame = Get-IconFrame $base $size
        $sg.DrawImage($frame, $x, $y, $size, $size)
        $frame.Dispose()
        $x += $size + 12
        $rowHeight = [Math]::Max($rowHeight, $size)
    }
    $sg.Dispose()
    $strip.Save($previewPath, [System.Drawing.Imaging.ImageFormat]::Png)
    $strip.Dispose()
    $base.Dispose()

    "wrote {0} ({1} bytes), preview: {2}" -f $iconPath, (Get-Item $iconPath).Length, $previewPath
}
