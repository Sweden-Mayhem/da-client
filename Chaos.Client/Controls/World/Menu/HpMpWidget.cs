#region
using Chaos.Client.Collections;
using Chaos.Client.Controls.Components;
using Microsoft.Xna.Framework;
#endregion

namespace Chaos.Client.Controls.World.Menu;

/// <summary>
///     Always-on HP/MP readout for the new UI. Two compact bars with the numeric values, polled from
///     <see cref="WorldState.Attributes" /> every frame. Pass-through so it never eats world clicks.
/// </summary>
public sealed class HpMpWidget : UIPanel
{
    private const int BAR_W = 116;
    private const int BAR_H = 13;
    private const int GAP = 2;

    private readonly UIPanel HpFill;
    private readonly UIPanel MpFill;
    private readonly UILabel HpText;
    private readonly UILabel MpText;

    public HpMpWidget()
    {
        Name = "HpMpWidget";
        Width = BAR_W + 4;
        Height = (BAR_H * 2) + GAP + 4;
        IsPassThrough = true;

        HpFill = AddBar(2, new Color(60, 14, 14) * 0.85f, new Color(196, 44, 44) * 0.95f, out HpText);
        MpFill = AddBar(2 + BAR_H + GAP, new Color(14, 22, 60) * 0.85f, new Color(58, 92, 210) * 0.95f, out MpText);
    }

    private UIPanel AddBar(int y, Color back, Color fill, out UILabel text)
    {
        AddChild(
            new UIPanel
            {
                X = 2,
                Y = y,
                Width = BAR_W,
                Height = BAR_H,
                BackgroundColor = back,
                BorderColor = new Color(110, 92, 60),
                IsHitTestVisible = false
            });

        var fillPanel = new UIPanel
        {
            X = 2,
            Y = y,
            Width = 0,
            Height = BAR_H,
            BackgroundColor = fill,
            IsHitTestVisible = false
        };
        AddChild(fillPanel);

        text = new UILabel
        {
            X = 2,
            Y = y,
            Width = BAR_W,
            Height = BAR_H,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            ForegroundColor = new Color(238, 234, 226),
            ShadowStyle = ShadowStyle.BottomRight,
            IsHitTestVisible = false
        };
        AddChild(text);

        return fillPanel;
    }

    public override void Update(GameTime gameTime)
    {
        base.Update(gameTime);

        //keep pinned to the top-right of the (resizable) window
        X = ChaosGame.UiWidth - Width - 8;
        Y = 8;

        if (WorldState.Attributes.Current is not { } attrs)
            return;

        SetBar(HpFill, HpText, (int)attrs.CurrentHp, (int)attrs.MaximumHp);
        SetBar(MpFill, MpText, (int)attrs.CurrentMp, (int)attrs.MaximumMp);
    }

    private static void SetBar(UIPanel fill, UILabel text, int current, int max)
    {
        fill.Width = max > 0 ? Math.Clamp(BAR_W * current / max, 0, BAR_W) : 0;
        text.Text = $"{current} / {max}";
    }
}
