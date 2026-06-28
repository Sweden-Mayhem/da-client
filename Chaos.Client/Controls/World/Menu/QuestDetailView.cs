#region
using Chaos.Client.Controls.Components;
using Chaos.Client.Controls.Generic;
using Chaos.Client.Data.Models;
using Chaos.Client.Rendering;
using Chaos.Client.Systems;
using Chaos.DarkAges.Definitions;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
#endregion

namespace Chaos.Client.Controls.World.Menu;

/// <summary>A reward item for the shared quest detail view.</summary>
public sealed record QuestRewardItemView(ushort Sprite, byte Color, int Count, string Name);

/// <summary>A reward legend mark for the shared quest detail view.</summary>
public sealed record QuestRewardMarkView(byte Icon, byte Color, string Title);

/// <summary>One reward OUTCOME for the shared quest detail view (a labelled exp / gold / items / marks bundle).</summary>
public sealed record QuestRewardOutcomeView(string Label, int Exp, int Gold, IReadOnlyList<QuestRewardItemView> Items, IReadOnlyList<QuestRewardMarkView> Marks);

/// <summary>The data the shared quest detail view renders. The CALLER adds the action buttons afterwards.</summary>
public sealed class QuestDetailModel
{
    public string Title = string.Empty;
    public string? Description;
    public bool ShowHowToStart;
    public string? StartHint;

    /// <summary>The reward outcomes to show. One -> a single "Reward" block; several -> a "Rewards" header + a labelled
    ///     sub-block per outcome. When empty, the legacy single <see cref="Exp" />/<see cref="Gold" />/<see cref="Items" />/
    ///     <see cref="Marks" /> fields are used instead (a single unlabelled outcome).</summary>
    public IReadOnlyList<QuestRewardOutcomeView> Outcomes = [];

    public int Exp;
    public int Gold;
    public IReadOnlyList<QuestRewardItemView> Items = [];
    public IReadOnlyList<QuestRewardMarkView> Marks = [];

    /// <summary>The repeatable cadence / live cooldown line shown above the action (e.g. "Daily - repeatable (12h)").</summary>
    public string? StatusLine;
}

/// <summary>
///     ONE renderer for a quest's detail pane, used by BOTH the journal's "startable" detail and the NPC offer
///     window so they look identical: large title / description / --- / [how to start] / Reward (visual icons) /
///     --- / [repeatable info]. Draws into any <see cref="ScrollRegion" />; the caller appends its own buttons.
/// </summary>
public static class QuestDetailView
{
    public const int TITLE_FONT = 22;
    public const int HEADER_FONT = 14;
    public const int BODY_FONT = 14;
    public const int PAD = 8;
    private const int ICON_SCALE = 2;

    public static readonly Color HeaderColor = new(224, 192, 108);
    public static readonly Color BodyColor = new(214, 206, 186);
    public static readonly Color DimColor = new(172, 164, 150);
    private static readonly Color Divider = new(60, 50, 34);
    private static readonly Color ExpRewardColor = new(150, 210, 255);
    private static readonly Color GoldRewardColor = new(255, 224, 96);

    public static int Scaled(int baseSize) => Math.Max(baseSize, (int)MathF.Round(baseSize * ClientSettings.EffectiveQuestFontScale));

    //the full "startable / offer" layout. innerW is the usable content width; onRewardHover reports the item under
    //the cursor (its name) for the tooltip resolver, or null on leave.
    public static void Render(ScrollRegion target, int innerW, QuestDetailModel model, Action<string?>? onRewardHover, ref int y)
    {
        AddTitle(target, model.Title, innerW, ref y);

        if (!string.IsNullOrEmpty(model.Description))
        {
            y += 8;
            AddText(target, model.Description, BODY_FONT, BodyColor, PAD, innerW, ref y, wrap: true);
        }

        AddSeparator(target, innerW, ref y);

        if (model.ShowHowToStart)
        {
            AddText(target, "How to start", HEADER_FONT, HeaderColor, PAD, innerW, ref y, wrap: false);
            y += 4;
            var hint = string.IsNullOrEmpty(model.StartHint) ? "Seek out this quest's giver in the world." : model.StartHint;
            AddText(target, hint, BODY_FONT, BodyColor, PAD, innerW, ref y, wrap: true);
            y += 6;
        }

        AddRewards(target, innerW, EffectiveOutcomes(model), onRewardHover, ref y);

        AddSeparator(target, innerW, ref y);

        if (!string.IsNullOrEmpty(model.StatusLine))
            AddText(target, model.StatusLine, BODY_FONT, DimColor, PAD, innerW, ref y, wrap: false);
    }

