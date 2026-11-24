# Generates Resources/neoide.ico (16..256 px, PNG entries) and Resources/logo.png from the vector logo.
# Usage: powershell -ExecutionPolicy Bypass -File tools/make-icon.ps1
Add-Type -AssemblyName PresentationCore, PresentationFramework, WindowsBase

$root = Split-Path -Parent $PSScriptRoot
$outDir = Join-Path $root "Resources"
New-Item -ItemType Directory -Force $outDir | Out-Null

function New-LogoDrawing {
    $group = New-Object System.Windows.Media.DrawingGroup

    # Rounded square with the NeoIDE gradient
    $grad = New-Object System.Windows.Media.LinearGradientBrush
    $grad.StartPoint = New-Object System.Windows.Point(0, 0)
    $grad.EndPoint = New-Object System.Windows.Point(1, 1)
    $grad.GradientStops.Add((New-Object System.Windows.Media.GradientStop([System.Windows.Media.ColorConverter]::ConvertFromString("#7B5CFF"), 0)))
    $grad.GradientStops.Add((New-Object System.Windows.Media.GradientStop([System.Windows.Media.ColorConverter]::ConvertFromString("#3574F0"), 0.55)))
    $grad.GradientStops.Add((New-Object System.Windows.Media.GradientStop([System.Windows.Media.ColorConverter]::ConvertFromString("#21D0B2"), 1)))
    $bg = New-Object System.Windows.Media.RectangleGeometry((New-Object System.Windows.Rect(8, 8, 240, 240)), 58, 58)
    $group.Children.Add((New-Object System.Windows.Media.GeometryDrawing($grad, $null, $bg)))

    # Subtle top highlight
    $hl = New-Object System.Windows.Media.SolidColorBrush([System.Windows.Media.Color]::FromArgb(40, 255, 255, 255))
    $hlGeo = [System.Windows.Media.Geometry]::Parse("M66,8 L190,8 A58,58 0 0 1 248,66 L248,96 C170,70 90,70 8,110 L8,66 A58,58 0 0 1 66,8 Z")
    $group.Children.Add((New-Object System.Windows.Media.GeometryDrawing($hl, $null, $hlGeo)))

    # Stylised "N"
    $white = [System.Windows.Media.Brushes]::White
    $n = [System.Windows.Media.Geometry]::Parse("M62,196 L62,60 L98,60 L158,148 L158,60 L194,60 L194,196 L158,196 L98,108 L98,196 Z")
    $group.Children.Add((New-Object System.Windows.Media.GeometryDrawing($white, $null, $n)))

    # Caret: the "IDE" touch
    $caret = New-Object System.Windows.Media.SolidColorBrush([System.Windows.Media.ColorConverter]::ConvertFromString("#B8FFF0"))
    $c = New-Object System.Windows.Media.RectangleGeometry((New-Object System.Windows.Rect(204, 176, 26, 20)), 4, 4)
    $group.Children.Add((New-Object System.Windows.Media.GeometryDrawing($caret, $null, $c)))
    return $group
}

function Render-Png([int]$size) {
    $visual = New-Object System.Windows.Media.DrawingVisual
    $ctx = $visual.RenderOpen()
    $k = [double]$size / 256.0
    $ctx.PushTransform((New-Object System.Windows.Media.ScaleTransform -ArgumentList $k, $k))
    $ctx.DrawDrawing((New-LogoDrawing))
    $ctx.Pop()
    $ctx.Close()
    $bmp = New-Object System.Windows.Media.Imaging.RenderTargetBitmap($size, $size, 96, 96, [System.Windows.Media.PixelFormats]::Pbgra32)
    $bmp.Render($visual)
    $enc = New-Object System.Windows.Media.Imaging.PngBitmapEncoder
    $enc.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($bmp))
    $ms = New-Object System.IO.MemoryStream
    $enc.Save($ms)
    return ,$ms.ToArray()
}

$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$images = @{}
foreach ($s in $sizes) { $images[$s] = Render-Png $s }

# ICO container with PNG payloads
$ico = New-Object System.IO.MemoryStream
$w = New-Object System.IO.BinaryWriter($ico)
$w.Write([UInt16]0); $w.Write([UInt16]1); $w.Write([UInt16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
foreach ($s in $sizes) {
    $dim = if ($s -ge 256) { 0 } else { $s }
    $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([UInt16]1); $w.Write([UInt16]32)
    $w.Write([UInt32]$images[$s].Length); $w.Write([UInt32]$offset)
    $offset += $images[$s].Length
}
foreach ($s in $sizes) { $w.Write($images[$s]) }
$w.Flush()
[System.IO.File]::WriteAllBytes((Join-Path $outDir "neoide.ico"), $ico.ToArray())
[System.IO.File]::WriteAllBytes((Join-Path $outDir "logo.png"), $images[256])
Write-Output "Written $outDir\neoide.ico and logo.png"
