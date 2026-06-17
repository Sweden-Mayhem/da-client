#region
using Chaos.Client.Controls.Components;
using Chaos.Client.Controls.Generic;
using Chaos.Client.Definitions;
using Chaos.Client.Rendering;
using Chaos.Client.Systems;
using Microsoft.Xna.Framework;
#endregion

namespace Chaos.Client.Controls.World.Menu;

/// <summary>
///     A scrollable live tuning panel for render FX, opened by the "/debugOptions" chat command (anyone can use it).
///     Each row drives a real knob (<see cref="DebugSettings" />, <see cref="DarknessRenderer" />,
///     <see cref="SilhouetteRenderer" />, <see cref="ClientSettings" />) and shows the raw value, so good numbers can
///     be dialled in by eye and then hard-coded. The DebugSettings/Darkness knobs are NOT persisted; the
///     ClientSettings rows are (they're the same fields the normal Options window writes).
/// </summary>
public sealed class DebugFxWindow : DraggableWindow
{
    private const int W = 392 + ScrollBarControl.DEFAULT_WIDTH;
    private const int H_MAX = 560;

    private const int PAD = 12;
    private const int LABEL_W = 168;
    private const int CTL_X = 182;
    private const int CTL_W = 120;
    private const int VAL_X = 308;
    private const int ROW = 26;
    private const int HEADER_ROW = 24;

    private static readonly Color LabelColor = new(200, 198, 190);
    private static readonly Color ValueColor = new(196, 168, 110);
    private static readonly Color HeaderColor = new(224, 196, 132);

    private readonly UIPanel Viewport;
    private readonly UIPanel RowsHost;
    private readonly ScrollBarControl ScrollBar;
    private readonly int RowW;
    private int ScrollOffset;
    private int NextY;