    //draws the quest's reward section: a single "Reward" block for one outcome, or a "Rewards" header with a labelled
    //sub-block per outcome when a quest grants several. Used by BOTH the journal and the offer/turn-in window so they
    //always match. Outcomes with nothing to give are skipped (and the labels number the rest 1, 2, ...).
    public static void AddRewards(ScrollRegion target, int innerW, IReadOnlyList<QuestRewardOutcomeView> outcomes, Action<string?>? onRewardHover, ref int y)
    {
        var rewarding = outcomes.Where(OutcomeHasReward).ToList();

        if (rewarding.Count == 0)
            return;

        if (rewarding.Count == 1)
        {
            var only = rewarding[0];
            AddText(target, "Reward", HEADER_FONT, HeaderColor, PAD, innerW, ref y, wrap: false);
            y += 6;
            AddRewardIcons(target, only.Exp, only.Gold, only.Items, only.Marks, PAD, innerW, onRewardHover, ref y);

            return;
        }

        AddText(target, "Rewards", HEADER_FONT, HeaderColor, PAD, innerW, ref y, wrap: false);
        y += 6;

        for (var i = 0; i < rewarding.Count; i++)
        {
            var o = rewarding[i];
            AddText(target, string.IsNullOrEmpty(o.Label) ? $"Outcome {i + 1}" : o.Label, BODY_FONT, DimColor, PAD, innerW, ref y, wrap: false);
            y += 4;
            AddRewardIcons(target, o.Exp, o.Gold, o.Items, o.Marks, PAD, innerW, onRewardHover, ref y);
        }
    }

    //maps the catalog's data-layer reward outcomes to the view's render records (the single conversion both the
    //journal and the offer window go through, so neither can drift from the other)
    public static IReadOnlyList<QuestRewardOutcomeView> ToOutcomeViews(IReadOnlyList<QuestRewardOutcome> outcomes)
        => outcomes.Select(
                       o => new QuestRewardOutcomeView(
                           o.Label,
                           o.Exp,
                           o.Gold,
                           o.Items.Select(i => new QuestRewardItemView(i.Sprite, i.Color, i.Count, i.Name)).ToList(),
                           o.Marks.Select(m => new QuestRewardMarkView(m.Icon, m.Color, m.Title)).ToList()))
                   .ToList();

    private static bool OutcomeHasReward(QuestRewardOutcomeView o) => (o.Exp > 0) || (o.Gold > 0) || (o.Items.Count > 0) || (o.Marks.Count > 0);

    //the outcomes to draw: the model's explicit outcomes, or the legacy single fields folded into one unlabelled outcome
    private static IReadOnlyList<QuestRewardOutcomeView> EffectiveOutcomes(QuestDetailModel model)
    {
        if (model.Outcomes.Count > 0)
            return model.Outcomes;

        if ((model.Exp > 0) || (model.Gold > 0) || (model.Items.Count > 0) || (model.Marks.Count > 0))
            return [new QuestRewardOutcomeView(string.Empty, model.Exp, model.Gold, model.Items, model.Marks)];

        return [];
    }

    public static void AddTitle(ScrollRegion target, string title, int innerW, ref int y)
        => AddText(target, title, TITLE_FONT, HeaderColor, PAD, innerW, ref y, wrap: true);

    public static void AddText(ScrollRegion target, string text, int fontSize, Color color, int x, int width, ref int y, bool wrap)
    {
        fontSize = Scaled(fontSize);

        var label = new UILabel
        {
            X = x, Y = y, Width = width,
            WordWrap = wrap,
            CustomFontSize = fontSize,
            ForegroundColor = color,
            Text = text,
            TruncateWithEllipsis = !wrap,
            IsHitTestVisible = false
        };

        var lineH = TtfTextRenderer.Available ? TtfTextRenderer.LineHeight(fontSize) : fontSize + 4;
        label.Height = wrap ? Math.Max(lineH, label.ContentHeight) + 2 : lineH;
        target.Add(label);
        y += label.Height + 2;
    }

    public static void AddSeparator(ScrollRegion target, int width, ref int y)
    {
        y += 10;
        target.Add(new UIPanel { X = PAD, Y = y, Width = width, Height = 1, BackgroundColor = Divider, IsHitTestVisible = false });
        y += 11;
    }

