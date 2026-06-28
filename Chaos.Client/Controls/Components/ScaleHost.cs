#region
using Chaos.Client.Definitions;
using Chaos.Client.Extensions;
using Chaos.Client.Rendering;
using Chaos.Client.Systems;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
#endregion

namespace Chaos.Client.Controls.Components;

/// <summary>
///     Hosts a single child and draws it magnified by an integer <see cref="Scale" /> (nearest-neighbour), so small
///     pixel-art panels read larger in the native-resolution UI. The child keeps its native coordinates: drawing is
///     scaled with a SpriteBatch transform anchored at this host's screen origin, and input is scaled back in
///     <c>InputDispatcher.HitTest</c> (it divides the cursor by <see cref="ContentScale" /> when descending into this
///     panel), so the magnified visuals and the click targets stay aligned.
/// </summary>
public sealed class ScaleHost : UIPanel, INativeTextRoot
{
    private readonly UIElement Inner;

    /// <summary>The wrapped (magnified) child, made available so WorldScreen's generic menu-text pass can walk it and paint its
    ///     RenderNative labels in crisp TTF at native resolution.</summary>
    public UIElement Content => Inner;

    /// <summary>
    ///     Magnification factor (>= 1). Setting it re-sizes the host to the scaled child, so callers can change it live
    ///     (e.g. from a settings slider) and the layout follows.
    /// </summary>
    public float Scale
    {
        get;
        set
        {
            field = value < 1f ? 1f : value;
            Width = (int)(Inner.Width * field);
            Height = (int)(Inner.Height * field);
        }
    }

    public override float ContentScale => Scale;

    /// <summary>
    ///     When true, the host can be dragged by clicking its background (any spot the inner control does not handle).
    ///     For this to work the inner control should be pass-through, so empty-space clicks fall through to this host.
    /// </summary>
    public bool Draggable { get; set; }

    /// <summary>When true, the host fades its content IN on show and OUT on close (a short alpha transition) instead of
    ///     popping. Set on the standalone menu windows; the hotbars and in-window content hosts leave it off.</summary>
    public bool Fades { get; set; }

    /// <summary>When <see cref="Fades" />, whether the open/close feedback SOUNDS play. Off for the NPC dialog (it has its
    ///     own dialog cue; we don't want the generic window sound doubled on top of it).</summary>
    public bool PlayFadeSounds { get; set; } = true;

    /// <summary>Seconds for the fade ramp (when <see cref="Fades" />). Defaults to the shared window speed; the NPC dialog
    ///     sets a slightly longer, more deliberate fade.</summary>
    public float FadeSeconds { get; set; } = FadeDurationSeconds;

    /// <summary>When true (and <see cref="Fades" />), the window also SLIDES up into place as it fades in (and back down as
    ///     it fades out), like the sign popup. A sub-menu SWITCH snaps the fade (FadeAlpha=1) so it does NOT slide.</summary>
    public bool SlideOnFade { get; set; }

    /// <summary>Slide distance in screen px for <see cref="SlideOnFade" /> (the window starts this far BELOW its spot).</summary>
    public float SlideDistance { get; set; } = 36f;

    /// <summary>0..1 current drawn opacity of the content (the fade alpha times the external multiplier). An overlay can
    ///     mirror this to dim the rest of the screen in lockstep with the window's fade.</summary>
    public float VisibleFraction => (Fades ? FadeAlpha : (Visible ? 1f : 0f)) * ExternalAlpha;

    /// <summary>0..1 OPEN/close fade fraction only (ignores <see cref="ExternalAlpha" />), so an overlay can track a
    ///     window's open/close without flickering when the owner uses ExternalAlpha for a separate content transition.</summary>
    public float OpenFraction => Fades ? FadeAlpha : (Visible ? 1f : 0f);

    /// <summary>True while the host is fading closed (FadeAlpha is dropping toward 0). False while opening, fully open,
    ///     or closed. Use this to distinguish a closing fade from an opening one when you need asymmetric behaviour.</summary>
    public bool IsFadingOut => Fades ? (FadeAlpha > FadeTarget) : !Visible;

    /// <summary>External 0..1 opacity multiplier, so an OWNER can fade this host's content along with the rest of a window
    ///     (e.g. a DraggableWindow fading its ScaleHost content in/out). 1 = fully opaque (default). Routed through the
    ///     sharp-bilinear target so it works even at an integer scale.</summary>
    public float ExternalAlpha { get; set; } = 1f;

