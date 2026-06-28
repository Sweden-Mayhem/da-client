#region
using Chaos.Client.Controls.Components;
using Chaos.Client.Controls.Generic;
using Chaos.Client.Controls.World.Menu;
using Chaos.Client.Data;
using Chaos.Client.Data.Models;
using Chaos.Client.Definitions;
using Chaos.Client.Networking;
using Chaos.Client.Rendering;
using Chaos.DarkAges.Definitions;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
#endregion

namespace Chaos.Client.Controls.World.Popups.Market;

/// <summary>
///     The Temuair Exchange. A standalone floating window (like Options) that the server feeds with
///     <see cref="MarketDataArgs" /> screens. The left side is a category tree (Browse) or a list (the other tabs); the
///     right side shows the selected item's detail + every seller, with Buy / Buy now / Bid actions. Every action is a
///     <see cref="MarketRequestArgs" /> raised through <see cref="OnRequest" />; the server answers with another screen.
/// </summary>
public sealed class MarketWindow : DraggableWindow
{
    private const int WIN_W = 900;
    private const int WIN_H = 580;
    private const int TABBAR_H = 34;     //big, prominent tabs
    private const int TAB_FONT = 16;
    private const int GOLD_W = 170;
    private const int SIDEBAR_W = 304;
    private const int PAD = 8;
    private const int ROW_H = 36;       //taller, clearer tree rows
    private const int ROW_ICON = 30;
    private const int FONT = 14;        //match the Options window body font
    private const int MIN_FIT_FONT = 9; //names/prices shrink down to this to avoid clipping/overlap
    private const int NAME_FONT = 17;
    private const int DETAIL_ICON = 72; //the right-side item icon, ~2x the row icon
    private const int BTN_H = 26;
    private const int FIELD_LABEL_W = 150; //sell-form label column (fits "Buy-now (0 = none)")

    private static readonly Color SidebarBg = new(14, 12, 9);
    private static readonly Color Divider = new(60, 50, 34);
    private static readonly Color RowHover = new(34, 29, 19);
    private static readonly Color RowSelected = new(54, 45, 28);
    private static readonly Color TextNormal = new(214, 204, 178);
    private static readonly Color TextDim = new(150, 142, 124);
    private static readonly Color TextGold = new(224, 192, 108);
    private static readonly Color TextSel = new(255, 224, 138);
    private static readonly Color TextHint = new(110, 102, 86);
    private static readonly Color GoodGreen = new(150, 210, 130);
    private static readonly Color AuctionBlue = new(150, 186, 236);

    //match the item tooltip's palette so the market detail reads identically: white name, grey category/weight, yellow desc
    private static readonly Color NameCol = new(240, 238, 232);
    private static readonly Color CatCol = new(158, 156, 148);
    private static readonly Color DescCol = new(230, 212, 140);

    private enum Tab
    {
        Browse,
        Sell,
        Listings,
        Bids,
        Storage
    }

    //tab bar
    private readonly Dictionary<Tab, MenuButton> TabButtons = new();
    private readonly UILabel GoldLabel;
    private readonly UILabel ToastLabel;
    private float ToastTimer;

    //sidebar (left) and detail (right)
    private readonly ScrollRegion Sidebar;
    private readonly UITextBox SearchBox;
    private readonly UILabel SearchPlaceholder;
    private readonly MenuButton SearchClear;
    private readonly ScrollRegion Detail;
    private readonly int BodyY;
    private readonly int BodyH;
    private readonly int SearchAreaH;

    //true when the window was opened by a market NPC (Colm): only then are Sell + Storage actionable. Opened remotely
    //(menu / hotkey / /market) they are read-only - you must visit a clerk to list or collect.
    private bool AtNpc;

    //retained state
    private List<MarketCategoryEntry> Catalog = [];
    private readonly HashSet<string> Expanded = new(StringComparer.OrdinalIgnoreCase);   //browse categories (collapsed by default)
    private readonly HashSet<string> Collapsed = new(StringComparer.OrdinalIgnoreCase);  //sell groups (expanded by default)
    private string? SelectedTemplateKey;
    private string SelectedItemName = string.Empty;
    private MarketListingEntry? SelectedListing; //the row picked in My Listings / My Bids (separate from the Browse sellers)
    private MarketStorageEntry? SelectedStorage; //the row picked in Storage
    private List<MarketListingEntry> Sellers = [];
    private List<MarketListingEntry> MyListings = [];
    private List<MarketListingEntry> MyBids = [];
    private List<MarketStorageEntry> StorageItems = [];
    private List<MarketSellEntry> SellItems = [];
    private uint AvailableGold;
    private Tab Current = Tab.Browse;
    private string LastSearch = string.Empty;

    //sell sub-form state (which inventory item is being listed)
    private MarketSellEntry? SellTarget;
    private bool SellAuction;

    //live "= total Gold" on the fixed-price sell form (qty x price each); refs cleared on every detail rebuild
    private UITextBox? SellQtyBox;
    private UITextBox? SellPriceBox;
    private UILabel? SellTotalLabel;
    private string SellTotalKey = string.Empty;

    //live "= total Gold" for the Buy-cheapest quantity box
    private UITextBox? CheapestQtyBox;
    private UILabel? CheapestTotalLabel;
    private int CheapestUnitPrice;
    private int CheapestStock;
    private string CheapestTotalKey = string.Empty;

    public event Action<MarketRequestArgs>? OnRequest;
    public event Action? OnClosed;

