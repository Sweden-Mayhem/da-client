#region
using System.Threading.Tasks;
using Chaos.Client.Collections;
using Chaos.Client.Controls.Components;
using Chaos.Client.Systems;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
#endregion

namespace Chaos.Client.Controls.World.Popups;

/// <summary>
///     Full-map overlay (M key or HUD button), drawn as a PARCHMENT SCROLL: the real current level (see
///     <see cref="MapOverviewRenderer" />) is recolored to ink on parchment (transparent where there is no map, so
///     the level truly sits ON the paper) and the scroll UNROLLS from its wooden rollers when opened, rolling back
///     in on close - with a whole-window fade riding the roll to keep it soft. Red X marks every warp (hover for the
///     destination, click to travel), the retail animated player icon stands on the player's tile, and a banner
///     sized to the map's name unfurls along the top in the blackletter display font. A full-screen modal: the world
///     behind dims, any click that is not a warp dismisses it.
/// </summary>
public sealed class TownMapControl : UIPanel
{
    //--- timing ---
    private const float OPEN_SECONDS = 0.3f;  //full unroll duration (close = same, reversed)
    private const float BANNER_START = 0.4f;  //fraction of the unroll at which the banner starts unfurling
    private const float FADE_SPAN = 0.5f;     //the whole window fades in over this fraction of the unroll (masks roll seams)

    //--- scroll art geometry (fractions of map_scroll.png, knobs if the art is ever swapped) ---
    private const float ROLLER_FRAC = 0.095f;   //width of each end strip (the wooden roller + its curl) in the art
    private const float INNER_L = 0.125f;       //usable parchment area the map content draws inside (clear of the wood)
    private const float INNER_R = 0.86f;
    private const float INNER_T = 0.14f;
    private const float INNER_B = 0.86f;
    private const float SCROLL_FIT = 0.86f;     //the scroll fills this fraction of the window (whichever axis binds)

    //--- banner geometry ---
    private const float BANNER_CURL_FRAC = 0.095f;    //width of each curled end in map_banner.png
    private const float BANNER_WIDTH_FRAC = 0.4f;     //banner width as a fraction of the scroll width (they scale as a set)
    private const float BANNER_TEXT_CENTER_Y = 0.47f; //vertical center of the banner's writable area
    private const float BANNER_TEXT_FILL = 0.72f;     //the title may fill at most this fraction of the banner's width
    private const float BANNER_OVERLAP = 0.02f;       //fraction of the banner's height that rides above the scroll's top edge
    private const float BANNER_SHADOW_ALPHA = 0.28f;  //the faint drop shadow under the banner
    private const int BANNER_SHADOW_OFFSET = 5;       //how far down the shadow falls (px)

    //--- marks / text ---
    private const int PLAYER_ICON_SCALE = 2;      //the animated "you are here" icon, at this multiple of native size
    private const int TOOLTIP_FONT = 15;          //warp destination tooltip (main UI font)
    private const float WARP_HOVER_RADIUS           = 17f;  //cursor proximity (px) to a warp mark to hover/click it
    private const float WORLDMAP_ARROW_HOVER_RADIUS = 38f;  //radius covering the edge-line + arrowhead footprint
    private const float DIM_ALPHA = 0.38f;        //world dim behind the scroll at full open
    private const float WALL_SHADE = 0.62f;       //extra multiply on WALL tiles (collision data): floors stay light, walls press dark

    private static readonly Color InkColor = new(74, 46, 22);    //banner title ink
    private static readonly Color TooltipColor = new(236, 220, 170);
    private static readonly (int X, int Y)[] OutlineOffsets =
        [(-1, -1), (0, -1), (1, -1), (-1, 0), (1, 0), (-1, 1), (0, 1), (1, 1)];

    //--- embedded art (loaded once, premultiplied for the UI's AlphaBlend pass) ---
    private static Texture2D? ScrollTexture;
    private static Texture2D? BannerTexture;
    private static Texture2D? CurlShadeTexture; //1D horizontal black falloff for the roll shadow at each roller

    //CPU copy of the scroll pixels, so the inked map can MULTIPLY itself into the parchment under it (see EnsureInkedMap)
    private static Color[]? ScrollPixels;
    private static int ScrollTexW;
    private static int ScrollTexH;

    //OpenT eases 0..1; when Closing it runs back to 0 and the scroll is torn down (Hide) once it reaches 0
    //the scroll keeps drawing while rolling back in; input capture is removed at BeginClose
    private float OpenT;
    private bool Closing;

    private int PlayerFrame;
    private float PlayerFrameTimer;

    private MapOverviewRenderer? Overview;
    private GraphicsDevice? Device;
    private Func<int, int, bool>? IsTileWallAt; //collision lookup for the floor-plan wall shading (bounds-safe)
    private string MapName = string.Empty;

    //the "ink on parchment" copy of the map content (parchment baked in via CPU multiply), rebuilt when the
    //layout or the overview changes
    private Texture2D? InkedMap;
    private Rectangle InkedContentRect;
    private Rectangle InkedScrollRect;
    private int InkedGen = -1;
    private int InkedRevision = -1; //the DebugSettings.MapInkRevision the current InkedMap was baked with

    //the heavy parchment recolor runs on a background thread; when done the result is uploaded and faded in
    //kept across opens (not disposed on Hide) so reopening the same map at the same size is instant
    private System.Threading.Tasks.Task<Color[]>? InkBuildTask;
    private int InkBuildW, InkBuildH;
    private Rectangle InkBuildContentRect, InkBuildScrollRect;
    private int InkBuildGen = -1, InkBuildRevision = -1;
    private float InkedReadyFade; //0..1, ramps the baked map in once it's ready (so it doesn't pop)
    private const float INK_FADE_SECONDS = 0.35f;

    //CPU copy of the baked inked pixels + the tile->pixel projection, kept so the corner minimap can sample a circular
    //crop around the player without re-rendering; the projection always matches LastInked*
    private Color[]? LastInkedPixels;
    private int LastInkedW, LastInkedH, LastInkedGen = -1;
    private Vector2 InkedP00, InkedEx, InkedEy;                 //ACTIVE projection (matches LastInkedPixels)
    private float InkedProjScale = 1f;
    private Vector2 InkBuildP00, InkBuildEx, InkBuildEy;        //PENDING projection for the in-flight build
    private float InkBuildProjScale = 1f;

    private IReadOnlyList<WarpData.WarpCluster> Warps = [];
    private int[] WarpIconX = []; //screen-space mark centers, recomputed each frame in Layout
    private int[] WarpIconY = [];
    private int HoveredWarp = -1;
    private int PressedWarp = -1;

    private int LastPlayerTileX;
    private int LastPlayerTileY;

    //layout, recomputed each frame so everything scales as the window is resized (screen-space rects)
    private Rectangle ScrollRect;   //the scroll art at full open
    private Rectangle ContentRect;  //where the inked map sits at full open (aspect-fit inside the parchment area)
    private int MarkerX;            //player tile center
    private int MarkerY;

    public TownMapControl()
    {
        Visible = false;
        UsesControlStack = true;
        ZIndex = 155_000; //a full-screen modal: above the hotbars/windows (the banner was drawing under the F-key bar)
        IsPassThrough = false;
    }

