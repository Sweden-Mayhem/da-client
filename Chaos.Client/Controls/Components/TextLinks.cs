#region
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
#endregion

namespace Chaos.Client.Controls.Components;

/// <summary>
///     Finds <c>https://</c> links in already-wrapped TTF text lines and makes available their on-screen rects, so a control that
///     draws rich/wrapped body text (board posts, mail) can show a hand cursor on hover and open the link on click. The
///     owner clears its rect list, runs <see cref="CollectLine" /> for each visible drawn line during its native-text
///     pass, then tests the cursor against the list with <see cref="HitTest" />.
/// </summary>
public static class TextLinks
{
    private const string Scheme = "https://";

    /// <summary>Appends the screen rect + url of every https:// span in one drawn line to <paramref name="into" />.
    ///     <paramref name="lineScreenX" />/<paramref name="lineScreenY" /> are the line's drawn top-left in screen pixels
    ///     and <paramref name="font" /> the (scaled) size it was drawn at, so the rects line up with the visible glyphs.</summary>
    public static void CollectLine(string line, int font, int lineScreenX, int lineScreenY, int lineHeight, List<(Rectangle Rect, string Url)> into)
    {
        if (string.IsNullOrEmpty(line))
            return;

        var i = 0;

        while (true)
        {
            var start = line.IndexOf(Scheme, i, StringComparison.OrdinalIgnoreCase);

            if (start < 0)
                break;

            var end = start;

            while ((end < line.Length) && !char.IsWhiteSpace(line[end]))
                end++;

            //drop trailing punctuation that usually isn't part of the url (a link at the end of a sentence etc.)
            while ((end > start + Scheme.Length) && (line[end - 1] is '.' or ',' or ';' or ':' or ')' or ']' or '>' or '"' or '\''))
                end--;

            var url = line[start..end];

            if (url.Length > Scheme.Length)
            {
                var preW = TtfTextRenderer.MeasureWidth(line[..start], font);
                var urlW = TtfTextRenderer.MeasureWidth(url, font);
                into.Add((new Rectangle(lineScreenX + preW, lineScreenY, urlW, lineHeight), url));
            }

            i = end;
        }
    }

    /// <summary>Returns the url whose rect contains the screen point, or null.</summary>
    public static string? HitTest(List<(Rectangle Rect, string Url)> rects, int screenX, int screenY)
    {
        foreach (var (rect, url) in rects)
            if (rect.Contains(screenX, screenY))
                return url;

        return null;
    }

    /// <summary>Draws an underline under each collected link rect (just below the glyph baseline), optionally clipped.
    ///     Draw this BEFORE the glyphs so the line sits ON TOP of its underline. <paramref name="startIndex" /> lets a
    ///     caller underline only the rects it just appended for the current line.</summary>
    public static void DrawUnderlines(SpriteBatch spriteBatch, List<(Rectangle Rect, string Url)> rects, Color color, int thickness, Rectangle? clip = null, int startIndex = 0)
    {
        var pixel = UIElement.GetPixel();

        for (var k = startIndex; k < rects.Count; k++)
        {
            var rect = rects[k].Rect;
            var u = new Rectangle(rect.X, rect.Y + (int)(rect.Height * 0.92f), rect.Width, Math.Max(1, thickness));

            if (clip is { } c)
                u = Rectangle.Intersect(u, c);

            if ((u.Width > 0) && (u.Height > 0))
                spriteBatch.Draw(pixel, u, color);
        }
    }
}
