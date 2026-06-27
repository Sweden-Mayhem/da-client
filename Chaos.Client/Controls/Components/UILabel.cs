#region
using Chaos.Client.Utilities;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
#endregion

namespace Chaos.Client.Controls.Components;

/// <summary>
///     Text label with optional word-wrap, text selection, and color code support. Re-renders only when content or color
///     changes. When WordWrap is true, text wraps to the label width; use ScrollOffset to scroll when ContentHeight exceeds
///     the label bounds.
/// </summary>

// ReSharper disable once ClassCanBeSealed.Global
public class UILabel : UIElement
{
    private readonly TextElement TextElement = new();
    private int CursorPosition;
    private int SelectionAnchor;
    private bool Dragging;
    private long LastClickTime;
    private int LastClickPosition = -1;
    private int ClickCount;

    public HorizontalAlignment HorizontalAlignment { get; set; } = HorizontalAlignment.Left;

    public bool IsSelectable { get; set; }
    public bool HasSelection => IsSelectable && (SelectionAnchor != CursorPosition);
    private int SelectionStart => Math.Min(SelectionAnchor, CursorPosition);
    private int SelectionEnd => Math.Max(SelectionAnchor, CursorPosition);

    public bool ColorCodesEnabled
    {
        get => TextElement.ColorCodesEnabled;
        set => TextElement.ColorCodesEnabled = value;
    }

    public Color ForegroundColor
    {
        get => TextElement.Color;
        set => Invalidate(TextElement.Text, value);
    }

    /// <summary>
    ///     When greater than zero, this label renders with the optional TrueType font (Cinzel by default) at this pixel
    ///     size instead of the bitmap font. Single-line display only: word wrap and text selection stay on the bitmap
    ///     font and ignore this. Zero (the default) keeps the bitmap font, so existing labels are unaffected.
    /// </summary>
    public int CustomFontSize
    {
        get => TextElement.CustomFontSize;
        set
        {
            TextElement.CustomFontSize = value;
            Invalidate(TextElement.Text, TextElement.Color);
        }
    }

    /// <summary>
    ///     When true, Draw still maintains the label's clip rect and hit area but renders NO glyphs (the owner paints
    ///     this label's text itself, elsewhere). The NPC shop and list use this since they sit inside a magnifying
    ///     ScaleHost, so their text is skipped here and redrawn crisp at native resolution (like the dialog's spoken
    ///     text) instead of being upscaled with the pixel-art frame. Off by default, so every other label is unaffected.
    /// </summary>
    public bool SuppressGlyphs { get; set; }

    /// <summary>
    ///     Like <see cref="SuppressGlyphs" /> (no glyphs drawn here), but signals that this label should be painted in TTF
    ///     at native resolution by WorldScreen's generic menu-text pass, which walks the visible ScaleHosts and draws every
    ///     RenderNative label using <see cref="CustomFontSize" /> as the base size. Set this (plus CustomFontSize) to make a
    ///     label inside a magnifying window render crisp TTF instead of upscaled bitmap glyphs. Off by default.
    /// </summary>
    public bool RenderNative { get; set; }

    /// <summary>
    ///     When true (single-line custom/TTF labels only), the font shrinks until the text fits the label's inner width,
    ///     down to ~55% of the requested size. Used for fixed-width plates where the content varies in length (e.g. the
    ///     social-status word on the profile book). Off by default. Honored by the native-text pass (the only path used by
    ///     the magnified profile books, which is where this is needed).
    /// </summary>
    public bool AutoFitWidth { get; set; }

    /// <summary>Marks this label for crisp native-resolution TTF rendering (see <see cref="RenderNative" />) at the given
    ///     base pixel size, and returns it so it can be chained at creation. Used when converting a magnified menu/popup off
    ///     the bitmap font: WorldScreen's generic menu-text pass paints it.</summary>
    public UILabel Native(int fontSize)
    {
        CustomFontSize = fontSize;
        RenderNative = true;

        return this;
    }

    /// <summary>
    ///     Opt in to inline <c>&lt;green&gt;/&lt;red&gt;/&lt;white&gt;/&lt;reset&gt;</c> color markup. TrueType + word-wrap
    ///     only (the item tooltip uses it so effect/stat lines color by meaning). Off by default, so every other label is
    ///     unchanged. Tags are measured out, never drawn as literal text.
    /// </summary>
    public bool RichTextMarkup
    {
        get => TextElement.RichMarkup;
        set
        {
            TextElement.RichMarkup = value;
            Invalidate(TextElement.Text, TextElement.Color);
        }
    }

