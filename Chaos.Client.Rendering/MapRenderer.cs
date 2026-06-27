#region
using Chaos.Client.Data;
using Chaos.Client.Data.Models;
using Chaos.Client.Rendering.Utility;
using DALib.Data;
using DALib.Definitions;
using DALib.Drawing;
using DALib.Extensions;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using SkiaSharp;
#endregion

namespace Chaos.Client.Rendering;

public sealed class MapRenderer : IDisposable
{
    private readonly Dictionary<int, SKImage> BgImageCache = [];
    private readonly Lock BgImageCacheLock = new();
    private readonly Dictionary<int, Texture2D> BgTextureCache = [];
    private readonly Dictionary<int, SKImage> FgImageCache = [];
    private readonly Lock FgImageCacheLock = new();
    private readonly Dictionary<int, Texture2D> FgTextureCache = [];
    
    private TextureAtlas? BgAtlas;
    private PaletteCyclingManager? CyclingManager;
    private TextureAtlas? FgAtlas;

    //DEBUG ("/debugOptions" -> Extend background): when on, the off-map void is filled by repeating the nearest
    //EDGE background tile forever instead of showing black. Background only, foreground/entities stay on the map.
    public static bool ExtendBackground;

    public int ForegroundExtraMargin { get; private set; }

    public void Dispose()
    {
        BgAtlas?.Dispose();
        BgAtlas = null;
        FgAtlas?.Dispose();
        FgAtlas = null;
        CyclingManager?.Dispose();
        CyclingManager = null;

        foreach (var texture in BgTextureCache.Values)
            texture.Dispose();

        foreach (var image in BgImageCache.Values)
            image.Dispose();

        foreach (var texture in FgTextureCache.Values)
            texture.Dispose();

        foreach (var image in FgImageCache.Values)
            image.Dispose();
    }
    
    public Rectangle? GetFgScreenRect(int tileId, float worldX, float worldY, Camera camera)
    {
        int width, height;

        if (CyclingManager is not null && CyclingManager.FgOverrides.TryGetValue(tileId, out var cyclingRegion))
        {
            width = cyclingRegion.SourceRect.Width;
            height = cyclingRegion.SourceRect.Height;
        }
        else 
        {
            var region = FgAtlas?.TryGetRegion(tileId);

            if (region.HasValue)
            {
                width = region.Value.SourceRect.Width;
                height = region.Value.SourceRect.Height;
            }
            else
            {
                var texture = GetOrCreateFgTexture(tileId);

                if (texture is null)
                    return null;

                width = texture.Width;
                height = texture.Height;
            }
        }

        var fgWorldY = worldY + CONSTANTS.HALF_TILE_HEIGHT * 2 - height;
        var screenPos = camera.WorldToScreen(new Vector2(worldX, fgWorldY));

        return new Rectangle((int)screenPos.X, (int)screenPos.Y, width, height);
    }

    /// <summary>True when the foreground sprite has an opaque (alpha > 0) pixel at (localX, localY)
    /// relative to its top-left corner. False for transparent pixels or out-of-bounds coords.</summary>
    public bool IsFgPixelOpaque(int tileId, int localX, int localY)
    {
        if ((localX < 0) || (localY < 0))
            return false;

        var image = GetOrCreateFgImage(tileId);

        if (image is null || (localX >= image.Width) || (localY >= image.Height))
            return false;

        using var bmp = SKBitmap.FromImage(image);
        var color = bmp.GetPixel(localX, localY);

        return color.Alpha > 0;
    }

    private SKImage? GetOrCreateFgImage(int tileId)
    {
        if (FgImageCache.TryGetValue(tileId, out var cached))
            return cached;

        var palettized = DataContext.Tiles.GetForegroundTile(tileId);

        if (palettized is null)
            return null;

        var image = Graphics.RenderImage(palettized.Entity.Decompress(), palettized.Palette);
        FgImageCache[tileId] = image;

        return image;
    }

