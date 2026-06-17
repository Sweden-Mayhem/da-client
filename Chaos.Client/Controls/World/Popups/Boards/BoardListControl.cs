#region
using Chaos.Client.Controls.Components;
using Chaos.Client.Controls.Generic;
using Chaos.Client.Definitions;
using Chaos.Client.Extensions;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Chaos.Client.Systems;
using Microsoft.Xna.Framework.Input;
#endregion

namespace Chaos.Client.Controls.World.Popups.Boards;

/// <summary>
///     Board selection panel using _nbdlist prefab. Displays a scrollable list of available bulletin boards. View button
///     opens the selected board; Quit closes. Hosted in a draggable ScaleHost (a free-floating window like
///     Group/Equipment): it follows the Window size scale, opens centered with no animation, and is dragged by its
///     background art (the row labels stay hit-testable so clicking a row selects it).
/// </summary>
public sealed class BoardListControl : PrefabPanel, INativeTextDrawer
{
    private const int ROW_HEIGHT = Constants.BOARD_ROW_HEIGHT;
    private const int ROW_INDENT = 8; //rows sit 8px in from the left edge (and 8px narrower) for a small reading margin

    private readonly Rectangle BoardListRect;
    private readonly int MaxVisibleRows;
    private readonly UILabel[] RowLabels;
    private readonly ScrollBarControl ScrollBar;
    private List<(ushort BoardId, string Name)> Boards = [];
    private int DataVersion;
    private int RenderedVersion = -1;
    private int ScrollOffset;
    private int SelectedIndex = -1;

    public UIButton? QuitButton { get; }
    public UIButton? ViewButton { get; }

    public BoardListControl()
        : base("_nbdlist", false)
    {
        Name = "BoardList";
        Visible = false;
        UsesControlStack = true;

        //pass-through so empty background art falls through to the draggable ScaleHost that wraps this window
        IsPassThrough = true;

        ViewButton = CreateButton("View");
        QuitButton = CreateButton("Quit");

        if (ViewButton is not null)
            ViewButton.Tooltip = "View\nOpen the selected board to read its messages.";

        if (QuitButton is not null)
            QuitButton.Tooltip = "Close\nClose this window.";

        if (QuitButton is not null)
            QuitButton.Clicked += Close;

        if (ViewButton is not null)
            ViewButton.Clicked += () =>
            {
                if ((SelectedIndex >= 0) && (SelectedIndex < Boards.Count))
                    OnViewBoard?.Invoke(Boards[SelectedIndex].BoardId);
            };

        BoardListRect = GetRect("BoardList");
        MaxVisibleRows = BoardListRect.Height > 0 ? BoardListRect.Height / ROW_HEIGHT : 0;

        RowLabels = new UILabel[MaxVisibleRows];

        for (var i = 0; i < MaxVisibleRows; i++)
        {
            RowLabels[i] = new UILabel
            {
                X = BoardListRect.X + ROW_INDENT,
                Y = BoardListRect.Y + i * ROW_HEIGHT,
                Width = BoardListRect.Width - ScrollBarControl.DEFAULT_WIDTH - ROW_INDENT,
                Height = ROW_HEIGHT,
                PaddingLeft = 0,
                PaddingTop = 0,
                //row text painted by DrawNativeText (crisp TTF at native res); skip bitmap glyphs in the scaled pass
                SuppressGlyphs = true
            };

            AddChild(RowLabels[i]);
        }

        ScrollBar = new ScrollBarControl
        {
            X = BoardListRect.AlignRight(ScrollBarControl.DEFAULT_WIDTH),
            Y = BoardListRect.Y - 5,
            Height = BoardListRect.Height + 10
        };

        ScrollBar.OnValueChanged += v => { ScrollOffset = v; DataVersion++; };
        AddChild(ScrollBar);
    }

    public void Close()
    {
        InputDispatcher.Instance?.RemoveControl(this);
        Visible = false;
        OnClose?.Invoke();
    }

    public override void Draw(SpriteBatch spriteBatch)
    {
        if (!Visible)
            return;

        RefreshLabels();
        base.Draw(spriteBatch);
    }

    /// <summary>Draws the row text at native (unscaled) resolution. Call from WorldScreen's native pass with the wrapping
    ///     ScaleHost's ScreenX/Y, Scale and OpenFraction (as alpha).</summary>
    public void DrawNativeText(SpriteBatch spriteBatch, int originX, int originY, int nativeOriginX, int nativeOriginY, float scale, float alpha)
        => BoardRowText.DrawRows(spriteBatch, RowLabels, ROW_HEIGHT, originX, originY, scale, alpha);

    public override void Hide()
    {
        InputDispatcher.Instance?.RemoveControl(this);
        Visible = false;
    }

    public event CloseHandler? OnClose;
    public event ViewBoardHandler? OnViewBoard;

