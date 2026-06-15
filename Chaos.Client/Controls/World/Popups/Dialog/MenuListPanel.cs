#region
using Chaos.Client.Collections;
using Chaos.Client.Controls.Components;
using Chaos.Client.Controls.Generic;
using Chaos.Client.Networking.Definitions;
using Chaos.Client.ViewModel;
using Chaos.DarkAges.Definitions;
using Chaos.Networking.Entities.Server;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
#endregion

namespace Chaos.Client.Controls.World.Popups.Dialog;

/// <summary>
///     Two-column scrollable icon+name list panel for ShowPlayerItems, ShowPlayerSkills, ShowPlayerSpells, ShowSkills, and
///     ShowSpells menu types. Uses the lnpcd2 prefab with 9-slice frame. Each cell shows an icon and the entity name.
///     Visually matches NPCListMenuDialog from the original client.
/// </summary>
public sealed class MenuListPanel : FramedDialogPanelBase
{
    private const int ICON_SIZE = 32;
    private const int ROW_HEIGHT = 32;
    private const int ICON_TEXT_GAP = 4;
    private const int COLUMN_COUNT = 2;

    //TTF (Cinzel) size for the entry names when the TrueType font is available; otherwise the bitmap font (size 0)
    private const int NAME_FONT = 11;

    //content area from lnpcd2 template (relative to panel)
    private const int CONTENT_X = 13;
    private const int CONTENT_Y = 6;
    private const int CONTENT_WIDTH = 400;
    private const int CONTENT_HEIGHT = 160;

    //panel sizing
    private const int PANEL_WIDTH = 426;
    private const int MAX_VISIBLE_ROWS = CONTENT_HEIGHT / ROW_HEIGHT;

    //one extra row for the partially-visible peek effect at the bottom
    private const int DISPLAY_ROWS = MAX_VISIBLE_ROWS + 1;

    //border metrics (from frameddialogpanel)
    private const int BORDER_BOTTOM = 30;
    private const int BTN_WIDTH = 61;
    private const int BTN_HEIGHT = 22;

    //bottom of panel aligns with top of the dialog bottom bar
    private const int BOTTOM_ANCHOR_Y = 372;

    private static readonly Color SELECTED_TEXT_COLOR = new(206, 0, 16);
    private static readonly Color GREYED_COLOR = new(120, 120, 120);
    private readonly List<ListEntryData> Entries = [];

    private readonly List<ListEntryControl> EntryControls = [];
    private readonly UIPanel ContentContainer;
    private readonly ScrollBarControl ScrollBar;

    private int ColumnWidth;
    private int ScrollOffset;
    private int SelectedIndex = -1;
    private MenuType CurrentMenuType;

    private int TotalRows => (Entries.Count + COLUMN_COUNT - 1) / COLUMN_COUNT;

    public MenuListPanel()
        : base("lnpcd2", false)
    {
        Name = "ListMenu";
        Visible = false;


        OkButton = CreateButton("Btn1");

        if (OkButton is not null)
        {
            OkButton.Enabled = false;

            OkButton.Clicked += () =>
            {
                if (SelectedIndex >= 0)
                    OnItemSelected?.Invoke(SelectedIndex);
            };
        }

        ContentContainer = new UIPanel
        {
            Name = "ContentContainer",
            X = CONTENT_X,
            Y = CONTENT_Y,
            Width = CONTENT_WIDTH,
            Height = CONTENT_HEIGHT,
            IsPassThrough = true
        };

        AddChild(ContentContainer);

        ScrollBar = new ScrollBarControl
        {
            Name = "ListScrollBar",
            X = CONTENT_X + CONTENT_WIDTH - ScrollBarControl.DEFAULT_WIDTH,
            Y = CONTENT_Y,
            Height = CONTENT_HEIGHT,
            Visible = false
        };

        ScrollBar.OnValueChanged += value =>
        {
            ScrollOffset = value;
            SelectedIndex = -1;
            LayoutEntries();
        };

        AddChild(ScrollBar);
    }

    private void AddEntry(string name, byte slot, Texture2D? icon)
    {
        var displayName = name.Length > 23 ? name[..20] + "..." : name;

        Entries.Add(
            new ListEntryData(
                name,
                displayName,
                slot,
                icon));
    }

    public string? GetEntryName(int index)
    {
        if ((index < 0) || (index >= Entries.Count))
            return null;

        return Entries[index].OriginalName;
    }

    public byte? GetEntrySlot(int index)
    {
        if ((index < 0) || (index >= Entries.Count))
            return null;

        return Entries[index].Slot;
    }

