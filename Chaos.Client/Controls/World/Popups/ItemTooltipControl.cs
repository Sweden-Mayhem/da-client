#region
using Chaos.Client.Controls.Components;
using Chaos.Client.Data;
using Chaos.Client.Systems;
using Microsoft.Xna.Framework;
#endregion

namespace Chaos.Client.Controls.World.Popups;

/// <summary>
///     Inventory/shop item tooltip: a simple semi-transparent black panel (same scalable style as the town-map warp
///     tooltip) that follows the cursor. Lays out, top to bottom: name (white), category (grey), a gap, the description
///     (yellow), a gap, then durability (blue) when the item has any. All text uses the optional UI font (Cinzel). The
///     category/description come from the client's ItemInfo metafiles, looked up by the item's clean name.
/// </summary>
public sealed class ItemTooltipControl : UIPanel
{
    private const int NAME_SIZE = 15;
    private const int SUB_SIZE = 13;
    private const int PADDING = 6;
    private const int SECTION_GAP = 8; //the blank line between the name/category, description, and durability blocks
    private const int SAFETY = 3;      //small right-side buffer so the TTF's last glyph never clips
    private const int DESC_WRAP = 480; //MAX description width; the tooltip auto-sizes to the widest ACTUAL line
                                       //below this, so short lines never wrap (there is room) and only genuinely
                                       //long prose still wraps. Server descriptions keep their own \n line breaks.
    private const int INFO_WRAP = 240; //narrower wrap for the stat-help tooltip (ShowInfo) so the prose forms a tidy column

    private static readonly Color NameColor = new(240, 238, 232);
    private static readonly Color CategoryColor = new(158, 156, 148);
    private static readonly Color DescColor = new(230, 212, 140);
    private static readonly Color DurabilityColor = new(100, 149, 237);

    private readonly UILabel NameLabel;
    private readonly UILabel CategoryLabel;

    //one label per description PARAGRAPH (split on blank lines), grown lazily. Placing each paragraph with the
    //same SECTION_GAP as the name/durability blocks makes every gap in the tooltip identical - a blank line
    //rendered inside one label is a full font line tall, which read as a bigger hole than the other gaps.
    private readonly List<UILabel> DescLabels = [];
    private readonly UILabel DurabilityLabel;
    private readonly UILabel WeightLabel;

    //layout metrics for the current Show/ShowInfo, recomputed from ClientSettings.TooltipScale by ApplyScale
    private int Pad;
    private int Gap;
    private int Safety;

    public ItemTooltipControl()
    {
        Name = "ItemTooltip";
        Visible = false;
        IsHitTestVisible = false;

        //the map-hover background: a plain semi-transparent black fill that scales to whatever size we set.
        //opacity is the player's "Tooltip opacity" setting, re-read in Show() so the slider takes effect live.
        BackgroundColor = Color.Black * ClientSettings.TooltipAlpha;

        NameLabel = MakeLabel(NAME_SIZE, NameColor, false);
        CategoryLabel = MakeLabel(SUB_SIZE, CategoryColor, false);
        DurabilityLabel = MakeLabel(SUB_SIZE, DurabilityColor, false);
        WeightLabel = MakeLabel(SUB_SIZE, CategoryColor, false); //grey, like the category line

        AddChild(NameLabel);
        AddChild(CategoryLabel);
        AddChild(DurabilityLabel);
        AddChild(WeightLabel);
    }

    private static UILabel MakeLabel(int fontSize, Color color, bool wrap)
        => new()
        {
            X = PADDING,
            CustomFontSize = fontSize,
            WordWrap = wrap,
            PaddingLeft = 0,
            PaddingTop = 0,
            PaddingRight = 0,
            PaddingBottom = 0,
            ForegroundColor = color
        };

    public void Hide() => Visible = false;

