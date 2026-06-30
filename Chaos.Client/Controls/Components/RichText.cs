#region
using System.Text;
using Chaos.Client.Rendering;
using Microsoft.Xna.Framework;
#endregion

namespace Chaos.Client.Controls.Components;

/// <summary>A run of text sharing one color. <see cref="Color" /> null means "use the element's default color".</summary>
public readonly record struct RichRun(string Text, Color? Color);

/// <summary>
///     Minimal inline-color markup for the TrueType text path. Recognizes <c>&lt;green&gt; &lt;red&gt; &lt;white&gt;
///     &lt;yellow&gt; &lt;grey&gt; &lt;blue&gt;</c> and <c>&lt;reset&gt;</c> (a closing tag such as <c>&lt;/green&gt;</c>
///     also resets), turning a tagged string into colored runs wrapped to a pixel width by VISIBLE text only. The server
///     item descriptions use it so positive effects read green, negatives red, and weapon damage white, at a glance.
///     Unknown <c>&lt;...&gt;</c> is kept literally, so plain text with a stray '&lt;' is never eaten.
/// </summary>
public static class RichText
{
    //named colors usable as <name>...</name> (or <reset>) in any rich-markup text (NPC dialog, item tooltips). Tuned to
    //read clearly on the dark dialog and tooltip backgrounds, nothing too dark to disappear. Several aliases map to one hue.
    private static readonly Dictionary<string, Color> Palette = new(StringComparer.OrdinalIgnoreCase)
    {
        ["green"]    = new Color(120, 205, 120),
        ["red"]      = new Color(228, 105, 95),
        ["white"]    = new Color(238, 238, 238),
        ["yellow"]   = new Color(230, 212, 140),
        ["grey"]     = new Color(158, 156, 148),
        ["gray"]     = new Color(158, 156, 148),
        ["blue"]     = new Color(100, 149, 237),
        ["orange"]   = new Color(235, 150, 70),
        ["gold"]     = new Color(228, 192, 95),
        ["lime"]     = new Color(160, 222, 90),
        ["cyan"]     = new Color(110, 212, 222),
        ["aqua"]     = new Color(110, 212, 222),
        ["teal"]     = new Color(90, 185, 172),
        ["purple"]   = new Color(176, 132, 232),
        ["violet"]   = new Color(176, 132, 232),
        ["pink"]     = new Color(236, 142, 192),
        ["magenta"]  = new Color(222, 110, 200),
        ["brown"]    = new Color(178, 132, 96),
        ["tan"]      = new Color(212, 182, 140),
        ["silver"]   = new Color(200, 200, 200),
        ["darkgrey"] = new Color(120, 120, 120),
        ["darkgray"] = new Color(120, 120, 120),
        ["black"]    = new Color(28, 28, 28)
    };

    /// <summary>True when the text contains at least one recognized color tag (callers can skip the rich path otherwise).</summary>
    public static bool HasMarkup(string? text)
    {
        if (string.IsNullOrEmpty(text) || (text.IndexOf('<') < 0))
            return false;

        foreach (var run in Parse(text))
            if (run.Color is not null)
                return true;

        return false;
    }

    /// <summary>The visible text with all recognized tags removed.</summary>
    public static string Strip(string? text)
    {
        if (string.IsNullOrEmpty(text) || (text.IndexOf('<') < 0))
            return text ?? string.Empty;

        var sb = new StringBuilder(text.Length);

        foreach (var run in Parse(text))
            sb.Append(run.Text);

        return sb.ToString();
    }

    /// <summary>Parses into flat runs (a run's text may contain spaces and newlines). Unknown tags stay literal.</summary>
    public static List<RichRun> Parse(string? text)
    {
        var runs = new List<RichRun>();

        if (string.IsNullOrEmpty(text))
            return runs;

        Color? color = null;
        var sb = new StringBuilder();

        void Flush()
        {
            if (sb.Length == 0)
                return;

            runs.Add(new RichRun(sb.ToString(), color));
            sb.Clear();
        }

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];

