#region
using Chaos.Client.Controls.Components;
using Chaos.Client.Controls.Generic;
using Chaos.Client.Definitions;
using Chaos.Client.Systems;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
#endregion

namespace Chaos.Client.Controls.World.Menu;

/// <summary>
///     The keybinding editor (Options &gt; Controls). A scrollable list of every <see cref="GameAction" /> grouped by
///     category, each with two key cells (primary + secondary). Left-click a cell to capture a new key, right-click to
///     clear it. Includes the turn-in-place modifier toggle and a reset-to-defaults button. Changes persist immediately.
/// </summary>
public sealed class ControlsWindow : DraggableWindow
{
    //wider than before so the bottom hint and the wider TrueType (Cinzel) text never clip
    private const int W = 548;
    private const int H = 544;
    private const int OUTER_PAD = 2;
    private const int PAD = 8;
    private const int ROW_H = 22;
    private const int HEADER_H = 26;
    private const int SPACING_H = 8;
    private const int LABEL_W = 210;
    private const int BTN_W = 124;
    private const int BTN_GAP = 6;
    private const int RESET_H = 24;
    private const int FONT = MenuButton.MenuFontSize; //Cinzel, like the rest of the options/menu UI
    private const int CONTROL_INDENT = 12;

    private static readonly Color HeaderColor = new(196, 168, 110);
    private static readonly Color LabelColor = new(202, 198, 188);
    private static readonly Color HintColor = new(150, 146, 136);

    private readonly UIPanel Viewport;
    private readonly UIPanel RowsHost;
    private readonly ScrollBarControl ScrollBar;
    private readonly List<MenuButton> BindCells = [];
    private MenuButton? TurnCell;

    private int ContentHeight;
    private int ScrollOffset;

    //the cell currently waiting for a key, and a one-frame guard so the click that started capture is not read as input
    private MenuButton? CapturingCell;
    private bool JustBeganCapture;

    public ControlsWindow()
        : base("Controls", W, H, useWoodFrame: true)
    {
        X = 120;
        Y = 50;

        //the base sized Content for the current frame mode (wood inset); lay everything out inside that rect
        var contentW = Content.Width;
        var contentH = Content.Height;

        var resetY = contentH - 2 * OUTER_PAD - PAD - RESET_H;
        var viewportH = resetY - PAD - OUTER_PAD;
        var viewportW = contentW - 2 * OUTER_PAD - ScrollBarControl.DEFAULT_WIDTH - PAD;

        Viewport = new UIPanel
        {
            Name = "Viewport",
            X = OUTER_PAD,
            Y = OUTER_PAD,
            Width = viewportW,
            Height = viewportH,
            IsPassThrough = true
        };
        Content.AddChild(Viewport);

        RowsHost = new UIPanel
        {
            Name = "Rows",
            X = 0,
            Y = 0,
            Width = viewportW,
            Height = 0,
            IsPassThrough = true
        };
        Viewport.AddChild(RowsHost);

        BuildRows(viewportW);

        //the real scrollbar widget (arrows + draggable thumb + wheel), pixel-based: items/value are content pixels
        ScrollBar = new ScrollBarControl
        {
            Name = "ScrollBar",
            X = OUTER_PAD + viewportW + PAD,
            Y = OUTER_PAD,
            Height = viewportH
        };
        ScrollBar.OnValueChanged += ApplyScroll;
        Content.AddChild(ScrollBar);

        ScrollBar.TotalItems = ContentHeight;
        ScrollBar.VisibleItems = viewportH;
        ScrollBar.MaxValue = Math.Max(0, ContentHeight - viewportH);

        //reset + hint along the bottom
        var reset = new MenuButton("Reset to defaults", 160, RESET_H) { X = OUTER_PAD + PAD, Y = resetY };
        reset.Clicked = _ => ResetAll();
        Content.AddChild(reset);

        Content.AddChild(
            new UILabel
            {
                X = OUTER_PAD + 168 + PAD,
                Y = resetY,
                Width = contentW - PAD - 176,
                Height = RESET_H,
                Text = "Click to set, right-click to clear",
                CustomFontSize = FONT,
                ForegroundColor = HintColor,
                VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible = false
            });

        RefreshAll();
        ApplyScroll(0);
    }

