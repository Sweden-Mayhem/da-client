#region
using Chaos.Client.Collections;
using Chaos.Client.Controls.Components;
using Chaos.Client.Controls.Generic;
using Chaos.Client.Data.Models;
using Chaos.Client.Models;
using Chaos.DarkAges.Definitions;
using Chaos.Extensions.Common;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
#endregion

namespace Chaos.Client.Controls.World.Popups.WorldList;

/// <summary>
///     Online users list panel loaded from _nusers prefab. A free-floating draggable window (like Group / Equipment /
///     Friends): it follows the Window size scale, opens centered with no slide animation, and is dragged by its
///     background art (the wrapping <c>ScaleHost</c> owns placement). Shows a scrollable user list and 9 class filter tabs.
/// </summary>
public sealed class WorldListControl : PrefabPanel
{
    private const int ROW_HEIGHT = 14;
    private const int TAB_COUNT = 9;

    private const int STATUS_ICON_COUNT = 8;
    private readonly List<WorldListEntry> FilterBuffer = [];

    private readonly int MaxVisibleRows;
    private readonly WorldListEntryControl[] RowEntries;
    private readonly ScrollBarControl ScrollBar;
    private readonly Texture2D?[] StatusIcons = new Texture2D?[STATUS_ICON_COUNT];

    //tab buttons
    private readonly UIButton[] TabButtons = new UIButton[TAB_COUNT];
    private readonly UILabel[] TabCountLabels = new UILabel[TAB_COUNT];

    //hover help per class-filter tab, in tab order (matches the ActiveTab switch in ApplyFilter)
    private static readonly string[] TabTooltips =
    [
        "All\nEveryone currently online.",
        "Masters\nPlayers who have reached master rank.",
        "Warriors\nOnline players of the Warrior class.",
        "Rogues\nOnline players of the Rogue class.",
        "Wizards\nOnline players of the Wizard class.",
        "Priests\nOnline players of the Priest class.",
        "Monks\nOnline players of the Monk class.",
        "Peasants\nPlayers who have not yet chosen a class.",
        "Guilded\nOnline players who belong to a guild."
    ];
    private readonly UILabel TotalNumLabel;

    private readonly Rectangle UsersListRect;

    private int ActiveTab;

    //player data
    private IReadOnlyList<WorldListEntry> AllEntries = [];
    private HashSet<string> FamilyNames = new(StringComparer.OrdinalIgnoreCase);
    private HashSet<string> FriendNames = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<WorldListEntry> FilteredEntries = [];
    private bool RowsDirty;
    private int ScrollOffset;

    private ushort TotalOnline;

    private static string PlayerName => WorldState.PlayerName;