    //fade transition state (only meaningful when Fades). FadeAlpha eases toward FadeTarget; Lingering means the inner has
    //hidden but we keep drawing the last captured frame, fading it out, before the host actually goes invisible.
    private float FadeAlpha;
    private float FadeTarget;
    private bool Lingering;
    private const float FadeDurationSeconds = 0.08f;

    //queued open/close feedback-sound triggers (only meaningful when Fades). Set when a real fade-in/out STARTS and
    //used once in Update. A grouped-window SWITCH retires the sibling and snaps the newcomer in via SnapHidden and
    //SnapShown, which clear these, so swapping board<->mail content plays no open/close sound, only a real open/close does.
    private bool PendingOpenSound;
    private bool PendingCloseSound;

    private bool Dragging;
    private int DragOffsetX;
    private int DragOffsetY;

    //sharp-bilinear scaling targets (only used at a NON-integer Scale - see Draw): the child rendered crisp at 1:1, and
    //that integer-upscaled to a multiple of the scaled size. Rebuilt only when their size changes.
    private RenderTarget2D? NativeTarget;
    private RenderTarget2D? ScaledTarget;

    //SHARPNESS knob for the fractional-scale path. The integer-upscaled image is supersampled to ~this many times the
    //final on-screen size before the bilinear downscale; the residual soft seam at each pixel step is ~1/this of an
    //output pixel, so a HIGHER value is sharper (closer to nearest) at the cost of a bigger intermediate target. 3 is
    //"sharp with a hint of anti-aliasing"; the upscaled image is piecewise-constant so the downscale never re-aliases.
    private const int SharpSupersample = 3;

    //hard cap on the intermediate target's long edge so a big panel at a high scale can't allocate an absurd texture
    //(well under any modern GPU's max texture size). The supersample is reduced to fit, never below a plain downscale.
    private const int MaxIntermediate = 4096;

    public ScaleHost(UIElement inner, float scale)
    {
        Inner = inner;
        inner.X = 0;
        inner.Y = 0;
        AddChild(inner);
        Scale = scale;

        //follow the inner control's visibility so a hidden dialog's host neither draws nor captures input.
        //A draggable host (the book/group) also rises to the top of the window stack each time it opens.
        Visible = inner.Visible;

        if (Visible)
            FadeAlpha = FadeTarget = 1f; //already on-screen at construction: no opening fade

        inner.VisibilityChanged += v =>
        {
            if (v)
            {
                Visible = true;
                Lingering = false;
                FadeTarget = 1f; //ease up from the current alpha (0 when fully hidden, or mid-fade if reopened quickly)

                if (Fades)
                {
                    PendingOpenSound = true; //a real open; a switch clears this again via SnapShown (below)
                    PendingCloseSound = false;
                }

                if (Draggable)
                    BringToFront();
            } else if (Fades && Visible && (FadeAlpha > 0.02f))
            {
                //fade OUT: keep the host visible and draw the last captured frame, easing alpha to 0; Update hides it for
                //real once it is transparent.
                Lingering = true;
                FadeTarget = 0f;
                PendingCloseSound = true; //a real close; a switch clears this again via SnapHidden (below)
                PendingOpenSound = false;
            } else
            {
                Visible = false;
                Lingering = false;
                FadeAlpha = 0f;
            }
        };
    }

    public override void OnMouseDown(MouseDownEvent e)
    {
        if (Draggable && (e.Button == MouseButton.Left))
        {
            //clicking a draggable menu background blurs any focused input box (chat or a menu field), the way
            //clicking off a text field anywhere else does.
            UITextBox.Blur();
            InputDispatcher.Instance?.ClearExplicitFocus();

            BringToFront();
            Dragging = true;
            DragOffsetX = e.ScreenX - X;
            DragOffsetY = e.ScreenY - Y;
        }

        e.Handled = true;
    }

    /// <summary>
    ///     A draggable menu only "occupies" the pixels its art actually paints: a click on the transparent margin around
    ///     the menu returns false here, so it falls through to whatever is behind instead of being eaten or starting a
    ///     drag. Non-draggable hosts (hotbars, inventory) keep the plain rectangular hit area.
    /// </summary>
    public override bool ContainsPoint(int screenX, int screenY)
    {
        if (!base.ContainsPoint(screenX, screenY))
            return false;

        if (!Draggable)
            return true;

        //map the screen point back into the child's native (un-scaled) coordinate space, then test whether any
        //visible panel background in the menu paints an opaque pixel there.
        var nativeX = ScreenX + (int)((screenX - ScreenX) / Scale);
        var nativeY = ScreenY + (int)((screenY - ScreenY) / Scale);

        return OpaqueAt(Inner, nativeX, nativeY);
    }