    private void BuildRows(int viewportW)
    {
        var y = 0;

        foreach (var category in (BindCategory[]) [BindCategory.Movement, BindCategory.Panels, BindCategory.Combat, BindCategory.Misc, BindCategory.System, BindCategory.Emotes])
        {
            AddHeader(CategoryTitle(category), ref y, viewportW);

            foreach (var info in Keybindings.Actions)
            {
                if ((info.Category != category) || !info.Bindable)
                    continue;

                AddActionRow(info, ref y);

                //the turn-in-place modifier sits with the movement keys, after the last move direction
                if ((category == BindCategory.Movement) && (info.Action == GameAction.MoveRight))
                    AddTurnRow(ref y);
            }

            AddSpacing(ref y);
        }

        ContentHeight = y;
        RowsHost.Height = y;
    }

    private void AddHeader(string text, ref int y, int viewportW)
    {
        RowsHost.AddChild(
            new UILabel
            {
                X = PAD - 2,
                Y = y + 4,
                Width = viewportW,
                Height = 16,
                Text = text,
                CustomFontSize = FONT,
                ForegroundColor = HeaderColor,
                VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible = false
            });

        y += HEADER_H;
    }

    public void AddSpacing(ref int y)
    {
        y += SPACING_H;
    }

    private void AddActionRow(ActionInfo info, ref int y)
    {
        RowsHost.AddChild(
            new UILabel
            {
                X = PAD + CONTROL_INDENT - 2,
                Y = y,
                Width = LABEL_W - 4,
                Height = ROW_H - 3,
                Text = info.Label,
                CustomFontSize = FONT,
                ForegroundColor = LabelColor,
                VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible = false
            });

        AddCell(info.Action, 0, LABEL_W + 4, y);
        AddCell(info.Action, 1, LABEL_W + 4 + BTN_W + BTN_GAP, y);

        y += ROW_H;
    }

    private void AddCell(GameAction action, int slot, int x, int y)
    {
        var cell = new MenuButton(string.Empty, BTN_W, ROW_H - 3)
        {
            X = x,
            Y = y,
            Action = action,
            Slot = slot
        };
        cell.Clicked = BeginCapture;
        cell.RightClicked = ClearCell;
        RowsHost.AddChild(cell);
        BindCells.Add(cell);
    }

    private void AddTurnRow(ref int y)
    {
        RowsHost.AddChild(
            new UILabel
            {
                X = PAD + CONTROL_INDENT - 2,
                Y = y,
                Width = LABEL_W - 4,
                Height = ROW_H - 3,
                Text = "Turn in place (hold)",
                CustomFontSize = FONT,
                ForegroundColor = LabelColor,
                VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible = false
            });

        TurnCell = new MenuButton(string.Empty, BTN_W, ROW_H - 3) { X = LABEL_W + 4, Y = y };
        TurnCell.Clicked = _ => CycleTurnModifier();
        TurnCell.RightClicked = _ => SetTurnModifier(KeyModifiers.None);
        RowsHost.AddChild(TurnCell);

        y += ROW_H;
    }

    private static string CategoryTitle(BindCategory category)
        => category switch
        {
            BindCategory.Movement => "Movement",
            BindCategory.Panels   => "Windows & panels",
            BindCategory.Combat   => "Combat & hotbar",
            BindCategory.Misc     => "Other",
            BindCategory.Emotes   => "Emotes",
            _                     => "System"
        };

    //--- capture ---

    private void BeginCapture(MenuButton cell)
    {
        //starting a new capture cancels any in-progress one
        CapturingCell = cell;
        JustBeganCapture = true;
        Keybindings.IsCapturing = true;
        RefreshAll();
    }

    private void EndCapture()
    {
        CapturingCell = null;
        Keybindings.IsCapturing = false;
        RefreshAll();
    }

