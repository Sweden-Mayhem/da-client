#region
using Chaos.Client.Controls.Components;
using Chaos.Client.Data;
using Chaos.Client.Rendering;
using Chaos.Client.Utilities;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
#endregion

namespace Chaos.Client.Controls.Generic;

/// <summary>
///     Popup text window for the ScrollWindow, NonScrollWindow and WoodenBoard message types
///     Scroll and NonScroll use a dialog frame, WoodenBoard uses a wooden plank background, all support wheel scrolling
/// </summary>
public sealed class TextPopupControl : UIPanel, INativeTextDrawer
{
    //original client rect, position 140,60 and size 360x180
    private const int POPUP_X = 140;
    private const int POPUP_Y = 60;
    private const int POPUP_WIDTH = 360;
    private const int POPUP_HEIGHT = 180;

    //dialog frame text insets (16px dlgframe border)
    private const int FRAME_INSET = DialogFrame.BORDER_SIZE;

    //wooden board text insets, top and bottom kept equal so the text block centers on the board
    private const int WOOD_INSET_X = 16;
    private const int WOOD_INSET_TOP = 12;
    private const int WOOD_INSET_BOTTOM = WOOD_INSET_TOP;

    //a click anywhere dismisses the popup, but only after it has been open this long
    //stops a player mid-click when a sign opens from closing it before reading anything
    private const float CLICK_DISMISS_DELAY_SECONDS = 0.25f;

    private readonly ScrollBarControl Scrollbar;

    private readonly UILabel TextLabel;
    private Texture2D? DialogBackground;
    private Texture2D? WoodBackground;

    //body text for native TTF rendering, used in WoodenBoard style when TTF is available
    private string BodyText = string.Empty;
    private List<string> CachedLines = [];
    private float CachedLinesScale;
    private int CachedFontSize;

    //seconds since Show, gates the click-to-dismiss
    private float OpenSeconds;
    public bool IsWooden { get; private set; }

    /// <summary>True once the popup has been open long enough that a click dismisses it</summary>
    public bool ReadyToDismiss => OpenSeconds >= CLICK_DISMISS_DELAY_SECONDS;

    public TextPopupControl()
    {
        Name = "TextPopup";
        Visible = false;
        UsesControlStack = true;
        X = POPUP_X;
        Y = POPUP_Y;
        Width = POPUP_WIDTH;
        Height = POPUP_HEIGHT;

        TextLabel = new UILabel
        {
            WordWrap = true,
            ForegroundColor = Color.White
        };
        AddChild(TextLabel);

        //scrollbar on the right, there is no close button so any click after the open delay dismisses the popup
        Scrollbar = new ScrollBarControl
        {
            X = POPUP_WIDTH - FRAME_INSET - ScrollBarControl.DEFAULT_WIDTH,
            Y = FRAME_INSET,
            Height = POPUP_HEIGHT - FRAME_INSET * 2,
            Visible = false
        };
        Scrollbar.OnValueChanged += value => TextLabel.ScrollOffset = value * TextRenderer.CHAR_HEIGHT;
        AddChild(Scrollbar);

        LoadBackgrounds();
    }

    public void Hide()
    {
        InputDispatcher.Instance?.RemoveControl(this);
        Visible = false;
        OnClose?.Invoke();
    }

    private void LoadBackgrounds()
    {
        //dialog frame, dlgback2.spf tiled background with the dlgframe.epf 8-piece border
        using var bgTile = DataContext.UserControls.GetSpfImage("DlgBack2.spf");

        if (bgTile is not null)
        {
            using var composite = DialogFrame.Composite(bgTile, POPUP_WIDTH, POPUP_HEIGHT);

            if (composite is not null)
                DialogBackground = TextureConverter.ToTexture2D(composite);
        }

        //wooden board, woodbk.epf from legend.dat at an exact 360x180
        using var woodImage = DataContext.UserControls.GetLegendEpfImage("woodbk.epf");

        if (woodImage is not null)
            WoodBackground = TextureConverter.ToTexture2D(woodImage);
    }

    public event CloseHandler? OnClose;

    /// <summary>
    ///     Shows a popup with the given text
    ///     Scroll and NonScroll use the dialog frame, WoodenBoard uses the wooden plank background
    /// </summary>
    public void Show(string text, PopupStyle style = PopupStyle.Scroll)
    {
        IsWooden = style == PopupStyle.Wooden;
        var useTtf = IsWooden && TtfTextRenderer.Available;
        var contentHeight = Scrollbar.Height;

        if (IsWooden)
        {
            Background = WoodBackground;
            TextLabel.X = WOOD_INSET_X;
            TextLabel.Y = WOOD_INSET_TOP;
            TextLabel.Width = POPUP_WIDTH - WOOD_INSET_X * 2;
            contentHeight = POPUP_HEIGHT - WOOD_INSET_TOP - WOOD_INSET_BOTTOM;
            TextLabel.Height = contentHeight;
            Scrollbar.Visible = false;
            //hide the bitmap label when TTF will draw natively
            TextLabel.Visible = !useTtf;
        } else
        {
            //scroll and nonscroll look the same
            Background = DialogBackground;
            TextLabel.X = FRAME_INSET;
            TextLabel.Y = FRAME_INSET;
            TextLabel.Width = POPUP_WIDTH - FRAME_INSET * 2 - ScrollBarControl.DEFAULT_WIDTH;
            TextLabel.Height = contentHeight;
            TextLabel.Visible = true;
            Scrollbar.Visible = true;
        }

        BodyText = text;
        CachedLines = [];
        CachedLinesScale = 0f;

        if (!useTtf)
        {
            TextLabel.ScrollOffset = 0;
            TextLabel.Text = text;

            var visibleLines = contentHeight / TextRenderer.CHAR_HEIGHT;
            var totalLines = TextLabel.ContentHeight / TextRenderer.CHAR_HEIGHT;
            var scrollMax = Math.Max(0, totalLines - visibleLines);
            Scrollbar.Value = 0;
            Scrollbar.MaxValue = scrollMax;
            Scrollbar.TotalItems = totalLines;
            Scrollbar.VisibleItems = visibleLines;
        }

        //X and Y stay at 0, the wrapping host centers it on screen
        X = 0;
        Y = 0;
        OpenSeconds = 0f;

        InputDispatcher.Instance?.PushControl(this);
        Visible = true;
    }