    public MarketWindow()
        : base("Temuair Exchange", WIN_W, WIN_H, useWoodFrame: true)
    {
        CentersOnFirstShow = true;
        FadeOnOpen = true;

        var cw = Content.Width;
        var ch = Content.Height;

        //tab bar across the top
        var tabs = new[]
        {
            (Tab.Browse, "Browse"),
            (Tab.Sell, "Sell"),
            (Tab.Listings, "My Listings"),
            (Tab.Bids, "My Bids"),
            (Tab.Storage, "Storage")
        };

        var tabW = (cw - GOLD_W) / tabs.Length;
        var tx = 0;

        foreach (var (tab, label) in tabs)
        {
            var btn = new MenuButton(label, tabW - 3, TABBAR_H)
            {
                X = tx,
                Y = 0,
                CustomFontSize = TAB_FONT
            };
            var captured = tab;
            btn.Clicked = _ => RequestTab(captured);
            TabButtons[tab] = btn;
            Content.AddChild(btn);
            tx += tabW;
        }

        GoldLabel = new UILabel
        {
            X = cw - GOLD_W + 4,
            Y = 0,
            Width = GOLD_W - 8,
            Height = TABBAR_H,
            CustomFontSize = TAB_FONT,
            ForegroundColor = TextGold,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false
        };
        Content.AddChild(GoldLabel);

        //divider under the tab bar
        Content.AddChild(new UIPanel
        {
            X = 0,
            Y = TABBAR_H + 2,
            Width = cw,
            Height = 1,
            BackgroundColor = Divider,
            IsHitTestVisible = false
        });

        BodyY = TABBAR_H + 5;
        BodyH = ch - BodyY;

        //sidebar background
        var sidebarBg = new UIPanel
        {
            X = 0,
            Y = BodyY,
            Width = SIDEBAR_W,
            Height = BodyH,
            BackgroundColor = SidebarBg,
            IsPassThrough = true
        };
        Content.AddChild(sidebarBg);

        //search box (browse only; hidden on other tabs). A visible box + a magnifier-style placeholder so it reads as
        //a clickable text field. Placeholder is toggled in Update. A "x" button clears the filter.
        const int clearW = 26;
        SearchBox = new UITextBox
        {
            X = PAD,
            Y = BodyY + PAD,
            Width = SIDEBAR_W - 2 * PAD - clearW - 2,
            Height = 24,
            MaxLength = 40,
            CustomFontSize = FONT,
            Prefix = "Find: ",
            BackgroundColor = new Color(26, 23, 17),
            BorderColor = new Color(74, 62, 42),
            FocusedBackgroundColor = new Color(34, 29, 20)
        };
        Content.AddChild(SearchBox);

        SearchClear = new MenuButton("x", clearW, 24)
        {
            X = SIDEBAR_W - PAD - clearW,
            Y = BodyY + PAD,
            CustomFontSize = TAB_FONT
        };
        SearchClear.Clicked = _ => ClearSearch();
        Content.AddChild(SearchClear);

        SearchPlaceholder = new UILabel
        {
            X = PAD + 6,
            Y = BodyY + PAD,
            Width = SIDEBAR_W - 2 * PAD - 10,
            Height = 24,
            CustomFontSize = FONT,
            ForegroundColor = new Color(120, 112, 96),
            VerticalAlignment = VerticalAlignment.Center,
            Text = "Search items...",
            IsHitTestVisible = false
        };
        Content.AddChild(SearchPlaceholder);

        //sidebar scroll region (the tree / list); below the search box on Browse, full height on the other tabs
        SearchAreaH = PAD + 24 + 6;
        Sidebar = new ScrollRegion(SIDEBAR_W, BodyH - SearchAreaH - PAD)
        {
            X = 0,
            Y = BodyY + SearchAreaH
        };
        Content.AddChild(Sidebar);

        //vertical divider
        Content.AddChild(new UIPanel
        {
            X = SIDEBAR_W,
            Y = BodyY,
            Width = 1,
            Height = BodyH,
            BackgroundColor = Divider,
            IsHitTestVisible = false
        });

        //detail scroll region (right)
        Detail = new ScrollRegion(cw - SIDEBAR_W - 1, BodyH - PAD)
        {
            X = SIDEBAR_W + 1,
            Y = BodyY
        };
        Content.AddChild(Detail);

        //toast (status line) floats over the detail top
        ToastLabel = new UILabel
        {
            X = SIDEBAR_W + 1 + PAD,
            Y = BodyY + 2,
            Width = cw - SIDEBAR_W - 1 - 2 * PAD,
            Height = 20,
            CustomFontSize = FONT,
            ForegroundColor = GoodGreen,
            IsHitTestVisible = false,
            Visible = false
        };
        Content.AddChild(ToastLabel);

        SetActiveTab(Tab.Browse);
        LayoutSidebar();
        RebuildSidebar();
        RebuildDetail();
    }

    /// <summary>Applies a server market screen, opening the window if needed.</summary>
    public void Apply(MarketDataArgs data)
    {
        AvailableGold = data.AvailableGold;
        AtNpc = data.AtNpc == 1;
        SelectedListing = null; //a new screen invalidates any My Listings/My Bids row selection (no stale detail across tabs)
        SelectedStorage = null;
        GoldLabel.Text = $"{AvailableGold:N0} Gold";

        if (!string.IsNullOrEmpty(data.Message))
            ShowToast(data.Message);

        switch ((MarketScreen)data.Screen)
        {
            case MarketScreen.Catalog:
                Catalog = data.Categories;
                Current = Tab.Browse;

                break;
            case MarketScreen.Sellers:
                Sellers = data.Listings;
                SelectedItemName = string.IsNullOrEmpty(data.Context) ? SelectedItemName : data.Context;

                if (Sellers.Count > 0)
                    SelectedTemplateKey = Sellers[0].TemplateKey;

                Current = Tab.Browse;

                break;
            case MarketScreen.MyListings:
                MyListings = data.Listings;
                Current = Tab.Listings;

                break;
            case MarketScreen.MyBids:
                MyBids = data.Listings;
                Current = Tab.Bids;

                break;
            case MarketScreen.Storage:
                StorageItems = data.Storage;
                Current = Tab.Storage;

                break;
            case MarketScreen.SellPicker:
                SellItems = data.SellItems;
                SellTarget = null;
                Current = Tab.Sell;

                break;
        }

        SetActiveTab(Current);
        LayoutSidebar();
        RebuildSidebar();
        RebuildDetail();

        if (!Visible)
            Open();
        else
            BringToFront();
    }

    //the search box is only meaningful while browsing; on the other tabs hide it and let the list use the full height
    //(otherwise the list sat below an empty search-box gap).
    private void LayoutSidebar()
    {
        var browsing = Current == Tab.Browse;
        SearchBox.Visible = browsing;
        SearchPlaceholder.Visible = browsing && string.IsNullOrEmpty(SearchBox.Text) && !SearchBox.IsFocused;
        SearchClear.Visible = browsing && !string.IsNullOrEmpty(SearchBox.Text);

        if (browsing)
            Sidebar.SetViewport(BodyY + SearchAreaH, BodyH - SearchAreaH - PAD);
        else
            Sidebar.SetViewport(BodyY + PAD, BodyH - 2 * PAD);
    }

    private void RequestTab(Tab tab)
    {
        switch (tab)
        {
            case Tab.Browse:
                Raise(MarketClientAction.OpenCatalog);

                break;
            case Tab.Sell:
                Raise(MarketClientAction.RequestSellPicker);

                break;
            case Tab.Listings:
                Raise(MarketClientAction.RequestMyListings);

                break;
            case Tab.Bids:
                Raise(MarketClientAction.RequestMyBids);

                break;
            case Tab.Storage:
                Raise(MarketClientAction.RequestStorage);

                break;
        }
    }

    private void SetActiveTab(Tab tab)
    {
        foreach (var (t, btn) in TabButtons)
        {
            var active = t == tab;
            btn.TextColor = active ? TextSel : TextNormal;
            btn.SuppressClickSound = active; //clicking the tab you are already on is silent
        }
    }

    private void Raise(MarketClientAction action, string arg = "", int qty = 0, int price = 0, int buyNow = 0, int hours = 0,
        int maxBid = 0, byte slot = 0, byte listingType = 0, byte fromBank = 0)
        => OnRequest?.Invoke(
            new MarketRequestArgs
            {
                Action = action,
                Arg = arg,
                Quantity = qty,
                Price = price,
                BuyNow = buyNow,
                Hours = hours,
                MaxBid = maxBid,
                Slot = slot,
                ListingType = listingType,
                FromBank = fromBank,
                AtNpc = (byte)(AtNpc ? 1 : 0) //echo the NPC context so the server keeps Sell/Storage enabled within an NPC session
            });

    private void ShowToast(string message)
    {
        ToastLabel.Text = message;
        ToastLabel.Visible = true;
        ToastTimer = 4f;
    }

