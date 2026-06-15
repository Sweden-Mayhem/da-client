#region
using Chaos.Client.Controls.Components;
using Chaos.Client.Data;
using Chaos.Client.Extensions;
using Chaos.Client.Utilities;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
#endregion

namespace Chaos.Client.Controls.Generic;

/// <summary>
///     A popup message dialog with a tiled background, dlgframe border, and an OK button plus an optional Cancel
/// </summary>
public sealed class OkPopupMessageControl : UIPanel
{
    private const int TILES_WIDE = 4;
    private const int INTERIOR_HEIGHT = 54;
    private const int CONTENT_PADDING = 6;
    private const int BUTTON_MARGIN = 1;

    //butt001.epf frame indices, 2 frames per button for normal and pressed
    private const int OK_NORMAL = 15;
    private const int OK_PRESSED = 16;
    private const int CANCEL_NORMAL = 21;
    private const int CANCEL_PRESSED = 22;

    private int ContentHeight;
    private int ContentWidth;
    private int ContentX;
    private int ContentY;

    //fixed width parts, only the height grows to fit the message
    private int InteriorWidth;
    private int TotalWidth;
    private int OkHeight;

    private readonly UILabel MessageLabel;

    public UIButton? CancelButton { get; }
    public UIButton OkButton { get; }

    //true centers on the legacy 640x480 screen the lobby renders into, false centers on the native window
    private readonly bool LegacyCenter;

    public OkPopupMessageControl(bool showCancel = false, bool legacyCenter = false)
    {
        Visible = false;
        UsesControlStack = true;
        LegacyCenter = legacyCenter;

        //fixed width comes from the background tile, only the height grows to fit the message
        using (var bgTile = DataContext.UserControls.GetSpfImage("DlgBack2.spf"))
        {
            if (bgTile is null)
                throw new InvalidOperationException("Failed to load DlgBack2.spf");

            var borderSize = DialogFrame.BORDER_SIZE;
            InteriorWidth = bgTile.Width * TILES_WIDE - 23;
            TotalWidth = borderSize + InteriorWidth + borderSize;
        }

        var cache = UiRenderer.Instance!;
        var okNormalTex = cache.GetEpfTexture("butt001.epf", OK_NORMAL);
        var okPressedTex = cache.GetEpfTexture("butt001.epf", OK_PRESSED);

        var okWidth = okNormalTex.Width;
        OkHeight = okNormalTex.Height;
        var rightButtonX = TotalWidth - DialogFrame.BORDER_SIZE - BUTTON_MARGIN + 4;
        int okX;

        //optional cancel button on the right, Y is set by BuildBox once the height is known
        if (showCancel)
        {
            var cancelNormalTex = cache.GetEpfTexture("butt001.epf", CANCEL_NORMAL);
            var cancelPressedTex = cache.GetEpfTexture("butt001.epf", CANCEL_PRESSED);

            var cancelWidth = cancelNormalTex.Width;

            CancelButton = new UIButton
            {
                Name = "Cancel",
                X = rightButtonX - cancelWidth,
                Width = cancelWidth,
                Height = cancelNormalTex.Height,
                NormalTexture = cancelNormalTex,
                PressedTexture = cancelPressedTex
            };
            CancelButton.Clicked += () => OnCancel?.Invoke();
            AddChild(CancelButton);

            okX = CancelButton.X - okWidth - BUTTON_MARGIN;
        } else
            okX = rightButtonX - okWidth;

        //ok button sits left of cancel, or slides right when cancel is absent
        OkButton = new UIButton
        {
            Name = "OK",
            X = okX,
            Width = okWidth,
            Height = OkHeight,
            NormalTexture = okNormalTex,
            PressedTexture = okPressedTex
        };
        OkButton.Clicked += () => OnOk?.Invoke();
        AddChild(OkButton);

        MessageLabel = new UILabel
        {
            WordWrap = true,
            ForegroundColor = Color.White,
            VerticalAlignment = VerticalAlignment.Top,
            //crisp TTF at native res, this popup is hosted in a magnifier
            CustomFontSize = 12,
            RenderNative = true
        };
        AddChild(MessageLabel);

        BuildBox(INTERIOR_HEIGHT);
    }

