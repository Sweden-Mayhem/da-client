#region
using System.Runtime.InteropServices;
using Chaos.Client.Data;
using Chaos.Client.Data.Models;
using Chaos.DarkAges.Definitions;
using DALib.Drawing;
using DALib.Extensions;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
#endregion

namespace Chaos.Client.Rendering;

/// <summary>
///     Manages the darkness overlay system: light metadata parsing, HEA file loading, per-pixel texture generation, and
///     overlay drawing. Supports both flat color overlays and per-pixel HEA light maps.
/// </summary>
public sealed class DarknessRenderer : IDisposable
{
    private readonly GraphicsDevice Device;

    //how long a light-level change takes to ease in (seconds). Map changes snap instead (see SnapFadeNext).
    //Time-based: AdvanceFade gets real elapsed seconds, so the fade always takes this long regardless of FPS.
    //NOTE: the fade is a fixed-DURATION transition from the level it started at to the target (eased per column by
    //the sweep below) - NOT a fixed-rate crawl across the full 0..1 range. The old rate-based MoveToward finished a
    //small one-level change in a fraction of FadeSeconds (a 0.1 alpha step completed in ~1s), which is why "10s"
    //fades looked like ~3s. Every level change now takes exactly FadeSeconds. Settable for the "/debugOptions" window.
    public static float FadeSeconds = 10f;

    //fraction of the fade spent rolling across the screen: each column transitions on its own slightly-offset clock,
    //so the new light level sweeps in from one side (dawn light from the left, nightfall from the right) instead of
    //the whole screen changing at once. 0 would make every column change together.
    private const float SWEEP_BAND = 0.65f;

    private float Alpha;

    //the alpha/colour the darkness is easing TOWARD; Alpha/DarknessColor are the current (interpolated) values.
    private float TargetAlpha;
    private Color TargetColor;

    //fixed-duration transition state: the level the running fade started from, its 0..1 progress (1 = settled),
    //and whether it is getting darker (sets which side the sweep enters from)
    private float FadeFromAlpha;
    private Vector3 FadeFromColor;
    private float FadeT = 1f;
    private bool FadeDarkening;

    //set on a map change so the first light-level after it snaps (no fade) - we only ease in-map level changes.
    private bool SnapFadeNext;

    //the baked HEA light maps are retired (see OnMapChanged). Flip to true only to A/B against the old bake.
    private const bool UseBakedHea = false;

    private int CacheBaseY;

    //one decoded row-cache per HEA light-map layer the viewport currently overlaps, keyed by layer index. The HEA
    //splits its scanline into layers; a 640-wide view only ever spanned two, but an expanded (wide-window) view can
    //span three or more, so we cache every layer in [CachedLeftLayer, CachedRightLayer] rather than a fixed pair.
    private readonly Dictionary<int, byte[,]> LayerCaches = new();
    private int CachedLeftLayer = -1;
    private int CachedRightLayer = -1;
    private bool CacheValid;
    private short CurrentMapId;
    private string CurrentLightType = "default";
    //the darkness tint, interpolated in FLOAT (0..255 per channel) so a long fade never stalls on byte truncation;
    //it quantizes to bytes only when a pixel is stamped.
    private Vector3 FadeColor;
    private Color DarknessColor => new((byte)FadeColor.X, (byte)FadeColor.Y, (byte)FadeColor.Z);

    //maps a 0..32 HEA light value to the 0..255 overlay scale, indexed by the raw byte so any value is in range. This
    //lets the fading AMBIENT use the full 0..255 range (smooth) instead of the light map's coarse 33 steps (choppy).
    private static readonly byte[] LightToByte = BuildLightToByte();

    private static byte[] BuildLightToByte()
    {
        var table = new byte[256];

        for (var i = 0; i < table.Length; i++)
            table[i] = (byte)(Math.Min(i, 32) * 255 / 32);

        return table;
    }
    //per-column shade scratch (ambient floor in 255-space + tint), refilled on each texture rebuild; uniform when
    //settled, swept during a transition. Reused to avoid per-frame allocation.
    private byte[]? ColAmbient;
    private Color[]? ColColor;

    //small gradient of the swept ambient that drives the deferred-lighting base during a transition, so the day/night
    //change ROLLS in diagonally from a top corner instead of the whole screen changing together. Null when settled
    //(LightingRenderer falls back to the uniform AmbientMultiplier then). Stretched bilinear over the viewport.
    private Texture2D? SweepTexture;
    private Color[]? SweepPixels;
    private const int SWEEP_TEX = 64;

    private HeaFile? HeaFile;
    private bool IsDarkMap;
    private LightLevel? LastLightLevel;
    private int LastLightSourceHash;
    private int LastOffsetX = int.MinValue;
    private int LastOffsetY = int.MinValue;
    private LightMetadata? LightData;
    private Color[]? Pixels;
    private Texture2D? Texture;

    /// <summary>
    ///     Whether a per-pixel HEA light map is loaded for the current map.
    /// </summary>
    public bool HasHeaFile => HeaFile is not null;

    /// <summary>
    ///     Whether darkness is currently active (alpha > 0).
    /// </summary>
    public bool IsActive => Alpha > 0f;