    /// <summary>Opt in to detecting https:// links: when set, the native-text pass collects each link's on-screen rect
    ///     into <see cref="LinkRects" /> (and underlines them) so the owner can hand-cursor/open them. Off by default.</summary>
    public bool CollectLinks { get; set; }

    /// <summary>On-screen rects + urls of the links in the last native-text draw (see <see cref="CollectLinks" />).</summary>
    public List<(Rectangle Rect, string Url)> LinkRects { get; } = [];

    /// <summary>
    ///     Vertical scroll offset in pixels for wrapped text content.
    /// </summary>
    public int ScrollOffset { get; set; }

    public ShadowStyle ShadowStyle { get; set; }

    /// <summary>
    ///     Pixel offset of the drop shadow for the custom (TrueType) font path, applied when <see cref="ShadowStyle" /> is
    ///     not <see cref="ShadowStyle.None" />. Defaults to a (1,1) diagonal; set to e.g. (0,2) for a straight-down shadow.
    /// </summary>
    public Point ShadowOffset { get; set; } = new(1, 1);

    /// <summary>
    ///     0..1 strength of a soft black "glow" halo drawn behind the custom (TrueType) text. 0 = off. Used by the chat log
    ///     so that, once the window chrome has faded away, the still-visible lines keep a readable dark backing on the bare
    ///     world (a disc of offset black copies of the text; overlap builds a soft dark core that fades at the edges).
    /// </summary>
    public float GlowAlpha { get; set; }

    //radius (px) and per-tap opacity of the glow disc. Radius stays within the chat line's padded height so the halo is
    //continuous across stacked lines and only soft-clips at the very top/bottom of the block.
    private const int GLOW_RADIUS = 3;
    private const float GLOW_TAP_ALPHA = 0.09f;

    public string Text
    {
        get => TextElement.Text;
        set => Invalidate(value, TextElement.Color);
    }

    public float Opacity { get; set; } = 1f;
    public VerticalAlignment VerticalAlignment { get; set; }
    public bool TruncateWithEllipsis { get; set; } = true;
    public bool WordWrap { get; set; }

    /// <summary>
    ///     Total pixel height of the rendered content. For wrapped text, this may exceed the label bounds.
    /// </summary>
    public int ContentHeight => TextElement.Height;

    /// <summary>Measured pixel width of the current text in the active font (wrap width when wrapping).</summary>
    public int ContentWidth => TextElement.Width;

    public UILabel()
    {
        PaddingLeft = 1;
        PaddingRight = 1;
        PaddingTop = 1;
        PaddingBottom = 1;
    }