    private readonly Dictionary<int, Texture2D> HoverTintedFgCache = [];

    public void DrawForegroundTileHoverTinted(
        SpriteBatchEx spriteBatch, Camera camera, MapFile mapFile, int x, int y)
    {
        var tile = mapFile.Tiles[x, y];
        var worldPos = Camera.TileToWorld(x, y, mapFile.Height);
        var gd = spriteBatch.GraphicsDevice;

        if (tile.LeftForeground.IsRenderedTileIndex())
            DrawSingleFgTinted(spriteBatch, gd, camera, tile.LeftForeground, worldPos.X, worldPos.Y);

        if (tile.RightForeground.IsRenderedTileIndex())
            DrawSingleFgTinted(spriteBatch, gd, camera, tile.RightForeground, worldPos.X + 28, worldPos.Y);
    }

    private void DrawSingleFgTinted(SpriteBatchEx sb, GraphicsDevice gd, Camera camera, short fgId, float worldX, float worldY)
    {
        if (!HoverTintedFgCache.TryGetValue(fgId, out var tintedTex))
        {
            var image = GetOrCreateFgImage(fgId);

            if (image is null)
                return;

            using var bmp = SKBitmap.FromImage(image);
            var pixels = bmp.Pixels;

            var mgPixels = new Color[pixels.Length];

            for (var i = 0; i < pixels.Length; i++)
            {
                var sk = pixels[i];

                if (sk.Alpha == 0)
                {
                    mgPixels[i] = Color.Transparent;

                    continue;
                }

                var r = Math.Clamp((128 * sk.Red + 2 * sk.Blue) / 256 + 59, 0, 255);
                var g = Math.Clamp((131 * sk.Green - 2 * sk.Blue) / 256 + 82, 0, 255);
                var b = Math.Clamp((133 * sk.Blue - 2 * sk.Green) / 256 + 120, 0, 255);
                mgPixels[i] = new Color((byte)r, (byte)g, (byte)b, sk.Alpha);
            }

            tintedTex = new Texture2D(gd, bmp.Width, bmp.Height);
            tintedTex.SetData(mgPixels);
            HoverTintedFgCache[fgId] = tintedTex;
        }

        var height = tintedTex.Height;
        var fgWorldY = worldY + CONSTANTS.HALF_TILE_HEIGHT * 2 - height;
        var screenPos = camera.WorldToScreen(new Vector2(worldX, fgWorldY));
        sb.Draw(tintedTex, screenPos, Color.White);
    }

    private void BuildBgAtlas(GraphicsDevice device)
    {
        if (BgImageCache.Count == 0)
            return;

        var atlas = new TextureAtlas(
            device,
            PackingMode.Grid,
            CONSTANTS.TILE_WIDTH,
            CONSTANTS.TILE_HEIGHT);

        foreach ((var tileId, var image) in BgImageCache)
            atlas.Add(tileId, image);

        atlas.Build();

        //dispose source images once the atlas has their pixels
        foreach (var image in BgImageCache.Values)
            image.Dispose();

        BgImageCache.Clear();

        BgAtlas = atlas;
    }

    private void BuildFgAtlas(GraphicsDevice device)
    {
        if (FgImageCache.Count == 0)
            return;

        var atlas = new TextureAtlas(device, PackingMode.Shelf);

        foreach ((var tileId, var image) in FgImageCache)
            atlas.Add(tileId, image);

        atlas.Build();

        //dispose source images once the atlas has their pixels
        foreach (var image in FgImageCache.Values)
            image.Dispose();

        FgImageCache.Clear();

        FgAtlas = atlas;
    }

    public void Draw(
        SpriteBatchEx spriteBatch,
        GraphicsDevice device,
        MapFile mapFile,
        Camera camera,
        int animationTick)
    {
        DrawBackground(
            spriteBatch,
            mapFile,
            camera,
            animationTick);

        (var fgMinX, var fgMinY, var fgMaxX, var fgMaxY)
            = camera.GetVisibleTileBounds(mapFile.Width, mapFile.Height, ForegroundExtraMargin);

        for (var y = fgMinY; y <= fgMaxY; y++)
        {
            for (var x = fgMinX; x <= fgMaxX; x++)
                DrawForegroundTile(
                    spriteBatch,
                    device,
                    mapFile,
                    camera,
                    x,
                    y,
                    animationTick);
        }
    }