    //applies the player's "Tooltip size" to the fonts + spacing, and returns the (scaled) description/info wrap widths.
    //Called at the top of Show/ShowInfo so the slider takes effect live.
    private (int DescWrap, int InfoWrap) ApplyScale()
    {
        var scale = ClientSettings.TooltipScale;

        NameLabel.CustomFontSize = Scaled(NAME_SIZE, scale);
        CategoryLabel.CustomFontSize = Scaled(SUB_SIZE, scale);

        foreach (var desc in DescLabels)
            desc.CustomFontSize = Scaled(SUB_SIZE, scale);

        DurabilityLabel.CustomFontSize = Scaled(SUB_SIZE, scale);
        WeightLabel.CustomFontSize = Scaled(SUB_SIZE, scale);

        Pad = Scaled(PADDING, scale);
        Gap = Scaled(SECTION_GAP, scale);
        Safety = Math.Max(1, Scaled(SAFETY, scale));

        return (Scaled(DESC_WRAP, scale), Scaled(INFO_WRAP, scale));
    }

    private static int Scaled(int value, float scale) => (int)((value * scale) + 0.5f);

    /// <summary>
    ///     Shows a plain info tooltip: a white <paramref name="title" />, an optional grey <paramref name="category" />
    ///     line (e.g. "Lv 1 Skill"), then a wrapped, &lt;green&gt;/&lt;red&gt;-colored <paramref name="body" />. Reuses the
    ///     item tooltip's name + category + description rows (no durability). Used for the Stats window, the Equipment book
    ///     stat hovers, and the skill/spell hovers.
    /// </summary>
    public void ShowInfo(string title, string category, string body, int mouseX, int mouseY)
    {
        BackgroundColor = Color.Black * ClientSettings.TooltipAlpha; //pick up the current "Tooltip opacity" setting
        var (_, infoWrap) = ApplyScale();                            //and the current "Tooltip size"

        var y = Pad;
        var contentWidth = 0;

        Place(NameLabel, title, ref y, ref contentWidth);

        if (category.Length > 0)
            Place(CategoryLabel, category, ref y, ref contentWidth);
        else
            CategoryLabel.Visible = false;

        PlaceBody(body, infoWrap, ref y, ref contentWidth);

        DurabilityLabel.Visible = false;
        WeightLabel.Visible = false;

        Width = Pad + contentWidth + Pad;
        Height = y + Pad;

        UpdatePosition(mouseX, mouseY);
        Visible = true;
    }

    /// <summary>Category-less info tooltip (the stat-help popups): title over body.</summary>
    public void ShowInfo(string title, string body, int mouseX, int mouseY) => ShowInfo(title, string.Empty, body, mouseX, mouseY);

    /// <summary>
    ///     Positions the tooltip relative to the cursor. Flips to the left of the cursor when the default right-side
    ///     placement would overflow the virtual viewport. Must be called after <see cref="UIElement.Width" /> and
    ///     <see cref="UIElement.Height" /> are set.
    /// </summary>
    public void UpdatePosition(int mouseX, int mouseY)
    {
        var rightX = mouseX + 15;

        X = (rightX + Width) <= ChaosGame.UiWidth ? rightX : mouseX - Width;
        Y = Math.Clamp(mouseY + 23, 0, Math.Max(0, ChaosGame.UiHeight - Height));
    }

    public void Show(
        string itemName,
        int currentDurability,
        int maxDurability,
        int mouseX,
        int mouseY)
    {
        BackgroundColor = Color.Black * ClientSettings.TooltipAlpha; //pick up the current "Tooltip opacity" setting
        var (descWrap, _) = ApplyScale();                            //and the current "Tooltip size"

        var (category, description, weight) = LookupMeta(itemName);

        var y = Pad;
        var contentWidth = 0;

        //name (white)
        Place(NameLabel, itemName, ref y, ref contentWidth);

        //category (grey), directly under the name
        if (category.Length > 0)
            Place(CategoryLabel, category, ref y, ref contentWidth);
        else
            CategoryLabel.Visible = false;

        //blank line + description (yellow), wrapped; paragraphs (blank-line separated) each get the section gap
        PlaceBody(description, descWrap, ref y, ref contentWidth);

        //blank line then the stat block: durability (blue) when the item has any, then weight (grey) below it.
        //the gap is added once before whichever stat line comes first so the block sits clear of the description.
        var hasDurability = maxDurability > 0;
        var hasWeight = weight > 0;

        if (hasDurability)
        {
            y += Gap;
            Place(DurabilityLabel, $"Durability: {currentDurability}/{maxDurability}", ref y, ref contentWidth);
        } else
            DurabilityLabel.Visible = false;

        if (hasWeight)
        {
            if (!hasDurability)
                y += Gap;

            Place(WeightLabel, $"Weight: {weight}", ref y, ref contentWidth);
        } else
            WeightLabel.Visible = false;

        Width = Pad + contentWidth + Pad;
        Height = y + Pad;

        UpdatePosition(mouseX, mouseY);
        Visible = true;
    }