    public DebugFxWindow()
        : base("Debug FX", W, H_MAX, useWoodFrame: true)
    {
        X = 70;
        Y = 70;

        RowW = Content.Width - ScrollBarControl.DEFAULT_WIDTH;
        var viewportH = Content.Height - 2 * PAD;

        Viewport = new UIPanel { Name = "Viewport", X = 0, Y = PAD, Width = RowW, Height = viewportH, IsPassThrough = true };
        Content.AddChild(Viewport);

        RowsHost = new UIPanel { Name = "Rows", X = 0, Y = 0, Width = RowW, Height = 0, IsPassThrough = true };
        Viewport.AddChild(RowsHost);

        ScrollBar = new ScrollBarControl { Name = "ScrollBar", X = RowW, Y = PAD, Height = viewportH };
        ScrollBar.OnValueChanged += ApplyScroll;
        Content.AddChild(ScrollBar);

        NextY = 0;

        AddHeader("Bloom (night glow)");
        AddSlider("Bloom dusk max", 0f, 1f, DebugSettings.BloomDuskMax, 0.01f, v => $"{v:0.00}", v => DebugSettings.BloomDuskMax = v);
        AddSlider("Bloom night max", 0f, 2f, DebugSettings.BloomNightMax, 0.05f, v => $"{v:0.00}", v => DebugSettings.BloomNightMax = v);
        AddSlider("Night glow pivot", 0.1f, 1f, DarknessRenderer.NightGlowAlpha, 0.01f, v => $"{v:0.00}", v => DarknessRenderer.NightGlowAlpha = v);
        AddSlider("Bloom tint R", 0f, 255f, DebugSettings.BloomTint.R, 1f, v => $"{v:0}", v => DebugSettings.BloomTint = new Color((int)v, DebugSettings.BloomTint.G, DebugSettings.BloomTint.B));
        AddSlider("Bloom tint G", 0f, 255f, DebugSettings.BloomTint.G, 1f, v => $"{v:0}", v => DebugSettings.BloomTint = new Color(DebugSettings.BloomTint.R, (int)v, DebugSettings.BloomTint.B));
        AddSlider("Bloom tint B", 0f, 255f, DebugSettings.BloomTint.B, 1f, v => $"{v:0}", v => DebugSettings.BloomTint = new Color(DebugSettings.BloomTint.R, DebugSettings.BloomTint.G, (int)v));

        AddHeader("Night vignette");
        AddSlider("Vignette max", 0f, 1f, DebugSettings.VignetteMax, 0.01f, v => $"{v:0.00}", v => DebugSettings.VignetteMax = v);
        AddSlider("Vignette inner", 0f, 0.9f, DebugSettings.VignetteInner, 0.01f, v => $"{v:0.00}", v => DebugSettings.VignetteInner = v);

        AddHeader("Darkness");
        AddSlider("Day/night fade", 0f, 30f, DarknessRenderer.FadeSeconds, 0.5f, v => $"{v:0.0}s", v => DarknessRenderer.FadeSeconds = Math.Max(0.1f, v));

        AddHeader("Night shadow tint (muted + blue outside light)");
        AddSlider("Shadow tint", 0f, 1f, DarknessRenderer.NightShadowStrength, 0.05f, v => $"{v:0.00}", v => DarknessRenderer.NightShadowStrength = v);
        AddSlider("Tint R", 0f, 255f, DarknessRenderer.NightShadowTintColor.R, 1f, v => $"{v:0}", v => DarknessRenderer.NightShadowTintColor = new Color((int)v, DarknessRenderer.NightShadowTintColor.G, DarknessRenderer.NightShadowTintColor.B));
        AddSlider("Tint G", 0f, 255f, DarknessRenderer.NightShadowTintColor.G, 1f, v => $"{v:0}", v => DarknessRenderer.NightShadowTintColor = new Color(DarknessRenderer.NightShadowTintColor.R, (int)v, DarknessRenderer.NightShadowTintColor.B));
        AddSlider("Tint B", 0f, 255f, DarknessRenderer.NightShadowTintColor.B, 1f, v => $"{v:0}", v => DarknessRenderer.NightShadowTintColor = new Color(DarknessRenderer.NightShadowTintColor.R, DarknessRenderer.NightShadowTintColor.G, (int)v));

        AddHeader("Camera FX");
        AddSlider("Damage flash", 0f, 1f, DebugSettings.DamageFlashMax, 0.01f, v => $"{v:0.00}", v => DebugSettings.DamageFlashMax = v);
        AddSlider("Low-HP pulse", 0f, 1f, DebugSettings.LowHpPulseMax, 0.01f, v => $"{v:0.00}", v => DebugSettings.LowHpPulseMax = v);

        AddHeader("Light shadows");
        AddCheckbox("Light shadows", DebugSettings.LightShadows, v => DebugSettings.LightShadows = v);
        AddSlider("Shadow foot px", 0f, 48f, DebugSettings.LightShadowFootPx, 1f, v => $"{v:0}", v => DebugSettings.LightShadowFootPx = (int)v);
        AddSlider("Light overlap", 0f, 2f, LightingRenderer.OverlapBlend, 1f,
            v => v switch { <= 0f => "Add", >= 2f => "Screen", _ => "Max" },
            v => LightingRenderer.OverlapBlend = (int)v);

        AddHeader("Lamp turn-on (fade + stagger)");
        AddSlider("Lamp fade", 0f, 6f, DebugSettings.LightFadeSeconds, 0.1f, v => $"{v:0.0}s", v => DebugSettings.LightFadeSeconds = v);
        AddSlider("Lamp on level", 0f, 1f, DebugSettings.LightOnLevel, 0.01f, v => $"{v:0.00}", v => DebugSettings.LightOnLevel = v);
        AddSlider("Lamp stagger", 0f, 0.5f, DebugSettings.LightStaggerSpan, 0.01f, v => $"{v:0.00}", v => DebugSettings.LightStaggerSpan = v);
        AddSlider("Light down-reach px/step", 4f, 40f, DebugSettings.LightDownReachPxPerStep, 1f, v => $"{v:0}px", v => DebugSettings.LightDownReachPxPerStep = v);
        AddSlider("Light down-reach max", 0f, 6f, DebugSettings.LightDownReachMaxSteps, 1f, v => $"{v:0}", v => DebugSettings.LightDownReachMaxSteps = (int)v);
        AddSlider("Flame flicker", 0f, 4f, DebugSettings.LightFlickerScale, 0.1f, v => $"{v:0.0}x", v => DebugSettings.LightFlickerScale = v);
        AddSlider("Lantern flicker", 0f, 2f, DebugSettings.LanternFlickerScale, 0.05f, v => $"{v:0.00}x", v => DebugSettings.LanternFlickerScale = v);

        AddHeader("Lantern darkness reduction (cycle map)");
        AddSlider("Small lantern (cycle)", 0f, 1f, DarknessRenderer.LanternReliefCycleSmall, 0.05f, v => $"{v:0.00}", v => DarknessRenderer.LanternReliefCycleSmall = v);
        AddSlider("Large lantern (cycle)", 0f, 1f, DarknessRenderer.LanternReliefCycleLarge, 0.05f, v => $"{v:0.00}", v => DarknessRenderer.LanternReliefCycleLarge = v);

        AddHeader("Lantern darkness reduction (dark map)");
        AddSlider("Small lantern (dark)", 0f, 1f, DarknessRenderer.LanternReliefDarkSmall, 0.05f, v => $"{v:0.00}", v => DarknessRenderer.LanternReliefDarkSmall = v);
        AddSlider("Large lantern (dark)", 0f, 1f, DarknessRenderer.LanternReliefDarkLarge, 0.05f, v => $"{v:0.00}", v => DarknessRenderer.LanternReliefDarkLarge = v);
        AddSlider("Glow mult (dark map)", 0f, 1f, LightingSystem.LanternGlowDarkMapMultiplier, 0.05f, v => $"{v:0.00}", v => LightingSystem.LanternGlowDarkMapMultiplier = v);

        AddHeader("Town map (parchment) - rebuilds on change");
        AddSlider("Floor colour tint", 0f, 0.6f, DebugSettings.MapFloorTint, 0.02f, v => $"{v:0.00}", v => { DebugSettings.MapFloorTint = v; DebugSettings.MapInkRevision++; });
        AddSlider("Structure colour tint", 0f, 0.6f, DebugSettings.MapForegroundTint, 0.02f, v => $"{v:0.00}", v => { DebugSettings.MapForegroundTint = v; DebugSettings.MapInkRevision++; });
        AddSlider("Collision strength", 0f, 1f, DebugSettings.MapCollisionStrength, 0.05f, v => $"{v:0.00}", v => { DebugSettings.MapCollisionStrength = v; DebugSettings.MapInkRevision++; });
        AddSlider("Wall ink", 0f, 1f, DebugSettings.MapWallInk, 0.05f, v => $"{v:0.00}", v => { DebugSettings.MapWallInk = v; DebugSettings.MapInkRevision++; });
        AddSlider("Sketch contrast", 0.5f, 4f, DebugSettings.MapSketchContrast, 0.1f, v => $"{v:0.0}", v => { DebugSettings.MapSketchContrast = v; DebugSettings.MapInkRevision++; });
        AddSlider("Edge squiggle", 0f, 3f, DebugSettings.MapEdgeSquiggle, 0.1f, v => $"{v:0.0}x", v => { DebugSettings.MapEdgeSquiggle = v; DebugSettings.MapInkRevision++; });
        AddSlider("Floor edge fade", 1f, 20f, DebugSettings.MapFloorFadePasses, 1f, v => $"{v:0}", v => { DebugSettings.MapFloorFadePasses = (int)v; DebugSettings.MapInkRevision++; });

        AddHeader("UI glow");
        AddSlider("Glow layers", 0f, 12f, DebugSettings.GlowLayers, 1f, v => $"{v:0}", v => DebugSettings.GlowLayers = (int)v);
        AddSlider("Glow strength", 0f, 3f, DebugSettings.GlowStrength, 0.01f, v => $"{v:0.00}", v => DebugSettings.GlowStrength = v);

        AddHeader("Misc");
        AddCheckbox("Extend background", MapRenderer.ExtendBackground, v => MapRenderer.ExtendBackground = v);

        FitHeight();
    }

