#region
using Chaos.Client.Controls.Components;
using Chaos.Client.Rendering;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
#endregion

namespace Chaos.Client.Controls.World.Popups.Boards;

/// <summary>
///     Article compose panel using _nartin prefab. Provides subject and body text entry fields for posting to a public
///     board. No recipient field (public board posts have no addressee). Hosted in a draggable ScaleHost (a free-floating
///     window like Group/Equipment): follows the Window size scale, opens centered with no animation, and is dragged by its
///     background art (the author label is non-hit-test; the subject/body fields stay editable).
/// </summary>
public sealed class ArticleSendControl : PrefabPanel
{
    private readonly UILabel? AuthorLabel;
    private readonly UITextBox BodyBox;
    private readonly UITextBox? TitleBox;

    public ushort BoardId { get; set; }
    public UIButton? CancelButton { get; }
    public UIButton? SendButton { get; }

    public ArticleSendControl()
        : base("_nartin", false)
    {
        Name = "ArticleSend";
        Visible = false;
        UsesControlStack = true;

        //pass-through so empty background art falls through to the draggable ScaleHost that wraps this window
        IsPassThrough = true;

        SendButton = CreateButton("Send");
        CancelButton = CreateButton("Cancel");

        if (SendButton is not null)
            SendButton.Tooltip = "Post\nPost this message to the board for everyone to read.";

        if (CancelButton is not null)
            CancelButton.Tooltip = "Cancel\nDiscard this message without posting it.";

        if (SendButton is not null)
            SendButton.Clicked += HandleSend;

        if (CancelButton is not null)
            CancelButton.Clicked += () =>
            {
                Hide();
                OnCancel?.Invoke();
            };

        //display-only author label, kept out of hit-testing so it does not block dragging the window
        AuthorLabel = CreateLabel("Author");
        AuthorLabel?.IsHitTestVisible = false;

        TitleBox = CreateTextBox("Title", 60);
        TitleBox?.ForegroundColor = LegendColors.White;
        TitleBox?.IsTabStop = true;

        if (TtfTextRenderer.Available)
        {
            if (AuthorLabel is not null) AuthorLabel.Native(11);
            if (TitleBox is not null) TitleBox.Native(11);
        }

        //content rect for multi-line body text entry
        var contentRect = GetRect("Content");

        BodyBox = new UITextBox
        {
            X = contentRect.X,
            Y = contentRect.Y,
            Width = contentRect.Width - 2,
            Height = contentRect.Height,
            IsMultiLine = true,
            IsSelectable = true,
            MaxLength = 10000,
            PaddingLeft = 0,
            PaddingRight = 0,
            PaddingTop = 0,
            PaddingBottom = 0,
            ForegroundColor = LegendColors.White,
            IsTabStop = true
        };

        AddChild(BodyBox);
        BodyBox.Native(11); //match the article read body font size
    }

    private void HandleSend()
    {
        var subject = TitleBox?.Text ?? string.Empty;
        OnSend?.Invoke(subject, BodyBox.Text);
    }

    public override void Hide()
    {
        InputDispatcher.Instance?.RemoveControl(this);
        Visible = false;
    }

    public event CancelHandler? OnCancel;
    public event ArticleSendHandler? OnSend; //subject, body

    /// <summary>
    ///     Opens the window. Placement and scale are owned by the wrapping ScaleHost (it centers on first open).
    /// </summary>
    public override void Show()
    {
        if (Visible)
            return;

        InputDispatcher.Instance?.PushControl(this);
        Visible = true;
    }

    /// <summary>
    ///     Shows the compose dialog for a new public board post.
    /// </summary>
    public void ShowCompose(string authorName)
    {
        AuthorLabel?.Text = authorName;

        if (TitleBox is not null)
        {
            TitleBox.Text = string.Empty;
            TitleBox.IsFocused = true;
        }

        BodyBox.Text = string.Empty;
        BodyBox.ScrollOffset = 0;
        BodyBox.CursorPosition = 0;

        Show();
    }

    public override void OnKeyDown(KeyDownEvent e)
    {
        switch (e.Key)
        {
            case Keys.Escape:
                Hide();
                OnCancel?.Invoke();
                e.Handled = true;

                break;

            case Keys.Tab when TitleBox?.IsFocused == true:
                TitleBox.IsFocused = false;
                BodyBox.IsFocused = true;
                e.Handled = true;

                break;
        }
    }
}