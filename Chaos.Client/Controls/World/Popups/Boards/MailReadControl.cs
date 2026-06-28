#region
using Chaos.Client.Controls.Components;
using Chaos.Client.Controls.Generic;
using Chaos.Client.Rendering;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Chaos.Client.Systems;
using Microsoft.Xna.Framework.Input;
#endregion

namespace Chaos.Client.Controls.World.Popups.Boards;

/// <summary>
///     Mail reading panel using _nmailr prefab. Displays a received mail message with author, title, date, and body text.
///     Buttons: Prev, Next, New, Reply, Delete, Up (back to list), Quit (close). Hosted in a draggable ScaleHost (a
///     free-floating window like Group/Equipment): follows the Window size scale, opens centered with no animation, and is
///     dragged by its background art (the header value labels are non-hit-test so the header band drags; the body stays selectable).
/// </summary>
public sealed class MailReadControl : PrefabPanel, INativeTextDrawer
{
    private const int BODY_FONT_SIZE = 11;
    private const int HEADER_FONT_SIZE = 11;

    private readonly UILabel? AuthorLabel;
    private readonly UILabel BodyLabel;
    private readonly UILabel? DateLabel;
    private readonly ScrollBarControl ScrollBar;
    private readonly UILabel? TitleLabel;
    private readonly int VisibleHeight;

    private string BodyText = string.Empty;
    private List<List<RichRun>> CachedLines = new();
    private float CachedLinesScale;
    private int MaxScrollLine;
    private int ScrollLine;

    //on-screen rects of https:// links in the body, rebuilt each DrawNativeText call for hover and click-to-open
    private readonly List<(Rectangle Rect, string Url)> LinkRects = [];

    /// <summary>True (with <paramref name="url" /> set) when the screen point is over an https:// link in the body.</summary>
    public bool TryGetLinkAt(int screenX, int screenY, out string url)
    {
        url = TextLinks.HitTest(LinkRects, screenX, screenY) ?? string.Empty;

        return url.Length > 0;
    }

    public ushort BoardId { get; set; }
    public string CurrentAuthor { get; private set; } = string.Empty;
    public short CurrentPostId { get; private set; }
    public UIButton? DeleteButton { get; }
    public UIButton? NewButton { get; }
    public UIButton? NextButton { get; }
    public UIButton? PrevButton { get; }
    public UIButton? QuitButton { get; }
    public UIButton? ReplyButton { get; }
    public UIButton? UpButton { get; }

