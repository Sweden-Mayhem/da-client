#region
using Chaos.Client.Collections;
using Chaos.Client.Rendering;
using Chaos.Client.Rendering.Definitions;
using Chaos.Client.Systems;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
#endregion

namespace Chaos.Client.Screens;

public sealed partial class WorldScreen
{
    private void DrawScreenBorder(SpriteBatchEx spriteBatch)
    {
        if (ScreenBorder is null)
            return;

        var scale = (int)MathF.Round(ClientSettings.EffectiveHotbarScale);

        var top = 16;
        var bottom = 3;
        var left = 32;
        var right = 3;
        var middleHeight = ScreenBorder.Height - top - bottom;
        var middleWidth = ScreenBorder.Width - left - right;

        // top left
        spriteBatch.Draw(ScreenBorder, new Rectangle(0, 0, left * scale, top * scale), new Rectangle(0, 0, left, top), Color.White);

        // top + bottom
        for(int x = left; x < ChaosGame.UiWidth; x += middleWidth * scale)
        {
            spriteBatch.Draw(ScreenBorder, new Rectangle(x, 0, middleWidth * scale, top * scale), new Rectangle(left, 0, middleWidth, top), Color.White);
            spriteBatch.Draw(ScreenBorder, new Rectangle(x, ChaosGame.UiHeight - bottom * scale, middleWidth * scale, bottom * scale), new Rectangle(left, ScreenBorder.Height - bottom, middleWidth, bottom), Color.White);
        }

        // top right
        spriteBatch.Draw(ScreenBorder, new Rectangle(ChaosGame.UiWidth - right * scale, 0, right * scale, top * scale), new Rectangle(ScreenBorder.Width - right, 0, right, top), Color.White);

        // left + right
        for(int y = top; y < ChaosGame.UiHeight; y += middleHeight * scale)
        {
            spriteBatch.Draw(ScreenBorder, new Rectangle(0, y, left * scale, middleHeight * scale), new Rectangle(0, top, left, middleHeight), Color.White);
            spriteBatch.Draw(ScreenBorder, new Rectangle(ChaosGame.UiWidth - right * scale, y, right * scale, middleHeight * scale), new Rectangle(ScreenBorder.Width - right, top, right, middleHeight), Color.White);
        }

        // bottom left
        spriteBatch.Draw(ScreenBorder, new Rectangle(0, ChaosGame.UiHeight - bottom * scale, left * scale, bottom * scale), new Rectangle(0, ScreenBorder.Height - bottom, left, bottom), Color.White);

        // bottom right
        spriteBatch.Draw(ScreenBorder, new Rectangle(ChaosGame.UiWidth - right * scale, ChaosGame.UiHeight - bottom * scale, right * scale, bottom * scale), new Rectangle(ScreenBorder.Width - right, ScreenBorder.Height - bottom, right, bottom), Color.White);
    }
}