    public WorldListControl()
        : base("_nusers", false)
    {
        Name = "WorldList";
        Visible = false;
        UsesControlStack = true;

        //pass-through so empty background art falls through to the draggable ScaleHost that wraps this window
        IsPassThrough = true;

        //userslist rect
        UsersListRect = GetRect("UsersList");
        MaxVisibleRows = UsersListRect.Height > 0 ? UsersListRect.Height / ROW_HEIGHT : 0;

        //scrollbar
        ScrollBar = new ScrollBarControl
        {
            Name = "ScrollBar",
            X = UsersListRect.X + UsersListRect.Width - ScrollBarControl.DEFAULT_WIDTH,
            Y = UsersListRect.Y - 3,
            Height = UsersListRect.Height + 6
        };

        ScrollBar.OnValueChanged += v =>
        {
            ScrollOffset = v;
            RowsDirty = true;
        };
        AddChild(ScrollBar);

        //count labels
        var totalNumRect = GetRect("TotalNum");
        var countryNumRect = GetRect("CountryNum");

        TotalNumLabel = new UILabel
        {
            Name = "TotalNumLabel",
            X = totalNumRect.X,
            Y = totalNumRect.Y,
            Width = totalNumRect.Width,
            Height = totalNumRect.Height,
            HorizontalAlignment = HorizontalAlignment.Right,
            PaddingLeft = 0,
            PaddingTop = 0,
            IsHitTestVisible = false
        };

        //crisp TTF at native res via WorldScreen's generic menu-text pass (this window is hosted in a magnifier)
        TotalNumLabel.Native(10);
        TotalNumLabel.ForegroundColor = Color.White;
        TotalNumLabel.Text = "0";
        AddChild(TotalNumLabel);

        //row entries
        var rowWidth = UsersListRect.Width - ScrollBarControl.DEFAULT_WIDTH - 5;
        RowEntries = new WorldListEntryControl[MaxVisibleRows];

        for (var i = 0; i < MaxVisibleRows; i++)
        {
            RowEntries[i] = new WorldListEntryControl(rowWidth)
            {
                Name = $"Row{i}",
                X = UsersListRect.X,
                Y = UsersListRect.Y + i * ROW_HEIGHT,
                Width = rowWidth,
                Visible = false
            };

            RowEntries[i].OnWhisper += name => OnWhisperRequested?.Invoke(name);
            AddChild(RowEntries[i]);
        }

        //social status icons from _nemots.spf (frame 0 of each 3-frame group)
        LoadStatusIcons();

        //tab buttons -built from _nusersb.spf frames (9 tabs x 2 states)
        var countryBtnRect = GetRect("CountryBtn");
        var masterBtnRect = GetRect("MasterBtn");

        //spacing taken from the y gap between the first two prefab buttons
        var tabStride = masterBtnRect.Y - countryBtnRect.Y;

        if (tabStride <= 0)
            tabStride = 22;

        //label stride taken from the y gap between first two prefab labels (same stride)
        var labelStride = tabStride;

        var cache = UiRenderer.Instance!;

        for (var i = 0; i < TAB_COUNT; i++)
        {
            var tabIndex = i;
            var normalIdx = i * 2;
            var activeIdx = i * 2 + 1;

            TabButtons[i] = new UIButton
            {
                Name = $"Tab{i}",
                X = countryBtnRect.X,
                Y = countryBtnRect.Y + i * tabStride,
                Width = countryBtnRect.Width,
                Height = countryBtnRect.Height,
                NormalTexture = cache.GetSpfTexture("_nusersb.spf", normalIdx),
                SelectedTexture = cache.GetSpfTexture("_nusersb.spf", activeIdx)
            };

            TabButtons[i].Clicked += () => SelectTab(tabIndex);
            TabButtons[i].Tooltip = TabTooltips[i];
            AddChild(TabButtons[i]);

            TabCountLabels[i] = new UILabel
            {
                Name = $"TabCount{i}",
                X = countryNumRect.X,
                Y = countryNumRect.Y + i * labelStride,
                Width = countryNumRect.Width,
                Height = countryNumRect.Height,
                HorizontalAlignment = HorizontalAlignment.Right,
                PaddingLeft = 0,
                PaddingTop = 0,
                IsHitTestVisible = false
            };

            TabCountLabels[i].Native(10);
            TabCountLabels[i].ForegroundColor = Color.White;
            AddChild(TabCountLabels[i]);
        }

        TabButtons[0].IsSelected = true;

        //close button
        var closeButton = CreateButton("Close");

        if (closeButton is not null)
        {
            closeButton.Clicked += Close;
            closeButton.Tooltip = "Close\nCloses the online players list.";
        }

        WorldState.WorldList.Changed += OnWorldListChanged;
    }

    private void ApplyFilter()
    {
        if (ActiveTab == 0)
            FilteredEntries = AllEntries;
        else
        {
            FilterBuffer.Clear();

            foreach (var e in AllEntries)
            {
                var match = ActiveTab switch
                {
                    1 => e.IsMaster,
                    2 => e.BaseClass == BaseClass.Warrior,
                    3 => e.BaseClass == BaseClass.Rogue,
                    4 => e.BaseClass == BaseClass.Wizard,
                    5 => e.BaseClass == BaseClass.Priest,
                    6 => e.BaseClass == BaseClass.Monk,
                    7 => e.BaseClass == BaseClass.Peasant,
                    8 => e.IsGuilded,
                    _ => false
                };

                if (match)
                    FilterBuffer.Add(e);
            }

            FilteredEntries = FilterBuffer;
        }

        ScrollBar.TotalItems = FilteredEntries.Count;
        ScrollBar.VisibleItems = MaxVisibleRows;
        ScrollBar.MaxValue = Math.Max(0, FilteredEntries.Count - MaxVisibleRows);
        ScrollBar.Value = ScrollOffset;
    }

    private void AutoScrollToSelf()
    {
        if ((PlayerName.Length == 0) || (MaxVisibleRows <= 0))
            return;

        for (var i = 0; i < FilteredEntries.Count; i++)
            if (FilteredEntries[i]
                .Name
                .EqualsI(PlayerName))
            {
                ScrollOffset = Math.Max(0, i - MaxVisibleRows / 2);

                if (FilteredEntries.Count > MaxVisibleRows)
                    ScrollOffset = Math.Min(ScrollOffset, FilteredEntries.Count - MaxVisibleRows);

                return;
            }
    }

    public override void Dispose()
    {
        WorldState.WorldList.Changed -= OnWorldListChanged;

        foreach (var icon in StatusIcons)
            icon?.Dispose();

        base.Dispose();
    }

    public override void Draw(SpriteBatch spriteBatch)
    {
        if (!Visible)
            return;

        base.Draw(spriteBatch);
    }

    public override void Hide()
    {
        InputDispatcher.Instance?.RemoveControl(this);
        Visible = false;
    }

    //close + notify (the close button, Escape, and the menu/key toggle all route here)
    private void Close()
    {
        Hide();
        OnClose?.Invoke();
    }