    private static bool OpaqueAt(UIElement element, int nativeX, int nativeY)
    {
        if (!element.Visible || !element.Enabled)
            return false;

        //anything that would itself be a hit target occupies its rect, so interactive controls (slots, buttons,
        //text boxes, scrollbars) stay reachable AND non-draggable even if the art behind them happens to be
        //transparent. Pass-through panels are skipped here: they never claim themselves, only their content does.
        if (element.IsHitTestVisible && element is not UIPanel { IsPassThrough: true } && element.ContainsPoint(nativeX, nativeY))
            return true;

        //opaque background art occupies the pixels it paints, covering decorative (non-hit-test) children drawn on top
        if (element is UIPanel { Background: { } background }
            && SampleOpaque(background, nativeX - element.ScreenX, nativeY - element.ScreenY))
            return true;

        if (element is UIPanel panel)
            foreach (var child in panel.Children)
                if (OpaqueAt(child, nativeX, nativeY))
                    return true;

        return false;
    }

    //one-bit alpha mask per background texture, built on first use. Keyed by the texture instance, so an atlas-backed
    //CachedTexture2D and a standalone texture are each cached once.
    private static readonly Dictionary<Texture2D, bool[]> AlphaMasks = [];

    private static bool SampleOpaque(Texture2D texture, int localX, int localY)
    {
        Texture2D source;
        Rectangle srcRect;

        if (texture is CachedTexture2D { AtlasRegion: { } region })
        {
            source = region.Atlas;
            srcRect = region.SourceRect;
        } else
        {
            source = texture;
            srcRect = new Rectangle(0, 0, texture.Width, texture.Height);
        }

        if ((localX < 0) || (localY < 0) || (localX >= srcRect.Width) || (localY >= srcRect.Height))
            return false;

        if (!AlphaMasks.TryGetValue(texture, out var mask))
        {
            var pixels = new Color[srcRect.Width * srcRect.Height];
            source.GetData(0, srcRect, pixels, 0, pixels.Length);
            mask = new bool[pixels.Length];

            for (var i = 0; i < pixels.Length; i++)
                mask[i] = pixels[i].A > 16;

            AlphaMasks[texture] = mask;
        }

        return mask[localY * srcRect.Width + localX];
    }

    public override void Update(GameTime gameTime)
    {
        base.Update(gameTime);

        //ease the open/close fade and, once a fade-out has finished, actually hide the host
        if (Fades)
        {
            var step = (float)gameTime.ElapsedGameTime.TotalSeconds / FadeSeconds;

            if (FadeAlpha < FadeTarget)
                FadeAlpha = Math.Min(FadeTarget, FadeAlpha + step);
            else if (FadeAlpha > FadeTarget)
                FadeAlpha = Math.Max(FadeTarget, FadeAlpha - step);

            if (Lingering && (FadeAlpha <= 0.01f))
            {
                Lingering = false;
                Visible = false;
                FadeAlpha = 0f;
            }

            //play the open/close feedback sound exactly once when a real fade starts (a switch already cleared these)
            if (PendingOpenSound)
            {
                PendingOpenSound = false;

                if (PlayFadeSounds)
                    SoundSystem.PlayWindowOpen();
            }

            if (PendingCloseSound)
            {
                PendingCloseSound = false;

                if (PlayFadeSounds)
                    SoundSystem.PlayWindowClose();
            }
        }

        //only the draggable standalone hosts (the profile book, the group window) own their on-screen position; the
        //rest (hotbars, the inventory grid inside a titled window, the social-status picker) are placed by their owners.
        if (!Draggable)
            return;

        if (Dragging)
        {
            if (!InputBuffer.IsLeftButtonHeld)
                Dragging = false;
            else
            {
                X = InputBuffer.MouseX - DragOffsetX;
                Y = InputBuffer.MouseY - DragOffsetY;
            }
        }

        //keep at least half the window on each axis (X and Y independent) so it can never be stranded, whether by a
        //drag or by the game window being resized smaller under it. It has no titlebar (it is dragged by its
        //background), so half-visible is enough to re-grab it. See UIElementExtensions.KeepOnScreen.
        this.KeepOnScreen();
    }