    public void DrawBackground(
        SpriteBatchEx spriteBatch,
        MapFile mapFile,
        Camera camera,
        int animationTick)
    {
        var extend = ExtendBackground;

        //extend mode steps through the full visible range (past the map edges) and looks up the CLAMPED edge tile for
        //off-map positions, so the outermost background repeats into the void instead of going black.
        (var bgMinX, var bgMinY, var bgMaxX, var bgMaxY) = camera.GetVisibleTileBounds(mapFile.Width, mapFile.Height, clampToMap: !extend);

        var maxX = mapFile.Width - 1;
        var maxY = mapFile.Height - 1;

        for (var y = bgMinY; y <= bgMaxY; y++)
        {
            for (var x = bgMinX; x <= bgMaxX; x++)
            {
                //lookup coords: in-bounds as-is, off-map clamped to the nearest edge tile (extend mode only)
                var lx = extend ? Math.Clamp(x, 0, maxX) : x;
                var ly = extend ? Math.Clamp(y, 0, maxY) : y;

                int bgIndex = mapFile.Tiles[lx, ly].Background;

                if (bgIndex <= 0)
                    continue;

                bgIndex = ResolveAnimatedTileId(bgIndex, DataContext.Tiles.GetBgAnimation(bgIndex), animationTick);

                var worldPos = Camera.TileToWorld(x, y, mapFile.Height);
                var screenPos = camera.WorldToScreen(worldPos);

                if (((screenPos.X + CONSTANTS.TILE_WIDTH) <= 0)
                    || (screenPos.X >= camera.ViewportWidth)
                    || ((screenPos.Y + CONSTANTS.TILE_HEIGHT) <= 0)
                    || (screenPos.Y >= camera.ViewportHeight))
                    continue;

                //prefer the atlas path, all bg tiles in one texture enables spritebatch batching
                if (BgAtlas is not null)
                {
                    AtlasRegion? region;

                    //cycling tiles have pre-baked variants in the atlas, pick the current step's region
                    if (CyclingManager is not null && CyclingManager.BgOverrides.TryGetValue(bgIndex, out var cyclingRegion))
                        region = cyclingRegion;
                    else
                        region = BgAtlas.TryGetRegion(bgIndex);

                    if (region.HasValue)
                    {
                        spriteBatch.Draw(
                            region.Value.Atlas,
                            screenPos,
                            region.Value.SourceRect,
                            Color.White);

                        continue;
                    }
                }

                //fallback to individual texture
                var bgTexture = GetOrCreateBgTexture(bgIndex);

                if (bgTexture is not null)
                    spriteBatch.Draw(bgTexture, screenPos, Color.White);
            }
        }
    }

    public void DrawForegroundTile(
        SpriteBatchEx spriteBatch,
        GraphicsDevice device,
        MapFile mapFile,
        Camera camera,
        int x,
        int y,
        int animationTick,
        Func<int, bool>? skipForeground = null,
        bool plain = false,
        int footPx = 0,
        Color? tint = null)
    {
        var tile = mapFile.Tiles[x, y];
        var worldPos = Camera.TileToWorld(x, y, mapFile.Height);

        if (tile.LeftForeground.IsRenderedTileIndex() && ((skipForeground is null) || !skipForeground(tile.LeftForeground)))
        {
            var lfgTileId = ResolveAnimatedTileId(
                tile.LeftForeground,
                DataContext.Tiles.GetFgAnimation(tile.LeftForeground),
                animationTick);

            DrawSingleFgTile(spriteBatch, device, camera, lfgTileId, worldPos.X, worldPos.Y, plain, footPx, tint);
        }

        //right foreground
        if (tile.RightForeground.IsRenderedTileIndex() && ((skipForeground is null) || !skipForeground(tile.RightForeground)))
        {
            var rfgTileId = ResolveAnimatedTileId(
                tile.RightForeground,
                DataContext.Tiles.GetFgAnimation(tile.RightForeground),
                animationTick);

            DrawSingleFgTile(spriteBatch, device, camera, rfgTileId, worldPos.X + CONSTANTS.HALF_TILE_WIDTH, worldPos.Y, plain, footPx, tint);
        }
    }

