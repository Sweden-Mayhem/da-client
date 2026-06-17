#region
using System.Reflection;
using Chaos.Client.Data;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
#endregion

namespace Chaos.Client.Controls.World;

/// <summary>
///     Shared map-marker art for the town map (M) and the world travel map: the painted red X that marks places the
///     player can go (cropped so its crossing IS the texture center - clicks land where the X looks like it is), and
///     the retail animated "you are here" player icon (tmuser.epf, 7 frames).
/// </summary>
internal static class MapMarkers
{
    public const int PLAYER_FRAME_COUNT = 7;
    public const float PLAYER_FRAME_INTERVAL = 0.083f;

    public static Texture2D? RedMark { get; private set; }

    /// <summary>1×1 white premultiplied pixel -used as a building block for procedural line drawing (DrawLine).</summary>
    public static Texture2D? Pixel { get; private set; }

    public static Texture2D[]? PlayerFrames { get; private set; }

    public static void EnsureLoaded(GraphicsDevice device)
    {
        RedMark ??= LoadEmbeddedPremultiplied(device, "map_mark_red.png");

        if (Pixel is null)
        {
            Pixel = new Texture2D(device, 1, 1);
            Pixel.SetData(new Color[] { Color.White });
        }

        if (PlayerFrames is null)
        {
            var frameCount = DataContext.UserControls.GetNationalEpfFrameCount("tmuser.epf");

            if (frameCount > 0)
            {
                var frames = new Texture2D[frameCount];

                for (var i = 0; i < frameCount; i++)
                    frames[i] = UiRenderer.Instance!.GetNationalEpfTexture("tmuser.epf", i);

                PlayerFrames = frames;
            }
        }
    }

    /// <summary>Draws one player-icon frame into <paramref name="dest" /> (handles atlas-backed frames).</summary>
    public static void DrawPlayerFrame(SpriteBatch spriteBatch, int frame, Rectangle dest, Color tint)
    {
        if ((PlayerFrames is null) || (PlayerFrames.Length == 0))
            return;

        var tex = PlayerFrames[frame % PlayerFrames.Length];

        if (tex is CachedTexture2D { AtlasRegion: { } region })
            spriteBatch.Draw(region.Atlas, dest, region.SourceRect, tint);
        else
            spriteBatch.Draw(tex, dest, null, tint);
    }

    /// <summary>
    ///     Loads an embedded PNG and premultiplies its alpha. <see cref="Texture2D.FromStream(GraphicsDevice, Stream)" />
    ///     yields STRAIGHT alpha; the UI batch blends premultiplied, so soft edges get bright fringes without this.
    /// </summary>
    public static Texture2D? LoadEmbeddedPremultiplied(GraphicsDevice device, string logicalName)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(logicalName);

        if (stream is null)
            return null;

        var tex = Texture2D.FromStream(device, stream);
        var px = new Color[tex.Width * tex.Height];
        tex.GetData(px);

        for (var i = 0; i < px.Length; i++)
        {
            var p = px[i];

            if (p.A == 255)
                continue;

            px[i] = new Color(
                (byte)(p.R * p.A / 255),
                (byte)(p.G * p.A / 255),
                (byte)(p.B * p.A / 255),
                p.A);
        }

        tex.SetData(px);

        return tex;
    }
}
