#region
using Chaos.Client.Controls.Components;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
#endregion

namespace Chaos.Client.Controls.World.Popups.Dialog;

/// <summary>
///     Darkens the rest of the game view while an NPC dialog is open, drawn BEHIND the dialog (just under its host's
///     ZIndex). Two layers, a uniform darken plus a radial VIGNETTE that deepens toward the screen edges, which reads more
///     like a focused spotlight than a flat grey wash. It has no timing of its own, it mirrors the dialog host's
///     <see cref="ScaleHost.OpenFraction" /> (the open/close fade only, NOT the content fade), so it eases in and out in
///     lockstep with the dialog opening/closing. Never takes input.
/// </summary>
public sealed class DialogDimmer : UIElement
{
    //uniform darken across the whole view, and the extra darkening added at the edges by the vignette
    private const float BASE_ALPHA = 0.55f;
    private const float VIGNETTE_ALPHA = 0.32f;

    private static Texture2D? VignetteTexture;
    private static Texture2D? SideFadeTexture;

    private readonly ScaleHost Dialog;

    //when true, the base darken + vignette are NOT drawn here, the world pass draws them (so it can re-draw the
    //spotlighted NPC speaker bright on top). The side-bars are still drawn natively (they key off the dialog's on-screen
    //position). Set per-frame by the world screen
    public bool SuppressBaseDim { get; set; }

    public DialogDimmer(ScaleHost dialog)
    {
        Dialog = dialog;
        IsHitTestVisible = false;
    }

    //draws the base uniform darken + radial vignette into <paramref name="rect" /> at <paramref name="fraction" />. Shared
    //so the world pass can render the dim (then a bright spotlighted speaker over it) using the same look, optionally
    //darker (the spotlight passes higher alphas, since the lit speaker on top can carry a deeper darken)
    public static void DrawBaseAndVignette(
        SpriteBatch spriteBatch,
        Rectangle rect,
        float fraction,
        float baseAlpha = BASE_ALPHA,
        float vignetteAlpha = VIGNETTE_ALPHA)
    {
        if (fraction <= 0f)
            return;

        RenderHelper.DrawRect(spriteBatch, rect, Color.Black * (baseAlpha * fraction));
        spriteBatch.Draw(EnsureVignette(spriteBatch.GraphicsDevice), rect, Color.White * (vignetteAlpha * fraction));
    }

    public override void Draw(SpriteBatch spriteBatch)
    {
        if (!Visible)
            return;

        //track only the OPEN/close fade, not the dialog's content-change ExternalAlpha (else the dim would flicker each
        //time the content changes during paging / opening the shop)
        var fraction = Dialog.OpenFraction;

        if (fraction <= 0f)
            return;

        var rect = new Rectangle(0, 0, ChaosGame.UiWidth, ChaosGame.UiHeight);

        //base uniform darken + radial vignette, UNLESS the world pass is drawing them (so it can put the spotlighted
        //speaker on top). The side-bars below are always native.
        if (!SuppressBaseDim)
            DrawBaseAndVignette(spriteBatch, rect, fraction);

        //SOLID black on the extended sides, all the way up to the dialog WINDOW, then (and only then) a short fade INTO
        //the centre at the dialog edge. Uses the dialog host's actual on-screen bounds, so the black hides the world on
        //the sides entirely and the only soft transition is right at the dialog window's left/right edge
        var dialogLeft = Dialog.ScreenX;
        var dialogRight = Dialog.ScreenX + Dialog.Width;
        const int FADE_W = 110;
        var black = Color.Black * fraction;
        var tint = Color.White * fraction;
        var sideTex = EnsureSideFade(spriteBatch.GraphicsDevice);

        if (dialogLeft > 0)
        {
            //solid black from the window edge to the dialog's left edge, then a fade from black to clear running into the centre
            RenderHelper.DrawRect(spriteBatch, new Rectangle(0, 0, dialogLeft, ChaosGame.UiHeight), black);
            spriteBatch.Draw(sideTex, new Rectangle(dialogLeft, 0, FADE_W, ChaosGame.UiHeight), tint);
        }

        if (dialogRight < ChaosGame.UiWidth)
        {
            RenderHelper.DrawRect(spriteBatch, new Rectangle(dialogRight, 0, ChaosGame.UiWidth - dialogRight, ChaosGame.UiHeight), black);

            //mirror the fade, clear toward the centre then black at the dialog's right edge
            spriteBatch.Draw(
                sideTex,
                new Rectangle(dialogRight - FADE_W, 0, FADE_W, ChaosGame.UiHeight),
                null,
                tint,
                0f,
                Vector2.Zero,
                SpriteEffects.FlipHorizontally,
                0f);
        }
    }

    //a black radial-gradient texture (transparent centre, opaque edges), built once. Stretched to the window each draw,
    //a square source becomes an ellipse on a wide screen, which is the usual vignette look. 512px so the point-sampled
    //UI batch still stretches it smoothly enough to avoid visible banding
    private static Texture2D EnsureVignette(GraphicsDevice device)
    {
        if (VignetteTexture is not null)
            return VignetteTexture;

        const int SIZE = 512;
        const float INNER = 0.45f; //fraction of the radius that stays clear before the darkening ramps in
        var pixels = new Color[SIZE * SIZE];
        var centre = (SIZE - 1) / 2f;
        var maxDist = MathF.Sqrt(2f) * centre; //centre to corner

        for (var y = 0; y < SIZE; y++)
            for (var x = 0; x < SIZE; x++)
            {
                var dx = x - centre;
                var dy = y - centre;
                var d = MathF.Sqrt(dx * dx + dy * dy) / maxDist; //0 centre to 1 corner

                var t = Math.Clamp((d - INNER) / (1f - INNER), 0f, 1f);
                t = t * t * (3f - 2f * t); //smoothstep

                pixels[y * SIZE + x] = new Color(0f, 0f, 0f, t); //premultiplied black at alpha t
            }

        VignetteTexture = new Texture2D(device, SIZE, SIZE);
        VignetteTexture.SetData(pixels);

        return VignetteTexture;
    }

    //a 1px-tall horizontal black to clear gradient (smoothstep), fully black at x=0, fully clear at x=W. Drawn as the short
    //transition right at the dialog window edge (the solid black beyond it is a separate filled rect)
    private static Texture2D EnsureSideFade(GraphicsDevice device)
    {
        if (SideFadeTexture is not null)
            return SideFadeTexture;

        const int W = 256;
        var pixels = new Color[W];

        for (var x = 0; x < W; x++)
        {
            var t = x / (float)(W - 1);             //0 = dialog edge (black), 1 = toward centre (clear)
            var a = 1f - (t * t * (3f - 2f * t));   //1 minus smoothstep, eased black to clear
            pixels[x] = new Color(0f, 0f, 0f, Math.Clamp(a, 0f, 1f));
        }

        SideFadeTexture = new Texture2D(device, W, 1);
        SideFadeTexture.SetData(pixels);

        return SideFadeTexture;
    }
}
