#if OPENGL
#define PS_SHADERMODEL ps_3_0
#else
#define PS_SHADERMODEL ps_4_0_level_9_1
#endif

float Time;
static const float PI = 3.1415926;

Texture2D SpriteTexture;
sampler2D SpriteTextureSampler = sampler_state
{
    Texture = <SpriteTexture>;
};
Texture2D WaterMaskTexture;
sampler2D WaterMaskTextureSampler = sampler_state
{
    Texture = <WaterMaskTexture>;
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
    float waterMask = tex2D(WaterMaskTextureSampler, input.TextureCoordinates).r;
    float2 uv = input.TextureCoordinates;
    float2 distortedUv = uv + float2(waterMask * sin(Time * 2.0 + input.TextureCoordinates.y * PI * 2.0 * 16.0) * (1 / 640.0), 0.0);

    float waterMaskCheck = tex2D(WaterMaskTextureSampler, uv).r;
    if (waterMaskCheck < 0.8)
        distortedUv = uv;

    float4 c = tex2D(SpriteTextureSampler, distortedUv) * input.Color;

    return c;
}

technique SpriteDrawing
{
    pass P0
    {
        PixelShader = compile PS_SHADERMODEL MainPS();
    }
};
