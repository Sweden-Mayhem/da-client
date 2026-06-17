#region
using Chaos.Client.Controls.Components;
using Chaos.Client.Controls.World.Hud.Panel;
using Chaos.Client.Data;
using Chaos.Client.Definitions;
using Chaos.Client.Models;
using Chaos.DarkAges.Definitions;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
#endregion

namespace Chaos.Client.Controls.World.Popups.Profile;

/// <summary>
///     Equipment tab page within the status book, loaded from _nui_eq prefab. Displays 18 equipment slots as a paper doll
///     layout with item icons. Each slot has a fixed position from the prefab and maps to an <see cref="EquipmentSlot" />.
///     Empty slots show a placeholder icon from _nui_eqi; occupied slots show the item's panel icon.
/// </summary>
public sealed class SelfProfileEquipmentTab : PrefabPanel
{
    //emoticon status icon frame index → _nemots.spf frame
    private const int EMOTICON_FRAME_COUNT = 8;

    //idle frame for south-facing direction (walk anim frames 5-9, idle = 5)
    private const int PAPERDOLL_IDLE_FRAME = 5;

    //level-up stat-raise arrows (shared art with the Stats window); gap is the pixels between a stat value and its arrow
    private const string LEVELUP_EPF = "levelup.epf";
    private const int STAT_ARROW_GAP = 3;

    //TrueType pixel size for every label on this page; the book's ScaleHost magnifies it (so the on-screen size is
    //EQUIP_FONT * WindowScale). 11 visually matches the retail 12px bitmap it replaces.
    private const int EQUIP_FONT = 11;
    private const int EQUIP_FONT_MIN = 7;

    //one stat-raise arrow per primary attribute (STR/INT/WIS/CON/DEX), shown only when there are unspent points
    private readonly StatButton?[] StatRaiseButtons = new StatButton?[5];
    private readonly UILabel? AcLabel;
    private readonly UILabel? ClanLabel;
    private readonly UILabel? ClanTitleLabel;
    private readonly UILabel? ClassLabel;
    private readonly UILabel? ConLabel;

    private readonly UILabel? DexLabel;

    //emoticon status
    private readonly Texture2D?[] EmoticonIcons;

    private readonly UILabel? EmoticonLabel;
    private readonly UIButton? GroupBtn;
    private readonly Texture2D? GroupClosedTexture;
    private readonly Texture2D? GroupOpenTexture;
    private readonly UIImage? EmoticonImage;
    private readonly UILabel? IntLabel;

    //player info labels
    private readonly UILabel? NameLabel;

    //nation icon and text
    private readonly UIImage? NationImage;
    private readonly UILabel? NationTextLabel;

    //paperdoll
    private readonly UIImage? PaperdollImage;

    //portrait and profile text
    private readonly UILabel? PortraitTextLabel;

    //equipment slot rendering: maps equipmentslot to its visual state
    private readonly Dictionary<EquipmentSlot, EquipmentSlotVisual> SlotVisuals = [];

    //stat labels from the _nui_eq prefab (n_ prefix)
    private readonly UILabel? StrLabel;
    private readonly UILabel? TitleLabel;
    private readonly UILabel? WisLabel;
    private Texture2D? NationIconTexture;
    private Texture2D? PaperdollTexture;

    /// <summary>
    ///     Gets the current profile text from the label.
    /// </summary>
    public string ProfileText => PortraitTextLabel?.Text ?? string.Empty;

