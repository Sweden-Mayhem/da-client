#region
using Chaos.Client.Controls.Components;
using Chaos.Client.Systems;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
#endregion

namespace Chaos.Client.Controls.World.Popups.Dialog;

/// <summary>
///     Floating option menu panel for NPC dialog/menu interactions. Uses the shared ornate frame with nd_n00/01/02 3-slice
///     option row stripes. Dynamic width, right-aligned and bottom-anchored above the dialog bar. Used for DialogMenu,
///     CreatureMenu, Menu, and MenuWithArgs.
/// </summary>
public sealed class DialogOptionPanel : FramedDialogPanelBase
{
    //dialog choices use the optional TrueType (Cinzel) font (drawn at native res by DrawTextNative, like the dialog text)
    private const int OPTION_FONT_SIZE = 10;

    private const int MIN_STRIPE_WIDTH = 185;
    private const int ROW_HEIGHT = 13;
    private const int STRIPE_GAP = 5;
    private const int TEXT_PADDING_H = 10;
    private const int BTN_WIDTH = 61;
    private const int BTN_HEIGHT = 22;

    //border thickness from edge tiles (content measured from inside of these)
    private const int BORDER_TOP = 6;
    private const int BORDER_LEFT = 13;
    private const int BORDER_RIGHT = 31;
    private const int BORDER_BOTTOM = 47;

    //content padding from inside of border to stripes
    private const int CONTENT_PADDING_TOP = 2;
    private const int CONTENT_PADDING_BOTTOM = -16;
    private const int CONTENT_PADDING_LEFT = 7;
    private const int CONTENT_PADDING_RIGHT = -11;

    //bottom of panel aligns with top of the dialog bottom bar
    private const int BOTTOM_ANCHOR_Y = 372;

    private readonly List<OptionLabel> OptionLabels = [];

    //3-slice stripe pieces
    private Texture2D? StripeLeft;
    private Texture2D? StripeLeftOn;
    private Texture2D? StripeMid;
    private Texture2D? StripeMidOn;
    private Texture2D? StripeRight;
    private Texture2D? StripeRightOn;
    private bool StripesLoaded;

    public DialogOptionPanel()
        : base("lnpcd2", false)
    {
        Name = "OptionMenu";
        Visible = false;
        
        OkButton = CreateButton("Btn1");

        //options are picked by clicking the stripe directly, so the OK button is always disabled here, hide it rather
        //than show a permanently-greyed button (it would re-show only if something enabled it again)
        if (OkButton is not null)
        {
            OkButton.Enabled = false;
            OkButton.Visible = false;
        }
    }

    private void ClearOptionLabels()
    {
        foreach (var label in OptionLabels)
        {
            Children.Remove(label);
            label.Dispose();
        }

        OptionLabels.Clear();
    }

    public override void Draw(SpriteBatchEx spriteBatch)
    {
        if (!Visible)
            return;

        EnsureStripeTextures();

        //frame + children drawn by base
        base.Draw(spriteBatch);
    }

    private void EnsureStripeTextures()
    {
        if (StripesLoaded)
            return;

        StripesLoaded = true;
        var renderer = UiRenderer.Instance;

        if (renderer is null)
            return;

        StripeLeft = renderer.GetSpfTexture("nd_n00.spf");
        StripeMid = renderer.GetSpfTexture("nd_n01.spf");
        StripeRight = renderer.GetSpfTexture("nd_n02.spf");
        StripeLeftOn = renderer.GetSpfTexture("nd_n00on.spf");
        StripeMidOn = renderer.GetSpfTexture("nd_n01on.spf");
        StripeRightOn = renderer.GetSpfTexture("nd_n02on.spf");
    }

    public int OptionCount => OptionLabels.Count;

    public ushort GetOptionPursuitId(int index)
    {
        if ((index < 0) || (index >= OptionLabels.Count))
            return 0;

        return OptionLabels[index].PursuitId;
    }

    public override void Hide()
    {
        //NOTE: do NOT clear the option labels here. On close they must persist so DrawTextNative can fade the option TEXT
        //out with the rest of the dialog (this panel hides immediately, but the host's fade lingers). ShowOptions clears
        //them before repopulating, so a new menu never inherits stale options.
        base.Hide();
    }

    public event CloseHandler? OnClose;

    public event OptionSelectedHandler? OnOptionSelected;

