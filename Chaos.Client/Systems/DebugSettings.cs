#region
using Microsoft.Xna.Framework;
#endregion

namespace Chaos.Client.Systems;

/// <summary>
///     Live, in-memory render-FX tuning knobs driven by the in-game "/debugOptions" window so values can be dialled
///     in by eye. NOT persisted - the defaults here are the real shipped values; the window just lets us find good
///     numbers to hard-code. A few related knobs that live in the rendering layer are settable on their own classes
///     instead (<see cref="Chaos.Client.Rendering.DarknessRenderer.NightGlowAlpha" />,
///     <see cref="Chaos.Client.Rendering.DarknessRenderer.FadeSeconds" />,
///     <see cref="Chaos.Client.Rendering.SilhouetteRenderer.SilhouetteAlpha" />).
/// </summary>
public static class DebugSettings
{
    // Bloom (night glow)
    public static float BloomDuskMax = 0.20f;        //additive strength as darkness first sets in (dusk/dawn)
    public static float BloomNightMax = 0.40f;       //additive strength at full night (cubic-weighted toward night)
    public static Color BloomTint = new(255, 190, 112); //warm amber (a touch deeper/more golden)

    // Night vignette
    public static float VignetteMax = 0.72f;         //edge darkness at full night
    public static float VignetteInner = 0.30f;       //fraction of the radius that stays clear before the dark ramps in

    // Camera FX
    public static float DamageFlashMax = 0.5f;       //peak red flash alpha on an HP loss
    public static float LowHpPulseMax = 0.22f;       //peak red pulse alpha while at low HP

    // Light shadows (foreground below a lamp blocks its light, ray-marched)
    public static bool LightShadows = false;         //below-lamp foreground feet cast real shadows into the pool
    public static int LightShadowFootPx = 16;        //in-tile foot height used as the shadow caster (bottom pixels)

    // Light vertical reach - how far down a lamp's light extends (greater-depth foreground, proportional to lamp height)
    //reach = clamp(round(-OffsetY / PxPerStep), 0, MaxSteps), so ground lights only reach their own depth
    //the cap stops a high lamp from lighting tall sprites several tiles in front of it
    public static float LightDownReachPxPerStep = 19f;
    public static int LightDownReachMaxSteps = 2;    //hard cap on how many iso-depth steps DOWN any light reaches

    // Flame flicker strength multiplier (scales the brightness wobble of flame-kind lights + carried lanterns)
    public static float LightFlickerScale = 1.7f;    //1 = the old subtle 0.1 amplitude; higher = a more visible flicker
    public static float LanternFlickerScale = 0.5f;  //extra factor ON TOP of LightFlickerScale, carried lanterns only (calmer than tile flames)

    // Town map (parchment inked map) - bump MapInkRevision after changing any of these to force a rebuild
    public static int MapInkRevision;                //incremented by the debug window so the cached inked map regenerates
    public static float MapFloorTint = 0.10f;        //how much of the blurred floor colour tints the parchment
    public static float MapForegroundTint = 0.40f;   //how much of each structure texel's real colour bleeds into its ink
    public static float MapCollisionStrength = 0.50f; //master strength of the collision layer (wall shade + outline); 0 = off
    public static float MapWallInk = 0.75f;          //ink strength of the collision wall/floor outline (scaled by strength)
    public static float MapSketchContrast = 1.0f;    //pencil-sketch contrast on the foreground structure
    public static float MapEdgeSquiggle = 2.5f;      //multiplier on the hand-drawn wobble of collision + floor edges
    public static int MapFloorFadePasses = 20;       //blur passes on the floor-edge mask (higher = softer/wider fade-out)

    // UI glow (soft halo behind the menu button and tab minimap)
    public static int GlowLayers = 4;               //number of concentric glow rings (0 = off)
    public static float GlowStrength = 1.0f;        //overall alpha multiplier on the glow
    public static float LightFadeSeconds = 0.2f;     //how long a lamp takes to fade fully on or off
    public static float LightOnLevel = 0.12f;        //darkness (DuskGlow 0..1) at which lamps begin coming on
    public static float LightStaggerSpan = 0.40f;    //per-light spread of the turn-on level (position-seeded)
}
