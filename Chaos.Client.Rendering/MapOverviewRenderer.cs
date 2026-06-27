#region
using DALib.Data;
using DALib.Definitions;
using DALib.Drawing;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
#endregion

namespace Chaos.Client.Rendering;

/// <summary>
///     Renders the entire current map (background + foreground tiles) into a single texture at NATIVE tile scale, so the
///     tiles tile perfectly with no seams. The town-map overlay (T) shows this instead of a hand-drawn map: the real
///     level, with an animated marker on the player's true tile. The caller scales this one texture down to fit the
///     window for display (scaling a single texture cannot streak, unlike per-tile scaling). Regenerated only when the
///     map changes; reuses <see cref="MapRenderer" />'s tile caches, so it is cheap and always in sync.
/// </summary>
public sealed class MapOverviewRenderer : IDisposable
{
    //cap the native render so a pathologically large map cannot exceed the GPU's max texture size; every real map is
    //far smaller than this, so the render stays 1:1 (integer tile positions, seam-free).
    private const int NATIVE_MAX = 8192;

    private RenderTarget2D? Target;
    private RenderTarget2D? Smoothed;
    //optional background-ONLY render (the town map sketches the foreground but wants the floor flat + low-detail, so it
    //needs the two layers apart)
    private RenderTarget2D? BackgroundTarget;
    private RenderTarget2D? BackgroundSmoothed;
    private int BackgroundSmoothedGen = -1;
    private SpriteBatchEx? Batch;
    private MapFile? GeneratedFor;
    private float MasterScale = 1f; //world pixels -> texture pixels (1 for every real map)
    private int MapHeightTiles;
    private int TopMarginPx;
    private int SmoothedGen = -1;
    private bool IncludedBackground = true; //whether the current master included the background (part of the cache key)

    /// <summary>Bumped each time the master is rebuilt, so copies built from it (the smoothed downscale, the town map's
    ///     parchment-inked recolor) know when to refresh.</summary>
    public int Generation { get; private set; }

    public Texture2D? Texture => Target;
    public int Width => Target?.Width ?? 0;
    public int Height => Target?.Height ?? 0;

    /// <inheritdoc />
    public void Dispose()
    {
        Target?.Dispose();
        Target = null;
        Smoothed?.Dispose();
        Smoothed = null;
        BackgroundTarget?.Dispose();
        BackgroundTarget = null;
        BackgroundSmoothed?.Dispose();
        BackgroundSmoothed = null;
        Batch?.Dispose();
        Batch = null;
        GeneratedFor = null;
    }

    /// <summary>
    ///     Returns the texture to display at the given size. With <paramref name="smooth" /> off this is the native master
    ///     (the caller scales it, point-sampled). With it on, the master is downscaled once to exactly
    ///     <paramref name="displayWidth" /> x <paramref name="displayHeight" /> with bilinear filtering, so the caller can
    ///     draw it 1:1 (smooth, no re-blur, no per-tile streaking). MUST be called outside an active SpriteBatch.
    /// </summary>
    public Texture2D? GetDisplayTexture(GraphicsDevice device, int displayWidth, int displayHeight, bool smooth)
    {
        if (Target is null)
            return null;

        if (!smooth || (displayWidth <= 0) || (displayHeight <= 0))
            return Target;

        Batch ??= new SpriteBatchEx(device);

        if ((Smoothed is null) || (Smoothed.Width != displayWidth) || (Smoothed.Height != displayHeight))
        {
            Smoothed?.Dispose();

            Smoothed = new RenderTarget2D(
                device,
                displayWidth,
                displayHeight,
                false,
                SurfaceFormat.Color,
                DepthFormat.None,
                0,
                RenderTargetUsage.PreserveContents);

            SmoothedGen = -1;
        }

        //only re-downscale when the master changed or the size changed (the realloc above resets SmoothedGen)
        if (SmoothedGen != Generation)
        {
            DownscaleHighQuality(device, Target, Smoothed);
            SmoothedGen = Generation;
        }

        return Smoothed;
    }