    public void Show(
        GraphicsDevice device,
        MapOverviewRenderer overview,
        string mapName,
        IReadOnlyList<WarpData.WarpCluster> warps,
        int playerTileX,
        int playerTileY,
        Func<int, int, bool>? isTileWall = null)
    {
        if (overview.Texture is null)
            return;

        Overview = overview;
        Device = device;
        IsTileWallAt = isTileWall;
        MapName = mapName ?? string.Empty;
        Warps = warps;
        WarpIconX = new int[warps.Count];
        WarpIconY = new int[warps.Count];
        HoveredWarp = -1;
        PressedWarp = -1;
        LastPlayerTileX = playerTileX;
        LastPlayerTileY = playerTileY;

        EnsureArt(device);
        MapMarkers.EnsureLoaded(device);
        Layout(); //size + position before the first draw
        InputDispatcher.Instance?.PushControl(this);

        //unroll from wherever we are - 0 on a fresh open, or the current progress when reversing a close mid-roll
        OpenT = Closing ? OpenT : 0f;
        Closing = false;
        Visible = true;
        SoundSystem.PlayWindowOpen();
    }

    /// <summary>True while the map is open and not already rolling closed (used by the toggle so a press during the
    ///     close roll reopens rather than re-closes).</summary>
    public bool IsOpen => Visible && !Closing;

    /// <summary>Animated dismiss: plays the close cue, releases input capture, and rolls the scroll back in (real
    ///     teardown in Update once closed). <see cref="Hide" /> is the immediate teardown for map changes.</summary>
    public void BeginClose()
    {
        if (!Visible || Closing)
            return;

        Closing = true;
        InputDispatcher.Instance?.RemoveControl(this); //stop capturing clicks/keys while it rolls back in
        SoundSystem.PlayWindowClose();
        Closed?.Invoke();
    }

    public void Hide()
    {
        //a direct hide (map change, warp confirm) announces the close
        //an animated close does not - BeginClose already did
        var announce = Visible && !Closing;

        InputDispatcher.Instance?.RemoveControl(this);
        Visible = false;
        Closing = false;
        OpenT = 0f;
        Overview = null;
        IsTileWallAt = null;
        Warps = [];
        HoveredWarp = -1;

        //the baked InkedMap is kept across closes so reopening the same map is a free cache hit
        //it is replaced on a genuine miss and freed in Dispose; the in-flight build task is key-checked on upload

        if (announce)
            Closed?.Invoke();
    }

    /// <inheritdoc />
    public override void Dispose()
    {
        Hide();
        InkedMap?.Dispose();
        InkedMap = null;
        InkedGen = -1;
        base.Dispose();
    }

    public override void OnKeyDown(KeyDownEvent e)
    {
        if (e.Key is Keys.Escape or Keys.T)
        {
            BeginClose();
            e.Handled = true;
        }
    }

    /// <summary>Raised when a warp mark is clicked. The map stays open (the caller shows a confirm over it).</summary>
    public event Action<WarpData.WarpCluster>? WarpSelected;

    /// <summary>Set by the host: true while the warp travel confirm is up. A non-warp click is then inert instead of
    ///     closing the map (a click that barely misses an X must not tear down both the dialog and the map).</summary>
    public Func<bool>? HasOpenConfirm;

    /// <summary>Raised once when the map stops being open (animated close or a direct hide), so anything anchored to
    ///     it - the warp travel confirm - can leave with it.</summary>
    public event Action? Closed;

    public override void OnMouseDown(MouseDownEvent e)
    {
        //while rolling closed the panel still hit-tests (it covers the screen as it fades) - eat the click
        //a warp clicked mid-close would pop the travel confirm over a map that is no longer there
        PressedWarp = Closing ? -1 : HoveredWarp;
        e.Handled = true;
    }

    //the dispatcher SYNTHESIZES Click/DoubleClick after a mouse-up; unhandled they bubble to the root world-click
    //handler, so every map click also clicked the WORLD behind it (walking the player, opening NPCs, stepping onto
    //warp reactors -> map change -> the map and travel confirm torn down mid-use). A full-screen modal eats them.
    public override void OnClick(ClickEvent e) => e.Handled = true;

    public override void OnDoubleClick(DoubleClickEvent e) => e.Handled = true;

    public override void OnMouseUp(MouseUpEvent e)
    {
        base.OnMouseUp(e);

        if (Closing)
            return;

        //clicked (pressed and released on) a warp mark - raise it and keep the map open for the confirm dialog
        //(if the confirm is already up, this re-targets it to the new destination)
        if ((PressedWarp >= 0) && (PressedWarp < Warps.Count) && (PressedWarp == HoveredWarp))
        {
            var cluster = Warps[PressedWarp];
            PressedWarp = -1;
            WarpSelected?.Invoke(cluster);

            return;
        }

        PressedWarp = -1;

        //while the travel confirm is open, a non-warp click is inert: answering the question is the dialog's job,
        //and a click that just missed an X must not close the map (and the dialog with it, via Closed)
        if (HasOpenConfirm?.Invoke() is true)
            return;

        BeginClose();
    }

    public override void Update(GameTime gameTime)
    {
        if (!Visible || (Overview is null))
            return;

        var dt = (float)gameTime.ElapsedGameTime.TotalSeconds;

        //advance the unroll; finishing a close-out tears the map down for real
        if (Closing)
        {
            OpenT -= dt / OPEN_SECONDS;

            if (OpenT <= 0f)
            {
                Hide();

                return;
            }
        } else if (OpenT < 1f)
            OpenT = Math.Min(1f, OpenT + (dt / OPEN_SECONDS));

        //animate the player icon
        PlayerFrameTimer += dt;

        if (PlayerFrameTimer >= MapMarkers.PLAYER_FRAME_INTERVAL)
        {
            PlayerFrameTimer -= MapMarkers.PLAYER_FRAME_INTERVAL;
            PlayerFrame = (PlayerFrame + 1) % MapMarkers.PLAYER_FRAME_COUNT;
        }

        //track the player's real tile
        var player = WorldState.GetPlayerEntity();

        if (player is not null)
        {
            LastPlayerTileX = player.TileX;
            LastPlayerTileY = player.TileY;
        }

        //re-fit to the current window every frame so everything scales as the window is resized
        Layout();
        EnsureInkedMap();

        //ease the baked map in once it's ready (a cache hit keeps it at 1, a fresh build resets it to 0)
        if (InkedMap is not null)
            InkedReadyFade = Math.Min(1f, InkedReadyFade + (dt / INK_FADE_SECONDS));

        //highlight the warp mark nearest the cursor (within the hover radius) so clumped warps don't fight for it.
        //none while rolling closed - the marks are leaving with the map.
        HoveredWarp = -1;
        var bestSq = float.MaxValue;

        if (!Closing)
        {
            for (var i = 0; i < Warps.Count; i++)
            {
                float hx, hy;

                if (Warps[i].DestMapId == -1)
                {
                    var ang = GetIsometricArrowAngle(WarpIconX[i], WarpIconY[i], ContentRect);
                    hx = WarpIconX[i] + (MathF.Cos(ang) * 17f);
                    hy = WarpIconY[i] + (MathF.Sin(ang) * 17f);
                }
                else
                {
                    hx = WarpIconX[i];
                    hy = WarpIconY[i];
                }

                float dx = InputBuffer.MouseX - hx;
                float dy = InputBuffer.MouseY - hy;
                var   sq = (dx * dx) + (dy * dy);
                var   r  = Warps[i].DestMapId == -1 ? WORLDMAP_ARROW_HOVER_RADIUS : WARP_HOVER_RADIUS;

                if ((sq < r * r) && (sq < bestSq))
                {
                    bestSq = sq;
                    HoveredWarp = i;
                }
            }
        }

        base.Update(gameTime);
    }