    //plain = draw the sprite as a flat silhouette (Color.White, no per-tile screen-blend toggling) so a caller can use
    //it as an occluder/mask under its own blend state. footPx > 0 draws ONLY the bottom footPx rows (the in-tile foot)
    //rather than the whole sprite, used as the shadow caster so a tall canopy doesn't cast, only the ground contact.
    private void DrawSingleFgTile(
        SpriteBatchEx spriteBatch,
        GraphicsDevice device,
        Camera camera,
        int tileId,
        float worldX,
        float worldY,
        bool plain = false,
        int footPx = 0,
        Color? tint = null)
    {
        var drawColor = tint ?? Color.White;
        //try atlas path (cycling override, then atlas, then fallback)
        AtlasRegion? region = null;

        if (CyclingManager is not null && CyclingManager.FgOverrides.TryGetValue(tileId, out var fgCyclingRegion))
            region = fgCyclingRegion;
        else if (FgAtlas is not null)
            region = FgAtlas.TryGetRegion(tileId);

        if (region.HasValue)
        {
            var rect = region.Value.SourceRect;

            if (footPx > 0)
            {
                DrawFootStrip(spriteBatch, region.Value.Atlas, rect, camera, worldX, worldY, footPx);

                return;
            }

            var fgWorldY = worldY + CONSTANTS.HALF_TILE_HEIGHT * 2 - rect.Height;
            var screenPos = camera.WorldToScreen(new Vector2(worldX, fgWorldY));

            if (IsOnScreen(screenPos, rect.Width, rect.Height, camera, 0))
            {
                var screenBlend = !plain && IsTileScreenBlend(tileId);

                if (screenBlend)
                    device.BlendState = BlendStates.Screen;

                spriteBatch.Draw(region.Value.Atlas, screenPos, rect, drawColor);

                if (screenBlend)
                    device.BlendState = BlendState.AlphaBlend;
            }

            return;
        }

        //fallback to individual texture
        var texture = GetOrCreateFgTexture(tileId);

        if (texture is null)
            return;

        if (footPx > 0)
        {
            DrawFootStrip(spriteBatch, texture, new Rectangle(0, 0, texture.Width, texture.Height), camera, worldX, worldY, footPx);

            return;
        }

        var fallbackWorldY = worldY + CONSTANTS.HALF_TILE_HEIGHT * 2 - texture.Height;
        var fallbackScreenPos = camera.WorldToScreen(new Vector2(worldX, fallbackWorldY));

        if (IsOnScreen(fallbackScreenPos, texture.Width, texture.Height, camera, 0))
        {
            var screenBlend = !plain && IsTileScreenBlend(tileId);

            if (screenBlend)
                device.BlendState = BlendStates.Screen;

            spriteBatch.Draw(texture, fallbackScreenPos, drawColor);

            if (screenBlend)
                device.BlendState = BlendState.AlphaBlend;
        }
    }

    //draws only the bottom footPx rows of a foreground sprite (its in-tile ground contact), used as the shadow caster.
    private static void DrawFootStrip(SpriteBatchEx spriteBatch, Texture2D atlas, Rectangle rect, Camera camera, float worldX, float worldY, int footPx)
    {
        var stripH = Math.Min(rect.Height, footPx);
        var stripRect = new Rectangle(rect.X, (rect.Y + rect.Height) - stripH, rect.Width, stripH);
        var stripWorldY = (worldY + (CONSTANTS.HALF_TILE_HEIGHT * 2)) - stripH;
        var stripScreenPos = camera.WorldToScreen(new Vector2(worldX, stripWorldY));

        if (IsOnScreen(stripScreenPos, stripRect.Width, stripRect.Height, camera, 0))
            spriteBatch.Draw(atlas, stripScreenPos, stripRect, Color.White);
    }