    public SelfProfileEquipmentTab(string prefabName)
        : base(prefabName, false)
    {
        Name = prefabName;
        Visible = false;

        //build slot visuals from prefab-created image elements. We use EquipmentSlotImage instead of the
        //generic CreateImage so each slot can initiate a drag when an item is equipped there.
        foreach ((var controlName, var slot) in Constants.EquipmentSlotsByControlName)
        {
            if (!PrefabSet.Contains(controlName))
                continue;

            var rect = GetRect(controlName);

            if (rect == Rectangle.Empty)
                continue;

            var placeholder = UiRenderer.Instance!.GetPrefabTexture(PrefabSet.Name, controlName, 0);

            var slotImage = new EquipmentSlotImage
            {
                Name = controlName,
                X = rect.X,
                Y = rect.Y,
                Width = rect.Width,
                Height = rect.Height,
                Texture = placeholder,
                Slot = slot,

                //names the slot on hover. An OCCUPIED slot shows the item tooltip instead (the resolver's HitEquipItem
                //wins over this generic-fallback tooltip), so this is really the "what goes here" hint for empty slots.
                Tooltip = SlotDisplayName(slot)
            };
            AddChild(slotImage);

            var visual = new EquipmentSlotVisual
            {
                Image = slotImage,
                PlaceholderTexture = placeholder
            };

            SlotVisuals[slot] = visual;
        }

        //stat labels, right-aligned numeric values
        StrLabel = CreateLabel("N_STR", HorizontalAlignment.Right);
        StrLabel?.TruncateWithEllipsis = false;
        
        IntLabel = CreateLabel("N_INT", HorizontalAlignment.Right);
        IntLabel?.TruncateWithEllipsis = false;
        
        WisLabel = CreateLabel("N_WIS", HorizontalAlignment.Right);
        WisLabel?.TruncateWithEllipsis = false;
        
        ConLabel = CreateLabel("N_CON", HorizontalAlignment.Right);
        ConLabel?.TruncateWithEllipsis = false;
        
        DexLabel = CreateLabel("N_DEX", HorizontalAlignment.Right);
        DexLabel?.TruncateWithEllipsis = false;
        
        AcLabel = CreateLabel("N_AC", HorizontalAlignment.Right);
        AcLabel?.TruncateWithEllipsis = false;

        //level-up stat-raise arrows next to STR/INT/WIS/CON/DEX, mirroring the Stats window. Shown only when the player
        //has unspent points; clicking one raises that stat (OnRaiseStat -> server). They are UIButtons (not labels/images)
        //so the decorative non-hit-test pass below leaves them interactive, and they are hidden when there is nothing to
        //spend, so they never block dragging the book by its background.
        var iconCache = UiRenderer.Instance!;

        if (iconCache.GetEpfFrameCount(LEVELUP_EPF) >= 3)
        {
            var blinkA = iconCache.GetEpfTexture(LEVELUP_EPF, 0);
            var blinkB = iconCache.GetEpfTexture(LEVELUP_EPF, 1);
            var hover = iconCache.GetEpfTexture(LEVELUP_EPF, 2);

            (UILabel? Label, Stat Stat)[] arrowRows =
            [
                (StrLabel, Stat.STR), (IntLabel, Stat.INT), (WisLabel, Stat.WIS), (ConLabel, Stat.CON), (DexLabel, Stat.DEX)
            ];

            var w = blinkA?.Width ?? 16;
            var h = blinkA?.Height ?? 18;

            for (var i = 0; i < arrowRows.Length; i++)
            {
                if (arrowRows[i].Label is not { } label)
                    continue;

                var btn = new StatButton
                {
                    Name = $"LevelUp_{arrowRows[i].Stat}",
                    X = label.X + label.Width + STAT_ARROW_GAP,
                    Y = label.Y + (label.Height - h) / 2,
                    Width = w,
                    Height = h,
                    PressedTexture = hover,
                    Visible = false
                };

                if ((blinkA is not null) && (blinkB is not null))
                    btn.SetFrames(blinkA, blinkB);

                var captured = arrowRows[i].Stat;
                btn.Clicked += () => OnRaiseStat?.Invoke(captured);
                btn.Tooltip = $"Raise {captured}\nYou have unspent stat points. Click to permanently increase your {captured}. This cannot be undone.";
                AddChild(btn);
                StatRaiseButtons[i] = btn;
            }
        }

        //player info labels -left-aligned text
        NameLabel = CreateLabel("NAME");
        ClassLabel = CreateLabel("CLASSTEXT");
        ClanLabel = CreateLabel("CLANTEXT");
        ClanLabel?.TruncateWithEllipsis = false;
        
        ClanTitleLabel = CreateLabel("CLANTITLETEXT");
        ClanTitleLabel?.TruncateWithEllipsis = false;
        
        TitleLabel = CreateLabel("TITLETEXT");
        TitleLabel?.TruncateWithEllipsis = false;

        //group button - swaps textures based on groupopen state
        //GroupBtn prefab has the "open/recruiting" images; GroupBtn_Disabled has the "closed" image
        GroupBtn = CreateButton("GroupBtn");

        if (GroupBtn is not null)
        {
            GroupOpenTexture = GroupBtn.NormalTexture;
            GroupBtn.PressedTexture = null;
            GroupBtn.Clicked += () => OnGroupToggled?.Invoke();
        }

        //extract the closed-state texture from groupbtn_disabled for the closed state icon
        if (CreateImage("GroupBtn_Disabled") is { } disabledImage)
        {
            GroupClosedTexture = disabledImage.Texture;
            Children.Remove(disabledImage);
            disabledImage.Dispose();
        }

        //nation icon and text
        NationImage = CreateImage("Nation");
        NationTextLabel = CreateLabel("NationText");
        NationTextLabel?.VerticalAlignment = VerticalAlignment.Top;
        NationTextLabel?.ForegroundColor = LegendColors.White;

        //paperdoll area
        PaperdollImage = CreateImage("HumanImage");

        //portrait and profile text
        CreateImage("Portrait");
        PortraitTextLabel = CreateLabel("PortraitText");

        if (PortraitTextLabel is not null)
        {
            PortraitTextLabel.WordWrap = true;
            PortraitTextLabel.ForegroundColor = Color.White;
        }

        //emoticon status areas
        var humanIconRect = GetRect("HumanIcon");

        //load emoticon icons from _nemots.spf (frames 0-7)
        EmoticonIcons = new Texture2D?[EMOTICON_FRAME_COUNT];

        for (var i = 0; i < EMOTICON_FRAME_COUNT; i++)
            EmoticonIcons[i] = UiRenderer.Instance!.GetSpfTexture("_nemots.spf", i);

        //emoticon status text label -prefab places it at the same origin as the icon, so shift
        //it right past the icon to avoid overlap
        EmoticonLabel = CreateLabel("HumanState");
        EmoticonLabel?.ForegroundColor = TextColors.Default;

        //the social-status word is CENTERED in the area to the right of the icon and AUTO-SHRINKS so a long status (e.g.
        //"Day Dreaming") fits the plate instead of clipping. Shift past the icon and shrink the box by the same amount.
        if (EmoticonLabel is not null)
        {
            EmoticonLabel.HorizontalAlignment = HorizontalAlignment.Center;
            EmoticonLabel.AutoFitWidth = true;

            if (humanIconRect != Rectangle.Empty)
            {
                EmoticonLabel.X += humanIconRect.Width + 2;
                EmoticonLabel.Width -= humanIconRect.Width + 2;
            }

            //nudge the current-social-status word left 2px / up 1px to sit centered on its plate
            EmoticonLabel.X -= 2;
            EmoticonLabel.Y -= 1;
        }

        //emoticon icon -drawn as a uiimage child so it participates in the regular child render
        //pipeline. this ensures zindex ordering works correctly, allowing the tooltip (zindex 10)
        //to draw on top of the emoticon icon.
        if (humanIconRect != Rectangle.Empty)
        {
            EmoticonImage = new UIImage
            {
                Name = "EmoticonIcon",
                X = humanIconRect.X,
                Y = humanIconRect.Y,
                Width = humanIconRect.Width,
                Height = humanIconRect.Height,
                Texture = EmoticonIcons[0]
            };
            AddChild(EmoticonImage);
        }

        //drag-by-background: the status book wraps this page in a draggable host and the page is pass-through,
        //so decorative labels/images must not capture the mouse or they would block dragging on their spots.
        //Mark every label/image non-hit-testable, then restore the genuinely interactive ones: equipment slots
        //(click to unequip), the profile-text label (click to open the editor), and the social-status emoticon
        //(click to open the picker). Buttons and text boxes are neither UILabel nor UIImage, so they stay
        //interactive untouched. Tooltips are driven by WorldScreen polling the slot/stat rects, so the value
        //labels can stay non-hit-testable (the book still drags from their spots).
        foreach (var child in Children)
        {
            if (child is UILabel or UIImage)
                child.IsHitTestVisible = false;

            //the book sits in a magnifying ScaleHost, so paint every label in crisp native TTF instead of the upscaled
            //retail bitmap font (matches the rest of the converted menus). Native() keeps the label's rect, so the paper-
            //doll layout + info hotspots are unchanged.
            if (child is UILabel label)
                label.Native(EQUIP_FONT);
        }

        foreach (var visual in SlotVisuals.Values)
            visual.Image.IsHitTestVisible = true;

        PortraitTextLabel?.IsHitTestVisible = true;

        //the social-status emoticon (icon + its label) is clickable - it opens the Social status picker. Restoring
        //hit-testing here also stops a click there from dragging the book.
        EmoticonImage?.IsHitTestVisible = true;
        EmoticonLabel?.IsHitTestVisible = true;

        //tooltips on the interactive gadgets. These are hit-test-visible, so the resolver's generic-fallback step
        //(any hovered control with a Tooltip) shows them - no per-control resolver code needed.
        if (GroupBtn is not null)
            GroupBtn.Tooltip = "Grouping\nControls whether other players are allowed to invite you into their group.\n\nClick to toggle it on or off.";

        if (EmoticonImage is not null)
            EmoticonImage.Tooltip = "Social status\nThe little face shown next to your name, telling others how you feel - friendly, busy, looking to group, and so on.\n\nClick to change it.";

        if (EmoticonLabel is not null)
            EmoticonLabel.Tooltip = "Social status\nThe little face shown next to your name, telling others how you feel - friendly, busy, looking to group, and so on.\n\nClick to change it.";

        if (PortraitTextLabel is not null)
            PortraitTextLabel.Tooltip = "Profile text\nThis is your profile text. Every player who opens your profile will read it, so use it to describe your character.\n\nClick to edit.";

        //hover info over the baked-art field labels (the STR/Name/Guild/... words painted into _nui_eq), mirroring
        //the Stats window. Built last so every value label it references already exists.
        BuildInfoHotspots();
    }

