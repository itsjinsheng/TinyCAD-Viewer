$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$assetDir = Join-Path $PSScriptRoot '../src/CadView/Assets'
$sizes = @(16,20,24,32,40,48,64,128,256)
$frames = @()
foreach ($size in $sizes) {
    $bitmap = [System.Drawing.Bitmap]::new(1024,1024)
    $g = [System.Drawing.Graphics]::FromImage($bitmap)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.ScaleTransform(4,4)
    $path = [System.Drawing.Drawing2D.GraphicsPath]::new()
    $path.AddArc(8,8,96,96,180,90); $path.AddArc(152,8,96,96,270,90)
    $path.AddArc(152,152,96,96,0,90); $path.AddArc(8,152,96,96,90,90); $path.CloseFigure()
    $brush = [System.Drawing.Drawing2D.LinearGradientBrush]::new([System.Drawing.Point]::new(0,8),[System.Drawing.Point]::new(0,248),[System.Drawing.ColorTranslator]::FromHtml('#263E58'),[System.Drawing.ColorTranslator]::FromHtml('#142233'))
    $g.FillPath($brush,$path)
    $white = [System.Drawing.Pen]::new([System.Drawing.ColorTranslator]::FromHtml('#F3F8FF'),20)
    $white.StartCap = $white.EndCap = [System.Drawing.Drawing2D.LineCap]::Square
    $g.DrawLine($white,64,76,180,76); $g.DrawLine($white,112,76,112,168)
    $cyan = [System.Drawing.Pen]::new([System.Drawing.ColorTranslator]::FromHtml('#55CEFF'),10)
    $cyan.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
    $points = [System.Drawing.PointF[]]@([System.Drawing.PointF]::new(151,117),[System.Drawing.PointF]::new(202,168),[System.Drawing.PointF]::new(151,168))
    $g.DrawPolygon($cyan,$points)
    $cyan.Width = 7; $cyan.StartCap = $cyan.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $g.DrawLine($cyan,59,201,202,201)
    $g.DrawLine($cyan,65,191,55,211); $g.DrawLine($cyan,207,191,197,211)
    $small = [System.Drawing.Bitmap]::new($size,$size)
    $sg = [System.Drawing.Graphics]::FromImage($small)
    $sg.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $sg.DrawImage($bitmap,[System.Drawing.Rectangle]::new(0,0,$size,$size))
    $stream = [System.IO.MemoryStream]::new()
    $small.Save($stream,[System.Drawing.Imaging.ImageFormat]::Png)
    $frames += ,$stream.ToArray()
    if ($size -eq 256) { $small.Save((Join-Path $assetDir 'TinyCAD.png'),[System.Drawing.Imaging.ImageFormat]::Png) }
    $stream.Dispose(); $sg.Dispose(); $small.Dispose(); $cyan.Dispose(); $white.Dispose(); $brush.Dispose(); $path.Dispose(); $g.Dispose(); $bitmap.Dispose()
}
$output = [System.IO.File]::Create((Join-Path $assetDir 'TinyCAD.ico'))
$writer = [System.IO.BinaryWriter]::new($output)
$writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i=0; $i -lt $sizes.Count; $i++) {
    $dimension = if ($sizes[$i] -eq 256) { 0 } else { $sizes[$i] }
    $writer.Write([byte]$dimension); $writer.Write([byte]$dimension)
    $writer.Write([byte]0); $writer.Write([byte]0); $writer.Write([uint16]1); $writer.Write([uint16]32)
    $writer.Write([uint32]$frames[$i].Length); $writer.Write([uint32]$offset)
    $offset += $frames[$i].Length
}
foreach ($frame in $frames) { $writer.Write([byte[]]$frame) }
$writer.Dispose()
