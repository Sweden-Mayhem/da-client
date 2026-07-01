#region
using System;
using Chaos.Client.Controls.Components;
using Chaos.Client.Definitions;
using Chaos.Client.Systems;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
#endregion

namespace Chaos.Client.Controls.World.Menu;

/// <summary>
///     Top-left "Menu" button with a dropdown that opens menu windows. Lives as a full-screen
///     pass-through layer so its dropdown is never clipped by a small parent; only the button and its
///     items take clicks, everything else passes through to the world.
///     The button is draggable: left-click-and-drag it to move it anywhere on screen.
/// </summary>
public sealed class MenuBar : UIPanel
{
    private const int ITEM_H = 22;
    private const int BTN_W = 76;
    private const int DROP_W = 150;
    private const int DRAG_THRESHOLD_X = 16;
    private const int DRAG_THRESHOLD_Y = 8;

    //minimap geometry (matches MinimapControl constants) used for default placement and snap targets
    private const int MINIMAP_BASE_DIAM = 176;
    private const int MINIMAP_MARGIN = 12;
    private const int MINIMAP_GAP = 4;
    private const int SNAP_RADIUS = 20; //pixels; squared distance used internally

    private readonly UIPanel Dropdown;
    private readonly MenuItem Button;
    private bool DropOpen;

    //drag state for the button
    private bool Dragging;
    private bool PressArmed;
    private int PressX, PressY, DragOffX, DragOffY;

    private bool OffsetLoaded;
    private bool HasOffset;
    //OffsetX: center-relative (X - screenW/2). OffsetY: anchor-relative, positive = distance from top,
    //negative = distance from bottom (Y - screenH), so the button stays near whichever edge it lives on.
    private int OffsetX, OffsetY;


    public MenuBar()
    {
        Name = "MenuBar";
        X = 0;
        Y = 0;
        Width = ChaosGame.UiWidth;
        Height = ChaosGame.UiHeight;
        IsPassThrough = true;
        ZIndex = 501; //above hotbars and HP/MP orbs (0), below all draggable windows (WindowOrder base 1000+)

        Button = new MenuItem("Menu", BTN_W, ITEM_H, header: true);
        Button.Activated += ToggleDropdown;
        Button.Pressed += OnButtonPressed;
        AddChild(Button);

        Dropdown = new UIPanel
        {
            Name = "Dropdown",
            Width = DROP_W,
            Height = 0,
            Visible = false
        };
        AddChild(Dropdown);
    }

    private void OnButtonPressed(int screenX, int screenY)
    {
        PressArmed = true;
        PressX = screenX;
        PressY = screenY;
        DragOffX = screenX - Button.ScreenX; //offset from cursor to button's current on-screen top-left
        DragOffY = screenY - Button.ScreenY;
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

        //first update: load the saved independent offset (only relevant in detached mode, AttachSide == -1)
        if (!OffsetLoaded)
        {
            OffsetLoaded = true;
            var savedX = ClientSettings.MenuButtonOffsetX;
            var savedY = ClientSettings.MenuButtonOffsetY;

            if ((savedX != int.MinValue) && (savedY != int.MinValue))
            {
                HasOffset = true;
                OffsetX = savedX;
                OffsetY = savedY;
            }
            //no else-branch: default AttachSide=0 means "attached below minimap" -position is always
            //computed live from the minimap geometry, so no saved offset is needed on first run.
        }

        var cx = ChaosGame.UiWidth / 2;
        var cy = ChaosGame.UiHeight / 2;

        //non-drag: position from attachment side (follows the minimap) or an independent center-relative offset.
        //attachment is only active when the minimap is visible -if it's been hidden, fall through to independent.
        if (!Dragging)
        {
            if ((ClientSettings.MenuButtonAttachSide >= 0) && ClientSettings.ShowMinimap)
            {
                var (ax, ay) = GetSnapPosition(ClientSettings.MenuButtonAttachSide);
                Button.X = Math.Clamp(ax, 0, Math.Max(0, ChaosGame.UiWidth - BTN_W));
                Button.Y = Math.Clamp(ay, 0, Math.Max(0, ChaosGame.UiHeight - ITEM_H));
            }
            else
            {
                Button.X = HasOffset ? cx + OffsetX : 48;
                Button.Y = HasOffset ? (OffsetY >= 0 ? OffsetY : ChaosGame.UiHeight + OffsetY) : 32;
            }
        }

        //drag: only when the left-press STARTED on the button, so world clicks can't teleport it
        if (PressArmed && InputBuffer.IsLeftButtonHeld && ClientSettings.AllowDragMenuButton)
        {
            if (!Dragging && ((Math.Abs(InputBuffer.MouseX - PressX) >= DRAG_THRESHOLD_X) || (Math.Abs(InputBuffer.MouseY - PressY) >= DRAG_THRESHOLD_Y)))
            {
                Dragging = true;
                CloseDropdown();
                //re-anchor to the current cursor so the button starts moving from here, not from the click point
                DragOffX = InputBuffer.MouseX - Button.ScreenX;
                DragOffY = InputBuffer.MouseY - Button.ScreenY;
            }

            if (Dragging)
            {
                Button.X = InputBuffer.MouseX - DragOffX;
                Button.Y = InputBuffer.MouseY - DragOffY;
                TrySnapButtonPosition(); //live preview: moves Button.X/Y while dragging near a snap candidate
            }
        }
        else if (!InputBuffer.IsLeftButtonHeld)
        {
            if (Dragging)
            {
                //on release: check whether to attach or detach
                var snappedSide = TrySnapButtonPosition();

                if (snappedSide >= 0)
                {
                    //snapped to a minimap side - enter (or stay) attached
                    ClientSettings.MenuButtonAttachSide = snappedSide;
                }
                else
                {
                    //dropped in free space -detach and store an independent center-relative offset
                    ClientSettings.MenuButtonAttachSide = -1;
                    HasOffset = true;
                    OffsetX = Button.X - cx;
                    OffsetY = Button.Y + ITEM_H / 2 < ChaosGame.UiHeight / 2 ? Button.Y : Button.Y - ChaosGame.UiHeight;
                    ClientSettings.MenuButtonOffsetX = OffsetX;
                    ClientSettings.MenuButtonOffsetY = OffsetY;
                }

                ClientSettings.Save();
            }

            Dragging = false;
            PressArmed = false;
        }

        //clamp the visual position to screen bounds
        Button.X = Math.Clamp(Button.X, 0, Math.Max(0, ChaosGame.UiWidth - BTN_W));
        Button.Y = Math.Clamp(Button.Y, 0, Math.Max(0, ChaosGame.UiHeight - ITEM_H));

        PlaceDropdown();

        var anyMarker = false;

        foreach (var child in Dropdown.Children)
            if (child is MenuItem { ShowMarker: true })
            {
                anyMarker = true;

                break;
            }

        Button.PulseMarker = anyMarker && !DropOpen;

        base.Update(gameTime);
    }