    private static string SlotDisplayName(EquipmentSlot slot)
        => slot switch
        {
            EquipmentSlot.Weapon     => "Weapon",
            EquipmentSlot.Armor      => "Armor",
            EquipmentSlot.Shield     => "Shield",
            EquipmentSlot.Helmet     => "Helmet",
            EquipmentSlot.Earrings   => "Earrings",
            EquipmentSlot.Necklace   => "Necklace",
            EquipmentSlot.LeftRing   => "Left Ring",
            EquipmentSlot.RightRing  => "Right Ring",
            EquipmentSlot.LeftGaunt  => "Left Gauntlet",
            EquipmentSlot.RightGaunt => "Right Gauntlet",
            EquipmentSlot.Belt       => "Belt",
            EquipmentSlot.Greaves    => "Greaves",
            EquipmentSlot.Boots      => "Boots",
            EquipmentSlot.Overcoat   => "Overcoat",
            EquipmentSlot.OverHelm   => "Over-Helm",
            _                        => "Accessory"
        };

    //lay a transparent InfoHotspot over each baked field NAME (the words left of the value boxes in the _nui_eq art).
    //The stat words reuse the same StatInfo help as their value hover / the Stats window; the identity words get a
    //short note. Geometry comes from the value-label rects (so it tracks the prefab); leftBound is the per-column
    //left edge of the word, eyeballed from the rendered art (nudge if a word is missed).
    private void BuildInfoHotspots()
    {
        //stats (bottom of the left page, a 3-column grid)
        AddStatWordHotspot(StrLabel, 30, StatInfoKind.Strength);
        AddStatWordHotspot(IntLabel, 30, StatInfoKind.Intelligence);
        AddStatWordHotspot(WisLabel, 108, StatInfoKind.Wisdom);
        AddStatWordHotspot(ConLabel, 108, StatInfoKind.Constitution);
        AddStatWordHotspot(DexLabel, 190, StatInfoKind.Dexterity);
        AddStatWordHotspot(AcLabel, 190, StatInfoKind.ArmorClass);

        //identity word plates (right page)
        AddWordHotspot(NameLabel, 362, "Name", "Your character's name.");
        AddWordHotspot(ClassLabel, 362, "Class", "Your character's class.");
        AddWordHotspot(ClanLabel, 362, "Guild", "The guild you belong to, if any.");
        AddWordHotspot(ClanTitleLabel, 362, "Guild Rank", "Your rank within your guild.");
        AddWordHotspot(TitleLabel, 362, "Title", "An honorific title you have earned.");

        //nation crest (no value label to anchor to - fixed to the _nui_eq "Nation" rect)
        AddChild(
            new InfoHotspot("Nation", "Your nation, or homeland.")
            {
                Name = "info_Nation",
                X = 351,
                Y = 152,
                Width = 42,
                Height = 49
            });
    }

