#region
using Chaos.Client.Rendering.Utility;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
#endregion

namespace Chaos.Client.Utilities;

/// <summary>
///     Switches the OS mouse cursor between the normal arrow and a hand pointer. Used on screens that keep the OS cursor
///     visible (the lobby), where the in-game hand-cursor texture isn't drawn - e.g. hovering an https:// link in the
///     login notice. In-world screens draw their own hand-cursor texture instead (see ChaosGame.UseHandCursor).
/// </summary>
public static class OsCursor
{
    private static bool HandActive;
    private static int TextureCursorScale = 1;
    private static MouseCursor[] ArrowCursor = new MouseCursor[2]{MouseCursor.Arrow, MouseCursor.Arrow};
    private static MouseCursor[] HandCursor = new MouseCursor[2]{MouseCursor.Hand, MouseCursor.Hand};

    /// <summary>Show the hand pointer (true) or the normal arrow (false). Only calls into MonoGame on a state change.</summary>
    public static void SetHand(bool hand)
    {
        if (hand == HandActive)
            return;

        HandActive = hand;
        RefreshCursor();
    }

    public static void SetTexturedCursorScale(int scale)
    {
        var newScale = Math.Clamp(scale, 1, 2);
        if (TextureCursorScale == newScale)
            return;

        TextureCursorScale = newScale;
        RefreshCursor();
    }

    private static void RefreshCursor()
    {
        Mouse.SetCursor(HandActive ? HandCursor[TextureCursorScale-1] : ArrowCursor[TextureCursorScale-1]);
    }

    public static void SetArrowCursorTexture(Texture2D texture, int offsetX, int offsetY)
    {
        ArrowCursor[0] = MouseCursor.FromTexture2D(texture, offsetX, offsetY);

        using var scaledTexture = ImageUtil.BuildScaledTexture(ChaosGame.Device, texture, 2);
        ArrowCursor[1] = MouseCursor.FromTexture2D(scaledTexture, offsetX*2, offsetY*2);

        if (!HandActive)
            RefreshCursor();
    }

    public static void SetHandCursorTexture(Texture2D texture, int offsetX, int offsetY)
    {
        HandCursor[0] = MouseCursor.FromTexture2D(texture, offsetX, offsetY);

        using var scaledTexture = ImageUtil.BuildScaledTexture(ChaosGame.Device, texture, 2);
        HandCursor[1] = MouseCursor.FromTexture2D(scaledTexture, offsetX*2, offsetY*2);

        if (HandActive)
            RefreshCursor();
    }
}