    /// <summary>The background-ONLY layer, downscaled to the display size (same as <see cref="GetDisplayTexture" />). Only
    ///     available when the map was built with a separate background. Null otherwise.</summary>
    public Texture2D? GetBackgroundDisplayTexture(GraphicsDevice device, int displayWidth, int displayHeight, bool smooth)
    {
        if (BackgroundTarget is null)
            return null;

        if (!smooth || (displayWidth <= 0) || (displayHeight <= 0))
            return BackgroundTarget;

        Batch ??= new SpriteBatchEx(device);

        if ((BackgroundSmoothed is null) || (BackgroundSmoothed.Width != displayWidth) || (BackgroundSmoothed.Height != displayHeight))
        {
            BackgroundSmoothed?.Dispose();

            BackgroundSmoothed = new RenderTarget2D(
                device,
                displayWidth,
                displayHeight,
                false,
                SurfaceFormat.Color,
                DepthFormat.None,
                0,
                RenderTargetUsage.PreserveContents);

            BackgroundSmoothedGen = -1;
        }

        if (BackgroundSmoothedGen != Generation)
        {
            DownscaleHighQuality(device, BackgroundTarget, BackgroundSmoothed);
            BackgroundSmoothedGen = Generation;
        }

        return BackgroundSmoothed;
    }

    /// <summary>
    ///     Downscales <paramref name="master" /> into <paramref name="dest" /> with a supersampled multi-step bilinear
    ///     filter: it halves the image repeatedly until within 2x of the destination, then does the final bilinear step.
    ///     Halving averages every source texel, so the result is sharper and free of the undersampling shimmer a single
    ///     large bilinear step leaves. For a small map (<= 2x downscale) the loop is skipped and it is just one bilinear pass.
    /// </summary>
    private void DownscaleHighQuality(GraphicsDevice device, Texture2D master, RenderTarget2D dest)
    {
        Texture2D current = master;
        var w = master.Width;
        var h = master.Height;
        RenderTarget2D? prevScratch = null;

        while ((w > dest.Width * 2) || (h > dest.Height * 2))
        {
            w = Math.Max(dest.Width, w / 2);
            h = Math.Max(dest.Height, h / 2);

            var scratch = new RenderTarget2D(device, w, h);
            device.SetRenderTarget(scratch);
            device.Clear(Color.Transparent);

            Batch!.Begin(samplerState: SamplerState.LinearClamp);
            Batch.Draw(current, new Rectangle(0, 0, w, h), Color.White);
            Batch.End();

            prevScratch?.Dispose();
            prevScratch = scratch;
            current = scratch;
        }

        device.SetRenderTarget(dest);
        device.Clear(Color.Transparent);

        Batch!.Begin(samplerState: SamplerState.LinearClamp);
        Batch.Draw(current, new Rectangle(0, 0, dest.Width, dest.Height), Color.White);
        Batch.End();

        device.SetRenderTarget(null);
        prevScratch?.Dispose();
    }