    private void ClearCell(MenuButton cell)
    {
        if (CapturingCell is not null)
            EndCapture();

        Keybindings.Set(cell.Action, cell.Slot, KeyBind.None);
        ClientSettings.Save();
        RefreshAll();
    }

    public override void Update(GameTime gameTime)
    {
        base.Update(gameTime);

        if (CapturingCell is null)
            return;

        //skip the frame the starting click happened on (its mouse-press is still in this frame's event buffer)
        if (JustBeganCapture)
        {
            JustBeganCapture = false;

            return;
        }

        foreach (var evt in InputBuffer.Events)
        {
            //clicking anything cancels capture (the click itself is handled normally by whatever it hit)
            if (evt is { Kind: BufferedInputKind.MouseButton, IsPress: true })
            {
                EndCapture();

                return;
            }

            if (evt.Kind != BufferedInputKind.KeyDown)
                continue;

            //Escape cancels; bare modifier presses are ignored until a real key arrives
            if (evt.Key == Keys.Escape)
            {
                EndCapture();

                return;
            }

            if (IsModifierKey(evt.Key))
                continue;

            var mods = evt.Modifiers & (KeyModifiers.Shift | KeyModifiers.Ctrl | KeyModifiers.Alt);
            Keybindings.Set(CapturingCell.Action, CapturingCell.Slot, new KeyBind(evt.Key, mods));
            ClientSettings.Save();
            EndCapture();

            return;
        }
    }

    private static bool IsModifierKey(Keys key)
        => key is Keys.LeftShift or Keys.RightShift or Keys.LeftControl or Keys.RightControl or Keys.LeftAlt or Keys.RightAlt;

    //--- turn modifier ---

    private void CycleTurnModifier()
    {
        var next = Keybindings.TurnModifier switch
        {
            KeyModifiers.Shift => KeyModifiers.Ctrl,
            KeyModifiers.Ctrl  => KeyModifiers.Alt,
            KeyModifiers.Alt   => KeyModifiers.None,
            _                  => KeyModifiers.Shift
        };

        SetTurnModifier(next);
    }

    private void SetTurnModifier(KeyModifiers mod)
    {
        Keybindings.TurnModifier = mod;
        ClientSettings.Save();
        RefreshAll();
    }

    private static string TurnText()
        => Keybindings.TurnModifier switch
        {
            KeyModifiers.Shift => "Shift",
            KeyModifiers.Ctrl  => "Ctrl",
            KeyModifiers.Alt   => "Alt",
            _                  => "Off"
        };

    //--- reset / refresh ---

    private void ResetAll()
    {
        if (CapturingCell is not null)
            EndCapture();

        Keybindings.ResetDefaults();
        ClientSettings.Save();
        RefreshAll();
    }

    private void RefreshAll()
    {
        foreach (var cell in BindCells)
        {
            if (cell == CapturingCell)
            {
                cell.Text = "press a key";

                continue;
            }

            var (primary, secondary) = Keybindings.Get(cell.Action);
            var bind = cell.Slot == 0 ? primary : secondary;
            cell.Text = bind.IsBound ? bind.Display() : "-";
        }

        if (TurnCell is not null)
            TurnCell.Text = TurnText();
    }

    //single point that moves the list: clamps the pixel offset, scrolls the rows, and syncs the scrollbar thumb. Driven
    //both by the scrollbar (OnValueChanged) and by the window-wide mouse wheel (OnMouseScroll).
    private void ApplyScroll(int value)
    {
        var maxScroll = Math.Max(0, ContentHeight - Viewport.Height);
        ScrollOffset = Math.Clamp(value, 0, maxScroll);
        RowsHost.Y = -ScrollOffset;
        ScrollBar.Value = ScrollOffset; //plain property set; does not re-fire OnValueChanged, so no feedback loop
    }

    public override void OnMouseScroll(MouseScrollEvent e)
    {
        if (ContentHeight <= Viewport.Height)
            return;

        ApplyScroll(ScrollOffset - Math.Sign(e.Delta) * ROW_H * 3);
        e.Handled = true;
    }
}
