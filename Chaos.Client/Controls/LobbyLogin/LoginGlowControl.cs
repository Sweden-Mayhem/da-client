#region
using Chaos.Client.Controls.Components;
using Chaos.Client.Definitions;
using Chaos.Client.Rendering;
using Chaos.Client.Rendering.Definitions;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
#endregion

namespace Chaos.Client.Controls.LobbyLogin;

/// <summary>
///     The animated login glow, effect 342 drawn over the painted lobby with Screen blend so it reads as light
///     Nothing draws until the player clicks inside its circle, the click plays the burst once then loops forever
/// </summary>
public sealed class LoginGlowControl : UIElement
{
    private const int EffectId = 342;
    private const int LoopStart = 15;
    private const float DrawScale = 2f;
    private const float SpeedMultiplier = 2f; //multiplies the frame interval, higher is slower
    private const float ClickFraction = 0.70f; //click target is this fraction of the glow's drawn radius

    private static readonly Vector2 Center =
        new((ChaosGame.VIRTUAL_WIDTH / 2f) + 4f, (ChaosGame.VIRTUAL_HEIGHT / 2f) - 10f);

    private readonly EffectRenderer Effects;
    private double Clock; //animation clock in ms, reset to 0 on each click
    private float ClickRadius = -1f; //virtual-pixel hotspot radius, computed once from the effect art
    private bool Playing; //false until the first click, then the burst plays and loops forever

    /// <summary>
    ///     When true the glow is suppressed, clock frozen, nothing drawn, not hit-testable
    ///     Playback is not reset, so the loop resumes after a popup that covered it closes
    /// </summary>
    public bool Held { get; set; } = true;

    public LoginGlowControl(EffectRenderer effects) => Effects = effects;

    //hit-tested by the dispatcher so a popup drawn above the glow handles the click first
    //the center is clear of the lobby's edge buttons, so it steals no button click
    public override bool ContainsPoint(int screenX, int screenY)
    {
        if (Held)
            return false;

        if (ClickRadius < 0f)
            ClickRadius = ComputeClickRadius();

        if (ClickRadius <= 0f)
            return false;

        var dx = screenX - Center.X;
        var dy = screenY - Center.Y;

        return ((dx * dx) + (dy * dy)) <= (ClickRadius * ClickRadius);
    }

    public override void OnClick(ClickEvent e)
    {
        if (Held)
            return;

        e.Handled = true;

        if (Playing)
            return; //already running, a repeat click must not restart the burst

        Playing = true; //first click starts the burst from frame 0, then it loops from LoopStart forever
        Clock = 0;
    }

    public override void Update(GameTime gameTime)
    {
        if (!Held && Playing)
            Clock += gameTime.ElapsedGameTime.TotalMilliseconds;
    }

    public override void Draw(SpriteBatch spriteBatch)
    {
        if (Held || !Playing)
            return;

        if (Effects.GetEffectInfo(EffectId) is not { FrameCount: > 0 } info)
            return;

        var interval = Math.Max(1, info.FrameIntervalMs) * SpeedMultiplier;
        var elapsed = (int)(Clock / interval);
        var total = info.FrameCount;

        int frameIdx;

        if (elapsed < total)
            frameIdx = elapsed; //first pass plays the whole burst
        else
        {
            var loopLen = total - LoopStart;
            frameIdx = loopLen > 0 ? LoopStart + ((elapsed - total) % loopLen) : total - 1;
        }

        if (Effects.GetFrame(EffectId, frameIdx) is not { } f)
            return;

        //Screen blend needs its own batch, flush the Root pass, draw the glow, then resume it with the same settings
        //so the popups after us draw normally
        spriteBatch.End();
        spriteBatch.Begin(samplerState: GlobalSettings.Sampler, blendState: BlendStates.Screen);

        var origin = new Vector2(f.Texture.Width / 2f, f.Texture.Height / 2f);
        spriteBatch.Draw(f.Texture, Center, null, Color.White, 0f, origin, DrawScale, SpriteEffects.None, 0f);

        spriteBatch.End();
        spriteBatch.Begin(samplerState: GlobalSettings.Sampler);
    }

    //hotspot radius in virtual pixels, a fraction of the glow's largest drawn half-extent across all frames
    private float ComputeClickRadius()
    {
        if (Effects.GetEffectInfo(EffectId) is not { FrameCount: > 0 } info)
            return 0f;

        var maxHalf = 0f;

        for (var i = 0; i < info.FrameCount; i++)
            if (Effects.GetFrame(EffectId, i) is { } f)
                maxHalf = Math.Max(maxHalf, Math.Max(f.Texture.Width, f.Texture.Height) / 2f);

        return maxHalf * DrawScale * ClickFraction;
    }
}