    /// <summary>
    ///     Renders the full map to a texture, unless the cached one already matches this map. MUST be called outside an
    ///     active SpriteBatch (e.g. during update / input handling): it switches the device render target and restores the
    ///     backbuffer afterward.
    /// </summary>
    public void Generate(
        GraphicsDevice device,
        MapRenderer mapRenderer,
        MapFile mapFile,
        int animationTick,
        bool includeBackground = true,
        bool separateBackground = false)
    {
        //map content is static per map, so only regenerate when the map instance changes
        if (ReferenceEquals(GeneratedFor, mapFile)
            && (Target is not null)
            && (IncludedBackground == includeBackground)
            && (!separateBackground || (BackgroundTarget is not null)))
            return;

        IncludedBackground = includeBackground;

        var w = mapFile.Width;
        var h = mapFile.Height;
        MapHeightTiles = h;

        //tall foreground tiles (walls, trees) extend UPWARD past their tile origin; reserve a top margin so they are
        //not clipped off the top of the texture.
        TopMarginPx = mapRenderer.ForegroundExtraMargin * CONSTANTS.HALF_TILE_HEIGHT;

        //full isometric pixel bounds of the map (tile origins span this; one extra tile of slack covers the far edges)
        var mapPixelW = (w + h) * CONSTANTS.HALF_TILE_WIDTH;
        var mapPixelH = (w + h) * CONSTANTS.HALF_TILE_HEIGHT + TopMarginPx;

        //render 1:1 (no seams) unless the map is larger than the GPU cap, in which case scale just enough to fit
        MasterScale = MathF.Min(1f, MathF.Min(NATIVE_MAX / (float)mapPixelW, NATIVE_MAX / (float)mapPixelH));

        var targetW = Math.Max(1, (int)MathF.Ceiling(mapPixelW * MasterScale));
        var targetH = Math.Max(1, (int)MathF.Ceiling(mapPixelH * MasterScale));

        if ((Target is null) || (Target.Width != targetW) || (Target.Height != targetH))
        {
            Target?.Dispose();

            Target = new RenderTarget2D(
                device,
                targetW,
                targetH,
                false,
                SurfaceFormat.Color,
                DepthFormat.None,
                0,
                RenderTargetUsage.PreserveContents);
        }

        //a camera that maps world iso coords straight through (Position == centre => screenPos == worldPos), shifted
        //down by the top margin so the upward foreground overhang lands inside the texture
        var camera = new Camera(mapPixelW, mapPixelH)
        {
            Position = new Vector2(mapPixelW / 2f, mapPixelH / 2f),
            Offset = new Vector2(0, TopMarginPx),
            Zoom = 1f
        };

        Batch ??= new SpriteBatchEx(device);

        //MasterScale is 1 for real maps, so this is the identity (tiles land on integer pixels -> no seams)
        var transform = Matrix.CreateScale(MasterScale);

        device.SetRenderTarget(Target);
        device.Clear(Color.Transparent);

        //background tiles (batched) - skippable: the town map's sketch wants only the STRUCTURE (foreground walls,
        //trees, buildings) drawn as line work, with the floor left as bare parchment
        if (includeBackground)
        {
            Batch.Begin(samplerState: SamplerState.PointClamp, transformMatrix: transform);
            mapRenderer.DrawBackground(Batch, mapFile, camera, animationTick);
            Batch.End();
        }

        //foreground tiles in painter order (depth = x + y ascending); immediate mode for the per-tile blend switches
        Batch.Begin(
            SpriteSortMode.Immediate,
            BlendState.AlphaBlend,
            SamplerState.PointClamp,
            null,
            null,
            null,
            transform);

        var maxDepth = (w - 1) + (h - 1);

        for (var depth = 0; depth <= maxDepth; depth++)
        {
            var xStart = Math.Max(0, depth - (h - 1));
            var xEnd = Math.Min(depth, w - 1);

            for (var x = xStart; x <= xEnd; x++)
                mapRenderer.DrawForegroundTile(
                    Batch,
                    device,
                    mapFile,
                    camera,
                    x,
                    depth - x,
                    animationTick);
        }

        Batch.End();

        //optional: render the BACKGROUND tiles alone into a second target, so a caller can treat floor and structure
        //differently (the town map flat-tints the floor but sketches the structure)
        if (separateBackground)
        {
            if ((BackgroundTarget is null) || (BackgroundTarget.Width != targetW) || (BackgroundTarget.Height != targetH))
            {
                BackgroundTarget?.Dispose();

                BackgroundTarget = new RenderTarget2D(
                    device,
                    targetW,
                    targetH,
                    false,
                    SurfaceFormat.Color,
                    DepthFormat.None,
                    0,
                    RenderTargetUsage.PreserveContents);
            }

            device.SetRenderTarget(BackgroundTarget);
            device.Clear(Color.Transparent);
            Batch.Begin(samplerState: SamplerState.PointClamp, transformMatrix: transform);
            mapRenderer.DrawBackground(Batch, mapFile, camera, animationTick);
            Batch.End();
        }

        device.SetRenderTarget(null);
        GeneratedFor = mapFile;
        Generation++; //invalidate any smoothed copy
    }

    /// <summary>The centre of the given tile, in the generated texture's pixel space (the caller multiplies by its own
    ///     display scale to place the player marker).</summary>
    public Vector2 Project(int tileX, int tileY)
    {
        var world = Camera.TileToWorld(tileX, tileY, MapHeightTiles);

        return new Vector2(
            (world.X + CONSTANTS.HALF_TILE_WIDTH) * MasterScale,
            (world.Y + CONSTANTS.HALF_TILE_HEIGHT + TopMarginPx) * MasterScale);
    }

    /// <summary>Same texture-pixel projection as <see cref="Project" /> but for an arbitrary world (TileToWorld) position,
    ///     so a smooth (sub-tile) position maps consistently with the tile projection.</summary>
    public Vector2 ProjectWorld(Vector2 world)
        => new(
            (world.X + CONSTANTS.HALF_TILE_WIDTH) * MasterScale,
            (world.Y + CONSTANTS.HALF_TILE_HEIGHT + TopMarginPx) * MasterScale);
}
