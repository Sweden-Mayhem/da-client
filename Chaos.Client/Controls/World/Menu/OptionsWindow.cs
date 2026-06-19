#region
using Chaos.Client.Controls.Components;
using Chaos.Client.Controls.Generic;
using Chaos.Client.Definitions;
using Chaos.Client.Rendering;
using Chaos.Client.Systems;
using Microsoft.Xna.Framework;
#endregion

namespace Chaos.Client.Controls.World.Menu;

public sealed class OptionsWindow : DraggableWindow
{
    //wide enough that the TrueType (Cinzel) slider labels (e.g. "Chat window fade after") fit without clipping,
    //plus DEFAULT_WIDTH extra on the right for the scrollbar
    private const int W = 452 + ScrollBarControl.DEFAULT_WIDTH;
    // max window height, matched to the Controls window so the long option list shows more rows per screen.
    // ChromeHeight for wood (non-flush) = FRAME + TITLE_H + FRAME = 38
    private const int H_MAX = 544;

    private const int OUTER_PAD = 2;
    private const int PAD = 8;
    private const int LABEL_W = 200;
    private const int CTL_X = 210;
    private const int CTL_W = 138;
    private const int VAL_X = 350;
    private const int TITLE_ROW = 26;
    private const int SLIDER_ROW = 26;
    private const int CHECK_ROW = 26;
    private const int SPACING_ROW = 8;
    private const int CONTROL_INDENT = 12;

    private static readonly Color TitleColor = new(196, 168, 110);
    private static readonly int TitleFontSize = 14;
    private static readonly Color LabelColor = new(200, 198, 190);
    private static readonly Color ValueColor = new(196, 168, 110);

    //rows go into RowsHost, Viewport clips the visible area, scrollbar sits to the right
    private readonly UIPanel Viewport;
    private readonly UIPanel RowsHost;
    private readonly ScrollBarControl ScrollBar;
    private int ScrollOffset;
    private int NextY;
    private int RowW;  //usable row width (Content.Width minus scrollbar)

    public OptionsWindow()
        : base("Options", W, H_MAX, useWoodFrame: true)
    {
        X = 90;
        Y = 90;

        RowW = Content.Width - 2 * OUTER_PAD - ScrollBarControl.DEFAULT_WIDTH - PAD;
        var viewportH = Content.Height - 2 * OUTER_PAD;

        Viewport = new UIPanel
        {
            Name = "Viewport",
            X = OUTER_PAD,
            Y = OUTER_PAD,
            Width = RowW,
            Height = viewportH,
            IsPassThrough = true
        };
        Content.AddChild(Viewport);

        RowsHost = new UIPanel
        {
            Name = "Rows",
            X = 0,
            Y = 0,
            Width = RowW,
            Height = 0,
            IsPassThrough = true
        };
        Viewport.AddChild(RowsHost);

        ScrollBar = new ScrollBarControl
        {
            Name = "ScrollBar",
            X = OUTER_PAD + RowW + PAD,
            Y = OUTER_PAD,
            Height = viewportH
        };
        ScrollBar.OnValueChanged += ApplyScroll;
        Content.AddChild(ScrollBar);

        NextY = PAD;
    }

    public void AddTitle(string title)
    {
        var label = new UILabel
        {
            X = PAD - 2,
            Y = NextY,
            Text = title,
            Width = RowW,
            Height = 16,
            CustomFontSize = TitleFontSize,
            ForegroundColor = TitleColor,
        };

        RowsHost.AddChild(label);
        NextY += TITLE_ROW;
    }

    public void AddSpacing()
    {
        NextY += SPACING_ROW;
    }

