#region
using Chaos.Client.Controls.Components;
using Microsoft.Xna.Framework;
#endregion

namespace Chaos.Client.Controls.World.ViewPort;

/// <summary>
///     Chant text rendered as plain blue text above an entity's head. No bubble, no name prefix. Max 32 characters, 18
///     chars per visual line with character wrap (not word wrap). If total character count is 10 or less, text is centered
///     per line; otherwise left-aligned.
/// </summary>
public sealed class ChantText : UIPanel
{
    private const int CHARS_PER_LINE = 18;
    private const int LINE_HEIGHT = 12;
    private const int MAX_CHARS = 32;
    private const int CENTER_THRESHOLD = 10;
    private const float DISPLAY_DURATION_MS = 2000f;

    public static readonly Color ChantColor = new(100, 149, 237);

    private float ElapsedMs;

    public uint EntityId { get; }
    public bool IsExpired => ElapsedMs >= DISPLAY_DURATION_MS;

    /// <summary>0..1 lifetime progress, used to drift the lines upward and fade them out as they age.</summary>
    public float Progress => Math.Clamp(ElapsedMs / DISPLAY_DURATION_MS, 0f, 1f);

    /// <summary>The character-wrapped visual lines, painted in TTF at native resolution by
    ///     <see cref="EntityOverlayManager.DrawChantOverlaysNative" /> (not as bitmap child labels in the world pass).</summary>
    public IReadOnlyList<string> Lines { get; private set; } = [];

    /// <summary>When true the lines are centered on the entity; otherwise the block is left-aligned (long chants).</summary>
    public bool Centered { get; private set; }

    private ChantText(uint entityId, int width, int height)
    {
        EntityId = entityId;
        Width = width;
        Height = height;
    }

    public static ChantText Create(uint entityId, string message)
    {
        var text = message.Length > MAX_CHARS ? message[..MAX_CHARS] : message;
        var centered = text.Length <= CENTER_THRESHOLD;

        //character-wrap into visual lines
        var visualLines = new List<string>();
        var remaining = text;

        while (remaining.Length > 0)
        {
            if (remaining.Length <= CHARS_PER_LINE)
            {
                visualLines.Add(remaining);

                break;
            }

            visualLines.Add(remaining[..CHARS_PER_LINE]);
            remaining = remaining[CHARS_PER_LINE..];
        }

        if (visualLines.Count == 0)
            visualLines.Add(" ");

        var textAreaWidth = CHARS_PER_LINE * 6 + 2;
        var totalHeight = visualLines.Count * LINE_HEIGHT;

        //the text is painted natively from these lines (see EntityOverlayManager.DrawChantOverlaysNative); no bitmap
        //child labels are created. Width/Height are kept only as the world-space anchor box for positioning.
        return new ChantText(entityId, textAreaWidth, totalHeight)
        {
            Lines = visualLines,
            Centered = centered
        };
    }

    public override void Update(GameTime gameTime) => ElapsedMs += (float)gameTime.ElapsedGameTime.TotalMilliseconds;
}