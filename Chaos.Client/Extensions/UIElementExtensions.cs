#region
using Chaos.Client.Controls.Components;
using Microsoft.Xna.Framework;
#endregion

namespace Chaos.Client.Extensions;

public static class UIElementExtensions
{
    private static readonly Rectangle ScreenRect = new(
        0,
        0,
        ChaosGame.VIRTUAL_WIDTH,
        ChaosGame.VIRTUAL_HEIGHT);

    extension(UIElement element)
    {
        public void AlignBottomIn(Rectangle rect, int padding = 0) => element.Y = rect.AlignBottom(element.Height, padding);

        public void AlignLeftIn(Rectangle rect, int padding = 0) => element.X = rect.AlignLeft(padding);

        public void AlignRightIn(Rectangle rect, int padding = 0) => element.X = rect.AlignRight(element.Width, padding);

        public void AlignTopIn(Rectangle rect, int padding = 0) => element.Y = rect.AlignTop(padding);

        public void CenterHorizontallyIn(Rectangle rect) => element.X = rect.CenterX(element.Width);

        public void CenterHorizontallyOnScreen() => element.X = ScreenRect.CenterX(element.Width);

        public void CenterIn(Rectangle rect)
        {
            element.X = rect.CenterX(element.Width);
            element.Y = rect.CenterY(element.Height);
        }

        public void CenterOnScreen() => element.CenterIn(ScreenRect);

        //centers on the full NATIVE window (the new UI's coordinate space). Use this for world popups so they sit in the
        //middle of the actual window, not the top-left 640x480 region that CenterOnScreen targets. Read live (resizes).
        public void CenterOnUi() => element.CenterIn(new Rectangle(0, 0, ChaosGame.UiWidth, ChaosGame.UiHeight));
        public void CenterOnUiNearMouse(float strength = 0.5f)
        {
            element.X = Math.Clamp((int)MathF.Round((ChaosGame.UiWidth/2 * (1.0f - strength) + InputBuffer.MouseX * strength) - element.Width/2), 0, ChaosGame.UiWidth - element.Width);
            element.Y = Math.Clamp((int)MathF.Round((ChaosGame.UiHeight/2 * (1.0f - strength) + InputBuffer.MouseY * strength) - element.Height/2), 0, ChaosGame.UiHeight - element.Height);
        }

        public void CenterVerticallyIn(Rectangle rect) => element.Y = rect.CenterY(element.Height);

        public void CenterVerticallyOnScreen() => element.Y = ScreenRect.CenterY(element.Height);

        //keeps a draggable window reachable by pulling it back so at least HALF of it stays inside the live UI window
        //on EACH axis (X and Y handled independently). Called every visible frame on the new menu/book windows, so it
        //rescues a window not just from being dragged too far but also from being stranded when the game window is
        //resized smaller under it. The UI window can be any size; values are read live from ChaosGame.UiWidth/Height.
        //A window grabbed by a titlebar at the top passes its titlebar height: that variant additionally pins the top
        //edge on-screen (Y >= 0), because a titlebar dragged above the top can never be grabbed to pull it back down.
        public void KeepOnScreen(int titleBarHeight = 0)
        {
            var viewW = ChaosGame.UiWidth;
            var viewH = ChaosGame.UiHeight;
            var halfW = element.Width / 2;
            var halfH = element.Height / 2;

            element.X = Math.Clamp(element.X, -halfW, viewW - halfW);

            element.Y = titleBarHeight > 0
                ? Math.Clamp(element.Y, 0, Math.Max(0, viewH - halfH)) //titlebar window: top edge must stay on-screen
                : Math.Clamp(element.Y, -halfH, viewH - halfH);        //plain window: half off either edge is fine
        }
    }
}