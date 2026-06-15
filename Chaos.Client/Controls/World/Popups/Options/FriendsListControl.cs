#region
using Chaos.Client.Controls.Components;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
#endregion

namespace Chaos.Client.Controls.World.Popups.Options;

/// <summary>
///     Friends list popup using _nfriend prefab. Two-column layout of 12 slots each (24 total);
///     column 1 holds slots 1-12, column 2 holds slots 13-24. Row height 16px. OK/Cancel buttons at bottom.
///     Hosted in a draggable ScaleHost (a free-floating window like Group/Equipment): it follows the Window
///     size scale, opens centered with no animation, and is dragged by its background art.
/// </summary>
public sealed class FriendsListControl : PrefabPanel
{
    private const int ROW_HEIGHT = 16;  //the engraved slot box height
    private const int ROW_STRIDE = 21;  //y distance between consecutive rows (TextBottomLeft.Y minus TextTopLeft.Y in the prefab)
    private const int MAX_VISIBLE_ROWS = 10; //the _nfriend art has 10 numbered slots per column (1-0 / 11-20)
    private const int MAX_LENGTH = 28;

    private readonly Rectangle LeftColumnRect;

    private readonly UITextBox[] NamesColumn1 = new UITextBox[MAX_VISIBLE_ROWS];
    private readonly UITextBox[] NamesColumn2 = new UITextBox[MAX_VISIBLE_ROWS];
    private readonly Rectangle RightColumnRect;
    private int DataVersion;

    private List<string> Friends = [];
    private int RenderedVersion = -1;

    public UIButton? CancelButton { get; }

    public UIButton? OkButton { get; }

    public FriendsListControl()
        : base("_nfriend", false)
    {
        Name = "FriendsList";
        Visible = false;
        UsesControlStack = true;

        //pass-through so empty background art falls through to the draggable ScaleHost that wraps this window
        IsPassThrough = true;

        CancelButton = CreateButton("Cancel");
        OkButton = CreateButton("OK");

        if (CancelButton is not null)
            CancelButton.Clicked += Close;

        if (OkButton is not null)
            OkButton.Clicked += CloseWithOk;

        //first-row rect for each column from the prefab. The row stride comes from the second-row rect (TextBottomLeft)
        //so the text boxes land on the painted slots (rows are 16px tall but spaced 21px apart in the art)
        LeftColumnRect = GetRect("TextTopLeft");
        RightColumnRect = GetRect("TextTopRight");

        //if no rects found, use defaults based on prefab layout (single first-row rect, height = one slot)
        if (LeftColumnRect == Rectangle.Empty)
            LeftColumnRect = new Rectangle(40, 40, 175, ROW_HEIGHT);

        if (RightColumnRect == Rectangle.Empty)
            RightColumnRect = new Rectangle(251, 40, 175, ROW_HEIGHT);

        //figure out the row stride from the prefab's second row when available, else the known default
        var secondRow = GetRect("TextBottomLeft");
        var rowStride = (secondRow != Rectangle.Empty) && (LeftColumnRect != Rectangle.Empty) ? secondRow.Y - LeftColumnRect.Y : ROW_STRIDE;

        for (var i = 0; i < MAX_VISIBLE_ROWS; i++)
        {
            NamesColumn1[i] = new UITextBox
            {
                Name = $"Left{i}",
                X = LeftColumnRect.X,
                Y = LeftColumnRect.Y + i * rowStride - 1,
                Width = LeftColumnRect.Width,
                Height = ROW_HEIGHT,
                MaxLength = MAX_LENGTH,
                ForegroundColor = LegendColors.White
            };

            NamesColumn2[i] = new UITextBox
            {
                Name = $"Right{i}",
                X = RightColumnRect.X,
                Y = RightColumnRect.Y + i * rowStride - 1,
                Width = RightColumnRect.Width,
                Height = ROW_HEIGHT,
                MaxLength = MAX_LENGTH,
                ForegroundColor = LegendColors.White
            };

            AddChild(NamesColumn1[i]);
            NamesColumn1[i].Native(11);
            AddChild(NamesColumn2[i]);
            NamesColumn2[i].Native(11);
        }
    }

    private void Close()
    {
        Hide();
        OnClose?.Invoke();
    }

    private void CloseWithOk()
    {
        OnOk?.Invoke();
        Hide();
        OnClose?.Invoke();
    }

    public override void Draw(SpriteBatch spriteBatch)
    {
        if (!Visible)
            return;

        RefreshCaches();

        //textboxes are children, drawn by base.Draw()
        base.Draw(spriteBatch);
    }

    /// <summary>
    ///     Returns all non-empty friend names from the textboxes in slot order
    ///     (column 1 top to bottom, then column 2 top to bottom).
    /// </summary>
    public List<string> GetFriendNames()
    {
        var names = new List<string>(MAX_VISIBLE_ROWS * 2);

        for (var i = 0; i < MAX_VISIBLE_ROWS; i++)
        {
            var text = NamesColumn1[i].Text;

            if (!string.IsNullOrWhiteSpace(text))
                names.Add(text.Trim());
        }

        for (var i = 0; i < MAX_VISIBLE_ROWS; i++)
        {
            var text = NamesColumn2[i].Text;

            if (!string.IsNullOrWhiteSpace(text))
                names.Add(text.Trim());
        }

        return names;
    }

    public override void Hide()
    {
        InputDispatcher.Instance?.RemoveControl(this);
        Visible = false;
    }

    public event CloseHandler? OnClose;
    public event OkHandler? OnOk;

    private void RefreshCaches()
    {
        if (RenderedVersion == DataVersion)
            return;

        RenderedVersion = DataVersion;

        //slots fill column 1 first (rows 0-11), then column 2 (rows 12-23)
        for (var i = 0; i < MAX_VISIBLE_ROWS; i++)
        {
            NamesColumn1[i].Text = i < Friends.Count ? Friends[i] : string.Empty;

            var rightIndex = i + MAX_VISIBLE_ROWS;
            NamesColumn2[i].Text = rightIndex < Friends.Count ? Friends[rightIndex] : string.Empty;
        }
    }

    /// <summary>
    ///     Fills the friends list. Slots fill column 1 first (rows 0-11),
    ///     then column 2 (rows 12-23).
    /// </summary>
    public void SetFriends(List<string> friends)
    {
        Friends = friends;
        DataVersion++;
    }

    /// <summary>
    ///     Opens the window. Placement and scale are owned by the wrapping ScaleHost (it centers on first open),
    ///     so this just makes the panel visible and takes keyboard focus.
    /// </summary>
    public override void Show()
    {
        if (Visible)
            return;

        InputDispatcher.Instance?.PushControl(this);
        Visible = true;
    }

    public override void Update(GameTime gameTime)
    {
        if (!Visible || !Enabled)
            return;

        base.Update(gameTime);
    }

    public override void OnKeyDown(KeyDownEvent e)
    {
        if (e.Key == Keys.Escape)
        {
            Close();
            e.Handled = true;
        }
    }
}