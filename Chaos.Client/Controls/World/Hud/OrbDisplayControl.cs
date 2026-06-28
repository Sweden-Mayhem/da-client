#region
using Chaos.Client.Collections;
using Chaos.Client.Controls.Components;
using Chaos.Client.Definitions;
using Chaos.Client.Rendering;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
#endregion

namespace Chaos.Client.Controls.World.Hud;

public enum OrbKind { Hp, Mp }

/// <summary>
///     Animated HP or MP orb drawn alongside the bottom hotbar. Frame is computed each draw from the live attribute
///     value (frame 0 = full, frame 17 = empty), so it always reflects the current HP/MP without any event wiring.
///     Width/Height are set externally each frame by AnchorHotbars to the hotbar-scale-adjusted size.
/// </summary>
public sealed class OrbDisplayControl : UIElement
{
    private const int NATIVE_W = 30;
    private const int NATIVE_H = 101;
    private const int FRAME_COUNT = 18;

    private static readonly string HpEpf = "orb001.epf";
    private static readonly string MpEpf = "orb002.epf";

    public OrbKind Kind { get; }
    public float Alpha { get; set; } = 1f;

    public event Action? HoverEntered;
    public event Action? HoverExited;

    /// <summary>Fired on a left-click of the orb (the HUD wires this to open the Stats window).</summary>
    public event Action? Clicked;

    public OrbDisplayControl(OrbKind kind)
    {
        Kind = kind;
        Width = NATIVE_W;
        Height = NATIVE_H;
    }

    public override void OnMouseEnter() => HoverEntered?.Invoke();
    public override void OnMouseLeave() => HoverExited?.Invoke();

    public override void OnClick(ClickEvent e)
    {
        if (e.Button != MouseButton.Left)
            return;

        Clicked?.Invoke();
        e.Handled = true;
    }

    public (int Current, int Max) GetValues()
    {
        if (WorldState.Attributes.Current is not { } attrs)
            return (0, 0);

        return Kind == OrbKind.Hp
            ? ((int)attrs.CurrentHp, (int)attrs.MaximumHp)
            : ((int)attrs.CurrentMp, (int)attrs.MaximumMp);
    }

    public override void Draw(SpriteBatchEx spriteBatch)
    {
        if (!Visible)
            return;

        //must call base so UpdateClipRect() runs, ContainsPoint (hit-testing + hover) uses ClipRect
        base.Draw(spriteBatch);

        var (cur, max) = GetValues();
        var pct = max > 0 ? Math.Clamp((float)cur / max, 0f, 1f) : 0f;
        var frame = Math.Clamp((int)MathF.Round((1f - pct) * (FRAME_COUNT - 1)), 0, FRAME_COUNT - 1);

        var epf = Kind == OrbKind.Hp ? HpEpf : MpEpf;
        var tex = UiRenderer.Instance!.GetEpfTexture(epf, frame);

        spriteBatch.Draw(tex, new Rectangle(ScreenX, ScreenY, Width, Height), Color.White * Alpha);
    }
}
