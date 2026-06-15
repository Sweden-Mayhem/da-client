#region
using Chaos.Client.Controls.Components;
using Chaos.Client.Rendering.Utility;
using Chaos.Client.Systems;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
#endregion

namespace Chaos.Client.Controls.World.ViewPort;

/// <summary>
///     Chat bubble shown above a speaking character. Drawn at NATIVE resolution (in the UI pass, not the low-res world
///     target) so the text is pixel-perfect at any window size: a darker rounded-capsule background (baked once via
///     <see cref="ImageUtil.DrawBubbleBody" /> at native size) with a downward tail, and the message in the optional
///     TrueType (Cinzel) font. The font size and lifetime come from the Options "Bubble font size" / "Bubble fade after"
///     settings, read when the bubble is created. Position (X/Y, top-left) is set in native coordinates by
///     <see cref="Chaos.Client.Rendering.EntityOverlayManager" /> after converting the entity's world anchor to the
///     backbuffer.
/// </summary>
public sealed class ChatBubble : UIImage
{
    private const int BASE_FONT_SIZE = 14;   //native pixels at 1.0x (multiplied by ClientSettings.BubbleFontScale)
    private const int MAX_WIDTH = 240;       //native pixels the text wraps at
    private const int TEXT_PAD_X = 9;        //horizontal text inset (clears the rounded ends)
    private const int TEXT_PAD_Y = 4;
    private const int TAIL_HEIGHT = 4;
    private const float FADE_MS = 400f;      //how long the bubble takes to fade out at the end of its life
    private const float POP_MS = 220f;       //appear "boing": how long the spring-scale takes to settle to 1.0
    private const float POP_OVERSHOOT = 2.4f; //easeOutBack springiness - higher overshoots further past 1.0 (bigger boing)
    private const float HOVER_ALPHA_MUL = 0.5f; //when the cursor is over the bubble, halve its opacity to see behind it

    //darker than the old 33% black so the text reads better over busy ground; still see-through. White border like before.
    private static readonly Color BubbleFillColor = new(0, 0, 0, 170);
    private static readonly Color BubbleBorderColor = Color.White;
    private static readonly Color OutlineColor = new(0, 0, 0, 220);

    private static readonly Color NormalTextColor = LegendColors.White;
    private static readonly Color ShoutTextColor = TextColors.Shout;

    //the four 1px offsets used to emboss a dark outline around the text (scaled with the bubble's pop)
    private static readonly Vector2[] OutlineOffsets = [new(-1, 0), new(1, 0), new(0, -1), new(0, 1)];

    private readonly List<string> Lines;
    private readonly Color TextColor;
    private readonly int FontSize;
    private readonly int LineHeight;
    private readonly float DurationMs;

    private float ElapsedMs;

    public uint EntityId { get; }

    public bool IsExpired => ElapsedMs >= DurationMs;

    //kept for API compatibility with the overlay manager; the native tail always points down at the head below it
    public bool TailOnTop { get; set; }

    //set each frame by EntityOverlayManager when the cursor is over the bubble; halves the bubble's opacity so the
    //player can read what is behind it
    public bool Hovered { get; set; }

    private ChatBubble(
        uint entityId,
        Texture2D background,
        List<string> lines,
        Color textColor,
        int width,
        int height,
        int fontSize,
        int lineHeight,
        float durationMs)
    {
        EntityId = entityId;
        Texture = background;
        Lines = lines;
        TextColor = textColor;
        FontSize = fontSize;
        LineHeight = lineHeight;
        DurationMs = durationMs;
        Width = width;
        Height = height;
    }

