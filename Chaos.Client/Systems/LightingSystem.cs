#region
using Chaos.Client.Collections;
using Chaos.Client.Data;
using Chaos.DarkAges.Definitions;
using Chaos.Geometry.Abstractions.Definitions;
using DALib.Data;
using Microsoft.Xna.Framework;
#endregion

namespace Chaos.Client.Systems;

/// <summary>
///     Owns the per-frame light source buffer and the gather logic that builds it from world entities.
///     Both <see cref="DarknessRenderer" /> and <see cref="TabMapRenderer" /> read from
///     <see cref="Sources" /> as pure read-only readers, neither stores its own copy.
/// </summary>
/// <remarks>
///     Tile-space offset shapes (Euclidean circles for lanterns, baseline visibility) live here as
///     static cached arrays so every light source of a given lantern size shares the same array
///     reference. Future direction-aware shapes (cones, lines) drop in by sending a different
///     cached array per direction inside <see cref="GetTileOffsets" />.
/// </remarks>
public sealed class LightingSystem
{
    private static readonly (int Dx, int Dy)[] Euclidean3 = ComputeEuclidean(3);
    private static readonly (int Dx, int Dy)[] Euclidean5 = ComputeEuclidean(5);

    //the player/NPC lantern glow, a weak warm amber flame, tunable
    public static Color LanternGlowTint = new(255, 198, 140);
    public static float LanternGlowIntensity = 0.55f;

    //on an "always dark" map the lantern pool reads brighter than on a day/night map (the ambient never lifts), so dim
    //the carried-lantern glow here. 0.75 means 25% less visible, only applied when the map's lightType is "alwaysdark"
    public static float LanternGlowDarkMapMultiplier = 0.75f;

    //world-space Y nudge for the carried lantern glow. 0 means on the ground at the player's feet (the tile centre). The
    //lantern is treated as a ground-level light, NOT held up in the air, so it lights its own level (down-reach 0), not
    //tiles in front. Negative would raise it. Keep it at 0 so a held lantern never lights things above/ahead of itself
    public static float LanternGlowYOffset = 0f;

    //seconds for a lantern glow to fade IN when it first appears, so it "comes on slowly" instead of snapping (mainly for
    //dark maps, on a cycle map the darkness-level ramp already brings it on as night falls)
    private const float LanternGlowFadeSeconds = 1.2f;
    //per-entity eased glow fade-in (0..1), keyed by entity id, cleared on a map change
    private readonly Dictionary<uint, float> LanternFade = new();

    /// <summary>
    ///     The unconditional baseline visibility around the player on a full-black-darkness map:
    ///     the player's own tile only. Adjacent tiles require an actual light source to reveal.
    /// </summary>
    public static readonly (int Dx, int Dy)[] BaselineVisibilityOffsets = ComputeEuclidean(0);

    private LightSource[] Buffer = new LightSource[16];
    private int Count;

    //per-tile-light eased "lit" amount (0..1), keyed by (tileX, tileY, foregroundTileId). Lets each lamp FADE on/off
    //instead of snapping, and reach full brightness at a slightly different darkness level (a position-seeded stagger).
    //Cleared on a map change so a tile in another map can't inherit a stale fraction
    private readonly Dictionary<(int X, int Y, int Id), float> LitFraction = new();
    private MapFile? LastMap;
    private float LastGatherTime;
    private bool AnyLit; //was any tile light still lit/fading last gather, keeps the scan alive to fade out after daybreak

    /// <summary>
    ///     The light sources gathered on the most recent <see cref="Gather" /> call. Span lifetime
    ///     is bounded by the next <see cref="Gather" /> call.
    /// </summary>
    public ReadOnlySpan<LightSource> Sources => Buffer.AsSpan(0, Count);

