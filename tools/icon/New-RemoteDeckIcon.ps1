<#
.SYNOPSIS
    Generates RemoteDeck's application icon: Resources\RemoteDeck.ico and Resources\RemoteDeck-32.png.

.DESCRIPTION
    This script is the icon's source. There is no .svg and no binary editor in the loop: the
    geometry below IS the artwork, and the two files it writes are build inputs committed
    beside the code. Run it after changing anything here, and commit what it produces --
    the release workflow publishes what is versioned, it does not generate art.

    System.Drawing only, so a fresh checkout needs nothing installed to reproduce the icon.
#>
[CmdletBinding()]
param(
    # Defaulted in the body rather than here: PowerShell evaluates a parameter's default in the
    # CALLER's scope, where $PSScriptRoot is empty, so writing the path here resolves to nothing.
    [string]$OutputDirectory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

if (-not $OutputDirectory) {
    $OutputDirectory = Join-Path $PSScriptRoot '..\..\src\RemoteDeck.App\Resources'
}

# ============================================================================================
# The motif: three screen cards stacked into depth -- the "deck" of connections the pane holds.
#
# Expressed once in a 256-unit design box and RE-DRAWN at every icon size, never downscaled
# from one large frame. That distinction is the whole reason this script exists: shrinking a
# 256 px render to 16 px turns the steps between the cards into a grey smear, whereas drawing
# the same geometry directly at 16 px lands them on whole pixels.
#
# Both offsets are whole multiples of 16 design units, which is what makes every step land on a
# whole pixel at the smallest size: 32/16 = 2 px across, 48/16 = 3 px down. Anything that does not
# divide cleanly there smears the three cards into one blurred rectangle in the taskbar.
#
# The cascade is steeper than it is wide on purpose. A deck of 16:10 screens offset evenly is a
# wide, short mark: measured, it filled only 10 of the 16 pixels of the icon square vertically and
# read as a small flat lozenge in the title bar. Taller cards and a 48-unit vertical step bring
# that to 13 of 16 and separate the layers, at no cost to the metaphor.
#
# Bounding box: 160 + 2*32 = 224 wide, 112 + 2*48 = 208 tall.
# ============================================================================================
$DesignBox  = 256.0
$CardWidth  = 160.0
$CardHeight = 112.0   # 10:7 -- still a screen
$OffsetX    = 32.0
$OffsetY    = 48.0
$Radius     = 16.0
$OriginX    = 16.0    # (256 - 224) / 2, centred
# NOT the centred 24. At 16 px that puts every card top on an exact .5 boundary (24 * 16/256 =
# 1.5), where [math]::Round rounds half to EVEN and turns the two equal steps into 2 px and 4 px.
# 26 gives 1.625 / 4.625 / 7.625 -> 2, 5, 8: two clean 3 px steps.
$OriginY    = 26.0

# Depth is carried by colour, never by a drop shadow -- a shadow is the first thing to vanish
# below 32 px. The front card takes a gradient, the two behind it flat and progressively darker
# blues.
#
# These are fixed literals, unlike every colour in Resources\Theme.xaml, and that is a real
# limitation rather than an oversight: an .ico is static, so the icon cannot follow the Windows
# accent the rest of the interface tracks. A neutral grey would follow it no better and would be
# invisible on a taskbar, so the icon carries a colour of its own.
$BackFill   = [System.Drawing.Color]::FromArgb(255, 0x16, 0x44, 0x7F)
$MiddleFill = [System.Drawing.Color]::FromArgb(255, 0x1E, 0x5F, 0xB8)
$FrontLight = [System.Drawing.Color]::FromArgb(255, 0x3C, 0x97, 0xFF)
$FrontDark  = [System.Drawing.Color]::FromArgb(255, 0x16, 0x68, 0xD8)

# What Windows asks for across the shell: 16/20/24 in list views and captions, 32 in the taskbar,
# 40/48/64 on the desktop, 128/256 in the large-icon views and the file properties page.
$IconSizes = @(16, 20, 24, 32, 40, 48, 64, 128, 256)

# The title-bar bitmap is 32 px because ui:TitleBar renders it at 16 logical pixels: exactly 2:1
# at 100 % scaling and pixel-for-pixel at 200 %, which are the two cases that actually occur.
$TitleBarBitmapSize = 32


function New-RoundedRectanglePath {
    param([single]$X, [single]$Y, [single]$W, [single]$H, [single]$R)

    $d = $R * 2
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddArc($X, $Y, $d, $d, 180, 90)
    $path.AddArc($X + $W - $d, $Y, $d, $d, 270, 90)
    $path.AddArc($X + $W - $d, $Y + $H - $d, $d, $d, 0, 90)
    $path.AddArc($X, $Y + $H - $d, $d, $d, 90, 90)
    $path.CloseFigure()
    return $path
}


function New-DeckBitmap {
    param([int]$Size)

    $k = $Size / $DesignBox
    # At and below 64 px a half-pixel edge is a visible smudge rather than a soft edge, so the
    # geometry is snapped to whole pixels there. Above it, antialiasing does better than rounding.
    $snap = $Size -le 64

    $w = $CardWidth * $k
    $h = $CardHeight * $k
    $r = $Radius * $k
    if ($snap) {
        $w = [math]::Round($w)
        $h = [math]::Round($h)
        $r = [math]::Max(1, [math]::Round($r))
    }

    $bmp = New-Object System.Drawing.Bitmap($Size, $Size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    try {
        $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $g.Clear([System.Drawing.Color]::Transparent)

        # Back to front, so each card overlaps the one behind it.
        for ($i = 0; $i -lt 3; $i++) {
            $x = ($OriginX + (2 - $i) * $OffsetX) * $k
            $y = ($OriginY + $i * $OffsetY) * $k
            if ($snap) {
                $x = [math]::Round($x)
                $y = [math]::Round($y)
            }

            $path = New-RoundedRectanglePath -X $x -Y $y -W $w -H $h -R $r
            try {
                if ($i -eq 2) {
                    $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
                        (New-Object System.Drawing.PointF($x, $y)),
                        (New-Object System.Drawing.PointF(($x + $w), ($y + $h))),
                        $FrontLight, $FrontDark)
                    # Without a flipped wrap, GDI+ samples one pixel outside the gradient rectangle
                    # along the far edge and leaves a hairline of the wrong colour there. The enum
                    # has no plain "TileFlip": mirroring on both axes is TileFlipXY.
                    $brush.WrapMode = [System.Drawing.Drawing2D.WrapMode]::TileFlipXY
                }
                else {
                    $fill = if ($i -eq 0) { $BackFill } else { $MiddleFill }
                    $brush = New-Object System.Drawing.SolidBrush($fill)
                }

                try { $g.FillPath($brush, $path) } finally { $brush.Dispose() }
            }
            finally { $path.Dispose() }
        }
    }
    finally { $g.Dispose() }

    # The centre of the front card must be opaque. This costs one pixel read and catches the one
    # failure mode this script actually has: a geometry mistake draws outside the canvas, every
    # frame comes out transparent, and the .ico is still structurally valid -- so neither the
    # build, nor Windows, nor the file properties page says a word about it.
    $probeX = [int](($OriginX + $CardWidth / 2) * $k)
    $probeY = [int](($OriginY + 2 * $OffsetY + $CardHeight / 2) * $k)
    if ($bmp.GetPixel($probeX, $probeY).A -eq 0) {
        throw "Nothing was drawn at $Size px: the front card is transparent at ($probeX, $probeY)."
    }

    return $bmp
}


function ConvertTo-IconDib {
    # The classic icon image: a BITMAPINFOHEADER, the pixels bottom-up, then the AND mask.
    param([System.Drawing.Bitmap]$Bitmap)

    $w = $Bitmap.Width
    $h = $Bitmap.Height

    $stream = New-Object System.IO.MemoryStream
    $writer = New-Object System.IO.BinaryWriter($stream)
    try {
        # biHeight is DOUBLE the real height: an icon DIB stacks the colour image and the 1 bpp
        # AND mask into one bitmap, and the header has to describe both.
        $writer.Write([uint32]40)
        $writer.Write([int32]$w)
        $writer.Write([int32]($h * 2))
        $writer.Write([uint16]1)              # planes
        $writer.Write([uint16]32)             # bits per pixel
        $writer.Write([uint32]0)              # BI_RGB, uncompressed
        $writer.Write([uint32]($w * $h * 4))
        $writer.Write([int32]0)               # pixels per metre, X
        $writer.Write([int32]0)               # pixels per metre, Y
        $writer.Write([uint32]0)              # palette entries used
        $writer.Write([uint32]0)              # palette entries required

        $rect = New-Object System.Drawing.Rectangle(0, 0, $w, $h)
        $data = $Bitmap.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::ReadOnly,
                                 [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
        try {
            $row = New-Object byte[] ($w * 4)
            for ($y = $h - 1; $y -ge 0; $y--) {
                [System.Runtime.InteropServices.Marshal]::Copy(
                    [System.IntPtr]::Add($data.Scan0, $y * $data.Stride), $row, 0, $row.Length)
                $writer.Write($row)
            }
        }
        finally { $Bitmap.UnlockBits($data) }

        # The AND mask, all zeros. Transparency comes from the alpha channel written above; the
        # mask survives only because the format predates alpha, and Windows still reads the bytes.
        $maskStride = [int]([math]::Floor(($w + 31) / 32) * 4)
        $writer.Write((New-Object byte[] ($maskStride * $h)))

        $writer.Flush()
        # The leading comma is load-bearing: PowerShell unrolls an array returned from a function
        # into the pipeline, so a bare `return $bytes` arrives at the caller as an Object[] of
        # boxed bytes. BinaryWriter.Write then binds to the scalar overload and writes ONE byte
        # per frame -- a 159-byte .ico whose directory looks perfectly valid.
        return ,$stream.ToArray()
    }
    finally { $writer.Dispose() }
}


function ConvertTo-PngBytes {
    param([System.Drawing.Bitmap]$Bitmap)

    $stream = New-Object System.IO.MemoryStream
    try {
        $Bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
        return ,$stream.ToArray()   # see ConvertTo-IconDib on the comma
    }
    finally { $stream.Dispose() }
}


# --- Every frame ----------------------------------------------------------------------------
$frames = @(
    foreach ($size in $IconSizes) {
        $bmp = New-DeckBitmap -Size $size
        try {
            # PNG for 256 and the classic DIB below it: that split is what every icon toolchain
            # emits and what every consumer has always read. A 256 px DIB would also add a flat
            # 256 KB to the file for nothing.
            $bytes = if ($size -ge 256) { ConvertTo-PngBytes $bmp } else { ConvertTo-IconDib $bmp }
            [pscustomobject]@{ Size = $size; Bytes = $bytes }
        }
        finally { $bmp.Dispose() }
    }
)

$resolved = (Resolve-Path $OutputDirectory).Path
$icoPath = Join-Path $resolved 'RemoteDeck.ico'
$pngPath = Join-Path $resolved "RemoteDeck-$TitleBarBitmapSize.png"

# --- ICONDIR, then ICONDIRENTRY per frame, then the frames themselves -----------------------
$file = [System.IO.File]::Create($icoPath)
$writer = New-Object System.IO.BinaryWriter($file)
try {
    $writer.Write([uint16]0)                  # reserved
    $writer.Write([uint16]1)                  # type: icon
    $writer.Write([uint16]$frames.Count)

    # Named $imageOffset, never $offset: PowerShell variable names are case-insensitive, so an
    # accumulator called $offset at script scope silently overwrites a design constant named
    # $Offset. That is not hypothetical -- it happened here, and every frame drawn afterwards
    # landed outside the canvas and came out empty with no error from anything.
    $imageOffset = 6 + 16 * $frames.Count
    foreach ($frame in $frames) {
        # 256 is written as 0: the field is one byte, and 256 does not fit in it.
        $dimension = if ($frame.Size -ge 256) { 0 } else { $frame.Size }
        $writer.Write([byte]$dimension)
        $writer.Write([byte]$dimension)
        $writer.Write([byte]0)                # palette size: none, this is true colour
        $writer.Write([byte]0)                # reserved
        $writer.Write([uint16]1)              # planes
        $writer.Write([uint16]32)             # bits per pixel
        $writer.Write([uint32]$frame.Bytes.Length)
        $writer.Write([uint32]$imageOffset)
        $imageOffset += $frame.Bytes.Length
    }

    foreach ($frame in $frames) { $writer.Write($frame.Bytes) }
}
finally { $writer.Dispose() }

# --- The title-bar bitmap -------------------------------------------------------------------
$titleBitmap = New-DeckBitmap -Size $TitleBarBitmapSize
try { $titleBitmap.Save($pngPath, [System.Drawing.Imaging.ImageFormat]::Png) }
finally { $titleBitmap.Dispose() }

Write-Host "$icoPath ($($frames.Count) frames: $($IconSizes -join ', ') -- $([math]::Round((Get-Item $icoPath).Length / 1KB)) KB)"
Write-Host "$pngPath ($TitleBarBitmapSize x $TitleBarBitmapSize)"