    private void AddStatWordHotspot(UILabel? value, int leftBound, StatInfoKind kind)
    {
        var (title, body) = StatInfo.Get(kind);
        AddWordHotspot(value, leftBound, title, body);
    }

    private void AddWordHotspot(UILabel? value, int leftBound, string title, string body)
    {
        if (value is null)
            return;

        var right = value.X - 1; //stop just left of the value box

        if (right <= leftBound)
            return;

        AddChild(
            new InfoHotspot(title, body)
            {
                Name = $"info_{value.Name}",
                X = leftBound,
                Y = value.Y - 2,
                Width = right - leftBound,
                Height = value.Height + 4
            });
    }

    /// <summary>
    ///     Clears all equipment slot icons, restoring placeholders.
    /// </summary>
    public void ClearAllSlots()
    {
        foreach ((_, var visual) in SlotVisuals)
        {
            if (visual.ItemTexture is not null)
            {
                visual.ItemTexture.Dispose();
                visual.ItemTexture = null;
            }

            visual.Image.Texture = visual.PlaceholderTexture;
            visual.Image.IsEquipped = false;
        }
    }

    /// <summary>
    ///     Clears the item icon for a specific equipment slot, restoring the placeholder.
    /// </summary>
    public void ClearSlot(EquipmentSlot slot)
    {
        if (!SlotVisuals.TryGetValue(slot, out var visual))
            return;

        if (visual.ItemTexture is not null)
        {
            visual.ItemTexture.Dispose();
            visual.ItemTexture = null;
        }

        visual.Image.Texture = visual.PlaceholderTexture;
        visual.Image.IsEquipped = false;
    }

