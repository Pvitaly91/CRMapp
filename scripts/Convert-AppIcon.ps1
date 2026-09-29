[CmdletBinding()]
param(
    [string]$Source,
    [string]$Destination
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if (-not $Source) { $Source = Join-Path $PSScriptRoot '..\src\CheckboxBatchPrinter\Resources\app-icon.png' }
if (-not $Destination) { $Destination = Join-Path $PSScriptRoot '..\src\CheckboxBatchPrinter\Resources\app-icon.ico' }
Add-Type -AssemblyName System.Drawing

$sourceImage = [Drawing.Bitmap]::new((Resolve-Path -LiteralPath $Source).Path)
$frames = [Collections.Generic.List[byte[]]]::new()
$sizes = @(16, 24, 32, 48, 64, 128, 256)
try {
    foreach ($size in $sizes) {
        $bitmap = [Drawing.Bitmap]::new($size, $size, [Drawing.Imaging.PixelFormat]::Format32bppArgb)
        $graphics = [Drawing.Graphics]::FromImage($bitmap)
        $buffer = [IO.MemoryStream]::new()
        try {
            $graphics.Clear([Drawing.Color]::Transparent)
            $graphics.CompositingMode = [Drawing.Drawing2D.CompositingMode]::SourceCopy
            $graphics.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
            $graphics.PixelOffsetMode = [Drawing.Drawing2D.PixelOffsetMode]::HighQuality
            $scale = [Math]::Min($size / $sourceImage.Width, $size / $sourceImage.Height)
            $width = [single]($sourceImage.Width * $scale)
            $height = [single]($sourceImage.Height * $scale)
            $rect = [Drawing.RectangleF]::new(($size - $width) / 2, ($size - $height) / 2, $width, $height)
            $graphics.DrawImage($sourceImage, $rect)
            $bitmap.Save($buffer, [Drawing.Imaging.ImageFormat]::Png)
            $frames.Add($buffer.ToArray())
        } finally {
            $buffer.Dispose()
            $graphics.Dispose()
            $bitmap.Dispose()
        }
    }
    $output = [IO.File]::Create([IO.Path]::GetFullPath($Destination))
    $writer = [IO.BinaryWriter]::new($output)
    try {
        $writer.Write([uint16]0)
        $writer.Write([uint16]1)
        $writer.Write([uint16]$sizes.Count)
        $offset = 6 + 16 * $sizes.Count
        for ($index = 0; $index -lt $sizes.Count; $index++) {
            $dimension = if ($sizes[$index] -eq 256) { 0 } else { $sizes[$index] }
            $writer.Write([byte]$dimension)
            $writer.Write([byte]$dimension)
            $writer.Write([byte]0)
            $writer.Write([byte]0)
            $writer.Write([uint16]1)
            $writer.Write([uint16]32)
            $writer.Write([uint32]$frames[$index].Length)
            $writer.Write([uint32]$offset)
            $offset += $frames[$index].Length
        }
        foreach ($frame in $frames) { $writer.Write($frame) }
    } finally { $writer.Dispose() }
} finally { $sourceImage.Dispose() }
Write-Host "Created Windows icon (16/24/32/48/64/128/256 px): $Destination"