    /// <summary>
    ///     The colour the world is MULTIPLIED by in shadow for the deferred lighting (<see cref="LightingRenderer" />):
    ///     white in daylight, the night darkness colour at full dark. Lights add brightness back on top of this, so
    ///     this owns the ambient day/night level while LightingRenderer owns the glows.
    /// </summary>
    public Color AmbientMultiplier
    {
        get
        {
            //a player carrying a lantern lifts the whole night ambient (less dark) - a bit with a small lantern, more
            //with a large one. This is separate from the lantern's local glow pool and from the day/night gating (Alpha /
            //DuskGlow are untouched so lamps still turn on); it only lightens the visual darkness. LanternRelief is eased
            //smoothly in AdvanceFade so it fades in/out as the lantern is raised/lowered or the light level changes.
            var effAlpha = Alpha * (1f - LanternRelief);
            var v = ComputeAmbientFloat(effAlpha, FadeColor);

            return new Color((byte)v.X, (byte)v.Y, (byte)v.Z);
        }
    }

    //the AMBIENT lift (darkness reduction) from a carried lantern, per lantern size AND map type. On a normal day/night
    //CYCLE map it is gentle (~10/20%) so the night still reads; on a DARK (no-cycle) map it is strong (60/90%) since the
    //lantern is the only light. Applied proportionally to the current darkness (so it scales across ALL stages of the
    //night fade, not just at set levels) and eased smoothly. Tunable via /debugOptions.
    public static float LanternReliefCycleSmall = 0.10f;
    public static float LanternReliefCycleLarge = 0.20f;
    public static float LanternReliefDarkSmall = 0.60f;
    public static float LanternReliefDarkLarge = 0.90f;
    public static float LanternReliefFadeSeconds = 0.6f;
    private float LanternRelief;
    private float LanternReliefTarget;
    private float LanternEffectReliefValue;
    private float LanternEffectReliefTarget;

    //set on map change: the next AdvanceFade snaps the lantern reliefs straight to target instead of easing, so the
    //lighting is fully set the instant a map appears (the ease is only for raising/lowering a lantern within a map).
    private bool SnapLanternReliefNext;

    /// <summary>True if the current map is a permanently dark dungeon. The server encodes this as the map template's
    ///     light type <c>"alwaysdark"</c> (caves/crypts; e.g. map 1 "Mileth Crypt 1") - NOT the Darkness map flag, which
    ///     these maps don't set. A day/night cycle map uses <c>"default"</c> (or another cycling type like bloodmoon).</summary>
    public bool IsAlwaysDark => CurrentLightType.Equals("alwaysdark", StringComparison.OrdinalIgnoreCase);

    /// <summary>True if the current map runs a day/night CYCLE: it has light metadata and is NOT the always-dark type, so
    ///     the server drives its level over time via OnLightLevel. False for an always-dark dungeon and a plain day map.</summary>
    public bool HasDayNightCycle => HasMetadataForCurrentMap && !IsAlwaysDark;

    /// <summary>The eased "global lantern effect" amount (0..1): fully removes the blue night tint AND the night vignette.
    ///     Driven only on DARK maps; on a cycle map it stays 0 (the lantern's light pool removes the tint locally instead).</summary>
    public float LanternEffectRelief => LanternEffectReliefValue;

    /// <summary>Sets the lantern targets: <paramref name="ambientTarget" /> lifts the night ambient (proportional to the
    ///     current darkness), and <paramref name="globalEffect" /> drives the blue tint + vignette fully away. Both eased.</summary>
    public void SetLanternRelief(float ambientTarget, bool globalEffect)
    {
        LanternReliefTarget = Math.Clamp(ambientTarget, 0f, 1f);
        LanternEffectReliefTarget = globalEffect ? 1f : 0f;
    }

    //the ambient multiply for one darkness alpha + tint colour, in FLOAT 0..255 per channel (white in day, the night
    //colour at full dark, biased darker at night so the lamp pools read as real lights). Float so the sweep gradient
    //can be dithered before it quantizes to 8 bits (a raw byte gradient bands across the screen).
    private static Vector3 ComputeAmbientFloat(float alpha, Vector3 color)
    {
        //BRIGHTNESS tracks alpha (day bright, night dark). COLOUR strength is boosted at the low dusk/dawn levels so
        //their tint (sunrise red, the purples) actually shows instead of washing out near white - but pinned to alpha
        //at and beyond NightGlowAlpha (the cycle's darkest level), so night and pure-black dark maps are UNCHANGED.
        var tint = Math.Clamp(MathF.Max(alpha, MathF.Pow(alpha, 0.4f) * MathF.Pow(NightGlowAlpha, 0.6f)), 0f, 1f);
        var darken = 1f - (0.45f * alpha);

        return new Vector3(
            (255f + ((color.X - 255f) * tint)) * darken,
            (255f + ((color.Y - 255f) * tint)) * darken,
            (255f + ((color.Z - 255f) * tint)) * darken);
    }


    //night-shadow grade strength (0..1, 0 = off) and the base cool floor that LightingRenderer lifts UNLIT areas toward
    //at night (world += tint*(1-light)) - shadows go muted + bluer while lit pools stay warm. Slate blue (blue > green >
    //red) so it desaturates AND cools. Live-tunable in the "/debugOptions" window; the defaults here are the shipped values.
    public static float NightShadowStrength = 0.3f;
    public static Color NightShadowTintColor = new(50, 66, 112);