    /// <summary>
    ///     Returns true if the given screen point is within any equipment slot image.
    /// </summary>
    public bool ContainsEquipmentSlotPoint(int screenX, int screenY)
    {
        foreach ((_, var visual) in SlotVisuals)
            if (visual.Image.ContainsPoint(screenX, screenY))
                return true;

        return false;
    }

    public override void Dispose()
    {
        foreach ((_, var visual) in SlotVisuals)
            if (visual.ItemTexture is not null)
            {
                if (visual.Image.Texture == visual.ItemTexture)
                    visual.Image.Texture = null;

                visual.ItemTexture.Dispose();
            }

        SlotVisuals.Clear();
        NationIconTexture?.Dispose();
        PaperdollTexture?.Dispose();

        //clear the emoticon image texture so uiimage.dispose doesn't dispose the cached spf texture
        EmoticonImage?.Texture = null;

        //uiimage children are disposed by base.dispose, but we own the dynamic textures
        base.Dispose();
    }

    public event GroupToggledHandler? OnGroupToggled;
    public event ProfileTextClickedHandler? OnProfileTextClicked;
    public event RaiseStatHandler? OnRaiseStat;
    public event Action? OnSocialStatusClicked;
    public event UnequipHandler? OnUnequip;

    /// <summary>
    ///     Shows or hides the level-up stat-raise arrows. They appear (and pulse) only while the player has unspent
    ///     attribute points, exactly like the Stats window.
    /// </summary>
    public void SetUnspentPoints(int count)
    {
        var hasPoints = count > 0;

        foreach (var btn in StatRaiseButtons)
            if (btn is not null)
            {
                btn.Visible = hasPoints;
                btn.SetAnimating(hasPoints);
            }
    }