    private void BringToFront() => ZIndex = WindowOrder.Next();

    /// <summary>Instantly show this host at full opacity (no fade-in / no linger). Used to switch between windows that share
    ///     a position group, so a sibling replacing another reads as one window changing content, not a cross-fade.</summary>
    public void SnapShown()
    {
        Lingering = false;
        FadeAlpha = FadeTarget = 1f;
        Visible = true;
        PendingOpenSound = false;  //a switch, not a fresh open, stay silent
        PendingCloseSound = false;
    }

    /// <summary>Instantly hide this host (cancel any fade-out / linger).</summary>
    public void SnapHidden()
    {
        Lingering = false;
        FadeAlpha = FadeTarget = 0f;
        Visible = false;
        PendingOpenSound = false;  //a switch, not a real close, stay silent
        PendingCloseSound = false;
    }

    public override void Draw(SpriteBatchEx spriteBatch)
    {
        if (!Visible)
            return;

        //slide the whole window up into place as it fades in (and back down as it fades out), like the sign popup. Only a
        //REAL fade slides: a sub-menu switch snaps FadeAlpha to 1 (SnapShown), so (1-FadeAlpha)*dist = 0 = no slide. Purely
        //visual: Y is offset for the duration of Draw and restored before any input/drag/hit-test reads it.
        var slideY = (SlideOnFade && Fades) ? (int)((1f - FadeAlpha) * SlideDistance) : 0;

        if (slideY != 0)
            Y += slideY;

        try
        {
            DrawInner(spriteBatch);
        } finally
        {
            if (slideY != 0)
                Y -= slideY;
        }
    }

    private void DrawInner(SpriteBatchEx spriteBatch)
    {
        UpdateClipRect();

        if ((ClipRect.Width <= 0) || (ClipRect.Height <= 0))
            return;

        //fade transition (in or out): render through the target and blit with the current alpha. During a fade-OUT the
        //inner has already hidden, so reuse the last captured frame (renderInner:false) instead of re-rendering nothing.
        //effective opacity = the open/close fade (when Fades) times the owner's external multiplier
        var alpha = (Fades ? FadeAlpha : 1f) * ExternalAlpha;

        if ((Fades && Lingering) || (alpha < 0.999f))
        {
            if (Fades && Lingering && (FadeAlpha <= 0.01f))
                return;

            DrawSharpBilinear(spriteBatch, alpha, Inner.Visible);
            DrawNativeTextLayer(spriteBatch, alpha);

            return;
        }

        if (!Inner.Visible)
            return;

        //at an EXACT integer scale, nearest-neighbour stretches every source pixel evenly, so the cheap direct
        //transform is already pixel-perfect. A fractional scale (e.g. a 1.5x / 2.25x window-size slider) would stretch
        //some source pixels across an extra screen pixel under plain nearest, so a few columns/rows look "squished" and
        //shimmer, exactly the world-scaling problem. There we route through the sharp-bilinear pass instead.
        if (IsNearInteger(Scale))
            DrawDirect(spriteBatch);
        else
            DrawSharpBilinear(spriteBatch);

        DrawNativeTextLayer(spriteBatch, alpha);
    }

    //paints this window's TTF text (RenderNative labels + slot-grid key hints/stack counts) at native resolution, ON TOP
    //of the just-blitted magnified content and IN this host's draw position, so a window drawn later (higher z-order)
    //correctly covers an earlier window's text instead of it bleeding over the top. Nested ScaleHosts are skipped here;
    //each draws its own text when it draws.
    private void DrawNativeTextLayer(SpriteBatchEx spriteBatch, float alpha)
    {
        if ((alpha <= 0.01f) || !TtfTextRenderer.Available)
            return;

        //a ScaleHost's descendants carry real screen-space ScreenX, so the screen origin and native origin are identical.
        //During a close-fade the inner is already hidden but we're drawing its lingering frame, so paint its text anyway
        //(ignoreRootVisibility) so it fades out WITH the window instead of disappearing in one frame.
        WalkNativeText(spriteBatch, Inner, ScreenX, ScreenY, ScreenX, ScreenY, Scale, alpha, Lingering);
    }

