#region
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
#endregion

namespace Chaos.Client.Controls.Components;

/// <summary>
///     Stores text-draw state and renders it via the font texture atlas. Re-measures only when content, color,
///     <see cref="WrapWidth" />, or <see cref="ShadowStyle" /> changes between successive <see cref="Update" /> calls.
///     No GPU resources are held (the shared font atlas handles all rendering).
/// </summary>
public sealed class TextElement
{
    private int LastWrapWidth;
    private ShadowStyle LastShadowStyle;
    private int LastCustomFontSize;

    public bool ColorCodesEnabled { get; set; } = true;
    public Color Color { get; private set; } = LegendColors.Silver;
    public int Height { get; private set; }
    public string Text { get; private set; } = string.Empty;
    public int Width { get; private set; }
    public IReadOnlyList<string>? WrappedLines { get; private set; }
    public bool HasContent => Width > 0;

    /// <summary>
    ///     Width to wrap at, in pixels. Zero disables wrapping. Read by <see cref="Update" />.
    /// </summary>
    public int WrapWidth { get; set; }

    /// <summary>
    ///     Shadow style applied during <see cref="Draw" />; also widens/heightens the bounding box reported by
    ///     <see cref="Width" /> and <see cref="Height" />.
    /// </summary>
    public ShadowStyle ShadowStyle { get; set; }

    /// <summary>
    ///     Shadow color used when <see cref="ShadowStyle" /> is not <see cref="ShadowStyle.None" />.
    /// </summary>
    public Color ShadowColor { get; set; } = Color.Black;

    /// <summary>
    ///     When greater than zero, the text is measured (and drawn by the owning label) with the optional TrueType font
    ///     at this pixel size instead of the bitmap font. Single line only; word wrap and selection stay on the bitmap
    ///     font. Zero keeps the bitmap font.
    /// </summary>
    public int CustomFontSize { get; set; }

    /// <summary>True when this text should use the TrueType font: opted in via <see cref="CustomFontSize" /> and a font is loaded.</summary>
    public bool UsesCustomFont => (CustomFontSize > 0) && TtfTextRenderer.Available;

    /// <summary>Single-line text the TrueType path actually draws (color codes stripped). Set during <see cref="Update" />.</summary>
    public string CustomDrawText { get; private set; } = string.Empty;

    /// <summary>
    ///     Opt in to inline <c>&lt;green&gt;/&lt;red&gt;/&lt;white&gt;/&lt;reset&gt;</c> color markup on the TrueType path.
    ///     When set, <see cref="RichLines" /> holds the per-line colored runs the owning label draws; <see cref="Width" />
    ///     and <see cref="WrappedLines" /> still measure the visible text. TrueType + word-wrap only.
    /// </summary>
    public bool RichMarkup { get; set; }

    /// <summary>Per-line colored runs for the rich-markup path, set during <see cref="Update" />. Null when not rich.</summary>
    public List<List<RichRun>>? RichLines { get; private set; }

    /// <summary>
    ///     Re-measures, and (when <see cref="WrapWidth" /> is positive) re-wraps the text. No-op when
    ///     <paramref name="text" />, <paramref name="color" />, <see cref="WrapWidth" />, and
    ///     <see cref="ShadowStyle" /> all match the previous call.
    /// </summary>
    public void Update(string text, Color color)
    {
        if ((text == Text) && (color == Color) && (WrapWidth == LastWrapWidth) && (ShadowStyle == LastShadowStyle)
            && (CustomFontSize == LastCustomFontSize))
            return;

        Text = text;
        Color = color;
        LastWrapWidth = WrapWidth;
        LastShadowStyle = ShadowStyle;
        LastCustomFontSize = CustomFontSize;

        if (string.IsNullOrEmpty(text))
        {
            Width = 0;
            Height = 0;
            WrappedLines = null;

            return;
        }

        if (UsesCustomFont)
        {
            //TrueType path: the owning label draws it; Width/Height feed the label's layout/alignment.
            var lineHeight = TtfTextRenderer.LineHeight(CustomFontSize);

            //inline <green>/<red>/<white>/<reset> markup: wrap by VISIBLE width and keep the colored runs per line
            //(the owning label draws RichLines run by run). Width still reports the widest actual line so the tooltip
            //shrinks to content. Opted in via RichMarkup; everything else stays single-color below.
            if (RichMarkup)
            {
                var rich = RichText.Wrap(text, WrapWidth > 0 ? WrapWidth : 0, CustomFontSize);
                RichLines = rich;

                var visible = new List<string>(rich.Count);
                Width = 0;

                foreach (var richLine in rich)
                {
                    var sb = new System.Text.StringBuilder();

                    foreach (var run in richLine)
                        sb.Append(run.Text);

                    var lineText = sb.ToString();
                    visible.Add(lineText);
                    Width = Math.Max(Width, TtfTextRenderer.MeasureWidth(lineText, CustomFontSize));
                }

                WrappedLines = visible;
                CustomDrawText = string.Join("\n", visible);
                Height = Math.Max(lineHeight, rich.Count * lineHeight);

                return;
            }

            RichLines = null;

            //The TTF path has no per-segment color, so strip {=x} codes rather than draw them as literal text.
            var draw = ColorCodesEnabled ? TextRenderer.StripColorCodes(text) : text;
            CustomDrawText = draw;

            if (WrapWidth > 0)
            {
                WrappedLines = TtfTextRenderer.WrapText(draw, WrapWidth, CustomFontSize);

                //report the actual widest line so callers can shrink to content, not always the wrap box
                Width = 0;

                foreach (var line in WrappedLines)
                    Width = Math.Max(Width, TtfTextRenderer.MeasureWidth(line, CustomFontSize));

                Height = Math.Max(lineHeight, WrappedLines.Count * lineHeight);
            } else
            {
                WrappedLines = null;
                Width = TtfTextRenderer.MeasureWidth(draw, CustomFontSize);
                Height = lineHeight;
            }

            return;
        }

        if (WrapWidth > 0)
        {
            WrappedLines = TextRenderer.WrapText(text, WrapWidth);
            Width = WrapWidth;
            Height = Math.Max(TextRenderer.CHAR_HEIGHT, WrappedLines.Count * TextRenderer.CHAR_HEIGHT);

            return;
        }

        WrappedLines = null;
        var marginX = ShadowStyle switch
        {
            ShadowStyle.BothSides                              => 2,
            ShadowStyle.BottomLeft or ShadowStyle.BottomRight  => 1,
            _                                                  => 0
        };
        var marginY = ShadowStyle == ShadowStyle.None ? 0 : 1;
        Width = TextRenderer.MeasureWidth(text) + marginX;
        Height = TextRenderer.CHAR_HEIGHT + marginY;
    }