    public override void Draw(SpriteBatch spriteBatch)
    {
        if (!Visible || (ScrollTexture is null))
            return;

        base.Draw(spriteBatch); //updates ClipRect for hit-testing (the panel itself is transparent)

        var open = SmoothStep01(Math.Clamp(OpenT, 0f, 1f));

        //whole-window fade riding the roll: everything (scroll included) fades in over the first FADE_SPAN of the
        //unroll and back out at the end of the close, softening any seam the roll animation shows
        var fade = SmoothStep01(Math.Clamp(OpenT / FADE_SPAN, 0f, 1f));

        //dim the world behind the scroll
        DrawRectClipped(spriteBatch, new Rectangle(0, 0, ChaosGame.UiWidth, ChaosGame.UiHeight), Color.Black * (DIM_ALPHA * fade));

        //the scroll, unrolling horizontally from its center: rollers carried outward, parchment revealed between them
        var reveal = DrawCenterUnroll(
            spriteBatch,
            ScrollTexture,
            ScrollRect,
            ROLLER_FRAC,
            open,
            fade);

        //the inked map content, clipped to the revealed parchment span
        DrawInkedMap(spriteBatch, reveal, fade);

        //marks (skipped while outside the revealed span so they appear as the parchment uncovers them)
        DrawMarks(spriteBatch, reveal, fade);

        //the curl shadows at each roller sell the roll while it moves (gone once fully open)
        DrawCurlShadows(
            spriteBatch,
            reveal,
            open,
            fade);

        //the name banner unfurls along the top, a beat behind the map
        var bannerT = SmoothStep01(Math.Clamp((OpenT - BANNER_START) / (1f - BANNER_START), 0f, 1f));

        if (Closing)
            bannerT = Math.Min(bannerT, open); //rolling closed: the banner leaves with the scroll, never lags behind it

        if (bannerT > 0.001f)
            DrawBanner(spriteBatch, bannerT, fade);

        //tooltip last so it sits on top of everything
        if ((HoveredWarp >= 0) && (HoveredWarp < Warps.Count) && (open >= 1f))
            DrawWarpTooltip(spriteBatch, Warps[HoveredWarp].DestName);
    }

    //--- drawing pieces ---

    /// <summary>
    ///     Draws a scroll texture unrolling horizontally from its center: the two end strips (rollers / curls, each
    ///     <paramref name="endFrac" /> of the texture width) slide apart, UNCOVERING the stationary middle. The middle
    ///     is drawn at a CONSTANT float scale with its position derived linearly from the source offset, so every
    ///     texel maps to mathematically the same screen position on every frame - only the crop boundary moves. (Int
    ///     dest rects looked "anchored" but their width rounded independently of the source width, so the effective
    ///     scale breathed frame-to-frame = the jitter.) Returns the revealed middle (the writable area).
    /// </summary>
    private static Rectangle DrawCenterUnroll(
        SpriteBatch spriteBatch,
        Texture2D tex,
        Rectangle fullRect,
        float endFrac,
        float openFraction,
        float alpha,
        Color? tintOverride = null)
    {
        var srcEndW = (int)(tex.Width * endFrac);
        var srcMidW = tex.Width - (srcEndW * 2);

        var k = fullRect.Width / (float)tex.Width;   //dest px per source px, CONSTANT for a given layout
        var scaleY = fullRect.Height / (float)tex.Height;
        var dstEndW = srcEndW * k;
        var fullMidLeft = fullRect.X + dstEndW;
        var tint = (tintOverride ?? Color.White) * alpha;
        var scaleVec = new Vector2(k, scaleY);

        //symmetric source reveal; any (srcX, srcVis) pair maps each texel to the same screen spot via the formula below
        var srcVis = Math.Max(2, (int)(srcMidW * openFraction));
        var srcX = srcEndW + ((srcMidW - srcVis) / 2);
        var midLeftF = fullMidLeft + ((srcX - srcEndW) * k);
        var midRightF = midLeftF + (srcVis * k);

        spriteBatch.Draw(
            tex,
            new Vector2(midLeftF, fullRect.Y),
            new Rectangle(srcX, 0, srcVis, tex.Height),
            tint,
            0f,
            Vector2.Zero,
            scaleVec,
            SpriteEffects.None,
            0f);

        //end strips at the moving edges
        spriteBatch.Draw(
            tex,
            new Vector2(midLeftF - dstEndW, fullRect.Y),
            new Rectangle(0, 0, srcEndW, tex.Height),
            tint,
            0f,
            Vector2.Zero,
            scaleVec,
            SpriteEffects.None,
            0f);

        spriteBatch.Draw(
            tex,
            new Vector2(midRightF, fullRect.Y),
            new Rectangle(tex.Width - srcEndW, 0, srcEndW, tex.Height),
            tint,
            0f,
            Vector2.Zero,
            scaleVec,
            SpriteEffects.None,
            0f);

        return new Rectangle((int)midLeftF, fullRect.Y, (int)(midRightF - midLeftF), fullRect.Height);
    }

    //the inked map sits at its full-open position; while unrolling, only the part inside the revealed span is drawn
    //(a matching source crop), so the parchment uncovers it
    private void DrawInkedMap(SpriteBatch spriteBatch, Rectangle reveal, float alpha)
    {
        if (InkedMap is null || (ContentRect.Width <= 0))
            return;

        //never draw a baked map from a DIFFERENT map - the cache is keyed by Overview.Generation
        //a stale-generation InkedMap is the previous level; skip it until the current map's build lands
        if ((Overview is not null) && (InkedGen != Overview.Generation))
            return;

        //fade the baked map in once it's ready (async build) so it doesn't pop in
        alpha *= InkedReadyFade;

        if (alpha <= 0f)
            return;

        var left = Math.Max(ContentRect.Left, reveal.Left);
        var right = Math.Min(ContentRect.Right, reveal.Right);

        if (right <= left)
            return;

        var srcPerPx = InkedMap.Width / (float)ContentRect.Width;
        var srcX = (int)((left - ContentRect.Left) * srcPerPx);
        var srcW = Math.Max(1, (int)((right - left) * srcPerPx));

        spriteBatch.Draw(
            InkedMap,
            new Rectangle(left, ContentRect.Y, right - left, ContentRect.Height),
            new Rectangle(srcX, 0, srcW, InkedMap.Height),
            Color.White * alpha);
    }

