#region
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
#endregion

namespace Chaos.Client.Rendering;

/// <summary>
///     Deferred 2D lighting with shadow occlusion
///     Each light renders an occluder map, ray-marches its gradient through the Light.fx shader, erases the object's own
///     silhouette so it isn't lit, then adds onto the ambient, and the world is multiplied by the light buffer
/// </summary>
public sealed class LightingRenderer : IDisposable
{
    private readonly GraphicsDevice Device;

    private readonly Dictionary<(LightShape Shape, int Soft, int Angle, int Source, int Pool), Texture2D> Gradients = new();
    private readonly BlendState AdditiveLight;
    private readonly BlendState AddBuffer;
    private readonly BlendState MaxBuffer;
    private readonly BlendState ScreenBuffer;

    //how overlapping lights combine, 0 = add and overlaps blow out, 1 = max takes the brighter, 2 = screen caps at full
    public static int OverlapBlend = 2;
    private readonly BlendState MultiplyComposite;
    private readonly BlendState NightTintSubtract;
    private readonly BlendState NightTintAdd;
    private readonly Texture2D WhitePixel;
    private readonly Effect ShadowEffect;
    private readonly EffectParameter PBufferSize;
    private readonly EffectParameter POccluderPad;
    private readonly EffectParameter POccluder;
    private readonly EffectParameter PLightPos;
    private readonly EffectParameter PDestRect;
    private RenderTarget2D? LightBuffer;
    private RenderTarget2D? GlowBuffer;
    private RenderTarget2D? LightBlur;
    private RenderTarget2D? OccluderBuffer;
    private int OccluderPadPx;

    public LightingRenderer(GraphicsDevice device, byte[] shaderBytes)
    {
        Device = device;

        ShadowEffect = new Effect(device, shaderBytes);
        PBufferSize = ShadowEffect.Parameters["BufferSize"];
        POccluderPad = ShadowEffect.Parameters["OccluderPad"];
        POccluder = ShadowEffect.Parameters["Occluder"];
        PLightPos = ShadowEffect.Parameters["LightPos"];
        PDestRect = ShadowEffect.Parameters["DestRect"];

        //additive, the gradient's colour weighted by its falloff alpha
        AdditiveLight = new BlendState
        {
            ColorSourceBlend = Blend.SourceAlpha,
            ColorDestinationBlend = Blend.One,
            AlphaSourceBlend = Blend.One,
            AlphaDestinationBlend = Blend.One
        };

        //plain add of an already-premultiplied glow buffer onto the light buffer
        AddBuffer = new BlendState
        {
            ColorSourceBlend = Blend.One,
            ColorDestinationBlend = Blend.One,
            AlphaSourceBlend = Blend.One,
            AlphaDestinationBlend = Blend.One
        };

        //per-channel max so overlapping lights take the brighter one instead of summing to white
        //used to combine the glows before the ambient is added
        MaxBuffer = new BlendState
        {
            ColorBlendFunction = BlendFunction.Max,
            ColorSourceBlend = Blend.One,
            ColorDestinationBlend = Blend.One,
            AlphaBlendFunction = BlendFunction.Max,
            AlphaSourceBlend = Blend.One,
            AlphaDestinationBlend = Blend.One
        };

        //screen, overlaps brighten but never exceed full white
        //a gentler middle ground between add and max
        ScreenBuffer = new BlendState
        {
            ColorSourceBlend = Blend.InverseDestinationColor,
            ColorDestinationBlend = Blend.One,
            AlphaSourceBlend = Blend.One,
            AlphaDestinationBlend = Blend.One
        };

        //multiply the world by the light
        MultiplyComposite = new BlendState
        {
            ColorSourceBlend = Blend.Zero,
            ColorDestinationBlend = Blend.SourceColor,
            AlphaSourceBlend = Blend.Zero,
            AlphaDestinationBlend = Blend.One
        };

        //night-shadow grade, rgb only with dest alpha preserved
        //together these lift unlit areas toward a cool floor while lit pools stay put, subtract runs first so bright pools don't clamp before the add
        NightTintSubtract = new BlendState
        {
            ColorBlendFunction = BlendFunction.ReverseSubtract,
            ColorSourceBlend = Blend.One,
            ColorDestinationBlend = Blend.One,
            AlphaSourceBlend = Blend.Zero,
            AlphaDestinationBlend = Blend.One
        };
        //the add is weighted by dest alpha which is map coverage, so the cool floor lifts every real map pixel and never the void
        //the floor sprites' transparent holes are filled first by stamping the floor parallelogram into the alpha
        NightTintAdd = new BlendState
        {
            ColorSourceBlend = Blend.DestinationAlpha,
            ColorDestinationBlend = Blend.One,
            AlphaSourceBlend = Blend.Zero,
            AlphaDestinationBlend = Blend.One
        };

        //alpha-only, forces the target's alpha to the source alpha over the drawn shape with rgb untouched
        //used to stamp the floor parallelogram so the floor's transparent holes count as covered map
        AlphaStamp = new BlendState
        {
            ColorWriteChannels = ColorWriteChannels.Alpha,
            AlphaSourceBlend = Blend.One,
            AlphaDestinationBlend = Blend.Zero,
            ColorSourceBlend = Blend.One,
            ColorDestinationBlend = Blend.Zero
        };

        WhitePixel = new Texture2D(device, 1, 1);
        WhitePixel.SetData([Color.White]);

        //flat vertex-colour effect for the floor-parallelogram alpha stamp, ortho projection set per call
        TintEffect = new BasicEffect(device)
        {
            VertexColorEnabled = true,
            TextureEnabled = false,
            LightingEnabled = false,
            World = Matrix.Identity,
            View = Matrix.Identity
        };
    }

