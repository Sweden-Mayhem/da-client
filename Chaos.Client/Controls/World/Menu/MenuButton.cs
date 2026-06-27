#region
using Chaos.Client.Controls.Components;
using Chaos.Client.Definitions;
using Chaos.Client.Systems;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
#endregion

namespace Chaos.Client.Controls.World.Menu;

/// <summary>
///     A small dark/bronze text button matching the menu/window styling. Used for the Options window's "Controls..."
///     entry and for every cell in the Controls (keybinding) window. <see cref="Action" />/<see cref="Slot" /> let the
///     keybinding rows carry which binding a cell edits; plain buttons ignore them.
/// </summary>
public sealed class MenuButton : UIPanel
{
    //the menu/window UI uses the optional TrueType (Cinzel) font everywhere; menu buttons match it by default
    public const int MenuFontSize = 14;

    private static readonly Color IdleBg = new(34, 30, 24);
    private static readonly Color HoverBg = new(54, 48, 36);
    private static readonly Color EdgeColor = new(88, 72, 46);

    private readonly UILabel Label;

    public Action<MenuButton>? Clicked;
    public Action<MenuButton>? RightClicked;

    //optional payload for keybinding cells (which action + which of the two slots this cell edits)
    public GameAction Action { get; set; }
    public int Slot { get; set; }

    public MenuButton(string text, int width, int height, Color? foreground = null)
    {
        Width = width;
        Height = height;
        BackgroundColor = IdleBg;
        BorderColor = EdgeColor;

        Label = new UILabel
        {
            X = 2,
            Y = 0,
            Width = width - 4,
            Height = height,
            Text = text,
            CustomFontSize = MenuFontSize,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            ForegroundColor = foreground ?? new Color(206, 184, 132),
            IsHitTestVisible = false
        };

        AddChild(Label);
    }

    /// <summary>Pixel size of the label's TrueType font; 0 reverts to the bitmap font. Defaults to <see cref="MenuFontSize" />.</summary>
    public int CustomFontSize
    {
        set => Label.CustomFontSize = value;
    }

    public string Text
    {
        set => Label.Text = value;
    }

    public Color TextColor
    {
        set => Label.ForegroundColor = value;
    }

    public override void Draw(SpriteBatchEx spriteBatch)
    {
        base.Draw(spriteBatch);
    }

    public override void OnMouseEnter() => BackgroundColor = HoverBg;
    public override void OnMouseLeave() => BackgroundColor = IdleBg;

    /// <summary>When true, a left click does not play the UI click sound (e.g. clicking an already-active tab).</summary>
    public bool SuppressClickSound { get; set; }

    public override void OnMouseDown(MouseDownEvent e)
    {
        if (e.Button == MouseButton.Right)
        {
            RightClicked?.Invoke(this);
        } else
        {
            if (!SuppressClickSound)
                SoundSystem.PlayUiClick();

            Clicked?.Invoke(this);
        }

        e.Handled = true;
    }
}
