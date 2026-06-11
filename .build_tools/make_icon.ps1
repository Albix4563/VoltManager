Add-Type -AssemblyName System.Drawing

function Draw-Bolt([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap($size, $size)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    # Rounded navy background
    $bg = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 10, 17, 40))
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $r = [Math]::Max(2, [int]($size * 0.22))
    $d = $r * 2
    $path.AddArc(0, 0, $d, $d, 180, 90)
    $path.AddArc($size - $d, 0, $d, $d, 270, 90)
    $path.AddArc($size - $d, $size - $d, $d, $d, 0, 90)
    $path.AddArc(0, $size - $d, $d, $d, 90, 90)
    $path.CloseFigure()
    $g.FillPath($bg, $path)
    # Cyan lightning bolt (normalized coords on 64 grid)
    $cyan = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 0, 241, 254))
    $s = $size / 64.0
    $pts = @(
        (New-Object System.Drawing.PointF([float](38*$s), [float](6*$s))),
        (New-Object System.Drawing.PointF([float](17*$s), [float](37*$s))),
        (New-Object System.Drawing.PointF([float](30*$s), [float](37*$s))),
        (New-Object System.Drawing.PointF([float](26*$s), [float](58*$s))),
        (New-Object System.Drawing.PointF([float](47*$s), [float](27*$s))),
        (New-Object System.Drawing.PointF([float](34*$s), [float](27*$s)))
    )
    $g.FillPolygon($cyan, $pts)
    $g.Dispose()
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    return ,$ms.ToArray()
}

$sizes = @(16, 24, 32, 48, 64, 256)
$images = @{}
foreach ($s in $sizes) { $images[$s] = Draw-Bolt $s }

$out = New-Object System.IO.MemoryStream
$w = New-Object System.IO.BinaryWriter($out)
# ICONDIR
$w.Write([UInt16]0); $w.Write([UInt16]1); $w.Write([UInt16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
foreach ($s in $sizes) {
    $data = $images[$s]
    $dim = if ($s -ge 256) { 0 } else { $s }
    $w.Write([Byte]$dim)      # width
    $w.Write([Byte]$dim)      # height
    $w.Write([Byte]0)         # palette
    $w.Write([Byte]0)         # reserved
    $w.Write([UInt16]1)       # planes
    $w.Write([UInt16]32)      # bpp
    $w.Write([UInt32]$data.Length)
    $w.Write([UInt32]$offset)
    $offset += $data.Length
}
foreach ($s in $sizes) { $w.Write($images[$s]) }
$w.Flush()
[System.IO.File]::WriteAllBytes('c:\power_efficency\src\VoltManager\Assets\voltmanager.ico', $out.ToArray())
$w.Dispose()
Write-Output ("ico written: " + (Get-Item 'c:\power_efficency\src\VoltManager\Assets\voltmanager.ico').Length + " bytes")