    public void ShowOptions(IReadOnlyList<(string Text, ushort Pursuit)> options)
    {
        ClearOptionLabels();
        EnsureStripeTextures();

        //dynamic width from longest option text
        var maxTextWidth = 0;

        foreach ((var text, _) in options)
        {
            var textWidth = TextRenderer.MeasureWidth(text);

            if (textWidth > maxTextWidth)
                maxTextWidth = textWidth;
        }

        var stripeWidth = maxTextWidth + TEXT_PADDING_H * 2;
        stripeWidth = Math.Max(stripeWidth, MIN_STRIPE_WIDTH);

        var stripeLeft = BORDER_LEFT + CONTENT_PADDING_LEFT;
        var stripeRight = BORDER_RIGHT + CONTENT_PADDING_RIGHT;

        Width = stripeWidth + stripeLeft + stripeRight;

        //dynamic height: top border + padding + stripes + padding + bottom border
        var stripesHeight = options.Count * (ROW_HEIGHT + STRIPE_GAP) - STRIPE_GAP;
        Height = BORDER_TOP + CONTENT_PADDING_TOP + stripesHeight + CONTENT_PADDING_BOTTOM + BORDER_BOTTOM;

        //right-aligned, bottom-anchored above dialog bar
        X = ChaosGame.VIRTUAL_WIDTH - Width;
        Y = BOTTOM_ANCHOR_Y - Height;

        //create option label children
        for (var i = 0; i < options.Count; i++)
        {
            (var text, var pursuit) = options[i];
            var index = i;

            //SWM quest markers: a trailing " !" = a TURN-IN (amber "!"), " ?" = a GIVE (green "?"). Strip it; the
            //panel draws the pulsing marker on the far side instead.
            var marker = text.EndsWith(" !", StringComparison.Ordinal) ? '!' : text.EndsWith(" ?", StringComparison.Ordinal) ? '?' : '\0';
            var labelText = marker != '\0' ? text[..^2].TrimEnd() : text;

            var label = new OptionLabel(
                labelText,
                marker,
                pursuit,
                StripeLeft,
                StripeMid,
                StripeRight,
                StripeLeftOn,
                StripeMidOn,
                StripeRightOn)
            {
                Name = $"Option_{i}",
                X = BORDER_LEFT + CONTENT_PADDING_LEFT,
                Y = BORDER_TOP + CONTENT_PADDING_TOP + i * (ROW_HEIGHT + STRIPE_GAP),
                Width = stripeWidth,
                Height = ROW_HEIGHT
            };

            label.Clicked += () =>
            {
                SoundSystem.PlayUiClick(); //dialog choices play the same button-press cue as the menu buttons
                OnOptionSelected?.Invoke(index);
            };
            OptionLabels.Add(label);
            AddChild(label);
        }

        //ok button: 20px from right outer edge, 4px from bottom outer edge, shown as disabled
        if (OkButton is not null)
        {
            OkButton.X = Width - BTN_WIDTH - 20;
            OkButton.Y = Height - BTN_HEIGHT - 3;
        }

        Visible = true;
    }

    public override void OnKeyDown(KeyDownEvent e)
    {
        if (e.Key == Keys.Escape)
        {
            Hide();
            OnClose?.Invoke();
            e.Handled = true;
        }
    }

    /// <summary>
    ///     Draws each option's TEXT at NATIVE resolution, centered over its (scaled) stripe, so the choices stay crisp.
    ///     Called from NpcSessionControl.DrawTextNative. origin + scale are the dialog host's on-screen origin/magnification;
    ///     each option's normal screen rect is remapped through them. alpha fades the text with the host's open fade.
    /// </summary>
    public void DrawTextNative(SpriteBatchEx spriteBatch, int originX, int originY, float scale, float alpha)
    {
        //NOTE: not gated on this.Visible - the caller (NpcSessionControl) decides whether to draw, so the option text can
        //keep drawing (and fading) during the dialog's close fade even though this panel hides itself immediately on close.
        if (!TtfTextRenderer.Available)
            return;

        var font = Math.Max(1, (int)MathF.Round(OPTION_FONT_SIZE * scale));
        var lineH = TtfTextRenderer.LineHeight(font);
        var shadowPx = Math.Max(1, (int)MathF.Round(scale));

        foreach (var opt in OptionLabels)
        {
            if (!opt.Visible || string.IsNullOrEmpty(opt.Text))
                continue;

            var tex = TtfTextRenderer.GetLine(opt.Text, font);

            if (tex is null)
                continue;

            var rx = originX + (int)((opt.ScreenX - originX) * scale);
            var ry = originY + (int)((opt.ScreenY - originY) * scale);
            var rw = (int)(opt.Width * scale);
            var rh = (int)(opt.Height * scale);
            var pos = new Vector2(rx + (rw - tex.Width) / 2, ry + (rh - lineH) / 2);

            //emboss: dark drop shadow down-right, then the white text (GetLine renders white glyphs, so a black tint = shadow)
            spriteBatch.Draw(tex, pos + new Vector2(shadowPx, shadowPx), Color.Black * (0.5f * alpha));
            spriteBatch.Draw(tex, pos, Color.White * alpha);

            //SWM rule 5: a quest option gets a PULSING marker offset to the far-right edge - amber "!" for a turn-in
            //(ready to hand in), green "?" for a give (available to start).
            if (opt.IsQuest)
            {
                var glyph = opt.Marker.ToString();
                var mark = TtfTextRenderer.GetLine(glyph, font);

                if (mark is not null)
                {
                    //pulse the brightness with a sine on wall-clock time (no dt needed at draw time)
                    var pulse = 0.55f + 0.45f * (0.5f + 0.5f * MathF.Sin(Environment.TickCount64 * 0.006f));
                    var color = opt.Marker == '?' ? new Color(110, 220, 110) : new Color(255, 190, 60);
                    var mx = rx + rw - mark.Width - (int)(10 * scale);
                    var my = ry + (rh - lineH) / 2;
                    spriteBatch.Draw(mark, new Vector2(mx + shadowPx, my + shadowPx), Color.Black * (0.5f * alpha));
                    spriteBatch.Draw(mark, new Vector2(mx, my), color * (alpha * pulse));
                }
            }
        }
    }