    public override void Hide()
    {

        foreach (var control in EntryControls)
        {
            ContentContainer.Children.Remove(control);
            control.Dispose();
        }

        EntryControls.Clear();
        Entries.Clear();
        ScrollOffset = 0;
        SelectedIndex = -1;
        Visible = false;
    }

    /// <summary>
    ///     Draws every visible entry's name crisp at NATIVE resolution over the magnified frame, like the dialog text.
    ///     Called from <see cref="NpcSessionControl.DrawTextNative" />. The list snaps closed (no fade), so this simply
    ///     stops drawing the instant the panel hides - it is gated on the panel's visibility by the caller.
    /// </summary>
    public void DrawTextNative(SpriteBatch spriteBatch, int originX, int originY, float scale, float alpha)
    {
        if ((scale <= 0f) || (alpha <= 0f) || !TtfTextRenderer.Available)
            return;

        foreach (var control in EntryControls)
            control.DrawTextNative(spriteBatch, originX, originY, scale, alpha);
    }

    private void LayoutEntries()
    {
        var firstEntry = ScrollOffset * COLUMN_COUNT;
        var controlIndex = 0;

        for (var row = 0; row < DISPLAY_ROWS; row++)
        {
            for (var col = 0; col < COLUMN_COUNT; col++)
            {
                if (controlIndex >= EntryControls.Count)
                    return;

                var control = EntryControls[controlIndex];
                var entryIndex = firstEntry + row * COLUMN_COUNT + col;

                if (entryIndex < Entries.Count)
                {
                    var entry = Entries[entryIndex];
                    var isSelected = entryIndex == SelectedIndex;

                    control.X = col * ColumnWidth;
                    control.Y = row * ROW_HEIGHT;
                    control.SetEntry(entryIndex, entry.Icon, entry.DisplayName, isSelected, entry.Greyed);
                    control.Visible = true;

                    //clip the peek row's hit-test area to the content bounds
                    var maxHeight = CONTENT_HEIGHT - control.Y;
                    control.Height = Math.Min(ROW_HEIGHT, maxHeight);
                } else
                {
                    control.ClearEntry();
                    control.Visible = false;
                }

                controlIndex++;
            }
        }

        //hide remaining controls
        for (; controlIndex < EntryControls.Count; controlIndex++)
        {
            EntryControls[controlIndex]
                .ClearEntry();
            EntryControls[controlIndex].Visible = false;
        }
    }

    public event CloseHandler? OnClose;
    public event ItemSelectedHandler? OnItemSelected;

    private void PopulatePlayerItems(DisplayMenuArgs args)
    {
        var renderer = UiRenderer.Instance!;

        if (args.Slots is not null)
            foreach (var slot in args.Slots)
            {
                ref readonly var slotData = ref WorldState.Inventory.GetSlot(slot);

                if (!slotData.IsOccupied)
                    continue;

                var icon = renderer.GetItemIcon(slotData.Sprite, slotData.Color);
                AddEntry(slotData.Name ?? string.Empty, slot, icon);
            }

        if (args.Items is not null)
            foreach (var item in args.Items)
            {
                var icon = renderer.GetItemIcon(item.Sprite, item.Color);
                AddGreyedEntry(item.Name, icon);
            }
    }

    private void AddGreyedEntry(string name, Texture2D? icon)
    {
        var displayName = name.Length > 23 ? name[..20] + "..." : name;

        Entries.Add(
            new ListEntryData(
                name,
                displayName,
                0,
                icon,
                Greyed: true));
    }

    private void PopulatePlayerSkills()
    {
        var renderer = UiRenderer.Instance!;

        for (byte slot = 1; slot <= SkillBook.MAX_SLOTS; slot++)
        {
            ref readonly var slotData = ref WorldState.SkillBook.GetSlot(slot);

            if (!slotData.IsOccupied)
                continue;

            var icon = renderer.GetSkillIcon(slotData.Sprite);
            AddEntry(slotData.Name ?? string.Empty, slot, icon);
        }
    }

    private void PopulatePlayerSpells()
    {
        var renderer = UiRenderer.Instance!;

        for (byte slot = 1; slot <= SpellBook.MAX_SLOTS; slot++)
        {
            ref readonly var slotData = ref WorldState.SpellBook.GetSlot(slot);

            if (!slotData.IsOccupied)
                continue;

            var icon = renderer.GetSpellIcon(slotData.Sprite);
            AddEntry(slotData.Name ?? string.Empty, slot, icon);
        }
    }

    private void PopulateSkills(DisplayMenuArgs args)
    {
        if (args.Skills is null)
            return;

        var renderer = UiRenderer.Instance!;

        foreach (var skill in args.Skills)
        {
            var icon = renderer.GetSkillIcon(skill.Sprite);
            AddEntry(skill.Name, skill.Slot, icon);
        }
    }

