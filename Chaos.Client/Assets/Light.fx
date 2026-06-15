// Deferred light with 2D shadow occlusion.
// Draws a light gradient (the SpriteBatch texture) and ray-marches from each fragment toward the light through an
// occluder map (foreground coverage, in its alpha). Where the ray hits an occluder before reaching the light, the
// light is blocked. Output is additively accumulated by the caller (AdditiveLight: dest += src.rgb * src.a), so the
// shadow only needs to scale src.a.
#if OPENGL
#define PS_SHADERMODEL ps_3_0
#else
#define PS_SHADERMODEL ps_4_0_level_9_1
#endif

Texture2D SpriteTexture;
sampler2D SpriteTextureSampler = sampler_state { Texture = <SpriteTexture>; };

Texture2D Occluder;
sampler2D OccluderSampler = sampler_state { Texture = <Occluder>; };

float2 BufferSize;   // OCCLUDER map size in pixels (the viewport plus padding on every side)
float2 OccluderPad;  // padding offset: viewport pixel + pad = occluder map pixel (off-screen occluders live in the pad)
float2 LightPos;     // light centre, viewport pixels
float4 DestRect;     // the gradient quad: x, y, w, h in viewport pixels (to recover each fragment's screen position)

struct VSOut
{
    float4 Position : SV_POSITION;
    float4 Color : COLOR0;
    float2 Tex : TEXCOORD0;
};

float4 MainPS(VSOut input) : COLOR
{
    float4 c = tex2D(SpriteTextureSampler, input.Tex) * input.Color;

    if (c.a <= 0.003)
        return float4(0, 0, 0, 0);

    // positions in viewport pixels; occluder UVs add the pad so rays can march through OFF-SCREEN occluders too
    float2 fragPix = DestRect.xy + (input.Tex * DestRect.zw);
    float2 fragUV = (fragPix + OccluderPad) / BufferSize;
    float2 lightUV = (LightPos + OccluderPad) / BufferSize;
    float2 delta = lightUV - fragUV;
    float distPx = length(LightPos - fragPix);

    // SOFT area-light shadow. A single ray gives a knife-edge shadow (the "sharp line"); instead treat the light as a
    // small disc and average several rays fanned across it. The penumbra then widens with distance, so a thin occluder
    // (lamp post, tree trunk, fence) casts a soft, subtle shadow rather than a hard line.
    const float LIGHT_RADIUS_PX = 8.0;   // penumbra width. Kept NARROW: a wide soft band spread across only RAYS samples
                                         // quantises into visible steps (the few shadow levels terrace). Narrow = tight.
    const float NEAR_PX = 6.0;           // no shadow within this of the light (don't darken the bright core/own base)
    const float MAX_SHADOW = 0.93;       // deepest a shadow gets (near-black but not pure, so shadows are clearly visible)
    const int RAYS = 4;
    const int STEPS = 48;                // dense march = smooth, unbroken shadows (no banding/stepping). No jitter (it
                                         // crawled as the camera moved). The cost is fine for a handful of night lamps.

    // spread the sample points perpendicular to the fragment->light direction, across the light disc (fixed width)
    float2 perp = normalize(float2(-delta.y, delta.x) + 1e-6);
    float2 spreadUV = (perp * LIGHT_RADIUS_PX) / BufferSize;

    float shadow = 0.0;
    [unroll]
    for (int r = 0; r < RAYS; r++)
    {
        float off = (((r + 0.5) / RAYS) * 2.0) - 1.0; // -1..1 across the disc
        float2 d = (lightUV + (spreadUV * off)) - fragUV;

        float s = 0.0;
        [unroll]
        for (int i = 0; i < STEPS; i++)
        {
            float t = (i + 1.0) / (STEPS + 1.0);
            s = max(s, tex2D(OccluderSampler, fragUV + (d * t)).a);
        }

        shadow += s;
    }

    shadow /= RAYS;
    shadow *= saturate((distPx - NEAR_PX) / NEAR_PX); // fade shadow in away from the light
    shadow = min(shadow, MAX_SHADOW);

    c.a *= 1.0 - shadow;
    return c;
}

technique SpriteDrawing
{
    pass P0
    {
        PixelShader = compile PS_SHADERMODEL MainPS();
    }
};
