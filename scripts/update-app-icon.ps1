param(
    [string]$Source = "$PSScriptRoot/../src/Sg.App/Assets/sg.png",
    [string]$Destination = "$PSScriptRoot/../src/Sg.App/Assets/sg.ico"
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$sourceImage = [System.Drawing.Image]::FromFile([IO.Path]::GetFullPath($Source))
try {
    if ($sourceImage.Width -ne $sourceImage.Height) {
        throw 'The app icon source must be square.'
    }

    $sizes = @(16, 20, 24, 32, 40, 48, 64, 96, 128, 256)
    $frames = foreach ($size in $sizes) {
        $bitmap = [System.Drawing.Bitmap]::new($size, $size)
        $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
        $stream = [IO.MemoryStream]::new()
        try {
            $graphics.CompositingMode = [System.Drawing.Drawing2D.CompositingMode]::SourceCopy
            $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
            $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
            $graphics.DrawImage($sourceImage, [System.Drawing.Rectangle]::new(0, 0, $size, $size))
            $bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
            ,$stream.ToArray()
        }
        finally {
            $stream.Dispose()
            $graphics.Dispose()
            $bitmap.Dispose()
        }
    }

    $output = [IO.MemoryStream]::new()
    $writer = [IO.BinaryWriter]::new($output)
    try {
        $writer.Write([uint16]0)
        $writer.Write([uint16]1)
        $writer.Write([uint16]$sizes.Count)
        $offset = 6 + 16 * $sizes.Count
        for ($index = 0; $index -lt $sizes.Count; $index++) {
            $dimension = [byte]($sizes[$index] % 256)
            $writer.Write($dimension)
            $writer.Write($dimension)
            $writer.Write([uint16]0)
            $writer.Write([uint16]1)
            $writer.Write([uint16]32)
            $writer.Write([uint32]$frames[$index].Length)
            $writer.Write([uint32]$offset)
            $offset += $frames[$index].Length
        }
        foreach ($frame in $frames) { $writer.Write([byte[]]$frame) }
        [IO.File]::WriteAllBytes([IO.Path]::GetFullPath($Destination), $output.ToArray())
    }
    finally {
        $writer.Dispose()
        $output.Dispose()
    }
}
finally {
    $sourceImage.Dispose()
}

Write-Host "Saved $Destination with sizes: $($sizes -join ', ')"
