#region
using Chaos.Client.Controls.Components;
using Chaos.Client.Controls.Generic;
using Chaos.Client.Utilities;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
#endregion

namespace Chaos.Client.Controls.LobbyLogin;

public sealed class LoginNoticeControl : PrefabPanel
{
    private readonly UILabel? AgreementTextLabel;
    private readonly ScrollBarControl? ScrollBar;
    private readonly int TextAreaHeight;
    private readonly int TextAreaWidth;
    public UIButton? CancelButton { get; }

    public UIButton? OkButton { get; }

    public LoginNoticeControl()
        : base("_nagree")
    {
        Name = "AgreePanel";
        Visible = false;
        UsesControlStack = true;

        //buttons, _nagree is one of the few prefabs with actual type 3 controls
        OkButton = CreateButton("OK");
        CancelButton = CreateButton("CANCEL");

        if (OkButton is not null)
            OkButton.Clicked += () => OnOk?.Invoke();

        if (CancelButton is not null)
            CancelButton.Clicked += () => OnCancel?.Invoke();

        //agreement text display region, type 7, 0 images
        var textRect = GetRect("AGREEMENTTEXT");

        if (textRect != Rectangle.Empty)
        {
            TextAreaWidth = textRect.Width - 25;
            TextAreaHeight = textRect.Height;

            AgreementTextLabel = new UILabel
            {
                Name = "AgreementText",
                X = textRect.X,
                Y = textRect.Y,
                Width = TextAreaWidth,
                Height = TextAreaHeight,
                PaddingLeft = 0,
                PaddingTop = 0,
                WordWrap = true,
                Visible = false
            };

            AddChild(AgreementTextLabel);
            AgreementTextLabel.Native(10);
            //let the server notice use inline <red>/<green>/<reset> color markup, like NPC dialogs/tooltips do
            AgreementTextLabel.RichTextMarkup = true;
            //detect and underline https:// links so they can be clicked
            //push the text 6px to the right
            AgreementTextLabel.CollectLinks = true;
            AgreementTextLabel.X += 6;

            ScrollBar = new ScrollBarControl
            {
                Name = "AgreementScroll",
                X = textRect.X + textRect.Width - ScrollBarControl.DEFAULT_WIDTH,
                Y = textRect.Y,
                Height = TextAreaHeight
            };

            ScrollBar.OnValueChanged += value =>
            {
                AgreementTextLabel?.ScrollOffset = value * TextRenderer.CHAR_HEIGHT;
            };

            AddChild(ScrollBar);
        }
    }

    public override void Update(GameTime gameTime)
    {
        base.Update(gameTime);

        //the lobby keeps the OS cursor, so switch it to a hand pointer while hovering an https:// link in the notice
        if (Visible && (AgreementTextLabel is not null))
            OsCursor.SetHand(TextLinks.HitTest(AgreementTextLabel.LinkRects, InputBuffer.RawMouseScreenX, InputBuffer.RawMouseScreenY) is not null);
    }

    public override void Hide()
    {
        AgreementTextLabel?.Visible = false;

        ScrollBar?.Visible = false;

        OsCursor.SetHand(false); //don't leave the hand cursor up after the notice closes

        base.Hide();
    }

    public event CancelHandler? OnCancel;

    public event OkHandler? OnOk;

    public void Show(string agreementText)
    {
        if (AgreementTextLabel is not null)
        {
            AgreementTextLabel.ForegroundColor = Color.White;
            AgreementTextLabel.ScrollOffset = 0;
            AgreementTextLabel.Text = agreementText;
            AgreementTextLabel.Visible = true;

            if (ScrollBar is not null)
            {
                var visibleLines = TextAreaHeight / TextRenderer.CHAR_HEIGHT;
                var totalLines = AgreementTextLabel.ContentHeight / TextRenderer.CHAR_HEIGHT;
                var scrollMax = Math.Max(0, totalLines - visibleLines);

                ScrollBar.Value = 0;
                ScrollBar.MaxValue = scrollMax;
                ScrollBar.TotalItems = totalLines;
                ScrollBar.VisibleItems = visibleLines;
                ScrollBar.Enabled = scrollMax > 0;
            }
        }

        base.Show();
    }

    public override void OnClick(ClickEvent e)
    {
        //the lobby uses the OS cursor but an https:// link in the notice is still clickable
        //hit-test against the raw mouse position since the link rects are collected in screen space
        if ((AgreementTextLabel is not null)
            && (TextLinks.HitTest(AgreementTextLabel.LinkRects, InputBuffer.RawMouseScreenX, InputBuffer.RawMouseScreenY) is { } url))
        {
            Browser.Open(url);
            e.Handled = true;

            return;
        }

        base.OnClick(e);
    }

    public override void OnKeyDown(KeyDownEvent e)
    {
        switch (e.Key)
        {
            case Keys.Enter:
                OkButton?.PerformClick();
                e.Handled = true;

                break;

            case Keys.Escape:
                //intentionally blocked, escape is a no-op during eula display
                e.Handled = true;

                break;
        }
    }

    public override void OnMouseScroll(MouseScrollEvent e)
    {
        if ((AgreementTextLabel is null) || (ScrollBar is null))
            return;

        if (AgreementTextLabel.ContentHeight <= TextAreaHeight)
            return;

        var visibleLines = TextAreaHeight / TextRenderer.CHAR_HEIGHT;
        var totalLines = AgreementTextLabel.ContentHeight / TextRenderer.CHAR_HEIGHT;
        var maxScroll = Math.Max(0, totalLines - visibleLines);
        var newValue = Math.Clamp(ScrollBar.Value - e.Delta, 0, maxScroll);

        if (newValue == ScrollBar.Value)
            return;

        ScrollBar.Value = newValue;
        AgreementTextLabel.ScrollOffset = newValue * TextRenderer.CHAR_HEIGHT;
        e.Handled = true;
    }
}