    /// <summary>
    ///     Builds the light source array for the current frame, entity lanterns plus config-driven foreground-tile
    ///     lights (<see cref="TileLights" />). Short-circuits to an empty span when the map isn't dark right now, so
    ///     no work is done in daylight and stale sources can't leak across a transition.
    /// </summary>
    /// <param name="darknessActive">Whether darkness is currently applied (a dark dungeon, or a day/night map at
    /// night). Town and cycle maps get lit too, not just flagged-dark maps.</param>
    /// <param name="time">Total elapsed seconds, for the flame flicker animation.</param>
    /// <param name="darknessLevel">How dark it is right now, 0 (full day) .. 1 (darkest night), the day/night ramp
    /// (<see cref="DarknessRenderer.DuskGlow" />). Drives each tile light's fade-in/out target.</param>
    public void Gather(MapFile? mapFile, Camera camera, bool darknessActive, float darknessLevel, float time, bool alwaysDark = false)
    {
        Count = 0;

        if (mapFile is null)
            return;

        //drop the per-light fade state on a map change (the same tile coords mean a different tile on another map). On the
        //change frame the glow snaps in INSTANTLY (mapJustChanged) instead of fading, a lantern only "comes on slowly"
        //when it is equipped mid-map, never on map entry
        var mapJustChanged = !ReferenceEquals(mapFile, LastMap);

        if (mapJustChanged)
        {
            LitFraction.Clear();
            LanternFade.Clear();
            AnyLit = false;
            LastMap = mapFile;
        }

        var dt = Math.Clamp(time - LastGatherTime, 0f, 0.1f);
        LastGatherTime = time;

        //entity lanterns (player, NPCs), neutral (Tint null), full strength, only while darkness is active (they snap)
        if (darknessActive)
            foreach (var entity in WorldState.GetEntities())
        {
            if (entity.LanternSize == LanternSize.None)
            {
                LanternFade.Remove(entity.Id); //so re-equipping a lantern fades in again rather than snapping
                continue;
            }

            var pixelMask = DataContext.LightMasks.Get(entity.LanternSize);

            if (pixelMask is null)
                continue;

            var tileOffsets = GetTileOffsets(entity.LanternSize, entity.Direction);

            var tileWorld = Camera.TileToWorld(entity.TileX, entity.TileY, mapFile.Height);
            var tileCenterX = tileWorld.X + DaLibConstants.HALF_TILE_WIDTH;
            var tileCenterY = tileWorld.Y + DaLibConstants.HALF_TILE_HEIGHT;

            //ease the per-entity glow fade-in so the lantern "comes on slowly" when equipped, but on map entry it is
            //already at full (mapJustChanged) so the lighting is set the instant the map appears
            if (!LanternFade.TryGetValue(entity.Id, out var glowFade))
                glowFade = mapJustChanged ? 1f : 0f;

            glowFade = MoveToward(glowFade, 1f, LanternGlowFadeSeconds <= 0f ? 1f : dt / LanternGlowFadeSeconds);
            LanternFade[entity.Id] = glowFade;

            //warm amber FLAME, a subtle phase-seeded flicker on the strength + a small drift on the position, so the
            //lantern lives a little instead of being a flat disc. Seeded per entity so two nearby lanterns don't pulse
            //in lockstep. Same shape as the tile-light flame, kept small + smooth
            var phase = (entity.Id & 1023) * 0.006141f;
            var flick = (0.6f * MathF.Sin((time * 6.1f) + phase)) + (0.4f * MathF.Sin((time * 9.7f) + (phase * 1.7f)));
            //the carried lantern flickers calmer than tile flames, an extra LanternFlickerScale factor on top of the global
            var amp = 0.1f * DebugSettings.LightFlickerScale * DebugSettings.LanternFlickerScale;
            //scale by the fade-in AND the darkness level. On a CYCLE map this ramps the glow on as night falls, on a DARK
            //map the level sits at ~1 so the fade-in alone brings it up gradually
            var darkMapDim = alwaysDark ? LanternGlowDarkMapMultiplier : 1f;
            var intensity = LanternGlowIntensity * (1f - amp + (amp * flick)) * glowFade * Math.Clamp(darknessLevel, 0f, 1f) * darkMapDim;
            var driftX = MathF.Sin((time * 7.0f) + phase) * 0.7f;
            var driftY = MathF.Cos((time * 8.5f) + phase) * 0.7f;
            var screenPos = camera.WorldToScreen(new Vector2(tileCenterX + entity.VisualOffset.X + driftX, tileCenterY + entity.VisualOffset.Y + driftY + LanternGlowYOffset));

            Add(
                new LightSource(
                    screenPos,
                    entity.TileX,
                    entity.TileY,
                    entity.Direction,
                    pixelMask,
                    tileOffsets,
                    LanternGlowTint,
                    intensity,
                    OffsetY: (int)LanternGlowYOffset));
        }

        //config-driven foreground-tile lights (lamps, torches, windows), the replacement for the baked HEA glows
        if (TileLights.Count == 0)
            return;

        //In full daylight nothing is lit and nothing is fading, so skip the scan. But keep scanning once darkness has
        //gone (darknessActive false) while lamps are still mid fade-OUT, so they fade rather than vanish at daybreak
        if (!darknessActive && !AnyLit)
            return;

        AnyLit = false;

        //scan well PAST the visible bounds so a lamp whose tile has just scrolled off-screen still lights the visible
        //edge (its pool reaches in) instead of its whole pool popping out the moment the tile leaves view
        var (minX, minY, maxX, maxY) = camera.GetVisibleTileBounds(mapFile.Width, mapFile.Height, TileLights.GatherMarginTiles);

        for (var ty = minY; ty <= maxY; ty++)
            for (var tx = minX; tx <= maxX; tx++)
            {
                var tile = mapFile.Tiles[tx, ty];
                EmitTileLight(tile.LeftForeground, tx, ty, mapFile.Height, camera, time, darknessLevel, dt);
                EmitTileLight(tile.RightForeground, tx, ty, mapFile.Height, camera, time, darknessLevel, dt);
            }
    }

