#region
using Chaos.Client.Rendering;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
#endregion

namespace Chaos.Client.Controls.Components;

/// <summary>
///     Paints a <see cref="UILabel" /> in the TTF font at native resolution, mapping its native position into a magnifying
///     ScaleHost's on-screen rect (so the glyphs grow crisply with the window instead of being upscaled bitmap pixels).
///     This is the shared worker behind WorldScreen's generic menu-text pass; it mirrors the alignment + vertical-centering
///     of <see cref="UILabel" />'s own custom-font path, and handles word-wrapped labels by wrapping to the label's width.
/// </summary>
internal static class NativeText
{
    private const int DEFAULT_FONT = 12;

    public static void DrawLabel(SpriteBatchEx spriteBatch, UILabel label, int screenOriginX, int screenOriginY, int nativeOriginX, int nativeOriginY, float scale, float alpha)
    {
        if (!label.Visible || string.IsNullOrEmpty(label.Text) || !TtfTextRenderer.Available || (scale <= 0f) || (alpha <= 0f))
            return;

        var fontBase = label.CustomFontSize > 0 ? label.CustomFontSize : DEFAULT_FONT;
        var font = Math.Max(1, (int)MathF.Round(fontBase * scale));

        //the label's inner (padded) box in native coords, then mapped into the root's magnified screen rect. A descendant
        //maps as screenX = screenOrigin + (native - nativeOrigin) * scale (the two origins are equal for a ScaleHost; they
        //differ for the lobby, whose descendants live in 640-virtual space).
        var innerXn = label.ScreenX + label.PaddingLeft;
        var innerYn = label.ScreenY + label.PaddingTop;
        var innerWn = label.Width - label.PaddingLeft - label.PaddingRight;
        var innerHn = label.Height - label.PaddingTop - label.PaddingBottom;

        var ix = screenOriginX + (int)((innerXn - nativeOriginX) * scale);
        var iy = screenOriginY + (int)((innerYn - nativeOriginY) * scale);
        var iw = Math.Max(1, (int)(innerWn * scale));
        var ih = (int)(innerHn * scale);

        //clip to the label's CURRENT clip rect (own bounds intersected with every parent clip, e.g. a scroll viewport),
        //mapped to the magnified screen - so a row scrolled out of a list is hidden and a partially-visible one is cut at
        //the viewport edge, exactly like the in-place bitmap path. An empty clip = fully scrolled out, so draw nothing.
        var clipN = label.CurrentClipRect;

        if ((clipN.Width <= 0) || (clipN.Height <= 0))
            return;

        var clip = new Rectangle(
            screenOriginX + (int)((clipN.X - nativeOriginX) * scale),
            screenOriginY + (int)((clipN.Y - nativeOriginY) * scale),
            Math.Max(1, (int)(clipN.Width * scale)),
            Math.Max(1, (int)(clipN.Height * scale)));

        var lineH = TtfTextRenderer.LineHeight(font);
        var shadow = new Vector2(Math.Max(1, (int)MathF.Round(scale)));
        var shadowCol = Color.Black * (0.5f * alpha);
        var col = label.ForegroundColor * alpha;

        if (label.CollectLinks)
            label.LinkRects.Clear();

        void DrawOne(string text, int lineY)
        {
            if (text.Length == 0)
                return;

            var tex = TtfTextRenderer.GetLine(text, font);

            if (tex is null)
                return;

            var w = tex.Width;

            var tx = label.HorizontalAlignment switch
            {
                HorizontalAlignment.Center when w <= iw => ix + ((iw - w) / 2),
                HorizontalAlignment.Right               => ix + iw - w,
                _                                       => ix
            };

            DrawClipped(spriteBatch, tex, tx + (int)shadow.X, lineY + (int)shadow.Y, shadowCol, clip);
            DrawClipped(spriteBatch, tex, tx, lineY, col, clip);
        }

        //inline color markup (<red>/<green>/<reset> ...): wrap to the box width by VISIBLE text and draw each run in its
        //own colour. Mirrors UILabel.DrawCustomFontWrapped's rich path, at the host's scaled font/position.
        if (label.RichTextMarkup && RichText.HasMarkup(label.Text))
        {
            var richLines = RichText.Wrap(label.Text, label.WordWrap ? iw : 0, font);
            var startYr = (label.VerticalAlignment == VerticalAlignment.Bottom ? iy + ih - (richLines.Count * lineH) : iy) - (int)(label.ScrollOffset * scale);

            for (var li = 0; li < richLines.Count; li++)
            {
                var lineYr = startYr + (li * lineH);

                //collect this line's https:// links and underline them BEHIND the text (drawn before the glyphs)
                if (label.CollectLinks)
                {
                    var lineStr = string.Empty;

                    foreach (var run in richLines[li])
                        lineStr += run.Text;

                    var linkStart = label.LinkRects.Count;
                    TextLinks.CollectLine(lineStr, font, ix, lineYr, lineH, label.LinkRects);
                    TextLinks.DrawUnderlines(spriteBatch, label.LinkRects, col, Math.Max(1, (int)MathF.Round(scale)), clip, linkStart);
                }

                var rx = ix;

                foreach (var run in richLines[li])
                {
                    if (run.Text.Length == 0)
                        continue;

                    var rtex = TtfTextRenderer.GetLine(run.Text, font);

                    if (rtex is not null)
                    {
                        DrawClipped(spriteBatch, rtex, rx + (int)shadow.X, lineYr + (int)shadow.Y, shadowCol, clip);
                        DrawClipped(spriteBatch, rtex, rx, lineYr, (run.Color ?? label.ForegroundColor) * alpha, clip);
                    }

                    rx += TtfTextRenderer.MeasureWidth(run.Text, font);
                }
            }

            return;
        }

        if (label.WordWrap)
        {
            var lines = TtfTextRenderer.WrapText(label.Text, iw, font);
            var totalH = lines.Count * lineH;

            //mirror UILabel.DrawCustomFontWrapped: lines are LEFT-aligned and anchored to the TOP (only Bottom alignment
            //differs), NOT vertically centered. The default VerticalAlignment is Center, so the old center branch put a
            //top-anchored notice block in the middle of its box. Scroll offset shifts the block up and the clip cuts overflow.
            var startY = (label.VerticalAlignment == VerticalAlignment.Bottom ? iy + ih - totalH : iy) - (int)(label.ScrollOffset * scale);

            for (var i = 0; i < lines.Count; i++)
            {
                var ly = startY + (i * lineH);

                //collect this line's https:// links and underline them BEHIND the text (drawn before the glyphs)
                if (label.CollectLinks)
                {
                    var linkStart = label.LinkRects.Count;
                    TextLinks.CollectLine(lines[i], font, ix, ly, lineH, label.LinkRects);
                    TextLinks.DrawUnderlines(spriteBatch, label.LinkRects, col, Math.Max(1, (int)MathF.Round(scale)), clip, linkStart);
                }

                DrawWrappedLine(spriteBatch, lines[i], ix, ly, font, col, shadow, shadowCol, clip);
            }

            return;
        }

        //single line: optionally shrink the font until the text fits the box width (a varying-length plate like the
        //social-status word), then vertically center in the box (matches UILabel's custom-font path)
        if (label.AutoFitWidth && (label.Text.Length > 0))
        {
            var floor = Math.Max(1, (int)MathF.Round(font * 0.55f));

            while ((font > floor) && (TtfTextRenderer.MeasureWidth(label.Text, font) > iw))
                font--;

            lineH = TtfTextRenderer.LineHeight(font);
        }

        DrawOne(label.Text, iy + ((ih - lineH) / 2));
    }

