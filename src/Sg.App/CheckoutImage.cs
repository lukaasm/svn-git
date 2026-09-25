using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using Sg.Core;

namespace Sg.App;

/// <summary>Decode once, preserve transparency and aspect ratio, and store a bounded PNG for fast UI loads.</summary>
internal static class CheckoutImage
{
    public static async Task<byte[]> ReadAsync(string path)
    {
        using var file = File.OpenRead(path);
        if (file.Length > 10 * 1024 * 1024) throw new SgException("Choose an image smaller than 10 MB.");
        using var input = file.AsRandomAccessStream();
        var decoder = await BitmapDecoder.CreateAsync(input);
        var frame = await decoder.GetFrameAsync(0);
        // ICO frames are alternate sizes, unlike an animation. Start from the largest available
        // image so a 16 px first entry does not blur in previews or on high-DPI displays.
        if (decoder.DecoderInformation.CodecId == BitmapDecoder.IcoDecoderId)
        {
            for (uint i = 1; i < decoder.FrameCount; i++)
            {
                var candidate = await decoder.GetFrameAsync(i);
                if ((long)candidate.PixelWidth * candidate.PixelHeight > (long)frame.PixelWidth * frame.PixelHeight)
                    frame = candidate;
            }
        }
        if (frame.PixelWidth == 0 || frame.PixelHeight == 0 || (long)frame.PixelWidth * frame.PixelHeight > 16_000_000)
            throw new SgException("Choose an image with at most 16 million pixels.");
        var scale = Math.Min(1, (double)CheckoutAppearance.MaxDimension / Math.Max(frame.OrientedPixelWidth, frame.OrientedPixelHeight));
        var transform = new BitmapTransform
        {
            ScaledWidth = (uint)Math.Max(1, Math.Round(frame.PixelWidth * scale)),
            ScaledHeight = (uint)Math.Max(1, Math.Round(frame.PixelHeight * scale)),
            InterpolationMode = BitmapInterpolationMode.Fant,
        };
        using var bitmap = await frame.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
            transform, ExifOrientationMode.RespectExifOrientation, ColorManagementMode.ColorManageToSRgb);
        using var output = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, output);
        encoder.SetSoftwareBitmap(bitmap);
        await encoder.FlushAsync();
        output.Seek(0);
        using var reader = new DataReader(output);
        await reader.LoadAsync((uint)output.Size);
        var bytes = new byte[(int)output.Size];
        reader.ReadBytes(bytes);
        return bytes;
    }
}
