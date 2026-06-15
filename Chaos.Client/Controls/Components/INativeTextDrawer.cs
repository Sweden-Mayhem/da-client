#region
using Microsoft.Xna.Framework.Graphics;
#endregion

namespace Chaos.Client.Controls.Components;

/// <summary>
///     Implemented by controls that paint their own text in TTF at native resolution (instead of bitmap glyphs drawn
///     in-place and then upscaled by a magnifying ScaleHost or the letterbox-upscaled lobby). The enclosing native-text
///     root's pass runs this on every such control it walks. A descendant maps a native point to the screen as
///     <c>screenX = screenOriginX + (control.ScreenX - nativeOriginX) * scale</c>. For an in-world ScaleHost the two
///     origins are equal (its descendants' ScreenX are already in real screen space); for the lobby they differ (its
///     descendants' ScreenX are in 640-virtual space, screenOrigin is the letterbox rect's corner, nativeOrigin is 0).
///     Used by the slot grids (hotbar and book key hints plus stack counts) and by native text boxes.
/// </summary>
public interface INativeTextDrawer
{
    void DrawNativeText(SpriteBatch spriteBatch, int screenOriginX, int screenOriginY, int nativeOriginX, int nativeOriginY, float scale, float alpha);
}