    public override void Update(GameTime gameTime)
    {
        if (Visible)
            OpenSeconds += (float)gameTime.ElapsedGameTime.TotalSeconds;

        base.Update(gameTime);
    }

    public override void OnKeyDown(KeyDownEvent e)
    {
        if (e.Key is Keys.Escape or Keys.Space or Keys.Enter)
        {
            Hide();
            e.Handled = true;
        }
    }

    //the press only arms, the dismiss runs on the click and handles it
    //that way the same event chain can never fall through and re-click the sign in the world
    public override void OnMouseDown(MouseDownEvent e) => e.Handled = true;

    public override void OnClick(ClickEvent e)
    {
        if (ReadyToDismiss)
            Hide();

        e.Handled = true;
    }

    public override void OnMouseScroll(MouseScrollEvent e)
    {
        if (TextLabel.ContentHeight <= TextLabel.Height)
            return;

        var visibleLines = TextLabel.Height / TextRenderer.CHAR_HEIGHT;
        var totalLines = TextLabel.ContentHeight / TextRenderer.CHAR_HEIGHT;
        var maxScroll = Math.Max(0, totalLines - visibleLines);
        var newValue = Math.Clamp(Scrollbar.Value - e.Delta, 0, maxScroll);

        if (newValue == Scrollbar.Value)
            return;

        Scrollbar.Value = newValue;
        TextLabel.ScrollOffset = newValue * TextRenderer.CHAR_HEIGHT;
        e.Handled = true;
    }

    /// <summary>
    ///     Draws the wooden board body text at native resolution
    ///     Only used in WoodenBoard style when the TTF renderer is available
    /// </summary>
    public void DrawNativeText(SpriteBatchEx spriteBatch, int originX, int originY, int nativeOriginX, int nativeOriginY, float scale, float alpha)
    {
        if (!IsWooden || !TtfTextRenderer.Available || (scale <= 0f) || (alpha <= 0f) || string.IsNullOrEmpty(BodyText))
            return;

        //wrap a little narrower than the board inset so the sign face never kisses the wooden edges
        var nativeWidth = Math.Max(1, (int)((TextLabel.Width - 12) * scale));
        var areaH = (int)(TextLabel.Height * scale);

        //auto-size, the font starts large and steps down until the wrapped block fits the board
        //a short notice stays big and readable while a long one still fits
        if ((Math.Abs(CachedLinesScale - scale) > 0.01f) || (CachedLines.Count == 0 && BodyText.Length > 0))
        {
            var size = Math.Max(1, (int)MathF.Round(18 * scale));
            var minSize = Math.Max(1, (int)MathF.Round(10 * scale));

            while (true)
            {
                CachedLines = TtfTextRenderer.WrapText(BodyText, nativeWidth, size, FontKind.Sign);

                if ((size <= minSize) || (CachedLines.Count * TtfTextRenderer.LineHeight(size, FontKind.Sign) <= areaH))
                    break;

                size -= 2;
            }

            CachedFontSize = size;
            CachedLinesScale = scale;
        }

        var font = CachedFontSize;
        var lh = TtfTextRenderer.LineHeight(font, FontKind.Sign);
        var x = originX + (int)(TextLabel.X * scale);
        var blockH = CachedLines.Count * lh;

        //the text block floats centered on the board, clamped to the top when it overfills
        var y = originY + (int)(TextLabel.Y * scale) + Math.Max(0, (areaH - blockH) / 2);

        //etched look, a dark shadow up-left and a warm bright one down-right with a near-white body between
        var etch = new Vector2(Math.Max(2, (int)MathF.Round(1.6f * scale)));
        var brightEtch = etch - new Vector2(1f, 1f);              //lit rim sits a pixel tighter to the cut
        var darkCol = new Color(24, 14, 7) * (0.85f * alpha);      //shadowed wall of the carve
        var brightCol = new Color(240, 205, 160) * (0.55f * alpha); //lit rim of the cut
        var bodyCol = new Color(245, 238, 226) * alpha;            //near-white lettering

        for (var i = 0; i < CachedLines.Count; i++)
        {
            var line = CachedLines[i];

            if (line.Length == 0)
                continue;

            var tex = TtfTextRenderer.GetLine(line, font, FontKind.Sign);

            if (tex is null)
                continue;

            //each line centered against the full inset width, the wrap width is narrower on purpose
            var p = new Vector2(x + Math.Max(0, ((int)(TextLabel.Width * scale) - tex.Width) / 2), y + i * lh);
            spriteBatch.Draw(tex, p + brightEtch, brightCol);
            spriteBatch.Draw(tex, p - etch, darkCol);
            spriteBatch.Draw(tex, p, bodyCol);
        }
    }
}