    //rebuilds the box at a given interior height, placing the content area, button row and message label
    //called on construction and again per message so tall text gets a taller box
    private void BuildBox(int interiorHeight)
    {
        var borderSize = DialogFrame.BORDER_SIZE;
        var totalHeight = borderSize + interiorHeight + borderSize;

        using var bgTile = DataContext.UserControls.GetSpfImage("DlgBack2.spf");

        if (bgTile is null)
            return;

        using var composite = DialogFrame.Composite(bgTile, TotalWidth, totalHeight);

        if (composite is null)
            return;

        Width = TotalWidth;
        Height = totalHeight;
        var previous = Background;
        Background = TextureConverter.ToTexture2D(composite);
        previous?.Dispose();

        ContentX = borderSize + CONTENT_PADDING;
        ContentY = borderSize + CONTENT_PADDING;
        ContentWidth = InteriorWidth - CONTENT_PADDING * 2;
        ContentHeight = interiorHeight - CONTENT_PADDING * 2;

        var buttonY = totalHeight - borderSize - OkHeight - BUTTON_MARGIN + 5;
        OkButton.Y = buttonY;

        if (CancelButton is not null)
            CancelButton.Y = buttonY;

        MessageLabel.X = ContentX - 9;
        MessageLabel.Y = ContentY - 10;
        MessageLabel.Width = ContentWidth;
        //keep the text above the button so a long message can never draw under it
        MessageLabel.Height = Math.Max(CONTENT_PADDING, buttonY - MessageLabel.Y);
    }

    public void Hide()
    {
        InputDispatcher.Instance!.RemoveControl(this);
        Visible = false;
        MessageLabel.Text = string.Empty;
    }

    public event CancelHandler? OnCancel;

    public event OkHandler? OnOk;

    public void Show(string message)
    {
        MessageLabel.Text = message;

        //grow the box so the whole wrapped message fits above the button row, only the height changes
        var font = MessageLabel.CustomFontSize;
        var lineHeight = TtfTextRenderer.Available ? TtfTextRenderer.LineHeight(font) : 16;
        var lineCount = TtfTextRenderer.Available
            ? Math.Max(1, TtfTextRenderer.WrapText(message, ContentWidth, font).Count)
            : 1;
        var textHeight = lineCount * lineHeight;

        //text height the default box fits above the button, short messages stay default size
        //a longer one grows the box by the overflow so it never spills under the button
        var defaultRoom = INTERIOR_HEIGHT - OkHeight - BUTTON_MARGIN - CONTENT_PADDING + 15;
        var interior = textHeight <= defaultRoom ? INTERIOR_HEIGHT : INTERIOR_HEIGHT + (textHeight - defaultRoom) + 4;
        BuildBox(interior);

        //center using the current size, native window for world popups and legacy 640x480 for the lobby
        if (LegacyCenter)
            this.CenterOnScreen();
        else
            this.CenterOnUi();

        InputDispatcher.Instance!.PushControl(this);
        Visible = true;
    }

    /// <summary>
    ///     Shows the dialog as a one-off confirm, onOk runs on OK and onCancel on Cancel, and it closes itself either way
    ///     Replaces any previous handlers so a single shared dialog can be reused for different questions
    /// </summary>
    public void Confirm(string message, Action onOk, Action? onCancel = null)
    {
        OnOk = null;
        OnCancel = null;
        OnOk += () => { Hide(); onOk(); };
        OnCancel += () => { Hide(); onCancel?.Invoke(); };

        Show(message);
    }

    public override void OnKeyDown(KeyDownEvent e)
    {
        if (e.Key is Keys.Enter or Keys.Space)
        {
            OkButton.PerformClick();
            e.Handled = true;
        } else if (e.Key == Keys.Escape)
        {
            if (CancelButton is not null)
                CancelButton.PerformClick();
            else
                OkButton.PerformClick();

            e.Handled = true;
        }
    }
}