    private void EmitTileLight(
        int foregroundTileId,
        int tx,
        int ty,
        int mapHeight,
        Camera camera,
        float time,
        float darknessLevel,
        float dt)
    {
        if (!TileLights.TryGet(foregroundTileId, out var def))
            return;

        //per-light turn-on, a position-seeded threshold staggers each lamp very slightly, and the lit amount eases over
        //time so a lamp always FADES on/off rather than snapping when the darkness crosses its threshold
        var hash = (((tx * 73856093) ^ (ty * 19349663)) & 1023) / 1023f;
        var onLevel = DebugSettings.LightOnLevel + (hash * DebugSettings.LightStaggerSpan);
        var target = darknessLevel >= onLevel ? 1f : 0f;
        var key = (tx, ty, foregroundTileId);

        if (!LitFraction.TryGetValue(key, out var lit))
            lit = target; //first sight snaps, so scrolling an already-lit lamp into view doesn't pop a fade-in
        else
        {
            var maxDelta = DebugSettings.LightFadeSeconds <= 0f ? 1f : dt / DebugSettings.LightFadeSeconds;
            lit = MoveToward(lit, target, maxDelta);
        }

        LitFraction[key] = lit;

        if (lit > 0.001f)
            AnyLit = true;

        if (lit <= 0.001f)
            return; //fully off, don't add a dead light

        var mask = TileLights.GetMask(in def);

        var tileWorld = Camera.TileToWorld(tx, ty, mapHeight);
        var cx = tileWorld.X + DaLibConstants.HALF_TILE_WIDTH + def.OffsetX;
        var cy = tileWorld.Y + DaLibConstants.HALF_TILE_HEIGHT + def.OffsetY;

        var intensity = def.Brightness;

        if (def.Kind == LightKind.Flame)
        {
            //SUBTLE organic flicker, phase-seeded per tile so neighbours don't pulse together. Kept small + smooth on
            //purpose, a big amplitude (and the coarse rebuild quantization) read as a weird strobing pulse, not a flame.
            //flicker= scales the amplitude + jitter, speed= the tempo (both config extras, 1 = the stock feel)
            var phase = (((tx * 73856093) ^ (ty * 19349663)) & 1023) * 0.006141f;
            var spd = def.FlickerSpeed;
            var amp = 0.1f * def.FlickerAmount * DebugSettings.LightFlickerScale;
            var flick = (0.6f * MathF.Sin((time * 6.1f * spd) + phase)) + (0.4f * MathF.Sin((time * 9.7f * spd) + (phase * 1.7f)));
            intensity = def.Brightness * (1f - amp + (amp * flick));
            cx += MathF.Sin((time * 7.0f * spd) + phase) * 0.7f * def.FlickerAmount;
            cy += MathF.Cos((time * 8.5f * spd) + phase) * 0.7f * def.FlickerAmount;
        }

        intensity *= lit; //fade the lamp in and out with its eased lit amount

        var screenPos = camera.WorldToScreen(new Vector2(cx, cy));

        Add(
            new LightSource(
                screenPos,
                tx,
                ty,
                Direction.Down,
                mask,
                [],
                def.Color,
                intensity,
                def.Shape,
                def.Ignore,
                def.Soft,
                def.AngleDeg,
                def.SourceFrac,
                def.PoolFrac,
                def.OffsetY));
    }

    private void Add(LightSource source)
    {
        if (Count >= Buffer.Length)
            Array.Resize(ref Buffer, Buffer.Length * 2);

        Buffer[Count++] = source;
    }

    /// <summary>
    ///     Returns the tile-space offset array for a given lantern size and direction. Lanterns are
    ///     circular so direction is currently ignored, but the parameter is wired through for future
    ///     direction-aware shapes (e.g., cones).
    /// </summary>
    public (int Dx, int Dy)[] GetTileOffsets(LanternSize size, Direction direction)
        => size switch
        {
            LanternSize.Small => Euclidean3,
            LanternSize.Large => Euclidean5,
            _                 => []
        };

    //linear step of current toward target, capped at maxDelta (per-frame fade rate). Snaps when within one step.
    private static float MoveToward(float current, float target, float maxDelta)
    {
        var d = target - current;

        return MathF.Abs(d) <= maxDelta ? target : current + (MathF.Sign(d) * maxDelta);
    }

    //half-step bulge: a tile counts if its center is within radius + 0.5.
    //all-integer rearrangement of √(dx² + dy²) < radius + 0.5 → 4*(dx² + dy²) < (2*radius + 1)².
    //the threshold happens to equal the bounding-box area, so one value serves both the
    //stackalloc size and the inclusion test.
    private static (int Dx, int Dy)[] ComputeEuclidean(int radius)
    {
        var diameterSquared = ((2 * radius) + 1) * ((2 * radius) + 1);
        Span<(int Dx, int Dy)> buffer = stackalloc (int, int)[diameterSquared];
        var count = 0;

        for (var dy = -radius; dy <= radius; dy++)
            for (var dx = -radius; dx <= radius; dx++)
                if ((4 * ((dx * dx) + (dy * dy))) < diameterSquared)
                    buffer[count++] = (dx, dy);

        return buffer[..count].ToArray();
    }
}