    #region sidebar build
    private void RebuildSidebar()
    {
        Sidebar.Clear();
        var y = 0;

        switch (Current)
        {
            case Tab.Browse:
                y = BuildTree();

                break;
            case Tab.Sell:
                y = BuildSellList();

                break;
            case Tab.Listings:
                y = BuildListingList(MyListings, "You have no active listings.");

                break;
            case Tab.Bids:
                y = BuildListingList(MyBids, "You have no active bids.");

                break;
            case Tab.Storage:
                y = BuildStorageList();

                break;
        }

        Sidebar.SetContentHeight(y);
    }

    private int BuildTree()
    {
        var term = SearchBox.Text?.Trim() ?? string.Empty;
        var y = 0;

        if (Catalog.Count == 0)
        {
            Sidebar.Add(Hint("The Exchange is empty right now.", y));

            return ROW_H;
        }

        foreach (var cat in Catalog)
        {
            var matchingItems = cat.Items
                                   .Where(i => term.Length == 0 || i.Name.Contains(term, StringComparison.OrdinalIgnoreCase))
                                   .ToList();

            if (term.Length > 0 && matchingItems.Count == 0)
                continue;

            var expanded = Expanded.Contains(cat.Name) || term.Length > 0;
            var header = new TreeRow(Sidebar.InnerWidth, $"{(expanded ? "-" : "+")}  {TitleCase(cat.Name)}  ({cat.Items.Count})", null, 0, false, TextNormal)
            {
                Y = y
            };
            var capturedCat = cat.Name;
            header.Clicked = () =>
            {
                if (!Expanded.Remove(capturedCat))
                    Expanded.Add(capturedCat);

                RebuildSidebar();
            };
            Sidebar.Add(header);
            y += ROW_H;

            if (!expanded)
                continue;

            foreach (var item in matchingItems)
            {
                var selected = string.Equals(item.TemplateKey, SelectedTemplateKey, StringComparison.OrdinalIgnoreCase);
                var icon = UiRenderer.Instance!.GetItemIcon(item.Sprite, (DisplayColor)item.Color);
                var price = item.MinPrice > 0 ? $"{item.MinPrice:N0}" : string.Empty;
                var row = new TreeRow(Sidebar.InnerWidth, item.Name, icon, 1, selected, selected ? TextSel : TextNormal, price)
                {
                    Y = y
                };
                var key = item.TemplateKey;
                var nm = item.Name;
                row.Clicked = () =>
                {
                    SelectedTemplateKey = key;
                    SelectedItemName = nm;
                    Sellers = [];
                    RebuildSidebar();
                    RebuildDetail();
                    Raise(MarketClientAction.RequestSellers, key);
                };
                Sidebar.Add(row);
                y += ROW_H;
            }
        }

        return y;
    }

    private int BuildSellList()
    {
        //both roots always show (even when empty). Inventory items always render; they are only LISTABLE in person at
        //Colm (the detail blocks listing remotely), while bank items can be listed from anywhere.
        var y = BuildSellGroup("Inventory", "__sell_inv__", SellItems.Where(i => i.FromBank == 0).ToList(), y: 0);
        y = BuildSellGroup("Bank", "__sell_bank__", SellItems.Where(i => i.FromBank == 1).ToList(), y);

        return y;
    }

    private int BuildSellGroup(string title, string key, List<MarketSellEntry> items, int y)
    {
        var expanded = !Collapsed.Contains(key); //expanded by default; the player can fold a group
        var header = new TreeRow(Sidebar.InnerWidth, $"{(expanded ? "-" : "+")}  {title}  ({items.Count})", null, 0, false, NameCol)
        {
            Y = y
        };
        header.Clicked = () =>
        {
            if (!Collapsed.Remove(key))
                Collapsed.Add(key);

            RebuildSidebar();
        };
        Sidebar.Add(header);
        y += ROW_H;

        if (!expanded)
            return y;

        if (items.Count == 0)
        {
            Sidebar.Add(new UILabel
            {
                X = PAD + 16,
                Y = y,
                Width = Sidebar.InnerWidth - PAD - 16,
                Height = ROW_H,
                CustomFontSize = FONT,
                ForegroundColor = TextHint,
                VerticalAlignment = VerticalAlignment.Center,
                Text = "(empty)",
                IsHitTestVisible = false
            });

            return y + ROW_H;
        }

        foreach (var item in items)
        {
            var selected = SellTarget is not null && SellTarget.Slot == item.Slot && SellTarget.FromBank == item.FromBank && SellTarget.Name == item.Name;
            var icon = UiRenderer.Instance!.GetItemIcon(item.Sprite, (DisplayColor)item.Color);
            var label = item.Count > 1 ? $"{item.Name} ({item.Count})" : item.Name;
            var row = new TreeRow(Sidebar.InnerWidth, label, icon, 1, selected, selected ? TextSel : TextNormal)
            {
                Y = y
            };
            var captured = item;
            row.Clicked = () =>
            {
                SellTarget = captured;
                SellAuction = false;
                RebuildSidebar();
                RebuildDetail();
            };
            Sidebar.Add(row);
            y += ROW_H;
        }

        return y;
    }

    private int BuildListingList(List<MarketListingEntry> listings, string emptyText)
    {
        var y = 0;

        if (listings.Count == 0)
        {
            Sidebar.Add(Hint(emptyText, y));

            return ROW_H;
        }

        foreach (var listing in listings)
        {
            var selected = string.Equals(listing.ListingId, SelectedListing?.ListingId, StringComparison.Ordinal);
            var icon = UiRenderer.Instance!.GetItemIcon(listing.Sprite, (DisplayColor)listing.Color);
            var row = new TreeRow(Sidebar.InnerWidth, listing.ItemName, icon, 0, selected, selected ? TextSel : TextNormal, $"{listing.Price:N0}")
            {
                Y = y
            };
            var captured = listing;
            row.Clicked = () =>
            {
                SelectedListing = captured;
                RebuildSidebar();
                RebuildDetail();
            };
            Sidebar.Add(row);
            y += ROW_H;
        }

        return y;
    }

    private int BuildStorageList()
    {
        var y = 0;

        if (StorageItems.Count == 0)
        {
            Sidebar.Add(Hint("Your Exchange storage is empty.", y));

            return ROW_H;
        }

        foreach (var item in StorageItems)
        {
            var selected = SelectedStorage is not null && SelectedStorage.UniqueId == item.UniqueId;
            var icon = UiRenderer.Instance!.GetItemIcon(item.Sprite, (DisplayColor)item.Color);
            var label = item.Count > 1 ? $"{item.Name} ({item.Count})" : item.Name;
            var row = new TreeRow(Sidebar.InnerWidth, label, icon, 0, selected, selected ? TextSel : TextNormal)
            {
                Y = y
            };
            var captured = item;
            row.Clicked = () =>
            {
                SelectedStorage = captured;
                RebuildSidebar();
                RebuildDetail();
            };
            Sidebar.Add(row);
            y += ROW_H;
        }

        return y;
    }
    #endregion