    private void LoadStatusIcons()
    {
        var cache = UiRenderer.Instance!;

        for (var i = 0; i < STATUS_ICON_COUNT; i++)
            StatusIcons[i] = cache.GetSpfTexture("_nemots.spf", i);
    }

    private static Color MapWorldListColor(WorldListColor color)
    {
        if (color == WorldListColor.Invisble)
            return Color.Transparent;

        if (color == WorldListColor.White)
            return LegendColors.White;

        return LegendColors.Get((int)color);
    }

    public event CloseHandler? OnClose;
    public event WhisperRequestedHandler? OnWhisperRequested;

    public void SetFamilyNames(FamilyList? family)
    {
        FamilyNames.Clear();

        if (family is null)
            return;

        foreach (var name in new[]
                 {
                     family.Mother,
                     family.Father,
                     family.Son1,
                     family.Son2,
                     family.Brother1,
                     family.Brother2,
                     family.Brother3,
                     family.Brother4,
                     family.Brother5,
                     family.Brother6
                 })
            if (!string.IsNullOrWhiteSpace(name))
                FamilyNames.Add(name);
    }

    public void SetFriendNames(IReadOnlyList<string> friends)
    {
        FriendNames.Clear();

        foreach (var name in friends)
            if (!string.IsNullOrWhiteSpace(name))
                FriendNames.Add(name);
    }

    private void OnWorldListChanged() => Show(WorldState.WorldList.Entries, WorldState.WorldList.TotalOnline);

    private void RefreshRowEntries()
    {
        for (var i = 0; i < MaxVisibleRows; i++)
        {
            var entryIndex = ScrollOffset + i;

            if (entryIndex < FilteredEntries.Count)
            {
                var entry = FilteredEntries[entryIndex];

                var nameColor = FamilyNames.Contains(entry.Name)
                    ? LegendColors.HotPink
                    : FriendNames.Contains(entry.Name)
                        ? LegendColors.Lime
                        : MapWorldListColor(entry.Color);

                var statusIdx = (int)entry.SocialStatus;
                var statusIcon = (statusIdx >= 0) && (statusIdx < StatusIcons.Length) ? StatusIcons[statusIdx] : null;

                RowEntries[i]
                    .SetEntry(entry, statusIcon, nameColor);
            } else
                RowEntries[i]
                    .Clear();
        }
    }

    private void SelectTab(int tab)
    {
        TabButtons[ActiveTab].IsSelected = false;
        ActiveTab = tab;
        TabButtons[ActiveTab].IsSelected = true;
        ScrollOffset = 0;
        ApplyFilter();
        UpdateCountLabels();
        RowsDirty = true;
    }

    //updates the data and opens the window (placement + scale owned by the wrapping ScaleHost; it centers on first open)
    public void Show(IReadOnlyList<WorldListEntry> entries, ushort totalOnline)
    {
        AllEntries = entries;
        TotalOnline = totalOnline;
        ActiveTab = 0;
        TabButtons[0].IsSelected = true;
        ScrollOffset = 0;

        ApplyFilter();
        UpdateCountLabels();
        AutoScrollToSelf();
        ScrollBar.Value = ScrollOffset;
        RowsDirty = true;

        if (!Visible)
        {
            InputDispatcher.Instance?.PushControl(this);
            Visible = true;
        }
    }

    public override void Update(GameTime gameTime)
    {
        if (!Visible || !Enabled)
            return;

        if (RowsDirty)
        {
            RefreshRowEntries();
            RowsDirty = false;
        }

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

    public override void OnMouseScroll(MouseScrollEvent e)
    {
        if (ScrollBar.TotalItems <= ScrollBar.VisibleItems)
            return;

        var newValue = Math.Clamp(ScrollBar.Value - e.Delta, 0, ScrollBar.MaxValue);

        if (newValue != ScrollBar.Value)
        {
            ScrollBar.Value = newValue;
            ScrollOffset = newValue;
            RowsDirty = true;
        }

        e.Handled = true;
    }

    private void UpdateCountLabels()
    {
        TotalNumLabel.Text = $"{TotalOnline}";
        var counts = new int[TAB_COUNT];
        counts[0] = AllEntries.Count;

        foreach (var entry in AllEntries)
        {
            if (entry.IsMaster)
                counts[1]++;

            if (entry.IsGuilded)
                counts[8]++;

            switch (entry.BaseClass)
            {
                case BaseClass.Warrior:
                    counts[2]++;

                    break;
                case BaseClass.Rogue:
                    counts[3]++;

                    break;
                case BaseClass.Wizard:
                    counts[4]++;

                    break;
                case BaseClass.Priest:
                    counts[5]++;

                    break;
                case BaseClass.Monk:
                    counts[6]++;

                    break;
                case BaseClass.Peasant:
                    counts[7]++;

                    break;
                default:
                    continue;
            }
        }

        for (var i = 0; i < TAB_COUNT; i++)
            TabCountLabels[i].Text = $"{counts[i]}";
    }
}