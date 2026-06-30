#region
using SkiaSharp;
#endregion

namespace Chaos.Client.Rendering.Utility;

/// <summary>
///     SkiaSharp helpers for turning an image into compressed bytes for upload/storage. Kept separate from
///     <see cref="ImageUtil" /> (which only mutates <see cref="Microsoft.Xna.Framework.Color" /> arrays / builds
///     textures) because these return encoded byte payloads.
/// </summary>
public static class ImageEncoding
{
    /// <summary>
    ///     Scales <paramref name="source" /> to fit within (<paramref name="maxW" />, <paramref name="maxH" />) keeping
    ///     aspect ratio (never upscaling), then JPEG-encodes it - trying each <paramref name="qualities" /> entry in order
    ///     and returning the first result at or under <paramref name="maxBytes" />. If none fit, the lowest-quality result
    ///     is returned anyway, so a caller that needs a hard size cap must re-check the returned length. Returns null only
    ///     on an encode failure. Shared by the album-screenshot and profile-portrait upload paths.
    /// </summary>
    public static byte[]? ScaleToJpeg(SKImage source, int maxW, int maxH, int maxBytes, params int[] qualities)
    {
        try
        {
            var scale = Math.Min(1f, Math.Min((float)maxW / source.Width, (float)maxH / source.Height));
            var w = Math.Max(1, (int)(source.Width * scale));
            var h = Math.Max(1, (int)(source.Height * scale));

            using var bmp = new SKBitmap(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul));
            source.ScalePixels(bmp.PeekPixels(), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None));
            using var scaled = SKImage.FromBitmap(bmp);

            byte[]? last = null;

            foreach (var quality in qualities)
            {
                using var data = scaled.Encode(SKEncodedImageFormat.Jpeg, quality);
                last = data.ToArray();

                if (last.Length <= maxBytes)
                    return last;
            }

            return last;
        }
        catch
        {
            return null;
        }
    }
}