    //EXP + gold on one centred row, then item icons (count overlay + hover tooltip), then legend marks (icon + title)
    public static void AddRewardIcons(
        ScrollRegion target,
        int exp,
        int gold,
        IReadOnlyList<QuestRewardItemView> items,
        IReadOnlyList<QuestRewardMarkView> marks,
        int x,
        int width,
        Action<string?>? onRewardHover,
        ref int y)
    {
        var ui = UiRenderer.Instance;

        if (ui is null)
            return;

        var font = Scaled(BODY_FONT + 1);
        var lineH = TtfTextRenderer.Available ? TtfTextRenderer.LineHeight(font) : font + 4;

        UILabel Lbl(string text, int lx, int ly, Color color)
        {
            var l = new UILabel
            {
                X = lx, Y = ly, Width = width, Height = lineH, CustomFontSize = font, ForegroundColor = color,
                Text = text, IsHitTestVisible = false, TruncateWithEllipsis = true
            };
            target.Add(l);

            return l;
        }

        //--- Row 1: EXP, then gold (icon + amount), vertically centred together ---
        var goldIcon = gold > 0 ? ui.GetItemIcon(136) : null;
        var goldIw = goldIcon is not null ? IconTexW(goldIcon) * ICON_SCALE : 0;
        var goldIh = goldIcon is not null ? IconTexH(goldIcon) * ICON_SCALE : 0;
        var row1H = Math.Max(lineH, goldIh);
        var cx = x;

        if ((exp > 0) || (goldIcon is not null))
        {
            if (exp > 0)
            {
                var l = Lbl($"+{exp:N0} EXP", cx, y + ((row1H - lineH) / 2), ExpRewardColor);
                cx += l.ContentWidth + 22;
            }

            if (goldIcon is not null)
            {
                target.Add(new RewardIcon { Icon = goldIcon, X = cx, Y = y + ((row1H - goldIh) / 2), Width = goldIw, Height = goldIh });
                Lbl($"{gold:N0}", cx + goldIw + 6, y + ((row1H - lineH) / 2), GoldRewardColor);
            }

            y += row1H + 10;
        }

        //--- Row 2: item icons (count overlaid bottom-right, hover -> tooltip) ---
        if (items.Count > 0)
        {
            var ix = x;
            var rowMaxH = 0;

            foreach (var it in items)
            {
                var icon = ui.GetItemIcon(it.Sprite, (DisplayColor)it.Color);
                var iw = IconTexW(icon) * ICON_SCALE;
                var ih = IconTexH(icon) * ICON_SCALE;

                if ((ix > x) && (ix + iw > x + width))
                {
                    ix = x;
                    y += rowMaxH + 8;
                    rowMaxH = 0;
                }

                target.Add(
                    new RewardIcon
                    {
                        Icon = icon, X = ix, Y = y, Width = iw, Height = ih,
                        ItemName = it.Name, Hovered = onRewardHover, IsHitTestVisible = true
                    });

                if (it.Count > 1)
                    target.Add(
                        new UILabel
                        {
                            X = ix, Y = y + ih - lineH + 2, Width = iw - 1, Height = lineH,
                            CustomFontSize = Scaled(BODY_FONT - 2), ForegroundColor = Color.White, Text = $"x{it.Count}",
                            HorizontalAlignment = HorizontalAlignment.Right, ShadowStyle = ShadowStyle.BottomRight, IsHitTestVisible = false
                        });

                rowMaxH = Math.Max(rowMaxH, ih);
                ix += iw + 8;
            }

            y += rowMaxH + 10;
        }

        //--- Row 3: legend marks (icon + title) ---
        foreach (var m in marks)
        {
            var icon = MarkIcon(ui, m.Icon);
            var iw = icon is null ? 0 : IconTexW(icon) * ICON_SCALE;
            var ih = icon is null ? 0 : IconTexH(icon) * ICON_SCALE;
            var rowH = Math.Max(ih, lineH);

            if (icon is not null)
                target.Add(new RewardIcon { Icon = icon, X = x, Y = y + ((rowH - ih) / 2), Width = iw, Height = ih });

            Lbl(m.Title, x + iw + 8, y + ((rowH - lineH) / 2), HeaderColor);
            y += rowH + 6;
        }
    }

    private static Texture2D? MarkIcon(UiRenderer ui, byte icon)
    {
        try
        {
            return ui.GetEpfTexture("legends.epf", icon);
        } catch
        {
            return null;
        }
    }

    public static int IconTexW(Texture2D t) => t is CachedTexture2D { AtlasRegion: { } r } ? r.SourceRect.Width : t.Width;
    public static int IconTexH(Texture2D t) => t is CachedTexture2D { AtlasRegion: { } r } ? r.SourceRect.Height : t.Height;

    //a clip-aware scaled reward icon that reports hover (for the item tooltip)
    public sealed class RewardIcon : UIElement
    {
        public Texture2D? Icon { get; init; }
        public string? ItemName { get; init; }
        public Action<string?>? Hovered { get; init; }

        public override void OnMouseEnter()
        {
            if (!string.IsNullOrEmpty(ItemName))
                Hovered?.Invoke(ItemName);
        }

        public override void OnMouseLeave()
        {
            if (!string.IsNullOrEmpty(ItemName))
                Hovered?.Invoke(null);
        }

        public override void Draw(SpriteBatchEx spriteBatch)
        {
            base.Draw(spriteBatch); //updates ClipRect

            if (Icon is null)
                return;

            Texture2D atlas;
            Rectangle src;

            if (Icon is CachedTexture2D { AtlasRegion: { } r })
            {
                atlas = r.Atlas;
                src = r.SourceRect;
            } else
            {
                atlas = Icon;
                src = new Rectangle(0, 0, Icon.Width, Icon.Height);
            }

            var dest = new Rectangle(ScreenX, ScreenY, Width, Height);
            var visible = Rectangle.Intersect(dest, ClipRect);

            if (visible.IsEmpty || (dest.Width <= 0) || (dest.Height <= 0))
                return;

            var sx = src.X + ((visible.X - dest.X) * src.Width / dest.Width);
            var sy = src.Y + ((visible.Y - dest.Y) * src.Height / dest.Height);
            var sw = visible.Width * src.Width / dest.Width;
            var sh = visible.Height * src.Height / dest.Height;
            spriteBatch.Draw(atlas, visible, new Rectangle(sx, sy, sw, sh), Color.White);
        }
    }
}