    private readonly BasicEffect TintEffect;
    private readonly BlendState AlphaStamp;

    /// <summary>
    ///     Builds the shadowed light buffer and multiplies <paramref name="worldTarget" /> by it
    ///     Per light the occluder map is drawn, the glow is ray-marched against it, then the object's silhouette is wiped so it isn't lit
    /// </summary>
    public void Render(
        SpriteBatchEx spriteBatch,
        RenderTarget2D worldTarget,
        Rectangle viewport,
        Color ambient,
        ReadOnlySpan<LightSource> sources,
        int occluderPad,
        bool blurShadows,
        Action<LightSource> drawOccluders,
        Action<LightSource>? eraseSilhouette = null,
        Texture2D? ambientGradient = null)
    {
        EnsureBuffers(viewport.Width, viewport.Height, occluderPad);

        PBufferSize.SetValue(new Vector2(OccluderBuffer!.Width, OccluderBuffer.Height));
        POccluderPad.SetValue(new Vector2(occluderPad, occluderPad));

        var pos = new Vector2(viewport.X, viewport.Y);

        //start from no light, the lamp glows are merged here so overlaps don't sum
        //the ambient base is added back after the loop and the blur so each pool still lifts off the ambient floor
        Device.SetRenderTarget(LightBuffer);
        Device.Clear(Color.Black);

        for (var i = 0; i < sources.Length; i++)
        {
            var s = sources[i];

            if (!LightTouchesViewport(s, viewport))
                continue;

            //occluder map is the feet of below-lamp foreground, the shadow casters
            Device.SetRenderTarget(OccluderBuffer);
            Device.Clear(Color.Transparent);
            drawOccluders(s);

            //this light's glow alone, ray-marched against the occluder map into the scratch buffer
            Device.SetRenderTarget(GlowBuffer);
            Device.Clear(Color.Transparent);
            BindOccluder();
            DrawGlow(spriteBatch, s);

            //erase the object's full silhouette from the glow so it isn't lit
            eraseSilhouette?.Invoke(s);

            //combine the shadowed, masked glow into the light buffer using the chosen overlap mode
            Device.SetRenderTarget(LightBuffer);

            var glowBlend = OverlapBlend switch
            {
                0 => AddBuffer,
                2 => ScreenBuffer,
                _ => MaxBuffer
            };

            spriteBatch.Begin(SpriteSortMode.Deferred, glowBlend, SamplerState.PointClamp);
            spriteBatch.Draw(GlowBuffer, pos, Color.White);
            spriteBatch.End();
        }

        //smooth the shadows with a same-resolution separable box blur
        //a downscaled blur is screen-locked and shimmers as the world scrolls, full res moves with the content
        //only run it when shadows are on, the glow gradients are already smooth otherwise
        if (blurShadows)
            BlurLightBuffer(spriteBatch, pos);

        //add the day/night ambient base under the merged glows so every pool lifts off the ambient floor
        //settled is a uniform colour, a transition passes a diagonal sweep gradient instead
        Device.SetRenderTarget(LightBuffer);

        if (ambientGradient is not null)
        {
            spriteBatch.Begin(SpriteSortMode.Deferred, AddBuffer, SamplerState.LinearClamp);
            spriteBatch.Draw(ambientGradient, new Rectangle((int)pos.X, (int)pos.Y, viewport.Width, viewport.Height), Color.White);
            spriteBatch.End();
        }
        else
        {
            spriteBatch.Begin(SpriteSortMode.Deferred, AddBuffer, SamplerState.PointClamp);
            spriteBatch.Draw(WhitePixel, new Rectangle((int)pos.X, (int)pos.Y, viewport.Width, viewport.Height), ambient);
            spriteBatch.End();
        }

        Device.SetRenderTarget(worldTarget);

        spriteBatch.Begin(SpriteSortMode.Deferred, MultiplyComposite, SamplerState.PointClamp);
        spriteBatch.Draw(LightBuffer, pos, Color.White);
        spriteBatch.End();
    }

