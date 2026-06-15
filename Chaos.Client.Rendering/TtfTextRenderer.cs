#region
using Microsoft.Xna.Framework.Graphics;
using SkiaSharp;
#endregion

namespace Chaos.Client.Rendering;

/// <summary>
///     Which TrueType face to render with. <see cref="Main" /> is the everyday UI font (font.ttf, Cinzel);
///     <see cref="Fancy" /> is the decorative blackletter (font_fancy.ttf, UnifrakturMaguntia - the website logo
///     font) for headings like the town-map banner; <see cref="Sign" /> is the readable old-print serif
///     (font_sign.ttf, IM Fell English) for sign/board body text. Unloaded faces fall back to Main.
/// </summary>
public enum FontKind
{
    Main,
    Fancy,
    Sign
}

/// <summary>
///     Optional TrueType text path that sits alongside the retail bitmap font (<see cref="TextRenderer" />). Loads up
///     to three swappable faces (see <see cref="FontKind" />) and rasterizes one line of text at a time into a white,
///     premultiplied <see cref="Texture2D" /> via SkiaSharp, cached by (text, size, face). Callers tint at draw time
///     exactly like the bitmap atlas, so the glyphs blend identically under the UI's AlphaBlend pass. Labels opt in
///     via <c>UILabel.CustomFontSize</c>; everything that does not opt in stays on the bitmap font.
/// </summary>
public static class TtfTextRenderer
{
    private static readonly SKData?[] FontDatas = new SKData?[3];
    private static readonly SKTypeface?[] Typefaces = new SKTypeface?[3];
    private static readonly Dictionary<(string Text, int Size, FontKind Kind), Texture2D> LineCache = new();

    /// <summary>True once the main font has loaded. When false, opt-in labels fall back to the bitmap font.</summary>
    public static bool Available => Typefaces[(int)FontKind.Main] is not null;

    //the typeface a request resolves to: the asked-for face when loaded, else the main font
    private static SKTypeface? Resolve(FontKind kind)
        => Typefaces[(int)kind] ?? Typefaces[(int)FontKind.Main];

    /// <summary>
    ///     Loads the main font from raw bytes (embedded default or an external font.ttf override). Call once at
    ///     startup, after <see cref="TextureConverter.Device" /> is set.
    /// </summary>
    public static void Initialize(byte[] fontBytes) => InitializeFace(FontKind.Main, fontBytes);

    /// <summary>Loads the decorative blackletter face (embedded default or an external font_fancy.ttf override).</summary>
    public static void InitializeFancy(byte[] fontBytes) => InitializeFace(FontKind.Fancy, fontBytes);

    /// <summary>Loads the sign-body face (embedded default or an external font_sign.ttf override).</summary>
    public static void InitializeSign(byte[] fontBytes) => InitializeFace(FontKind.Sign, fontBytes);

    private static void InitializeFace(FontKind kind, byte[] fontBytes)
    {
        foreach (var texture in LineCache.Values)
            texture.Dispose();

        LineCache.Clear();

        var i = (int)kind;
        Typefaces[i]?.Dispose();
        FontDatas[i]?.Dispose();

        FontDatas[i] = SKData.CreateCopy(fontBytes);
        Typefaces[i] = SKTypeface.FromData(FontDatas[i]!);
    }

    /// <summary>Pixel line height for the loaded font at the given size (full ascent-to-descent box).</summary>
    public static int LineHeight(int size, FontKind kind = FontKind.Main)
    {
        if (Resolve(kind) is not { } typeface)
            return size;

        using var font = new SKFont(typeface, size);
        var metrics = font.Metrics;

        return (int)MathF.Ceiling(metrics.Descent - metrics.Ascent);
    }

