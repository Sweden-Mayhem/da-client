// Death greyscale, desaturates the world blit while the player is dead.
// Saturation 1 = untouched colors, 0 = full greyscale (Rec.601 luma weights).
#if OPENGL
#define PS_SHADERMODEL ps_3_0
#else
#define PS_SHADERMODEL ps_4_0_level_9_1
#endif

Texture2D SpriteTexture;
sampler2D SpriteTextureSampler = sampler_state
{
    Texture = <SpriteTexture>;
};

float Saturation;

struct VertexShaderOutput
{
    float4 Position : SV_POSITION;
    float4 Color : COLOR0;
    float2 TextureCoordinates : TEXCOORD0;
};

float4 MainPS(VertexShaderOutput input) : COLOR
{
    float4 c = tex2D(SpriteTextureSampler, input.TextureCoordinates) * input.Color;

    float grey = dot(c.rgb, float3(0.299, 0.587, 0.114));
    c.rgb = lerp(float3(grey, grey, grey), c.rgb, Saturation);

    return c;
}

technique SpriteDrawing
{
    pass P0
    {
        PixelShader = compile PS_SHADERMODEL MainPS();
    }
};