    /// <summary>
    ///     Lifts unlit areas toward a cool blue-grey while lit areas stay put
    ///     Call after <see cref="Render" /> and after the bloom so the bloom samples the warm lit world, no-op when off
    /// </summary>
    public void ApplyNightShadowTint(SpriteBatchEx spriteBatch, RenderTarget2D worldTarget, Rectangle viewport, Color nightTint, Vector2[] mapQuad)
    {
        if (((nightTint.R | nightTint.G | nightTint.B) == 0) || (LightBuffer is null) || (mapQuad.Length < 4))
            return;

        var pos = new Vector2(viewport.X, viewport.Y);
        Device.SetRenderTarget(worldTarget);

        //fill the floor diamond's alpha as one parallelogram so the floor's transparent holes count as covered map
        //foreground sprites already wrote their own alpha, so walls and arches keep coverage and get tinted later
        TintEffect.Projection = Matrix.CreateOrthographicOffCenter(0, worldTarget.Width, worldTarget.Height, 0, 0, 1);

        var verts = new[]
        {
            new VertexPositionColor(new Vector3(mapQuad[0], 0f), Color.White),
            new VertexPositionColor(new Vector3(mapQuad[1], 0f), Color.White),
            new VertexPositionColor(new Vector3(mapQuad[2], 0f), Color.White),
            new VertexPositionColor(new Vector3(mapQuad[3], 0f), Color.White)
        };

        Device.BlendState = AlphaStamp;
        Device.DepthStencilState = DepthStencilState.None;
        Device.RasterizerState = RasterizerState.CullNone;

        foreach (var effectPass in TintEffect.CurrentTechnique.Passes)
        {
            effectPass.Apply();
            Device.DrawUserIndexedPrimitives(PrimitiveType.TriangleList, verts, 0, 4, QuadIndices, 0, 2);
        }

        //subtract light times tint over the whole viewport, bright pools darken toward neutral and the void clamps at 0
        spriteBatch.Begin(SpriteSortMode.Deferred, NightTintSubtract, SamplerState.PointClamp);
        spriteBatch.Draw(LightBuffer, pos, nightTint);
        spriteBatch.End();

        //add the cool floor weighted by the coverage alpha so the void is never tinted
        spriteBatch.Begin(SpriteSortMode.Deferred, NightTintAdd, SamplerState.PointClamp);
        spriteBatch.Draw(WhitePixel, new Rectangle(viewport.X, viewport.Y, viewport.Width, viewport.Height), nightTint);
        spriteBatch.End();
    }

    private static readonly short[] QuadIndices = [0, 1, 2, 0, 2, 3];

    //same-resolution separable box blur of the light buffer
    //full res so the kernel moves with the world content, a downscaled intermediate is screen-locked and shimmers
    //two passes, horizontal then vertical, each a few weighted additive taps that average to a box blur
    private void BlurLightBuffer(SpriteBatchEx spriteBatch, Vector2 pos)
    {
        const int R = 4; //blur radius in px per axis, bigger is softer shadows
        var w = Color.White * (1f / ((2 * R) + 1));

        var bw = LightBuffer!.Width;
        var bh = LightBuffer.Height;
        var full = new Rectangle((int)pos.X, (int)pos.Y, bw, bh);

        //offset the source rect, not the dest, so every tap covers the whole buffer and off-edge samples clamp to the edge texel
        //offsetting the dest leaves a gutter where the edges sum fewer taps, which darkens the ambient and blackens the screen edges at night
        Device.SetRenderTarget(LightBlur);
        Device.Clear(Color.Transparent);
        spriteBatch.Begin(SpriteSortMode.Deferred, AddBuffer, SamplerState.PointClamp);

        for (var dx = -R; dx <= R; dx++)
            spriteBatch.Draw(LightBuffer, full, new Rectangle(dx, 0, bw, bh), w);

        spriteBatch.End();

        Device.SetRenderTarget(LightBuffer);
        Device.Clear(Color.Transparent);
        spriteBatch.Begin(SpriteSortMode.Deferred, AddBuffer, SamplerState.PointClamp);

        for (var dy = -R; dy <= R; dy++)
            spriteBatch.Draw(LightBlur, full, new Rectangle(0, dy, bw, bh), w);

        spriteBatch.End();
    }