    #region detail build
    private void RebuildDetail()
    {
        Detail.Clear();
        SellQtyBox = null; //the detail (and its sell-form / buy-cheapest controls) are about to be rebuilt
        SellPriceBox = null;
        SellTotalLabel = null;
        CheapestQtyBox = null;
        CheapestTotalLabel = null;

        switch (Current)
        {
            case Tab.Browse:
                BuildBrowseDetail();

                break;
            case Tab.Sell:
                BuildSellDetail();

                break;
            case Tab.Listings:
            case Tab.Bids:
                BuildListingDetail();

                break;
            case Tab.Storage:
                BuildStorageDetail();

                break;
        }
    }

    private void BuildStorageDetail()
    {
        var dw = Detail.InnerWidth - 2 * PAD;
        var y = PAD + 18;

        if (SelectedStorage is null)
        {
            Detail.Add(DetailHint(StorageItems.Count == 0
                ? "Items you have won or bought wait here."
                : "Items you have won or bought wait here. Pick one on the left."));
            Detail.SetContentHeight(60);

            return;
        }

        var entry = SelectedStorage;
        var icon = UiRenderer.Instance!.GetItemIcon(entry.Sprite, (DisplayColor)entry.Color);
        Detail.Add(new FitImage { X = PAD, Y = y, Width = DETAIL_ICON, Height = DETAIL_ICON, FixedScale = 2, Texture = icon, Visible = icon is not null });

        var tx = PAD + DETAIL_ICON + 8;
        var meta = DataContext.MetaFiles.GetItemMetadata(entry.Name);
        meta.TryGetValue(entry.Name, out var md);
        var name = entry.Count > 1 ? $"{entry.Name} ({entry.Count})" : entry.Name;
        y = AddItemInfo(name, md, tx, y, dw);

        Detail.Add(new UIPanel { X = PAD, Y = y, Width = dw, Height = 1, BackgroundColor = Divider, IsHitTestVisible = false });
        y += 8;

        if (!AtNpc)
        {
            Detail.Add(new UILabel
            {
                X = PAD,
                Y = y,
                Width = dw,
                Height = 36,
                CustomFontSize = FONT,
                ForegroundColor = TextHint,
                WordWrap = true,
                Text = "Visit Colm at the Temuair Exchange in Mileth to collect this.",
                IsHitTestVisible = false
            });
            Detail.SetContentHeight(y + 40);

            return;
        }

        var id = entry.UniqueId;
        var toInv = new MenuButton("Send to Inventory", (dw - 8) / 2, BTN_H) { X = PAD, Y = y };
        toInv.Clicked = _ => Raise(MarketClientAction.CollectStorage, id);
        Detail.Add(toInv);

        var toBank = new MenuButton("Send to Bank", (dw - 8) / 2, BTN_H) { X = PAD + (dw - 8) / 2 + 8, Y = y };
        toBank.Clicked = _ => Raise(MarketClientAction.CollectStorageToBank, id);
        Detail.Add(toBank);
        y += BTN_H + PAD;

        Detail.SetContentHeight(y);
    }

    private void BuildBrowseDetail()
    {
        if (SelectedTemplateKey is null)
        {
            Detail.Add(DetailHint("Select an item to see its sellers."));
            Detail.SetContentHeight(40);

            return;
        }

        var dw = Detail.InnerWidth - 2 * PAD;
        var y = PAD + 18; //leave room under the toast

        var icon = Sellers.Count > 0
            ? UiRenderer.Instance!.GetItemIcon(Sellers[0].Sprite, (DisplayColor)Sellers[0].Color)
            : null;

        Detail.Add(new FitImage
        {
            X = PAD,
            Y = y,
            Width = DETAIL_ICON,
            Height = DETAIL_ICON,
            FixedScale = 2,
            Texture = icon,
            Visible = icon is not null
        });

        var tx = PAD + DETAIL_ICON + 8;
        var meta = DataContext.MetaFiles.GetItemMetadata(SelectedItemName);
        meta.TryGetValue(SelectedItemName, out var md);

        //item info block laid out exactly like the in-game item tooltip: name, "Lv N Type", description, weight
        y = AddItemInfo(SelectedItemName, md, tx, y, dw);

        //separator
        Detail.Add(new UIPanel { X = PAD, Y = y, Width = dw, Height = 1, BackgroundColor = Divider, IsHitTestVisible = false });
        y += 8;

        if (Sellers.Count == 0)
        {
            Detail.Add(new UILabel
            {
                X = PAD,
                Y = y,
                Width = dw,
                Height = 20,
                CustomFontSize = FONT,
                ForegroundColor = TextHint,
                Text = "No sellers right now.",
                IsHitTestVisible = false
            });
            Detail.SetContentHeight(y + 24);

            return;
        }

        //quick "buy cheapest fixed" action, with a quantity selector + live total when it is a stack
        var cheapest = Sellers.Where(s => s.IsAuction == 0).OrderBy(s => s.Price).FirstOrDefault();

        if (cheapest is not null)
        {
            var id = cheapest.ListingId;
            var stock = (int)cheapest.Count;

            if (stock > 1)
            {
                Detail.Add(new UILabel
                {
                    X = PAD,
                    Y = y,
                    Width = 32,
                    Height = 24,
                    CustomFontSize = FONT,
                    ForegroundColor = TextDim,
                    VerticalAlignment = VerticalAlignment.Center,
                    Text = "Qty",
                    IsHitTestVisible = false
                });

                CheapestQtyBox = new UITextBox
                {
                    X = PAD + 36,
                    Y = y,
                    Width = 56,
                    Height = 24,
                    MaxLength = 6,
                    CustomFontSize = FONT,
                    Prefix = string.Empty,
                    Text = "1",
                    BackgroundColor = new Color(30, 26, 19),
                    BorderColor = new Color(86, 72, 48),
                    FocusedBackgroundColor = new Color(38, 33, 23)
                };
                Detail.Add(CheapestQtyBox);

                CheapestTotalLabel = new UILabel
                {
                    X = PAD + 100,
                    Y = y,
                    Width = dw - 100,
                    Height = 24,
                    CustomFontSize = FONT,
                    ForegroundColor = TextGold,
                    VerticalAlignment = VerticalAlignment.Center,
                    Text = $"= {cheapest.Price:N0} Gold",
                    IsHitTestVisible = false
                };
                Detail.Add(CheapestTotalLabel);
                CheapestUnitPrice = (int)cheapest.Price;
                CheapestStock = stock;
                CheapestTotalKey = string.Empty;
                y += 28;

                var buyQ = new MenuButton($"Buy cheapest from {cheapest.SellerName}", dw, BTN_H) { X = PAD, Y = y };
                buyQ.Clicked = _ =>
                {
                    var q = Math.Clamp(int.TryParse(CheapestQtyBox.Text?.Trim(), out var v) ? v : 1, 1, stock);
                    Raise(MarketClientAction.Buy, id, q);
                };
                Detail.Add(buyQ);
                y += BTN_H + 8;
            } else
            {
                var buy = new MenuButton($"Buy cheapest - {cheapest.Price:N0} Gold", dw, BTN_H) { X = PAD, Y = y };
                buy.Clicked = _ => Raise(MarketClientAction.Buy, id, 1);
                Detail.Add(buy);
                y += BTN_H + 8;
            }
        }

        //every seller except the one already offered as "Buy cheapest" above
        var others = Sellers.Where(s => !ReferenceEquals(s, cheapest))
                            .OrderBy(s => s.IsAuction)
                            .ThenBy(s => s.Price)
                            .ToList();

        if (others.Count == 0)
        {
            Detail.SetContentHeight(y + PAD);

            return;
        }

        Detail.Add(new UILabel
        {
            X = PAD,
            Y = y,
            Width = dw,
            Height = 20,
            CustomFontSize = FONT,
            ForegroundColor = TextDim,
            Text = "Other sellers:",
            IsHitTestVisible = false
        });
        y += 20;

        foreach (var seller in others)
        {
            var row = new SellerRow(dw, seller);
            row.Y = y;
            row.OnBuy = qty => Raise(MarketClientAction.Buy, seller.ListingId, qty);
            row.OnBuyNow = () => Raise(MarketClientAction.BuyNow, seller.ListingId);
            row.OnBid = amount => Raise(MarketClientAction.Bid, seller.ListingId, maxBid: amount);
            Detail.Add(row);
            y += row.Height + 4;
        }

        Detail.SetContentHeight(y + PAD);
    }