    /// <summary>
    ///     Draws <paramref name="text" /> (or <see cref="Text" /> when null) at <paramref name="position" />,
    ///     applying <see cref="ShadowStyle" /> and clipping each pass to <paramref name="clipRect" />. Pass
    ///     <see cref="Rectangle.Empty" /> (or omit) to skip clipping entirely.
    /// </summary>
    /// <summary>
    ///     Draws <paramref name="text" /> (or <see cref="Text" /> when null) at <paramref name="position" />,
    ///     applying <see cref="ShadowStyle" /> and clipping each pass to <paramref name="clipRect" />. Pass
    ///     <see cref="Rectangle.Empty" /> (or omit) to skip clipping entirely.
    /// </summary>
    public void Draw(SpriteBatch spriteBatch, Vector2 position, Rectangle clipRect = default, string? text = null, float opacity = 1f)
    {
        text ??= Text;

        if (string.IsNullOrEmpty(text))
            return;

        switch (ShadowStyle)
        {
            case ShadowStyle.None:
                DrawClipped(spriteBatch, position, text, Color, clipRect, opacity, applyCodeColors: true);

                break;
            case ShadowStyle.BottomLeft:
                DrawClipped(spriteBatch, position + new Vector2(0, 1), text, ShadowColor, clipRect, opacity, applyCodeColors: false);
                DrawClipped(spriteBatch, position + new Vector2(1, 0), text, Color, clipRect, opacity, applyCodeColors: true);

                break;
            case ShadowStyle.BottomRight:
                DrawClipped(spriteBatch, position + new Vector2(1, 1), text, ShadowColor, clipRect, opacity, applyCodeColors: false);
                DrawClipped(spriteBatch, position, text, Color, clipRect, opacity, applyCodeColors: true);

                break;
            case ShadowStyle.BothSides:
                DrawClipped(spriteBatch, position + new Vector2(2, 1), text, ShadowColor, clipRect, opacity, applyCodeColors: false);
                DrawClipped(spriteBatch, position + new Vector2(0, 1), text, ShadowColor, clipRect, opacity, applyCodeColors: false);
                DrawClipped(spriteBatch, position + new Vector2(1, 0), text, Color, clipRect, opacity, applyCodeColors: true);

                break;
        }
    }

    private void DrawClipped(
        SpriteBatch spriteBatch,
        Vector2 position,
        string text,
        Color color,
        Rectangle clipRect,
        float opacity,
        bool applyCodeColors)
    {
        if (clipRect.IsEmpty)
        {
            TextRenderer.DrawText(spriteBatch, position, text, color, ColorCodesEnabled, opacity, applyCodeColors);

            return;
        }

        var textWidth = TextRenderer.MeasureWidth(text);
        var textBounds = new Rectangle((int)position.X, (int)position.Y, textWidth, TextRenderer.CHAR_HEIGHT);

        if (!clipRect.Intersects(textBounds))
            return;

        if (clipRect.Contains(textBounds))
        {
            TextRenderer.DrawText(spriteBatch, position, text, color, ColorCodesEnabled, opacity, applyCodeColors);

            return;
        }

        TextRenderer.DrawTextClipped(spriteBatch, position, text, color, clipRect, ColorCodesEnabled, opacity, applyCodeColors);
    }
}
