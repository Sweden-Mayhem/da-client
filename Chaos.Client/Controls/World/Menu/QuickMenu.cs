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
    public const int ITEM_H = 32;
    public const int ITEM_W = 130;
    public const int GROW_LEFT = 10;
    public const int GROW_RIGHT = 0;
    public const int ITEM_SPACING = 8;

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
        private readonly Color NormalColor;
        private readonly Color HoverColor;
    
        private readonly UILabel Label;
    
        public event Action? Activated;

        public HorizontalAlignment Alignment
        {
            get => Label.HorizontalAlignment;
            set
            {
                Label.HorizontalAlignment = value;
            }
        }
    
        /// <summary>Show a notification marker on this item.</summary>
        public bool ShowNotfication { get; set; }
    
        public Item(string text, int width, int height, HorizontalAlignment horizontalAlignment)
        {
            Width = width;
            Height = height;
            NormalColor = new Color(20, 18, 13) * 0.97f;
            HoverColor = new Color(48, 40, 26) * 0.98f;
            BackgroundColor = NormalColor;
            BorderColor = new Color(88, 72, 46);

            Label = new UILabel
            {
                Text = text,
                X = 8,
                Y = 0,
                Width = width - 16,
                Height = height,
                CustomFontSize = 16,
                ForegroundColor = new Color(192, 176, 138),
                HorizontalAlignment = horizontalAlignment,
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
                spriteBatch.Draw(NotificationCircle, new Vector2(Label.ScreenX + Label.Width - 8, Label.ScreenY - 2), Color.White);
        }

        public override void OnMouseEnter() {
            X = 0;
            Width = GROW_LEFT + ITEM_W + GROW_RIGHT;
            Label.X = GROW_LEFT/2 + 8;
            BackgroundColor = HoverColor;
        }
    
        public override void OnMouseLeave() {
            X = GROW_LEFT;
            Width = ITEM_W;
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
        Width = GROW_LEFT + ITEM_W + GROW_RIGHT;
        Height = 0;
        IsPassThrough = true;
        ZIndex = 501; //above hotbars and HP/MP orbs (0), below all draggable windows (WindowOrder base 1000+)

        NotificationCircle ??= ChaosGame.LoadTextureResource("notification_circle.png");
    }

    /// <summary>Add a dropdown entry that runs <paramref name="onPick" /> when chosen, with an optional static hover tooltip.
    ///     Returns the created item so the caller can drive its attention marker (e.g. unspent stat points, unread mail).</summary>
    public Item AddEntry(string label, Action onPick, string? tooltip = null) => AddEntry(label, onPick, item => item.Tooltip = tooltip);

    /// <summary>Add a dropdown entry whose tooltip is evaluated live each time it shows, so it can reflect the action's
    ///     CURRENT (rebindable) hotkey instead of a string baked in at construction.</summary>
    public Item AddEntry(string label, Action onPick, Func<string?> tooltipProvider) => AddEntry(label, onPick, item => item.TooltipProvider = tooltipProvider);

    private Item AddEntry(string label, Action onPick, Action<Item> configureTooltip)
    {
        var index = Children.Count;

        var item = new Item(label, ITEM_W, ITEM_H, Alignment)
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