    private void DrawMarks(SpriteBatch spriteBatch, Rectangle reveal, float alpha)
    {
        Texture2D? mark = MapMarkers.RedMark;
        var markW = mark is not null ? Math.Clamp(ContentRect.Width / 34, 12, 22) : 0;
        var markH = mark is not null ? Math.Max(1, (int)(markW * (mark.Height / (float)mark.Width))) : 0;

        for (var i = 0; i < Warps.Count; i++)
        {
            if ((WarpIconX[i] < reveal.Left) || (WarpIconX[i] > reveal.Right))
                continue;

            if (Warps[i].DestMapId == -1)
            {
                var seed = unchecked((uint)(((int)Warps[i].TileX * 1664525) + ((int)Warps[i].TileY * 214013))) ^ 0xDEADBEEFu;
                DrawWorldMapArrow(spriteBatch, WarpIconX[i], WarpIconY[i], ContentRect, i == HoveredWarp, alpha, seed);

                continue;
            }

            if (mark is null)
                continue;

            var tint = i == HoveredWarp ? Color.White : new Color(235, 235, 235) * 0.9f;
            var rect = new Rectangle(WarpIconX[i] - (markW / 2), WarpIconY[i] - (markH / 2), markW, markH);

            spriteBatch.Draw(mark, new Rectangle(rect.X + 2, rect.Y + 2, markW, markH), Color.Black * (0.55f * alpha));
            spriteBatch.Draw(mark, rect, tint * alpha);
        }

        if ((MapMarkers.PlayerFrames is { Length: > 0 } frames)
            && (MarkerX >= reveal.Left)
            && (MarkerX <= reveal.Right))
        {
            var pw = frames[0].Width * PLAYER_ICON_SCALE;
            var ph = frames[0].Height * PLAYER_ICON_SCALE;

            MapMarkers.DrawPlayerFrame(
                spriteBatch,
                PlayerFrame,
                new Rectangle(MarkerX - (pw / 2), MarkerY - ph, pw, ph),
                Color.White * alpha);
        }
    }

    private static void DrawWorldMapArrow(
        SpriteBatch spriteBatch,
        int x,
        int y,
        Rectangle contentRect,
        bool hovered,
        float alpha,
        uint seed)
    {
        if (MapMarkers.Pixel is not { } pixel)
            return;

        var angle = GetIsometricArrowAngle(x, y, contentRect);
        var cos   = MathF.Cos(angle);
        var sin   = MathF.Sin(angle);

        var arrowPerpX = MathF.Abs(cos) > MathF.Abs(sin) ? -cos : cos;
        var arrowPerpY = MathF.Abs(cos) > MathF.Abs(sin) ? sin : -sin;

        const float HEAD_HALF = 30f;
        const float HEAD_LEN  = 33f;
        const float ARROW_W   = 5f;
        const int   SLICES    = 20;

        var color = (hovered ? Color.White : new Color(240, 240, 235)) * 0.7f * alpha;
        var shadow = Color.Black * 0.35f * alpha;

        var sx = x + 3;
        var sy = y + 4;

        for (var i = 0; i < SLICES; i++)
        {
            var t    = i / (float)SLICES;
            var half = HEAD_HALF * (1f - t);
            var cx   = sx + cos * HEAD_LEN * t;
            var cy   = sy + sin * HEAD_LEN * t;

            DrawLine(spriteBatch, pixel,
                new Vector2(cx + arrowPerpX * half, cy + arrowPerpY * half),
                new Vector2(cx - arrowPerpX * half, cy - arrowPerpY * half),
                ARROW_W, shadow);
        }

        for (var i = 0; i < SLICES; i++)
        {
            var t    = i / (float)SLICES;
            var half = HEAD_HALF * (1f - t);
            var cx   = x + cos * HEAD_LEN * t;
            var cy   = y + sin * HEAD_LEN * t;

            DrawLine(spriteBatch, pixel,
                new Vector2(cx + arrowPerpX * half, cy + arrowPerpY * half),
                new Vector2(cx - arrowPerpX * half, cy - arrowPerpY * half),
                ARROW_W, color);
        }
    }

    // DA isometric: +X tile -> screen(28,14), +Y tile -> screen(-28,14).
    // Project onto the two basis vectors to find the dominant isometric axis, then
    // return the screen-space angle toward the nearest map corner in that direction.
    private static float GetIsometricArrowAngle(int x, int y, Rectangle contentRect)
    {
        var cx   = contentRect.X + contentRect.Width / 2f;
        var cy   = contentRect.Y + contentRect.Height / 2f;
        var dx   = x - cx;
        var dy   = y - cy;

        var dotX = dx * 28f + dy * 14f;
        var dotY = dx * -28f + dy * 14f;

        if (Math.Abs(dotX) >= Math.Abs(dotY))
            return dotX >= 0
                ? MathF.Atan2(14f, 28f)
                : MathF.Atan2(-14f, -28f);
        else
            return dotY >= 0
                ? MathF.Atan2(14f, -28f)
                : MathF.Atan2(-14f, 28f);
    }

    // Draws a rotated 1-pixel-wide rectangle between two points, used to build procedural lines.
    private static void DrawLine(SpriteBatch sb, Texture2D pixel, Vector2 from, Vector2 to, float width, Color color)
    {
        var dx  = to.X - from.X;
        var dy  = to.Y - from.Y;
        var len = MathF.Sqrt((dx * dx) + (dy * dy));

        if (len < 0.5f)
            return;

        sb.Draw(
            pixel,
            new Vector2((from.X + to.X) * 0.5f, (from.Y + to.Y) * 0.5f),
            null,
            color,
            MathF.Atan2(dy, dx),
            new Vector2(0.5f, 0.5f),
            new Vector2(len, width),
            SpriteEffects.None,
            0f);
    }

    //a soft dark falloff just inside each roller, as if the parchment is still curving off the roll. Only while the
    //roll is actually MOVING (fades out as the unroll completes) - at rest it read as a dark smudge on the parchment.
    //Vertically inset to the parchment band so the strip never overhangs the art's wavy transparent edges.
    private void DrawCurlShadows(
        SpriteBatch spriteBatch,
        Rectangle reveal,
        float open,
        float alpha)
    {
        if (CurlShadeTexture is null || (reveal.Width <= 0))
            return;

        var shade = 0.35f * (1f - open) * alpha;

        if (shade <= 0.01f)
            return;

        var w = Math.Max(8, (int)(ScrollRect.Width * 0.035f));
        var top = ScrollRect.Y + (int)(ScrollRect.Height * 0.07f);
        var height = (int)(ScrollRect.Height * 0.86f);

        //left edge shadow falls off rightward (texture is dark at x=0), right edge is mirrored
        spriteBatch.Draw(
            CurlShadeTexture,
            new Rectangle(reveal.Left, top, w, height),
            null,
            Color.White * shade);

        spriteBatch.Draw(
            CurlShadeTexture,
            new Rectangle(reveal.Right - w, top, w, height),
            null,
            Color.White * shade,
            0f,
            Vector2.Zero,
            SpriteEffects.FlipHorizontally,
            0f);
    }

