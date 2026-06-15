#region
using Chaos.Client.Controls.Components;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
#endregion

namespace Chaos.Client.Controls.LobbyLogin;

/// <summary>
///     A lobby <see cref="PrefabPanel" /> that opens by sliding up a little and fading in, and closes by simply fading
///     out with no slide, the same feel as the in-world sign popups. The panel art is faded by rendering the whole panel
///     into a shared 640x480 target and blitting its rect tinted by the current alpha (children, not just the background,
///     so buttons and fields fade too). Its crisp native-resolution TTF text is suppressed in place and painted by the
///     lobby's <c>DrawNativeUi</c> pass, which reads <see cref="OpenFraction" /> to fade the text in lockstep.
/// </summary>
public abstract class SlideFadePanel : PrefabPanel
{
    //~0.22s open/close, snappy but visible
    //the dialog slides up SLIDE_DISTANCE into its resting place while fading in, closing only fades with no slide
    private const float FADE_SECONDS = 0.22f;
    private const float SLIDE_DISTANCE = 24f;

    //one transient target shared across the lobby dialogs (only allocated/used while a dialog is mid-fade)
    private static RenderTarget2D? SharedFadeTarget;

    private float OpenAlpha;
    private float OpenTarget;

    //0..1 eased slide, advances only while opening so the close is a pure fade
    private float SlideProgress;

    //true while drawing through a fade-out before the panel is truly hidden
    private bool Lingering;

    private int BaseY;
    private bool BaseYCaptured;

    /// <summary>Current animated opacity (0..1). The lobby's native-text pass reads this to fade the dialog's TTF text in
    ///     lockstep with its art.</summary>
    public float OpenFraction => OpenAlpha;

    protected SlideFadePanel(string prefabName)
        : base(prefabName) { }

    /// <summary>Shifts a text field down 1px and trims 2px off its height, shared across the lobby dialogs.</summary>
    protected static void NudgeField(UITextBox? field)
    {
        if (field is null)
            return;

        field.Y += 1;
        field.Height -= 2;
    }

    public override void Show()
    {
        CaptureBaseY();

        //fresh open: start transparent + below the resting spot, then ease up and in
        OpenAlpha = 0f;
        OpenTarget = 1f;
        SlideProgress = 0f;
        Lingering = false;

        base.Show(); //pushes the control stack + Visible = true
    }

    public override void Hide()
    {
        //begin a fade-out and keep drawing until transparent
        //remove from the control stack now so keyboard input returns to the lobby immediately
        //the real Visible=false happens once the fade completes in Update
        if (Visible && (OpenAlpha > 0.02f))
        {
            if (UsesControlStack)
                InputDispatcher.Instance?.RemoveControl(this);

            OpenTarget = 0f;
            Lingering = true;

            return;
        }

        base.Hide();
    }

    private void CaptureBaseY()
    {
        if (BaseYCaptured)
            return;

        BaseY = Y;
        BaseYCaptured = true;
    }

    public override void Update(GameTime gameTime)
    {
        base.Update(gameTime);

        CaptureBaseY();

        var step = (float)gameTime.ElapsedGameTime.TotalSeconds / FADE_SECONDS;

        if (OpenAlpha < OpenTarget)
            OpenAlpha = Math.Min(OpenTarget, OpenAlpha + step);
        else if (OpenAlpha > OpenTarget)
            OpenAlpha = Math.Max(OpenTarget, OpenAlpha - step);

        //the slide only runs while opening; on close it stays at rest (1) so the dialog simply fades, no downward slide
        if ((OpenTarget >= 1f) && (SlideProgress < 1f))
            SlideProgress = Math.Min(1f, SlideProgress + step);

        var ease = EaseOutCubic(SlideProgress);
        Y = BaseY + (int)MathF.Round((1f - ease) * SLIDE_DISTANCE);

        if (Lingering && (OpenAlpha <= 0.01f))
        {
            Lingering = false;
            Y = BaseY;
            base.Hide(); //fully gone now, the control stack was already cleared in Hide
        }
    }

    private static float EaseOutCubic(float t)
    {
        var u = 1f - t;

        return 1f - (u * u * u);
    }

    public override void Draw(SpriteBatch spriteBatch)
    {
        if (!Visible)
            return;

        //settled fully open, draw normally at zero cost
        if (OpenAlpha >= 0.999f)
        {
            base.Draw(spriteBatch);

            return;
        }

        //fully transparent (a fade-out that reached zero before Update hides it next frame), nothing to draw
        if (OpenAlpha <= 0.01f)
            return;

        DrawFaded(spriteBatch, OpenAlpha);
    }

    //renders the whole panel (background and every child) into a shared 640x480 target at its normal coords
    //then blits its rect tinted by alpha
    //native text is suppressed in place so the target holds only the art, the lobby's native text pass paints it at the matching alpha
    private void DrawFaded(SpriteBatch spriteBatch, float alpha)
    {
        var gd = spriteBatch.GraphicsDevice;
        var tw = ChaosGame.VIRTUAL_WIDTH;
        var th = ChaosGame.VIRTUAL_HEIGHT;

        if ((SharedFadeTarget is null) || (SharedFadeTarget.Width != tw) || (SharedFadeTarget.Height != th))
        {
            SharedFadeTarget?.Dispose();
            SharedFadeTarget = new RenderTarget2D(gd, tw, th, false, SurfaceFormat.Color, DepthFormat.None);
        }

        var prevTargets = gd.GetRenderTargets();
        spriteBatch.End();

        gd.SetRenderTarget(SharedFadeTarget);
        gd.Clear(Color.Transparent);
        spriteBatch.Begin(samplerState: GlobalSettings.Sampler);
        base.Draw(spriteBatch);
        spriteBatch.End();

        if (prevTargets.Length == 0)
            gd.SetRenderTarget(null);
        else
            gd.SetRenderTargets(prevTargets);

        var rect = new Rectangle(ScreenX, ScreenY, Width, Height);
        spriteBatch.Begin(samplerState: GlobalSettings.Sampler);
        spriteBatch.Draw(SharedFadeTarget, rect, rect, Color.White * alpha);
        spriteBatch.End();

        //restore the plain UI batch the caller keeps drawing the rest of the Root tree into
        spriteBatch.Begin(samplerState: GlobalSettings.Sampler);
    }
}