    /// <summary>
    ///     The cool blue-grey tint blended into shadowed (unlit) areas for the deferred lighting. Scaled by how dark it
    ///     currently is (<see cref="Alpha" />) and the strength, so it ramps in at night and is zero in daylight.
    /// </summary>
    public Color NightShadowTint
    {
        get
        {
            //ramp the cool tint in only at the DARKEST night level, not dusk/dawn. Keyed on DuskGlow (Alpha normalised
            //so the default cycle's darkest = 1.0). The levels land at: Darkest_A -> 1.0 (deep night, tinted),
            //Darker_A (/setLightLevel 1, the dusk/dawn level) -> ~0.82, and lighter levels below that - so the start
            //sits above Darker_A and the tint is zero through dusk and dawn, easing to full only as it reaches Darkest_A.
            const float start = 0.87f;
            var night = SmoothStep01(Math.Clamp((DuskGlow - start) / (1f - start), 0f, 1f));
            //a carried lantern removes the cool blue tint entirely (eased), so the world under a lantern is neutral, not blue
            var s = Math.Clamp(night * NightShadowStrength * (1f - LanternEffectReliefValue), 0f, 1f);

            return new Color((byte)(NightShadowTintColor.R * s), (byte)(NightShadowTintColor.G * s), (byte)(NightShadowTintColor.B * s));
        }
    }

    /// <summary>
    ///     True only when the map is dark, no graduated light metadata reduced the alpha, and the resulting
    ///     darkness color is pure black. The tab map uses this signal to switch into limited visibility mode.
    /// </summary>
    public bool IsFullBlackDark => IsDarkMap && (Alpha >= 1f) && (DarknessColor == Color.Black);

    /// <summary>
    ///     True when loaded light metadata explicitly assigns a light type to the current map. Gates all darkness
    ///     application: light level packets and the dark-map fallback only take effect when this is true. Maps without
    ///     an explicit metadata entry render without any darkness overlay regardless of the server-sent Darkness flag.
    /// </summary>
    private bool HasMetadataForCurrentMap => LightData?.MapLightTypes.ContainsKey(CurrentMapId) is true;

    public DarknessRenderer(GraphicsDevice device) => Device = device;

    /// <inheritdoc />
    public void Dispose()
    {
        DisposeTexture();
        SweepTexture?.Dispose();
        SweepTexture = null;
        Pixels = null;
        LayerCaches.Clear();
        CachedLeftLayer = -1;
        CachedRightLayer = -1;
        CacheValid = false;
    }

    //fills the per-column ambient floor (255-space brightness) and tint for the current frame
    private void FillColumnShade(int vpWidth)
    {
        if ((ColAmbient is null) || (ColAmbient.Length < vpWidth))
            ColAmbient = new byte[vpWidth];

        if ((ColColor is null) || (ColColor.Length < vpWidth))
            ColColor = new Color[vpWidth];

        if (!Sweeping)
        {
            var ambient = (byte)Math.Clamp(((1f - Alpha) * 255f) + 0.5f, 0f, 255f);
            var color = DarknessColor;

            for (var x = 0; x < vpWidth; x++)
            {
                ColAmbient[x] = ambient;
                ColColor[x] = color;
            }

            return;
        }

        var inv = vpWidth > 1 ? 1f / (vpWidth - 1) : 0f;

        for (var x = 0; x < vpWidth; x++)
        {
            (var a, var c) = ColumnDarkness(x * inv);
            ColAmbient[x] = (byte)Math.Clamp(((1f - a) * 255f) + 0.5f, 0f, 255f);
            ColColor[x] = new Color((byte)c.X, (byte)c.Y, (byte)c.Z);
        }
    }

    private void ComputePixelsFromCache(
        int heaOffsetX,
        int vpWidth,
        int vpHeight)
    {
        var pixels = Pixels!;
        var colAmbient = ColAmbient!;
        var colColor = ColColor!;
        var scanlineWidth = HeaFile!.ScanlineWidth;

        //gather the contiguous run of cached layers [CachedLeftLayer, CachedRightLayer] into flat scratch arrays so the
        //hot loop indexes by a small advancing cursor instead of doing a dictionary lookup per pixel. heaX increases
        //monotonically across each row, and layers tile the scanline left-to-right, so the cursor only ever moves forward.
        var span = (CachedLeftLayer >= 0) && (CachedRightLayer >= CachedLeftLayer) ? (CachedRightLayer - CachedLeftLayer) + 1 : 0;
        var rangeStart = span > 0 ? new int[span] : [];
        var rangeEnd = span > 0 ? new int[span] : [];
        var rangeCache = span > 0 ? new byte[span][,] : [];

        for (var k = 0; k < span; k++)
        {
            var layer = CachedLeftLayer + k;
            rangeStart[k] = HeaFile.Thresholds[layer];
            rangeEnd[k] = rangeStart[k] + HeaFile.GetLayerWidth(layer);
            rangeCache[k] = LayerCaches[layer];
        }

        for (var vy = 0; vy < vpHeight; vy++)
        {
            var k = 0;

            for (var vx = 0; vx < vpWidth; vx++)
            {
                var heaX = heaOffsetX + vx;
                byte lightValue;

                if ((heaX < 0) || (heaX >= scanlineWidth) || (span == 0))
                    lightValue = 0;
                else
                {
                    //advance the cursor to the layer that covers heaX (forward-only within the row)
                    while ((k < span - 1) && (heaX >= rangeEnd[k]))
                        k++;

                    lightValue = (heaX >= rangeStart[k]) && (heaX < rangeEnd[k]) ? rangeCache[k][vy, heaX - rangeStart[k]] : (byte)0;
                }

                //combine the smooth ambient (0..255, per column so a sweep shades across the screen) with the baked
                //light map (0..32 -> 0..255), in 255-space so the fade reads as a continuous gradient
                var effective = Math.Max(colAmbient[vx], LightToByte[lightValue]);
                var alpha = (byte)(255 - effective);
                var tint = colColor[vx];

                pixels[vy * vpWidth + vx] = new Color(
                    tint.R,
                    tint.G,
                    tint.B,
                    alpha);
            }
        }
    }

