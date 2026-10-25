# Renders the app icon from branding/AppIcon.xaml (32 px and up) and branding/AppIcon-small.xaml (16-24 px) into
# VidFlow/Assets/AppIcon.png (256 px) and VidFlow/Assets/AppIcon.ico (16-256 px). Optionally writes a preview sheet.
#   pwsh tools/gen-icon.ps1 [-Preview <png path>]
param([string]$Preview)
$ErrorActionPreference = 'Stop'

# WPF rendering needs a single-threaded apartment.
if ([Threading.Thread]::CurrentThread.ApartmentState -ne 'STA') {
    $argList = @('-STA', '-NoProfile', '-File', $PSCommandPath)
    if ($Preview) { $argList += @('-Preview', $Preview) }
    & pwsh @argList
    exit $LASTEXITCODE
}

Add-Type -AssemblyName PresentationCore, PresentationFramework, WindowsBase
$root = Resolve-Path (Join-Path $PSScriptRoot '..')
$large = Join-Path $root 'branding\AppIcon.xaml'
$small = Join-Path $root 'branding\AppIcon-small.xaml'
$assets = Join-Path $root 'VidFlow\Assets'

function Render([int]$size, [string]$source = $(if ($size -le 24) { $small } else { $large })) {
    # Relative image paths in the XAML (e.g. a wordmark PNG) resolve next to the XAML file.
    $context = [Windows.Markup.ParserContext]@{ BaseUri = [Uri]::new((Split-Path $source) + '\') }
    $canvas = [Windows.Markup.XamlReader]::Parse([IO.File]::ReadAllText($source), $context)
    $box = [Windows.Controls.Viewbox]@{ Width = $size; Height = $size; Child = $canvas }
    $box.Measure([Windows.Size]::new($size, $size))
    $box.Arrange([Windows.Rect]::new(0, 0, $size, $size))
    $box.UpdateLayout()
    $bmp = [Windows.Media.Imaging.RenderTargetBitmap]::new($size, $size, 96, 96, [Windows.Media.PixelFormats]::Pbgra32)
    $bmp.Render($box)
    $bmp
}

function PngBytes($bitmap) {
    $enc = [Windows.Media.Imaging.PngBitmapEncoder]::new()
    $enc.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
    $ms = [IO.MemoryStream]::new()
    $enc.Save($ms)
    $ms.ToArray()
}

# Classic 32-bit DIB entry (BGRA bottom-up + AND mask): the most widely supported form for small icon sizes.
function DibBytes($bitmap) {
    $size = $bitmap.PixelWidth
    $converted = [Windows.Media.Imaging.FormatConvertedBitmap]::new($bitmap, [Windows.Media.PixelFormats]::Bgra32, $null, 0)
    $stride = $size * 4
    $pixels = [byte[]]::new($stride * $size)
    $converted.CopyPixels($pixels, $stride, 0)
    $ms = [IO.MemoryStream]::new()
    $w = [IO.BinaryWriter]::new($ms)
    $w.Write([int]40); $w.Write([int]$size); $w.Write([int]($size * 2)); $w.Write([int16]1); $w.Write([int16]32)
    $w.Write([int]0); $w.Write([int]0); $w.Write([int]0); $w.Write([int]0); $w.Write([int]0); $w.Write([int]0)
    for ($y = $size - 1; $y -ge 0; $y--) { $w.Write($pixels, $y * $stride, $stride) }
    $maskStride = [int]([math]::Ceiling($size / 32.0) * 4)
    $w.Write([byte[]]::new($maskStride * $size))
    $w.Flush()
    $ms.ToArray()
}

$sizes = 16, 20, 24, 32, 40, 48, 64, 96, 128, 256
$images = @{}
foreach ($s in $sizes) { $images[$s] = Render $s }

[IO.File]::WriteAllBytes((Join-Path $assets 'AppIcon.png'), (PngBytes $images[256]))

$entries = foreach ($s in $sizes) { , ($(if ($s -ge 256) { PngBytes $images[$s] } else { DibBytes $images[$s] })) }
$ms = [IO.MemoryStream]::new()
$w = [IO.BinaryWriter]::new($ms)
$w.Write([int16]0); $w.Write([int16]1); $w.Write([int16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]
    $dim = if ($s -ge 256) { 0 } else { $s }
    $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([int16]1); $w.Write([int16]32); $w.Write([int]$entries[$i].Length); $w.Write([int]$offset)
    $offset += $entries[$i].Length
}
foreach ($e in $entries) { $w.Write([byte[]]$e) }
$w.Flush()
[IO.File]::WriteAllBytes((Join-Path $assets 'AppIcon.ico'), $ms.ToArray())
"Wrote AppIcon.png and AppIcon.ico ($($sizes -join ', ') px)"

# Platform logos drawn as vectors (branding/platforms/*.xaml) -> Assets/Platforms/*.png. The others in that folder
# are the platforms' own published icons.
$platformDir = Join-Path $assets 'Platforms'
foreach ($xaml in Get-ChildItem (Join-Path $root 'branding\platforms') -Filter '*.xaml') {
    $name = $xaml.BaseName -replace '-tile$', ''
    [IO.File]::WriteAllBytes((Join-Path $platformDir "$name.png"), (PngBytes (Render 128 $xaml.FullName)))
    "Wrote Platforms\$name.png"
}

if ($Preview) {
    # Every size on a light and a dark strip.
    $shown = 256, 64, 48, 40, 32, 24, 20, 16
    $panelW = 20 + ($shown | Measure-Object -Sum).Sum + 20 * $shown.Count
    $h = 300
    $dv = [Windows.Media.DrawingVisual]::new()
    $dc = $dv.RenderOpen()
    $x = 0
    foreach ($bg in '#F3F3F3', '#202020') {
        $brush = [Windows.Media.SolidColorBrush]::new([Windows.Media.ColorConverter]::ConvertFromString($bg))
        $dc.DrawRectangle($brush, $null, [Windows.Rect]::new($x, 0, $panelW, $h))
        $cx = $x + 20
        foreach ($s in $shown) {
            $dc.DrawImage($images[$s], [Windows.Rect]::new($cx, ($h - $s) / 2, $s, $s))
            $cx += $s + 20
        }
        $x += $panelW
    }
    $dc.Close()
    $sheet = [Windows.Media.Imaging.RenderTargetBitmap]::new($panelW * 2, $h, 96, 96, [Windows.Media.PixelFormats]::Pbgra32)
    $sheet.Render($dv)
    [IO.File]::WriteAllBytes($Preview, (PngBytes $sheet))
    "Preview: $Preview"
}