    public override void Draw(SpriteBatchEx spriteBatch)
    {
        if (!Visible)
            return;

        //empty labels still need a valid ClipRect so ContainsPoint hit-testing works.
        //ClipRect is otherwise only refreshed inside base.Draw, which the next early
        //return would skip.
        if (!TextElement.HasContent)
        {
            UpdateClipRect();

            return;
        }

        base.Draw(spriteBatch);

        //base.Draw refreshed ClipRect, if it collapsed to empty the label is fully outside its
        //parent's clip region. Bail before TextElement.Draw, it treats an empty rect as the
        //"no clipping" sentinel and would otherwise draw the text unclipped at its full screen position
        if ((ClipRect.Width <= 0) || (ClipRect.Height <= 0))
            return;

        //native-text owners (the scaled NPC shop/list) paint this label's glyphs themselves, so skip the in-place render.
        if (SuppressGlyphs)
            return;

        //a RenderNative label inside a native-text root (a magnifying ScaleHost, or the letterbox-upscaled lobby root) is
        //painted crisp by that root's native-text pass (in z-order), so suppress the would-be-upscaled in-place glyphs here.
        //With no such ancestor (e.g. the standalone group-box viewer) fall through and draw in-place, that is already
        //native resolution. base.Draw kept the clip and hit area
        if (RenderNative && HasNativeTextAncestor())
            return;

        var innerX = ScreenX + PaddingLeft;
        var innerY = ScreenY + PaddingTop;
        var innerW = Width - PaddingLeft - PaddingRight;
        var innerH = Height - PaddingTop - PaddingBottom;

        //TrueType opt-in (Cinzel etc.). Selectable labels stay on the bitmap path for caret/selection metrics.
        if (TextElement.UsesCustomFont && !IsSelectable)
        {
            if (WordWrap)
                DrawCustomFontWrapped(spriteBatch, innerX, innerY, innerH);
            else
                DrawCustomFontLine(spriteBatch, innerX, innerY, innerW, innerH);

            return;
        }

        if (HasSelection)
        {
            if (TextElement.WrappedLines is not null)
                DrawWrappedWithSelection(spriteBatch, innerX, innerY, innerH);
            else
                DrawSingleLineWithSelection(spriteBatch, innerX, innerY, innerW, innerH);
        } else if (TextElement.WrappedLines is not null)
        {
            var firstLine = ScrollOffset / TextRenderer.CHAR_HEIGHT;
            var maxLines = (innerH + TextRenderer.CHAR_HEIGHT - 1) / TextRenderer.CHAR_HEIGHT;
            var endLine = Math.Min(TextElement.WrappedLines.Count, firstLine + maxLines);

            for (var lineIdx = firstLine; lineIdx < endLine; lineIdx++)
            {
                var lineY = innerY + (lineIdx - firstLine) * TextRenderer.CHAR_HEIGHT;

                if (TextElement.WrappedLines[lineIdx].Length > 0)
                    TextElement.Draw(spriteBatch, new Vector2(innerX, lineY), ClipRect, TextElement.WrappedLines[lineIdx], Opacity);
            }
        } else if (TruncateWithEllipsis && (TextElement.Width > innerW))
        {
            //ellipsis truncation, find longest prefix that fits with "..."
            var text = TextElement.Text;
            var ellipsisWidth = TextRenderer.MeasureWidth("...");
            var maxTextWidth = innerW - ellipsisWidth;
            var truncLen = text.Length;

            while ((truncLen > 0) && (TextRenderer.MeasureWidth(text[..truncLen]) > maxTextWidth))
                truncLen--;

            var truncated = truncLen > 0 ? text[..truncLen] + "..." : "...";
            TextElement.Draw(spriteBatch, new Vector2(innerX, innerY + (int)(((VerticalAlignment == VerticalAlignment.Top ? TextElement.Height : innerH) - TextRenderer.CHAR_HEIGHT) / 2f)), ClipRect, truncated, Opacity);
        } else
        {
            var bounds = new Rectangle(
                innerX,
                innerY,
                innerW,
                VerticalAlignment == VerticalAlignment.Top ? TextElement.Height : innerH);

            //center alignment clamps to the left edge when the text is wider than the
            //bounds, so overflow clips off the right rather than both sides. Right
            //alignment keeps its natural overflow so clipping stays on the left.
            var textX = HorizontalAlignment switch
            {
                HorizontalAlignment.Center when TextElement.Width <= bounds.Width => bounds.X + (bounds.Width - TextElement.Width) / 2,
                HorizontalAlignment.Right                                         => bounds.X + bounds.Width - TextElement.Width,
                _                                                                 => bounds.X
            };

            var textY = bounds.Y + (bounds.Height - TextElement.Height) / 2;
            var pos = new Vector2(textX, textY);

            TextElement.Draw(spriteBatch, pos, ClipRect, opacity: Opacity);
        }
    }

    //true when some ancestor is a native-text root (a magnifying ScaleHost, or the lobby's letterbox-upscaled root). Such
    //a label's TTF text is painted by that root's native-text pass instead of in-place, so it stays crisp instead of being
    //upscaled with the pixel-art frame / login art.
    private bool HasNativeTextAncestor()
    {
        for (var p = Parent; p is not null; p = p.Parent)
            if (p is INativeTextRoot)
                return true;

        return false;
    }

