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

float Saturation;

struct VertexShaderOutput
{
    float4 Position : SV_POSITION;
    float4 Color : COLOR0;
    float2 TextureCoordinates : TEXCOORD0;
};

float4 MainPS(VertexShaderOutput input) : COLOR
{
    float2 uv = input.TextureCoordinates;
    float4 source = tex2D(SpriteTextureSampler, uv);
    float waterMask = 1.0 - source.a;
    uv.x += waterMask * sin(Time * 2.0 + uv.y * PI * 2.0 * 16.0) * (1 / 640.0);

    float4 sample = tex2D(SpriteTextureSampler, uv);
    if (sample.a > 0.5)
        sample = source;

    sample.a = 1.0;

    return sample;
}

technique SpriteDrawing
{
    pass P0
    {
        PixelShader = compile PS_SHADERMODEL MainPS();
    }
};