    private void RefreshLabels()
    {
        if (RenderedVersion == DataVersion)
            return;

        RenderedVersion = DataVersion;

        for (var i = 0; i < MaxVisibleRows; i++)
        {
            var boardIndex = ScrollOffset + i;

            if (boardIndex < Boards.Count)
            {
                var (_, name) = Boards[boardIndex];

                RowLabels[i].ForegroundColor = boardIndex == SelectedIndex ? new Color(100, 149, 237) : LegendColors.White;
                RowLabels[i].Text = name;
            } else
                RowLabels[i].Text = string.Empty;
        }
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

    public void ShowBoards(List<(ushort BoardId, string Name)> boards)
    {
        Boards = boards;
        SelectedIndex = boards.Count > 0 ? 0 : -1;
        ScrollOffset = 0;
        DataVersion++;
        ScrollBar.Value = 0;
        ScrollBar.TotalItems = boards.Count;
        ScrollBar.VisibleItems = MaxVisibleRows;
        ScrollBar.MaxValue = Math.Max(0, boards.Count - MaxVisibleRows);
        UpdateButtonStates();

        Show();
    }

    /// <summary>
    ///     Appends additional boards (e.g. server boards arriving after the list was already shown with help topics).
    /// </summary>
    public void AppendBoards(List<(ushort BoardId, string Name)> extra)
    {
        if (extra.Count == 0)
            return;

        Boards.AddRange(extra);
        DataVersion++;
        ScrollBar.TotalItems = Boards.Count;
        ScrollBar.MaxValue = Math.Max(0, Boards.Count - MaxVisibleRows);
    }

    public void SlideClose() => Close();

    public override void Update(GameTime gameTime)
    {
        if (!Visible || !Enabled)
            return;

        base.Update(gameTime);
    }

    public override void OnKeyDown(KeyDownEvent e)
    {
        switch (Keybindings.Resolve(e.Key, e.Modifiers))
        {
            case GameAction.MoveUp: //the player's walk-up key moves the selection up
                MoveSelection(-1);
                e.Handled = true;

                return;
            case GameAction.MoveDown:
                MoveSelection(1);
                e.Handled = true;

                return;
            case GameAction.Assail: //space = open the selected board, same as View / double-click
                ActivateSelected();
                e.Handled = true;

                return;
            case GameAction.ToggleBulletinBoard: //this is the top page: the bound key just closes the whole menu
                Close();
                e.Handled = true;

                return;
        }

        if (e.Key == Keys.Escape) //Escape closes the whole menu
        {
            Close();
            e.Handled = true;
        }
    }

    //keyboard selection: move the highlighted row by delta, keeping it scrolled into view
    private void MoveSelection(int delta)
    {
        if (Boards.Count == 0)
            return;

        var next = SelectedIndex < 0
            ? (delta > 0 ? 0 : Boards.Count - 1)
            : Math.Clamp(SelectedIndex + delta, 0, Boards.Count - 1);

        if (next == SelectedIndex)
            return;

        SelectedIndex = next;

        if (SelectedIndex < ScrollOffset)
            ScrollOffset = SelectedIndex;
        else if (SelectedIndex >= ScrollOffset + MaxVisibleRows)
            ScrollOffset = SelectedIndex - MaxVisibleRows + 1;

        ScrollBar.Value = ScrollOffset;
        DataVersion++;
        UpdateButtonStates();
    }

    //space / View / double-click: open the selected board
    private void ActivateSelected()
    {
        if ((SelectedIndex < 0) || (SelectedIndex >= Boards.Count))
            return;

        SoundSystem.PlayUiClick();
        OnViewBoard?.Invoke(Boards[SelectedIndex].BoardId);
    }

    public override void OnMouseScroll(MouseScrollEvent e)
    {
        if (ScrollBar.TotalItems <= ScrollBar.VisibleItems)
            return;

        var newValue = Math.Clamp(ScrollBar.Value - e.Delta, 0, ScrollBar.MaxValue);

        if (newValue != ScrollBar.Value)
        {
            ScrollBar.Value = newValue;
            ScrollOffset = newValue;
            DataVersion++;
        }

        e.Handled = true;
    }

    public override void OnClick(ClickEvent e)
    {
        base.OnClick(e);

        if (e.Button != MouseButton.Left)
            return;

        var localX = e.ScreenX - ScreenX - BoardListRect.X;
        var localY = e.ScreenY - ScreenY - BoardListRect.Y;

        if ((localX < 0) || (localX >= BoardListRect.Width) || (localY < 0) || (localY >= BoardListRect.Height))
            return;

        var row = localY / ROW_HEIGHT;

        if (row >= MaxVisibleRows)
            return;

        var entryIndex = ScrollOffset + row;

        if (entryIndex >= Boards.Count)
            return;

        SelectedIndex = entryIndex;
        DataVersion++;
        UpdateButtonStates();
    }

    public override void OnDoubleClick(DoubleClickEvent e)
    {
        base.OnDoubleClick(e);

        if (e.Button != MouseButton.Left)
            return;

        var localX = e.ScreenX - ScreenX - BoardListRect.X;
        var localY = e.ScreenY - ScreenY - BoardListRect.Y;

        if ((localX < 0) || (localX >= BoardListRect.Width) || (localY < 0) || (localY >= BoardListRect.Height))
            return;

        var row = localY / ROW_HEIGHT;

        if (row >= MaxVisibleRows)
            return;

        var entryIndex = ScrollOffset + row;

        if (entryIndex >= Boards.Count)
            return;

        SelectedIndex = entryIndex;
        DataVersion++;
        UpdateButtonStates();
        SoundSystem.PlayUiClick(); //navigating IN makes the same click as the View button
        OnViewBoard?.Invoke(Boards[entryIndex].BoardId);
    }

    private void UpdateButtonStates()
    {
        var hasSelection = SelectedIndex >= 0;

        ViewButton?.Enabled = hasSelection;
    }
}