    public static ChatBubble Create(uint entityId, string message, bool isShout)
    {
        var textColor = isShout ? ShoutTextColor : NormalTextColor;
        var fontSize = Math.Max(8, (int)MathF.Round(BASE_FONT_SIZE * ClientSettings.BubbleFontScale));

        //"Bubble fade after" in seconds (0 is handled upstream as "disabled" so it never reaches here)
        var durationMs = Math.Max(FADE_MS, ClientSettings.BubbleFadeSeconds * 1000f);

        //wrap by measured TrueType width (falls back to the whole string when no font is loaded)
        var lines = TtfTextRenderer.WrapText(message.Trim(), MAX_WIDTH, fontSize);

        if (lines.Count == 0)
            lines.Add(" ");

        var lineHeight = TtfTextRenderer.Available ? TtfTextRenderer.LineHeight(fontSize) : fontSize + 2;

        var textWidth = 0;

        foreach (var line in lines)
            textWidth = Math.Max(textWidth, TtfTextRenderer.MeasureWidth(line, fontSize));

        var width = textWidth + 2 * TEXT_PAD_X;
        var bodyHeight = Math.Max(18, 2 * TEXT_PAD_Y + lines.Count * lineHeight);
        var totalHeight = bodyHeight + TAIL_HEIGHT;

        //bake the rounded-capsule background + tail once, at native size
        using var scope = new PixelBufferScope(width, totalHeight);
        Array.Clear(scope.Pixels, 0, scope.Count);

        ImageUtil.DrawBubbleBody(scope.Pixels, width, 0, 0, width, bodyHeight, BubbleBorderColor, BubbleFillColor);
        ImageUtil.DrawBubbleTail(scope.Pixels, width, width / 2, bodyHeight - 1, BubbleBorderColor, BubbleFillColor);

        var texture = new Texture2D(ChaosGame.Device, width, totalHeight);
        scope.CommitTo(texture);

        return new ChatBubble(entityId, texture, lines, textColor, width, totalHeight, fontSize, lineHeight, durationMs);
    }

    //full opacity until the last FADE_MS, then eases to 0 so the bubble fades out instead of popping
    private float Alpha()
    {
        var left = DurationMs - ElapsedMs;

        return left >= FADE_MS ? 1f : Math.Clamp(left / FADE_MS, 0f, 1f);
    }

    //appear "boing": grows from 0 with an easeOutBack overshoot (springs past 1.0, then settles) over POP_MS,
    //then holds at 1.0 for the rest of the bubble's life. Scaled about the tail tip so it inflates off the head.
    private float PopScale()
    {
        if (ElapsedMs >= POP_MS)
            return 1f;

        var t = ElapsedMs / POP_MS;                 //0..1
        var c1 = POP_OVERSHOOT;
        var c3 = c1 + 1f;
        var p = t - 1f;

        return 1f + c3 * p * p * p + c1 * p * p;    //easeOutBack: 0 at t=0, overshoots, 1 at t=1
    }

    public override void Draw(SpriteBatch spriteBatch)
    {
        if (!Visible || Texture is null)
            return;

        var x = ScreenX;
        var y = ScreenY;
        var alpha = Alpha();

        if (Hovered)
            alpha *= HOVER_ALPHA_MUL;

        var pop = PopScale();

        //the tail tip (bottom-center of the bubble) sits on the head; scale the whole bubble about it so it
        //inflates upward off the head instead of growing from a corner
        var pivot = new Vector2(x + Width / 2f, y + Height);

        //rounded background + tail (baked native texture), faded near end of life, scaled by the appear pop
        spriteBatch.Draw(
            Texture,
            pivot,
            null,
            Color.White * alpha,
            0f,
            new Vector2(Width / 2f, Height), //texture-space bottom-center = the tail tip
            pop,
            SpriteEffects.None,
            0f);

        //text lines, centered, with a 1px dark outline so they stay readable over any background
        var textY = y + TEXT_PAD_Y;
        var textColor = TextColor * alpha;
        var outline = OutlineColor * alpha;

        foreach (var line in Lines)
        {
            var glyphs = TtfTextRenderer.GetLine(line, FontSize);

            if (glyphs is not null)
            {
                var lineX = x + (Width - glyphs.Width) / 2f;
                var basePos = new Vector2(lineX, textY);
                var pos = pivot + (basePos - pivot) * pop; //scale the line position about the same tail-tip pivot

                DrawGlyphLine(spriteBatch, glyphs, pos, pop, outline, textColor);
            }

            textY += LineHeight;
        }
    }

    //draws one glyph line scaled by <paramref name="scale" /> with a 1px (scaled) dark outline behind the fill
    private static void DrawGlyphLine(SpriteBatch spriteBatch, Texture2D glyphs, Vector2 pos, float scale, Color outline, Color fill)
    {
        foreach (var off in OutlineOffsets)
            spriteBatch.Draw(glyphs, pos + off * scale, null, outline, 0f, Vector2.Zero, scale, SpriteEffects.None, 0f);

        spriteBatch.Draw(glyphs, pos, null, fill, 0f, Vector2.Zero, scale, SpriteEffects.None, 0f);
    }

    public override void Update(GameTime gameTime) => ElapsedMs += (float)gameTime.ElapsedGameTime.TotalMilliseconds;
}