    private void PopulateSpells(DisplayMenuArgs args)
    {
        if (args.Spells is null)
            return;

        var renderer = UiRenderer.Instance!;

        foreach (var spell in args.Spells)
        {
            var icon = renderer.GetSpellIcon(spell.Sprite);
            AddEntry(spell.Name, spell.Slot, icon);
        }
    }

    //SWM craft menu: server-provided items (a ShowItems payload) rendered with this list panel,
    //item icons and all - the workbench's recipe catalog
    private void PopulateCraftItems(DisplayMenuArgs args)
    {
        if (args.Items is null)
            return;

        var renderer = UiRenderer.Instance!;

        foreach (var item in args.Items)
        {
            var icon = renderer.GetItemIcon(item.Sprite, item.Color);
            AddEntry(item.Name, 0, icon);
        }
    }

    public void ShowList(DisplayMenuArgs args)
    {
        Hide();
        CurrentMenuType = args.MenuType;

        switch (args.MenuType)
        {
            case MenuType.ShowPlayerItems:
                PopulatePlayerItems(args);

                break;

            case MenuType.ShowPlayerSkills:
                PopulatePlayerSkills();

                break;

            case MenuType.ShowPlayerSpells:
                PopulatePlayerSpells();

                break;

            case MenuType.ShowSkills:
                PopulateSkills(args);

                break;

            case MenuType.ShowSpells:
                PopulateSpells(args);

                break;

            case SwmProtocol.CraftMenu:
                PopulateCraftItems(args);

                break;
        }

        //column width based on scrollbar presence
        var needsScroll = TotalRows > MAX_VISIBLE_ROWS;
        var availableWidth = needsScroll ? CONTENT_WIDTH - ScrollBarControl.DEFAULT_WIDTH : CONTENT_WIDTH;
        ColumnWidth = availableWidth / COLUMN_COUNT;

        //fixed panel size
        var contentHeight = MAX_VISIBLE_ROWS * ROW_HEIGHT;
        Height = CONTENT_Y + contentHeight + BORDER_BOTTOM;
        Width = PANEL_WIDTH;

        //right-aligned, bottom-anchored above dialog bar
        X = ChaosGame.VIRTUAL_WIDTH - Width;
        Y = BOTTOM_ANCHOR_Y - Height;

        //ok button positioning
        if (OkButton is not null)
        {
            OkButton.X = Width - BTN_WIDTH - 20;
            OkButton.Y = Height - BTN_HEIGHT - 3;
            OkButton.Enabled = false;
        }

        //scrollbar
        ScrollBar.Visible = needsScroll;
        ScrollBar.Enabled = needsScroll;
        ScrollBar.Y = CONTENT_Y;
        ScrollBar.Height = contentHeight + 2;

        if (needsScroll)
        {
            ScrollBar.TotalItems = TotalRows;
            ScrollBar.VisibleItems = MAX_VISIBLE_ROWS;
            ScrollBar.MaxValue = TotalRows - MAX_VISIBLE_ROWS;
            ScrollBar.Value = 0;
        }

        //resize container to match available content width
        ContentContainer.Width = availableWidth;

        //create entry controls for visible slots (including peek row)
        var visibleCount = Math.Min(DISPLAY_ROWS * COLUMN_COUNT, Entries.Count);

        for (var i = 0; i < visibleCount; i++)
        {
            var control = new ListEntryControl(ColumnWidth)
            {
                Name = $"Entry_{i}",
                Clicked = HandleEntryClicked,
                DoubleClicked = HandleEntryDoubleClicked
            };

            EntryControls.Add(control);
            ContentContainer.AddChild(control);
        }

        LayoutEntries();
        Visible = true;
    }

    private void HandleEntryClicked(int entryIndex)
    {
        if ((entryIndex < 0) || (entryIndex >= Entries.Count))
            return;

        if (Entries[entryIndex].Greyed)
            return;

        SelectedIndex = entryIndex;
        LayoutEntries();

        if (OkButton is not null)
            OkButton.Enabled = true;
    }

    private void HandleEntryDoubleClicked(int entryIndex)
    {
        if ((entryIndex < 0) || (entryIndex >= Entries.Count))
            return;

        if (Entries[entryIndex].Greyed)
            return;

        SelectedIndex = entryIndex;
        LayoutEntries();
        OnItemSelected?.Invoke(SelectedIndex);
    }