    private void DrawCustomFontLine(SpriteBatchEx spriteBatch, int innerX, int innerY, int innerW, int innerH)
    {
        var texture = TtfTextRenderer.GetLine(TextElement.CustomDrawText, CustomFontSize);

        if (texture is null)
            return;

        var w = TextElement.Width;
        var h = TextElement.Height;

        var textX = HorizontalAlignment switch
        {
            HorizontalAlignment.Center when w <= innerW => innerX + ((innerW - w) / 2),
            HorizontalAlignment.Right                   => innerX + innerW - w,
            _                                           => innerX
        };

        var textY = innerY + ((innerH - h) / 2);
        var color = ForegroundColor * Opacity;
        var pos = new Vector2(textX, textY);

        //soft black glow first (furthest back): a disc of offset black copies of the line, building a dark halo that
        //gives the text a readable backing when the window chrome behind it has faded away. Off (GlowAlpha 0) for
        //everything but the faded chat log.
        if (GlowAlpha > 0.003f)
        {
            var glow = Color.Black * (GlowAlpha * GLOW_TAP_ALPHA);

            for (var gy = -GLOW_RADIUS; gy <= GLOW_RADIUS; gy++)
                for (var gx = -GLOW_RADIUS; gx <= GLOW_RADIUS; gx++)
                {
                    if ((gx == 0) && (gy == 0))
                        continue;

                    if (((gx * gx) + (gy * gy)) > (GLOW_RADIUS * GLOW_RADIUS))
                        continue;

                    DrawTexture(spriteBatch, texture, pos + new Vector2(gx, gy), glow);
                }
        }

        //DrawTexture clips to ClipRect and tints the white glyph texture. When shadowing is on, draw (back to front):
        //a soft 50% drop shadow further down, then a crisp 1px black outline all around, then the colored text, so the
        //text stays readable over any background.
        //The outline is 8 overlapping black copies, so it accumulates to a much higher opacity than the single text pass.
        //At full opacity that just reads as a solid outline, but while the line FADES a linear outline alpha left the dark
        //outline lingering after the colored text was basically gone. Fade the dark passes super-linearly (cubed, like the
        //glow) so they disappear in lockstep with the text instead of dwelling. At Opacity 1 this is a no-op.
        if (ShadowStyle != ShadowStyle.None)
        {
            var darkFade = Opacity * Opacity * Opacity;

            DrawTexture(spriteBatch, texture, pos + new Vector2(ShadowOffset.X, ShadowOffset.Y), TextElement.ShadowColor * (0.5f * darkFade));

            var outline = TextElement.ShadowColor * darkFade;

            for (var oy = -1; oy <= 1; oy++)
                for (var ox = -1; ox <= 1; ox++)
                    if ((ox != 0) || (oy != 0))
                        DrawTexture(spriteBatch, texture, pos + new Vector2(ox, oy), outline);
        }

        DrawTexture(spriteBatch, texture, pos, color);
    }

    private void DrawCustomFontWrapped(SpriteBatchEx spriteBatch, int innerX, int innerY, int innerH)
    {
        var lineH = TtfTextRenderer.LineHeight(CustomFontSize);

        //rich path: draw each line's colored runs left to right. The renderer's 2px line pad is identical per run, so
        //advancing x by each run's measured advance keeps glyphs abutting cleanly.
        if (TextElement.RichLines is { } richLines)
        {
            var startYRich = VerticalAlignment == VerticalAlignment.Bottom ? innerY + innerH - richLines.Count * lineH : innerY;
            var defColor = ForegroundColor;

            for (var i = 0; i < richLines.Count; i++)
            {
                var x = innerX;
                var y = startYRich + (i * lineH);

                foreach (var run in richLines[i])
                {
                    if (run.Text.Length == 0)
                        continue;

                    var glyphs = TtfTextRenderer.GetLine(run.Text, CustomFontSize);

                    if (glyphs is not null)
                        DrawTexture(spriteBatch, glyphs, new Vector2(x, y), (run.Color ?? defColor) * Opacity);

                    x += TtfTextRenderer.MeasureWidth(run.Text, CustomFontSize);
                }
            }

            return;
        }

        var lines = TextElement.WrappedLines;

        if (lines is null)
            return;

        var totalH = lines.Count * lineH;

        //bottom alignment (chat) anchors the newest lines to the bottom; older lines clip off the top via ClipRect
        var startY = VerticalAlignment == VerticalAlignment.Bottom ? innerY + innerH - totalH : innerY;
        var color = ForegroundColor * Opacity;

        for (var i = 0; i < lines.Count; i++)
        {
            if (lines[i].Length == 0)
                continue;

            var texture = TtfTextRenderer.GetLine(lines[i], CustomFontSize);

            if (texture is not null)
                DrawTexture(spriteBatch, texture, new Vector2(innerX, startY + (i * lineH)), color);
        }
    }

