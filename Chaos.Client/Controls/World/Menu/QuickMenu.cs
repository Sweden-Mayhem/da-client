#region
using System;
using Chaos.Client.Controls.Components;
using Chaos.Client.Definitions;
using Chaos.Client.Systems;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
#endregion

namespace Chaos.Client.Controls.World.Menu;

public sealed class QuickMenu : UIPanel
{
    public const int ITEM_H = 32+16;
    public const int ITEM_TEXT_W = 130;
    public const int ITEM_ICONS_W = 32+16;
    public const int GROW_LEFT = 10;
    public const int GROW_RIGHT = 0;
    public const int ITEM_SPACING = 8;
    public const int ICON_MARGIN = 0;//8;

    public enum DisplayMode {
        text,
        icons
    }

    public int InnerWidth {
        get
        {
            return Mode switch
            {
                DisplayMode.text => ITEM_TEXT_W,
                DisplayMode.icons => ITEM_ICONS_W,
                _ => ITEM_TEXT_W,
            };
        }
    }

    public DisplayMode Mode {
        get;
        set
        {
            field = value;

            Width = GROW_LEFT + InnerWidth + GROW_RIGHT;

            foreach (var child in Children)
            {
                var item = (child as Item)!;
                item.Width = InnerWidth;
                item.Icon?.Visible = value == DisplayMode.icons;
                item.Label.Visible = value == DisplayMode.text;
            }
        }
    } = DisplayMode.text;

    public HorizontalAlignment Alignment {
        get;
        set
        {
            field = value;
            foreach (var child in Children)
            {
                (child as Item)!.Alignment = value;
            }
        }
    }

    /// <summary>A clickable text row used for the Menu button.</summary>
    public sealed class Item : UIPanel
    {
        private readonly QuickMenu Menu;

        private readonly Color NormalColor;
        private readonly Color HoverColor;
    
        public readonly UILabel Label;
        public readonly UIImage? Icon;
    
        public event Action? Activated;

        public HorizontalAlignment Alignment
        {
            get;
            set
            {
                Label.HorizontalAlignment = value;
                Icon?.X = 8 + ICON_MARGIN + (int)MathF.Round((ITEM_ICONS_W - 16 - ICON_MARGIN * 2 - Icon.Width) * (value == HorizontalAlignment.Left ? 0f : value == HorizontalAlignment.Right ? 1f : 0.5f));
                field = value;
            }
        }
    
        /// <summary>Show a notification marker on this item.</summary>
        public bool ShowNotfication { get; set; }
    
        public Item(QuickMenu menu, string text, Texture2D? icon)
        {
            Menu = menu;
            Width = Menu.InnerWidth;
            Height = ITEM_H;
            NormalColor = new Color(20, 18, 13) * 0.97f;
            HoverColor = new Color(48, 40, 26) * 0.98f;
            BackgroundColor = NormalColor;
            BorderColor = new Color(88, 72, 46);

            var color = new Color(192, 176, 138);

            if (icon is not null)
            {
                Icon = new UIImage(icon)
                {
                    Visible = Menu.Mode == DisplayMode.icons,
                    Color = color,
                    IsHitTestVisible = false
                };
                Icon.X = 8 + ICON_MARGIN + (int)MathF.Round((ITEM_ICONS_W - 16 - ICON_MARGIN * 2 - Icon.Width) * (Menu.Alignment == HorizontalAlignment.Left ? 0f : Menu.Alignment == HorizontalAlignment.Right ? 1f : 0.5f));
                Icon.Y = (Height - Icon.Height) / 2;
                AddChild(Icon);
            }

            Label = new UILabel
            {
                Text = text,
                Visible = Menu.Mode == DisplayMode.text,
                X = 8,
                Y = 0,
                Width = ITEM_TEXT_W - 16,
                Height = ITEM_H,
                CustomFontSize = 16,
                ForegroundColor = color,
                HorizontalAlignment = Menu.Alignment,
                IsHitTestVisible = false,
                PaddingLeft = 8,
                PaddingRight = 8
            };
            AddChild(Label);
        }
    
        public override void Draw(SpriteBatchEx spriteBatch)
        {
            base.Draw(spriteBatch);

            if (ShowNotfication && NotificationCircle is not null)
            {
                switch(Menu.Mode)
                {
                    case DisplayMode.text:
                        spriteBatch.Draw(NotificationCircle, new Vector2(Label.ScreenX + Label.Width - 8, Label.ScreenY - 2), Color.White);
                    break;
                    case DisplayMode.icons:
                        if (Icon is not null)
                            spriteBatch.Draw(NotificationCircle, new Vector2(Icon.ScreenX + Icon.Width - 8, Icon.ScreenY - 4), Color.White);
                    break;
                }
            }
        }

        public override void OnMouseEnter() {
            X = 0;
            Width = GROW_LEFT + Menu.InnerWidth + GROW_RIGHT;
            Label.X = GROW_LEFT/2 + 8;
            BackgroundColor = HoverColor;
        }
    
        public override void OnMouseLeave() {
            X = GROW_LEFT;
            Width = Menu.InnerWidth;
            Label.X = 8;
            BackgroundColor = NormalColor;
        }
    
        public override void OnClick(ClickEvent e)
        {
            Activated?.Invoke();
            e.Handled = true;
        }
    }

    public static Texture2D? NotificationCircle;

    public QuickMenu()
    {
        Name = "QuickMenu";
        X = 0;
        Y = 0;
        Width = GROW_LEFT + ITEM_TEXT_W + GROW_RIGHT;
        Height = 0;
        IsPassThrough = true;
        ZIndex = 501; //above hotbars and HP/MP orbs (0), below all draggable windows (WindowOrder base 1000+)

        NotificationCircle ??= ChaosGame.LoadTextureResource("notification_circle.png");
    }

    /// <summary>Add a dropdown entry that runs <paramref name="onPick" /> when chosen, with an optional static hover tooltip.
    ///     Returns the created item so the caller can drive its attention marker (e.g. unspent stat points, unread mail).</summary>
    public Item AddEntry(string label, Texture2D? icon, Action onPick, string? tooltip = null) => AddEntry(label, icon, onPick, item => item.Tooltip = tooltip);

    /// <summary>Add a dropdown entry whose tooltip is evaluated live each time it shows, so it can reflect the action's
    ///     CURRENT (rebindable) hotkey instead of a string baked in at construction.</summary>
    public Item AddEntry(string label, Texture2D? icon, Action onPick, Func<string?> tooltipProvider) => AddEntry(label, icon, onPick, item => item.TooltipProvider = tooltipProvider);

    private Item AddEntry(string label, Texture2D? icon, Action onPick, Action<Item> configureTooltip)
    {
        var index = Children.Count;

        var item = new Item(this, label, icon)
        {
            X = GROW_LEFT,
            Y = Height + (index > 0 ? ITEM_SPACING : 0),
            Alignment = Alignment
        };
        configureTooltip(item);

        item.Activated += onPick;

        AddChild(item);
        Height = item.Y + item.Height;

        return item;
    }
}
