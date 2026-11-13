param(
    [Parameter(Mandatory = $true)][string]$SourcePath,
    [Parameter(Mandatory = $true)][string]$OutputPath
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$sourceFile = (Resolve-Path -LiteralPath $SourcePath).Path
$outputFile = [IO.Path]::GetFullPath($OutputPath)
if (Test-Path -LiteralPath $outputFile) { throw 'Output already exists. Use a new versioned filename.' }
$source = [Drawing.Bitmap]::new($sourceFile)
$frames = [Collections.Generic.List[object]]::new()
try {
    if ($source.Width -ne $source.Height) { throw 'The icon source must be square.' }
    foreach ($size in @(16, 20, 24, 32, 40, 48, 64, 128, 256)) {
        $bitmap = [Drawing.Bitmap]::new($size, $size, [Drawing.Imaging.PixelFormat]::Format32bppArgb)
        $graphics = [Drawing.Graphics]::FromImage($bitmap)
        $memory = [IO.MemoryStream]::new()
        try {
            $graphics.CompositingMode = [Drawing.Drawing2D.CompositingMode]::SourceCopy
            $graphics.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
            $graphics.PixelOffsetMode = [Drawing.Drawing2D.PixelOffsetMode]::HighQuality
            $graphics.Clear([Drawing.Color]::Transparent)
            $graphics.DrawImage($source, [Drawing.Rectangle]::new(0, 0, $size, $size), 0, 0, $source.Width, $source.Height, [Drawing.GraphicsUnit]::Pixel)
            $bitmap.Save($memory, [Drawing.Imaging.ImageFormat]::Png)
            $frames.Add([pscustomobject]@{Size=$size;Bytes=$memory.ToArray()})
        } finally { $memory.Dispose(); $graphics.Dispose(); $bitmap.Dispose() }
    }
} finally { $source.Dispose() }
[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($outputFile)) | Out-Null
$stream = [IO.File]::Open($outputFile, [IO.FileMode]::CreateNew)
$writer = [IO.BinaryWriter]::new($stream)
try {
    $writer.Write([uint16]0)
    $writer.Write([uint16]1)
    $writer.Write([uint16]$frames.Count)
    $offset = [uint32](6 + 16 * $frames.Count)
    foreach ($frame in $frames) {
        $dimension = if ($frame.Size -eq 256) { [byte]0 } else { [byte]$frame.Size }
        $writer.Write($dimension)
        $writer.Write($dimension)
        $writer.Write([byte]0)
        $writer.Write([byte]0)
        $writer.Write([uint16]1)
        $writer.Write([uint16]32)
        $writer.Write([uint32]$frame.Bytes.Length)
        $writer.Write($offset)
        $offset += [uint32]$frame.Bytes.Length
    }
    foreach ($frame in $frames) { $writer.Write([byte[]]$frame.Bytes) }
} finally { $writer.Dispose() }
Write-Output "Created Windows icon: $outputFile ($($frames.Count) sizes)"