    private void DrawSingleLineWithSelection(SpriteBatchEx spriteBatch, int innerX, int innerY, int innerW, int innerH)
    {
        var text = PlainText;
        var selStart = SnapSelectionBoundary(Math.Min(SelectionStart, text.Length));
        var selEnd = Math.Min(SelectionEnd, text.Length);

        var drawX = HorizontalAlignment switch
        {
            HorizontalAlignment.Center when TextElement.Width <= innerW => innerX + (innerW - TextElement.Width) / 2,
            HorizontalAlignment.Right                                   => innerX + innerW - TextElement.Width,
            _                                                           => innerX
        };

        var drawY = innerY + (((VerticalAlignment == VerticalAlignment.Top ? TextElement.Height : innerH) - TextElement.Height) / 2);

        //pre-selection segment
        if (selStart > 0)
            DrawTextClipped(spriteBatch, new Vector2(drawX, drawY), text[..selStart], TextElement.Color, ColorCodesEnabled);

        //selection segment: white rect + black text
        var selStartX = drawX + (selStart > 0 ? TextRenderer.MeasureWidth(text[..selStart]) : 0);
        var selText = text[selStart..selEnd];
        var selWidth = TextRenderer.MeasureWidth(selText);

        DrawRectClipped(spriteBatch, new Rectangle(selStartX, drawY, selWidth, TextRenderer.CHAR_HEIGHT), Color.White);
        DrawTextClipped(spriteBatch, new Vector2(selStartX, drawY), selText, Color.Black, ColorCodesEnabled);

        //post-selection segment
        if (selEnd < text.Length)
        {
            var postX = selStartX + selWidth;
            DrawTextClipped(spriteBatch, new Vector2(postX, drawY), text[selEnd..], TextElement.Color, ColorCodesEnabled);
        }
    }

    private void DrawWrappedWithSelection(SpriteBatchEx spriteBatch, int innerX, int innerY, int innerH)
    {
        var lines = TextElement.WrappedLines!;
        var firstLine = ScrollOffset / TextRenderer.CHAR_HEIGHT;
        var maxLines = (innerH + TextRenderer.CHAR_HEIGHT - 1) / TextRenderer.CHAR_HEIGHT;
        var endLine = Math.Min(lines.Count, firstLine + maxLines);
        var selStart = SnapSelectionBoundary(SelectionStart);
        var selEnd = SelectionEnd;
        var charOffset = 0;

        //compute character offset up to firstline
        for (var i = 0; i < firstLine; i++)
            charOffset += lines[i].Length;

        for (var i = firstLine; i < endLine; i++)
        {
            var lineText = lines[i];
            var lineY = innerY + (i - firstLine) * TextRenderer.CHAR_HEIGHT;
            var lineStartIdx = charOffset;
            var lineEndIdx = charOffset + lineText.Length;

            if ((selStart < lineEndIdx) && (selEnd > lineStartIdx) && (lineText.Length > 0))
            {
                var hlStart = Math.Max(selStart, lineStartIdx) - lineStartIdx;
                var hlEnd = Math.Min(selEnd, lineEndIdx) - lineStartIdx;

                //pre-selection segment
                if (hlStart > 0)
                    DrawTextClipped(spriteBatch, new Vector2(innerX, lineY), lineText[..hlStart], TextElement.Color, ColorCodesEnabled);

                //selection segment: white rect + black text
                var hlX = innerX + (hlStart > 0 ? TextRenderer.MeasureWidth(lineText[..hlStart]) : 0);
                var hlText = lineText[hlStart..hlEnd];
                var hlWidth = TextRenderer.MeasureWidth(hlText);

                DrawRectClipped(spriteBatch, new Rectangle(hlX, lineY, hlWidth, TextRenderer.CHAR_HEIGHT), Color.White);
                DrawTextClipped(spriteBatch, new Vector2(hlX, lineY), hlText, Color.Black, ColorCodesEnabled);

                //post-selection segment
                if (hlEnd < lineText.Length)
                {
                    var postX = hlX + hlWidth;
                    DrawTextClipped(spriteBatch, new Vector2(postX, lineY), lineText[hlEnd..], TextElement.Color, ColorCodesEnabled);
                }
            } else if (lineText.Length > 0)
                DrawTextClipped(spriteBatch, new Vector2(innerX, lineY), lineText, TextElement.Color, ColorCodesEnabled);

            charOffset = lineEndIdx;
        }
    }