    private void DecodeLayerRows(
        int layerIndex,
        int heaStartY,
        int startRow,
        int rowCount,
        byte[,] target)
    {
        var layerWidth = HeaFile!.GetLayerWidth(layerIndex);

        for (var row = startRow; row < (startRow + rowCount); row++)
        {
            var heaY = heaStartY + row;
            var rowSpan = MemoryMarshal.CreateSpan(ref target[row, 0], layerWidth);

            if ((heaY < 0) || (heaY >= HeaFile.ScanlineCount))
            {
                rowSpan.Clear();

                continue;
            }

            HeaFile.DecodeScanline(layerIndex, heaY, rowSpan);
        }
    }

    private (int LeftLayer, int RightLayer) DetermineViewportLayers(int heaOffsetX, int vpWidth)
    {
        var leftHeaX = Math.Max(0, heaOffsetX);
        var rightHeaX = Math.Min(HeaFile!.ScanlineWidth - 1, heaOffsetX + vpWidth - 1);

        if ((rightHeaX < 0) || (leftHeaX >= HeaFile.ScanlineWidth))
            return (-1, -1);

        var leftLayer = -1;
        var rightLayer = -1;

        for (var i = 0; i < HeaFile.LayerCount; i++)
        {
            var start = HeaFile.Thresholds[i];
            var end = start + HeaFile.GetLayerWidth(i);

            if ((leftHeaX >= start) && (leftHeaX < end))
                leftLayer = i;

            if ((rightHeaX >= start) && (rightHeaX < end))
                rightLayer = i;
        }

        return (leftLayer, rightLayer);
    }

    private void DisposeTexture()
    {
        Texture?.Dispose();
        Texture = null;
    }

    /// <summary>
    ///     Draws the darkness overlay. Uses per-pixel HEA texture if available, otherwise flat color.
    /// </summary>
    public void Draw(SpriteBatchEx spriteBatch, Rectangle viewport)
    {
        if (Alpha <= 0f)
            return;

        if (Texture is not null && !Texture.IsDisposed)
            spriteBatch.Draw(Texture, new Vector2(viewport.X, viewport.Y), Color.White);
        else
            RenderHelper.DrawRect(spriteBatch, viewport, DarknessColor * Alpha);
    }

    /// <summary>
    ///     Called when the server sends a LightLevel packet. The level is always cached so that a later metadata load
    ///     can refresh via <see cref="ReapplyLightLevel" />. The visual state is only updated when the current map has
    ///     an explicit entry in the light metadata; maps without metadata are left bright regardless of the packet.
    /// </summary>
    public void OnLightLevel(LightLevel lightLevel)
    {
        LastLightLevel = lightLevel;

        //skip visual updates until metadata for this map is available, once it loads ReapplyLightLevel re-runs this
        //path with the cached level and the current state takes effect
        if (!HasMetadataForCurrentMap)
            return;

        var enumHex = ((byte)lightLevel).ToString("X");
        var key = $"{CurrentLightType}_{enumHex}".ToLowerInvariant();

        float newAlpha;
        Color newColor;

        if (LightData!.LightProperties.TryGetValue(key, out var props) && (props.Alpha < 32))
        {
            newAlpha = (32 - props.Alpha) / 32f;
            newColor = new Color(props.R, props.G, props.B);
        } else if (IsDarkMap)
        {
            //dark map with no graduated entry for this level (pure black darkness)
            newAlpha = 1f;
            newColor = Color.Black;
        } else
        {
            newAlpha = 0f;
            newColor = Color.Transparent;
        }

        //a repeat of the level we're already at (or already fading toward) changes nothing
        var sameTarget = (Math.Abs(newAlpha - TargetAlpha) < 0.0005f) && (newColor == TargetColor);

        TargetAlpha = newAlpha;
        TargetColor = newColor;

        //a map change snaps to the new level; an in-map level change runs one fixed-duration eased sweep
        if (!SnapFadeNext)
        {
            if (sameTarget)
                return;

            FadeFromAlpha = Alpha;
            FadeFromColor = FadeColor;
            FadeDarkening = newAlpha > Alpha;
            FadeT = 0f;

            return;
        }

        SnapFadeNext = false;
        FadeT = 1f;
        Alpha = TargetAlpha;
        FadeColor = new Vector3(TargetColor.R, TargetColor.G, TargetColor.B);

        //invalidate dirty tracking so pixels are recomputed with updated alpha/color
        LastOffsetX = int.MinValue;
        LastOffsetY = int.MinValue;

        if (HeaFile is null || (Alpha <= 0f))
        {
            DisposeTexture();
            CacheValid = false;
            CachedLeftLayer = -1;
            CachedRightLayer = -1;
            LayerCaches.Clear();
        }
    }