    public MailReadControl()
        : base("_nmailr", false)
    {
        Name = "MailRead";
        Visible = false;
        UsesControlStack = true;

        IsPassThrough = true;

        PrevButton = CreateButton("Prev");
        NextButton = CreateButton("Next");
        NewButton = CreateButton("New");
        ReplyButton = CreateButton("Reply");
        DeleteButton = CreateButton("Delete");
        UpButton = CreateButton("Up");
        QuitButton = CreateButton("Quit");

        if (PrevButton is not null)
            PrevButton.Tooltip = "Previous\nRead the previous message.";

        if (NextButton is not null)
            NextButton.Tooltip = "Next\nRead the next message.";

        if (NewButton is not null)
            NewButton.Tooltip = "New\nWrite a new mail to send to another player.";

        if (ReplyButton is not null)
            ReplyButton.Tooltip = "Reply\nWrite a reply to the sender of this message.";

        if (DeleteButton is not null)
            DeleteButton.Tooltip = "Delete\nPermanently remove this message.";

        if (UpButton is not null)
            UpButton.Tooltip = "Back\nReturn to the list of messages.";

        if (QuitButton is not null)
            QuitButton.Tooltip = "Close\nClose this window.";

        if (QuitButton is not null)
            QuitButton.Clicked += () => OnQuit?.Invoke();

        if (UpButton is not null)
            UpButton.Clicked += () => OnUp?.Invoke();

        if (PrevButton is not null)
            PrevButton.Clicked += () => OnPrev?.Invoke();

        if (NextButton is not null)
            NextButton.Clicked += () => OnNext?.Invoke();

        if (NewButton is not null)
            NewButton.Clicked += () => OnNewMail?.Invoke();

        if (ReplyButton is not null)
            ReplyButton.Clicked += () => OnReplyPost?.Invoke(CurrentPostId);

        if (DeleteButton is not null)
            DeleteButton.Clicked += () => OnDeletePost?.Invoke(CurrentPostId);

        AuthorLabel = CreateLabel("Author");
        AuthorLabel?.ForegroundColor = LegendColors.White;
        AuthorLabel?.IsHitTestVisible = false;

        TitleLabel = CreateLabel("Title");
        TitleLabel?.ForegroundColor = LegendColors.White;
        TitleLabel?.IsHitTestVisible = false;

        DateLabel = CreateLabel("Mmdd");
        DateLabel?.ForegroundColor = LegendColors.White;
        DateLabel?.IsHitTestVisible = false;

        if (TtfTextRenderer.Available)
        {
            if (AuthorLabel is not null) AuthorLabel.Native(HEADER_FONT_SIZE);
            if (TitleLabel is not null) TitleLabel.Native(HEADER_FONT_SIZE);
            if (DateLabel is not null) DateLabel.Native(HEADER_FONT_SIZE);
        }

        //nudge the Name (author) + Title value labels up 1px so they sit better on their header line
        if (AuthorLabel is not null) AuthorLabel.Y -= 1;
        if (TitleLabel is not null) TitleLabel.Y -= 1;

        var contentRect = GetRect("Content");
        VisibleHeight = contentRect.Height;

        BodyLabel = new UILabel
        {
            X = contentRect.X,
            Y = contentRect.Y,
            Width = contentRect.Width - ScrollBarControl.DEFAULT_WIDTH,
            Height = contentRect.Height,
            PaddingLeft = 0,
            PaddingRight = 2,
            PaddingTop = 0,
            WordWrap = true,
            ForegroundColor = LegendColors.White,
            IsSelectable = true
        };

        AddChild(BodyLabel);

        //in the TTF path the body text is painted natively (DrawNativeText), so suppress the bitmap glyphs but keep the
        //label VISIBLE as a hit target - that way the mouse wheel over the body bubbles to this control's OnMouseScroll
        //(a hidden label is skipped by hit-testing, which is why the wheel did nothing in the read view before).
        BodyLabel.SuppressGlyphs = TtfTextRenderer.Available;

        ScrollBar = new ScrollBarControl
        {
            Name = "ScrollBar",
            X = contentRect.X + contentRect.Width - ScrollBarControl.DEFAULT_WIDTH,
            Y = contentRect.Y - 5,
            Height = contentRect.Height + 10
        };

        ScrollBar.OnValueChanged += v =>
        {
            if (TtfTextRenderer.Available)
                ScrollLine = v;
            else
                BodyLabel.ScrollOffset = v * TextRenderer.CHAR_HEIGHT;
        };

        AddChild(ScrollBar);
    }

    public override void Hide()
    {
        InputDispatcher.Instance?.RemoveControl(this);
        Visible = false;
    }

    public event DeletePostHandler? OnDeletePost;
    public event NewMailHandler? OnNewMail;
    public event NextHandler? OnNext;
    public event PrevHandler? OnPrev;
    public event QuitHandler? OnQuit;
    public event ReplyPostHandler? OnReplyPost;
    public event UpHandler? OnUp;

    public override void Show()
    {
        if (Visible)
            return;

        InputDispatcher.Instance?.PushControl(this);
        Visible = true;
    }