    private void BuildSellDetail()
    {
        var dw = Detail.InnerWidth - 2 * PAD;
        var y = PAD + 18;

        if (SellTarget is null)
        {
            Detail.Add(DetailHint(AtNpc
                ? "Pick an item on the left to list it for sale."
                : "Bank items can be listed from anywhere. To sell from your inventory, visit Colm in person."));
            Detail.SetContentHeight(60);

            return;
        }

        var item = SellTarget;

        var icon = UiRenderer.Instance!.GetItemIcon(item.Sprite, (DisplayColor)item.Color);
        Detail.Add(new FitImage { X = PAD, Y = y, Width = DETAIL_ICON, Height = DETAIL_ICON, FixedScale = 2, Texture = icon, Visible = icon is not null });
        var sx = PAD + DETAIL_ICON + 8;
        Detail.Add(new UILabel
        {
            X = sx,
            Y = y + 4,
            Width = dw - (sx - PAD),
            Height = 24,
            CustomFontSize = NAME_FONT,
            ForegroundColor = NameCol,
            Text = item.Count > 1 ? $"{item.Name} ({item.Count})" : item.Name,
            IsHitTestVisible = false
        });
        Detail.Add(new UILabel
        {
            X = sx,
            Y = y + 32,
            Width = dw - (sx - PAD),
            Height = 20,
            CustomFontSize = FONT,
            ForegroundColor = TextDim,
            Text = $"{(item.FromBank == 1 ? "From bank - " : "")}Listing fee: {item.Fee:N0} Gold",
            IsHitTestVisible = false
        });
        y += DETAIL_ICON + 4;

        //inventory items can only be listed in person at a clerk; show the item but offer no list controls remotely
        if (item.FromBank == 0 && !AtNpc)
        {
            Detail.Add(new UIPanel { X = PAD, Y = y, Width = dw, Height = 1, BackgroundColor = Divider, IsHitTestVisible = false });
            y += 8;
            Detail.Add(new UILabel
            {
                X = PAD,
                Y = y,
                Width = dw,
                Height = 44,
                CustomFontSize = FONT,
                ForegroundColor = TextHint,
                WordWrap = true,
                Text = "Inventory items can only be listed in person. Visit Colm at the Temuair Exchange in Mileth - or list items straight from your bank from anywhere.",
                IsHitTestVisible = false
            });
            Detail.SetContentHeight(y + 48);

            return;
        }

        //fixed / auction toggle
        var fixedBtn = new MenuButton("Fixed price", (dw - 6) / 2, BTN_H) { X = PAD, Y = y };
        var auctionBtn = new MenuButton("Auction", (dw - 6) / 2, BTN_H) { X = PAD + (dw - 6) / 2 + 6, Y = y };
        fixedBtn.TextColor = SellAuction ? TextNormal : TextSel;
        auctionBtn.TextColor = SellAuction ? TextSel : TextNormal;
        fixedBtn.Clicked = _ =>
        {
            SellAuction = false;
            RebuildDetail();
        };
        auctionBtn.Clicked = _ =>
        {
            SellAuction = true;
            RebuildDetail();
        };
        Detail.Add(fixedBtn);
        Detail.Add(auctionBtn);
        y += BTN_H + 8;

        var qtyBox = AddField("Quantity", item.Stackable == 1 ? item.Count.ToString() : "1", ref y, dw, item.Stackable == 1);
        var priceBox = AddField(SellAuction ? "Start price (Gold)" : "Price each (Gold)", "", ref y, dw, true);
        UITextBox? buyNowBox = null;
        UITextBox? hoursBox = null;

        if (SellAuction)
        {
            buyNowBox = AddField("Buy-now (0 = none)", "0", ref y, dw, true);
            hoursBox = AddField("Duration (hours)", "24", ref y, dw, true);
        } else
        {
            //live total for a fixed-price sale (qty x price each), updated in Update
            SellTotalLabel = new UILabel
            {
                X = PAD + FIELD_LABEL_W + 4,
                Y = y,
                Width = dw - FIELD_LABEL_W - 4,
                Height = 20,
                CustomFontSize = FONT,
                ForegroundColor = TextGold,
                Text = "= 0 Gold",
                IsHitTestVisible = false
            };
            Detail.Add(SellTotalLabel);
            SellQtyBox = qtyBox;
            SellPriceBox = priceBox;
            SellTotalKey = string.Empty;
            y += 26;
        }

        var listBtn = new MenuButton(SellAuction ? "Start auction" : "List for sale", dw, BTN_H) { X = PAD, Y = y };
        listBtn.Clicked = _ =>
        {
            var qty = item.Stackable == 1 ? ParseInt(qtyBox.Text, 1) : 1;
            var price = ParseInt(priceBox.Text, 0);

            if (price <= 0)
            {
                ShowToast("Enter a price greater than zero.");

                return;
            }

            Raise(
                MarketClientAction.ListItem,
                arg: item.FromBank == 1 ? item.Name : string.Empty, //a bank item is identified by name, not a slot
                slot: item.Slot,
                listingType: (byte)(SellAuction ? 1 : 0),
                qty: qty,
                price: price,
                buyNow: SellAuction ? ParseInt(buyNowBox!.Text, 0) : 0,
                hours: SellAuction ? ParseInt(hoursBox!.Text, 24) : 0,
                fromBank: item.FromBank);
        };
        Detail.Add(listBtn);
        y += BTN_H + PAD;

        Detail.SetContentHeight(y);
    }