    /// <summary>Returns the equipment slot whose occupied item icon contains the (book-space) point, or null.</summary>
    public EquipmentSlot? HitItemSlot(int x, int y)
    {
        foreach ((var slot, var visual) in SlotVisuals)
            if ((visual.ItemTexture is not null) && visual.Image.ContainsPoint(x, y))
                return slot;

        return null;
    }

    /// <summary>Returns the stat whose value label contains the (book-space) point, or null.</summary>
    public StatInfoKind? HitStatInfo(int x, int y)
    {
        if (Hit(StrLabel))
            return StatInfoKind.Strength;

        if (Hit(IntLabel))
            return StatInfoKind.Intelligence;

        if (Hit(WisLabel))
            return StatInfoKind.Wisdom;

        if (Hit(ConLabel))
            return StatInfoKind.Constitution;

        if (Hit(DexLabel))
            return StatInfoKind.Dexterity;

        if (Hit(AcLabel))
            return StatInfoKind.ArmorClass;

        return null;

        bool Hit(UILabel? l) => (l is not null) && l.ContainsPoint(x, y);
    }

    /// <summary>
    ///     Renders an item icon from the panel item sprite sheet using the same pipeline as inventory icons.
    /// </summary>
    /// <summary>
    ///     Sets the emoticon/social status icon and text. State 0-7 maps to _nemots.spf frames.
    /// </summary>
    public void SetEmoticonState(byte state, string statusText)
    {
        EmoticonLabel?.Text = statusText;

        if ((EmoticonImage is not null) && (state < EmoticonIcons.Length))
            EmoticonImage.Texture = EmoticonIcons[state];
    }

    /// <summary>
    ///     Swaps the group button texture between recruiting (open) and closed states.
    /// </summary>
    public void SetGroupOpen(bool groupOpen)
    {
        GroupBtn?.NormalTexture = groupOpen ? GroupOpenTexture : GroupClosedTexture;
    }

    /// <summary>
    ///     Sets the nation icon (from _nui_nat.spf, frame = nationId - 1).
    /// </summary>
    public void SetNation(byte nationId)
    {
        NationIconTexture?.Dispose();
        NationIconTexture = null;

        if (nationId > 0)
            NationIconTexture = UiRenderer.Instance!.GetSpfTexture("_nui_nat.spf", nationId - 1);

        NationImage?.Texture = NationIconTexture;

        if (NationTextLabel is not null)
        {
            var nationMeta = DataContext.MetaFiles.GetNationMetadata();
            NationTextLabel.Text = nationMeta?.Nations.TryGetValue(nationId, out var name) == true ? name : string.Empty;
        }
    }

    /// <summary>
    ///     Renders the paperdoll using the player's current appearance. Uses the full AislingRenderer at the south-facing idle
    ///     frame (same composition as the world aisling, just frozen).
    /// </summary>
    public void SetPaperdoll(AislingRenderer renderer, in AislingAppearance appearance)
    {
        PaperdollTexture?.Dispose();

        //south-facing (direction=2) = right idle frame (5) + horizontal flip
        PaperdollTexture = renderer.Render(in appearance, PAPERDOLL_IDLE_FRAME, flipHorizontal: true);

        PaperdollImage?.Texture = PaperdollTexture;
    }

    /// <summary>
    ///     Updates the player identity labels (name, class, clan, title).
    /// </summary>
    public void SetPlayerInfo(
        string name,
        string className,
        string clanName,
        string clanTitle,
        string title)
    {
        NameLabel?.ForegroundColor = LegendColors.White;
        NameLabel?.Text = name;
        ClassLabel?.ForegroundColor = LegendColors.White;
        ClassLabel?.Text = className;
        ClanLabel?.ForegroundColor = LegendColors.White;
        ClanLabel?.Text = clanName;
        ClanTitleLabel?.ForegroundColor = LegendColors.White;
        ClanTitleLabel?.Text = clanTitle;
        TitleLabel?.ForegroundColor = LegendColors.White;
        TitleLabel?.Text = title;
    }