    private void PlaceDropdown()
    {
        var h = Dropdown.Height;

        if (h <= 0)
            return;

        var screenW = ChaosGame.UiWidth;
        var screenH = ChaosGame.UiHeight;

        //X: button on the left half of the screen → extend right; right half → extend left.
        //This naturally keeps the dropdown away from whatever corner the button lives in.
        int x;

        if (Button.X + BTN_W / 2 < screenW / 2)
            x = Button.X;                   //left side: left-align dropdown with button
        else
            x = Button.X + BTN_W - DROP_W;  //right side: right-align dropdown with button's right edge

        x = Math.Clamp(x, 0, Math.Max(0, screenW - DROP_W));

        //Y: prefer below the button; go above only if it clips the bottom edge
        var belowY = Button.Y + ITEM_H;
        var y = belowY + h <= screenH ? belowY : Math.Clamp(Button.Y - h, 0, Math.Max(0, screenH - h));

        Dropdown.X = x;
        Dropdown.Y = y;
    }

    //returns the minimap's top-left position and diameter from saved settings (or hardcoded default).
    private (int X, int Y, int Diam) GetMinimapGeometry()
    {
        var scale = Math.Clamp(ClientSettings.EffectiveMinimapScale, 0.1f, 4f);
        var diam = Math.Max(32, (int)MathF.Round(MINIMAP_BASE_DIAM * scale));
        var mmOffX = ClientSettings.MinimapOffsetX;
        var mmOffY = ClientSettings.MinimapOffsetY;

        if ((mmOffX != int.MinValue) && (mmOffY != int.MinValue))
        {
            var mmY = mmOffY >= 0 ? mmOffY : ChaosGame.UiHeight + mmOffY; //decode anchor-relative Y
            return (ChaosGame.UiWidth / 2 + mmOffX, mmY, diam);
        }

        return (ChaosGame.UiWidth - diam - MINIMAP_MARGIN, MINIMAP_MARGIN, diam);
    }

    //returns the absolute button position for the given attachment side (0=below,1=above,2=left,3=right).
    private (int X, int Y) GetSnapPosition(int side)
    {
        var (mmX, mmY, diam) = GetMinimapGeometry();
        var mmCX = mmX + diam / 2;
        var mmCY = mmY + diam / 2;

        return side switch
        {
            0 => (mmCX - BTN_W / 2,            mmY + diam + MINIMAP_GAP),   //below
            1 => (mmCX - BTN_W / 2,            mmY - ITEM_H - MINIMAP_GAP), //above
            2 => (mmX - BTN_W - MINIMAP_GAP,   mmCY - ITEM_H / 2),          //left
            3 => (mmX + diam + MINIMAP_GAP,    mmCY - ITEM_H / 2),          //right
            _ => (Button.X, Button.Y)
        };
    }