    private Texture2D? GetOrCreateBgTexture(int tileId)
    {
        if (BgTextureCache.TryGetValue(tileId, out var cached))
            return cached;

        var palettized = DataContext.Tiles.GetBackgroundTile(tileId);

        if (palettized is null)
            return null;

        using var image = Graphics.RenderTile(palettized.Entity, palettized.Palette);
        var texture = TextureConverter.ToTexture2D(image);
        BgTextureCache[tileId] = texture;

        return texture;
    }

    private Texture2D? GetOrCreateFgTexture(int tileId)
    {
        if (FgTextureCache.TryGetValue(tileId, out var cached))
            return cached;

        var palettized = DataContext.Tiles.GetForegroundTile(tileId);

        if (palettized is null)
            return null;

        var image = Graphics.RenderImage(palettized.Entity.Decompress(), palettized.Palette);

        var texture = TextureConverter.ToTexture2D(image);
        image.Dispose();
        FgTextureCache[tileId] = texture;

        return texture;
    }

    //margin: accept sprites up to this many pixels OFF-screen. The lighting occluder map is padded past the viewport
    //so off-screen sprites can still block edge lights. 0 = plain on-screen culling.
    private static bool IsOnScreen(
        Vector2 screenPos,
        int width,
        int height,
        Camera camera,
        int margin = 0)
        => ((screenPos.X + width) > -margin)
           && (screenPos.X < (camera.ViewportWidth + margin))
           && ((screenPos.Y + height) > -margin)
           && (screenPos.Y < (camera.ViewportHeight + margin));

    private bool IsTileScreenBlend(int tileId)
    {
        var sotpIndex = tileId - 1;
        var sotpData = DataContext.Tiles.SotpData;

        if ((sotpIndex < 0) || (sotpIndex >= sotpData.Length))
            return false;

        return (sotpData[sotpIndex] & TileFlags.Transparent) != 0;
    }