    /// <summary>
    ///     Called on map change. Looks up the map's light type and loads the HEA file if one exists. The
    ///     <c>MapFlags.Darkness</c> flag always produces pure black darkness on entry; light metadata only refines
    ///     this via <see cref="OnLightLevel" /> when the map has an explicit metadata entry.
    /// </summary>
    public void OnMapChanged(short mapId, bool isDarkMap)
    {
        IsDarkMap = isDarkMap;
        CurrentMapId = mapId;
        CurrentLightType = LightData?.MapLightTypes.TryGetValue(mapId, out var lightType) is true ? lightType : "default";

        //level is per-map state; the new map starts without a cached level until the server resends one
        LastLightLevel = null;

        //dark maps start dark immediately, light metadata can refine via OnLightLevel. Snap (no cross-map fade),
        //and flag the first in-map light-level to snap too; only later level changes within the map ease.
        if (isDarkMap)
        {
            Alpha = TargetAlpha = 1f;
            TargetColor = Color.Black;
            FadeColor = new Vector3(TargetColor.R, TargetColor.G, TargetColor.B);
        } else
        {
            Alpha = TargetAlpha = 0f;
            TargetColor = Color.Transparent;
            FadeColor = new Vector3(TargetColor.R, TargetColor.G, TargetColor.B);
        }

        FadeT = 1f; //settle any sweep still running from the previous map
        SnapFadeNext = true;
        SnapLanternReliefNext = true; //the lantern relief snaps to its new-map target too (no fade-in on entry)

        LastOffsetX = int.MinValue;
        LastOffsetY = int.MinValue;
        DisposeTexture();

        CacheValid = false;
        CachedLeftLayer = -1;
        CachedRightLayer = -1;
        LayerCaches.Clear();

        //the baked seo*.hea light maps are retired in favour of config-driven dynamic tile lights (TileLights).
        //they couldn't follow an edited lamp and couldn't be coloured.
        //forcing HeaFile null routes every dark map through the flat light-carving path (RebuildFlatWithLights).
        //flip UseBakedHea to true only to compare against the old bake.
        HeaFile = UseBakedHea ? TryLoadHeaFile(mapId) : null;
    }

    /// <summary>
    ///     Reapplies the last received light level. Called after metadata reload so the refreshed light type and
    ///     properties take effect. No-op when no level has been received for the current map.
    /// </summary>
    public void ReapplyLightLevel()
    {
        if (LastLightLevel is { } level)
            OnLightLevel(level);
    }

    private void RebuildFlatWithLights(Rectangle viewport, ReadOnlySpan<LightSource> sources)
    {
        var vpWidth = viewport.Width;
        var vpHeight = viewport.Height;

        if ((vpWidth <= 0) || (vpHeight <= 0))
            return;

        //dirty check, skip rebuild if light sources haven't changed
        if (LastOffsetX != int.MinValue)
            return;

        var pixelCount = vpWidth * vpHeight;

        if (Texture is null || Texture.IsDisposed || (Texture.Width != vpWidth) || (Texture.Height != vpHeight))
        {
            Texture?.Dispose();
            Texture = new Texture2D(Device, vpWidth, vpHeight);
        }

        if (Pixels is null || (Pixels.Length < pixelCount))
            Pixels = new Color[pixelCount];

        //fill with flat darkness, per column (a sweep shades across the screen); build one row, replicate the rest
        FillColumnShade(vpWidth);

        for (var x = 0; x < vpWidth; x++)
        {
            var tint = ColColor![x];
            Pixels[x] = new Color(tint.R, tint.G, tint.B, (byte)(255 - ColAmbient![x]));
        }

        for (var y = 1; y < vpHeight; y++)
            Array.Copy(Pixels, 0, Pixels, y * vpWidth, vpWidth);

        //stamp light sources
        StampLightSources(sources, vpWidth, vpHeight);

        Texture.SetData(Pixels, 0, pixelCount);
        LastOffsetX = 0;
        LastOffsetY = 0;
    }

