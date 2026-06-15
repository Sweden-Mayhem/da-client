#region
using Chaos.Client.Data.Models;
using Chaos.Geometry.Abstractions.Definitions;
using Microsoft.Xna.Framework;
#endregion

namespace Chaos.Client.Rendering;

/// <summary>The glow shape: a symmetric round glow, or a downward cone/beam (the config "spotlight").</summary>
public enum LightShape
{
    Round,
    Spotlight
}

public readonly record struct LightSource(
    Vector2 ScreenPosition,
    int TileX,
    int TileY,
    Direction Direction,
    //provides the glow SIZE (Width/Height); the LightingRenderer draws a soft gradient scaled to it (the pixel data
    //itself is unused by the deferred renderer).
    LightMask PixelMask,
    (int Dx, int Dy)[] TileOffsets,
    //the light's colour. null = neutral white (lanterns); a value = a coloured config tile light.
    Color? Tint = null,
    //0..1+ scale on the light's strength, used for the flame flicker. 1 = full (lanterns, solid tile lights).
    float Intensity = 1f,
    //which soft gradient to draw.
    LightShape Shape = LightShape.Round,
    //foreground tile ids this light does NOT interact with (won't be blocked/shadowed by). A lamp lists its own post
    //here so it doesn't shadow its own pool. null/empty = the light uses the shared base occluder map. See TileLights.
    int[]? Ignore = null,
    //gradient shape parameters (config-driven via tilelights.ini; the renderer caches one gradient texture per unique
    //combination). Soft = edge softness 0..1; the rest are spotlight-only: cone apex angle, beam width at the source
    //(fraction of the final width), and the bottom fraction that rounds into the ground pool.
    float Soft = 0.7f,
    float AngleDeg = 80f,
    float SourceFrac = 0.16f,
    float PoolFrac = 0.38f,
    //the light's vertical pixel offset from its tile (negative = raised above the ground). The higher a light sits, the
    //more tiles DOWN (in front, greater iso depth) it illuminates before its glow is erased - see WorldScreen's occluder
    ///silhouette passes. 0 = sits on the ground, lights only its own depth.
    int OffsetY = 0);
