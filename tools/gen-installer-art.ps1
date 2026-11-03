# Draws the installer's wizard images from the app icon: installer/art/*.png.
# The large image (Welcome / Finished pages) comes in a light and a dark version, each at 100-200% scale;
# the small one (top right of the other pages) is the icon on transparency, which suits both.
# Run again after changing the icon:  pwsh tools/gen-installer-art.ps1

Add-Type -AssemblyName System.Drawing
$root = Split-Path $PSScriptRoot -Parent
$icon = [System.Drawing.Image]::FromFile((Join-Path $root 'VidFlow\Assets\AppIcon.png'))
$out = Join-Path $root 'installer\art'
New-Item -ItemType Directory -Force $out | Out-Null

function Color([string]$hex) { [System.Drawing.ColorTranslator]::FromHtml($hex) }

function New-Canvas([int]$w, [int]$h) {
    $bmp = New-Object System.Drawing.Bitmap $w, $h, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.InterpolationMode = 'HighQualityBicubic'
    $g.PixelOffsetMode = 'HighQuality'
    $g.TextRenderingHint = 'AntiAliasGridFit'
    return $bmp, $g
}

# Modern wizard image at 100% is 164x314; Inno picks the closest of the sizes listed.
$scales = @{ 100 = 1.0; 125 = 1.25; 150 = 1.5; 200 = 2.0 }
$themes = @{
    'light' = @{ Top = '#F4F1FF'; Bottom = '#E6F6FA'; Glow = '#FFFFFF'; Text = '#1F1F2E' }
    'dark'  = @{ Top = '#26223A'; Bottom = '#18222A'; Glow = '#3A3460'; Text = '#F2F2F7' }
}

foreach ($theme in $themes.Keys) {
    $t = $themes[$theme]
    foreach ($pct in $scales.Keys) {
        $s = $scales[$pct]
        $w = [int][math]::Round(164 * $s); $h = [int][math]::Round(314 * $s)
        $bmp, $g = New-Canvas $w $h

        # Soft vertical gradient, with a faint glow behind the icon.
        $rect = New-Object System.Drawing.Rectangle 0, 0, $w, $h
        $bg = New-Object System.Drawing.Drawing2D.LinearGradientBrush $rect, (Color $t.Top), (Color $t.Bottom), 90
        $g.FillRectangle($bg, $rect)
        $size = [int](96 * $s)
        $cx = $w / 2; $cy = $h * 0.40
        $glowRect = New-Object System.Drawing.RectangleF ($cx - $size), ($cy - $size), ($size * 2), ($size * 2)
        $path = New-Object System.Drawing.Drawing2D.GraphicsPath
        $path.AddEllipse($glowRect)
        $glow = New-Object System.Drawing.Drawing2D.PathGradientBrush $path
        $glow.CenterColor = [System.Drawing.Color]::FromArgb(150, (Color $t.Glow))
        $glow.SurroundColors = @([System.Drawing.Color]::FromArgb(0, (Color $t.Glow)))
        $g.FillEllipse($glow, $glowRect)

        $g.DrawImage($icon, [int]($cx - $size / 2), [int]($cy - $size / 2), $size, $size)

        $fmt = New-Object System.Drawing.StringFormat
        $fmt.Alignment = 'Center'
        $font = New-Object System.Drawing.Font 'Segoe UI Semibold', (19 * $s), ([System.Drawing.FontStyle]::Regular), ([System.Drawing.GraphicsUnit]::Pixel)
        $g.DrawString('VidFlow', $font, (New-Object System.Drawing.SolidBrush (Color $t.Text)), (New-Object System.Drawing.RectangleF 0, ($cy + $size / 2 + 14 * $s), $w, (30 * $s)), $fmt)

        $g.Dispose()
        $bmp.Save((Join-Path $out "wizard-$theme-$pct.png"), [System.Drawing.Imaging.ImageFormat]::Png)
        $bmp.Dispose()
    }
}

# Small image: 55x55 at 100%.
foreach ($pct in $scales.Keys) {
    $n = [int][math]::Round(55 * $scales[$pct])
    $bmp, $g = New-Canvas $n $n
    $pad = [int]($n * 0.06)
    $g.DrawImage($icon, $pad, $pad, $n - 2 * $pad, $n - 2 * $pad)
    $g.Dispose()
    $bmp.Save((Join-Path $out "small-$pct.png"), [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
}
$icon.Dispose()
Get-ChildItem $out | ForEach-Object { "$($_.Name)" }