    private void RebuildTexture(Camera camera, Rectangle viewport, ReadOnlySpan<LightSource> sources)
    {
        if (HeaFile is null)
            return;

        int vpWidth,
            vpHeight;

        if (viewport != default)
        {
            vpWidth = viewport.Width;
            vpHeight = viewport.Height;
        } else
            return;

        var viewportTopLeft = camera.ScreenToWorld(Vector2.Zero);
        var worldOffsetX = (int)viewportTopLeft.X;
        var worldOffsetY = (int)viewportTopLeft.Y;

        //early return only if everything matches, viewport size changes (hud swap) must force a rebuild
        //even when the world offset happens to coincide with the previous frame's value
        if ((worldOffsetX == LastOffsetX)
            && (worldOffsetY == LastOffsetY)
            && Texture is not null
            && !Texture.IsDisposed
            && (Texture.Width == vpWidth)
            && (Texture.Height == vpHeight))
            return;

        var heaOffsetX = worldOffsetX + HeaFile.ScreenWidth;
        var heaOffsetY = worldOffsetY + HeaFile.ScreenHeight;

        if (Texture is null || Texture.IsDisposed || (Texture.Width != vpWidth) || (Texture.Height != vpHeight))
        {
            Texture?.Dispose();
            Texture = new Texture2D(Device, vpWidth, vpHeight);
            CacheValid = false;
        }

        var pixelCount = vpWidth * vpHeight;

        if (Pixels is null || (Pixels.Length < pixelCount))
            Pixels = new Color[pixelCount];

        //figure out which layers the viewport overlaps
        (var leftLayer, var rightLayer) = DetermineViewportLayers(heaOffsetX, vpWidth);

        //capture the previously-cached layer range before we overwrite it, needed for the layersmatch check
        var prevLeft = CachedLeftLayer;
        var prevRight = CachedRightLayer;

        if ((leftLayer < 0) || (rightLayer < 0))
        {
            //viewport is entirely outside the scanline, no layers to cache, ComputePixels falls back to baseline
            CachedLeftLayer = -1;
            CachedRightLayer = -1;
            CacheBaseY = heaOffsetY;
            CacheValid = false;
        } else
        {
            //allocate/reuse a row-cache for every layer the viewport overlaps (not just the two edges)
            for (var layer = leftLayer; layer <= rightLayer; layer++)
            {
                var layerWidth = HeaFile.GetLayerWidth(layer);

                if (!LayerCaches.TryGetValue(layer, out var lc) || (lc.GetLength(0) != vpHeight) || (lc.GetLength(1) != layerWidth))
                    LayerCaches[layer] = new byte[vpHeight, layerWidth];
            }

            //check if incremental update is possible, only when the exact same layer range is still in view
            var dy = heaOffsetY - CacheBaseY;
            var layersMatch = (prevLeft == leftLayer) && (prevRight == rightLayer);
            var canIncrement = CacheValid && layersMatch && (Math.Abs(dy) < vpHeight);

            if (canIncrement && (dy != 0))
            {
                //shift cached rows and decode only the newly-revealed rows, per layer
                for (var layer = leftLayer; layer <= rightLayer; layer++)
                    ShiftAndDecodeRows(
                        LayerCaches[layer],
                        layer,
                        heaOffsetY,
                        dy,
                        vpHeight);
            } else if (!canIncrement)
            {
                //full decode, layer range changed, large vertical shift, or first frame
                for (var layer = leftLayer; layer <= rightLayer; layer++)
                    DecodeLayerRows(
                        layer,
                        heaOffsetY,
                        0,
                        vpHeight,
                        LayerCaches[layer]);
            }

            //else: canincrement && dy == 0 → only x shifted within the same layers, caches are valid as-is

            CachedLeftLayer = leftLayer;
            CachedRightLayer = rightLayer;
            CacheBaseY = heaOffsetY;
            CacheValid = true;
        }

        //compute pixels from cache
        FillColumnShade(vpWidth);

        ComputePixelsFromCache(
            heaOffsetX,
            vpWidth,
            vpHeight);

        //stamp lantern/dynamic light sources via max-blend
        StampLightSources(sources, vpWidth, vpHeight);

        Texture.SetData(Pixels, 0, pixelCount);
        LastOffsetX = worldOffsetX;
        LastOffsetY = worldOffsetY;
    }

    /// <summary>
    ///     Reloads light metadata from disk. Call after metadata sync completes. Recomputes the current light type
    ///     from the fresh metadata so that stale "default" fallback from pre-sync OnMapChanged is corrected.
    /// </summary>
    public void ReloadMetadata()
    {
        LightData = DataContext.MetaFiles.GetLightMetadata();
        CurrentLightType = LightData?.MapLightTypes.TryGetValue(CurrentMapId, out var lightType) is true ? lightType : "default";
    }

    private void ShiftAndDecodeRows(
        byte[,] cache,
        int layerIndex,
        int newHeaOffsetY,
        int dy,
        int vpHeight)
    {
        var layerWidth = HeaFile!.GetLayerWidth(layerIndex);
        var absDy = Math.Abs(dy);

        if (dy > 0)
        {
            //camera moved down, shift rows up, decode new rows at bottom
            Array.Copy(
                cache,
                dy * layerWidth,
                cache,
                0,
                (vpHeight - absDy) * layerWidth);

            DecodeLayerRows(
                layerIndex,
                newHeaOffsetY,
                vpHeight - absDy,
                absDy,
                cache);
        } else
        {
            //camera moved up, shift rows down, decode new rows at top
            Array.Copy(
                cache,
                0,
                cache,
                absDy * layerWidth,
                (vpHeight - absDy) * layerWidth);

            DecodeLayerRows(
                layerIndex,
                newHeaOffsetY,
                0,
                absDy,
                cache);
        }
    }

