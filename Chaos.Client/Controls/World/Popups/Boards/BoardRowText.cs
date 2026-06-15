#region
using Chaos.Client.Controls.Components;
using Chaos.Client.Rendering;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
#endregion

namespace Chaos.Client.Controls.World.Popups.Boards;

/// <summary>
///     Draws board/mail list rows in the TTF font at native resolution, so a list hosted in a magnifying ScaleHost shows
///     crisp text over the upscaled pixel-art frame, the same approach the article/mail bodies, signs and NPC dialog use.
///     The row UILabels are marked <see cref="UILabel.SuppressGlyphs" /> (kept for layout/hit-testing, no bitmap glyphs);
///     this paints them. Call from WorldScreen's native pass with the wrapping ScaleHost's ScreenX/Y, Scale and
///     OpenFraction (as alpha).
/// </summary>
internal static class BoardRowText
{
    private const int ROW_FONT_BASE = 11;

    public static void DrawRows(
        SpriteBatch spriteBatch,
        IReadOnlyList<UILabel> rows,
        int rowHeight,
        int originX,
        int originY,
        float scale,
        float alpha)
    {
        if (!TtfTextRenderer.Available || (scale <= 0f) || (alpha <= 0f))
            return;

        var font = Math.Max(1, (int)MathF.Round(ROW_FONT_BASE * scale));
        var lineH = TtfTextRenderer.LineHeight(font);
        var rowPx = rowHeight * scale;
        var shadowOff = new Vector2(Math.Max(1, (int)MathF.Round(scale)));
        var shadowCol = Color.Black * (0.5f * alpha);

        foreach (var label in rows)
        {
            if (!label.Visible || string.IsNullOrEmpty(label.Text))
                continue;

            var text = Fit(label.Text, font, (int)(label.Width * scale));
            var tex = TtfTextRenderer.GetLine(text, font);

            if (tex is null)
                continue;

            //map the row's native position into the magnified on-screen position, then center the line in the row
            var x = originX + (int)((label.ScreenX - originX) * scale) + (int)(label.PaddingLeft * scale);
            var y = originY + (int)((label.ScreenY - originY) * scale) + (int)((rowPx - lineH) / 2f);
            var p = new Vector2(x, y);

            spriteBatch.Draw(tex, p + shadowOff, shadowCol);
            spriteBatch.Draw(tex, p, label.ForegroundColor * alpha);
        }
    }

    //draws TAB-separated rows as aligned COLUMNS: each row's Text is split on '\t' and segment N is drawn left-aligned
    //at native x-offset columnX[N] (so all rows line up regardless of the proportional font). Each column is
    //ellipsis-truncated to the gap before the next column (the last runs to the row's right edge).
    public static void DrawColumns(
        SpriteBatch spriteBatch,
        IReadOnlyList<UILabel> rows,
        IReadOnlyList<int> columnX,
        int rowHeight,
        int originX,
        int originY,
        float scale,
        float alpha)
    {
        if (!TtfTextRenderer.Available || (scale <= 0f) || (alpha <= 0f) || (columnX.Count == 0))
            return;

        var font = Math.Max(1, (int)MathF.Round(ROW_FONT_BASE * scale));
        var lineH = TtfTextRenderer.LineHeight(font);
        var rowPx = rowHeight * scale;
        var shadowOff = new Vector2(Math.Max(1, (int)MathF.Round(scale)));
        var shadowCol = Color.Black * (0.5f * alpha);
        var gap = (int)(4 * scale);

        foreach (var label in rows)
        {
            if (!label.Visible || string.IsNullOrEmpty(label.Text))
                continue;

            var baseX = originX + (int)((label.ScreenX - originX) * scale) + (int)(label.PaddingLeft * scale);
            var rowRight = baseX + (int)(label.Width * scale);
            var y = originY + (int)((label.ScreenY - originY) * scale) + (int)((rowPx - lineH) / 2f);
            var parts = label.Text.Split('\t');

            for (var c = 0; c < parts.Length; c++)
            {
                var colLeft = baseX + (int)(columnX[Math.Min(c, columnX.Count - 1)] * scale);
                var colRight = (c + 1 < columnX.Count) ? baseX + (int)(columnX[c + 1] * scale) : rowRight;
                var avail = colRight - colLeft - gap;

                if ((avail <= 0) || (parts[c].Length == 0))
                    continue;

                var tex = TtfTextRenderer.GetLine(Fit(parts[c], font, avail), font);

                if (tex is null)
                    continue;

                var p = new Vector2(colLeft, y);
                spriteBatch.Draw(tex, p + shadowOff, shadowCol);
                spriteBatch.Draw(tex, p, label.ForegroundColor * alpha);
            }
        }
    }

    //ellipsis-truncate a row to its usable width so a long board name / subject never spills past the wooden frame
    private static string Fit(string text, int font, int maxWidth)
    {
        if ((maxWidth <= 0) || (TtfTextRenderer.MeasureWidth(text, font) <= maxWidth))
            return text;

        while ((text.Length > 1) && (TtfTextRenderer.MeasureWidth(text + "...", font) > maxWidth))
            text = text[..^1];

        return text + "...";
    }
}
