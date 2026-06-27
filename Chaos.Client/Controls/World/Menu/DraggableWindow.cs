#region
using Chaos.Client.Controls.Components;
using Chaos.Client.Definitions;
using Chaos.Client.Extensions;
using Chaos.Client.Rendering;
using Chaos.Client.Systems;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
#endregion

namespace Chaos.Client.Controls.World.Menu;

/// <summary>
///     A non-resizable window the player can drag by its titlebar. The reusable shell for the new menu
///     system: put content in <see cref="Content" />. Dark, low-contrast nine-patch-style frame drawn
///     programmatically (crisp 1px lines at any resolution), matching the hud.png concept: near-black
///     fill, a thin bronze border, a titlebar with an underline, and a boxed close button.
/// </summary>
public class DraggableWindow : UIPanel
{
    public const int TITLE_H = 22;
    private const int CLOSE_W = 18;
    private const int PIN_W = 18;
    private const int TITLE_FONT = 16;
    private const int GRIP_SIZE = 14;
    private const int MIN_W = 140;
    private const int MIN_H = TITLE_H + 48;

    //dlgframe wood-border geometry. The 16x16 tile only has WOOD_PIXELS_* of opaque wood on its outer edge
    //(the rest is transparent), so the border is drawn at FRAME_SCALE and content sits flush at FRAME px, not the
    //whole tile (that transparent inner is what caused the fat black padding). Only windows that opt in (useWoodFrame)
    //get the wood look; everything else keeps the original flat 1px-border layout untouched.
    private const int TILE_PIXELS = 16;
    private const int WOOD_PIXELS_TOP = 4;
    private const int WOOD_PIXELS_BOTTOM = 5;
    private const int WOOD_PIXELS_LEFT = 4;
    private const int WOOD_PIXELS_RIGHT = 5;
    public const int FRAME_SCALE = 2;
    public const int FRAME_TOP    = FRAME_SCALE * WOOD_PIXELS_TOP;
    public const int FRAME_BOTTOM = FRAME_SCALE * WOOD_PIXELS_BOTTOM;
    public const int FRAME_LEFT   = FRAME_SCALE * WOOD_PIXELS_LEFT;
    public const int FRAME_RIGHT  = FRAME_SCALE * WOOD_PIXELS_RIGHT;

    private readonly bool UseWoodFrame;

    //when true, the content fills the window edge-to-edge (sides + bottom) UNDER the wood frame, which draws on top of its
    //outer border, so a bordered content panel (inventory/skills/spells art) merges into the frame instead of showing a
    //second border with a dark gap between. Top still sits below the titlebar. Only for windows whose content has a border.
    public bool FlushContent { get; }

    private static Texture2D[]? FramePieces; //dlgframe 0..7 (TL,top,TR,left,right,BL,bottom,BR), loaded once