    public void PreloadMapTiles(
        GraphicsDevice device,
        MapFile mapFile,
        Action<float>? onProgress = null,
        Func<int, IEnumerable<int>>? expandFgVariants = null)
    {
        var uniqueBgTileIds = new HashSet<int>();
        var uniqueFgTileIds = new HashSet<int>();

        //phase 1: scan map to collect unique tile ids (cheap, sequential)
        for (var y = 0; y < mapFile.Height; y++)
        {
            for (var x = 0; x < mapFile.Width; x++)
            {
                var tile = mapFile.Tiles[x, y];

                if (tile.Background > 0)
                    uniqueBgTileIds.Add(tile.Background);

                if (tile.LeftForeground.IsRenderedTileIndex())
                    uniqueFgTileIds.Add(tile.LeftForeground);

                if (tile.RightForeground.IsRenderedTileIndex())
                    uniqueFgTileIds.Add(tile.RightForeground);
            }
        }

        //expand caller-provided variants (e.g. door open/closed counterparts that can appear at runtime via
        //server DoorArgs packets but are not in the initial map). without this, those variants fall through to
        //GetOrCreateFgTexture, producing standalone Texture2Ds that some gpu drivers transiently display with
        //undefined contents.
        if (expandFgVariants is not null)
            foreach (var fgId in uniqueFgTileIds.ToArray())
                foreach (var variant in expandFgVariants(fgId))
                    uniqueFgTileIds.Add(variant);

        //expand animated bg tiles: add all animation frame ids to the set
        var bgAnimEntries = new HashSet<TileAnimationEntry>(ReferenceEqualityComparer.Instance);

        foreach (var bgId in uniqueBgTileIds.ToArray())
        {
            var anim = DataContext.Tiles.GetBgAnimation(bgId);

            if (anim is null || !bgAnimEntries.Add(anim))
                continue;

            foreach (var frameTileId in anim.TileSequence)
                uniqueBgTileIds.Add(frameTileId);
        }

        //expand animated fg tiles: add all animation frame ids to the set
        var fgAnimEntries = new HashSet<TileAnimationEntry>(ReferenceEqualityComparer.Instance);

        foreach (var fgId in uniqueFgTileIds.ToArray())
        {
            var anim = DataContext.Tiles.GetFgAnimation(fgId);

            if (anim is null || !fgAnimEntries.Add(anim))
                continue;

            foreach (var frameTileId in anim.TileSequence)
                uniqueFgTileIds.Add(frameTileId);
        }

        onProgress?.Invoke(0.1f);

        //phase 2a: read bg tile data from archives sequentially (archive streams are not thread-safe)
        var bgTileData = new Dictionary<int, (Tile Tile, Palette Palette)>();

        foreach (var tileId in uniqueBgTileIds)
        {
            var palettized = DataContext.Tiles.GetBackgroundTile(tileId);

            if (palettized is not null)
                bgTileData[tileId] = (palettized.Entity, palettized.Palette);
        }

        //phase 2b: read compressed fg tile data from archives sequentially (not thread-safe)
        var compressedFgData = new Dictionary<int, (CompressedHpfFile Compressed, Palette Palette)>();

        foreach (var tileId in uniqueFgTileIds)
        {
            var palettized = DataContext.Tiles.GetForegroundTile(tileId);

            if (palettized is not null)
                compressedFgData[tileId] = (palettized.Entity, palettized.Palette);
        }

        onProgress?.Invoke(0.4f);

        //phase 3: decompress + render all tiles in parallel (cpu-only, no archive access)
        Parallel.ForEach(
            bgTileData,
            kvp =>
            {
                var image = Graphics.RenderTile(kvp.Value.Tile, kvp.Value.Palette);

                using (BgImageCacheLock.EnterScope())
                    BgImageCache[kvp.Key] = image;
            });

        var maxFgHeight = 0;

        Parallel.ForEach(
            compressedFgData,
            kvp =>
            {
                var hpf = kvp.Value.Compressed.Decompress();
                var image = Graphics.RenderImage(hpf, kvp.Value.Palette);

                using (FgImageCacheLock.EnterScope())
                {
                    FgImageCache[kvp.Key] = image;

                    if (hpf.PixelHeight > maxFgHeight)
                        maxFgHeight = hpf.PixelHeight;
                }
            });

        onProgress?.Invoke(0.7f);

        //convert max pixel height to tile rows: each tile row = 14px
        ForegroundExtraMargin = (int)MathF.Ceiling(maxFgHeight / (float)CONSTANTS.HALF_TILE_HEIGHT);

        //pre-render palette cycling variants before atlas build
        CyclingManager = new PaletteCyclingManager();

        CyclingManager.PrepareVariants(
            mapFile,
            BgImageCache,
            BgImageCacheLock,
            FgImageCache,
            FgImageCacheLock);

        onProgress?.Invoke(0.85f);

        //build atlases from all preloaded pixel data (includes base + cycling variant frames)
        BuildBgAtlas(device);
        BuildFgAtlas(device);

        //resolve cycling variant regions from the built atlases
        CyclingManager.ResolveRegions(BgAtlas, FgAtlas);

        onProgress?.Invoke(1f);
    }

    private static int ResolveAnimatedTileId(int tileId, TileAnimationEntry? anim, int animationTick)
    {
        if (anim is null)
            return tileId;

        var frameIndex = animationTick / (anim.AnimationIntervalMs / 100) % anim.TileSequence.Count;

        return anim.TileSequence[frameIndex];
    }

    public void UpdatePaletteCycling(int animationTick) => CyclingManager?.Update(animationTick);

}