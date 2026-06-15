#region
using System;
using Chaos.Client.Controls.Components;
using Chaos.Client.Definitions;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
#endregion

namespace Chaos.Client.Controls.World.Menu;

/// <summary>
///     Top-left "Menu" button with a dropdown that opens menu windows. Lives as a full-screen
///     pass-through layer so its dropdown is never clipped by a small parent; only the button and its
///     items take clicks, everything else passes through to the world.
/// </summary>
public sealed class MenuBar : UIPanel
{
    private const int ITEM_H = 22;
    private const int BTN_W = 76;
    private const int DROP_W = 150;

    private readonly UIPanel Dropdown;
    private readonly MenuItem Button;
    private bool DropOpen;

    public MenuBar()
    {
        Name = "MenuBar";
        X = 0;
        Y = 0;
        Width = ChaosGame.UiWidth;
        Height = ChaosGame.UiHeight;
        IsPassThrough = true;

        Button = new MenuItem("Menu", BTN_W, ITEM_H, header: true)
        {
            X = 2,
            Y = 2
        };
        Button.Activated += ToggleDropdown;
        AddChild(Button);

        Dropdown = new UIPanel
        {
            Name = "Dropdown",
            X = 2,
            Y = 2 + ITEM_H,
            Width = DROP_W,
            Height = 0,
            Visible = false
        };
        AddChild(Dropdown);
    }

    /// <summary>Add a dropdown entry that runs <paramref name="onPick" /> when chosen, with an optional static hover tooltip.
    ///     Returns the created item so the caller can drive its attention marker (e.g. unspent stat points, unread mail).</summary>
    public MenuItem AddEntry(string label, Action onPick, string? tooltip = null) => AddEntry(label, onPick, item => item.Tooltip = tooltip);

    /// <summary>Add a dropdown entry whose tooltip is evaluated live each time it shows, so it can reflect the action's
    ///     CURRENT (rebindable) hotkey instead of a string baked in at construction.</summary>
    public MenuItem AddEntry(string label, Action onPick, Func<string?> tooltipProvider) => AddEntry(label, onPick, item => item.TooltipProvider = tooltipProvider);

    private MenuItem AddEntry(string label, Action onPick, Action<MenuItem> configureTooltip)
    {
        var index = Dropdown.Children.Count;

        var item = new MenuItem(label, DROP_W, ITEM_H)
        {
            X = 0,
            Y = index * ITEM_H
        };
        configureTooltip(item);

        item.Activated += () =>
        {
            onPick();
            CloseDropdown();
        };

        Dropdown.AddChild(item);
        Dropdown.Height = (index + 1) * ITEM_H;

        return item;
    }

    public override void Update(GameTime gameTime)
    {
        //full-window pass-through layer so the dropdown is never clipped
        Width = ChaosGame.UiWidth;
        Height = ChaosGame.UiHeight;

        //if any entry is flagged for attention, the closed "Menu" button pulses a marker to draw the eye to it; once the
        //dropdown is open the per-entry "!" markers are visible instead, so the button marker hides.
        var anyMarker = false;

        foreach (var child in Dropdown.Children)
            if (child is MenuItem { ShowMarker: true })
            {
                anyMarker = true;

                break;
            }

        Button.PulseMarker = true;
        Button.ShowMarker = anyMarker && !DropOpen;

        base.Update(gameTime);
    }

    private void ToggleDropdown()
    {
        DropOpen = !DropOpen;
        Dropdown.Visible = DropOpen;
    }

    private void CloseDropdown()
    {
        DropOpen = false;
        Dropdown.Visible = false;
    }
}

/// <summary>A clickable text row used for the Menu button and its dropdown entries.</summary>
public sealed class MenuItem : UIPanel
{
    //radians/sec of the attention pulse (~0.8s period), shared by the marker glow and the button outline
    private const float PULSE_SPEED = 7.85f;

    private readonly Color NormalColor;
    private readonly Color HoverColor;

    //the "!" attention marker (created lazily the first time it is shown), drawn at the right edge of the row
    private UILabel? Marker;
    private float PulseTime;

    public event Action? Activated;

    /// <summary>Colour of the attention marker (and the pulsing outline on the menu button). Amber by default.</summary>
    public Color MarkerColor { get; set; } = new(255, 170, 40);

    /// <summary>When true the row shows a coloured "!" at its right edge (and, if <see cref="PulseMarker" />, a pulsing
    ///     outline). Used to flag e.g. unspent stat points or unread mail.</summary>
    public bool ShowMarker
    {
        get;

        set
        {
            if (field == value)
                return;

            field = value;

            if (value && (Marker is null))
                CreateMarker();

            if (Marker is not null)
                Marker.Visible = value;
        }
    }

    /// <summary>When true the marker glow pulses and a coloured outline pulses around the whole row (used on the closed
    ///     menu button to draw the eye). The dropdown entries leave it false, showing a steady "!".</summary>
    public bool PulseMarker { get; set; }

    public MenuItem(string text, int width, int height, bool header = false)
    {
        Width = width;
        Height = height;
        NormalColor = (header ? new Color(27, 23, 16) : new Color(20, 18, 13)) * 0.97f;
        HoverColor = new Color(48, 40, 26) * 0.98f;
        BackgroundColor = NormalColor;
        BorderColor = new Color(88, 72, 46);

        AddChild(
            new UILabel
            {
                Text = text,
                X = 8,
                Y = 0,
                Width = width - 12,
                Height = height,
                CustomFontSize = 16, //Menu button and its dropdown entries use the optional UI font (Cinzel)
                ForegroundColor = new Color(192, 176, 138),
                IsHitTestVisible = false
            });
    }

    private void CreateMarker()
    {
        Marker = new UILabel
        {
            Text = "!",
            X = Width - 16,
            Y = 0,
            Width = 13,
            Height = Height,
            CustomFontSize = 16,
            ForegroundColor = MarkerColor,
            HorizontalAlignment = HorizontalAlignment.Center,
            IsHitTestVisible = false
        };

        AddChild(Marker);
    }

    public override void OnMouseEnter() => BackgroundColor = HoverColor;

    public override void OnMouseLeave() => BackgroundColor = NormalColor;

    public override void OnClick(ClickEvent e)
    {
        Activated?.Invoke();
        e.Handled = true;
    }

    public override void Update(GameTime gameTime)
    {
        base.Update(gameTime);

        if (Marker is not { Visible: true })
            return;

        if (PulseMarker)
        {
            PulseTime += (float)gameTime.ElapsedGameTime.TotalSeconds;
            Marker.ForegroundColor = MarkerColor * Pulse();
        } else
            Marker.ForegroundColor = MarkerColor;
    }

    public override void Draw(SpriteBatch spriteBatch)
    {
        base.Draw(spriteBatch);

        //a pulsing coloured outline around the (closed) menu button, on top of its normal chrome
        if (Marker is { Visible: true } && PulseMarker)
            DrawBorder(spriteBatch, new Rectangle(ScreenX, ScreenY, Width, Height), MarkerColor * Pulse());
    }

    //0.55..1.0 sine glow
    private float Pulse() => 0.55f + 0.45f * (float)Math.Sin(PulseTime * PULSE_SPEED);
}