    private void StampLightSources(ReadOnlySpan<LightSource> sources, int vpWidth, int vpHeight)
    {
        if (sources.Length == 0)
            return;

        var pixels = Pixels!;
        var darkR = DarknessColor.R;
        var darkG = DarknessColor.G;
        var darkB = DarknessColor.B;

        for (var i = 0; i < sources.Length; i++)
        {
            var source = sources[i];
            var mask = source.PixelMask;

            //per-source colour + flicker strength. Tint null = neutral lantern (reveal with the ambient darkness
            //colour, the 0-32 EPF mask); a value = a config tile light with a full 8-bit (smooth) mask + colour.
            var isTile = source.Tint.HasValue;
            var tint = source.Tint ?? DarknessColor;
            var tintR = tint.R;
            var tintG = tint.G;
            var tintB = tint.B;
            var intensity = source.Intensity;

            //mask rect centered on screen position
            var maskLeft = (int)source.ScreenPosition.X - mask.Width / 2;
            var maskTop = (int)source.ScreenPosition.Y - mask.Height / 2;

            //clip to viewport
            var startX = Math.Max(0, -maskLeft);
            var startY = Math.Max(0, -maskTop);
            var endX = Math.Min(mask.Width, vpWidth - maskLeft);
            var endY = Math.Min(mask.Height, vpHeight - maskTop);

            if ((startX >= endX) || (startY >= endY))
                continue;

            for (var my = startY; my < endY; my++)
            {
                var vpY = maskTop + my;
                var maskRowOffset = my * mask.Width;
                var pixelRowOffset = vpY * vpWidth;

                for (var mx = startX; mx < endX; mx++)
                {
                    var rawMask = mask.Pixels[maskRowOffset + mx];

                    if (rawMask == 0)
                        continue;

                    //reveal strength 0..255. Tile masks are full 8-bit (smooth gradient, no banding); lantern EPF
                    //masks are 0..32, scaled up so they look exactly as before. Intensity is the flame flicker.
                    var strength = isTile ? (int)(rawMask * intensity) : (int)(rawMask * intensity) * 255 / 32;

                    if (strength <= 0)
                        continue;

                    if (strength > 255)
                        strength = 255;

                    var newAlpha = (byte)(255 - strength);
                    var pixelIndex = pixelRowOffset + maskLeft + mx;

                    //a light only ever LIGHTENS (lowers the darkness alpha); the brightest light wins per pixel
                    if (newAlpha >= pixels[pixelIndex].A)
                        continue;

                    //tint the revealed pixel toward the light's colour, strongest at the bright core. A neutral light
                    //(tint == the darkness colour) reproduces the original monochrome reveal, so lanterns are unchanged.
                    var t = strength / 255f;

                    pixels[pixelIndex] = new Color(
                        (byte)(darkR + ((tintR - darkR) * t)),
                        (byte)(darkG + ((tintG - darkG) * t)),
                        (byte)(darkB + ((tintB - darkB) * t)),
                        newAlpha);
                }
            }
        }
    }

    private static HeaFile? TryLoadHeaFile(short mapId)
    {
        var heaName = $"{mapId:D6}";

        if (!DatArchives.Seo.TryGetValue(heaName.WithExtension(".hea"), out var entry))
            return null;

        try
        {
            return HeaFile.FromEntry(entry);
        }
        //a corrupt or missing light map just falls back to no lighting
        catch
        {
            return null;
        }
    }

    /// <summary>
    ///     Advances the running light-level transition. The fade is fixed-duration: every level change takes exactly
    ///     <see cref="FadeSeconds" /> from the level it started at to the target, eased per screen column by the
    ///     sweep. Call once per frame (before <see cref="Update" />) with the frame's elapsed seconds. Runs even
    ///     while inactive so a fade-IN from full brightness still animates.
    /// </summary>
    public void AdvanceFade(float deltaSeconds)
    {
        if (deltaSeconds <= 0f)
            return;

        //ease the lantern reliefs every frame (independent of the day/night crossfade below, which settles and stops) so
        //they fade in when a lantern is raised and out when it is lowered or the light brightens. On map entry they SNAP
        //to the new-map target instead (so the lighting is fully set the instant the map appears).
        if (SnapLanternReliefNext)
        {
            SnapLanternReliefNext = false;
            LanternRelief = LanternReliefTarget;
            LanternEffectReliefValue = LanternEffectReliefTarget;
        } else
        {
            var step = LanternReliefFadeSeconds <= 0f ? 1f : deltaSeconds / LanternReliefFadeSeconds;
            var d = LanternReliefTarget - LanternRelief;
            LanternRelief = MathF.Abs(d) <= step ? LanternReliefTarget : LanternRelief + (MathF.Sign(d) * step);
            var de = LanternEffectReliefTarget - LanternEffectReliefValue;
            LanternEffectReliefValue = MathF.Abs(de) <= step ? LanternEffectReliefTarget : LanternEffectReliefValue + (MathF.Sign(de) * step);
        }

        if (FadeT >= 1f)
            return;

        FadeT = Math.Min(1f, FadeT + (deltaSeconds / FadeSeconds));

        //the uniform state values (IsActive / IsFullBlackDark / DuskGlow / the flat-rect fallback) track the
        //screen-average of the sweep; the pixel paths use the per-column values from ColumnDarkness instead
        var eased = SmoothStep01(FadeT);
        Alpha = FadeFromAlpha + ((TargetAlpha - FadeFromAlpha) * eased);

        FadeColor = Vector3.Lerp(
            FadeFromColor,
            new Vector3(TargetColor.R, TargetColor.G, TargetColor.B),
            eased);

        if (FadeT >= 1f)
        {
            Alpha = TargetAlpha;
            FadeColor = new Vector3(TargetColor.R, TargetColor.G, TargetColor.B);
        }

        //recompute the overlay this frame with the interpolated alpha/colour
        LastOffsetX = int.MinValue;
        LastOffsetY = int.MinValue;
    }

    private static float SmoothStep01(float t) => t * t * (3f - (2f * t));

    //true while a light-level transition is running (the pixel paths then shade per column)
    private bool Sweeping => FadeT < 1f;