    private string PlainText => TextElement.Text;

    private void Invalidate(string text, Color color)
    {
        //clear selection when text content changes
        if (text != TextElement.Text)
        {
            CursorPosition = 0;
            SelectionAnchor = 0;
        }

        TextElement.WrapWidth = WordWrap ? Width - PaddingLeft - PaddingRight : 0;
        TextElement.ShadowStyle = ShadowStyle;
        TextElement.Update(text, color);
    }

    public override void ResetInteractionState()
    {
        Dragging = false;
        ClickCount = 0;
        CursorPosition = 0;
        SelectionAnchor = 0;
    }

    private void MoveCursor(int newPosition, bool extendSelection)
    {
        if (extendSelection && !HasSelection)
            SelectionAnchor = CursorPosition;

        CursorPosition = Math.Clamp(SnapPastColorCode(newPosition), 0, PlainText.Length);

        if (!extendSelection)
            SelectionAnchor = CursorPosition;
    }

    private int FindWordBoundaryLeft(int from)
    {
        if (from <= 0)
            return 0;

        var text = PlainText;
        var i = from - 1;

        while ((i > 0) && (text[i] == ' '))
            i--;

        while ((i > 0) && (text[i - 1] != ' '))
            i--;

        return SnapPastColorCode(i);
    }

    private int FindWordBoundaryRight(int from)
    {
        var text = PlainText;

        if (from >= text.Length)
            return text.Length;

        var i = from;

        while ((i < text.Length) && (text[i] != ' '))
            i++;

        while ((i < text.Length) && (text[i] == ' '))
            i++;

        return SnapPastColorCode(i);
    }

    /// <summary>
    ///     If position lands inside a {=x} color code, snap forward past it.
    /// </summary>
    private int SnapPastColorCode(int position)
    {
        var text = PlainText;

        if (!ColorCodesEnabled || (position <= 0) || (position >= text.Length))
            return position;

        if ((position >= 2) && TextRenderer.IsColorCode(text, position - 2))
            return Math.Min(position + 1, text.Length);

        if (((position + 1) < text.Length) && TextRenderer.IsColorCode(text, position - 1))
            return Math.Min(position + 2, text.Length);

        return position;
    }

    /// <summary>
    ///     Snaps a selection boundary to the start of a color code if position lands inside one.
    /// </summary>
    private int SnapSelectionBoundary(int position)
    {
        var text = PlainText;

        if (!ColorCodesEnabled || (position <= 0) || (position >= text.Length))
            return position;

        if ((position >= 2) && TextRenderer.IsColorCode(text, position - 2))
            return position - 2;

        if (((position + 1) < text.Length) && TextRenderer.IsColorCode(text, position - 1))
            return position - 1;

        return position;
    }

    /// <summary>
    ///     Moves position left, skipping entirely over any {=x} color code.
    /// </summary>
    private int StepLeft(int position)
    {
        if (position <= 0)
            return 0;

        var text = PlainText;

        if (ColorCodesEnabled && (position >= 3) && TextRenderer.IsColorCode(text, position - 3))
            return position - 3;

        return position - 1;
    }

    /// <summary>
    ///     Moves position right, skipping entirely over any {=x} color code.
    /// </summary>
    private int StepRight(int position)
    {
        var text = PlainText;

        if (position >= text.Length)
            return text.Length;

        if (ColorCodesEnabled && TextRenderer.IsColorCode(text, position))
            return Math.Min(position + 3, text.Length);

        return position + 1;
    }

    /// <summary>
    ///     Strips {=x} color codes from text for clipboard operations.
    /// </summary>
    private static string StripColorCodes(string text)
    {
        if (string.IsNullOrEmpty(text))
            return text;

        var sb = new System.Text.StringBuilder(text.Length);

        for (var i = 0; i < text.Length; i++)
        {
            if (TextRenderer.IsColorCode(text, i))
            {
                i += 2;

                continue;
            }

            sb.Append(text[i]);
        }

        return sb.ToString();
    }