    /// <summary>Advance width of the text at the given size. Zero when no font is loaded or the text is empty.</summary>
    public static int MeasureWidth(string text, int size, FontKind kind = FontKind.Main)
    {
        if ((Resolve(kind) is not { } typeface) || string.IsNullOrEmpty(text))
            return 0;

        using var font = new SKFont(typeface, size);

        //fast path: no trailing space, the advance is correct as-is
        if (text[^1] != ' ')
            return (int)MathF.Ceiling(font.MeasureText(text));

        //SkiaSharp's MeasureText drops TRAILING whitespace from the advance (it measures visible ink), which froze a
        //text-box caret when a space was typed at the end of a line until the next glyph followed. Measure the trimmed
        //text and add each trailing space's own advance back, so the caret tracks trailing spaces.
        var trimmed = text.TrimEnd(' ');
        var trailing = text.Length - trimmed.Length;
        var spaceAdv = font.MeasureText("i i") - font.MeasureText("ii");

        if (spaceAdv <= 0f)
            spaceAdv = size * 0.3f;

        var w = (trimmed.Length > 0 ? font.MeasureText(trimmed) : 0f) + (trailing * spaceAdv);

        return (int)MathF.Ceiling(w);
    }

    /// <summary>
    ///     Greedy word-wraps text to <paramref name="maxWidth" /> pixels at the given size, honoring existing newlines.
    ///     A single word wider than the limit is left on its own line (it overflows and clips). Always returns at least
    ///     one entry.
    /// </summary>
    public static List<string> WrapText(string text, int maxWidth, int size, FontKind kind = FontKind.Main)
    {
        var result = new List<string>();

        if ((Resolve(kind) is not { } typeface) || string.IsNullOrEmpty(text) || (maxWidth <= 0))
        {
            result.Add(text ?? string.Empty);

            return result;
        }

        using var font = new SKFont(typeface, size);

        foreach (var rawLine in text.Split('\n'))
        {
            if (rawLine.Length == 0)
            {
                result.Add(string.Empty);

                continue;
            }

            var current = string.Empty;

            foreach (var word in rawLine.Split(' '))
            {
                var candidate = current.Length == 0 ? word : current + " " + word;

                if ((current.Length == 0) || (font.MeasureText(candidate) <= maxWidth))
                    current = candidate;
                else
                {
                    result.Add(current);
                    current = word;
                }
            }

            result.Add(current);
        }

        return result;
    }

    /// <summary>
    ///     White, premultiplied line texture for the text at the given size, cached by (text, size, face). Returns null
    ///     when no font is loaded or the text is empty. The caller tints it at draw time.
    /// </summary>
    public static Texture2D? GetLine(string text, int size, FontKind kind = FontKind.Main)
    {
        if ((Resolve(kind) is not { } typeface) || string.IsNullOrEmpty(text))
            return null;

        //unloaded faces fall back to Main; cache under Main so the entry is shared
        var effectiveKind = Typefaces[(int)kind] is null ? FontKind.Main : kind;
        var key = (text, size, effectiveKind);

        if (LineCache.TryGetValue(key, out var cached))
            return cached;

        using var font = new SKFont(typeface, size);
        var advance = font.MeasureText(text, out var bounds);
        var metrics = font.Metrics;

        const int pad = 2;
        var baseline = (int)MathF.Ceiling(-metrics.Ascent);

        //size the bitmap from the BASELINE plus the real ink depth - the old ceil(descent - ascent) box could be a
        //pixel shorter than baseline + descent (two independent roundings), clipping descenders like 'g'. Some faces
        //also draw below their declared descent (swashes); bounds.Bottom catches those.
        var height = baseline + (int)MathF.Ceiling(MathF.Max(metrics.Descent, bounds.Bottom)) + 1;
        var width = (int)MathF.Ceiling(MathF.Max(advance, bounds.Right)) + (pad * 2);
        var originX = pad + (bounds.Left < 0 ? (int)MathF.Ceiling(-bounds.Left) : 0);

        var info = new SKImageInfo(Math.Max(1, width), Math.Max(1, height), SKColorType.Rgba8888, SKAlphaType.Premul);
        using var surface = SKSurface.Create(info);
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.Transparent);

        using var paint = new SKPaint
        {
            Color = SKColors.White,
            IsAntialias = true
        };
        canvas.DrawText(text, originX, baseline, font, paint);

        using var image = surface.Snapshot();
        var texture = TextureConverter.ToTexture2D(image);
        LineCache[key] = texture;

        return texture;
    }
}