    //generic 18x18 button art for the close (_00) and pin (_01) titlebar buttons. Loaded fresh PER BOX because each
    //UIPanel disposes its own Background on teardown; premultiplied so the rounded-corner alpha blends cleanly.
    private static Texture2D? LoadButtonArt(string resourceName)
    {
        try
        {
            using var stream = System.Reflection.Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName);

            if (stream is null)
                return null;

            var texture = Texture2D.FromStream(ChaosGame.Device, stream);
            var pixels = new Color[texture.Width * texture.Height];
            texture.GetData(pixels);

            for (var i = 0; i < pixels.Length; i++)
                pixels[i] = Color.FromNonPremultiplied(pixels[i].R, pixels[i].G, pixels[i].B, pixels[i].A);

            texture.SetData(pixels);

            return texture;
        } catch
        {
            return null;
        }
    }

    //total non-content chrome for the current frame mode, so a subclass can size itself from its content:
    //outer width = ContentWidth + ChromeWidth, outer height = ContentHeight + ChromeHeight. Matches the ctor's insets
    //(flat: 1px sides + bottom, TITLE_H top; wood: FRAME sides + bottom, FRAME+TITLE_H top).
    public int ChromeWidth => FlushContent ? 0 : (UseWoodFrame ? FRAME_LEFT + FRAME_RIGHT : 2);
    public int ChromeHeight => (UseWoodFrame ? FRAME_TOP : 0) + TITLE_H + (FlushContent ? 0 : (UseWoodFrame ? FRAME_BOTTOM : 1));

    private bool Dragging;
    private int DragOffsetX;
    private int DragOffsetY;

    /// <summary>True while the user is actively dragging the window (auto-layout nudges must not fight the hand).</summary>
    public bool IsUserDragging => Dragging;

    private bool Resizing;
    private int ResizeOffsetX;
    private int ResizeOffsetY;

    //close/pin button press state: the action fires on RELEASE with the cursor still on the button (like a real
    //button), and the art shows the pressed sprite (_01) while held over it. Each box owns its own texture instances
    //(_00 normal, _01 pressed) so disposal is safe; the window disposes all four in Dispose.
    private bool ClosePressed;
    private bool PinPressed;
    private readonly Texture2D? CloseNormalTex;
    private readonly Texture2D? ClosePressedTex;
    private readonly Texture2D? PinNormalTex;
    private readonly Texture2D? PinPressedTex;
    private readonly Texture2D? PinActiveTex;

    private readonly UIPanel TitleBar;
    private readonly UIPanel CloseBox;
    private readonly UIPanel PinBox;
    private readonly ResizeHandle ResizeGrip;
    private readonly UILabel TitleLabel;

    //each titlebar-button glyph (X, *) is two stacked labels for a 1px emboss: a Shadow (drawn first/behind) and a Front
    //(drawn second/on top). When NOT pressed/on: dark shadow offset +1,+1, bright front centered. When pressed/on the
    //colours invert AND the whole pair shifts +1,+1 (a "pushed in" look): bright shadow, dark front. See ApplyGlyph.
    private readonly Color BaseBg;
    private readonly Color BaseBorder;
    private readonly Color BaseTitleBar;
    private readonly bool ShowClose;
    protected float Fade = 1f;

    //open/close fade (opt-in via FadeOnOpen). The whole window (chrome + content, including nested ScaleHosts) is
    //rendered to a shared full-screen target at its normal screen coords, then blitted with OpenAlpha so it fades
    //uniformly. On close it LINGERS (keeps drawing the target while fading out) before it actually hides.
    public bool FadeOnOpen { get; set; }

    //when set, the window fades by tinting its CHROME (Fade) and this content host's ExternalAlpha together, instead of
    //rendering the whole window to a target, which a nested ScaleHost's own render-target detour would break (it would
    //leave the titlebar/close drawn at full alpha). Used by the hud windows whose content is one magnified ScaleHost.
    public ScaleHost? ContentHost { get; set; }

    private float OpenAlpha;
    private float OpenTarget;
    private bool OpenLingering;
    private bool SuppressVis; //guards the re-entrant Visible writes used to keep drawing during the fade-out
    private const float OpenFadeSeconds = 0.08f;
    private static RenderTarget2D? SharedFadeTarget;

    /// <summary>When true, the window dims toward <see cref="IdleOpacity" /> while the cursor is off it.</summary>
    public bool AutoFade { get; set; }

    /// <summary>When true (and AutoFade), the window is pinned open and never fades. Toggled by the titlebar pin button.</summary>
    public bool Pinned { get; set; }

    /// <summary>When true, the window can be resized by dragging its bottom-right corner.</summary>
    public bool Resizable { get; set; }

    public float IdleOpacity { get; set; } = 0.3f;

    /// <summary>Host area for the menu's content, positioned below the titlebar.</summary>
    public UIPanel Content { get; }

    public DraggableWindow(string title, int width, int height, bool showClose = true, bool useWoodFrame = false, bool flushContent = false)
    {
        Visible = false;
        Width = width;
        Height = height;
        ShowClose = showClose;
        UseWoodFrame = useWoodFrame;
        FlushContent = flushContent;

        //the wood thickness drawn at the edges + the titlebar/button inset (0 in flat mode).
        var titleInsetTop = useWoodFrame ? FRAME_TOP : 0;
        var titleInsetLeft = useWoodFrame ? FRAME_LEFT : 0;
        var titleInsetRight = useWoodFrame ? FRAME_RIGHT : 0;

        //the content side/bottom inset: the wood thickness in wood mode, the old 1px border otherwise.
        var frameLeft = useWoodFrame ? FRAME_LEFT : 1;
        var frameRight = useWoodFrame ? FRAME_RIGHT : 1;
        var frameBottom = useWoodFrame ? FRAME_BOTTOM : 1;

        //FlushContent: the content fills edge-to-edge (X=0, full width, down to the bottom) under the wood frame, which
        //draws over its outer border. Otherwise it is inset by b (the wood thickness / flat 1px border) like before.
        var bodyInsetLeft = flushContent ? 0 : frameLeft;
        var bodyInsetRight = flushContent ? 0 : frameRight;
        var bodyInsetBottom = flushContent ? 0 : frameBottom;

        //when there is no close box, the pin button takes the right-edge slot instead of sitting left of it
        var pinX = (showClose ? width - CLOSE_W - PIN_W - 2 : width - PIN_W - 1) - titleInsetRight;

        //colors sampled from the hud.png concept: near-black warm fill, subtle bronze border
        BaseBg = new Color(18, 16, 12) * 0.96f;
        BaseBorder = new Color(88, 72, 46);
        BaseTitleBar = new Color(27, 23, 16) * 0.97f;
        BackgroundColor = BaseBg;
        BorderColor = useWoodFrame ? null : BaseBorder; //wood mode draws its own border, so no flat 1px line

        //titlebar strip: a plain darker fill, no outline (the surrounding window border/frame is the only edge)
        TitleBar = new UIPanel
        {
            Name = "TitleBar",
            X = titleInsetLeft,
            Y = titleInsetTop,
            Width = width - titleInsetLeft - titleInsetRight,
            Height = TITLE_H,
            BackgroundColor = BaseTitleBar,
            IsPassThrough = true
        };
        AddChild(TitleBar);

        var cache = UiRenderer.Instance!;

        CloseNormalTex = LoadButtonArt("window_widget_close.png");
        ClosePressedTex = LoadButtonArt("window_widget_close_pressed.png");

        PinNormalTex = LoadButtonArt("window_widget_pin.png");
        PinPressedTex = LoadButtonArt("window_widget_pin_pressed.png");
        PinActiveTex = LoadButtonArt("window_widget_pin_active.png");

        //close button: the generic button art drawn behind the X glyph (hidden when the window has no close)
        CloseBox = new UIPanel
        {
            Name = "CloseBox",
            X = width - titleInsetRight - CLOSE_W - 1,
            Y = titleInsetTop + 2,
            Width = CLOSE_W,
            Height = CLOSE_W,
            Background = CloseNormalTex,
            IsPassThrough = true,
            Visible = showClose
        };
        AddChild(CloseBox);

        TitleLabel = new UILabel
        {
            Name = "Title",
            Text = title,
            X = titleInsetLeft + 5,
            Y = titleInsetTop,
            Width = pinX - titleInsetRight - 7,
            Height = TITLE_H,
            CustomFontSize = TITLE_FONT, //window titles use the optional UI font (Cinzel); the rest of the UI stays bitmap
            ForegroundColor = new Color(192, 176, 138),
            IsHitTestVisible = false //let titlebar clicks reach the window for dragging
        };
        AddChild(TitleLabel);

        //pin / sticky toggle, left of the close button. Only shown on fadeable windows; clicking it keeps the
        //window from fading out. Pass-through so the window's OnMouseDown handles the click (like the titlebar).
        PinBox = new UIPanel
        {
            Name = "PinBox",
            X = pinX,
            Y = titleInsetTop + 2,
            Width = PIN_W,
            Height = PIN_W,
            Background = PinNormalTex, //_00 when not toggled; Update swaps to _01 (PinPressedTex) when pressed or pinned
            IsPassThrough = true,
            Visible = false
        };
        AddChild(PinBox);

        Content = new UIPanel
        {
            Name = "Content",
            X = bodyInsetLeft,
            Y = titleInsetTop + TITLE_H,
            Width = width - bodyInsetLeft - bodyInsetRight,
            Height = height - titleInsetTop - TITLE_H - bodyInsetBottom
        };
        AddChild(Content);

        //bottom-right resize grip (only shown when Resizable). It captures the press itself and starts the resize,
        //so it must NOT be pass-through, otherwise the content panel underneath would eat the click.
        ResizeGrip = new ResizeHandle
        {
            Name = "ResizeGrip",
            X = width - frameRight - GRIP_SIZE,
            Y = height - frameBottom - GRIP_SIZE,
            Width = GRIP_SIZE,
            Height = GRIP_SIZE,
            BorderColor = BaseBorder,
            Visible = false
        };
        ResizeGrip.Pressed += BeginResize;
        AddChild(ResizeGrip);

        //drive the open/close fade from this window's own visibility (opt-in via FadeOnOpen). On close, cancel the hide
        //and keep the window visible (drawing) while it fades out; Update finishes the hide once transparent.
        VisibilityChanged += v =>
        {
            if (SuppressVis || !FadeOnOpen)
                return;

            if (v)
            {
                OpenTarget = 1f; //ease up from the current alpha (0 fresh, or wherever a fast re-open caught it)
                OpenLingering = false;
                SoundSystem.PlayWindowOpen(); //window opened by the player: play the open (fade-in) sound
            } else if (OpenAlpha > 0.02f)
            {
                OpenLingering = true;
                OpenTarget = 0f;
                SuppressVis = true;
                Visible = true; //keep drawing through the fade-out
                SuppressVis = false;
                SoundSystem.PlayWindowClose(); //window closed by the player: play the close (fade-out) sound
            }
        };
    }

    public static DraggableWindow CreatePanelPopup(string name, UIPanel panel)
    {
        var window = new DraggableWindow(name, panel.Width, panel.Height + FRAME_TOP + TITLE_H, useWoodFrame: true, flushContent: true)
        {
            CentersOnFirstShow = true,
            FadeOnOpen = true
        };
        window.Content.AddChild(panel);
        return window;
    }

    public static DraggableWindow CreateScaledPopup(string name, UIPanel panel)
    {
        var scaleHost = new ScaleHost(panel, ClientSettings.EffectiveWindowScale);
        var window = CreatePanelPopup(name, scaleHost);
        window.ContentHost = scaleHost;
        return window;
    }

    private void BeginResize(MouseDownEvent e)
    {
        if (!Resizable)
            return;

        BringToFront();
        Resizing = true;
        ResizeOffsetX = e.ScreenX - (ScreenX + Width);
        ResizeOffsetY = e.ScreenY - (ScreenY + Height);
    }

    /// <summary>
    ///     When true, the window centers itself on the native UI the FIRST time it opens, then never again, so once the
    ///     player drags it somewhere it stays there for the rest of the session (not persisted). Off by default.
    /// </summary>
    public bool CentersOnFirstShow { get; set; }

    private bool HasCentered;

    /// <summary>Show the window and raise it above its siblings. Centers once on first open if <see cref="CentersOnFirstShow" />.</summary>
    public void Open()
    {
        if (CentersOnFirstShow && !HasCentered)
        {
            this.CenterOnUi();
            HasCentered = true;
        }

        BringToFront();
        Visible = true;
    }

    public void Toggle()
    {
        if (Visible)
            Visible = false;
        else
            Open();
    }

    public void BringToFront() => ZIndex = WindowOrder.Next();

    /// <summary>True while the window is genuinely open (visible and not just drawing through its close-fade).</summary>
    public bool IsOpen => Visible && (!FadeOnOpen || OpenTarget > 0f);

    /// <summary>Whether this window has a close box. Always-present windows (e.g. chat) have none and must not be
    ///     closed by the Escape-closes-focused-window rule.</summary>
    public bool Closeable => ShowClose;

    /// <summary>Closes the window exactly as its close box does (honouring any subclass <see cref="OnCloseClicked" />).</summary>
    public void Close() => OnCloseClicked();

    /// <summary>
    ///     Draws the window. Flat windows are entirely the base panel (fill + 1px border + chrome children). Wood windows
    ///     let the base draw the dark fill + titlebar band + content (all already inset by FRAME), then paint the dlgframe
    ///     wood border ON TOP of the outer ring (content never sits there, so it is not covered).
    /// </summary>
    public override void Draw(SpriteBatchEx spriteBatch)
    {
        //target-fade mode (no single content host to self-fade, e.g. Options/Emotes): render the whole window to a target
        //and blit it tinted by OpenAlpha. Windows WITH a ContentHost fade via the chrome-fade path instead (see Update),
        //which avoids the nested ScaleHost's render-target detour leaving the titlebar/close at full alpha.
        if (FadeOnOpen && (ContentHost is null) && (OpenLingering || (OpenAlpha < 0.999f)))
        {
            if (OpenLingering && (OpenAlpha <= 0.01f))
                return;

            DrawFaded(spriteBatch, OpenAlpha);

            return;
        }

        base.Draw(spriteBatch);

        if (UseWoodFrame && Visible)
            DrawWoodFrame(spriteBatch);
    }

    //renders the whole window (fill + children incl. nested ScaleHosts + wood frame) into a shared full-screen target at
    //its NORMAL screen coords (so the nested ScaleHosts' absolute-coord blits land correctly) then blits the window's
    //rect to the backbuffer tinted by alpha. One transient target shared across windows (used only while fading).
    private void DrawFaded(SpriteBatchEx spriteBatch, float alpha)
    {
        var gd = spriteBatch.GraphicsDevice;
        var tw = ChaosGame.UiWidth;
        var th = ChaosGame.UiHeight;

        if ((tw <= 0) || (th <= 0))
        {
            base.Draw(spriteBatch);

            if (UseWoodFrame)
                DrawWoodFrame(spriteBatch);

            return;
        }

        if ((SharedFadeTarget is null) || (SharedFadeTarget.Width != tw) || (SharedFadeTarget.Height != th))
        {
            SharedFadeTarget?.Dispose();
            SharedFadeTarget = new RenderTarget2D(gd, tw, th, false, SurfaceFormat.Color, DepthFormat.None);
        }

        var prevTargets = gd.GetRenderTargets();
        spriteBatch.Flush();

        gd.SetRenderTarget(SharedFadeTarget);
        gd.Clear(Color.Transparent);
        spriteBatch.Begin(samplerState: spriteBatch.SamplerState);
        base.Draw(spriteBatch);

        if (UseWoodFrame)
            DrawWoodFrame(spriteBatch);

        spriteBatch.End();

        if (prevTargets.Length == 0)
            gd.SetRenderTarget(null);
        else
            gd.SetRenderTargets(prevTargets);

        var rect = new Rectangle(ScreenX, ScreenY, Width, Height);
        spriteBatch.Begin(samplerState: spriteBatch.SamplerState);
        spriteBatch.Draw(SharedFadeTarget, rect, rect, Color.White * alpha);
        spriteBatch.End();
    }

    //dlgframe border, edges LOOPED (tiled) not scaled, corners fixed. Each 16x16 piece is drawn at FRAME_SCALE; only its
    //outer WOOD_PIXELS px is opaque wood, the rest transparent, so it overlays harmlessly over the band/content. Tinted by
    //Fade so AutoFade still works. Pieces are clipped to the window rect so a tiled edge never bleeds past the window.
    private void DrawWoodFrame(SpriteBatchEx spriteBatch)
    {
        var pieces = LoadFramePieces();

        if (pieces is null)
            return;

        var tint = Color.White * Fade;
        var t = TILE_PIXELS * FRAME_SCALE; //scaled tile size (the wood occupies the outer WOOD_PIXELS*FRAME_SCALE of it)
        int x0 = ScreenX, y0 = ScreenY, w = Width, h = Height;

        for (var x = 0; x < w; x += t)
        {
            Blit(spriteBatch, pieces[1], x0 + x, y0, tint);             //top edge, looped
            Blit(spriteBatch, pieces[6], x0 + x, y0 + h - t, tint);     //bottom edge, looped
        }

        for (var y = 0; y < h; y += t)
        {
            Blit(spriteBatch, pieces[3], x0, y0 + y, tint);            //left edge, looped
            Blit(spriteBatch, pieces[4], x0 + w - t, y0 + y, tint);    //right edge, looped
        }

        Blit(spriteBatch, pieces[0], x0, y0, tint);                   //corners last, over the edge ends
        Blit(spriteBatch, pieces[2], x0 + w - t, y0, tint);
        Blit(spriteBatch, pieces[5], x0, y0 + h - t, tint);
        Blit(spriteBatch, pieces[7], x0 + w - t, y0 + h - t, tint);
    }

    //draws one frame piece at FRAME_SCALE, clipped to the window rect (so tiled edges/corners never spill past the window).
    private void Blit(SpriteBatchEx spriteBatch, Texture2D tex, int x, int y, Color color)
    {
        var atlas = tex;
        var src = new Rectangle(0, 0, tex.Width, tex.Height);

        if (tex is CachedTexture2D { AtlasRegion: { } region })
        {
            atlas = region.Atlas;
            src = region.SourceRect;
        }

        var dest = new Rectangle(x, y, src.Width * FRAME_SCALE, src.Height * FRAME_SCALE);
        var clipped = Rectangle.Intersect(dest, new Rectangle(ScreenX, ScreenY, Width, Height));

        if (clipped.IsEmpty)
            return;

        //map the clipped destination back to a source sub-rect (everything is on a FRAME_SCALE grid)
        var sub = new Rectangle(
            src.X + (clipped.X - dest.X) / FRAME_SCALE,
            src.Y + (clipped.Y - dest.Y) / FRAME_SCALE,
            clipped.Width / FRAME_SCALE,
            clipped.Height / FRAME_SCALE);

        if ((sub.Width <= 0) || (sub.Height <= 0))
            return;

        spriteBatch.Draw(atlas, clipped, sub, color);
    }

    //the 8 dlgframe pieces as textures (GUI palette, already correct), loaded once and shared by every wood window.
    private static Texture2D[]? LoadFramePieces()
    {
        if (FramePieces is not null)
            return FramePieces;

        var ui = UiRenderer.Instance;

        if (ui is null)
            return null;

        var pieces = new Texture2D[8];

        for (var i = 0; i < 8; i++)
            pieces[i] = ui.GetEpfTexture("dlgframe.epf", i);

        return FramePieces = pieces;
    }

    /// <summary>Subclasses override to react to the current chrome opacity (0 = idle, 1 = active).</summary>
    protected virtual void OnFade(float opacity) { }

    //true when the cursor is within a titlebar button's screen rect (used for the release-over-button click + pressed art)
    private static bool CursorOverBox(UIPanel box)
        => box.Visible
           && (InputBuffer.MouseX >= box.ScreenX) && (InputBuffer.MouseX < box.ScreenX + box.Width)
           && (InputBuffer.MouseY >= box.ScreenY) && (InputBuffer.MouseY < box.ScreenY + box.Height);

    //the close/pin boxes and resize grip are pass-through, so the dispatcher reports THIS window as the hovered element
    //rather than the box. The tooltip resolver asks the window what chrome (if any) the cursor is over so those gadgets
    //still get hover help. Returns null when the cursor is not over a chrome gadget (the content/titlebar gets nothing).
    public string? ChromeTooltipAt(int mouseX, int mouseY)
    {
        bool Over(UIPanel box)
            => box.Visible
               && (mouseX >= box.ScreenX) && (mouseX < box.ScreenX + box.Width)
               && (mouseY >= box.ScreenY) && (mouseY < box.ScreenY + box.Height);

        if (Over(CloseBox))
            return "Close\nCloses this window. You can reopen it from the Menu, or with its hotkey.";

        if (Over(PinBox))
            return Pinned
                ? "Unpin\nThis window is pinned open. Click to let it fade out again when your cursor leaves it."
                : "Pin open\nThis window fades out while you are not using it. Click to pin it so it always stays fully visible.";

        if (ResizeGrip.Visible && (mouseX >= ResizeGrip.ScreenX) && (mouseX < ResizeGrip.ScreenX + ResizeGrip.Width)
            && (mouseY >= ResizeGrip.ScreenY) && (mouseY < ResizeGrip.ScreenY + ResizeGrip.Height))
            return "Resize\nDrag to make this window larger or smaller.";

        return null;
    }

    public override void Dispose()
    {
        //the button boxes each hold one of the four textures; null them so base.Dispose doesn't free a shared instance,
        //then dispose all four here exactly once.
        CloseBox.Background = null;
        PinBox.Background = null;
        CloseNormalTex?.Dispose();
        ClosePressedTex?.Dispose();
        PinNormalTex?.Dispose();
        PinPressedTex?.Dispose();
        PinActiveTex?.Dispose();

        base.Dispose();
    }

    //a visible window swallows clicks so they never fall through to the world behind it (a faded window is not
    //hit-test-visible, so it isn't hit at all and clicks pass through as intended). OnMouseDown below already eats the
    //press; these eat the synthesized click/double-click so left-click world interaction can't fire under the window.
    public override void OnClick(ClickEvent e) => e.Handled = true;
    public override void OnDoubleClick(DoubleClickEvent e) => e.Handled = true;

    public override void OnMouseDown(MouseDownEvent e)
    {
        BringToFront();

        var localX = e.ScreenX - ScreenX;
        var localY = e.ScreenY - ScreenY;

        //titlebar band + buttons are inset by the wood thickness
        var insetTop = UseWoodFrame ? FRAME_TOP : 0;
        var insetLeft = UseWoodFrame ? FRAME_LEFT : 0;
        var insetRight = UseWoodFrame ? FRAME_RIGHT : 0;

        //the draggable strip is the top wood + the titlebar band beneath it
        if ((e.Button == MouseButton.Left) && (localY < insetTop + TITLE_H))
        {
            //close button on the right of the titlebar (skipped entirely when this window has no close button). Pressing
            //only ARMS it; the close fires in Update on release if the cursor is still on the button (real-button feel).
            if (ShowClose && (localX >= Width - insetRight - CLOSE_W - 1))
            {
                ClosePressed = true;
                e.Handled = true;

                return;
            }

            //pin / sticky toggle, only on fadeable windows. It sits left of close, or at the right edge when there is none.
            var pinX = (ShowClose ? Width - CLOSE_W - PIN_W - 2 : Width - PIN_W - 1) - insetRight;
            var pinEnd = (ShowClose ? Width - CLOSE_W - 1 : Width) - insetRight;

            if (AutoFade && (localX >= pinX) && (localX < pinEnd))
            {
                PinPressed = true; //toggles in Update on release if still over the button
                e.Handled = true;

                return;
            }

            //start dragging by the titlebar; the actual move runs in Update so it does not depend on
            //the input dispatcher's capture/mouse-up path (which can drop the mouse-up after a drag).
            Dragging = true;
            DragOffsetX = e.ScreenX - X;
            DragOffsetY = e.ScreenY - Y;
        }

        e.Handled = true;
    }

    public override void Update(GameTime gameTime)
    {
        base.Update(gameTime);

        //ease the open/close fade and, once a fade-out has finished, actually hide the window
        if (FadeOnOpen)
        {
            var step = (float)gameTime.ElapsedGameTime.TotalSeconds / OpenFadeSeconds;

            if (OpenAlpha < OpenTarget)
                OpenAlpha = Math.Min(OpenTarget, OpenAlpha + step);
            else if (OpenAlpha > OpenTarget)
                OpenAlpha = Math.Max(OpenTarget, OpenAlpha - step);

            if (OpenLingering && (OpenAlpha <= 0.01f))
            {
                OpenLingering = false;
                SuppressVis = true;
                Visible = false;
                SuppressVis = false;
            }

            //chrome-fade mode: tint the chrome by OpenAlpha and fade the content host's alpha together (no whole-window
            //target render, which the nested host's own target detour would break)
            if (ContentHost is not null)
            {
                Fade = OpenAlpha;
                ContentHost.ExternalAlpha = OpenAlpha;
                ApplyChromeFade();
            }
        }

        ResizeGrip.Visible = Resizable;

        if (Resizing)
        {
            if (!InputBuffer.IsLeftButtonHeld)
                Resizing = false;
            else
            {
                var newW = Math.Clamp(InputBuffer.MouseX - ScreenX - ResizeOffsetX, MIN_W, ChaosGame.UiWidth - X);
                var newH = Math.Clamp(InputBuffer.MouseY - ScreenY - ResizeOffsetY, MIN_H, ChaosGame.UiHeight - Y);

                if ((newW != Width) || (newH != Height))
                {
                    Width = newW;
                    Height = newH;
                    Relayout();
                }
            }
        }

        if (Dragging)
        {
            //release detected from the global button state, so a missed OnMouseUp can never strand us
            if (!InputBuffer.IsLeftButtonHeld)
                Dragging = false;
            else
            {
                X = InputBuffer.MouseX - DragOffsetX;
                Y = InputBuffer.MouseY - DragOffsetY;
            }
        }

        //close/pin: the action fires on RELEASE, and only if the cursor is still on the button (release off it cancels,
        //like a real button). The art shows the pressed sprite (_01) while held over the button; the pin also shows _01
        //while toggled on, and _00 otherwise.
        var lmbHeld = InputBuffer.IsLeftButtonHeld;
        var overClose = CursorOverBox(CloseBox);
        var overPin = CursorOverBox(PinBox);

        if (ClosePressed && !lmbHeld)
        {
            if (overClose)
            {
                SoundSystem.PlayUiClick();
                OnCloseClicked();
            }

            ClosePressed = false;
        }

        if (PinPressed && !lmbHeld)
        {
            if (overPin)
            {
                SoundSystem.PlayUiClick();
                Pinned = !Pinned;
            }

            PinPressed = false;
        }

        var insetTop = UseWoodFrame ? FRAME_TOP : 0;
        var insetRight = UseWoodFrame ? FRAME_RIGHT : 0;
        var closeInverted = ClosePressed && overClose;
        var pinInverted = PinPressed && overPin;

        CloseBox.Background = closeInverted ? ClosePressedTex : CloseNormalTex;
        PinBox.Background = pinInverted ? PinPressedTex : Pinned ? PinActiveTex : PinNormalTex;

        var pinXpos = (ShowClose ? Width - CLOSE_W - PIN_W - 2 : Width - PIN_W - 1) - insetRight;

        //keep the window reachable every frame: at least half of it on each axis, and (because the only grab handle
        //is the titlebar at the top) never let the titlebar leave the top edge (a titlebar pulled above the top can
        //never be grabbed again). Running it unconditionally (not just while dragging) also rescues a window stranded
        //by the game window being resized smaller under it. See UIElementExtensions.KeepOnScreen.
        this.KeepOnScreen(TITLE_H);

        //the pin/sticky toggle only exists on fadeable windows
        PinBox.Visible = AutoFade;

        if (!AutoFade)
            return;

        //subclasses decide the fade target (the chat window is event-driven, not hover-driven). The ramp is framerate-
        //independent and reaches the target exactly. Fade-in and fade-out have separate durations (subclass-overridable):
        //the chat fades IN snappily on a new line but fades OUT slowly so the disappearance is visible.
        var target = ComputeFadeTarget();
        var fadeDuration = Fade > target ? AutoFadeOutSeconds : AutoFadeInSeconds;
        var fadeStep = (float)gameTime.ElapsedGameTime.TotalSeconds / Math.Max(0.0001f, fadeDuration);

        if (Fade < target)
            Fade = Math.Min(target, Fade + fadeStep);
        else if (Fade > target)
            Fade = Math.Max(target, Fade - fadeStep);

        //fade the chrome (fill, borders, titlebar labels + button art); subclasses keep CONTENT text opaque via OnFade
        ApplyChromeFade();

        //a faded-out window must not sit invisibly over the world catching clicks
        IsHitTestVisible = Fade > 0.04f;

        OnFade(Fade);
    }

    //tints the chrome (fill, border, titlebar band, titlebar labels + close/pin button art) by the current Fade. Shared by
    //the AutoFade idle fade and the open/close fade (chrome-fade mode) so the whole frame fades uniformly.
    private void ApplyChromeFade()
    {
        BackgroundColor = BaseBg * Fade;

        if (!UseWoodFrame)
            BorderColor = BaseBorder * Fade; //wood mode draws its own faded border in Draw; keep the flat line off

        TitleBar.BackgroundColor = BaseTitleBar * Fade;
        ResizeGrip.BorderColor = BaseBorder * Fade;
        TitleLabel.Opacity = Fade;
        CloseBox.BackgroundOpacity = Fade;
        PinBox.BackgroundOpacity = Fade;
    }

    /// <summary>Seconds for the AutoFade idle fade-IN ramp. Defaults to the snappy open/close speed.</summary>
    protected virtual float AutoFadeInSeconds => OpenFadeSeconds;

    /// <summary>Seconds for the AutoFade idle fade-OUT ramp. Defaults to the snappy open/close speed; the chat window
    ///     overrides this to a slower value so its disappearance is a visible fade rather than a snap.</summary>
    protected virtual float AutoFadeOutSeconds => OpenFadeSeconds;

    /// <summary>The opacity the window eases toward. Default: 1 while hovered/dragging/pinned, else <see cref="IdleOpacity" />.
    ///     Subclasses override to drive the fade from their own state instead of hover.</summary>
    protected virtual float ComputeFadeTarget()
    {
        var hovered = Dragging || Resizing || Pinned || CursorOnTop();

        return hovered ? 1f : IdleOpacity;
    }

    /// <summary>Invoked when the titlebar close box is clicked. Default hides the window; subclasses may override.</summary>
    protected virtual void OnCloseClicked()
    {
        Visible = false;
    }

    /// <summary>Programmatically resize the window (e.g. when the Options window-scale slider changes) and re-flow chrome.</summary>
    public void Resize(int width, int height)
    {
        Width = width;
        Height = height;
        Relayout();
    }

    /// <summary>Re-flows the chrome to the current Width/Height after a resize, then notifies subclasses.</summary>
    private void Relayout()
    {
        //mirror the ctor's frame/b insets so a resized wood window keeps its wood layout (frame=0,b=1 => original flat).
        var titleInsetTop = UseWoodFrame ? FRAME_TOP : 0;
        var titleInsetLeft = UseWoodFrame ? FRAME_LEFT : 0;
        var titleInsetRight = UseWoodFrame ? FRAME_RIGHT : 0;

        var frameLeft = UseWoodFrame ? FRAME_LEFT : 1;
        var frameRight = UseWoodFrame ? FRAME_RIGHT : 1;
        var frameBottom = UseWoodFrame ? FRAME_BOTTOM : 1;

        var bodyInsetLeft = FlushContent ? 0 : frameLeft;
        var bodyInsetRight = FlushContent ? 0 : frameRight;

        var pinX = (ShowClose ? Width - CLOSE_W - PIN_W - 2 : Width - PIN_W - 1) - titleInsetRight;

        TitleBar.Width = Width - titleInsetLeft - titleInsetRight;
        CloseBox.X = Width - titleInsetRight - CLOSE_W - 1;
        TitleLabel.Width = pinX - titleInsetLeft - 7;
        PinBox.X = pinX;
        //the close/pin GLYPH positions follow Width via ApplyGlyph in Update every frame, so they need no update here
        Content.X = bodyInsetLeft;
        Content.Width = Width - bodyInsetLeft - bodyInsetRight;
        Content.Height = Height - (titleInsetTop + TITLE_H) - (FlushContent ? 0 : frameBottom);
        ResizeGrip.X = Width - frameRight - GRIP_SIZE;
        ResizeGrip.Y = Height - frameBottom - GRIP_SIZE;
        OnResized();
    }

    /// <summary>Subclasses override to re-lay-out their content after the window is resized.</summary>
    protected virtual void OnResized() { }

    private bool PointInside(int x, int y)
        => (x >= ScreenX) && (x < ScreenX + Width) && (y >= ScreenY) && (y < ScreenY + Height);

    /// <summary>
    ///     True when the cursor is over this window AND this window (or one of its descendants) is the topmost thing
    ///     under the cursor. Hit-testing from the root respects ZIndex, so a window stacked on top of this one correctly
    ///     prevents it from registering as hovered (and un-fading) while it is covered.
    /// </summary>
    protected bool CursorOnTop()
    {
        var mouseX = InputBuffer.MouseX;
        var mouseY = InputBuffer.MouseY;

        if (!PointInside(mouseX, mouseY))
            return false;

        UIPanel root = this;

        while (root.Parent is not null)
            root = root.Parent;

        var hit = InputDispatcher.HitTest(root, mouseX, mouseY);

        for (var element = hit; element is not null; element = element.Parent)
            if (element == this)
                return true;

        return false;
    }

    /// <summary>
    ///     The bottom-right corner grip. Captures the mouse-down itself (so the content panel under it cannot eat the
    ///     click) and raises <see cref="Pressed" />; the owning window does the actual resize from its Update loop.
    /// </summary>
    private sealed class ResizeHandle : UIPanel
    {
        public event Action<MouseDownEvent>? Pressed;

        public override void OnMouseDown(MouseDownEvent e)
        {
            if (e.Button != MouseButton.Left)
                return;

            Pressed?.Invoke(e);
            e.Handled = true;
        }
    }
}
