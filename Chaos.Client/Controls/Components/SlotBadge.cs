#region
using Chaos.Client.Rendering;
using Microsoft.Xna.Framework;
#endregion

namespace Chaos.Client.Controls.Components;

/// <summary>
///     Shared drawing for the small status marks painted in an item slot's corner during a menu's native-text pass, so
///     every slot menu (the inventory grid and the equipment paper-doll) renders them identically and crisply. The
///     caller maps the slot's lower-right corner to screen space (the same <c>MapX</c>/<c>MapY</c> it uses for stack
///     counts) and passes it here; the mark is drawn just inside that corner over a 1px black shadow.
/// </summary>
public static class SlotBadge
{
    //gold "?" on an un-appraised item; same base size as the inventory stack count so the two read as one family
    private const int FONT = 10;
    private static readonly Color UnidentifiedColor = new(255, 210, 70);

    /// <summary>Draws the gold "?" unidentified mark anchored at a slot's already-mapped lower-right screen corner.</summary>
    public static void DrawUnidentified(SpriteBatchEx spriteBatch, int rightX, int bottomY, float scale, float alpha)
    {
        if (!TtfTextRenderer.Available)
            return;

        var font = Math.Max(1, (int)MathF.Round(FONT * scale));
        var glyph = TtfTextRenderer.GetLine("?", font);

        if (glyph is null)
            return;

        var step = Math.Max(1, (int)MathF.Round(scale));
        var pos = new Vector2(rightX - glyph.Width - step, bottomY - glyph.Height - step);

        spriteBatch.Draw(glyph, pos + new Vector2(step, step), Color.Black * alpha); //shadow
        spriteBatch.Draw(glyph, pos, UnidentifiedColor * alpha);
    }
}
