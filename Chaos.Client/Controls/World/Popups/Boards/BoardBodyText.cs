#region
using System.Text;
using Chaos.Client.Controls.Components;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
#endregion

namespace Chaos.Client.Controls.World.Popups.Boards;

/// <summary>
///     Renders a board post / mail body in the TTF font at native resolution WITH inline <c>&lt;red&gt;/&lt;green&gt;/
///     &lt;reset&gt;</c> color markup (same palette as NPC dialogs/tooltips) and clickable https:// links. Shared by
///     <see cref="ArticleReadControl" /> and <see cref="MailReadControl" /> so both behave identically.
/// </summary>
internal static class BoardBodyText
{
    /// <summary>Wraps the body into colored runs per line, at the given pixel width and (scaled) font size.</summary>
    public static List<List<RichRun>> Wrap(string text, int maxWidth, int font) => RichText.Wrap(text, maxWidth, font);

    /// <summary>
    ///     Draws one wrapped line at (<paramref name="x" />, <paramref name="y" />): first underlines any https:// links
    ///     (behind the glyphs), then draws each colored run with a drop shadow. Links are appended to
    ///     <paramref name="links" /> for the owner's hover/click hit-testing.
    /// </summary>
    public static void DrawLine(
        SpriteBatch spriteBatch,
        IReadOnlyList<RichRun> runs,
        int x,
        int y,
        int font,
        int lineHeight,
        Color defaultColor,
        float alpha,
        int step,
        List<(Rectangle Rect, string Url)> links)
    {
        //visible text of the line (markup already stripped into runs) drives link detection
        var sb = new StringBuilder();

        foreach (var run in runs)
            sb.Append(run.Text);

        var linkStart = links.Count;
        TextLinks.CollectLine(sb.ToString(), font, x, y, lineHeight, links);
        TextLinks.DrawUnderlines(spriteBatch, links, defaultColor * alpha, step, startIndex: linkStart);

        var shadowCol = Color.Black * (0.5f * alpha);
        var shadow = new Vector2(step);
        var rx = x;

        foreach (var run in runs)
        {
            if (run.Text.Length == 0)
                continue;

            var tex = TtfTextRenderer.GetLine(run.Text, font);

            if (tex is not null)
            {
                spriteBatch.Draw(tex, new Vector2(rx, y) + shadow, shadowCol);
                spriteBatch.Draw(tex, new Vector2(rx, y), (run.Color ?? defaultColor) * alpha);
            }

            rx += TtfTextRenderer.MeasureWidth(run.Text, font);
        }
    }
}