    //checks if Button.X/Y is within SNAP_RADIUS of any minimap snap candidate.
    //moves Button.X/Y to the closest candidate if within range and returns its side index (0-3), else -1.
    //snap is suppressed when the minimap is not shown -you can't snap to something you can't see.
    private int TrySnapButtonPosition()
    {
        if (!ClientSettings.ShowMinimap)
            return -1;

        var (mmX, mmY, diam) = GetMinimapGeometry();
        var mmCX = mmX + diam / 2;
        var mmCY = mmY + diam / 2;

        Span<(int X, int Y)> candidates =
        [
            (mmCX - BTN_W / 2,            mmY + diam + MINIMAP_GAP),   //0: below
            (mmCX - BTN_W / 2,            mmY - ITEM_H - MINIMAP_GAP), //1: above
            (mmX - BTN_W - MINIMAP_GAP,   mmCY - ITEM_H / 2),          //2: left
            (mmX + diam + MINIMAP_GAP,    mmCY - ITEM_H / 2),          //3: right
        ];

        var bestDistSq = SNAP_RADIUS * SNAP_RADIUS;
        var bestSide = -1;
        var snapX = Button.X;
        var snapY = Button.Y;

        for (var i = 0; i < candidates.Length; i++)
        {
            var (cx, cy) = candidates[i];
            var dx = Button.X - cx;
            var dy = Button.Y - cy;
            var distSq = dx * dx + dy * dy;

            if (distSq < bestDistSq)
            {
                bestDistSq = distSq;
                bestSide = i;
                snapX = cx;
                snapY = cy;
            }
        }

        if (bestSide < 0)
            return -1;

        Button.X = Math.Clamp(snapX, 0, Math.Max(0, ChaosGame.UiWidth - BTN_W));
        Button.Y = Math.Clamp(snapY, 0, Math.Max(0, ChaosGame.UiHeight - ITEM_H));

        return bestSide;
    }

    private void ToggleDropdown()
    {
        DropOpen = !DropOpen;
        Dropdown.Visible = DropOpen;

        //move the menu on top of other windows
        if (DropOpen)
            ZIndex = WindowOrder.Next();
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
    private readonly bool IsHeader;

    //the "!" attention marker (created lazily the first time it is shown), drawn at the right edge of the row
    private UILabel? Marker;
    private float PulseTime;

    public event Action? Activated;

    /// <summary>Fires on left mouse-down with the screen position. Used by the header button's parent (MenuBar) to arm drag tracking.</summary>
    public event Action<int, int>? Pressed;

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
        IsHeader = header;
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
                X = header ? 0 : 8,
                Y = 0,
                Width = header ? width : width - 12,
                Height = height,
                CustomFontSize = 16,
                ForegroundColor = new Color(192, 176, 138),
                HorizontalAlignment = header ? HorizontalAlignment.Center : HorizontalAlignment.Left,
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

    public override void OnMouseDown(MouseDownEvent e)
    {
        if (e.Button == MouseButton.Left)
            Pressed?.Invoke(e.ScreenX, e.ScreenY);
    }

    //when the header button is dragged, report a dummy payload so the dispatcher marks the action as a drag and
    //suppresses the click synthesis -otherwise releasing after a drag would toggle the dropdown.
    public override void OnDragStart(DragStartEvent e)
    {
        if (IsHeader && (e.Button == MouseButton.Left))
            e.Payload = this;
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

        if (PulseMarker)
        {
            PulseTime += (float)gameTime.ElapsedGameTime.TotalSeconds;

            if (Marker is { Visible: true })
                Marker.ForegroundColor = MarkerColor * Pulse();
        } else if (Marker is { Visible: true })
            Marker.ForegroundColor = MarkerColor;
    }

    public override void Draw(SpriteBatchEx spriteBatch)
    {
        var layers = DebugSettings.GlowLayers;
        var pulsing = PulseMarker;
        var pulse = pulsing ? Pulse() : 0f;

        if (layers > 0 && IsHeader)
        {
            var pixel = GetPixel();
            var strength = DebugSettings.GlowStrength;

            for (var j = 0; j < layers; j++)
            {
                var dist = layers - j + (int)(4f * pulse);
                var alpha = (0.02f + (j + 1) * 0.035f) * strength;
                var alphaByte = (byte)(alpha * 255f);
                var glow = pulsing
                    ? new Color((byte)(MarkerColor.R * alpha * pulse), (byte)(MarkerColor.G * alpha * pulse), (byte)(MarkerColor.B * alpha * pulse), alphaByte)
                    : Color.Black * alpha;

                spriteBatch.Draw(pixel,
                    new Rectangle(ScreenX - dist, ScreenY - dist, Width + dist * 2, Height + dist * 2),
                    glow);
            }
        }

        if (pulsing)
        {
            var saved = BackgroundColor!.Value;
            var amber = MarkerColor * 0.45f;
            BackgroundColor = new Color(
                (int)(saved.R + (amber.R - saved.R) * pulse),
                (int)(saved.G + (amber.G - saved.G) * pulse),
                (int)(saved.B + (amber.B - saved.B) * pulse));
            base.Draw(spriteBatch);
            BackgroundColor = saved;

            DrawBorder(spriteBatch, new Rectangle(ScreenX, ScreenY, Width, Height), MarkerColor * pulse);
        } else
            base.Draw(spriteBatch);
    }

    //0.55..1.0 sine glow
    private float Pulse() => 0.55f + 0.45f * (float)Math.Sin(PulseTime * PULSE_SPEED);
}