    private void BuildListingDetail()
    {
        var dw = Detail.InnerWidth - 2 * PAD;
        var y = PAD + 18;
        var listing = SelectedListing;

        if (listing is null)
        {
            Detail.Add(DetailHint(Current == Tab.Listings ? "Pick a listing to manage it." : "Pick a bid to review it."));
            Detail.SetContentHeight(40);

            return;
        }

        var icon = UiRenderer.Instance!.GetItemIcon(listing.Sprite, (DisplayColor)listing.Color);
        Detail.Add(new FitImage { X = PAD, Y = y, Width = DETAIL_ICON, Height = DETAIL_ICON, FixedScale = 2, Texture = icon, Visible = icon is not null });
        var lx = PAD + DETAIL_ICON + 8;
        Detail.Add(new UILabel
        {
            X = lx,
            Y = y + 4,
            Width = dw - (lx - PAD),
            Height = 24,
            CustomFontSize = NAME_FONT,
            ForegroundColor = NameCol,
            Text = listing.ItemName,
            IsHitTestVisible = false
        });
        Detail.Add(new UILabel
        {
            X = lx,
            Y = y + 32,
            Width = dw - (lx - PAD),
            Height = 20,
            CustomFontSize = FONT,
            ForegroundColor = listing.IsAuction == 1 ? AuctionBlue : TextGold,
            Text = listing.IsAuction == 1
                ? $"Auction - {listing.Price:N0} Gold - {TimeLeft(listing.SecondsLeft)}"
                : $"{listing.Price:N0} Gold",
            IsHitTestVisible = false
        });
        y += DETAIL_ICON + 6;

        if (listing.IsAuction == 1 && listing.HasBids == 1)
        {
            Detail.Add(new UILabel
            {
                X = PAD,
                Y = y,
                Width = dw,
                Height = 20,
                CustomFontSize = FONT,
                ForegroundColor = TextDim,
                Text = $"High bidder: {listing.HighBidder}",
                IsHitTestVisible = false
            });
            y += 20;
        }

        if (Current == Tab.Listings)
        {
            //cancel is only allowed for non-auction or auctions with no bids (server enforces too)
            if (listing.IsAuction == 0 || listing.HasBids == 0)
            {
                var cancel = new MenuButton("Cancel listing", dw, BTN_H) { X = PAD, Y = y };
                cancel.Clicked = _ => Raise(MarketClientAction.CancelListing, listing.ListingId);
                Detail.Add(cancel);
                y += BTN_H;
            } else
            {
                Detail.Add(new UILabel
                {
                    X = PAD,
                    Y = y,
                    Width = dw,
                    Height = 20,
                    CustomFontSize = FONT,
                    ForegroundColor = TextHint,
                    Text = "An auction with bids cannot be cancelled.",
                    IsHitTestVisible = false
                });
                y += 22;
            }
        } else
        {
            Detail.Add(new UILabel
            {
                X = PAD,
                Y = y,
                Width = dw,
                Height = 20,
                CustomFontSize = FONT,
                ForegroundColor = listing.Winning == 1 ? GoodGreen : new Color(214, 150, 120),
                Text = listing.Winning == 1 ? "You are the high bidder." : "You have been outbid.",
                IsHitTestVisible = false
            });
            y += 22;
        }

        Detail.SetContentHeight(y + PAD);
    }
    #endregion

    #region helpers
    private UITextBox AddField(string label, string initial, ref int y, int dw, bool enabled)
    {
        Detail.Add(new UILabel
        {
            X = PAD,
            Y = y,
            Width = FIELD_LABEL_W,
            Height = 22,
            CustomFontSize = FONT,
            ForegroundColor = TextDim,
            VerticalAlignment = VerticalAlignment.Center,
            Text = label,
            IsHitTestVisible = false
        });

        var box = new UITextBox
        {
            X = PAD + FIELD_LABEL_W + 4,
            Y = y,
            Width = dw - FIELD_LABEL_W - 4,
            Height = 22,
            MaxLength = 12,
            CustomFontSize = FONT,
            FocusedBackgroundColor = new Color(22, 19, 14),
            BackgroundColor = new Color(20, 18, 13),
            BorderColor = new Color(74, 62, 42),
            Text = initial,
            IsHitTestVisible = enabled
        };
        Detail.Add(box);
        y += 28;

        return box;
    }

    private UILabel Hint(string text, int y)
        => new()
        {
            X = PAD,
            Y = y,
            Width = Sidebar.InnerWidth - 2 * PAD,
            Height = ROW_H * 2, //room to wrap so a longer empty-state line is never cropped
            CustomFontSize = FONT,
            ForegroundColor = TextHint,
            WordWrap = true,
            Text = text,
            IsHitTestVisible = false
        };

    private UILabel DetailHint(string text)
        => new()
        {
            X = PAD,
            Y = PAD + 18,
            Width = Detail.InnerWidth - 2 * PAD,
            Height = 40,
            CustomFontSize = FONT,
            ForegroundColor = TextHint,
            WordWrap = true,
            Text = text,
            IsHitTestVisible = false
        };

    //lays out an item's info exactly like the in-game tooltip (name, "Lv N Type", description, weight) and returns the
    //y below it. The icon is drawn by the caller to the left at PAD; name/category sit to its right at tx.
    private int AddItemInfo(string itemName, ItemMetadataEntry? md, int tx, int y, int dw)
    {
        var infoW = dw - (tx - PAD);
        Detail.Add(new UILabel
        {
            X = tx,
            Y = y + 4,
            Width = infoW,
            Height = 26,
            CustomFontSize = NAME_FONT,
            ForegroundColor = NameCol,
            Text = itemName,
            IsHitTestVisible = false
        });

        var category = CategoryLine(md);

        if (category.Length > 0)
            Detail.Add(new UILabel
            {
                X = tx,
                Y = y + 32,
                Width = infoW,
                Height = 20,
                CustomFontSize = FONT,
                ForegroundColor = CatCol,
                Text = category,
                IsHitTestVisible = false
            });

        y += DETAIL_ICON + 4;

        if (md is not null && !string.IsNullOrWhiteSpace(md.Description))
        {
            var desc = new UILabel
            {
                X = PAD,
                Y = y,
                Width = dw,
                Height = 24,
                CustomFontSize = FONT,
                ForegroundColor = DescCol,
                WordWrap = true,
                RichTextMarkup = true, //honor the server description's <green>/<red>/<white> markup
                Text = md.Description.Trim(),
                IsHitTestVisible = false
            };
            Detail.Add(desc);
            desc.Height = Math.Max(20, desc.ContentHeight); //size to the wrapped content so nothing is cropped
            y += desc.Height + 6;
        }

        if (md is { Weight: > 0 })
        {
            Detail.Add(new UILabel
            {
                X = PAD,
                Y = y,
                Width = dw,
                Height = 20,
                CustomFontSize = FONT,
                ForegroundColor = CatCol,
                Text = $"Weight: {md.Weight}",
                IsHitTestVisible = false
            });
            y += 22;
        }

        return y;
    }

    //"Lv N Type" with the category word singularized (Potions -> Potion), matching the tooltip
    private static string CategoryLine(ItemMetadataEntry? md)
    {
        if (md is null)
            return string.Empty;

        var hasCategory = !string.IsNullOrWhiteSpace(md.Category);
        var word = hasCategory ? Singularize(md.Category.Trim()) : string.Empty;

        return md.Level > 0
            ? hasCategory ? $"Lv {md.Level} {word}" : $"Lv {md.Level}"
            : word;
    }