    //point the shadow shader at the current occluder map on sampler slot 1, slot 0 is the gradient
    private void BindOccluder()
    {
        POccluder.SetValue(OccluderBuffer);
        Device.SamplerStates[1] = SamplerState.LinearClamp;
    }

    private static bool LightTouchesViewport(LightSource s, Rectangle viewport)
    {
        var w = s.PixelMask.Width;
        var h = s.PixelMask.Height;
        var x = (int)s.ScreenPosition.X - (w / 2);
        var y = (int)s.ScreenPosition.Y - (h / 2);

        return (x < viewport.Right) && ((x + w) > viewport.Left) && (y < viewport.Bottom) && ((y + h) > viewport.Top);
    }

    private Texture2D GetGradient(in LightSource s)
    {
        var key = s.Shape == LightShape.Spotlight
            ? (s.Shape, (int)MathF.Round(s.Soft * 100f), (int)MathF.Round(s.AngleDeg), (int)MathF.Round(s.SourceFrac * 100f),
               (int)MathF.Round(s.PoolFrac * 100f))
            : (s.Shape, (int)MathF.Round(s.Soft * 100f), 0, 0, 0);

        if (Gradients.TryGetValue(key, out var cached))
            return cached;

        var tex = s.Shape == LightShape.Spotlight
            ? BuildConeGradient(Device, s.AngleDeg, s.SourceFrac, s.PoolFrac, s.Soft)
            : BuildRoundGradient(Device, 256, s.Soft);

        Gradients[key] = tex;

        return tex;
    }

    //draw one light's soft gradient through the ray-march shader into the bound glow buffer
    private void DrawGlow(SpriteBatchEx spriteBatch, LightSource s)
    {
        var tex = GetGradient(in s);
        var w = s.PixelMask.Width;
        var h = s.PixelMask.Height;

        var dest = new Rectangle((int)s.ScreenPosition.X - (w / 2), (int)s.ScreenPosition.Y - (h / 2), w, h);
        var tint = s.Tint ?? Color.White;
        var a = (byte)Math.Clamp((int)(s.Intensity * 255f), 0, 255);

        PLightPos.SetValue(s.ScreenPosition);
        PDestRect.SetValue(new Vector4(dest.X, dest.Y, dest.Width, dest.Height));

        spriteBatch.Begin(SpriteSortMode.Deferred, AdditiveLight, SamplerState.LinearClamp, null, null, ShadowEffect);
        spriteBatch.Draw(tex, dest, new Color(tint.R, tint.G, tint.B, a));
        spriteBatch.End();
    }

    private void EnsureBuffers(int w, int h, int occluderPad)
    {
        w = Math.Max(1, w);
        h = Math.Max(1, h);

        if (LightBuffer is { IsDisposed: false } && (LightBuffer.Width == w) && (LightBuffer.Height == h) && (OccluderPadPx == occluderPad))
            return;

        LightBuffer?.Dispose();
        GlowBuffer?.Dispose();
        LightBlur?.Dispose();
        OccluderBuffer?.Dispose();

        //16-bit per channel so the smooth ambient and the blur round-trip stay at full precision
        //at 8-bit the ambient collapses to a handful of levels across a fade and the multiply bands per region
        LightBuffer = new RenderTarget2D(Device, w, h, false, SurfaceFormat.HalfVector4, DepthFormat.None, 0, RenderTargetUsage.PreserveContents);
        GlowBuffer = new RenderTarget2D(Device, w, h, false, SurfaceFormat.HalfVector4, DepthFormat.None, 0, RenderTargetUsage.PreserveContents);

        //full-res scratch for the shadow blur, a downscale would shimmer
        //must also be 16-bit since the blur round-trips the ambient through here
        LightBlur = new RenderTarget2D(Device, w, h, false, SurfaceFormat.HalfVector4, DepthFormat.None, 0, RenderTargetUsage.PreserveContents);

        //pad the occluder map past the viewport so an off-screen occluder can still shadow an edge light
        OccluderBuffer = new RenderTarget2D(Device, w + (occluderPad * 2), h + (occluderPad * 2), false, SurfaceFormat.Color, DepthFormat.None, 0, RenderTargetUsage.PreserveContents);
        OccluderPadPx = occluderPad;
    }