    public void ShowMail(
        short postId,
        string author,
        int month,
        int day,
        string subject,
        string message,
        bool enablePrev)
    {
        CurrentPostId = postId;
        CurrentAuthor = author;

        AuthorLabel?.Text = author;
        TitleLabel?.Text = subject;
        DateLabel?.Text = $"{month}/{day}";

        PrevButton?.Enabled = enablePrev;

        ScrollLine = 0;
        BodyText = message;
        CachedLines.Clear();
        CachedLinesScale = 0;
        ScrollBar.Value = 0;

        if (TtfTextRenderer.Available)
        {
            //kept VISIBLE (glyphs suppressed) so it stays a wheel hit target; the text is drawn by DrawNativeText
            BodyLabel.Visible = true;
            //hide header labels too - they're drawn natively in DrawNativeText
            if (AuthorLabel is not null) AuthorLabel.Visible = false;
            if (TitleLabel is not null) TitleLabel.Visible = false;
            if (DateLabel is not null) DateLabel.Visible = false;
            ScrollBar.TotalItems = 0;
            ScrollBar.VisibleItems = 1;
            ScrollBar.MaxValue = 0;
        }
        else
        {
            BodyLabel.Visible = true;
            if (AuthorLabel is not null) AuthorLabel.Visible = true;
            if (TitleLabel is not null) TitleLabel.Visible = true;
            if (DateLabel is not null) DateLabel.Visible = true;
            BodyLabel.Text = message;
            BodyLabel.ScrollOffset = 0;
            UpdateScrollBar();
        }

        Show();
    }

    //the player's walk-up/down keys (W/S AND the arrows, whatever they're bound to) scroll the mail body. Polled from
    //the input buffer (not OnKeyDown) the same way the chat input recalls history: those KeyDowns don't reliably reach a
    //non-textbox control here, so poll while this is the topmost window.
    public override void Update(GameTime gameTime)
    {
        base.Update(gameTime);

        if (!Visible || (InputDispatcher.Instance?.TopControl != this))
            return;

        if (Keybindings.Triggered(GameAction.MoveUp))
            ScrollBy(-1);
        else if (Keybindings.Triggered(GameAction.MoveDown))
            ScrollBy(1);
    }

    public override void OnKeyDown(KeyDownEvent e)
    {
        //bound board key backs out one level (to the mail list); Escape closes the whole menu
        if (Keybindings.Resolve(e.Key, e.Modifiers) == GameAction.ToggleBulletinBoard)
        {
            SoundSystem.PlayUiClick(); //match the Up button's click sound
            OnUp?.Invoke();
            e.Handled = true;
        } else if (e.Key == Keys.Escape)
        {
            OnQuit?.Invoke();
            e.Handled = true;
        }
    }

    //scroll the body by delta lines (keyboard arrows), mirroring the wheel path for both the TTF and bitmap renderers
    private void ScrollBy(int delta)
    {
        if (TtfTextRenderer.Available)
        {
            var newLine = Math.Clamp(ScrollLine + delta, 0, MaxScrollLine);

            if (newLine != ScrollLine)
            {
                ScrollLine = newLine;
                ScrollBar.Value = ScrollLine;
            }

            return;
        }

        if (ScrollBar.TotalItems <= ScrollBar.VisibleItems)
            return;

        var newValue = Math.Clamp(ScrollBar.Value + delta, 0, ScrollBar.MaxValue);

        if (newValue != ScrollBar.Value)
        {
            ScrollBar.Value = newValue;
            BodyLabel.ScrollOffset = newValue * TextRenderer.CHAR_HEIGHT;
        }
    }

    public override void OnMouseScroll(MouseScrollEvent e)
    {
        if (TtfTextRenderer.Available)
        {
            var newLine = Math.Clamp(ScrollLine - e.Delta, 0, MaxScrollLine);

            if (newLine != ScrollLine)
            {
                ScrollLine = newLine;
                ScrollBar.Value = ScrollLine;
            }

            e.Handled = true;

            return;
        }

        if (ScrollBar.TotalItems <= ScrollBar.VisibleItems)
            return;

        var newValue = Math.Clamp(ScrollBar.Value - e.Delta, 0, ScrollBar.MaxValue);

        if (newValue != ScrollBar.Value)
        {
            ScrollBar.Value = newValue;
            BodyLabel.ScrollOffset = newValue * TextRenderer.CHAR_HEIGHT;
        }

        e.Handled = true;
    }