    /// <summary>
    ///     A small gradient texture of the swept ambient for the deferred-lighting base, or null when the fade is
    ///     settled (the uniform <see cref="AmbientMultiplier" /> is used then). During a day/night transition the new
    ///     level enters from a TOP corner, top-RIGHT when darkening (chasing the setting sun), top-LEFT when
    ///     brightening, and rolls diagonally to the opposite corner over <see cref="SWEEP_BAND" /> of the fade, so the
    ///     darkness and dusk/dawn tint fade IN from the side instead of the whole screen changing at once. Built each
    ///     frame while sweeping (64x64, stretched bilinear) so the moving band stays smooth.
    /// </summary>
    public Texture2D? GetSweepAmbientTexture()
    {
        if (FadeT >= 1f)
            return null;

        SweepPixels ??= new Color[SWEEP_TEX * SWEEP_TEX];
        SweepTexture ??= new Texture2D(Device, SWEEP_TEX, SWEEP_TEX);

        var cornerX = FadeDarkening ? 1f : 0f; //origin top corner: right when darkening, left when brightening
        var target = new Vector3(TargetColor.R, TargetColor.G, TargetColor.B);
        var inv = 1f / (SWEEP_TEX - 1);

        for (var gy = 0; gy < SWEEP_TEX; gy++)
            for (var gx = 0; gx < SWEEP_TEX; gx++)
            {
                var xN = gx * inv;
                var yN = gy * inv;

                //0 at the origin top corner, 1 at the opposite bottom corner (diagonal distance); the iso-distance
                //fronts are diagonals, so the wipe sweeps corner-to-corner
                var u = (MathF.Abs(xN - cornerX) + yN) * 0.5f;
                var local = SmoothStep01(Math.Clamp((FadeT - (u * SWEEP_BAND)) / (1f - SWEEP_BAND), 0f, 1f));

                var a = FadeFromAlpha + ((TargetAlpha - FadeFromAlpha) * local);
                var c = Vector3.Lerp(FadeFromColor, target, local);
                var v = ComputeAmbientFloat(a, c);

                SweepPixels[(gy * SWEEP_TEX) + gx] = new Color((byte)v.X, (byte)v.Y, (byte)v.Z, (byte)255);
            }

        SweepTexture.SetData(SweepPixels);

        return SweepTexture;
    }

    /// <summary>
    ///     The darkness alpha + tint for one screen column during a sweep. <paramref name="xNorm" /> is the column's
    ///     0..1 position across the viewport. Each column runs the same eased transition on a clock offset by its
    ///     distance from the entry side, so the new level rolls across the screen over SWEEP_BAND of the fade.
    /// </summary>
    private (float Alpha, Vector3 Color) ColumnDarkness(float xNorm)
    {
        //nightfall enters from the right (chasing the setting sun), dawn light enters from the left
        var u = FadeDarkening ? 1f - xNorm : xNorm;
        var local = Math.Clamp((FadeT - (u * SWEEP_BAND)) / (1f - SWEEP_BAND), 0f, 1f);
        local = SmoothStep01(local);

        return (FadeFromAlpha + ((TargetAlpha - FadeFromAlpha) * local),
                Vector3.Lerp(FadeFromColor, new Vector3(TargetColor.R, TargetColor.G, TargetColor.B), local));
    }

    //the darkness alpha treated as "full night" for the glow ramp. The default day/night cycle's darkest level is
    //Light metafile alpha 15 (server Lights.json) -> (32 - 15) / 32 = 0.53125; anything at least that dark
    //(including pure-black dark maps, Alpha 1) saturates the ramp.
    //settable so the in-game "/debugOptions" window can tune where the bloom/vignette reach full strength
    public static float NightGlowAlpha = 0.53125f;

    /// <summary>
    ///     0..1 strength of the night glow, for the world bloom pass: zero in bright day, rising with darkness and
    ///     saturating at full-night darkness, the darker the world, the harder its remaining lights bloom.
    /// </summary>
    public float DuskGlow => Math.Clamp(Alpha / NightGlowAlpha, 0f, 1f);

    /// <summary>
    ///     Rebuilds the darkness overlay texture for the current viewport. Handles both HEA-based per-pixel
    ///     light maps and flat overlays with dynamic light sources. Call each frame before <see cref="Draw" />.
    /// </summary>
    public void Update(Camera camera, Rectangle viewport, ReadOnlySpan<LightSource> sources)
    {
        if (Alpha <= 0f)
            return;

        var hash = ComputeSourceHash(sources);

        if (hash != LastLightSourceHash)
        {
            LastLightSourceHash = hash;
            LastOffsetX = int.MinValue;
            LastOffsetY = int.MinValue;
        }

        if (HeaFile is not null)
            RebuildTexture(camera, viewport, sources);
        else if ((sources.Length > 0) || Sweeping)
            RebuildFlatWithLights(viewport, sources); //a sweep needs the textured path for its per-column shade
        else
        {
            //no sources, no HEA, fall back to flat-rect draw, release the texture
            DisposeTexture();
            Pixels = null;
        }
    }

    private static int ComputeSourceHash(ReadOnlySpan<LightSource> sources)
    {
        var hash = sources.Length;

        for (var i = 0; i < sources.Length; i++)
        {
            var src = sources[i];

            hash = HashCode.Combine(
                hash,
                (int)src.ScreenPosition.X,
                (int)src.ScreenPosition.Y,
                src.PixelMask.Width,
                src.Tint?.PackedValue ?? 0u,
                //quantize the flicker finely so a flame animates smoothly (coarse steps strobed). Steady lights
                //(Intensity 1) never change, so they still don't rebuild. Per-frame rebuild only while a flame is on screen.
                (int)(src.Intensity * 48f));
        }

        return hash;
    }
}