    private static float EdgeFade(float dist, float half)
    {
        var t = Math.Clamp(dist / half, 0f, 1f);
        const float start = 0.74f;

        if (t <= start)
            return 1f;

        var u = (t - start) / (1f - start);

        return 1f - (u * u * (3f - (2f * u)));
    }

    private static float Plateau(float d, float core, float edge)
    {
        if (d <= core)
            return 1f;

        if (d >= edge)
            return 0f;

        var u = (d - core) / (edge - core);

        return 1f - (u * u * (3f - (2f * u)));
    }

    private static Texture2D BuildRoundGradient(GraphicsDevice device, int size, float soft)
    {
        var px = new Color[size * size];
        var c = (size - 1) / 2f;
        var core = Math.Clamp(1f - soft, 0f, 0.98f);

        for (var y = 0; y < size; y++)
            for (var x = 0; x < size; x++)
            {
                var dx = (x - c) / c;
                var dy = (y - c) / c;
                var r = MathF.Sqrt((dx * dx) + (dy * dy));
                var a = Plateau(r, core, 1f);
                a *= EdgeFade(MathF.Abs(x - c), c) * EdgeFade(MathF.Abs(y - c), c);

                px[(y * size) + x] = new Color((byte)255, (byte)255, (byte)255, (byte)(Math.Clamp(a, 0f, 1f) * 255f));
            }

        var t = new Texture2D(device, size, size);
        t.SetData(px);

        return t;
    }

    public static float SpotlightAspect(float angleDeg, float sourceFrac, float poolFrac)
    {
        var yRound = 1f - Math.Clamp(poolFrac, 0.05f, 0.95f);
        var halfW = MathF.Tan(Math.Clamp(angleDeg, 5f, 160f) * 0.5f * (MathF.PI / 180f)) * yRound / (1f - Math.Clamp(sourceFrac, 0f, 0.9f));

        return Math.Clamp(halfW, 0.05f, 2.5f);
    }

    private static Texture2D BuildConeGradient(GraphicsDevice device, float angleDeg, float sourceFrac, float poolFrac, float soft)
    {
        const int h = 320;
        var w = Math.Clamp((int)(h * SpotlightAspect(angleDeg, sourceFrac, poolFrac)), 32, 800);

        var px = new Color[w * h];
        var halfW = (w - 1) / 2f;
        var halfH = (h - 1) / 2f;

        var r0 = halfW * Math.Clamp(sourceFrac, 0f, 0.9f);
        var capH = MathF.Max(1f, r0 * 0.85f);
        var yRound = halfH * (1f - Math.Clamp(poolFrac, 0.05f, 0.95f));
        var spread = (halfW - r0) / yRound;
        var core = Math.Clamp(1f - soft, 0f, 0.98f);

        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var dx = x - halfW;
                var dy = y - halfH;

                float bw;

                if (dy < -capH)
                    bw = 0f;
                else if (dy < 0)
                    bw = r0 * MathF.Sqrt(1f - ((dy / capH) * (dy / capH)));
                else if (dy <= yRound)
                    bw = r0 + (dy * spread);
                else
                {
                    var bt = (dy - yRound) / (halfH - yRound);
                    bw = halfW * MathF.Sqrt(MathF.Max(0f, 1f - (bt * bt)));
                }

                var a = bw > 0.5f ? Plateau(MathF.Abs(dx) / bw, core, 1f) : 0f;

                a *= EdgeFade(MathF.Abs(dx), halfW) * EdgeFade(MathF.Abs(dy), halfH);

                px[(y * w) + x] = new Color((byte)255, (byte)255, (byte)255, (byte)(Math.Clamp(a, 0f, 1f) * 255f));
            }

        var t = new Texture2D(device, w, h);
        t.SetData(px);

        return t;
    }

    public void Dispose()
    {
        foreach (var tex in Gradients.Values)
            tex.Dispose();

        Gradients.Clear();
        LightBuffer?.Dispose();
        GlowBuffer?.Dispose();
        LightBlur?.Dispose();
        OccluderBuffer?.Dispose();
        ShadowEffect.Dispose();
        AdditiveLight.Dispose();
        AddBuffer.Dispose();
        MultiplyComposite.Dispose();
        NightTintSubtract.Dispose();
        NightTintAdd.Dispose();
        AlphaStamp.Dispose();
        WhitePixel.Dispose();
        TintEffect.Dispose();
    }
}
