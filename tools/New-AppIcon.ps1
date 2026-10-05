<#
.SYNOPSIS
    Draws the CloudDrive-Sync icon (blue cloud with white sync arrows) and writes it as a multi-size .ico.
.DESCRIPTION
    Same blue cloud as CloudDrives (the drives program), but with sync arrows instead of the drive, so both
    programs look related and are still easy to tell apart. Drawn at 256 px and scaled down for the small sizes.
#>
param(
    [string]$OutFile = (Join-Path $PSScriptRoot '..\src\CloudDriveSync.App\Assets\clouddrive-sync.ico')
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

function New-Master {
    $size = 256
    $bitmap = [System.Drawing.Bitmap]::new($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bitmap)
    try {
        $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit
        $g.Clear([System.Drawing.Color]::Transparent)

        # The cloud: a wide rounded base and three circles, filled as one shape.
        $cloud = [System.Drawing.Drawing2D.GraphicsPath]::new([System.Drawing.Drawing2D.FillMode]::Winding)
        $top = 18
        $radius = 44
        $base = [System.Drawing.RectangleF]::new(16, 112 + $top, 224, 88)
        $cloud.AddArc($base.X, $base.Y, 2 * $radius, 2 * $radius, 90, 180)
        $cloud.AddLine($base.X + $radius, $base.Y, $base.Right - $radius, $base.Y)
        $cloud.AddArc($base.Right - 2 * $radius, $base.Y, 2 * $radius, 2 * $radius, 270, 180)
        $cloud.CloseFigure()
        $cloud.AddEllipse(70, 34 + $top, 132, 132)
        $cloud.AddEllipse(26, 86 + $top, 100, 100)
        $cloud.AddEllipse(150, 92 + $top, 88, 88)
        $gradient = [System.Drawing.Drawing2D.LinearGradientBrush]::new(
            [System.Drawing.PointF]::new(0, 34 + $top), [System.Drawing.PointF]::new(0, 200 + $top),
            [System.Drawing.ColorTranslator]::FromHtml('#5FB0FF'), [System.Drawing.ColorTranslator]::FromHtml('#1F5FEA'))
        $g.FillPath($gradient, $cloud)
        $gradient.Dispose()
        $cloud.Dispose()

        # White sync arrows (Segoe Fluent Icons "Sync"), centred on the cloud.
        $font = [System.Drawing.Font]::new('Segoe Fluent Icons', 92, [System.Drawing.FontStyle]::Bold, [System.Drawing.GraphicsUnit]::Pixel)
        if ($font.Name -ne 'Segoe Fluent Icons') { $font.Dispose(); $font = [System.Drawing.Font]::new('Segoe MDL2 Assets', 92, [System.Drawing.FontStyle]::Bold, [System.Drawing.GraphicsUnit]::Pixel) }
        $format = [System.Drawing.StringFormat]::new()
        $format.Alignment = [System.Drawing.StringAlignment]::Center
        $format.LineAlignment = [System.Drawing.StringAlignment]::Center
        $white = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::White)
        $g.DrawString([string][char]0xE895, $font, $white, [System.Drawing.RectangleF]::new(0, 70 + $top, 256, 140), $format)
        $white.Dispose()
        $format.Dispose()
        $font.Dispose()
    }
    finally { $g.Dispose() }
    $bitmap
}

function ConvertTo-Png([System.Drawing.Bitmap]$Master, [int]$Size) {
    $frame = [System.Drawing.Bitmap]::new($Size, $Size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($frame)
    try {
        $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $g.DrawImage($Master, 0, 0, $Size, $Size)
    }
    finally { $g.Dispose() }
    $stream = [System.IO.MemoryStream]::new()
    $frame.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
    $frame.Dispose()
    , $stream.ToArray()
}

$master = New-Master
try {
    $sizes = 16, 24, 32, 48, 64, 128, 256
    $frames = foreach ($size in $sizes) { , (ConvertTo-Png $master $size) }
    $file = [System.IO.MemoryStream]::new()
    $writer = [System.IO.BinaryWriter]::new($file)
    $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$sizes.Count)
    $offset = 6 + 16 * $sizes.Count
    for ($i = 0; $i -lt $sizes.Count; $i++) {
        $dimension = if ($sizes[$i] -ge 256) { 0 } else { $sizes[$i] }
        $writer.Write([byte]$dimension); $writer.Write([byte]$dimension); $writer.Write([byte]0); $writer.Write([byte]0)
        $writer.Write([uint16]1); $writer.Write([uint16]32)
        $writer.Write([uint32]$frames[$i].Length); $writer.Write([uint32]$offset)
        $offset += $frames[$i].Length
    }
    foreach ($frame in $frames) { $writer.Write($frame) }
    $writer.Flush()
    [System.IO.File]::WriteAllBytes((Resolve-Path (Split-Path $OutFile)).Path + '\' + (Split-Path $OutFile -Leaf), $file.ToArray())
    $writer.Dispose()
    "Written: $OutFile ($($sizes -join ', ') px)"
}
finally { $master.Dispose() }