    private static string Singularize(string category)
    {
        var space = category.LastIndexOf(' ');
        var word = space >= 0 ? category[(space + 1)..] : category;

        if (word.Length > 2 && word.EndsWith('s') && !word.EndsWith("ss", StringComparison.OrdinalIgnoreCase))
            word = word[..^1];

        return space >= 0 ? category[..(space + 1)] + word : word;
    }

    //clear the Browse search filter (the "x" button, and whenever the window closes)
    public void ClearSearch()
    {
        if (string.IsNullOrEmpty(SearchBox.Text))
            return;

        SearchBox.Text = string.Empty;
        LastSearch = string.Empty;

        if (Current == Tab.Browse)
            RebuildSidebar();
    }

    private static int ParseInt(string? s, int fallback) => int.TryParse(s?.Trim(), out var v) ? v : fallback;

    //server categories arrive lowercase ("food", "ring"); title-case each word for display ("Food", "Ring")
    private static string TitleCase(string s)
    {
        if (string.IsNullOrEmpty(s))
            return s;

        var words = s.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        for (var i = 0; i < words.Length; i++)
            words[i] = char.ToUpperInvariant(words[i][0]) + words[i][1..];

        return string.Join(' ', words);
    }

    private static string TimeLeft(uint seconds)
    {
        if (seconds == 0)
            return "ending";

        if (seconds >= 3600)
            return $"{seconds / 3600}h {seconds % 3600 / 60}m";

        if (seconds >= 60)
            return $"{seconds / 60}m";

        return $"{seconds}s";
    }
    #endregion

    public override void Update(GameTime gameTime)
    {
        base.Update(gameTime);

        if (!Visible)
            return;

        if (ToastTimer > 0f)
        {
            ToastTimer -= (float)gameTime.ElapsedGameTime.TotalSeconds;

            if (ToastTimer <= 0f)
                ToastLabel.Visible = false;
        }

        //placeholder shows only while browsing, the box is empty, and it is not focused; the clear "x" only when there is text
        SearchPlaceholder.Visible = Current == Tab.Browse && string.IsNullOrEmpty(SearchBox.Text) && !SearchBox.IsFocused;
        SearchClear.Visible = Current == Tab.Browse && !string.IsNullOrEmpty(SearchBox.Text);

        //keep the Buy-cheapest total (qty x unit price) live
        if (CheapestTotalLabel is not null && CheapestQtyBox is not null)
        {
            var text = CheapestQtyBox.Text ?? string.Empty;

            if (text != CheapestTotalKey)
            {
                CheapestTotalKey = text;
                var qty = Math.Clamp(ParseInt(text, 1), 1, Math.Max(1, CheapestStock));
                CheapestTotalLabel.Text = $"= {(long)qty * CheapestUnitPrice:N0} Gold";
            }
        }

        //keep the fixed-price sell total (qty x price each) live
        if (SellTotalLabel is not null && SellQtyBox is not null && SellPriceBox is not null)
        {
            var key = $"{SellQtyBox.Text}|{SellPriceBox.Text}";

            if (key != SellTotalKey)
            {
                SellTotalKey = key;
                var qty = Math.Max(1, ParseInt(SellQtyBox.Text, 1));
                var price = Math.Max(0, ParseInt(SellPriceBox.Text, 0));
                SellTotalLabel.Text = $"= {(long)qty * price:N0} Gold";
            }
        }

        var cur = SearchBox.Text ?? string.Empty;

        if (cur != LastSearch)
        {
            LastSearch = cur;

            if (Current == Tab.Browse)
                RebuildSidebar();
        }
    }

    protected override void OnCloseClicked()
    {
        ClearSearch(); //closing the window clears the Browse filter
        Visible = false;
        OnClosed?.Invoke();
    }

    public override void OnKeyDown(KeyDownEvent e)
    {
        if (e.Key == Keys.Escape)
        {
            ClearSearch();
            Visible = false;
            OnClosed?.Invoke();
            e.Handled = true;
        }
    }

    /// <summary>
    ///     An item icon centered in its box and clipped to it. With <see cref="FixedScale" /> = 0 (default) it scales DOWN
    ///     to fit; with a positive value it draws at native x FixedScale (1 = crisp 1:1, 2 = double), clipped to the box.
    /// </summary>
    private sealed class FitImage : UIImage
    {
        public int FixedScale { get; init; }

        public override void Draw(SpriteBatchEx spriteBatch)
        {
            if (!Visible)
                return;

            UpdateClipRect();

            if (Texture is null || (ClipRect.Width <= 0) || (ClipRect.Height <= 0))
                return;

            var tw = Texture.Width;
            var th = Texture.Height;

            if ((tw <= 0) || (th <= 0))
                return;

            var scale = FixedScale > 0 ? FixedScale : Math.Min(1f, Math.Min((float)Width / tw, (float)Height / th));
            var dw = Math.Max(1, (int)(tw * scale));
            var dh = Math.Max(1, (int)(th * scale));
            var dest = new Rectangle(ScreenX + (Width - dw) / 2, ScreenY + (Height - dh) / 2, dw, dh);

            //CPU-clip the scaled draw to the clip rect (so a row scrolled half off the viewport does not bleed)
            var clipped = Rectangle.Intersect(dest, ClipRect);

            if ((clipped.Width <= 0) || (clipped.Height <= 0))
                return;

            var src = new Rectangle(
                (int)((clipped.X - dest.X) / (float)dest.Width * tw),
                (int)((clipped.Y - dest.Y) / (float)dest.Height * th),
                (int)Math.Ceiling(clipped.Width / (float)dest.Width * tw),
                (int)Math.Ceiling(clipped.Height / (float)dest.Height * th));

            src.Width = Math.Min(src.Width, tw - src.X);
            src.Height = Math.Min(src.Height, th - src.Y);

            if ((src.Width <= 0) || (src.Height <= 0))
                return;

            spriteBatch.Draw(Texture, clipped, src, Color.White);
        }
    }

    /// <summary>A row in the sidebar tree/list: optional icon, a label, an optional right-aligned value, indent + select state.</summary>
    private sealed class TreeRow : UIPanel
    {
        private readonly bool Selectable;

        public Action? Clicked { get; set; }

        public TreeRow(int width, string label, Texture2D? icon, int indent, bool selected, Color textColor, string? rightText = null)
        {
            Width = width;
            Height = ROW_H;
            Selectable = true;
            BackgroundColor = selected ? RowSelected : null;

            var x = PAD + indent * 16;

            if (icon is not null)
            {
                AddChild(new FitImage
                {
                    X = x,
                    Y = (ROW_H - ROW_ICON) / 2,
                    Width = ROW_ICON,
                    Height = ROW_ICON,
                    FixedScale = 1, //tree icons render crisp at native 1:1
                    Texture = icon,
                    IsHitTestVisible = false
                });
                x += ROW_ICON + 4;
            }

            var rightW = string.IsNullOrEmpty(rightText) ? 0 : 80;
            var nameW = width - x - rightW - 6;
            AddChild(new UILabel
            {
                X = x,
                Y = 0,
                Width = nameW,
                Height = ROW_H,
                CustomFontSize = FitFont(label, nameW, FONT), //shrink a long name so it never clips into the price
                ForegroundColor = textColor,
                VerticalAlignment = VerticalAlignment.Center,
                Text = label,
                IsHitTestVisible = false
            });

            if (rightW > 0)
                AddChild(new UILabel
                {
                    X = width - rightW - 6,
                    Y = 0,
                    Width = rightW,
                    Height = ROW_H,
                    CustomFontSize = FitFont(rightText!, rightW, FONT), //shrink a long price the same way
                    ForegroundColor = TextGold,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Center,
                    Text = rightText,
                    IsHitTestVisible = false
                });
        }