    /// <summary>
    ///     A single clickable text option. Draws a 3-slice dark stripe background (nd_n00 left + nd_n01 tiled + nd_n02 right)
    ///     with centered text.
    /// </summary>
    private sealed class OptionLabel : UIElement
    {
        private readonly Texture2D? StripeLeft;
        private readonly Texture2D? StripeLeftOn;
        private readonly Texture2D? StripeMid;
        private readonly Texture2D? StripeMidOn;
        private readonly Texture2D? StripeRight;
        private readonly Texture2D? StripeRightOn;
        private bool IsHovered;
        private bool IsPressed;
        public ushort PursuitId { get; }
        public string Text { get; }

        //SWM: a quest-related option carries a marker char ('!' turn-in / '?' give, '\0' = none). It always shows the
        //lit "on" stripe and a far-right pulsing marker so quests stand out in an NPC's menu (rule 5).
        public char Marker { get; }
        public bool IsQuest => Marker != '\0';

        public OptionLabel(
            string text,
            char marker,
            ushort pursuitId,
            Texture2D? stripeLeft,
            Texture2D? stripeMid,
            Texture2D? stripeRight,
            Texture2D? stripeLeftOn,
            Texture2D? stripeMidOn,
            Texture2D? stripeRightOn)
        {
            Text = text;
            Marker = marker;
            PursuitId = pursuitId;
            StripeLeft = stripeLeft;
            StripeMid = stripeMid;
            StripeRight = stripeRight;
            StripeLeftOn = stripeLeftOn;
            StripeMidOn = stripeMidOn;
            StripeRightOn = stripeRightOn;
        }

        public override void Draw(SpriteBatchEx spriteBatch)
        {
            if (!Visible)
                return;

            base.Draw(spriteBatch);

            //pressed state shifts down 1px and contracts 1px on each side. expand ClipRect
            //downward so the stripe textures' bottom border row (now at sy+textureHeight)
            //isn't clipped by the OptionLabel's nominal bounds, otherwise the BL/BR ends
            //of the bottom border vanish (StripeMid uses TileTexture which bypasses ClipRect,
            //but DrawTexture for left/right pieces clips to ClipRect)
            if (IsPressed)
                ClipRect.Height += 1;

            var sx = ScreenX + (IsPressed ? 1 : 0);
            var sy = ScreenY + (IsPressed ? 1 : 0);
            var drawWidth = Width - (IsPressed ? 2 : 0);

            //draw 3-slice stripe background - a quest option is always LIT (the "on" stripe), like a permanent hover
            var on = IsHovered || IsQuest;
            var left = on ? StripeLeftOn ?? StripeLeft : StripeLeft;
            var mid = on ? StripeMidOn ?? StripeMid : StripeMid;
            var right = on ? StripeRightOn ?? StripeRight : StripeRight;

            var leftW = left?.Width ?? 0;
            var rightW = right?.Width ?? 0;
            var midWidth = drawWidth - leftW - rightW;

            if (left is not null)
                DrawTexture(
                    spriteBatch,
                    left,
                    new Vector2(sx, sy),
                    Color.White);

            if (mid is not null)
                TileTexture(
                    spriteBatch,
                    mid,
                    sx + leftW,
                    sy,
                    midWidth,
                    mid.Height);

            if (right is not null)
                DrawTexture(
                    spriteBatch,
                    right,
                    new Vector2(sx + drawWidth - rightW, sy),
                    Color.White);

            //the option TEXT is NOT drawn here - it is drawn crisp at native resolution by DialogOptionPanel.DrawTextNative
            //(on top of this scaled stripe art), so it doesn't get magnified/softened with the rest of the dialog frame.
        }

        public event ClickedHandler? Clicked;

        public override void OnMouseEnter()
        {
            IsHovered = true;
        }

        public override void OnMouseLeave()
        {
            IsHovered = false;
            IsPressed = false;
        }

        public override void OnMouseDown(MouseDownEvent e)
        {
            if (e.Button == MouseButton.Left)
            {
                IsPressed = true;
                e.Handled = true;
            }
        }

        public override void OnMouseUp(MouseUpEvent e)
        {
            if (e.Button == MouseButton.Left)
                IsPressed = false;
        }

        public override void OnClick(ClickEvent e)
        {
            Clicked?.Invoke();
            e.Handled = true;
        }
    }
}