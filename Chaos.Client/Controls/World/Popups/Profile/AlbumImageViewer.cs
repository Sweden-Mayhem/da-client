#region
using Chaos.Client.Controls.Components;
using Chaos.Client.Controls.World.Menu;
using Chaos.Client.Rendering;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
#endregion

namespace Chaos.Client.Controls.World.Popups.Profile;

/// <summary>
///     A screen-space modal that shows one album screenshot at NATIVE resolution, centered on the screen, with
///     Delete/Back centered directly beneath it. It lives at the WorldScreen root (NOT inside the magnified profile
///     book), so nothing about its layout relates to where the book sits. Extends <see cref="InputBlocker" /> so, while
///     open, it veils and blocks the game behind it (it just paints its own picture + buttons over that veil).
/// </summary>
public sealed class AlbumImageViewer : InputBlocker
{
    private const int GAP = 16;    //between the picture and the buttons
    private const int MARGIN = 24; //screen-edge breathing room

    private static readonly Color Veil = new(0, 0, 0, 242); //near-opaque so the book behind is fully obscured
    private static readonly Color Frame = new(40, 34, 24);

    private readonly MenuButton BackButton;
    private readonly MenuButton DeleteButton;
    private Texture2D? Image;
    private uint ImageId;

    /// <summary>Raised when Delete is pressed; the screen confirms, then deletes.</summary>
    public event Action<uint>? OnDelete;

    public AlbumImageViewer()
    {
        Name = "AlbumImageViewer";
        Visible = false;
        BackgroundColor = null; //the veil is drawn manually so it sits under the picture + buttons
        ZIndex = 150_000;       //above the book (ZIndex 2) but below the confirm dialog (160_001) so it shows on top

        DeleteButton = new MenuButton("Delete", 110, 26, new Color(214, 120, 110));
        DeleteButton.Clicked = _ => OnDelete?.Invoke(ImageId);

        BackButton = new MenuButton("Back", 110, 26);
        BackButton.Clicked = _ => Hide();

        AddChild(DeleteButton);
        AddChild(BackButton);
    }

    public void Show(uint id, Texture2D image)
    {
        ImageId = id;
        Image = image;
        Visible = true;
        Layout(); //so the buttons are positioned before the first hit-test
    }

    public void Hide()
    {
        Visible = false;
        Image = null;
    }

    //(veil clicks/scroll are swallowed by the InputBlocker base; the Delete/Back buttons, being deeper children, still
    //get theirs)

    //lays the whole modal out in screen space and returns the picture's screen rect
    private Rectangle Layout()
    {
        var sw = ChaosGame.UiWidth;
        var sh = ChaosGame.UiHeight;

        X = 0;
        Y = 0;
        Width = sw;
        Height = sh;

        if (Image is null)
            return Rectangle.Empty;

        //native 1:1, scaled DOWN only if it would not fit the screen (minus the margins + the button strip)
        var availW = sw - (MARGIN * 2);
        var availH = sh - (MARGIN * 2) - GAP - DeleteButton.Height;
        var fit = Math.Min(1f, Math.Min((float)availW / Image.Width, (float)availH / Image.Height));
        var iw = Math.Max(1, (int)(Image.Width * fit));
        var ih = Math.Max(1, (int)(Image.Height * fit));

        //center the picture + button row as one block
        var blockH = ih + GAP + DeleteButton.Height;
        var iy = (sh - blockH) / 2;
        var ix = (sw - iw) / 2;

        var rowW = DeleteButton.Width + 10 + BackButton.Width;
        var btnY = iy + ih + GAP;
        DeleteButton.X = (sw - rowW) / 2;
        DeleteButton.Y = btnY;
        BackButton.X = DeleteButton.X + DeleteButton.Width + 10;
        BackButton.Y = btnY;

        return new Rectangle(ix, iy, iw, ih);
    }

    public override void Draw(SpriteBatchEx spriteBatch)
    {
        if (!Visible)
            return;

        //if the picture was freed underneath us (e.g. deleted via another path), close instead of drawing it
        if ((Image is null) || Image.IsDisposed)
        {
            Hide();

            return;
        }

        var img = Layout();

        DrawRect(spriteBatch, new Rectangle(0, 0, Width, Height), Veil);
        DrawRect(spriteBatch, new Rectangle(img.X - 2, img.Y - 2, img.Width + 4, img.Height + 4), Frame);
        spriteBatch.Draw(Image, img, Color.White);

        base.Draw(spriteBatch); //the Delete/Back buttons on top
    }
}
