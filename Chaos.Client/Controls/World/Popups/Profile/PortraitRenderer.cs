#region
using Chaos.Client.Rendering;
using Microsoft.Xna.Framework.Graphics;
using SkiaSharp;
#endregion

namespace Chaos.Client.Controls.World.Popups.Profile;

/// <summary>
///     Decodes a profile portrait (JPEG/PNG) and "cover"-fits it to a box: crop to the box's aspect ratio, then scale
///     to the box, so it FILLS the frame without distortion (the same framing the website uses). Shared by the self and
///     other profile equipment tabs so a tall/wide portrait never overflows or shows only a corner.
/// </summary>
internal static class PortraitRenderer
{
    public static Texture2D? Cover(byte[]? data, int boxWidth, int boxHeight)
    {
        if (data is not { Length: > 0 })
            return null;

        using var src = SKImage.FromEncodedData(data);

        if (src is null)
            return null;

        var bw = boxWidth > 0 ? boxWidth : src.Width;
        var bh = boxHeight > 0 ? boxHeight : src.Height;

        var boxAspect = (float)bw / bh;
        var srcAspect = (float)src.Width / src.Height;
        SKRectI crop;

        if (srcAspect > boxAspect)
        {
            var cw = Math.Max(1, (int)(src.Height * boxAspect));
            crop = new SKRectI((src.Width - cw) / 2, 0, ((src.Width - cw) / 2) + cw, src.Height);
        } else
        {
            var ch = Math.Max(1, (int)(src.Width / boxAspect));
            crop = new SKRectI(0, (src.Height - ch) / 2, src.Width, ((src.Height - ch) / 2) + ch);
        }

        using var cropped = src.Subset(crop);

        if (cropped is null)
            return null;

        var info = new SKImageInfo(bw, bh, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var bmp = new SKBitmap(info);
        cropped.ScalePixels(bmp.PeekPixels(), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None));
        using var scaled = SKImage.FromBitmap(bmp);

        return TextureConverter.ToTexture2D(scaled);
    }
}