    /// <summary>Adds a "&lt;label&gt;  [==O==]  &lt;value&gt;" row. <paramref name="format" /> renders the live value text.</summary>
    public void AddSlider(
        string label,
        float min,
        float max,
        float value,
        float step,
        Func<float, string> format,
        Action<float> onChanged)
    {
        RowsHost.AddChild(
            new UILabel
            {
                Text = label,
                X = PAD + CONTROL_INDENT - 2,
                Y = NextY,
                Width = LABEL_W,
                Height = 16,
                CustomFontSize = MenuButton.MenuFontSize,
                ForegroundColor = LabelColor,
                IsHitTestVisible = false
            });

        var valueLabel = new UILabel
        {
            Text = format(value),
            X = VAL_X,
            Y = NextY,
            Width = RowW - VAL_X - PAD - CONTROL_INDENT,
            Height = 16,
            CustomFontSize = MenuButton.MenuFontSize,
            ForegroundColor = ValueColor,
            IsHitTestVisible = false
        };

        var slider = new Slider(CTL_W, min, max, value, step)
        {
            X = CTL_X,
            Y = NextY
        };

        slider.Changed += v =>
        {
            valueLabel.Text = format(v);
            onChanged(v);
        };

        RowsHost.AddChild(slider);
        RowsHost.AddChild(valueLabel);
        NextY += SLIDER_ROW;
    }

    public void AddCheckbox(string label, bool value, Action<bool> onChanged)
    {
        var checkbox = new Checkbox(label, RowW - PAD - CONTROL_INDENT - PAD, value)
        {
            X = PAD + CONTROL_INDENT,
            Y = NextY - 3
        };

        checkbox.Changed += onChanged;
        RowsHost.AddChild(checkbox);
        NextY += CHECK_ROW;
    }

    public void AddChoice(string label, string[] options, int currentIndex, Action<int> onChanged)
    {
        RowsHost.AddChild(
            new UILabel
            {
                Text = label,
                X = PAD + CONTROL_INDENT,
                Y = NextY,
                Width = LABEL_W,
                Height = 16,
                CustomFontSize = MenuButton.MenuFontSize,
                ForegroundColor = LabelColor,
                IsHitTestVisible = false
            });

        var index = Math.Clamp(currentIndex, 0, options.Length - 1);

        var button = new MenuButton(options[index], RowW - CTL_X - PAD, 20)
        {
            X = CTL_X,
            Y = NextY
        };

        button.Clicked = _ =>
        {
            index = (index + 1) % options.Length;
            button.Text = options[index];
            onChanged(index);
        };

        RowsHost.AddChild(button);
        NextY += SLIDER_ROW;
    }

    public void AddButton(string label, Action onClick)
    {
        var button = new MenuButton(label, RowW - 2 * PAD, 22)
        {
            X = PAD,
            Y = NextY + 2
        };

        button.Clicked = _ => onClick();
        RowsHost.AddChild(button);
        NextY += CHECK_ROW + 4;
    }

    public void AddNote(string text)
    {
        RowsHost.AddChild(
            new UILabel
            {
                Text = text,
                X = PAD,
                Y = NextY + 4,
                Width = RowW - 2 * PAD,
                Height = 16,
                CustomFontSize = MenuButton.MenuFontSize,
                ForegroundColor = LabelColor,
                HorizontalAlignment = HorizontalAlignment.Center,
                IsHitTestVisible = false
            });

        NextY += CHECK_ROW;
    }

    public void FitHeight()
    {
        var contentH = NextY;
        RowsHost.Height = contentH;

        //shrink window to content if it fits, otherwise cap at H_MAX
        var neededWindowH = contentH + 2 * OUTER_PAD + ChromeHeight;
        Height = Math.Min(neededWindowH, H_MAX);
        Content.Height = Height - ChromeHeight;

        var viewportH = Content.Height - 2 * OUTER_PAD;
        Viewport.Height = viewportH;
        ScrollBar.Height = viewportH;

        ScrollBar.TotalItems = contentH;
        ScrollBar.VisibleItems = viewportH;
        ScrollBar.MaxValue = Math.Max(0, contentH - viewportH);

        ApplyScroll(0);
    }

    private void ApplyScroll(int value)
    {
        var maxScroll = Math.Max(0, RowsHost.Height - Viewport.Height);
        ScrollOffset = Math.Clamp(value, 0, maxScroll);
        RowsHost.Y = -ScrollOffset;
        ScrollBar.Value = ScrollOffset;
    }

