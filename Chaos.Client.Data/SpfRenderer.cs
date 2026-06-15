#region
using DALib.Definitions;
using DALib.Drawing;
using DALib.Drawing.Virtualized;
using SkiaSharp;
#endregion

namespace Chaos.Client.Data;

/// <summary>
///     Wraps DALib's <see cref="Graphics.RenderImage(SpfFrame, Palette)" /> so palettized SPF frames whose BACKGROUND is a
///     non-zero palette index that maps to <c>(0,0,0)</c> (e.g. full-art NPC illustrations in <c>npcbase.dat</c> such as
///     <c>enchant.spf</c>) render with a transparent background instead of an opaque black box - matching the Colorized
///     path's "pure black -> transparent" convention.
///     <para />
///     IMPORTANT: only the BACKGROUND black is keyed out, by flood-filling pure-black pixels reachable from the frame
///     EDGE. Interior black that is part of the art (a black cloak, hair, outlines) is enclosed by coloured pixels, so the
///     fill never reaches it and it stays opaque. This follows the sprite's SHAPE rather than keying every black pixel,
///     which used to punch holes through dark sprites (they went see-through and dropped out of the alpha-masked night tint).
/// </summary>
public static class SpfRenderer
{
    public static SKImage RenderFrame(SpfFile spf, int frameIndex)
        => RenderFrameCore(spf[frameIndex], spf.Format, spf.PrimaryColors);

    public static SKImage RenderFrame(SpfView spf, int frameIndex)
        => RenderFrameCore(spf[frameIndex], spf.Format, spf.PrimaryColors);

    private static SKImage RenderFrameCore(SpfFrame frame, SpfFormatType format, Palette? primaryColors)
    {
        if (format == SpfFormatType.Palettized && primaryColors is not null)
            return KeyOutBackgroundBlack(Graphics.RenderImage(frame, primaryColors));

        return Graphics.RenderImage(frame);
    }

    //flood-fill from the border, turning edge-connected pure-black (and already-transparent) pixels transparent and
    //stopping at any coloured pixel - so only the background is removed and interior black art is preserved.
    private static SKImage KeyOutBackgroundBlack(SKImage rendered)
    {
        using var bmp = SKBitmap.FromImage(rendered);
        rendered.Dispose();

        var w = bmp.Width;
        var h = bmp.Height;

        if ((w > 0) && (h > 0))
        {
            var pixels = bmp.Pixels;
            var visited = new bool[w * h];
            var stack = new Stack<int>();

            bool IsBackground(SKColor c) => (c.Alpha == 0) || ((c.Red == 0) && (c.Green == 0) && (c.Blue == 0));

            void Seed(int x, int y)
            {
                if ((x < 0) || (y < 0) || (x >= w) || (y >= h))
                    return;

                var idx = (y * w) + x;

                if (visited[idx] || !IsBackground(pixels[idx]))
                    return;

                visited[idx] = true;
                stack.Push(idx);
            }

            for (var x = 0; x < w; x++)
            {
                Seed(x, 0);
                Seed(x, h - 1);
            }

            for (var y = 0; y < h; y++)
            {
                Seed(0, y);
                Seed(w - 1, y);
            }

            while (stack.Count > 0)
            {
                var idx = stack.Pop();

                if (pixels[idx].Alpha != 0)
                    pixels[idx] = SKColors.Transparent; //an edge-connected pure-black background pixel

                var px = idx % w;
                var py = idx / w;

                Seed(px - 1, py);
                Seed(px + 1, py);
                Seed(px, py - 1);
                Seed(px, py + 1);
            }

            bmp.Pixels = pixels;
        }

        //copy out so the returned image does not depend on the bitmap we are about to dispose
        return SKImage.FromPixelCopy(bmp.PeekPixels());
    }
}