    /// <summary>
    ///     Sets the profile text on the display label.
    /// </summary>
    public void SetProfileText(string text)
    {
        PortraitTextLabel?.Text = text;
    }

    /// <summary>
    ///     Sets the item icon for a specific equipment slot.
    /// </summary>
    public void SetSlot(EquipmentSlot slot, ushort sprite, DisplayColor color, string? itemName = null)
    {
        if (!SlotVisuals.TryGetValue(slot, out var visual))
            return;

        //dispose previous item texture (not the placeholder -that's shared/owned by the prefab)
        if (visual.ItemTexture is not null)
        {
            visual.ItemTexture.Dispose();
            visual.ItemTexture = null;
        }

        visual.ItemName = itemName ?? string.Empty;

        var texture = UiRenderer.Instance!.GetItemIcon(sprite, color);
        visual.ItemTexture = texture;
        visual.Image.Texture = texture;
        visual.Image.IsEquipped = true;
    }

    /// <summary>
    ///     Updates the stat display labels on the equipment page.
    /// </summary>
    public void UpdateStats(
        int str,
        int intel,
        int wis,
        int con,
        int dex,
        int ac)
    {
        SetStatLabel(StrLabel, str);
        SetStatLabel(IntLabel, intel);
        SetStatLabel(WisLabel, wis);
        SetStatLabel(ConLabel, con);
        SetStatLabel(DexLabel, dex);
        SetStatLabel(AcLabel, ac);
    }

    private static void SetStatLabel(UILabel? label, int value)
    {
        if (label is null)
            return;

        var text = $"{value}";

        //default to EQUIP_FONT and shrink down to EQUIP_FONT_MIN so negative AC values and other
        //unexpectedly wide numbers never overflow the fixed-width label box
        if (TtfTextRenderer.Available && (label.Width > 0))
        {
            var size = EQUIP_FONT;

            while ((size > EQUIP_FONT_MIN) && (TtfTextRenderer.MeasureWidth(text, size) > label.Width))
                size--;

            label.CustomFontSize = size;
        }

        label.Text = text;
    }

    public override void OnClick(ClickEvent e)
    {
        if (e.Button != MouseButton.Left)
            return;

        foreach ((var slot, var visual) in SlotVisuals)
            if (visual.Image.ContainsPoint(e.ScreenX, e.ScreenY) && (visual.ItemTexture is not null))
            {
                OnUnequip?.Invoke(slot);
                e.Handled = true;

                return;
            }

        //the social-status emoticon (icon or its text) opens the Social status picker at the cursor
        if (((EmoticonImage is not null) && EmoticonImage.ContainsPoint(e.ScreenX, e.ScreenY))
            || ((EmoticonLabel is not null) && EmoticonLabel.ContainsPoint(e.ScreenX, e.ScreenY)))
        {
            OnSocialStatusClicked?.Invoke();
            e.Handled = true;

            return;
        }

        //check if portrait text area was clicked
        if (PortraitTextLabel is not null && PortraitTextLabel.ContainsPoint(e.ScreenX, e.ScreenY))
        {
            OnProfileTextClicked?.Invoke();
            e.Handled = true;
        }
    }

    private sealed class EquipmentSlotVisual
    {
        public required EquipmentSlotImage Image { get; init; }
        public string ItemName { get; set; } = string.Empty;
        public Texture2D? ItemTexture { get; set; }
        public Texture2D? PlaceholderTexture { get; init; }
    }

    /// <summary>
    ///     An equipment slot icon that supports drag-and-drop. When an item is equipped, dragging this image
    ///     fires an <see cref="EquipmentDragPayload" /> so WorldScreen can call Unequip on drop.
    /// </summary>
    private sealed class EquipmentSlotImage : UIImage
    {
        public EquipmentSlot Slot { get; init; }
        public bool IsEquipped { get; set; }

        public override void OnDragStart(DragStartEvent e)
        {
            if (!IsEquipped)
                return;

            e.Payload = new EquipmentDragPayload
            {
                Slot = Slot,
                Icon = Texture
            };
        }
    }
}