    /// <summary>
    ///     Walks an element tree painting every RenderNative <see cref="UILabel" /> and <see cref="INativeTextDrawer" /> in
    ///     crisp TTF at native resolution, mapping native positions to the screen via the two origins (a descendant maps as
    ///     <c>screenX = screenOrigin + (ScreenX - nativeOrigin) * scale</c>). Shared by the in-world ScaleHosts (equal
    ///     origins) and the lobby's letterbox pass (screenOrigin = the letterbox corner, nativeOrigin = 0).
    /// </summary>
    public static void WalkNativeText(SpriteBatchEx spriteBatch, UIElement element, int screenOriginX, int screenOriginY, int nativeOriginX, int nativeOriginY, float scale, float alpha, bool ignoreRootVisibility = false)
    {
        //ignoreRootVisibility lets the host paint its text during a close-fade LINGER, where the inner is already hidden
        //(Visible=false) but we still draw its captured frame fading out, otherwise the text would vanish in one frame
        //while the rest of the window fades. Only the ROOT element is exempt; descendants are still visibility-checked.
        if (!ignoreRootVisibility && !element.Visible)
            return;

        //a nested magnifier paints its own text in its own Draw at its own scale
        if (element is ScaleHost)
            return;

        if (element is UILabel { RenderNative: true } label)
            NativeText.DrawLabel(spriteBatch, label, screenOriginX, screenOriginY, nativeOriginX, nativeOriginY, scale, alpha);

        if (element is INativeTextDrawer nativeDrawer)
            nativeDrawer.DrawNativeText(spriteBatch, screenOriginX, screenOriginY, nativeOriginX, nativeOriginY, scale, alpha);

        if (element is UIPanel panel)
            foreach (var c in panel.Children)
                WalkNativeText(spriteBatch, c, screenOriginX, screenOriginY, nativeOriginX, nativeOriginY, scale, alpha);
    }

    //integer scale: draw the child under a plain scale transform anchored at this host's screen origin. The default UI
    //pass uses a deferred batch with GlobalSettings.Sampler and no transform, so end it, draw scaled, then restore it.
    private void DrawDirect(SpriteBatchEx spriteBatch)
    {
        var ox = ScreenX;
        var oy = ScreenY;

        var transform = Matrix.CreateTranslation(-ox, -oy, 0f)
                        * Matrix.CreateScale(Scale, Scale, 1f)
                        * Matrix.CreateTranslation(ox, oy, 0f);

        //children compute their own ClipRect against THIS panel's ClipRect, but they draw in NATIVE coords under the
        //scale transform. Our ClipRect is screen-space (already clamped to the viewport). Map it back into native
        //space so off-screen edges clip where the magnified pixels actually fall - otherwise a window dragged partly
        //off an edge over-clips its content (the clip would be applied before the magnify, eating visible pixels).
        //Restored after the child draws so input-time ContainsPoint keeps using the screen-space rect.
        var screenClip = ClipRect;
        var nl = ox + (int)Math.Floor((screenClip.Left - ox) / Scale);
        var nt = oy + (int)Math.Floor((screenClip.Top - oy) / Scale);
        var nr = ox + (int)Math.Ceiling((screenClip.Right - ox) / Scale);
        var nb = oy + (int)Math.Ceiling((screenClip.Bottom - oy) / Scale);
        ClipRect = new Rectangle(nl, nt, nr - nl, nb - nt);

        spriteBatch.Begin(samplerState: spriteBatch.SamplerState, transformMatrix: transform);
        Inner.Draw(spriteBatch);
        spriteBatch.End();

        ClipRect = screenClip;
    }