    private int HitTestSingleLine(int mouseX)
    {
        var innerX = ScreenX + PaddingLeft;
        var innerW = Width - PaddingLeft - PaddingRight;

        var drawX = HorizontalAlignment switch
        {
            HorizontalAlignment.Center => innerX + (innerW - TextElement.Width) / 2,
            HorizontalAlignment.Right  => innerX + innerW - TextElement.Width,
            _                    => innerX
        };

        var localX = mouseX - drawX;
        var text = PlainText;

        if ((text.Length == 0) || (localX <= 0))
            return 0;

        var prevWidth = 0;

        for (var i = 0; i < text.Length;)
        {
            //skip color codes as atomic units
            if (ColorCodesEnabled && TextRenderer.IsColorCode(text, i))
            {
                i += 3;

                continue;
            }

            var nextI = i + 1;
            var charWidth = prevWidth + TextRenderer.MeasureCharWidth(text[i]);
            var midpoint = (prevWidth + charWidth) / 2;

            if (localX < midpoint)
                return i;

            prevWidth = charWidth;
            i = nextI;
        }

        return text.Length;
    }

    private int HitTestWrapped(int mouseX, int mouseY)
    {
        var lines = TextElement.WrappedLines;

        if (lines is null || (lines.Count == 0))
            return 0;

        var innerY = ScreenY + PaddingTop;
        var firstLine = ScrollOffset / TextRenderer.CHAR_HEIGHT;
        var localY = mouseY - innerY;
        var clickLine = firstLine + localY / TextRenderer.CHAR_HEIGHT;
        clickLine = Math.Clamp(clickLine, 0, lines.Count - 1);

        var charOffset = 0;

        for (var i = 0; i < clickLine; i++)
            charOffset += lines[i].Length;

        var lineText = lines[clickLine];
        var localX = mouseX - ScreenX - PaddingLeft;

        if ((lineText.Length == 0) || (localX <= 0))
            return charOffset;

        var prevWidth = 0;

        for (var i = 0; i < lineText.Length;)
        {
            //skip color codes as atomic units
            if (ColorCodesEnabled && TextRenderer.IsColorCode(lineText, i))
            {
                i += 3;

                continue;
            }

            var nextI = i + 1;
            var charWidth = prevWidth + TextRenderer.MeasureCharWidth(lineText[i]);
            var midpoint = (prevWidth + charWidth) / 2;

            if (localX < midpoint)
                return charOffset + i;

            prevWidth = charWidth;
            i = nextI;
        }

        return charOffset + lineText.Length;
    }

    private int GetWrappedLineForPosition(int position)
    {
        var lines = TextElement.WrappedLines;

        if (lines is null)
            return 0;

        var charOffset = 0;

        for (var i = 0; i < lines.Count; i++)
        {
            if (position < (charOffset + lines[i].Length))
                return i;

            charOffset += lines[i].Length;
        }

        return Math.Max(0, lines.Count - 1);
    }

    private int GetWrappedLineStart(int lineIndex)
    {
        var lines = TextElement.WrappedLines;

        if (lines is null)
            return 0;

        var offset = 0;

        for (var i = 0; i < lineIndex; i++)
            offset += lines[i].Length;

        return offset;
    }

    private void ClampPositions()
    {
        var len = PlainText.Length;

        if (CursorPosition > len)
            CursorPosition = len;

        if (SelectionAnchor > len)
            SelectionAnchor = len;
    }

    public override void OnMouseDown(MouseDownEvent e)
    {
        if (!IsSelectable || (e.Button != MouseButton.Left))
            return;

        ClampPositions();

        var isWrapped = TextElement.WrappedLines is not null;

        var clickPos = isWrapped
            ? HitTestWrapped(e.ScreenX, e.ScreenY)
            : HitTestSingleLine(e.ScreenX);

        var now = Environment.TickCount64;

        if (((now - LastClickTime) < 400) && (clickPos == LastClickPosition))
            ClickCount++;
        else
            ClickCount = 1;

        LastClickTime = now;
        LastClickPosition = clickPos;

        if (ClickCount == 3)
        {
            if (isWrapped)
            {
                var line = GetWrappedLineForPosition(clickPos);
                SelectionAnchor = GetWrappedLineStart(line);
                CursorPosition = SelectionAnchor + TextElement.WrappedLines![line].Length;
            } else
            {
                SelectionAnchor = 0;
                CursorPosition = PlainText.Length;
            }

            ClickCount = 0;
        } else if (ClickCount == 2)
        {
            SelectionAnchor = FindWordBoundaryLeft(clickPos);
            CursorPosition = FindWordBoundaryRight(clickPos);
        } else if (e.Shift)
            CursorPosition = clickPos;
        else
        {
            CursorPosition = clickPos;
            SelectionAnchor = clickPos;
        }

        Dragging = true;
        e.Handled = true;
    }

