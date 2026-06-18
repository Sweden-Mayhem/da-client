#region
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
    private static MouseCursor ArrowCursor = MouseCursor.Arrow;
    private static MouseCursor HandCursor = MouseCursor.Hand;

    /// <summary>Show the hand pointer (true) or the normal arrow (false). Only calls into MonoGame on a state change.</summary>
    public static void SetHand(bool hand)
    {
        if (hand == HandActive)
            return;

        HandActive = hand;
        RefreshCursor();
    }

    private static void RefreshCursor()
    {
        Mouse.SetCursor(HandActive ? HandCursor : ArrowCursor);
    }

    public static void SetArrowCursorTexture(Texture2D texture, int offsetX, int offsetY)
    {
        ArrowCursor = MouseCursor.FromTexture2D(texture, offsetX, offsetY);
        if (!HandActive)
            RefreshCursor();
    }

    public static void SetHandCursorTexture(Texture2D texture, int offsetX, int offsetY)
    {
        HandCursor = MouseCursor.FromTexture2D(texture, offsetX, offsetY);
        if (HandActive)
            RefreshCursor();
    }
}