        //largest font (down to MIN_FIT_FONT) at which the text fits the given width, so names/prices never clip or overlap
        private static int FitFont(string text, int maxWidth, int baseSize)
        {
            if (string.IsNullOrEmpty(text) || !TtfTextRenderer.Available || maxWidth <= 0)
                return baseSize;

            for (var size = baseSize; size > MIN_FIT_FONT; size--)
                if (TtfTextRenderer.MeasureWidth(text, size) <= maxWidth)
                    return size;

            return MIN_FIT_FONT;
        }

        public override void OnClick(ClickEvent e)
        {
            if (e.Button != MouseButton.Left)
                return;

            Clicked?.Invoke();
            e.Handled = true;
        }

        public override void OnMouseEnter()
        {
            if (Selectable && BackgroundColor != RowSelected)
                BackgroundColor = RowHover;
        }

        public override void OnMouseLeave()
        {
            if (BackgroundColor != RowSelected)
                BackgroundColor = null;
        }
    }

    /// <summary>One seller row in the detail panel: name + price/qty/time on the left, action(s) on the right. A stackable
    ///     fixed listing gets a quantity box; an auction gets a bid box (+ optional buy-now).</summary>
    private sealed class SellerRow : UIPanel
    {
        private readonly MarketListingEntry Listing;
        private readonly UITextBox? BidBox;
        private readonly UITextBox? QtyBox;
        private readonly UILabel? TotalLabel;
        private string LastQty = string.Empty;

        public Action<int>? OnBuy { get; set; } //quantity
        public Action? OnBuyNow { get; set; }
        public Action<int>? OnBid { get; set; }

        public SellerRow(int width, MarketListingEntry listing)
        {
            Listing = listing;
            Width = width;
            var stackable = listing.IsAuction == 0 && listing.Count > 1;
            Height = listing.IsAuction == 1 || stackable ? 56 : 30;
            BackgroundColor = new Color(20, 18, 13);

            var info = listing.IsAuction == 1
                ? $"{listing.SellerName} - bid {listing.Price:N0} Gold - {TimeLeft(listing.SecondsLeft)}"
                : stackable
                    ? $"{listing.SellerName} - {listing.Price:N0} Gold each - {listing.Count} available"
                    : $"{listing.SellerName} - {listing.Price:N0} Gold";

            AddChild(new UILabel
            {
                X = 6,
                Y = 0,
                Width = listing.IsAuction == 1 || stackable ? width - 12 : width - 90,
                Height = 26,
                CustomFontSize = FONT,
                ForegroundColor = listing.IsAuction == 1 ? AuctionBlue : TextNormal,
                VerticalAlignment = VerticalAlignment.Center,
                Text = info,
                IsHitTestVisible = false
            });

            if (listing.IsAuction == 0)
            {
                if (!stackable)
                {
                    var buy = new MenuButton("Buy", 80, 24) { X = width - 84, Y = 3, CustomFontSize = FONT };
                    buy.Clicked = _ => OnBuy?.Invoke(1);
                    AddChild(buy);
                } else
                {
                    //second row: a quantity selector, a live total, and the Buy button
                    AddChild(new UILabel
                    {
                        X = 6,
                        Y = 28,
                        Width = 32,
                        Height = 24,
                        CustomFontSize = FONT,
                        ForegroundColor = TextDim,
                        VerticalAlignment = VerticalAlignment.Center,
                        Text = "Qty",
                        IsHitTestVisible = false
                    });

                    QtyBox = new UITextBox
                    {
                        X = 40,
                        Y = 28,
                        Width = 56,
                        Height = 24,
                        MaxLength = 6,
                        CustomFontSize = FONT,
                        Prefix = string.Empty,
                        Text = "1",
                        BackgroundColor = new Color(30, 26, 19),
                        BorderColor = new Color(86, 72, 48),
                        FocusedBackgroundColor = new Color(38, 33, 23)
                    };
                    AddChild(QtyBox);

                    TotalLabel = new UILabel
                    {
                        X = 104,
                        Y = 28,
                        Width = width - 104 - 88,
                        Height = 24,
                        CustomFontSize = FONT,
                        ForegroundColor = TextGold,
                        VerticalAlignment = VerticalAlignment.Center,
                        Text = $"= {listing.Price:N0} Gold",
                        IsHitTestVisible = false
                    };
                    AddChild(TotalLabel);

                    var stock = (int)listing.Count;
                    var buy = new MenuButton("Buy", 80, 24) { X = width - 84, Y = 28, CustomFontSize = FONT };
                    buy.Clicked = _ =>
                    {
                        var qty = int.TryParse(QtyBox.Text?.Trim(), out var q) ? q : 1;
                        OnBuy?.Invoke(Math.Clamp(qty, 1, stock));
                    };
                    AddChild(buy);
                }
            } else
            {
                //bid input (prefilled with the server's actual minimum acceptable bid) + Bid button + optional Buy-now
                var minBid = listing.MinBid > 0 ? (int)listing.MinBid : (int)listing.Price + 1;
                BidBox = new UITextBox
                {
                    X = 6,
                    Y = 28,
                    Width = 96,
                    Height = 24,
                    MaxLength = 12,
                    CustomFontSize = FONT,
                    Prefix = string.Empty,
                    Text = minBid.ToString(),
                    BackgroundColor = new Color(30, 26, 19),
                    BorderColor = new Color(86, 72, 48),
                    FocusedBackgroundColor = new Color(38, 33, 23)
                };
                AddChild(BidBox);

                var bid = new MenuButton("Bid", 64, 24) { X = 106, Y = 28, CustomFontSize = FONT };
                bid.Clicked = _ =>
                {
                    if (int.TryParse(BidBox.Text?.Trim(), out var amount) && (amount > 0))
                        OnBid?.Invoke(amount);
                };
                AddChild(bid);

                if (listing.BuyNow > 0)
                {
                    var bn = new MenuButton($"Buy now {listing.BuyNow:N0}", width - 178, 24) { X = 174, Y = 28, CustomFontSize = FONT };
                    bn.Clicked = _ => OnBuyNow?.Invoke();
                    AddChild(bn);
                }
            }
        }

        //keep the "= total Gold" line in step with the quantity box
        public override void Update(GameTime gameTime)
        {
            base.Update(gameTime);

            if (QtyBox is null || TotalLabel is null)
                return;

            var text = QtyBox.Text ?? string.Empty;

            if (text == LastQty)
                return;

            LastQty = text;
            var qty = Math.Clamp(int.TryParse(text.Trim(), out var q) ? q : 1, 1, (int)Listing.Count);
            TotalLabel.Text = $"= {(long)qty * Listing.Price:N0} Gold";
        }
    }
}