    private void DrawBanner(SpriteBatch spriteBatch, float bannerT, float fade)
    {
        if (BannerTexture is null || string.IsNullOrEmpty(MapName))
            return;

        //the banner is a FIXED fraction of the scroll, so the two pieces of parchment always scale as a matched
        //set; the TITLE then sizes itself to fit inside the banner (starting from the banner's height, shrinking
        //for long names)
        var aspect = BannerTexture.Height / (float)BannerTexture.Width;
        var bw = (int)(ScrollRect.Width * BANNER_WIDTH_FRAC);
        var bh = (int)(bw * aspect);
        var bx = ScrollRect.X + ((ScrollRect.Width - bw) / 2);

        var maxTextW = (int)(bw * BANNER_TEXT_FILL);
        var fontSize = Math.Max(14, (int)(bh * 0.34f));

        Texture2D? tex = null;

        while (fontSize >= 12)
        {
            tex = TtfTextRenderer.GetLine(MapName, fontSize, FontKind.Fancy);

            if (tex is null)
                return;

            if (tex.Width <= maxTextW)
                break;

            fontSize -= 2;
        }

        if (tex is null)
            return;

        //overlaps the scroll's top edge, but is never allowed off the top of the screen
        var by = Math.Max(2, ScrollRect.Y - (int)(bh * BANNER_OVERLAP));

        var bannerRect = new Rectangle(bx, by, bw, bh);

        //a faint matching-silhouette shadow under the banner grounds it on the scroll
        DrawCenterUnroll(
            spriteBatch,
            BannerTexture,
            new Rectangle(bannerRect.X, bannerRect.Y + BANNER_SHADOW_OFFSET, bannerRect.Width, bannerRect.Height),
            BANNER_CURL_FRAC,
            bannerT,
            BANNER_SHADOW_ALPHA * bannerT * fade,
            Color.Black);

        DrawCenterUnroll(
            spriteBatch,
            BannerTexture,
            bannerRect,
            BANNER_CURL_FRAC,
            bannerT,
            bannerT * fade);

        //the title inked onto the banner once it is mostly out
        if (bannerT < 0.6f)
            return;

        var textAlpha = ((bannerT - 0.6f) / 0.4f) * fade;

        var pos = new Vector2(
            bannerRect.X + ((bannerRect.Width - tex.Width) / 2f),
            bannerRect.Y + (int)(bannerRect.Height * BANNER_TEXT_CENTER_Y) - (tex.Height / 2f));

        //a soft pressed-ink shadow, then the ink
        spriteBatch.Draw(tex, pos + new Vector2(1, 2), Color.Black * (0.25f * textAlpha));
        spriteBatch.Draw(tex, pos, InkColor * textAlpha);
    }

    private void DrawWarpTooltip(SpriteBatch spriteBatch, string text)
    {
        if (string.IsNullOrEmpty(text))
            return;

        var tex = TtfTextRenderer.GetLine(text, TOOLTIP_FONT);

        if (tex is null)
            return;

        //next to the cursor, clamped on-screen
        var tx = Math.Clamp(InputBuffer.MouseX + 14, 2, ChaosGame.UiWidth - tex.Width - 4);
        var ty = Math.Clamp(InputBuffer.MouseY - tex.Height - 6, 2, ChaosGame.UiHeight - tex.Height - 2);

        DrawRectClipped(spriteBatch, new Rectangle(tx - 3, ty - 2, tex.Width + 6, tex.Height + 4), Color.Black * ClientSettings.TooltipAlpha);

        foreach (var (ox, oy) in OutlineOffsets)
            DrawTexture(spriteBatch, tex, new Vector2(tx + ox, ty + oy), Color.Black);

        DrawTexture(spriteBatch, tex, new Vector2(tx, ty), TooltipColor);
    }

    //--- layout / content prep ---

    //full-screen modal panel; the scroll art fits SCROLL_FIT of the window, the map content aspect-fits the
    //parchment's writable area, and every mark position is projected into screen space
    private void Layout()
    {
        X = 0;
        Y = 0;
        Width = ChaosGame.UiWidth;
        Height = ChaosGame.UiHeight;

        if (ScrollTexture is null || Overview?.Texture is null)
            return;

        var aspect = ScrollTexture.Height / (float)ScrollTexture.Width;
        var sw = (int)(ChaosGame.UiWidth * SCROLL_FIT);
        var sh = (int)(sw * aspect);

        if (sh > ChaosGame.UiHeight * SCROLL_FIT)
        {
            sh = (int)(ChaosGame.UiHeight * SCROLL_FIT);
            sw = (int)(sh / aspect);
        }

        ScrollRect = new Rectangle((ChaosGame.UiWidth - sw) / 2, (ChaosGame.UiHeight - sh) / 2, sw, sh);

        //writable parchment area, then the map content aspect-fit inside it (never upscaled past native)
        var inner = new Rectangle(
            ScrollRect.X + (int)(sw * INNER_L),
            ScrollRect.Y + (int)(sh * INNER_T),
            (int)(sw * (INNER_R - INNER_L)),
            (int)(sh * (INNER_B - INNER_T)));

        var scale = MathF.Min(inner.Width / (float)Overview.Width, inner.Height / (float)Overview.Height);

        if (scale > 1f)
            scale = 1f; //a small map stays small/crisp rather than blowing up

        var cw = Math.Max(1, (int)(Overview.Width * scale));
        var ch = Math.Max(1, (int)(Overview.Height * scale));

        ContentRect = new Rectangle(
            inner.X + ((inner.Width - cw) / 2),
            inner.Y + ((inner.Height - ch) / 2),
            cw,
            ch);

        //project the player + warp tiles into screen space
        var pos = Overview.Project(LastPlayerTileX, LastPlayerTileY);
        MarkerX = ContentRect.X + (int)(pos.X * scale);
        MarkerY = ContentRect.Y + (int)(pos.Y * scale);

        for (var i = 0; i < Warps.Count; i++)
        {
            var wp = Overview.Project((int)MathF.Round(Warps[i].TileX), (int)MathF.Round(Warps[i].TileY));
            WarpIconX[i] = ContentRect.X + (int)(wp.X * scale);
            WarpIconY[i] = ContentRect.Y + (int)(wp.Y * scale);
        }
    }

    //rebuilds the ink-on-parchment copy of the map when the layout or the overview changes -
    //TRUE MULTIPLY baked on the CPU, each pixel multiplied by a luminance-driven ink factor into the parchment
    /// <summary>The baked inked pixels for the CURRENT map (for the corner minimap). False until a build for THIS map has
    ///     landed - so the minimap never samples a stale (previous-map) texture with the new projection (which flickered).</summary>
    public bool TryGetMinimapPixels(out Color[] pixels, out int w, out int h)
    {
        pixels = LastInkedPixels!;
        w = LastInkedW;
        h = LastInkedH;

        return (LastInkedPixels is not null) && (Overview is not null) && (LastInkedGen == Overview.Generation);
    }

    /// <summary>A tile mapped into the baked inked-pixel space (matches TryGetMinimapPixels' pixels).</summary>
    public Vector2 InkedPixelForTile(float tileX, float tileY)
        => (InkedP00 + (tileX * InkedEx) + (tileY * InkedEy)) * InkedProjScale;

    /// <summary>A world (TileToWorld) position mapped into the baked inked-pixel space - uses the SAME projection as
    ///     InkedPixelForTile (incl. the overview's tile/top-margin offset) so a smooth centre lines up with the tile dots.</summary>
    public Vector2 InkedPixelForWorld(Vector2 worldPos)
        => Overview is not null ? Overview.ProjectWorld(worldPos) * InkedProjScale : worldPos * InkedProjScale;

    /// <summary>Inked pixels spanned by one tile step - lets the minimap pick a zoom that shows a fixed tile radius.</summary>
    public float InkedPixelsPerTile => InkedEx.Length() * InkedProjScale;

    /// <summary>The generation of the baked minimap pixels if they match the current map, else -1 (used to drive a fade-in
    ///     on map change and to avoid showing a stale map).</summary>
    public int MinimapMapGen => ((LastInkedPixels is not null) && (Overview is not null) && (LastInkedGen == Overview.Generation)) ? LastInkedGen : -1;