            if (c == '<')
            {
                var close = text.IndexOf('>', i + 1);

                if (close > i)
                {
                    var tag = text.Substring(i + 1, close - i - 1);

                    if (tag.Equals("reset", StringComparison.OrdinalIgnoreCase) || tag.StartsWith('/'))
                    {
                        Flush();
                        color = null;
                        i = close;

                        continue;
                    }

                    if (Palette.TryGetValue(tag, out var col))
                    {
                        Flush();
                        color = col;
                        i = close;

                        continue;
                    }
                }
                //unrecognized: fall through and keep the '<' as literal text
            }

            sb.Append(c);
        }

        Flush();

        return runs;
    }

    /// <summary>
    ///     Parses then wraps to <paramref name="maxWidth" /> pixels (measured on VISIBLE text) at the given TTF size,
    ///     honoring existing newlines. <paramref name="maxWidth" /> &lt;= 0 breaks only on newlines. Each output line is a
    ///     list of colored runs; adjacent same-color runs are merged.
    /// </summary>
    public static List<List<RichRun>> Wrap(string? text, int maxWidth, int size)
    {
        var lines = new List<List<RichRun>>();
        var line = new List<RichRun>();
        var lineWidth = 0;

        var word = new StringBuilder();
        Color? wordColor = null;

        void AppendRun(string s, Color? c)
        {
            if (s.Length == 0)
                return;

            if ((line.Count > 0) && (line[^1].Color == c))
                line[^1] = new RichRun(line[^1].Text + s, c);
            else
                line.Add(new RichRun(s, c));
        }

        void BreakLine()
        {
            lines.Add(line);
            line = [];
            lineWidth = 0;
        }

        void FlushWord()
        {
            if (word.Length == 0)
                return;

            var w = word.ToString();
            word.Clear();
            var color = wordColor;
            wordColor = null;

            var wWidth = TtfTextRenderer.MeasureWidth(w, size);
            var spaceWidth = lineWidth > 0 ? TtfTextRenderer.MeasureWidth(" ", size) : 0;

            //wrap before this word when it (plus the joining space) would overflow a non-empty line
            if ((maxWidth > 0) && (lineWidth > 0) && ((lineWidth + spaceWidth + wWidth) > maxWidth))
            {
                BreakLine();
                spaceWidth = 0;
            }

            //a single word longer than the whole line: force-break it across lines so none of it is clipped/lost
            if ((maxWidth > 0) && (wWidth > maxWidth))
            {
                var start = 0;
                var segWidth = 0;

                for (var i = 0; i < w.Length; i++)
                {
                    var cw = TtfTextRenderer.MeasureWidth(w[i].ToString(), size);

                    if ((segWidth + cw > maxWidth) && (i > start))
                    {
                        AppendRun(w[start..i], color);
                        BreakLine();
                        start = i;
                        segWidth = 0;
                    }

                    segWidth += cw;
                }

                AppendRun(w[start..], color);
                lineWidth = segWidth;

                return;
            }

            if (spaceWidth > 0)
            {
                //the joining space belongs to the preceding run's color so runs stay merged
                AppendRun(" ", line.Count > 0 ? line[^1].Color : color);
                lineWidth += spaceWidth;
            }

            AppendRun(w, color);
            lineWidth += wWidth;
        }

        foreach (var run in Parse(text))
            foreach (var ch in run.Text)
                switch (ch)
                {
                    case '\n':
                        FlushWord();
                        BreakLine();

                        break;
                    case ' ':
                        FlushWord();

                        break;
                    default:
                        if (word.Length == 0)
                            wordColor = run.Color;

                        word.Append(ch);

                        break;
                }

        FlushWord();
        lines.Add(line);

        return lines;
    }

    /// <summary>Visible pixel width of one wrapped line at the given size.</summary>
    public static int LineWidth(IReadOnlyList<RichRun> line, int size)
    {
        if ((line is null) || (line.Count == 0))
            return 0;

        var sb = new StringBuilder();

        foreach (var run in line)
            sb.Append(run.Text);

        return TtfTextRenderer.MeasureWidth(sb.ToString(), size);
    }
}
