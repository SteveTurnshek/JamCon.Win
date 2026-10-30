# Builds src/JamCon.App/Assets/JamCon.ico (the exe and Start menu icon) from the tray outline:
# a white Joy-Con on a dark rounded square, so it reads on both light and dark Start menus.
# Run from the repository root:  powershell -File tools/make-app-icon.ps1
param(
  [string]$Source = "src/JamCon.App/Assets/joyconR.png",
  [string]$Out = "src/JamCon.App/Assets/JamCon.ico"
)
Add-Type -AssemblyName System.Drawing
$Source = (Resolve-Path $Source).Path
$Out = [IO.Path]::GetFullPath((Join-Path (Get-Location) $Out))
$src = [Drawing.Bitmap]::FromFile($Source)

# White copy of the outline: keep the alpha, force the colour.
$white = New-Object Drawing.Bitmap $src.Width, $src.Height, ([Drawing.Imaging.PixelFormat]::Format32bppArgb)
for ($x = 0; $x -lt $src.Width; $x++) {
  for ($y = 0; $y -lt $src.Height; $y++) {
    $white.SetPixel($x, $y, [Drawing.Color]::FromArgb($src.GetPixel($x, $y).A, 255, 255, 255))
  }
}

$sizes = 16, 24, 32, 48, 64, 256
$pngs = @()
foreach ($s in $sizes) {
  $bmp = New-Object Drawing.Bitmap $s, $s, ([Drawing.Imaging.PixelFormat]::Format32bppArgb)
  $g = [Drawing.Graphics]::FromImage($bmp)
  $g.SmoothingMode = 'AntiAlias'; $g.InterpolationMode = 'HighQualityBicubic'; $g.PixelOffsetMode = 'HighQuality'
  $d = 2 * [Math]::Max(2, $s * 0.22); $w = $s - 1
  $path = New-Object Drawing.Drawing2D.GraphicsPath
  $path.AddArc(0, 0, $d, $d, 180, 90); $path.AddArc($w - $d, 0, $d, $d, 270, 90)
  $path.AddArc($w - $d, $w - $d, $d, $d, 0, 90); $path.AddArc(0, $w - $d, $d, $d, 90, 90); $path.CloseFigure()
  $g.FillPath((New-Object Drawing.SolidBrush ([Drawing.Color]::FromArgb(255, 0x2B, 0x30, 0x38))), $path)
  $h = $s * 0.78; $iw = $h * $src.Width / $src.Height
  $g.DrawImage($white, [single](($s - $iw) / 2 + $s * 0.04), [single](($s - $h) / 2), [single]$iw, [single]$h)
  $g.Dispose()
  $ms = New-Object IO.MemoryStream
  $bmp.Save($ms, [Drawing.Imaging.ImageFormat]::Png)
  $pngs += , $ms.ToArray()
  $bmp.Dispose()
}

# ICO container with PNG-compressed entries (supported since Windows Vista).
$fs = [IO.File]::Create($Out); $bw = New-Object IO.BinaryWriter $fs
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
  $b = if ($sizes[$i] -ge 256) { 0 } else { $sizes[$i] }
  $bw.Write([byte]$b); $bw.Write([byte]$b); $bw.Write([byte]0); $bw.Write([byte]0)
  $bw.Write([uint16]1); $bw.Write([uint16]32); $bw.Write([uint32]$pngs[$i].Length); $bw.Write([uint32]$offset)
  $offset += $pngs[$i].Length
}
foreach ($p in $pngs) { $bw.Write($p) }
$bw.Close()
"Wrote $Out"