    /// <summary>Inverse of InkedPixelForTile: which tile a baked-inked-pixel position falls on (for click-to-path).</summary>
    public (int X, int Y) TileForInkedPixel(Vector2 inked)
    {
        var det = (InkedEx.X * InkedEy.Y) - (InkedEx.Y * InkedEy.X);

        if (Math.Abs(det) < 1e-4f)
            return (0, 0);

        var p = (inked / Math.Max(0.0001f, InkedProjScale)) - InkedP00;
        var tx = ((p.X * InkedEy.Y) - (p.Y * InkedEy.X)) / det;
        var ty = ((p.Y * InkedEx.X) - (p.X * InkedEx.Y)) / det;

        return ((int)MathF.Round(tx), (int)MathF.Round(ty));
    }

    /// <summary>Builds (cached, async) the inked map for the current map WITHOUT opening the town-map window, so the corner
    ///     minimap can use it. Safe to call every frame; it shares the same cached texture the M window reuses.</summary>
    public void EnsureInkedForMinimap(GraphicsDevice device, MapOverviewRenderer overview, Func<int, int, bool>? isTileWall)
    {
        if (overview.Texture is null)
            return;

        Device = device;
        Overview = overview;
        IsTileWallAt = isTileWall;
        EnsureArt(device);
        Layout();
        EnsureInkedMap();
    }

    //orchestrates the (cached, async) parchment recolor on the main thread - cache hits return instantly,
    //otherwise GPU prep runs here and the heavy CPU array work is launched on the thread pool
    private void EnsureInkedMap()
    {
        if (Device is null || Overview is null || (ContentRect.Width <= 0) || (ContentRect.Height <= 0) || (ScrollPixels is null))
            return;

        var gen = Overview.Generation;
        var rev = DebugSettings.MapInkRevision;

        //cache hit - the baked map already matches this map + size + tuning knobs
        if ((InkedMap is not null) && (InkedContentRect == ContentRect) && (InkedScrollRect == ScrollRect)
            && (InkedGen == gen) && (InkedRevision == rev))
            return;

        //a background build finished - upload it if it still matches what we want, then fade it in
        if (InkBuildTask is { IsCompleted: true })
        {
            var done = InkBuildTask;
            InkBuildTask = null;

            if ((done.Status == TaskStatus.RanToCompletion)
                && (InkBuildContentRect == ContentRect) && (InkBuildScrollRect == ScrollRect)
                && (InkBuildGen == gen) && (InkBuildRevision == rev))
            {
                UploadInkedMap(done.Result, InkBuildW, InkBuildH);
                InkedContentRect = ContentRect;
                InkedScrollRect = ScrollRect;
                InkedGen = gen;
                InkedRevision = rev;
                InkedReadyFade = 0f; //fade the freshly-baked map in

                //keep the CPU pixels + which map they're for + the matching projection, so the minimap can sample them
                LastInkedPixels = done.Result;
                LastInkedW = InkBuildW;
                LastInkedH = InkBuildH;
                LastInkedGen = gen;
                InkedP00 = InkBuildP00;
                InkedEx = InkBuildEx;
                InkedEy = InkBuildEy;
                InkedProjScale = InkBuildProjScale;
            }
        }

        //already building exactly this? wait for it.
        if ((InkBuildTask is not null)
            && (InkBuildContentRect == ContentRect) && (InkBuildScrollRect == ScrollRect)
            && (InkBuildGen == gen) && (InkBuildRevision == rev))
            return;

        StartInkedBuild(gen, rev);
    }

    //main-thread GPU prep (downscale + readback + wall mask), then launches the heavy array math on the thread pool
    //a stale in-flight build is abandoned; results are key-checked on upload
    private void StartInkedBuild(int gen, int rev)
    {
        if (Device is null || Overview is null || (ScrollPixels is null))
            return;

        var source = Overview.GetDisplayTexture(Device, ContentRect.Width, ContentRect.Height, true) ?? Overview.Texture;

        if (source is null)
            return;

        var w = source.Width;
        var h = source.Height;
        var src = new Color[w * h];
        source.GetData(src);

        //cache the tile->inked-pixel projection for this build and apply it on upload,
        //so the minimap never reads a projection that doesn't match the pixels it sees
        var proj0 = Overview.Project(0, 0);
        InkBuildP00 = proj0;
        InkBuildEx = Overview.Project(1, 0) - proj0;
        InkBuildEy = Overview.Project(0, 1) - proj0;
        InkBuildProjScale = w / (float)Overview.Width;

        Color[]? bg = null;
        var bgSource = Overview.GetBackgroundDisplayTexture(Device, ContentRect.Width, ContentRect.Height, true);

        if ((bgSource is not null) && (bgSource.Width == w) && (bgSource.Height == h))
        {
            bg = new Color[w * h];
            bgSource.GetData(bg);
        }

        //wall mask from collision (built here on the MAIN thread - the collision lambda is only ever touched on this thread)
        byte[]? wallMask = null;

        if (IsTileWallAt is { } isWall)
        {
            var p00 = Overview.Project(0, 0);
            var p10 = Overview.Project(1, 0);
            var p01 = Overview.Project(0, 1);
            var exX = p10.X - p00.X;
            var exY = p10.Y - p00.Y;
            var eyX = p01.X - p00.X;
            var eyY = p01.Y - p00.Y;
            var det = (exX * eyY) - (exY * eyX);

            if (Math.Abs(det) > 0.0001f)
            {
                wallMask = new byte[w * h];
                var cScale = ContentRect.Width / (float)Overview.Width;

                for (var y = 0; y < h; y++)
                    for (var x = 0; x < w; x++)
                    {
                        var mx = ((x + 0.5f) / cScale) - p00.X;
                        var my = ((y + 0.5f) / cScale) - p00.Y;
                        var tx = (int)MathF.Round(((mx * eyY) - (my * eyX)) / det);
                        var ty = (int)MathF.Round(((my * exX) - (mx * exY)) / det);

                        if (isWall(tx, ty))
                            wallMask[(y * w) + x] = 1;
                    }
            }
        }

        var job = new InkJob
        {
            W = w,
            H = h,
            Src = src,
            Bg = bg,
            WallMask = wallMask,
            ScrollPixels = ScrollPixels,
            ScrollTexW = ScrollTexW,
            ScrollTexH = ScrollTexH,
            OffX = ContentRect.X - ScrollRect.X,
            OffY = ContentRect.Y - ScrollRect.Y,
            SxScale = ScrollTexW / (float)ScrollRect.Width,
            SyScale = ScrollTexH / (float)ScrollRect.Height,
            FloorTint = DebugSettings.MapFloorTint,
            ForegroundTint = DebugSettings.MapForegroundTint,
            WallInk = DebugSettings.MapWallInk,
            CollisionStrength = DebugSettings.MapCollisionStrength,
            SketchContrast = DebugSettings.MapSketchContrast,
            Squiggle = DebugSettings.MapEdgeSquiggle,
            FloorFadePasses = Math.Max(1, DebugSettings.MapFloorFadePasses)
        };

        InkBuildW = w;
        InkBuildH = h;
        InkBuildContentRect = ContentRect;
        InkBuildScrollRect = ScrollRect;
        InkBuildGen = gen;
        InkBuildRevision = rev;
        InkBuildTask = Task.Run(() => BuildInkedPixels(job));
    }