    //draws one wrapped line LEFT-aligned at (x, y), clipped (source-rect) to the label's mapped box so scrolled/overflow
    //lines never spill past the notice - the in-place path is clipped by ClipRect; this reproduces that here.
    private static void DrawWrappedLine(SpriteBatchEx spriteBatch, string text, int x, int y, int font, Color col, Vector2 shadow, Color shadowCol, Rectangle clip)
    {
        if (text.Length == 0)
            return;

        var tex = TtfTextRenderer.GetLine(text, font);

        if (tex is null)
            return;

        DrawClipped(spriteBatch, tex, x + (int)shadow.X, y + (int)shadow.Y, shadowCol, clip);
        DrawClipped(spriteBatch, tex, x, y, col, clip);
    }

    //draws a glyph-line texture at (x,y) source-rect clipped to the screen-space clip rect (so it never spills past a
    //gadget/scroll-viewport edge). Shared by the menu native-text drawers.
    internal static void DrawClipped(SpriteBatchEx spriteBatch, Texture2D tex, int x, int y, Color color, Rectangle clip)
    {
        int srcX = 0, srcY = 0, w = tex.Width, h = tex.Height;

        if (x < clip.Left) { srcX = clip.Left - x; w -= srcX; x = clip.Left; }
        if (y < clip.Top) { srcY = clip.Top - y; h -= srcY; y = clip.Top; }
        if (x + w > clip.Right) w = clip.Right - x;
        if (y + h > clip.Bottom) h = clip.Bottom - y;

        if ((w <= 0) || (h <= 0))
            return;

        spriteBatch.Draw(tex, new Vector2(x, y), new Rectangle(srcX, srcY, w, h), color);
    }
}
