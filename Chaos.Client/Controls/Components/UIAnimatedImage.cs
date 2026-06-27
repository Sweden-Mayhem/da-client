#region
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
#endregion

namespace Chaos.Client.Controls.Components;

public class UIAnimatedImage : UIElement
{
    public Texture2D? Texture { get; set; }
    public double FrameTime = 1000/24.0;
    public int FrameHeight { get; set; }
    public int FrameCount { get => Texture is null ? 1 : Texture.Height / FrameHeight; }

    public static UIAnimatedImage CreateWithTexture(string name, Texture2D texture, int frameHeight)
    {
        return new UIAnimatedImage
        {
            Name = name,
            X = 0,
            Y = 0,
            Texture = texture,
            Width = texture?.Width ?? 0,
            Height = texture?.Height ?? 0,
            FrameHeight = frameHeight,
        };

    }

    public override void Dispose()
    {
        Texture?.Dispose();
        Texture = null;

        base.Dispose();
    }

    public override void Draw(SpriteBatchEx spriteBatch)
    {
        if (!Visible)
            return;

        var frame = (int)(ChaosGame.GameTime.TotalGameTime.TotalMilliseconds / FrameTime) % FrameCount;

        //always run base.Draw so ClipRect updates for hit-testing, even when Texture is null
        //a textureless visible image still has bounds and may be hit-tested
        base.Draw(spriteBatch);

        if (Texture is null)
            return;

        spriteBatch.Begin(samplerState: spriteBatch.SamplerState, blendState: BlendState.NonPremultiplied);

        DrawTexture(
            spriteBatch,
            Texture,
            new Vector2(ScreenX, ScreenY),
            new Rectangle(0, frame * FrameHeight, Texture.Width, FrameHeight),
            Color.White);

        spriteBatch.End();
    }
}