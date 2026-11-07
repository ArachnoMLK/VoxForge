# Renders the VoxForge logo (same vector as LogoImage in MainWindow.xaml) into src/VoxForge/VoxForge.ico.
# Run with Windows PowerShell (WPF): powershell -ExecutionPolicy Bypass -File tools/make-icon.ps1
Add-Type -AssemblyName PresentationCore, PresentationFramework, WindowsBase
$ErrorActionPreference = 'Stop'

$xaml = @'
<DrawingGroup xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
  <GeometryDrawing Geometry="M8,0 H40 A8,8 0 0 1 48,8 V40 A8,8 0 0 1 40,48 H8 A8,8 0 0 1 0,40 V8 A8,8 0 0 1 8,0 Z">
    <GeometryDrawing.Brush>
      <LinearGradientBrush StartPoint="0,0" EndPoint="1,1">
        <GradientStop Color="#FFB347" Offset="0"/>
        <GradientStop Color="#FF5A1F" Offset="1"/>
      </LinearGradientBrush>
    </GeometryDrawing.Brush>
  </GeometryDrawing>
  <GeometryDrawing Geometry="M7,26 L13,26 L16,17 L20,35 L24,10 L28,38 L32,19 L35,26 L41,26">
    <GeometryDrawing.Pen>
      <Pen Brush="#1A1006" Thickness="3.4" StartLineCap="Round" EndLineCap="Round" LineJoin="Round"/>
    </GeometryDrawing.Pen>
  </GeometryDrawing>
  <GeometryDrawing Brush="#FFF4E0" Geometry="M38,6 L39.5,10 L43.5,11.5 L39.5,13 L38,17 L36.5,13 L32.5,11.5 L36.5,10 Z"/>
</DrawingGroup>
'@
$drawing = [System.Windows.Markup.XamlReader]::Parse($xaml)

$frames = foreach ($size in 16, 24, 32, 48, 64, 128, 256) {
    $visual = New-Object System.Windows.Media.DrawingVisual
    $dc = $visual.RenderOpen()
    $dc.PushTransform((New-Object System.Windows.Media.ScaleTransform ($size / 48), ($size / 48)))
    $dc.DrawDrawing($drawing)
    $dc.Pop()
    $dc.Close()
    $bitmap = New-Object System.Windows.Media.Imaging.RenderTargetBitmap $size, $size, 96, 96, ([System.Windows.Media.PixelFormats]::Pbgra32)
    $bitmap.Render($visual)
    if ($size -ge 256) {
        # Large frame: PNG keeps the file small.
        $encoder = New-Object System.Windows.Media.Imaging.PngBitmapEncoder
        $encoder.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
        $stream = New-Object System.IO.MemoryStream
        $encoder.Save($stream)
        [pscustomobject]@{ Size = $size; Bytes = $stream.ToArray() }
        continue
    }
    # Small frames: classic 32-bit DIB (straight alpha, bottom-up rows, then a 1-bit AND mask).
    $straight = New-Object System.Windows.Media.Imaging.FormatConvertedBitmap $bitmap, ([System.Windows.Media.PixelFormats]::Bgra32), $null, 0
    $pixels = New-Object byte[] ($size * $size * 4)
    $straight.CopyPixels($pixels, $size * 4, 0)
    $dib = New-Object System.IO.MemoryStream
    $d = New-Object System.IO.BinaryWriter $dib
    $d.Write([uint32]40); $d.Write([int32]$size); $d.Write([int32]($size * 2)); $d.Write([uint16]1); $d.Write([uint16]32)
    $d.Write([uint32]0); $d.Write([uint32]0); $d.Write([int32]0); $d.Write([int32]0); $d.Write([uint32]0); $d.Write([uint32]0)
    for ($y = $size - 1; $y -ge 0; $y--) { $d.Write($pixels, $y * $size * 4, $size * 4) }
    $maskStride = [int]([Math]::Ceiling($size / 32.0) * 4)
    $d.Write((New-Object byte[] ($maskStride * $size)))
    $d.Flush()
    [pscustomobject]@{ Size = $size; Bytes = $dib.ToArray() }
}

# ICO container: DIB entries for small sizes, PNG for 256 px.
$out = Join-Path (Split-Path $PSScriptRoot) 'src\VoxForge\VoxForge.ico'
$file = New-Object System.IO.MemoryStream
$w = New-Object System.IO.BinaryWriter $file
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$frames.Count)
$offset = 6 + 16 * $frames.Count
foreach ($f in $frames) {
    $dim = if ($f.Size -ge 256) { 0 } else { $f.Size }
    $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32)
    $w.Write([uint32]$f.Bytes.Length); $w.Write([uint32]$offset)
    $offset += $f.Bytes.Length
}
foreach ($f in $frames) { $w.Write($f.Bytes) }
$w.Flush()
[System.IO.File]::WriteAllBytes($out, $file.ToArray())
Write-Host "Icon written: $out"