    private void AddHeader(string text)
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
                ForegroundColor = HeaderColor,
                IsHitTestVisible = false
            });

        NextY += HEADER_ROW;
    }

    private void AddCheckbox(string label, bool value, Action<bool> onChanged)
    {
        var checkbox = new Checkbox(label, RowW - 2 * PAD, value)
        {
            X = PAD,
            Y = NextY
        };

        checkbox.Changed += onChanged;
        RowsHost.AddChild(checkbox);
        NextY += ROW;
    }

    private void AddSlider(string label, float min, float max, float value, float step, Func<float, string> format, Action<float> onChanged)
    {
        RowsHost.AddChild(
            new UILabel
            {
                Text = label,
                X = PAD,
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
            Width = RowW - VAL_X - PAD,
            Height = 16,
            CustomFontSize = MenuButton.MenuFontSize,
            ForegroundColor = ValueColor,
            IsHitTestVisible = false
        };

        var slider = new Slider(CTL_W, min, max, value, step) { X = CTL_X, Y = NextY };

        slider.Changed += v =>
        {
            valueLabel.Text = format(v);
            onChanged(v);
        };

        RowsHost.AddChild(slider);
        RowsHost.AddChild(valueLabel);
        NextY += ROW;
    }

    private void FitHeight()
    {
        var contentH = NextY + PAD;
        RowsHost.Height = contentH;

        var neededWindowH = contentH + 2 * PAD + ChromeHeight;
        Height = Math.Min(neededWindowH, H_MAX);
        Content.Height = Height - ChromeHeight;

        var viewportH = Content.Height - 2 * PAD;
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
        ApplyScroll(ScrollOffset - e.Delta * ROW);
        e.Handled = true;
    }
}
