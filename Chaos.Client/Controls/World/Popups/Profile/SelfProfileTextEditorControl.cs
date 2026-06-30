#region
using Chaos.Client.Controls.Components;
using Chaos.Client.Data;
using Chaos.Client.Extensions;
using Chaos.Client.Utilities;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
#endregion

namespace Chaos.Client.Controls.World.Popups.Profile;

/// <summary>
///     Screen-centered popup editor for the player's profile text. Uses DialogFrame compositing for the border/background.
///     Contains a multiline UITextBox for editing and OK/Cancel buttons. OK saves and fires OnSave; Cancel discards
///     changes.
/// </summary>
public sealed class SelfProfileTextEditorControl : UIPanel
{
    private const int TOTAL_WIDTH = 200;
    private const int TOTAL_HEIGHT = 200;
    private const int INSET_LEFT = 13;
    private const int INSET_RIGHT = 13;
    private const int INSET_TOP = 9;
    private const int INSET_BOTTOM = 6;

    //butt001.epf frame indices, 2 frames per button (normal/pressed)
    private const int OK_NORMAL = 15;
    private const int OK_PRESSED = 16;
    private const int CANCEL_NORMAL = 21;
    private const int CANCEL_PRESSED = 22;

    private const int BUTTON_GAP = 2;

    private readonly UIButton CancelButton;
    private readonly UIButton OkButton;
    private readonly UITextBox TextBox;

    public SelfProfileTextEditorControl()
    {
        Name = "ProfileTextEditor";
        Visible = false;
        UsesControlStack = true;
        IsModal = true;
        Width = TOTAL_WIDTH;
        Height = TOTAL_HEIGHT;

        //composite background with dialogframe border
        using var bgTile = DataContext.UserControls.GetSpfImage("DlgBack2.spf");

        if (bgTile is not null)
        {
            using var composite = DialogFrame.Composite(bgTile, TOTAL_WIDTH, TOTAL_HEIGHT);

            if (composite is not null)
                Background = TextureConverter.ToTexture2D(composite);
        }

        //button textures from butt001.epf
        var cache = UiRenderer.Instance!;
        var okNormalTex = cache.GetEpfTexture("butt001.epf", OK_NORMAL);
        var okPressedTex = cache.GetEpfTexture("butt001.epf", OK_PRESSED);
        var cancelNormalTex = cache.GetEpfTexture("butt001.epf", CANCEL_NORMAL);
        var cancelPressedTex = cache.GetEpfTexture("butt001.epf", CANCEL_PRESSED);

        var btnHeight = okNormalTex.Height;
        var buttonY = TOTAL_HEIGHT - INSET_BOTTOM - btnHeight;

        //ok button, bottom-left
        OkButton = UIButton.CreateWithTexture("OK", okNormalTex, okPressedTex);
        OkButton.X = INSET_LEFT;
        OkButton.Y = buttonY;
        OkButton.Clicked += Confirm;
        AddChild(OkButton);

        //cancel button, bottom-right
        CancelButton = UIButton.CreateWithTexture("Cancel", cancelNormalTex, cancelPressedTex);
        CancelButton.X = TOTAL_WIDTH - INSET_RIGHT - cancelNormalTex.Width;
        CancelButton.Y = buttonY;
        CancelButton.Clicked += Cancel;
        AddChild(CancelButton);

        //multiline textbox fills the area between top inset and buttons
        var textBoxHeight = buttonY - BUTTON_GAP - INSET_TOP;

        TextBox = new UITextBox
        {
            Name = "Editor",
            X = INSET_LEFT,
            Y = INSET_TOP,
            Width = TOTAL_WIDTH - INSET_LEFT - INSET_RIGHT,
            Height = textBoxHeight,
            IsMultiLine = true,        //soft-wraps long text across lines...
            AllowNewlines = false,     //...but it stays one paragraph - Enter saves instead of adding a line break
            ClampToVisibleArea = true,
            BackgroundColor = Color.Black,
            FocusedBackgroundColor = Color.Black,
            ForegroundColor = Color.White,
            MaxLength = 85
        }.Native(11); //magnified by the editor's ScaleHost, so paint the body in crisp native TTF
        AddChild(TextBox);

        //placement is owned by the wrapping ScaleHost (it centers + magnifies), so the editor does NOT self-center.

        //if the editor's field is defocused from outside (e.g. clicking the book's close box or Menu while it is open),
        //treat that as Cancel so the editor never lingers focus-less on screen and trap input.
        UITextBox.TextBoxFocusLost += OnTextBoxFocusLost;
    }

    private void OnTextBoxFocusLost(UITextBox textBox)
    {
        //Hide() clears Visible before blurring, so this is inert for our own close path and only fires on external
        //defocus (a click that landed outside the editor while it was open).
        if ((textBox == TextBox) && Visible)
            Cancel();
    }

    private void Cancel() => Hide();

    private void Confirm()
    {
        //defensive: one line, no stray newlines, capped at 85 (the box already enforces this, but never trust it blindly)
        var text = TextBox.Text.Replace("\r", " ").Replace("\n", " ").Trim();

        if (text.Length > 85)
            text = text[..85];

        OnSave?.Invoke(text);
        Hide();
    }

    public override void Dispose()
    {
        UITextBox.TextBoxFocusLost -= OnTextBoxFocusLost;
        Background?.Dispose();
        Background = null;

        base.Dispose();
    }

    public void Hide()
    {
        //clear Visible FIRST so the TextBoxFocusLost handler (fired by the blur below) sees us as already closed and
        //does not re-enter Cancel.
        Visible = false;
        InputDispatcher.Instance?.RemoveControl(this);
        TextBox.IsFocused = false;
    }

    public event ProfileTextSavedHandler? OnSave;

    public void Show(string text, int wrapWidth = 0, int fontSize = 0)
    {
        //match the in-book Presentation label's EFFECTIVE wrap width + font so the editor previews the EXACT same line
        //breaks. wrapWidth is the label's inner width (already minus its padding), so add this box's own padding back to
        //get the outer width that yields the same inner wrap, then CENTER the (often narrower) text column in the popup.
        if (wrapWidth > 0)
        {
            TextBox.Width = Math.Min(wrapWidth + TextBox.PaddingLeft + TextBox.PaddingRight, TOTAL_WIDTH - INSET_LEFT - INSET_RIGHT);
            TextBox.X = (TOTAL_WIDTH - TextBox.Width) / 2;
        }

        if (fontSize > 0)
            TextBox.CustomFontSize = fontSize;

        TextBox.Text = text;
        TextBox.CursorPosition = 0;
        TextBox.ScrollOffset = 0;
        TextBox.IsFocused = true;
        InputDispatcher.Instance?.PushControl(this);
        Visible = true;
    }

    public override void OnKeyDown(KeyDownEvent e)
    {
        if (e.Key == Keys.Escape)
        {
            Cancel();
            e.Handled = true;
        } else if (e.Key == Keys.Enter)
        {
            //the field forbids line breaks, so Enter submits the profile text
            Confirm();
            e.Handled = true;
        }
    }
}