    //lays the body/description out paragraph by paragraph (split on blank lines), one label per paragraph, each
    //separated by the SAME section gap as the tooltip's other blocks - so all gaps read identically
    private void PlaceBody(string body, int wrapWidth, ref int y, ref int contentWidth)
    {
        var paragraphs = body.Length > 0
            ? body.Split("\n\n", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : [];

        for (var i = 0; i < paragraphs.Length; i++)
        {
            var label = GetDescLabel(i);
            y += Gap;
            label.Width = wrapWidth;
            Place(label, paragraphs[i], ref y, ref contentWidth, isWrapped: true);
        }

        for (var i = paragraphs.Length; i < DescLabels.Count; i++)
            DescLabels[i].Visible = false;
    }

    //the pool grows to the largest paragraph count seen; extras stay hidden
    private UILabel GetDescLabel(int index)
    {
        while (DescLabels.Count <= index)
        {
            var label = MakeLabel(Scaled(SUB_SIZE, ClientSettings.TooltipScale), DescColor, true);
            label.RichTextMarkup = true; //honor the server description's <green>/<red>/<white> effect coloring
            DescLabels.Add(label);
            AddChild(label);
        }

        return DescLabels[index];
    }

    //lays a label at the running y, advances y past it, and grows contentWidth to fit it
    private void Place(UILabel label, string text, ref int y, ref int contentWidth, bool isWrapped = false)
    {
        label.Visible = true;
        label.X = Pad;
        label.Text = text;
        label.Y = y;

        if (!isWrapped)
            label.Width = label.ContentWidth + Safety;

        label.Height = label.ContentHeight;
        contentWidth = Math.Max(contentWidth, label.ContentWidth + Safety);
        y += label.ContentHeight;
    }

    //category ("Lv 14 Weapon" / "Enemydrops"), description, and weight from the client's ItemInfo metafiles, keyed by
    //item name. The category word is singularized (Potions -> Potion, Earrings -> Earring) for a tidier tooltip; the
    //shop list keeps the plural form.
    private static (string Category, string Description, int Weight) LookupMeta(string itemName)
    {
        if (string.IsNullOrEmpty(itemName))
            return (string.Empty, string.Empty, 0);

        var metadata = DataContext.MetaFiles.GetItemMetadata(itemName);

        if (!metadata.TryGetValue(itemName, out var meta) || (meta is null))
            return (string.Empty, string.Empty, 0);

        var hasCategory = !string.IsNullOrWhiteSpace(meta.Category);
        var categoryWord = hasCategory ? Singularize(meta.Category.Trim()) : string.Empty;

        var category = meta.Level > 0
            ? hasCategory ? $"Lv {meta.Level} {categoryWord}" : $"Lv {meta.Level}"
            : hasCategory ? categoryWord : string.Empty;

        return (category, meta.Description?.Trim() ?? string.Empty, meta.Weight);
    }

    //drops a single trailing plural "s" off the last word of the category (Potions -> Potion). Leaves "ss" endings
    //(Glass/Dress) and very short words alone so we never mangle a singular noun that happens to end in s.
    private static string Singularize(string category)
    {
        var space = category.LastIndexOf(' ');
        var word = space >= 0 ? category[(space + 1)..] : category;

        if ((word.Length > 2)
            && word.EndsWith('s')
            && !word.EndsWith("ss", StringComparison.OrdinalIgnoreCase))
            word = word[..^1];

        return space >= 0 ? category[..(space + 1)] + word : word;
    }
}