    public override void OnMouseMove(MouseMoveEvent e)
    {
        if (!IsSelectable || !Dragging)
            return;

        var isWrapped = TextElement.WrappedLines is not null;

        var dragPos = isWrapped
            ? HitTestWrapped(e.ScreenX, e.ScreenY)
            : HitTestSingleLine(e.ScreenX);

        CursorPosition = dragPos;
    }

    public override void OnMouseUp(MouseUpEvent e)
    {
        if (e.Button == MouseButton.Left)
            Dragging = false;
    }

    public override void OnKeyDown(KeyDownEvent e)
    {
        if (!IsSelectable)
            return;

        ClampPositions();

        var shift = e.Shift;
        var ctrl = e.Ctrl;
        var isWrapped = TextElement.WrappedLines is not null;

        switch (e.Key)
        {
            case Keys.Left:
                if (!shift && HasSelection)
                    MoveCursor(SelectionStart, false);
                else if (CursorPosition > 0)
                    MoveCursor(ctrl ? FindWordBoundaryLeft(CursorPosition) : StepLeft(CursorPosition), shift);

                e.Handled = true;

                break;

            case Keys.Right:
                if (!shift && HasSelection)
                    MoveCursor(SelectionEnd, false);
                else if (CursorPosition < PlainText.Length)
                    MoveCursor(ctrl ? FindWordBoundaryRight(CursorPosition) : StepRight(CursorPosition), shift);

                e.Handled = true;

                break;

            case Keys.Up when isWrapped:
            {
                var line = GetWrappedLineForPosition(CursorPosition);

                if (line > 0)
                {
                    var lineStart = GetWrappedLineStart(line);
                    var col = CursorPosition - lineStart;
                    var prevLineStart = GetWrappedLineStart(line - 1);
                    var prevLineLen = TextElement.WrappedLines![line - 1].Length;
                    MoveCursor(prevLineStart + Math.Min(col, prevLineLen), shift);
                }

                e.Handled = true;

                break;
            }

            case Keys.Down when isWrapped:
            {
                var lines = TextElement.WrappedLines!;
                var line = GetWrappedLineForPosition(CursorPosition);

                if ((line + 1) < lines.Count)
                {
                    var lineStart = GetWrappedLineStart(line);
                    var col = CursorPosition - lineStart;
                    var nextLineStart = GetWrappedLineStart(line + 1);
                    var nextLineLen = lines[line + 1].Length;
                    MoveCursor(nextLineStart + Math.Min(col, nextLineLen), shift);
                }

                e.Handled = true;

                break;
            }

            case Keys.Home:
                if (isWrapped && !ctrl)
                {
                    var line = GetWrappedLineForPosition(CursorPosition);
                    MoveCursor(GetWrappedLineStart(line), shift);
                } else
                    MoveCursor(0, shift);

                e.Handled = true;

                break;

            case Keys.End:
                if (isWrapped && !ctrl)
                {
                    var line = GetWrappedLineForPosition(CursorPosition);
                    MoveCursor(GetWrappedLineStart(line) + TextElement.WrappedLines![line].Length, shift);
                } else
                    MoveCursor(PlainText.Length, shift);

                e.Handled = true;

                break;

            case Keys.A when ctrl && (PlainText.Length > 0):
                SelectionAnchor = 0;
                CursorPosition = PlainText.Length;
                e.Handled = true;

                break;

            case Keys.C when ctrl && HasSelection:
                Clipboard.SetText(StripColorCodes(PlainText[SelectionStart..Math.Min(SelectionEnd, PlainText.Length)]));
                e.Handled = true;

                break;
        }
    }
}