    public override void OnMouseScroll(MouseScrollEvent e)
    {
        ApplyScroll(ScrollOffset - e.Delta * CHECK_ROW);
        e.Handled = true;
    }

    /// <summary>
    ///     Builds the standard settings window, shared by the world screen and the lobby. Each row applies live (where it
    ///     can) and persists to Darkages.cfg. <paramref name="compact" /> (the lobby) keeps only the core pre-game
    ///     settings (Sound, Music, VSync) and shows a "More options in-game" note instead of the keybind button;
    ///     pass false in the world for the full menu.
    /// </summary>
    public static OptionsWindow Create(ChaosGame game, ControlsWindow controls, bool compact = false, Action? onChatRefresh = null)
    {
        var win = new OptionsWindow();

        win.AddTitle("Volume");

        win.AddSlider(
            "Sound Effects", 0f, 10f, ClientSettings.SoundVolume, 1f, v => v <= 0f ? "Off" : $"{v * 10:0}%",
            v =>
            {
                ClientSettings.SoundVolume = (int)v;
                game.SoundSystem.SetSoundVolume((int)v);
                ClientSettings.Save();
            });

        win.AddSlider(
            "Music", 0f, 10f, ClientSettings.MusicVolume, 1f, v => v <= 0f ? "Off" : $"{v * 10:0}%",
            v =>
            {
                ClientSettings.MusicVolume = (int)v;
                game.SoundSystem.SetMusicVolume((int)v);
                ClientSettings.Save();
            });

        //footstep + chat-bubble volumes are in-world cues, so they stay out of the minimal lobby (compact) menu. Kept right
        //after Sound/Music as one volume group. The footstep TUNING (even frames + per-step debug) comes after.
        if (!compact)
        {
            win.AddSlider(
                "Footsteps", 0f, 100f, ClientSettings.FootstepVolume, 5f, v => v <= 0f ? "Off" : $"{v:0}%",
                v =>
                {
                    ClientSettings.FootstepVolume = (int)v;
                    game.SoundSystem.SetFootstepVolume((int)v);
                    ClientSettings.Save();
                });

            win.AddSlider(
                "Chat bubble", 0f, 100f, ClientSettings.ChatBubbleVolume, 5f, v => v <= 0f ? "Off" : $"{v:0}%",
                v =>
                {
                    ClientSettings.ChatBubbleVolume = (int)v;
                    game.SoundSystem.SetChatVolume((int)v);
                    ClientSettings.Save();
                });

            //(the per-step "Step 1..4" debug toggles were removed from the menu; the FootstepStepsEnabled flags + their
            //persistence stay, so they can be re-exposed later if needed.)
        }

        win.AddSpacing();

        win.AddTitle("Video");

        win.AddCheckbox(
            "VSync (smooth, no tearing)", ClientSettings.VSync,
            v =>
            {
                ClientSettings.VSync = v;
                game.SetVSync(v);
                ClientSettings.Save();
            });

        win.AddSpacing();

        win.AddTitle("Interface");

        win.AddSlider(
            "Scale", 0.75f, 4f, ClientSettings.InterfaceScale, 0.25f, v => v < 1 ? "Auto" : $"{v:0.0#}x",
            v =>
            {
                ClientSettings.InterfaceScale = v;
                ClientSettings.Save();
                ClientSettings.NotifyEffectiveWindowScaleChanged();
            });

        if (!compact)
        {
            win.AddSlider(
                "    Windows", 0.25f, 4f, ClientSettings.WindowScale, 0.25f, v => $"{v:0.0#}x",
                v =>
                {
                    ClientSettings.WindowScale = v;
                    ClientSettings.Save();
                    ClientSettings.NotifyEffectiveWindowScaleChanged();
                });

            win.AddSlider(
                "    Hotbar", 0.25f, 4f, ClientSettings.HotbarScale, 0.25f, v => $"{v:0.0#}x",
                v =>
                {
                    ClientSettings.HotbarScale = v; //AnchorHotbars re-reads this every frame in the world
                    ClientSettings.Save();
                });

            win.AddSlider(
                "    Minimap", 0.5f, 4f, ClientSettings.MinimapScale, 0.1f, v => $"{v:0.0#}x",
                v =>
                {
                    ClientSettings.MinimapScale = v; //the minimap re-reads this every frame (scales the circle, not the zoom)
                    ClientSettings.Save();
                });


            win.AddSlider(
                "    Chat", 0.25f, 4f, ClientSettings.ChatFontScale, 0.05f, v => $"{v:0.0#}x",
                v =>
                {
                    ClientSettings.ChatFontScale = v; //ChatWindow re-reads this each frame and re-lays out when it changes
                    ClientSettings.Save();
                });

            win.AddSlider(
                "    Bubbles", 0.25f, 4f, ClientSettings.BubbleFontScale, 0.05f, v => $"{v:0.0#}x",
                v =>
                {
                    ClientSettings.BubbleFontScale = v; //read when a bubble is created
                    ClientSettings.Save();
                });

            win.AddSlider(
                "    Names", 0.25f, 4f, ClientSettings.NameFontScale, 0.05f, v => $"{v:0.0#}x",
                v =>
                {
                    ClientSettings.NameFontScale = v; //read live each frame when over-head name tags are drawn
                    ClientSettings.Save();
                });

            win.AddSlider(
                "    Tooltips", 0.2f, 4f, ClientSettings.TooltipScale, 0.1f, v => $"{v:0.0#}x",
                v =>
                {
                    ClientSettings.TooltipScale = v;
                    ClientSettings.Save();
                });

            win.AddSlider(
                "Chat line fade after", 0f, 60f, ClientSettings.ChatFadeDelaySeconds, 5f, v => v <= 0f ? "Off" : $"{v:0}s",
                v =>
                {
                    ClientSettings.ChatFadeDelaySeconds = (int)v;
                    ClientSettings.Save();
                });

            win.AddSlider(
                "Chat window fade after", 0f, 30f, ClientSettings.ChatWindowFadeSeconds, 1f, v => v <= 0f ? "Off" : $"{v:0}s",
                v =>
                {
                    ClientSettings.ChatWindowFadeSeconds = (int)v;
                    ClientSettings.Save();
                });

            win.AddSlider(
                "Bubble fade after", 0f, 30f, ClientSettings.BubbleFadeSeconds, 1f, v => v <= 0f ? "Off" : $"{v:0}s",
                v =>
                {
                    ClientSettings.BubbleFadeSeconds = (int)v;
                    ClientSettings.Save();
                });

            win.AddCheckbox(
                "Modern controls", ClientSettings.ModernControls,
                v =>
                {
                    ClientSettings.ModernControls = v;
                    ClientSettings.Save();
                });

            win.AddCheckbox(
                "Minimap (corner)", ClientSettings.ShowMinimap,
                v =>
                {
                    ClientSettings.ShowMinimap = v;
                    ClientSettings.Save();
                });

            win.AddCheckbox(
                "Allow dragging minimap", ClientSettings.AllowDragMinimap,
                v =>
                {
                    ClientSettings.AllowDragMinimap = v;
                    ClientSettings.Save();
                });

            win.AddCheckbox(
                "Allow dragging menu button", ClientSettings.AllowDragMenuButton,
                v =>
                {
                    ClientSettings.AllowDragMenuButton = v;
                    ClientSettings.Save();
                });

            win.AddCheckbox(
                "Show NPC dialog in chat", ClientSettings.RecordNpcChat,
                v =>
                {
                    ClientSettings.RecordNpcChat = v;
                    ClientSettings.Save();
                });

            win.AddCheckbox(
                "Show chat timestamps", ClientSettings.ShowChatTimestamp,
                v =>
                {
                    ClientSettings.ShowChatTimestamp = v;
                    ClientSettings.Save();
                    onChatRefresh?.Invoke(); //re-wrap all stored lines so timestamps appear/disappear immediately
                });

            win.AddSlider(
                "Tooltip opacity", 0.25f, 1f, ClientSettings.TooltipAlpha, 0.05f, v => $"{v * 100f:0}%",
                v =>
                {
                    ClientSettings.TooltipAlpha = v;
                    ClientSettings.Save();
                });

            win.AddSlider(
                "Tooltip delay", 0f, 1f, ClientSettings.TooltipDelaySeconds, 0.05f, v => v <= 0f ? "Instant" : $"{v * 1000f:0}ms",
                v =>
                {
                    ClientSettings.TooltipDelaySeconds = v;
                    ClientSettings.Save();
                });
        }

        win.AddSpacing();

        if (!compact)
        {
            win.AddTitle("Game Settings");

            win.AddCheckbox(
                "Flip walk / interact click", ClientSettings.FlipWalkInteract,
                v =>
                {
                    ClientSettings.FlipWalkInteract = v;
                    ClientSettings.Save();
                });

            win.AddCheckbox(
                "Flip mouse target buttons", ClientSettings.FlipMouseTargetButtons,
                v =>
                {
                    ClientSettings.FlipMouseTargetButtons = v;
                    ClientSettings.Save();
                });

            win.AddCheckbox(
                "Click players for profile", ClientSettings.EnableProfileClick,
                v =>
                {
                    ClientSettings.EnableProfileClick = v;
                    ClientSettings.Save();
                });

            win.AddCheckbox(
                "Focus speaker", ClientSettings.FocusSpeaker,
                v =>
                {
                    ClientSettings.FocusSpeaker = v;
                    ClientSettings.Save();
                });

            win.AddCheckbox(
                "Show over-head group", ClientSettings.UseGroupWindow,
                v =>
                {
                    ClientSettings.UseGroupWindow = v;
                    ClientSettings.Save();
                });

            win.AddCheckbox(
                "Spell target line", ClientSettings.SpellTargetLine,
                v =>
                {
                    ClientSettings.SpellTargetLine = v;
                    ClientSettings.Save();
                });

            win.AddSpacing();

            win.AddTitle("Effects");

            win.AddCheckbox(
                "Smooth scrolling", ClientSettings.ScrollLevel > 0,
                v =>
                {
                    ClientSettings.ScrollLevel = v ? 1 : 0;
                    ClientSettings.Save();
                });

            win.AddCheckbox(
                "Smooth creature movement", ClientSettings.SmoothCreatureMovement,
                v =>
                {
                    ClientSettings.SmoothCreatureMovement = v;
                    ClientSettings.Save();
                });
            win.AddSlider(
                "Camera shake", 0f, 100f, ClientSettings.CameraShake, 5f, v => v <= 0f ? "Off" : $"{v:0}%",
                v =>
                {
                    ClientSettings.CameraShake = v;
                    ClientSettings.Save();
                });

            win.AddSlider(
                "Camera effects", 0f, 100f, ClientSettings.CameraEffects, 5f, v => v <= 0f ? "Off" : $"{v:0}%",
                v =>
                {
                    ClientSettings.CameraEffects = v;
                    ClientSettings.Save();
                });

            win.AddCheckbox(
                "Sound on whisper", ClientSettings.WhisperSound,
                v =>
                {
                    ClientSettings.WhisperSound = v;
                    ClientSettings.Save();
                });

            win.AddCheckbox(
                "Alternative map fade", ClientSettings.AlternativeMapFade,
                v =>
                {
                    ClientSettings.AlternativeMapFade = v;
                    ClientSettings.Save();
                });

            win.AddSlider(
                "Behind-walls opacity", 0f, 0.85f, ClientSettings.SilhouetteAlpha, 0.05f, v => v <= 0f ? "Off" : $"{v * 100f:0}%",
                v =>
                {
                    ClientSettings.SilhouetteAlpha = v;
                    SilhouetteRenderer.SilhouetteAlpha = v;
                    ClientSettings.Save();
                });

            win.AddSpacing();
        }

        //the lobby (compact) hides the keybinding editor; a note points the player at the full set in-game
        if (compact)
            win.AddNote("More options in-game");
        else
            win.AddButton("Controls...", controls.Toggle);

        win.FitHeight();

        return win;
    }
}