    private void UploadInkedMap(Color[] px, int w, int h)
    {
        if (Device is null)
            return;

        if ((InkedMap is null) || (InkedMap.Width != w) || (InkedMap.Height != h))
        {
            InkedMap?.Dispose();
            InkedMap = new Texture2D(Device, w, h);
        }

        InkedMap.SetData(px);
    }

    //inputs for the off-thread parchment recolor (no GPU / no map objects - arrays + scalars only, so it is thread-safe)
    private sealed class InkJob
    {
        public int W, H;
        public Color[] Src = [];
        public Color[]? Bg;
        public byte[]? WallMask;
        public Color[] ScrollPixels = [];
        public int ScrollTexW, ScrollTexH, OffX, OffY;
        public float SxScale, SyScale;
        public float FloorTint, ForegroundTint, WallInk, CollisionStrength, SketchContrast, Squiggle;
        public int FloorFadePasses;
    }

    //the heavy parchment recolor, pure array math (runs on a background thread, parallel by row): background -> low-detail
    //colour wash; foreground -> pencil sketch + a tad of colour; collision -> shade + squiggly ink; floor edge -> faded.
    private static Color[] BuildInkedPixels(InkJob job)
    {
        var w = job.W;
        var h = job.H;
        var n = w * h;
        var pixels = new Color[n];
        var src = job.Src;

        const float HATCH = 0.2f;
        const float BG_WASH = 0.97f;
        var sketchContrast = job.SketchContrast;
        var collisionStrength = Math.Clamp(job.CollisionStrength, 0f, 1f);
        var wallInk = job.WallInk * collisionStrength;          //outline scaled by the master collision strength
        var wallShade = MathHelper.Lerp(1f, WALL_SHADE, collisionStrength); //fill darkening scaled (1 = no darkening)
        var colorTint = job.ForegroundTint;
        var bgTint = job.FloorTint;
        var squig = job.Squiggle;
        var inkVec = new Vector3(0.27f, 0.18f, 0.11f);
        var washTint = new Vector3(1f, 0.965f, 0.91f);

        //--- background (floor) colour + edge-coverage field ---
        float[]? bgR = null, bgG = null, bgB = null, bgCov = null;

        if (job.Bg is { } bgPixels)
        {
            bgR = new float[n];
            bgG = new float[n];
            bgB = new float[n];
            bgCov = new float[n];
            var bgHas = new bool[n];

            for (var i = 0; i < n; i++)
            {
                var p = bgPixels[i];
                var a = p.A / 255f;

                if (a <= 0.4f)
                    continue;

                bgHas[i] = true;
                bgR[i] = Math.Min(255f, p.R / a);
                bgG[i] = Math.Min(255f, p.G / a);
                bgB[i] = Math.Min(255f, p.B / a);
                bgCov[i] = 1f;
            }

            BoxBlurMasked(bgR, bgHas, w, h, 10);
            BoxBlurMasked(bgG, bgHas, w, h, 10);
            BoxBlurMasked(bgB, bgHas, w, h, 10);
            BoxBlur(bgCov, w, h, job.FloorFadePasses);
        }

        //--- PASS 1: luminance + coverage, then AUTO-LEVELS (serial: shared histogram, but it is cheap) ---
        var lum = new float[n];
        var cover = new float[n];
        var histogram = new int[256];
        var opaqueCount = 0;

        for (var i = 0; i < n; i++)
        {
            var p = src[i];
            var a = p.A / 255f;
            cover[i] = Math.Clamp((a - 0.45f) / 0.25f, 0f, 1f);

            if (cover[i] <= 0f)
                continue;

            var l = ((0.299f * Math.Min(255f, p.R / a)) + (0.587f * Math.Min(255f, p.G / a)) + (0.114f * Math.Min(255f, p.B / a))) / 255f;
            lum[i] = l;
            histogram[(int)(l * 255f)]++;
            opaqueCount++;
        }

        var lo = 0f;
        var hi = 1f;

        if (opaqueCount > 0)
        {
            lo = PercentileLevel(histogram, opaqueCount, 0.05f);
            hi = PercentileLevel(histogram, opaqueCount, 0.95f);

            if (hi - lo < 0.1f)
                hi = lo + 0.1f;
        }

        //--- PASS 2: tone + blurred inverse (the dodge denominator) ---
        var tone = new float[n];

        for (var i = 0; i < n; i++)
            tone[i] = cover[i] <= 0f ? 1f : Math.Clamp((lum[i] - lo) / (hi - lo), 0f, 1f);

        var blurInv = new float[n];

        for (var i = 0; i < n; i++)
            blurInv[i] = 1f - tone[i];

        BoxBlur(blurInv, w, h, 3);

        //--- main loop, parallel by row ---
        var wm = job.WallMask;
        var scroll = job.ScrollPixels;
        var stw = job.ScrollTexW;
        var sth = job.ScrollTexH;
        var offX = job.OffX;
        var offY = job.OffY;
        var sxScale = job.SxScale;
        var syScale = job.SyScale;

        Parallel.For(
            0,
            h,
            y =>
            {
                for (var x = 0; x < w; x++)
                {
                    var i = (y * w) + x;
                    var edge = cover[i];
                    var srcTexel = src[i];

                    var self = false;
                    var boundary = false;

                    if ((wm is not null) && (collisionStrength > 0f))
                    {
                        var ox = (int)MathF.Round(EdgeWarp(x, y, 0f, squig));
                        var oy = (int)MathF.Round(EdgeWarp(x, y, 37.2f, squig));
                        var sx2 = x + ox;
                        var sy2 = y + oy;
                        self = WallAtMask(wm, w, h, sx2, sy2);
                        boundary = (self != WallAtMask(wm, w, h, sx2 + 1, sy2)) || (self != WallAtMask(wm, w, h, sx2 - 1, sy2))
                                                                               || (self != WallAtMask(wm, w, h, sx2, sy2 + 1)) || (self != WallAtMask(wm, w, h, sx2, sy2 - 1));
                    }

                    var hasBg = false;
                    var floorCov = 0f;
                    var fbi = i;

                    if (bgCov is not null)
                    {
                        var fx = Math.Clamp(x + (int)MathF.Round(EdgeWarp(x, y, 91.3f, squig)), 0, w - 1);
                        var fy = Math.Clamp(y + (int)MathF.Round(EdgeWarp(x, y, 53.7f, squig)), 0, h - 1);
                        fbi = (fy * w) + fx;
                        floorCov = SmoothStep01(Math.Clamp((bgCov[fbi] - 0.5f) / 0.5f, 0f, 1f));
                        hasBg = floorCov > 0.01f;
                    }

                    if ((edge <= 0f) && !self && !boundary && !hasBg)
                    {
                        pixels[i] = Color.Transparent;

                        continue;
                    }

                    float wash;

                    if (edge > 0f)
                    {
                        var dodge = Math.Clamp(tone[i] / Math.Max(0.02f, 1f - blurInv[i]), 0f, 1f);
                        dodge = MathF.Pow(dodge, sketchContrast);
                        wash = Math.Clamp(dodge * ((1f - HATCH) + (HATCH * tone[i])), 0.12f, 0.995f);
                    }
                    else
                        wash = hasBg ? BG_WASH : 0.995f;

                    var ink = 0f;

                    if (self)
                        wash *= wallShade;

                    if (boundary)
                    {
                        var pressure = 0.65f + (0.35f * ValueNoise((x * 0.09f) + 70f, (y * 0.09f) + 70f));
                        ink = wallInk * pressure;
                    }

                    var factor = Vector3.Lerp(washTint * wash, inkVec, ink);

                    var px = Math.Clamp((int)((offX + x) * sxScale), 0, stw - 1);
                    var py = Math.Clamp((int)((offY + y) * syScale), 0, sth - 1);
                    var parch = scroll[(py * stw) + px];

                    var a = edge > 0f ? edge : ((self || boundary) ? 1f : floorCov);

                    var outR = parch.R * factor.X;
                    var outG = parch.G * factor.Y;
                    var outB = parch.B * factor.Z;

                    if ((edge > 0f) && (colorTint > 0f) && (ink <= 0f))
                    {
                        var ia = Math.Max(srcTexel.A / 255f, 0.001f);
                        var cr = Math.Min(255f, srcTexel.R / ia);
                        var cg = Math.Min(255f, srcTexel.G / ia);
                        var cb = Math.Min(255f, srcTexel.B / ia);
                        outR += (cr - outR) * colorTint;
                        outG += (cg - outG) * colorTint;
                        outB += (cb - outB) * colorTint;
                    }
                    else if (hasBg && (ink <= 0f) && (bgR is not null))
                    {
                        outR += (bgR[fbi] - outR) * bgTint;
                        outG += (bgG![fbi] - outG) * bgTint;
                        outB += (bgB![fbi] - outB) * bgTint;
                    }

                    pixels[i] = new Color(
                        (byte)(outR * a),
                        (byte)(outG * a),
                        (byte)(outB * a),
                        (byte)(255f * a));
                }
            });

        return pixels;
    }