    /// <summary>
    ///     Returns the ability name and isSpell flag for the entry at panel-space coordinates, or null if not over an
    ///     entry. Called each frame from the WorldScreen tooltip resolver (polled, same as book/stats tooltips).
    /// </summary>
    public (string Name, bool IsSpell)? HitTeachAbility(int panelX, int panelY)
    {
        if (!Visible || CurrentMenuType is not (MenuType.ShowSkills or MenuType.ShowSpells))
            return null;

        var localX = panelX - ScreenX - CONTENT_X;
        var localY = panelY - ScreenY - CONTENT_Y;

        if (localX < 0 || localX >= ContentContainer.Width || localY < 0 || localY >= CONTENT_HEIGHT)
            return null;

        var col = ColumnWidth > 0 ? Math.Min(localX / ColumnWidth, COLUMN_COUNT - 1) : 0;
        var row = localY / ROW_HEIGHT;
        var entryIndex = (ScrollOffset + row) * COLUMN_COUNT + col;

        if (entryIndex < 0 || entryIndex >= Entries.Count)
            return null;

        var name = Entries[entryIndex].OriginalName;

        return name.Length > 0 ? (name, CurrentMenuType == MenuType.ShowSpells) : null;
    }

    public override void OnKeyDown(KeyDownEvent e)
    {
        if (e.Key == Keys.Escape)
        {
            Hide();
            OnClose?.Invoke();
            e.Handled = true;
            return;
        }

        if (e.Key is Keys.Enter or Keys.Space && SelectedIndex >= 0)
        {
            OnItemSelected?.Invoke(SelectedIndex);
            e.Handled = true;
        }
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
            SelectedIndex = -1;
            LayoutEntries();
        }

        e.Handled = true;
    }

    private sealed class ListEntryControl : UIPanel
    {
        private readonly UIImage IconImage;
        private readonly UILabel NameLabel;

        public int EntryIndex { get; private set; } = -1;

        public Action<int>? Clicked { get; set; }
        public Action<int>? DoubleClicked { get; set; }

        public ListEntryControl(int columnWidth)
        {
            Width = columnWidth;
            Height = ROW_HEIGHT;

            IconImage = new UIImage
            {
                X = 0,
                Y = (ROW_HEIGHT - ICON_SIZE) / 2,
                Width = ICON_SIZE,
                Height = ICON_SIZE
            };

            var useTtf = TtfTextRenderer.Available;
            var lineH = useTtf ? TtfTextRenderer.LineHeight(NAME_FONT) : TextRenderer.CHAR_HEIGHT;

            NameLabel = new UILabel
            {
                X = ICON_SIZE + ICON_TEXT_GAP,
                Y = (ROW_HEIGHT - lineH) / 2,
                Width = columnWidth - ICON_SIZE - ICON_TEXT_GAP,
                Height = lineH,
                CustomFontSize = useTtf ? NAME_FONT : 0,
                SuppressGlyphs = useTtf,
                ForegroundColor = LegendColors.White
            };

            AddChild(IconImage);
            AddChild(NameLabel);
        }

        public void ClearEntry()
        {
            EntryIndex = -1;
            IconImage.Visible = false;
            NameLabel.Text = string.Empty;
        }

        public void SetEntry(int entryIndex, Texture2D? icon, string name, bool selected, bool greyed = false)
        {
            EntryIndex = entryIndex;
            IconImage.Texture = icon;
            IconImage.Visible = icon is not null;
            NameLabel.Text = name;
            NameLabel.ForegroundColor = greyed ? GREYED_COLOR : selected ? SELECTED_TEXT_COLOR : LegendColors.White;
        }

        //draws this entry's name crisp at native resolution (only while the entry slot is showing an item)
        public void DrawTextNative(SpriteBatch spriteBatch, int originX, int originY, float scale, float alpha)
        {
            if (Visible)
                MenuShopPanel.DrawLabelNative(spriteBatch, NameLabel, originX, originY, scale, alpha);
        }

        //handle click on the entry directly, the outer MenuListPanel can't see clicks that bubble
        //through us because UIPanel absorbs click events by default. matches MerchantListingPanel
        public override void OnClick(ClickEvent e)
        {
            if ((e.Button != MouseButton.Left) || (EntryIndex < 0))
                return;

            Clicked?.Invoke(EntryIndex);
            e.Handled = true;
        }

        public override void OnDoubleClick(DoubleClickEvent e)
        {
            if ((e.Button != MouseButton.Left) || (EntryIndex < 0))
                return;

            DoubleClicked?.Invoke(EntryIndex);
            e.Handled = true;
        }
    }

    private sealed record ListEntryData(
        string OriginalName,
        string DisplayName,
        byte Slot,
        Texture2D? Icon,
        bool Greyed = false);
}