    //fractional scale: the same sharp-bilinear trick the world render uses (ChaosGame.Draw). Render the child crisp at
    //1:1 into NativeTarget, integer-upscale that (nearest) to the smallest whole multiple >= the scaled size so pixel
    //INTERIORS stay crisp, then bilinear-DOWNscale to the final on-screen size, which softens only the thin pixel seams
    //instead of squishing whole columns. Costs two render-target switches per visible fractional host; the common
    //integer scales (1x/2x/3x/4x) never reach here, so the default UI pays nothing.
    private void DrawSharpBilinear(SpriteBatchEx spriteBatch, float alpha = 1f, bool renderInner = true)
    {
        var iw = Inner.Width;
        var ih = Inner.Height;

        if ((iw <= 0) || (ih <= 0))
            return;

        //a fade-out reuses the last captured NativeTarget without re-rendering the (now hidden) inner; bail if there is
        //nothing captured yet.
        if (!renderInner && (NativeTarget is null))
            return;

        var tint = Color.White * alpha;
        var gd = spriteBatch.GraphicsDevice;

        //integer prescale factor (nearest upscale of the native child). minK = the smallest multiple that still leaves
        //a DOWNscale to the final size (never an upscale, which would blur); wantK supersamples by SharpSupersample for
        //crispness; maxK keeps the intermediate within MaxIntermediate. Clamp wantK between the two.
        var minK = (int)Math.Ceiling(Scale);
        var wantK = (int)Math.Ceiling(Scale * SharpSupersample);
        var maxK = Math.Max(minK, MaxIntermediate / Math.Max(iw, ih));
        var k = Math.Clamp(wantK, minK, maxK);

        //(re)create the two targets only when their size changes (the child is a fixed native size; Scale changes the
        //host size, not the child). NativeTarget = the child at 1:1; ScaledTarget = that at k x k.
        if (renderInner && ((NativeTarget is null) || (NativeTarget.Width != iw) || (NativeTarget.Height != ih)))
        {
            NativeTarget?.Dispose();
            NativeTarget = new RenderTarget2D(gd, iw, ih, false, SurfaceFormat.Color, DepthFormat.None);
        }

        var sw = iw * k;
        var sh = ih * k;

        if ((ScaledTarget is null) || (ScaledTarget.Width != sw) || (ScaledTarget.Height != sh))
        {
            ScaledTarget?.Dispose();
            ScaledTarget = new RenderTarget2D(gd, sw, sh, false, SurfaceFormat.Color, DepthFormat.None);
        }

        var ox = ScreenX;
        var oy = ScreenY;
        var screenClip = ClipRect; //on-screen clip (already intersected with the parents + viewport)

        //whatever the UI pass is rendering into (the backbuffer in practice), so we can put it back after the detour
        var prevTargets = gd.GetRenderTargets();

        spriteBatch.Flush();
        //1) child crisp at 1:1, translated so its top-left lands at the target origin. No external clip inside the
        //target (clip to the child's own bounds) so the WHOLE panel is captured; on-screen clipping happens at step 3.
        //A fade-OUT skips this and reuses the last captured frame (the inner is already hidden).
        if (renderInner)
        {
            gd.SetRenderTarget(NativeTarget);
            gd.Clear(Color.Transparent);
            ClipRect = new Rectangle(ox, oy, iw, ih);
            spriteBatch.Begin(samplerState: spriteBatch.SamplerState, transformMatrix: Matrix.CreateTranslation(-ox, -oy, 0f));
            Inner.Draw(spriteBatch);
            spriteBatch.End();
            ClipRect = screenClip;
        }

        //2) crisp integer upscale (nearest) keeps pixel interiors sharp
        gd.SetRenderTarget(ScaledTarget);
        gd.Clear(Color.Transparent);
        spriteBatch.Begin(samplerState: SamplerState.PointClamp);
        spriteBatch.Draw(NativeTarget, new Rectangle(0, 0, sw, sh), Color.White);
        spriteBatch.End();

        if (prevTargets.Length == 0)
            gd.SetRenderTarget(null);
        else
            gd.SetRenderTargets(prevTargets);

        //3) bilinear downscale to the final size, clipped to the on-screen rect (matters only if a parent clips smaller
        //than the viewport; an off-screen edge is clipped by the backbuffer for free). LinearClamp blends only seams.
        var dest = new Rectangle(ox, oy, Width, Height);
        var clip = Rectangle.Intersect(dest, screenClip);

        spriteBatch.Begin(samplerState: SamplerState.LinearClamp);

        if ((clip.Width > 0) && (clip.Height > 0))
        {
            if (clip == dest)
                spriteBatch.Draw(ScaledTarget, dest, tint);
            else
            {
                //map the clipped destination back into the scaled source so a partial clip samples the right region
                var fx = (float)sw / dest.Width;
                var fy = (float)sh / dest.Height;

                var sub = new Rectangle(
                    (int)((clip.X - dest.X) * fx),
                    (int)((clip.Y - dest.Y) * fy),
                    (int)(clip.Width * fx),
                    (int)(clip.Height * fy));

                spriteBatch.Draw(ScaledTarget, clip, sub, tint);
            }
        }

        spriteBatch.End();
    }

    //true when the scale is (essentially) a whole number, so plain nearest stretches every source pixel evenly and the
    //sharp-bilinear detour is unnecessary, mirrors ChaosGame.IsNearInteger
    private static bool IsNearInteger(float v) => Math.Abs(v - (float)Math.Round(v)) < 0.001f;
}