    private static float EdgeWarp(int px, int py, float seed, float amp)
    {
        const float F1 = 0.085f, A1 = 3.0f; //long lazy waves
        const float F2 = 0.26f, A2 = 1.25f; //fine jitter

        return ((((ValueNoise((px * F1) + seed, (py * F1) + seed) - 0.5f) * 2f) * A1)
                + (((ValueNoise((px * F2) + seed + 13f, (py * F2) + seed + 13f) - 0.5f) * 2f) * A2)) * amp;
    }

    private static bool WallAtMask(byte[] mask, int w, int h, int xx, int yy)
        => (xx >= 0) && (xx < w) && (yy >= 0) && (yy < h) && (mask[(yy * w) + xx] == 1);

    private static float SmoothStep01(float t) => t * t * (3f - (2f * t));

    //plain repeated 3x3 box blur (edge-clamped); used on the floor coverage mask so it ramps from 1 to 0 across the boundary
    private static void BoxBlur(float[] data, int w, int h, int passes)
    {
        var tmp = new float[data.Length];

        for (var pass = 0; pass < passes; pass++)
        {
            var d = data; //captured by the row lambda
            Parallel.For(
                0,
                h,
                y =>
                {
                    for (var x = 0; x < w; x++)
                    {
                        var sum = 0f;

                        for (var dy = -1; dy <= 1; dy++)
                            for (var dx = -1; dx <= 1; dx++)
                                sum += d[(Math.Clamp(y + dy, 0, h - 1) * w) + Math.Clamp(x + dx, 0, w - 1)];

                        tmp[(y * w) + x] = sum / 9f;
                    }
                });

            Array.Copy(tmp, data, data.Length);
        }
    }

    //repeated 3x3 box blur that only averages MASKED (in-map) neighbours, so the transparent surround can't bleed in
    //(used to smear the floor colour into broad low-detail washes).
    private static void BoxBlurMasked(float[] data, bool[] mask, int w, int h, int passes)
    {
        var tmp = new float[data.Length];

        for (var pass = 0; pass < passes; pass++)
        {
            var d = data; //captured by the row lambda
            Parallel.For(
                0,
                h,
                y =>
                {
                    for (var x = 0; x < w; x++)
                    {
                        var idx = (y * w) + x;

                        if (!mask[idx])
                        {
                            tmp[idx] = d[idx];

                            continue;
                        }

                        var sum = 0f;
                        var nn = 0;

                        for (var dy = -1; dy <= 1; dy++)
                            for (var dx = -1; dx <= 1; dx++)
                            {
                                var xx = x + dx;
                                var yy = y + dy;

                                if ((xx < 0) || (xx >= w) || (yy < 0) || (yy >= h))
                                    continue;

                                var j = (yy * w) + xx;

                                if (!mask[j])
                                    continue;

                                sum += d[j];
                                nn++;
                            }

                        tmp[idx] = nn > 0 ? sum / nn : d[idx];
                    }
                });

            Array.Copy(tmp, data, data.Length);
        }
    }

    //deterministic hash -> [0,1). Stable per (x,y), so the inked map (baked once + cached) never flickers.
    private static float Hash01(int x, int y)
    {
        var n = unchecked((x * 374761393) + (y * 668265263));
        n = unchecked((n ^ (n >> 13)) * 1274126177);

        return ((n ^ (n >> 16)) & 0x7fffffff) / (float)int.MaxValue;
    }

    //smooth value noise in [0,1): a hashed integer lattice with smoothstep interpolation. Used to warp the collision
    //outline into a hand-drawn squiggle (and to vary its "pen pressure").
    private static float ValueNoise(float fx, float fy)
    {
        var x0 = (int)MathF.Floor(fx);
        var y0 = (int)MathF.Floor(fy);
        var sx = SmoothStep01(fx - x0);
        var sy = SmoothStep01(fy - y0);

        var v00 = Hash01(x0, y0);
        var v10 = Hash01(x0 + 1, y0);
        var v01 = Hash01(x0, y0 + 1);
        var v11 = Hash01(x0 + 1, y0 + 1);

        var a = v00 + ((v10 - v00) * sx);
        var b = v01 + ((v11 - v01) * sx);

        return a + ((b - a) * sy);
    }

    //the luminance (0..1) below which the given fraction of opaque pixels fall
    private static float PercentileLevel(int[] histogram, int total, float fraction)
    {
        var target = (int)(total * fraction);
        var seen = 0;

        for (var i = 0; i < histogram.Length; i++)
        {
            seen += histogram[i];

            if (seen >= target)
                return i / 255f;
        }

        return 1f;
    }

    //--- embedded art loading ---

    private static void EnsureArt(GraphicsDevice device)
    {
        ScrollTexture ??= MapMarkers.LoadEmbeddedPremultiplied(device, "map_scroll.png");
        BannerTexture ??= MapMarkers.LoadEmbeddedPremultiplied(device, "map_banner.png");

        //CPU copy of the parchment for the inked-map multiply bake
        if ((ScrollPixels is null) && (ScrollTexture is not null))
        {
            ScrollTexW = ScrollTexture.Width;
            ScrollTexH = ScrollTexture.Height;
            ScrollPixels = new Color[ScrollTexW * ScrollTexH];
            ScrollTexture.GetData(ScrollPixels);
        }

        if (CurlShadeTexture is null)
        {
            //64x1 black falloff, dark at x=0 (premultiplied black = just alpha)
            const int W = 64;
            var px = new Color[W];

            for (var x = 0; x < W; x++)
            {
                var t = 1f - (x / (float)(W - 1));
                px[x] = new Color(0f, 0f, 0f, t * t);
            }

            CurlShadeTexture = new Texture2D(device, W, 1);
            CurlShadeTexture.SetData(px);
        }
    }
}