    /// <summary>
    ///     Draws the body text at native (unscaled) resolution. Call from WorldScreen.DrawNativeUi after Root.Draw,
    ///     passing the wrapping ScaleHost's ScreenX/Y, Scale, and OpenFraction as the alpha.
    /// </summary>
    public void DrawNativeText(SpriteBatchEx spriteBatch, int originX, int originY, int nativeOriginX, int nativeOriginY, float scale, float alpha)
    {
        if (!TtfTextRenderer.Available || (scale <= 0f) || (alpha <= 0f))
            return;

        int MapX(int sx) => originX + (int)((sx - originX) * scale);
        int MapY(int sy) => originY + (int)((sy - originY) * scale);

        var shadowOff = new Vector2(Math.Max(1, (int)MathF.Round(scale)));
        var shadowCol = Color.Black * (0.5f * alpha);

        //draw header labels natively (hidden in the ScaleHost pass)
        var hdrFont = Math.Max(1, (int)MathF.Round(HEADER_FONT_SIZE * scale));

        void DrawLabel(UILabel? label)
        {
            if (label is null || string.IsNullOrEmpty(label.Text))
                return;

            var tex = TtfTextRenderer.GetLine(label.Text, hdrFont);

            if (tex is null)
                return;

            var p = new Vector2(MapX(label.ScreenX), MapY(label.ScreenY));
            spriteBatch.Draw(tex, p + shadowOff, shadowCol);
            spriteBatch.Draw(tex, p, label.ForegroundColor * alpha);
        }

        DrawLabel(AuthorLabel);
        DrawLabel(TitleLabel);
        DrawLabel(DateLabel);

        //draw body text natively
        var font = Math.Max(1, (int)MathF.Round(BODY_FONT_SIZE * scale));
        var nativeWidth = Math.Max(1, (int)(BodyLabel.Width * scale));

        if ((Math.Abs(CachedLinesScale - scale) > 0.01f) || (CachedLines.Count == 0 && BodyText.Length > 0))
        {
            CachedLines = BoardBodyText.Wrap(BodyText, nativeWidth, font);
            CachedLinesScale = scale;

            var lh = TtfTextRenderer.LineHeight(font);
            var visibleH = (int)(VisibleHeight * scale);
            var visLines = Math.Max(1, visibleH / lh);
            MaxScrollLine = Math.Max(0, CachedLines.Count - visLines);
            ScrollLine = Math.Clamp(ScrollLine, 0, MaxScrollLine);

            ScrollBar.TotalItems = CachedLines.Count;
            ScrollBar.VisibleItems = visLines;
            ScrollBar.MaxValue = MaxScrollLine;
            ScrollBar.Value = ScrollLine;
        }

        LinkRects.Clear();

        if (string.IsNullOrEmpty(BodyText))
            return;

        var lhDraw = TtfTextRenderer.LineHeight(font);
        var visibleHDraw = (int)(VisibleHeight * scale);
        var visLinesDraw = Math.Max(1, visibleHDraw / lhDraw);
        var x = MapX(BodyLabel.ScreenX);
        var y = MapY(BodyLabel.ScreenY);
        var col = BodyLabel.ForegroundColor;
        var step = Math.Max(1, (int)MathF.Round(scale));

        //each line is drawn with inline <red>/<green>/<reset> color markup + https:// links/underlines
        for (var i = ScrollLine; (i < CachedLines.Count) && (i < ScrollLine + visLinesDraw); i++)
        {
            var lineY = (int)(y + (i - ScrollLine) * lhDraw);
            BoardBodyText.DrawLine(spriteBatch, CachedLines[i], x, lineY, font, lhDraw, col, alpha, step, LinkRects);
        }
    }

    private void UpdateScrollBar()
    {
        var totalLines = BodyLabel.ContentHeight / TextRenderer.CHAR_HEIGHT;
        var visibleLines = VisibleHeight / TextRenderer.CHAR_HEIGHT;

        ScrollBar.TotalItems = totalLines;
        ScrollBar.VisibleItems = visibleLines;
        ScrollBar.MaxValue = Math.Max(0, totalLines - visibleLines);
        ScrollBar.Value = 0;
    }
}
